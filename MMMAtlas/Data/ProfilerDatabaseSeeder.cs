using Microsoft.Data.Sqlite;

namespace MMMAtlas.Data;

public sealed record ProfilerSeedTargets(
    int Games,
    int Users,
    int Votes,
    int RandomSeed);

public sealed record ProfilerDatabaseSnapshot(
    int Games,
    int RatedGames,
    int Users,
    int Votes);

public sealed record ProfilerGameCreationResult(
    bool Created,
    bool TargetReached,
    long? GameId,
    int GameCount,
    string? Name,
    string? AtlasIconUrl,
    string? LibraryCardUrl,
    int AtlasIconBytes,
    int LibraryCardBytes);

/// <summary>
/// Builds synthetic scale-test data in the dedicated profiler database.
/// This type is only called by profiler endpoints, which are omitted from
/// Production builds.
/// </summary>
public static class ProfilerDatabaseSeeder
{
    public const int MaximumTargetCount = 10_000;
    private static readonly byte[] AtlasIconPlaceholder =
        Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private static readonly byte[] DevelopmentImagePlaceholder =
        System.Text.Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"920\" height=\"430\">"
            + "<rect width=\"920\" height=\"430\" fill=\"#14243a\"/>"
            + "<circle cx=\"460\" cy=\"215\" r=\"110\" fill=\"#65d6e7\"/>"
            + "</svg>");
    private static readonly byte[] LibraryCardPlaceholder =
    [
        0xff, 0xd8,
        0xff, 0xc0, 0x00, 0x11, 0x08,
        0x03, 0x84,
        0x02, 0x58,
        0x03,
        0x01, 0x11, 0x00,
        0x02, 0x11, 0x00,
        0x03, 0x11, 0x00,
        0xff, 0xd9
    ];
    private static readonly string[] GameGenres =
    [
        "Action", "Adventure", "Casual", "Indie", "RPG",
        "Simulation", "Strategy", "Puzzle"
    ];

    public static void EnsureDedicatedConnection(
        string profilerConnectionString,
        string? developmentConnectionString,
        string? productionConnectionString,
        string contentRootPath)
    {
        var profilerPath = ResolveDataSource(
            profilerConnectionString,
            contentRootPath);
        if (profilerPath is null)
        {
            throw new InvalidOperationException(
                "ConnectionStrings:MMMAtlasProfiler must use a SQLite database file.");
        }

        foreach (var ordinaryConnection in new[]
        {
            developmentConnectionString,
            productionConnectionString
        })
        {
            if (ordinaryConnection is null)
            {
                continue;
            }

            var ordinaryPath = ResolveDataSource(
                ordinaryConnection,
                contentRootPath);
            if (ordinaryPath is not null
                && string.Equals(
                    profilerPath,
                    ordinaryPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "ConnectionStrings:MMMAtlasProfiler must point to a file "
                    + "that is separate from the Development and Production databases.");
            }
        }
    }

    public static async Task<ProfilerDatabaseSnapshot> PrepareAsync(
        DatabaseConnection database,
        bool reset,
        ProfilerSeedTargets targets,
        CancellationToken cancellationToken = default)
    {
        Validate(targets);

        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);
        // Reserve the SQLite writer before inspecting and changing targets.
        await using var transaction = connection.BeginTransaction(deferred: false);

        if (reset)
        {
            await ClearAsync(connection, transaction, cancellationToken);
        }

        var before = await ReadSnapshotAsync(
            connection,
            transaction,
            cancellationToken);
        if (!reset
            && (before.Games > targets.Games
                || before.Users > targets.Users
                || before.Votes > targets.Votes))
        {
            throw new InvalidOperationException(
                "Grow targets cannot be smaller than the existing profiler database. "
                + $"Existing: {before.Games} games, {before.Users} users, "
                + $"{before.Votes} votes.");
        }

        await SeedUsersAsync(
            connection,
            transaction,
            targets.Users,
            cancellationToken);
        await SeedGamesAsync(
            connection,
            transaction,
            targets.Games,
            cancellationToken);
        await SeedVotesAsync(
            connection,
            transaction,
            targets,
            cancellationToken);
        await RebuildAggregatesAsync(
            connection,
            transaction,
            cancellationToken);

        var result = await ReadSnapshotAsync(
            connection,
            transaction,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public static async Task<ProfilerDatabaseSnapshot> ReadSnapshotAsync(
        DatabaseConnection database,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);
        return await ReadSnapshotAsync(
            connection,
            transaction: null,
            cancellationToken);
    }

    public static async Task<ProfilerGameCreationResult> CreateNextGameAsync(
        DatabaseConnection database,
        int targetGames,
        long userId,
        int randomSeed,
        CancellationToken cancellationToken = default)
    {
        if (targetGames is < 1 or > MaximumTargetCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetGames),
                $"The game target must be from 1 through {MaximumTargetCount:N0}.");
        }

        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);
        // Acquire the SQLite writer reservation before reading the next ID so
        // concurrent creator clients cannot select the same game number.
        await using var transaction = connection.BeginTransaction(deferred: false);

        await using var stateCommand = connection.CreateCommand();
        stateCommand.Transaction = transaction;
        stateCommand.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM Games),
                (SELECT COALESCE(MAX(GameId), 0) FROM Games),
                (SELECT EXISTS(SELECT 1 FROM Users WHERE UserId = $userId));
            """;
        stateCommand.Parameters.AddWithValue("$userId", userId);
        await using var stateReader = await stateCommand.ExecuteReaderAsync(
            cancellationToken);
        await stateReader.ReadAsync(cancellationToken);
        var gameCount = stateReader.GetInt32(0);
        var maximumGameId = stateReader.GetInt64(1);
        var userExists = stateReader.GetBoolean(2);
        await stateReader.DisposeAsync();

        if (!userExists)
        {
            throw new InvalidOperationException(
                $"Profiler game creator user {userId} does not exist.");
        }

        if (gameCount >= targetGames)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ProfilerGameCreationResult(
                false,
                true,
                null,
                gameCount,
                null,
                null,
                null,
                0,
                0);
        }

        var gameId = maximumGameId + 1;
        var hash = unchecked(
            randomSeed
            ^ (int)(gameId * 19_349_663)
            ^ (int)(userId * 73_856_093));
        var name = $"Profiler Game {gameId:000000}";
        var micro = Score(hash, 0);
        var meso = Score(hash, 8);
        var macro = Score(hash, 16);
        var total = micro + meso + macro;
        var svg = System.Text.Encoding.UTF8.GetBytes($"""
            <svg xmlns="http://www.w3.org/2000/svg" width="920" height="430" viewBox="0 0 920 430">
              <rect width="920" height="430" fill="#14243a"/>
              <circle cx="460" cy="190" r="110" fill="#65d6e7" opacity=".75"/>
              <text x="460" y="380" fill="#edf5ff" font-family="sans-serif" font-size="48" text-anchor="middle">{name}</text>
            </svg>
            """);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Games (GameId, CreatedAtUtc)
            VALUES ($gameId, CURRENT_TIMESTAMP);

            INSERT INTO SteamGames
            (
                GameId, SteamAppId, Name, Description, ReleaseYear, IsFree,
                StoreMetadataFetchedAtUtc, CreatedAtUtc, UpdatedAtUtc
            )
            VALUES
            (
                $gameId, $appId, $name, $description, $year, $isFree,
                CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
            );

            INSERT INTO SteamGameDevelopers (GameId, DeveloperName)
            VALUES ($gameId, $developer);
            INSERT INTO SteamGamePublishers (GameId, PublisherName)
            VALUES ($gameId, $publisher);
            INSERT INTO SteamGameGenres (GameId, GenreName)
            VALUES ($gameId, $genre1), ($gameId, $genre2);

            INSERT INTO DevelopmentGameInfo
            (
                GameId, Name, Summary, ImageContentType, ImageContent,
                CreatedAtUtc, UpdatedAtUtc
            )
            VALUES
            (
                $gameId, $name, $description, 'image/svg+xml', $image,
                CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
            );

            INSERT INTO GameAtlasIcons
            (
                GameId, ImageContentType, ImageContent, DiameterPixels,
                LibraryCardContentType, LibraryCardContent,
                LibraryCardWidthPixels, LibraryCardHeightPixels, CreatedAtUtc
            )
            VALUES
            (
                $gameId, 'image/png', $atlasIcon, 96,
                'image/jpeg', $libraryCard, 600, 900, CURRENT_TIMESTAMP
            );

            INSERT INTO SteamLibraryGames
            (
                UserId, GameId, PlaytimeForeverMinutes, FirstSeenAtUtc,
                VerifiedAtUtc
            )
            VALUES
            (
                $userId, $gameId, $playtime, CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP
            );

            INSERT INTO Votes
            (
                UserId, GameId, MicroScore, MesoScore, MacroScore, IsPublic,
                CreatedAtUtc, UpdatedAtUtc
            )
            VALUES
            (
                $userId, $gameId, $micro, $meso, $macro, $public,
                CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
            );

            INSERT INTO GameVoteAggregates
            (
                GameId, VoteCount, MicroRatio, MesoRatio, MacroRatio,
                UpdatedAtUtc
            )
            VALUES
            (
                $gameId, 1, $microRatio, $mesoRatio, $macroRatio,
                CURRENT_TIMESTAMP
            );
            """;
        command.Parameters.AddWithValue("$gameId", gameId);
        command.Parameters.AddWithValue("$appId", 10_000_000L + gameId);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue(
            "$description",
            $"Synthetic profiler fixture {gameId:N0} supplied without Steam API access.");
        command.Parameters.AddWithValue("$year", 1990 + gameId % 36);
        command.Parameters.AddWithValue("$isFree", gameId % 11 == 0 ? 1 : 0);
        command.Parameters.AddWithValue(
            "$developer",
            $"Profiler Studio {(gameId % 40) + 1:00}");
        command.Parameters.AddWithValue(
            "$publisher",
            $"Profiler Publisher {(gameId % 20) + 1:00}");
        command.Parameters.AddWithValue("$genre1", GameGenres[gameId % GameGenres.Length]);
        command.Parameters.AddWithValue(
            "$genre2",
            GameGenres[(gameId + 3) % GameGenres.Length]);
        command.Parameters.AddWithValue("$image", svg);
        command.Parameters.AddWithValue("$atlasIcon", AtlasIconPlaceholder);
        command.Parameters.AddWithValue("$libraryCard", LibraryCardPlaceholder);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$playtime", Math.Abs(hash % 20_000));
        command.Parameters.AddWithValue("$micro", micro);
        command.Parameters.AddWithValue("$meso", meso);
        command.Parameters.AddWithValue("$macro", macro);
        command.Parameters.AddWithValue("$public", (hash & 3) == 0 ? 0 : 1);
        command.Parameters.AddWithValue("$microRatio", micro / total);
        command.Parameters.AddWithValue("$mesoRatio", meso / total);
        command.Parameters.AddWithValue("$macroRatio", macro / total);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        gameCount++;
        return new ProfilerGameCreationResult(
            true,
            gameCount >= targetGames,
            gameId,
            gameCount,
            name,
            $"/api/games/{gameId}/atlas-icon",
            $"/api/games/{gameId}/library-card",
            AtlasIconPlaceholder.Length,
            LibraryCardPlaceholder.Length);
    }

    private static void Validate(ProfilerSeedTargets targets)
    {
        static void ValidateCount(string name, int value)
        {
            if (value is < 0 or > MaximumTargetCount)
            {
                throw new ArgumentOutOfRangeException(
                    name,
                    $"Profiler seed targets must be from 0 through {MaximumTargetCount:N0}.");
            }
        }

        ValidateCount(nameof(targets.Games), targets.Games);
        ValidateCount(nameof(targets.Users), targets.Users);
        ValidateCount(nameof(targets.Votes), targets.Votes);
        if (targets.Votes > 0 && (targets.Games == 0 || targets.Users == 0))
        {
            throw new ArgumentException(
                "A positive vote target requires at least one game and one user.");
        }

        if ((long)targets.Games * targets.Users < targets.Votes)
        {
            throw new ArgumentException(
                "The vote target exceeds the number of unique user/game pairs.");
        }
    }

    private static async Task ClearAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM SteamLibraryGames;
            DELETE FROM Votes;
            DELETE FROM GameVoteAggregates;
            DELETE FROM SteamGameDevelopers;
            DELETE FROM SteamGamePublishers;
            DELETE FROM SteamGameGenres;
            DELETE FROM GameAtlasIcons;
            DELETE FROM DevelopmentGameInfo;
            DELETE FROM SteamGames;
            DELETE FROM SteamAccounts;
            DELETE FROM Users;
            DELETE FROM Games;
            DELETE FROM sqlite_sequence
            WHERE name IN ('Users', 'Games');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedUsersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int target,
        CancellationToken cancellationToken)
    {
        var existing = await ReadIdsAsync(
            connection,
            transaction,
            "Users",
            "UserId",
            cancellationToken);
        var nextId = existing.Count == 0 ? 1L : existing[^1] + 1;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Users
                (UserId, ShowVotesPublicly, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                ($id, $public, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);

            INSERT INTO SteamAccounts
            (
                UserId,
                SteamId,
                DataCollectionEnabled,
                PersonaName,
                CreatedAtUtc,
                UpdatedAtUtc,
                LibraryCheckStatus
            )
            VALUES
            (
                $id,
                $steamId,
                0,
                $name,
                CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP,
                'NeverChecked'
            );
            """;
        var idParameter = command.Parameters.Add("$id", SqliteType.Integer);
        var publicParameter = command.Parameters.Add("$public", SqliteType.Integer);
        var steamIdParameter = command.Parameters.Add("$steamId", SqliteType.Integer);
        var nameParameter = command.Parameters.Add("$name", SqliteType.Text);

        for (var count = existing.Count; count < target; count++, nextId++)
        {
            idParameter.Value = nextId;
            publicParameter.Value = nextId % 4 == 0 ? 0 : 1;
            steamIdParameter.Value = 76_561_199_000_000_000L + nextId;
            nameParameter.Value = $"Profiler User {nextId:N0}";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task SeedGamesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int target,
        CancellationToken cancellationToken)
    {
        var existing = await ReadIdsAsync(
            connection,
            transaction,
            "Games",
            "GameId",
            cancellationToken);
        var nextId = existing.Count == 0 ? 1L : existing[^1] + 1;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Games (GameId, CreatedAtUtc)
            VALUES ($id, CURRENT_TIMESTAMP);

            INSERT INTO SteamGames
            (
                GameId,
                SteamAppId,
                Name,
                Description,
                ReleaseYear,
                IsFree,
                StoreMetadataFetchedAtUtc,
                CreatedAtUtc,
                UpdatedAtUtc
            )
            VALUES
            (
                $id,
                $appId,
                $name,
                $description,
                $year,
                $isFree,
                CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP
            );

            INSERT INTO SteamGameDevelopers (GameId, DeveloperName)
            VALUES ($id, $developer);
            INSERT INTO SteamGamePublishers (GameId, PublisherName)
            VALUES ($id, $publisher);
            INSERT INTO SteamGameGenres (GameId, GenreName)
            VALUES ($id, $genre1), ($id, $genre2);

            INSERT INTO DevelopmentGameInfo
            (
                GameId, Name, Summary, ImageContentType, ImageContent,
                CreatedAtUtc, UpdatedAtUtc
            )
            VALUES
            (
                $id, $name, $description, 'image/svg+xml', $image,
                CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
            );

            INSERT INTO GameAtlasIcons
            (
                GameId, ImageContentType, ImageContent, DiameterPixels,
                LibraryCardContentType, LibraryCardContent,
                LibraryCardWidthPixels, LibraryCardHeightPixels, CreatedAtUtc
            )
            VALUES
            (
                $id, 'image/png', $atlasIcon, 96,
                'image/jpeg', $libraryCard, 600, 900, CURRENT_TIMESTAMP
            );
            """;
        var idParameter = command.Parameters.Add("$id", SqliteType.Integer);
        var appIdParameter = command.Parameters.Add("$appId", SqliteType.Integer);
        var nameParameter = command.Parameters.Add("$name", SqliteType.Text);
        var descriptionParameter = command.Parameters.Add("$description", SqliteType.Text);
        var yearParameter = command.Parameters.Add("$year", SqliteType.Integer);
        var isFreeParameter = command.Parameters.Add("$isFree", SqliteType.Integer);
        var developerParameter = command.Parameters.Add("$developer", SqliteType.Text);
        var publisherParameter = command.Parameters.Add("$publisher", SqliteType.Text);
        var genre1Parameter = command.Parameters.Add("$genre1", SqliteType.Text);
        var genre2Parameter = command.Parameters.Add("$genre2", SqliteType.Text);
        command.Parameters.AddWithValue("$image", DevelopmentImagePlaceholder);
        command.Parameters.AddWithValue("$atlasIcon", AtlasIconPlaceholder);
        command.Parameters.AddWithValue("$libraryCard", LibraryCardPlaceholder);

        for (var count = existing.Count; count < target; count++, nextId++)
        {
            idParameter.Value = nextId;
            appIdParameter.Value = 10_000_000L + nextId;
            nameParameter.Value = $"Profiler Game {nextId:000000}";
            descriptionParameter.Value =
                $"Synthetic profiler fixture {nextId:N0} for API scale testing.";
            yearParameter.Value = 1990 + (nextId % 36);
            isFreeParameter.Value = nextId % 11 == 0 ? 1 : 0;
            developerParameter.Value = $"Profiler Studio {(nextId % 40) + 1:00}";
            publisherParameter.Value = $"Profiler Publisher {(nextId % 20) + 1:00}";
            genre1Parameter.Value = GameGenres[nextId % GameGenres.Length];
            genre2Parameter.Value = GameGenres[(nextId + 3) % GameGenres.Length];
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task SeedVotesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProfilerSeedTargets targets,
        CancellationToken cancellationToken)
    {
        var existingVotes = await ReadVotePairsAsync(
            connection,
            transaction,
            cancellationToken);
        if (existingVotes.Count >= targets.Votes)
        {
            return;
        }

        var users = await ReadIdsAsync(
            connection,
            transaction,
            "Users",
            "UserId",
            cancellationToken);
        var games = await ReadIdsAsync(
            connection,
            transaction,
            "Games",
            "GameId",
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Votes
            (
                UserId,
                GameId,
                MicroScore,
                MesoScore,
                MacroScore,
                IsPublic,
                CreatedAtUtc,
                UpdatedAtUtc
            )
            VALUES
            (
                $userId,
                $gameId,
                $micro,
                $meso,
                $macro,
                $public,
                CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP
            );
            """;
        var userParameter = command.Parameters.Add("$userId", SqliteType.Integer);
        var gameParameter = command.Parameters.Add("$gameId", SqliteType.Integer);
        var microParameter = command.Parameters.Add("$micro", SqliteType.Real);
        var mesoParameter = command.Parameters.Add("$meso", SqliteType.Real);
        var macroParameter = command.Parameters.Add("$macro", SqliteType.Real);
        var publicParameter = command.Parameters.Add("$public", SqliteType.Integer);

        for (var layer = 0;
            layer < users.Count && existingVotes.Count < targets.Votes;
            layer++)
        {
            for (var gameIndex = 0;
                gameIndex < games.Count && existingVotes.Count < targets.Votes;
                gameIndex++)
            {
                var gameId = games[gameIndex];
                var userIndex = (layer + gameIndex * 17 + PositiveMod(
                    targets.RandomSeed,
                    users.Count)) % users.Count;
                var userId = users[userIndex];
                if (!existingVotes.Add((userId, gameId)))
                {
                    continue;
                }

                var hash = unchecked(
                    targets.RandomSeed
                    ^ (int)(userId * 73_856_093)
                    ^ (int)(gameId * 19_349_663));
                userParameter.Value = userId;
                gameParameter.Value = gameId;
                microParameter.Value = Score(hash, 0);
                mesoParameter.Value = Score(hash, 8);
                macroParameter.Value = Score(hash, 16);
                publicParameter.Value = (hash & 3) == 0 ? 0 : 1;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        if (existingVotes.Count != targets.Votes)
        {
            throw new InvalidOperationException(
                $"Could only construct {existingVotes.Count:N0} unique votes "
                + $"for the requested target of {targets.Votes:N0}.");
        }
    }

    private static async Task RebuildAggregatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM GameVoteAggregates;
            INSERT INTO GameVoteAggregates
            (
                GameId,
                VoteCount,
                MicroRatio,
                MesoRatio,
                MacroRatio,
                UpdatedAtUtc
            )
            SELECT
                GameId,
                COUNT(*),
                AVG(MicroScore / (MicroScore + MesoScore + MacroScore)),
                AVG(MesoScore / (MicroScore + MesoScore + MacroScore)),
                AVG(MacroScore / (MicroScore + MesoScore + MacroScore)),
                CURRENT_TIMESTAMP
            FROM Votes
            GROUP BY GameId;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ProfilerDatabaseSnapshot> ReadSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM Games),
                (SELECT COUNT(*) FROM GameVoteAggregates),
                (SELECT COUNT(*) FROM Users),
                (SELECT COUNT(*) FROM Votes);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new ProfilerDatabaseSnapshot(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    private static async Task<List<long>> ReadIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string idColumn,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {idColumn} FROM {table} ORDER BY {idColumn};";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private static async Task<HashSet<(long UserId, long GameId)>> ReadVotePairsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT UserId, GameId FROM Votes;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var pairs = new HashSet<(long, long)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            pairs.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        return pairs;
    }

    private static double Score(int hash, int shift) =>
        1 + ((uint)hash >> shift & 0xff) % 91 / 10d;

    private static int PositiveMod(int value, int divisor) =>
        (int)((uint)value % (uint)divisor);

    private static string? ResolveDataSource(
        string connectionString,
        string contentRootPath)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || builder.DataSource == ":memory:")
        {
            return null;
        }

        return Path.GetFullPath(builder.DataSource, contentRootPath);
    }
}
