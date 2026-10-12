using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMMAtlas.AtlasEvents;
using MMMAtlas.Data;
using MMMAtlas.Steam;
using MMMAtlas.Voting;

namespace MMMAtlas.Tests;

[TestClass]
public sealed class SteamGameMetadataUpdaterTests
{
    private string _path = null!;
    private DatabaseConnection _database = null!;
    private ServiceProvider _services = null!;
    private IConfigurationRoot _configuration = null!;
    private ParentClient _parents = null!;
    private AppClient _apps = null!;
    private AtlasEventBroadcaster _events = null!;
    private SteamGameMetadataUpdater _updater = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _path = Path.Combine(Path.GetTempPath(), $"atlas-flags-{Guid.NewGuid():N}.db");
        _database = DatabaseConnection.Create($"Data Source={_path};Foreign Keys=True", Directory.GetCurrentDirectory());
        await DatabaseInitializer.InitializeAsync(_database, false);
        await ExecuteAsync("""
            INSERT INTO Users(UserId) VALUES(1),(2);
            INSERT INTO Games(GameId) VALUES(1),(2),(3),(4);
            INSERT INTO SteamGames(GameId, SteamAppId) VALUES(1,1),(2,2),(3,3),(4,4);
            INSERT INTO Votes(UserId,GameId,MicroScore,MesoScore,MacroScore)
                VALUES(1,1,8,6,2),(2,1,4,5,1),(1,2,8,6,2),(1,3,8,6,2);
            INSERT INTO GameVoteAggregates(GameId,VoteCount,MicroRatio,MesoRatio,MacroRatio)
                VALUES(1,2,.45,.4375,.1125),(2,1,.5,.375,.125),(3,1,.5,.375,.125);
            """);
        // The command line overrides the base setting using normal launch parameters.
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Steam:Metadata:BatchSize"] = "100" })
            .AddCommandLine(["--Steam:Metadata:BatchSize=2"])
            .Build();
        _services = new ServiceCollection()
            .Configure<SteamLibraryFilterOptions>(_configuration.GetSection("Steam:LibraryFilters"))
            .Configure<SteamGameMetadataOptions>(_configuration.GetSection("Steam:Metadata"))
            .BuildServiceProvider();
        _parents = new ParentClient();
        _apps = new AppClient();
        _events = new AtlasEventBroadcaster();
        _updater = new SteamGameMetadataUpdater(_database, _parents, _apps,
            new SteamLibraryFilter(_services.GetRequiredService<IOptionsMonitor<SteamLibraryFilterOptions>>()),
            _services.GetRequiredService<IOptionsMonitor<SteamGameMetadataOptions>>(), _events,
            NullLogger<SteamGameMetadataUpdater>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _services.Dispose();
        (_configuration as IDisposable)?.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
        File.Delete(_path + "-wal");
        File.Delete(_path + "-shm");
    }

