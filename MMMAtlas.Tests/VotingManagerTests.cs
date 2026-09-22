using Microsoft.Data.Sqlite;
using MMMAtlas.Data;
using MMMAtlas.Voting;

namespace MMMAtlas.Tests;

[TestClass]
public sealed class VotingManagerTests
{
    private string _databasePath = null!;

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
        command.CommandText = File.ReadAllText(schemaPath) + """

            INSERT INTO Users (UserId) VALUES (1), (2);
            INSERT INTO Games (GameId) VALUES (1);
            INSERT INTO SteamGames (GameId, SteamAppId, Name)
            VALUES (1, 1234, 'Test Game');
            """;
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
    public async Task SubmitCreatesAndAmendsVoteAndUpdatesAggregate()
    {
        var manager = CreateManager();

        var created = await manager.SubmitAsync(
            1,
            1,
            new VoteScores(8, 6, 2),
            CancellationToken.None,
            new AtlasIcon([1], 96, CreateLibraryCard()));

        Assert.IsNotNull(created);
        Assert.IsFalse(created.Amended);
        Assert.IsFalse(created.Scores.IsPublic);
        Assert.AreEqual(
            new VoteAggregate(1, 0.5, 0.375, 0.125),
            ReadAggregate());

        var amended = await manager.SubmitAsync(
            1,
            1,
            new VoteScores(4, 5, 1, IsPublic: true),
            CancellationToken.None);

        Assert.IsNotNull(amended);
        Assert.IsTrue(amended.Amended);
        Assert.IsTrue(amended.Scores.IsPublic);
        Assert.IsTrue(ReadVoteIsPublic(userId: 1));
        Assert.AreEqual(
            new VoteAggregate(1, 0.4, 0.5, 0.1),
            ReadAggregate());

        await manager.SubmitAsync(
            2,
            1,
            new VoteScores(1, 1, 8),
            CancellationToken.None);

        var aggregate = ReadAggregate();
        Assert.AreEqual(2, aggregate.VoteCount);
        Assert.AreEqual(0.25, aggregate.MicroRatio, 0.000001);
        Assert.AreEqual(0.3, aggregate.MesoRatio, 0.000001);
        Assert.AreEqual(0.45, aggregate.MacroRatio, 0.000001);

        var privateCount = await manager.MakeAllPrivateAsync(
            1,
            CancellationToken.None);

        Assert.AreEqual(1, privateCount);
        Assert.IsFalse(ReadVoteIsPublic(userId: 1));
        Assert.AreEqual(2L, CountVotes());
        Assert.AreEqual(aggregate, ReadAggregate());
    }

    [TestMethod]
    public async Task SubmitReturnsNullForUnknownGame()
    {
        var result = await CreateManager().SubmitAsync(
            1,
            999,
            new VoteScores(5, 5, 5),
            CancellationToken.None);

        Assert.IsNull(result);
        Assert.AreEqual(0L, CountVotes());
    }

    [TestMethod]
    public async Task ConcurrentSubmissionsDoNotLoseAggregateUpdates()
    {
        var manager = CreateManager();

        await Task.WhenAll(
            manager.SubmitAsync(
                1,
                1,
                new VoteScores(8, 6, 2),
                CancellationToken.None,
                new AtlasIcon([1], 96, CreateLibraryCard())),
            manager.SubmitAsync(
                2,
                1,
                new VoteScores(1, 1, 8),
                CancellationToken.None,
                new AtlasIcon([2], 96, CreateLibraryCard())));

        var aggregate = ReadAggregate();
        Assert.AreEqual(2, aggregate.VoteCount);
        Assert.AreEqual(0.3, aggregate.MicroRatio, 0.000001);
        Assert.AreEqual(0.2375, aggregate.MesoRatio, 0.000001);
        Assert.AreEqual(0.4625, aggregate.MacroRatio, 0.000001);
        Assert.AreEqual(2L, CountVotes());
    }

