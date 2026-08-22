using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MMMAtlas.Data;
using MMMAtlas.Steam;

namespace MMMAtlas.Tests;

[TestClass]
public sealed class SteamAuthenticationManagerTests
{
    private string _databasePath = null!;
    private SteamApiMessageHandler _apiHandler = null!;
    private TestSteamStoreClient _storeClient = null!;

    [TestInitialize]
    public void Initialize()
    {
        _databasePath =
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");

        using var connection =
            new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();

        var schemaPath = Path.GetFullPath(
            "../../../../SQLiteTableSchemas.sql",
            AppContext.BaseDirectory);
        using var command = connection.CreateCommand();
        command.CommandText = File.ReadAllText(schemaPath);
        command.ExecuteNonQuery();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [TestMethod]
    public async Task SignInDoesNotCollectUntilExplicitOptInAndErasurePreservesVotes()
    {
        var manager = CreateManager();

        var firstSignIn = await manager.CompleteSignInAsync(
            76561198012345678,
            CancellationToken.None);

        var dashboard = await manager.GetDashboardAsync(
            firstSignIn.UserId,
            CancellationToken.None);

        Assert.IsNotNull(dashboard);
        Assert.IsFalse(dashboard.DataCollectionEnabled);
        Assert.IsNull(dashboard.PersonaName);
        Assert.IsEmpty(dashboard.MostPlayedGames);
        Assert.AreEqual(0, _apiHandler.RequestCount);
        Assert.AreEqual(0, _storeClient.RequestCount);

        await manager.SetDataCollectionEnabledAsync(
            firstSignIn.UserId,
            enabled: true,
            CancellationToken.None);

        var optedIn = await manager.GetDashboardAsync(
            firstSignIn.UserId,
            CancellationToken.None);

        Assert.IsNotNull(optedIn);
        Assert.IsTrue(optedIn.DataCollectionEnabled);
        Assert.AreEqual("Atlas Tester", optedIn.PersonaName);
        Assert.HasCount(2, optedIn.MostPlayedGames);
        Assert.AreEqual("Long Game", optedIn.MostPlayedGames[0].Name);
        Assert.AreEqual(
            9000,
            optedIn.MostPlayedGames[0].PlaytimeForeverMinutes);
        Assert.AreEqual(2, _apiHandler.RequestCount);
        Assert.AreEqual(2, _storeClient.RequestCount);
        Assert.AreEqual(
            ("Long Game description", 2011, false),
            ReadStoreMetadata(10));
        CollectionAssert.AreEqual(
            new[] { "Atlas Developer" },
            ReadStoreLabels("SteamGameDevelopers", "DeveloperName", 10));
        CollectionAssert.AreEqual(
            new[] { "Atlas Publisher" },
            ReadStoreLabels("SteamGamePublishers", "PublisherName", 10));
        CollectionAssert.AreEqual(
            new[] { "Strategy" },
            ReadStoreLabels("SteamGameGenres", "GenreName", 10));

        AddVote(firstSignIn.UserId);

        var votedDashboard = await manager.GetDashboardAsync(
            firstSignIn.UserId,
            CancellationToken.None);
        Assert.IsNotNull(votedDashboard);
        Assert.AreEqual(8d, votedDashboard.MostPlayedGames[0].MicroScore);
        Assert.AreEqual(6d, votedDashboard.MostPlayedGames[0].MesoScore);
        Assert.AreEqual(2d, votedDashboard.MostPlayedGames[0].MacroScore);
        Assert.IsFalse(votedDashboard.MostPlayedGames[0].IsPublic);

        await manager.SetDataCollectionEnabledAsync(
            firstSignIn.UserId,
            enabled: false,
            CancellationToken.None);

        var erased = await manager.GetDashboardAsync(
            firstSignIn.UserId,
            CancellationToken.None);
        Assert.IsNotNull(erased);
        Assert.IsFalse(erased.DataCollectionEnabled);
        Assert.IsNull(erased.PersonaName);
        Assert.IsEmpty(erased.MostPlayedGames);
        Assert.AreEqual(1L, CountRows("Votes"));
        Assert.AreEqual(1L, CountRows("GameVoteAggregates"));

        await manager.CompleteSignInAsync(
            76561198012345678,
            CancellationToken.None);

        var stillDisabled = await manager.GetDashboardAsync(
            firstSignIn.UserId,
            CancellationToken.None);
        Assert.IsNotNull(stillDisabled);
        Assert.IsFalse(stillDisabled.DataCollectionEnabled);
        Assert.IsNull(stillDisabled.PersonaName);
        Assert.AreEqual(2, _apiHandler.RequestCount);

        await manager.SetDataCollectionEnabledAsync(
            firstSignIn.UserId,
            enabled: true,
            CancellationToken.None);

        var optedBackIn = await manager.GetDashboardAsync(
            firstSignIn.UserId,
            CancellationToken.None);
        Assert.IsNotNull(optedBackIn);
        Assert.IsTrue(optedBackIn.DataCollectionEnabled);
        Assert.AreEqual("Atlas Tester", optedBackIn.PersonaName);
        Assert.HasCount(2, optedBackIn.MostPlayedGames);
        Assert.AreEqual(4, _apiHandler.RequestCount);
        Assert.AreEqual(2, _storeClient.RequestCount);

        using var connection =
            new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM Users;";
        Assert.AreEqual(1L, (long)countCommand.ExecuteScalar()!);
    }

    private void AddVote(long userId)
    {
        using var connection =
            new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Votes
                (UserId, GameId, MicroScore, MesoScore, MacroScore)
            SELECT $userId, GameId, 8, 6, 2
            FROM SteamGames
            WHERE SteamAppId = 10;

            INSERT INTO GameVoteAggregates
                (GameId, VoteCount, MicroRatio, MesoRatio, MacroRatio)
            SELECT GameId, 1, 0.5, 0.375, 0.125
            FROM SteamGames
            WHERE SteamAppId = 10;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.ExecuteNonQuery();
    }

    private long CountRows(string tableName)
    {
        using var connection =
            new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return (long)command.ExecuteScalar()!;
    }

    private (string? Description, int? ReleaseYear, bool? IsFree)
        ReadStoreMetadata(
        int appId)
    {
        using var connection =
            new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Description, ReleaseYear, IsFree
            FROM SteamGames
            WHERE SteamAppId = $appId;
            """;
        command.Parameters.AddWithValue("$appId", appId);
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetBoolean(2));
    }

    private string[] ReadStoreLabels(
        string tableName,
        string columnName,
        int appId)
    {
        using var connection =
            new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT metadata.{columnName}
            FROM {tableName} AS metadata
            INNER JOIN SteamGames AS steam
                ON steam.GameId = metadata.GameId
            WHERE steam.SteamAppId = $appId
            ORDER BY metadata.{columnName};
            """;
        command.Parameters.AddWithValue("$appId", appId);
        using var reader = command.ExecuteReader();
        var results = new List<string>();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }
        return results.ToArray();
    }