    [TestMethod]
    public async Task BatchClassifiesAndDeletesAllExcludedVotesAndAggregatesBeforePublishing()
    {
        using var subscription = _events.Subscribe();
        Assert.AreEqual(2, await _updater.ProcessBatchAsync(CancellationToken.None));
        Assert.AreEqual(2L, await ScalarAsync("SELECT COUNT(*) FROM SteamGames WHERE hasParentApp IS NULL AND isBeta IS NULL;"));
        Assert.AreEqual(1L, await ScalarAsync("SELECT COUNT(*) FROM SteamGames WHERE GameId=1 AND hasParentApp=1 AND isBeta=0;"));
        Assert.AreEqual(1L, await ScalarAsync("SELECT COUNT(*) FROM SteamGames WHERE GameId=2 AND hasParentApp=0 AND isBeta=1;"));
        Assert.AreEqual(1L, await ScalarAsync("SELECT COUNT(*) FROM Votes;"));
        Assert.AreEqual(1L, await ScalarAsync("SELECT COUNT(*) FROM GameVoteAggregates WHERE GameId=3 AND VoteCount=1;"));
        Assert.IsTrue(subscription.Reader.TryRead(out var parentEvent));
        Assert.AreEqual(1L, parentEvent.GameId);
        Assert.IsTrue(subscription.Reader.TryRead(out var betaEvent));
        Assert.AreEqual(2L, betaEvent.GameId);

        Assert.AreEqual(2, await _updater.ProcessBatchAsync(CancellationToken.None));
        Assert.AreEqual(0, await _updater.ProcessBatchAsync(CancellationToken.None));
        CollectionAssert.AreEqual(new[] { 1, 2 }, _parents.Requests[0]);
        CollectionAssert.AreEqual(new[] { 3, 4 }, _parents.Requests[1]);
        Assert.HasCount(2, _parents.Requests);
        Assert.HasCount(2, _apps.Requests);
        Assert.AreEqual(2L, await ScalarAsync("SELECT COUNT(*) FROM SteamGames WHERE GameId IN (3,4) AND hasParentApp=0 AND isBeta=0;"));
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public async Task DisabledFiltersDoNotFetchOrSetTheirFlags(bool requireGameType, bool excludeBeta)
    {
        SetFilters(requireGameType, excludeBeta);
        Assert.AreEqual(requireGameType || excludeBeta ? 2 : 0,
            await _updater.ProcessBatchAsync(CancellationToken.None));
        Assert.HasCount(requireGameType ? 1 : 0, _parents.Requests);
        Assert.HasCount(excludeBeta ? 1 : 0, _apps.Requests);
        Assert.AreEqual(requireGameType ? 2L : 4L, await ScalarAsync("SELECT COUNT(*) FROM SteamGames WHERE hasParentApp IS NULL;"));
        Assert.AreEqual(excludeBeta ? 2L : 4L, await ScalarAsync("SELECT COUNT(*) FROM SteamGames WHERE isBeta IS NULL;"));
        Assert.AreEqual(1L, await ScalarAsync("SELECT COUNT(*) FROM GameVoteAggregates WHERE GameId=3;"));
        if (!requireGameType)
            Assert.AreEqual(2L, await ScalarAsync("SELECT COUNT(*) FROM Votes WHERE GameId=1;"));
        if (!excludeBeta)
            Assert.AreEqual(1L, await ScalarAsync("SELECT COUNT(*) FROM Votes WHERE GameId=2;"));

        // Enabling a filter later fills its still-null column.
        SetFilters(true, true);
        for (var batch = 0; batch < 3; batch++)
            await _updater.ProcessBatchAsync(CancellationToken.None);
        Assert.AreEqual(0L, await ScalarAsync("SELECT COUNT(*) FROM SteamGames WHERE hasParentApp IS NULL OR isBeta IS NULL;"));
        Assert.AreEqual(1L, await ScalarAsync("SELECT COUNT(*) FROM Votes;"));
    }

    [TestMethod]
    public async Task FailedAndPartialLookupsLeaveUnknownFlagsAndDoNotStarveLaterGames()
    {
        _parents.Fail = true;
        _apps.Omitted.Add(1);
        _apps.Blank.Add(2);
        Assert.AreEqual(0, await _updater.ProcessBatchAsync(CancellationToken.None));
        Assert.AreEqual(4L, await ScalarAsync("SELECT COUNT(*) FROM Votes;"));
        Assert.AreEqual(4L, await ScalarAsync("SELECT COUNT(*) FROM SteamGames WHERE hasParentApp IS NULL AND isBeta IS NULL;"));

        Assert.AreEqual(2, await _updater.ProcessBatchAsync(CancellationToken.None));
        Assert.AreEqual(2L, await ScalarAsync("SELECT COUNT(*) FROM SteamGames WHERE GameId IN (3,4) AND isBeta=0 AND hasParentApp IS NULL;"));
        _parents.Fail = false;
        _apps.Omitted.Clear();
        _apps.Blank.Clear();
        Assert.AreEqual(2, await _updater.ProcessBatchAsync(CancellationToken.None));
        Assert.AreEqual(2, await _updater.ProcessBatchAsync(CancellationToken.None));
        Assert.HasCount(3, _apps.Requests); // Known Beta values are not fetched again.
        Assert.AreEqual(1L, await ScalarAsync("SELECT COUNT(*) FROM Votes;"));
    }

    [TestMethod]
    public async Task CleanupFailureRollsBackFlagsVotesAndAggregatesAndPublishesNothing()
    {
        using var subscription = _events.Subscribe();
        await ExecuteAsync("""
            CREATE TRIGGER FailCleanup BEFORE DELETE ON GameVoteAggregates
            WHEN OLD.GameId=2 BEGIN SELECT RAISE(ABORT,'cleanup failure'); END;
            """);
        await Assert.ThrowsExactlyAsync<SqliteException>(() => _updater.ProcessBatchAsync(CancellationToken.None));
        Assert.AreEqual(4L, await ScalarAsync("SELECT COUNT(*) FROM SteamGames WHERE hasParentApp IS NULL AND isBeta IS NULL;"));
        Assert.AreEqual(4L, await ScalarAsync("SELECT COUNT(*) FROM Votes;"));
        Assert.AreEqual(3L, await ScalarAsync("SELECT COUNT(*) FROM GameVoteAggregates;"));
        Assert.IsFalse(subscription.Reader.TryRead(out _));
        await ExecuteAsync("DROP TRIGGER FailCleanup;");
        await _updater.ProcessBatchAsync(CancellationToken.None);
        await _updater.ProcessBatchAsync(CancellationToken.None);
        Assert.AreEqual(1L, await ScalarAsync("SELECT COUNT(*) FROM Votes;"));
    }

    [TestMethod]
    public async Task VotesDuringMetadataLookupAreCleanedUpAndLaterVotesAreRejected()
    {
        await ExecuteAsync("INSERT INTO Users(UserId) VALUES(3);");
        _parents.Pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = _updater.ProcessBatchAsync(CancellationToken.None);
        await _parents.Started.Task;
        var manager = new VotingManager(_database);
        try
        {
            Assert.IsNotNull(await manager.SubmitAsync(3, 1, new VoteScores(8, 6, 2), CancellationToken.None));
            Assert.AreEqual(3L, await ScalarAsync("SELECT COUNT(*) FROM Votes WHERE GameId=1;"));
        }
        finally
        {
            _parents.Pause.SetResult();
        }
        await batch;
        Assert.AreEqual(0L, await ScalarAsync("SELECT COUNT(*) FROM Votes WHERE GameId=1;"));
        Assert.IsNull(await manager.SubmitAsync(3, 1, new VoteScores(8, 6, 2), CancellationToken.None));
        Assert.AreEqual(0L, await ScalarAsync("SELECT COUNT(*) FROM GameVoteAggregates WHERE GameId=1;"));
    }

    private void SetFilters(bool parent, bool beta)
    {
        _configuration["Steam:LibraryFilters:RequireGameType"] = parent.ToString();
        _configuration["Steam:LibraryFilters:ExcludeBeta"] = beta.ToString();
        _configuration.Reload();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.Value);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.Value);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class ParentClient : ISteamStoreParentClient
    {
        public bool Fail { get; set; }
        public TaskCompletionSource? Pause { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<int[]> Requests { get; } = [];
        public async Task<IReadOnlyDictionary<int, bool>?> GetParentsAsync(IEnumerable<int> appIds, CancellationToken cancellationToken)
        {
            var ids = appIds.ToArray();
            Requests.Add(ids);
            Started.TrySetResult();
            if (Fail) throw new HttpRequestException("Steam unavailable");
            if (Pause is not null) await Pause.Task.WaitAsync(cancellationToken);
            return ids.ToDictionary(id => id, id => id == 1);
        }
    }

    private sealed class AppClient : ISteamAppInfoClient
    {
        public HashSet<int> Omitted { get; } = [];
        public HashSet<int> Blank { get; } = [];
        public List<int[]> Requests { get; } = [];
        public Task<IReadOnlyDictionary<int, SteamAppInfo>?> GetAppsAsync(IEnumerable<int> appIds, CancellationToken cancellationToken)
        {
            var ids = appIds.ToArray();
            Requests.Add(ids);
            return Task.FromResult<IReadOnlyDictionary<int, SteamAppInfo>?>(ids.Where(id => !Omitted.Contains(id))
                .ToDictionary(id => id, id => SteamAppInfo.FromCommonType(Blank.Contains(id) ? " " : id == 2 ? " bEtA " : "Game")));
        }
    }
}
