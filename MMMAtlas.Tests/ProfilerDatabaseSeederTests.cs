using Microsoft.Data.Sqlite;
using MMMAtlas.Data;

namespace MMMAtlas.Tests;

[TestClass]
public sealed class ProfilerDatabaseSeederTests
{
    [TestMethod]
    public void DedicatedConnectionCannotReuseAnApplicationDatabaseFile()
    {
        var root = Path.GetTempPath();
        var profiler = "Data Source=profiler.db";

        Assert.Throws<InvalidOperationException>(() =>
            ProfilerDatabaseSeeder.EnsureDedicatedConnection(
                profiler,
                "Data Source=profiler.db",
                "Data Source=production.db",
                root));

        ProfilerDatabaseSeeder.EnsureDedicatedConnection(
            profiler,
            "Data Source=development.db",
            "Data Source=production.db",
            root);
    }

    [TestMethod]
    public async Task ResetSeedsExactTargetsAndCanReturnToEmpty()
    {
        await using var fixture = await ProfilerDatabaseFixture.CreateAsync();

        var seeded = await ProfilerDatabaseSeeder.PrepareAsync(
            fixture.Database,
            reset: true,
            new ProfilerSeedTargets(40, 25, 120, 8675309));

        Assert.AreEqual(40, seeded.Games);
        Assert.AreEqual(40, seeded.RatedGames);
        Assert.AreEqual(25, seeded.Users);
        Assert.AreEqual(120, seeded.Votes);
        Assert.AreEqual(40L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM SteamGames;"));
        Assert.AreEqual(80L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM SteamGameGenres;"));
        Assert.AreEqual(40L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM GameAtlasIcons WHERE length(ImageContent) > 0 AND length(LibraryCardContent) > 0;"));
        Assert.AreEqual(0L, await fixture.ScalarAsync(
            """
            SELECT COUNT(*)
            FROM GameVoteAggregates AS aggregate
            JOIN
            (
                SELECT GameId, COUNT(*) AS VoteCount
                FROM Votes
                GROUP BY GameId
            ) AS actual ON actual.GameId = aggregate.GameId
            WHERE aggregate.VoteCount <> actual.VoteCount;
            """));

        var empty = await ProfilerDatabaseSeeder.PrepareAsync(
            fixture.Database,
            reset: true,
            new ProfilerSeedTargets(0, 0, 0, 8675309));

        Assert.AreEqual(
            new ProfilerDatabaseSnapshot(0, 0, 0, 0),
            empty);
    }

    [TestMethod]
    public async Task GrowPreservesRowsAndAddsToLargerTargets()
    {
        await using var fixture = await ProfilerDatabaseFixture.CreateAsync();
        await ProfilerDatabaseSeeder.PrepareAsync(
            fixture.Database,
            reset: true,
            new ProfilerSeedTargets(20, 15, 40, 42));
        var originalScore = await fixture.ScalarDoubleAsync(
            "SELECT MicroScore FROM Votes ORDER BY GameId, UserId LIMIT 1;");

        var grown = await ProfilerDatabaseSeeder.PrepareAsync(
            fixture.Database,
            reset: false,
            new ProfilerSeedTargets(35, 30, 100, 42));

        Assert.AreEqual(35, grown.Games);
        Assert.AreEqual(30, grown.Users);
        Assert.AreEqual(100, grown.Votes);
        Assert.AreEqual(originalScore, await fixture.ScalarDoubleAsync(
            "SELECT MicroScore FROM Votes ORDER BY GameId, UserId LIMIT 1;"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProfilerDatabaseSeeder.PrepareAsync(
                fixture.Database,
                reset: false,
                new ProfilerSeedTargets(10, 30, 100, 42)));
    }

    [TestMethod]
    public async Task ConcurrentGameCreationAddsMetadataImagesVotesAndStopsAtTarget()
    {
        await using var fixture = await ProfilerDatabaseFixture.CreateAsync();
        await ProfilerDatabaseSeeder.PrepareAsync(
            fixture.Database,
            reset: true,
            new ProfilerSeedTargets(10, 5, 10, 123));

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(index =>
            ProfilerDatabaseSeeder.CreateNextGameAsync(
                fixture.Database,
                targetGames: 20,
                userId: (index % 5) + 1,
                randomSeed: 123)));

        Assert.AreEqual(10, results.Count(result => result.Created));
        Assert.AreEqual(10, results.Select(result => result.GameId).Distinct().Count());
        var snapshot = await ProfilerDatabaseSeeder.ReadSnapshotAsync(
            fixture.Database);
        Assert.AreEqual(new ProfilerDatabaseSnapshot(20, 20, 5, 20), snapshot);
        Assert.AreEqual(20L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM DevelopmentGameInfo WHERE length(ImageContent) > 0;"));
        Assert.AreEqual(20L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM GameAtlasIcons WHERE length(ImageContent) > 0 AND length(LibraryCardContent) > 0;"));

        var atTarget = await ProfilerDatabaseSeeder.CreateNextGameAsync(
            fixture.Database,
            targetGames: 20,
            userId: 1,
            randomSeed: 123);
        Assert.IsFalse(atTarget.Created);
        Assert.IsTrue(atTarget.TargetReached);
        Assert.AreEqual(20, atTarget.GameCount);
    }

    private sealed class ProfilerDatabaseFixture : IAsyncDisposable
    {
        private readonly string _path;

        private ProfilerDatabaseFixture(string path, DatabaseConnection database)
        {
            _path = path;
            Database = database;
        }

        public DatabaseConnection Database { get; }

        public static async Task<ProfilerDatabaseFixture> CreateAsync()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"mmm-atlas-profiler-test-{Guid.NewGuid():N}.db");
            var database = DatabaseConnection.Create(
                $"Data Source={path};Foreign Keys=True",
                Directory.GetCurrentDirectory());
            await DevelopmentDatabase.InitializeAsync(database, seedData: false);
            return new ProfilerDatabaseFixture(path, database);
        }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection(Database.Value);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task<double> ScalarDoubleAsync(string sql)
        {
            await using var connection = new SqliteConnection(Database.Value);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToDouble(await command.ExecuteScalarAsync());
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { _path, _path + "-shm", _path + "-wal" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