    private SteamAuthenticationManager CreateManager()
    {
        _apiHandler = new SteamApiMessageHandler();
        var httpClient = new HttpClient(_apiHandler)
        {
            BaseAddress = new Uri("https://api.steampowered.com/")
        };
        var apiClient = new SteamApiClient(
            httpClient,
            new SteamApiOptions { ApiKey = "test-key" },
            NullLogger<SteamApiClient>.Instance);
        var database = DatabaseConnection.Create(
            $"Data Source={_databasePath};Foreign Keys=True",
            Directory.GetCurrentDirectory());

        _storeClient = new TestSteamStoreClient();
        return new SteamAuthenticationManager(
            database,
            apiClient,
            _storeClient);
    }

    private sealed class TestSteamStoreClient : ISteamStoreClient
    {
        private int _requestCount;

        public int RequestCount => _requestCount;

        public Task<SteamStoreGame?> GetGameAsync(
            int appId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var name = appId == 10 ? "Long Game" : "Short Game";
            var year = appId == 10 ? 2011 : 2020;
            return Task.FromResult<SteamStoreGame?>(
                new SteamStoreGame(
                    $"{name} description",
                    year,
                    ["Atlas Developer"],
                    ["Atlas Publisher"],
                    ["Strategy"],
                    IsFree: false));
        }
    }

    private sealed class SteamApiMessageHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var json = request.RequestUri?.AbsolutePath.Contains(
                "GetPlayerSummaries",
                StringComparison.Ordinal) == true
                ? """
                    {
                      "response": {
                        "players": [{
                          "personaname": "Atlas Tester",
                          "profileurl": "https://steamcommunity.com/id/atlas/",
                          "avatarfull": "https://example.test/avatar.jpg"
                        }]
                      }
                    }
                    """
                : """
                    {
                      "response": {
                        "game_count": 2,
                        "games": [
                          {
                            "appid": 20,
                            "name": "Short Game",
                            "img_icon_url": "short",
                            "playtime_forever": 120
                          },
                          {
                            "appid": 10,
                            "name": "Long Game",
                            "img_icon_url": "long",
                            "playtime_forever": 9000,
                            "playtime_2weeks": 60,
                            "rtime_last_played": 1700000000
                          }
                        ]
                      }
                    }
                    """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    json,
                    System.Text.Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