    [TestMethod]
    public async Task FirstVoteRequiresAndStoresOneAtlasIcon()
    {
        var manager = CreateManager();

        await Assert.ThrowsAsync<AtlasIconRequiredException>(() =>
            manager.SubmitAsync(
                1,
                1,
                new VoteScores(5, 5, 5),
                CancellationToken.None));
        Assert.AreEqual(0L, CountVotes());

        await manager.SubmitAsync(
            1,
            1,
            new VoteScores(5, 5, 5),
            CancellationToken.None,
            new AtlasIcon([10, 20, 30], 96, CreateLibraryCard()));
        await manager.SubmitAsync(
            2,
            1,
            new VoteScores(4, 5, 6),
            CancellationToken.None,
            new AtlasIcon([99], 96, CreateLibraryCard()));

        using var connection =
            new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                ImageContent,
                DiameterPixels,
                LibraryCardContentType,
                LibraryCardContent,
                LibraryCardWidthPixels,
                LibraryCardHeightPixels
            FROM GameAtlasIcons
            WHERE GameId = 1;
            """;
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        CollectionAssert.AreEqual(
            new byte[] { 10, 20, 30 },
            (byte[])reader.GetValue(0));
        Assert.AreEqual(96, reader.GetInt32(1));
        Assert.AreEqual("image/jpeg", reader.GetString(2));
        CollectionAssert.AreEqual(
            new byte[] { 40, 50, 60 },
            (byte[])reader.GetValue(3));
        Assert.AreEqual(600, reader.GetInt32(4));
        Assert.AreEqual(900, reader.GetInt32(5));
        Assert.IsFalse(reader.Read());
    }

    [TestMethod]
    public void AtlasIconsRequireTheStored96PixelSize()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new AtlasIcon([1], 95, CreateLibraryCard()));
    }

    [TestMethod]
    public async Task DatabaseMigrationRemovesLegacyIconUrlColumn()
    {
        using (var connection =
            new SqliteConnection($"Data Source={_databasePath}"))
        {
            connection.Open();
            using var addCommand = connection.CreateCommand();
            addCommand.CommandText =
                "ALTER TABLE SteamGames ADD COLUMN IconUrl TEXT NULL;";
            addCommand.ExecuteNonQuery();
        }

        await DatabaseInitializer.InitializeAsync(
            DatabaseConnection.Create(
                $"Data Source={_databasePath};Foreign Keys=True",
                Directory.GetCurrentDirectory()),
            seedData: false);

        using var verifyConnection =
            new SqliteConnection($"Data Source={_databasePath}");
        verifyConnection.Open();
        using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('SteamGames')
            WHERE name = 'IconUrl';
            """;
        Assert.AreEqual(0L, (long)verifyCommand.ExecuteScalar()!);
    }

    private static LibraryCard CreateLibraryCard() =>
        new([40, 50, 60], "image/jpeg", 600, 900);

    private VotingManager CreateManager() =>
        new(DatabaseConnection.Create(
            $"Data Source={_databasePath};Foreign Keys=True",
            Directory.GetCurrentDirectory()));

    private VoteAggregate ReadAggregate()
    {
        using var connection =
            new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT VoteCount, MicroRatio, MesoRatio, MacroRatio
            FROM GameVoteAggregates
            WHERE GameId = 1;
            """;
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        return new VoteAggregate(
            reader.GetInt32(0),
            reader.GetDouble(1),
            reader.GetDouble(2),
            reader.GetDouble(3));
    }

    private long CountVotes()
    {
        using var connection =
            new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Votes;";
        return (long)command.ExecuteScalar()!;
    }

    private bool ReadVoteIsPublic(long userId)
    {
        using var connection =
            new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT IsPublic
            FROM Votes
            WHERE UserId = $userId
                AND GameId = 1;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        return Convert.ToBoolean(command.ExecuteScalar());
    }

    private sealed record VoteAggregate(
        int VoteCount,
        double MicroRatio,
        double MesoRatio,
        double MacroRatio);
}
