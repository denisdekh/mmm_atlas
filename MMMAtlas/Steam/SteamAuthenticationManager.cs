using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using MMMAtlas.Data;

namespace MMMAtlas.Steam;

public sealed class SteamAuthenticationManager(
    DatabaseConnection database,
    SteamApiClient steamApiClient,
    ISteamStoreClient steamStoreClient,
    ILogger<SteamAuthenticationManager> logger,
    SteamArtworkService artworkService,
    SteamLibraryFilter libraryFilter)
{
    public async Task<SteamSignInResult> CompleteSignInAsync(
        long steamId,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var account = await GetOrCreateAccountAsync(
            steamId,
            cancellationToken);

        if (account.DataCollectionEnabled)
        {
            await RefreshStoredDataAsync(
                account.UserId,
                cancellationToken);
        }

        var dashboard = await GetDashboardAsync(
            account.UserId,
            cancellationToken);

        var result = new SteamSignInResult(
            account.UserId,
            steamId,
            dashboard?.PersonaName,
            account.DataCollectionEnabled);

        logger.LogInformation(
            "Steam sign-in completed for UserId {UserId} in "
            + "{ElapsedMilliseconds:F1} ms; data collection enabled: "
            + "{DataCollectionEnabled}.",
            account.UserId,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            account.DataCollectionEnabled);

        return result;
    }

    public async Task RefreshStoredDataAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var account = await GetAccountAsync(userId, cancellationToken);
        if (account is null || !account.DataCollectionEnabled)
        {
            return;
        }

        var profileTask = steamApiClient.GetProfileAsync(
            account.SteamId,
            cancellationToken);
        var libraryTask = steamApiClient.GetOwnedGamesAsync(
            account.SteamId,
            cancellationToken);

        await Task.WhenAll(profileTask, libraryTask);
        var library = await libraryFilter.FilterAsync(await libraryTask, cancellationToken);
        var newGameMetadata = await FetchNewGameMetadataAsync(
            library,
            cancellationToken);
        await SaveFetchedDataAsync(
            userId,
            await profileTask,
            library,
            newGameMetadata,
            cancellationToken);

        if (library.Availability == SteamLibraryAvailability.Available)
        {
            await artworkService.ImportMissingAsync(
                library.Games.Select(game => game.AppId), cancellationToken);
        }

        logger.LogInformation(
            "Steam data refresh completed for UserId {UserId} in "
            + "{ElapsedMilliseconds:F1} ms; library status {LibraryStatus}, "
            + "owned games {OwnedGameCount}, new metadata records "
            + "{MetadataCount}.",
            userId,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            library.Availability,
            library.Games.Count,
            newGameMetadata.Count);
    }

    public async Task SetDataCollectionEnabledAsync(
        long userId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            await EraseStoredDataAsync(userId, cancellationToken);
            return;
        }

        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SteamAccounts
            SET DataCollectionEnabled = 1,
                UpdatedAtUtc = CURRENT_TIMESTAMP
            WHERE UserId = $userId;
            """;
        command.Parameters.AddWithValue("$userId", userId);

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidOperationException("The Steam account does not exist.");
        }

        logger.LogInformation(
            "Steam data collection enabled for UserId {UserId}.",
            userId);

        await RefreshStoredDataAsync(userId, cancellationToken);
    }

    public async Task EraseStoredDataAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(
            connection,
            """
            DELETE FROM SteamLibraryGames
            WHERE UserId = $userId;

            UPDATE SteamAccounts
            SET DataCollectionEnabled = 0,
                PersonaName = NULL,
                ProfileUrl = NULL,
                AvatarUrl = NULL,
                ProfileFetchedAtUtc = NULL,
                LibraryCheckStatus = 'NeverChecked',
                LibraryCheckedAtUtc = NULL,
                LibrarySyncedAtUtc = NULL,
                UpdatedAtUtc = CURRENT_TIMESTAMP
            WHERE UserId = $userId;
            """,
            userId,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Stored Steam data erased and collection disabled for UserId {UserId}.",
            userId);
    }

    public async Task<SteamDashboard?> GetDashboardAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);

        await using var accountCommand = connection.CreateCommand();
        accountCommand.CommandText = """
            SELECT
                SteamId,
                DataCollectionEnabled,
                PersonaName,
                ProfileUrl,
                AvatarUrl,
                ProfileFetchedAtUtc,
                LibraryCheckStatus,
                LibraryCheckedAtUtc,
                LibrarySyncedAtUtc
            FROM SteamAccounts
            WHERE UserId = $userId;
            """;
        accountCommand.Parameters.AddWithValue("$userId", userId);

        await using var reader =
            await accountCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var dashboard = new SteamDashboard(
            userId,
            reader.GetInt64(0),
            reader.GetBoolean(1),
            ReadNullableString(reader, 2),
            ReadNullableString(reader, 3),
            ReadNullableString(reader, 4),
            ReadNullableDateTimeOffset(reader, 5),
            reader.GetString(6),
            ReadNullableDateTimeOffset(reader, 7),
            ReadNullableDateTimeOffset(reader, 8),
            []);

        await reader.DisposeAsync();

        await using var gamesCommand = connection.CreateCommand();
        gamesCommand.CommandText = """
            SELECT
                steam.GameId,
                steam.SteamAppId,
                COALESCE(steam.Name, development.Name, 'Steam app ' || steam.SteamAppId),
                library.PlaytimeForeverMinutes,
                library.PlaytimeTwoWeeksMinutes,
                library.LastPlayedAtUtc,
                vote.MicroScore,
                vote.MesoScore,
                vote.MacroScore,
                COALESCE(vote.IsPublic, 0),
                CASE WHEN aggregate.GameId IS NULL OR steam.AtlasIconNeedsUpdate = 1 THEN 1 ELSE 0 END,
                CASE WHEN atlas.GameId IS NULL THEN 0 ELSE 1 END,
                steam.LibraryCardUrl
            FROM SteamLibraryGames AS library
            INNER JOIN SteamGames AS steam
                ON steam.GameId = library.GameId
            LEFT JOIN DevelopmentGameInfo AS development
                ON development.GameId = library.GameId
            LEFT JOIN Votes AS vote
                ON vote.UserId = library.UserId
                AND vote.GameId = library.GameId
            LEFT JOIN GameVoteAggregates AS aggregate
                ON aggregate.GameId = library.GameId
            LEFT JOIN GameAtlasIcons AS atlas
                ON atlas.GameId = library.GameId
            WHERE library.UserId = $userId
            ORDER BY library.PlaytimeForeverMinutes DESC, steam.SteamAppId
            LIMIT 20;
            """;
        gamesCommand.Parameters.AddWithValue("$userId", userId);

        var games = new List<SteamDashboardGame>();
        await using var gamesReader =
            await gamesCommand.ExecuteReaderAsync(cancellationToken);
        while (await gamesReader.ReadAsync(cancellationToken))
        {
            games.Add(new SteamDashboardGame(
                gamesReader.GetInt64(0),
                gamesReader.GetInt32(1),
                gamesReader.GetString(2),
                gamesReader.GetInt32(3),
                gamesReader.IsDBNull(4) ? null : gamesReader.GetInt32(4),
                ReadNullableDateTimeOffset(gamesReader, 5),
                gamesReader.IsDBNull(6) ? null : gamesReader.GetDouble(6),
                gamesReader.IsDBNull(7) ? null : gamesReader.GetDouble(7),
                gamesReader.IsDBNull(8) ? null : gamesReader.GetDouble(8),
                gamesReader.GetBoolean(9),
                gamesReader.GetBoolean(10),
                gamesReader.GetBoolean(11),
                ReadNullableString(gamesReader, 12)));
        }

        return dashboard with { MostPlayedGames = games };
    }

    private async Task<SteamAccount> GetOrCreateAccountAsync(
        long steamId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        await using var findCommand = connection.CreateCommand();
        findCommand.CommandText = """
            SELECT UserId, DataCollectionEnabled
            FROM SteamAccounts
            WHERE SteamId = $steamId;
            """;
        findCommand.Parameters.AddWithValue("$steamId", steamId);

        await using (var reader =
            await findCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                var userId = reader.GetInt64(0);
                var enabled = reader.GetBoolean(1);
                await reader.DisposeAsync();

                await transaction.CommitAsync(cancellationToken);
                return new SteamAccount(userId, steamId, enabled);
            }
        }

        await using var createUserCommand = connection.CreateCommand();
        createUserCommand.CommandText = """
            INSERT INTO Users DEFAULT VALUES;
            SELECT last_insert_rowid();
            """;
        var newUserId = Convert.ToInt64(
            await createUserCommand.ExecuteScalarAsync(cancellationToken));

        await using var createAccountCommand = connection.CreateCommand();
        createAccountCommand.CommandText = """
            INSERT INTO SteamAccounts
            (
                UserId,
                SteamId
            )
            VALUES
            (
                $userId,
                $steamId
            );
            """;
        createAccountCommand.Parameters.AddWithValue("$userId", newUserId);
        createAccountCommand.Parameters.AddWithValue("$steamId", steamId);
        await createAccountCommand.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Steam account created for UserId {UserId}.",
            newUserId);
        return new SteamAccount(newUserId, steamId, false);
    }

    private async Task<SteamAccount?> GetAccountAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SteamId, DataCollectionEnabled
            FROM SteamAccounts
            WHERE UserId = $userId;
            """;
        command.Parameters.AddWithValue("$userId", userId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SteamAccount(
                userId,
                reader.GetInt64(0),
                reader.GetBoolean(1))
            : null;
    }

    private async Task SaveFetchedDataAsync(
        long userId,
        SteamProfile? profile,
        SteamLibraryResult library,
        IReadOnlyDictionary<int, SteamStoreGame> newGameMetadata,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        if (profile is not null)
        {
            await using var profileCommand = connection.CreateCommand();
            profileCommand.CommandText = """
                UPDATE SteamAccounts
                SET PersonaName = $personaName,
                    ProfileUrl = $profileUrl,
                    AvatarUrl = $avatarUrl,
                    ProfileFetchedAtUtc = CURRENT_TIMESTAMP,
                    UpdatedAtUtc = CURRENT_TIMESTAMP
                WHERE UserId = $userId
                    AND DataCollectionEnabled = 1;
                """;
            profileCommand.Parameters.AddWithValue("$userId", userId);
            profileCommand.Parameters.AddWithValue(
                "$personaName",
                profile.PersonaName);
            profileCommand.Parameters.AddWithValue(
                "$profileUrl",
                (object?)profile.ProfileUrl ?? DBNull.Value);
            profileCommand.Parameters.AddWithValue(
                "$avatarUrl",
                (object?)profile.AvatarUrl ?? DBNull.Value);
            await profileCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        switch (library.Availability)
        {
            case SteamLibraryAvailability.Available:
                await SaveAvailableLibraryAsync(
                    connection,
                    userId,
                    library.Games,
                    newGameMetadata,
                    cancellationToken);
                break;

            case SteamLibraryAvailability.Private:
                await SetLibraryStatusAsync(
                    connection,
                    userId,
                    "Private",
                    cancellationToken);
                break;

            case SteamLibraryAvailability.Unavailable:
                await SetLibraryStatusAsync(
                    connection,
                    userId,
                    "Unavailable",
                    cancellationToken);
                break;
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<IReadOnlyDictionary<int, SteamStoreGame>>
        FetchNewGameMetadataAsync(
            SteamLibraryResult library,
            CancellationToken cancellationToken)
    {
        if (library.Availability != SteamLibraryAvailability.Available
            || library.Games.Count == 0)
        {
            return new Dictionary<int, SteamStoreGame>();
        }

        var appIdsWithStoreMetadata = new HashSet<int>();
        await using (var connection = new SqliteConnection(database.Value))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT SteamAppId
                FROM SteamGames
                WHERE StoreMetadataFetchedAtUtc IS NOT NULL;
                """;
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                appIdsWithStoreMetadata.Add(reader.GetInt32(0));
            }
        }

        var newAppIds = library.Games
            .Select(game => game.AppId)
            .Where(appId => !appIdsWithStoreMetadata.Contains(appId))
            .Distinct()
            .ToArray();
        var results = new ConcurrentDictionary<int, SteamStoreGame>();

        await Parallel.ForEachAsync(
            newAppIds,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 4
            },
            async (appId, token) =>
            {
                var metadata = await steamStoreClient.GetGameAsync(
                    appId,
                    token);
                if (metadata is not null)
                {
                    results[appId] = metadata;
                }
            });

        return results;
    }

    private static async Task SaveAvailableLibraryAsync(
        SqliteConnection connection,
        long userId,
        IReadOnlyList<SteamOwnedGame> games,
        IReadOnlyDictionary<int, SteamStoreGame> newGameMetadata,
        CancellationToken cancellationToken)
    {
        var verifiedAtUtc = DateTimeOffset.UtcNow.ToString("O");

        foreach (var game in games)
        {
            var gameId = await GetOrCreateGameAsync(
                connection,
                game,
                newGameMetadata.GetValueOrDefault(game.AppId),
                cancellationToken);

            await using var libraryCommand = connection.CreateCommand();
            libraryCommand.CommandText = """
                INSERT INTO SteamLibraryGames
                (
                    UserId,
                    GameId,
                    PlaytimeForeverMinutes,
                    PlaytimeTwoWeeksMinutes,
                    LastPlayedAtUtc,
                    VerifiedAtUtc
                )
                VALUES
                (
                    $userId,
                    $gameId,
                    $playtimeForever,
                    $playtimeTwoWeeks,
                    $lastPlayedAtUtc,
                    $verifiedAtUtc
                )
                ON CONFLICT (UserId, GameId) DO UPDATE SET
                    PlaytimeForeverMinutes = excluded.PlaytimeForeverMinutes,
                    PlaytimeTwoWeeksMinutes = excluded.PlaytimeTwoWeeksMinutes,
                    LastPlayedAtUtc = excluded.LastPlayedAtUtc,
                    VerifiedAtUtc = excluded.VerifiedAtUtc;
                """;
            libraryCommand.Parameters.AddWithValue("$userId", userId);
            libraryCommand.Parameters.AddWithValue("$gameId", gameId);
            libraryCommand.Parameters.AddWithValue(
                "$playtimeForever",
                game.PlaytimeForeverMinutes);
            libraryCommand.Parameters.AddWithValue(
                "$playtimeTwoWeeks",
                (object?)game.PlaytimeTwoWeeksMinutes ?? DBNull.Value);
            libraryCommand.Parameters.AddWithValue(
                "$lastPlayedAtUtc",
                game.LastPlayedAtUtc?.ToString("O") ?? (object)DBNull.Value);
            libraryCommand.Parameters.AddWithValue(
                "$verifiedAtUtc",
                verifiedAtUtc);
            await libraryCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var deleteMissingCommand = connection.CreateCommand();
        deleteMissingCommand.CommandText = """
            DELETE FROM SteamLibraryGames
            WHERE UserId = $userId
                AND VerifiedAtUtc <> $verifiedAtUtc;
            """;
        deleteMissingCommand.Parameters.AddWithValue("$userId", userId);
        deleteMissingCommand.Parameters.AddWithValue(
            "$verifiedAtUtc",
            verifiedAtUtc);
        await deleteMissingCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var statusCommand = connection.CreateCommand();
        statusCommand.CommandText = """
            UPDATE SteamAccounts
            SET LibraryCheckStatus = 'Available',
                LibraryCheckedAtUtc = CURRENT_TIMESTAMP,
                LibrarySyncedAtUtc = CURRENT_TIMESTAMP,
                UpdatedAtUtc = CURRENT_TIMESTAMP
            WHERE UserId = $userId
                AND DataCollectionEnabled = 1;
            """;
        statusCommand.Parameters.AddWithValue("$userId", userId);
        await statusCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> GetOrCreateGameAsync(
        SqliteConnection connection,
        SteamOwnedGame game,
        SteamStoreGame? storeGame,
        CancellationToken cancellationToken)
    {
        await using var findCommand = connection.CreateCommand();
        findCommand.CommandText = """
            SELECT GameId
            FROM SteamGames
            WHERE SteamAppId = $steamAppId;
            """;
        findCommand.Parameters.AddWithValue("$steamAppId", game.AppId);
        var existingGameId =
            await findCommand.ExecuteScalarAsync(cancellationToken);

        if (existingGameId is not null)
        {
            await using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = """
                UPDATE SteamGames
                SET Name = COALESCE($name, Name),
                    Description = COALESCE(Description, $description),
                    ReleaseYear = COALESCE(ReleaseYear, $releaseYear),
                    IsFree = COALESCE($isFree, IsFree),
                    StoreMetadataFetchedAtUtc = CASE
                        WHEN $storeMetadataFetched = 1
                            THEN CURRENT_TIMESTAMP
                        ELSE StoreMetadataFetchedAtUtc
                    END,
                    UpdatedAtUtc = CURRENT_TIMESTAMP
                WHERE GameId = $gameId;
                """;
            updateCommand.Parameters.AddWithValue(
                "$gameId",
                Convert.ToInt64(existingGameId));
            updateCommand.Parameters.AddWithValue(
                "$name",
                (object?)game.Name ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue(
                "$description",
                (object?)storeGame?.Description ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue(
                "$releaseYear",
                (object?)storeGame?.ReleaseYear ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue(
                "$isFree",
                (object?)storeGame?.IsFree ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue(
                "$storeMetadataFetched",
                storeGame is null ? 0 : 1);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);

            var existingId = Convert.ToInt64(existingGameId);
            if (storeGame is not null)
            {
                await ReplaceStoreLabelsAsync(
                    connection,
                    existingId,
                    storeGame,
                    cancellationToken);
            }

            return existingId;
        }

        await using var createGameCommand = connection.CreateCommand();
        createGameCommand.CommandText = """
            INSERT INTO Games DEFAULT VALUES;
            SELECT last_insert_rowid();
            """;
        var gameId = Convert.ToInt64(
            await createGameCommand.ExecuteScalarAsync(cancellationToken));

        await using var createSteamGameCommand = connection.CreateCommand();
        createSteamGameCommand.CommandText = """
            INSERT INTO SteamGames
                (
                    GameId,
                    SteamAppId,
                    Name,
                    Description,
                    ReleaseYear,
                    IsFree,
                    StoreMetadataFetchedAtUtc
                )
            VALUES
                (
                    $gameId,
                    $steamAppId,
                    $name,
                    $description,
                    $releaseYear,
                    $isFree,
                    CASE
                        WHEN $storeMetadataFetched = 1
                            THEN CURRENT_TIMESTAMP
                        ELSE NULL
                    END
                );
            """;
        createSteamGameCommand.Parameters.AddWithValue("$gameId", gameId);
        createSteamGameCommand.Parameters.AddWithValue("$steamAppId", game.AppId);
        createSteamGameCommand.Parameters.AddWithValue(
            "$name",
            (object?)game.Name ?? DBNull.Value);
        createSteamGameCommand.Parameters.AddWithValue(
            "$description",
            (object?)storeGame?.Description ?? DBNull.Value);
        createSteamGameCommand.Parameters.AddWithValue(
            "$releaseYear",
            (object?)storeGame?.ReleaseYear ?? DBNull.Value);
        createSteamGameCommand.Parameters.AddWithValue(
            "$isFree",
            (object?)storeGame?.IsFree ?? DBNull.Value);
        createSteamGameCommand.Parameters.AddWithValue(
            "$storeMetadataFetched",
            storeGame is null ? 0 : 1);
        await createSteamGameCommand.ExecuteNonQueryAsync(cancellationToken);

        if (storeGame is not null)
        {
            await ReplaceStoreLabelsAsync(
                connection,
                gameId,
                storeGame,
                cancellationToken);
        }

        return gameId;
    }

    private static async Task ReplaceStoreLabelsAsync(
        SqliteConnection connection,
        long gameId,
        SteamStoreGame storeGame,
        CancellationToken cancellationToken)
    {
        await ReplaceStoreLabelsAsync(
            connection,
            gameId,
            "SteamGameDevelopers",
            "DeveloperName",
            storeGame.Developers,
            cancellationToken);
        await ReplaceStoreLabelsAsync(
            connection,
            gameId,
            "SteamGamePublishers",
            "PublisherName",
            storeGame.Publishers,
            cancellationToken);
        await ReplaceStoreLabelsAsync(
            connection,
            gameId,
            "SteamGameGenres",
            "GenreName",
            storeGame.Genres,
            cancellationToken);
    }

    private static async Task ReplaceStoreLabelsAsync(
        SqliteConnection connection,
        long gameId,
        string tableName,
        string columnName,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken)
    {
        await using var deleteCommand = connection.CreateCommand();
        deleteCommand.CommandText = $"DELETE FROM {tableName} WHERE GameId = $gameId;";
        deleteCommand.Parameters.AddWithValue("$gameId", gameId);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

        foreach (var label in labels)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.CommandText = $"""
                INSERT OR IGNORE INTO {tableName} (GameId, {columnName})
                VALUES ($gameId, $label);
                """;
            insertCommand.Parameters.AddWithValue("$gameId", gameId);
            insertCommand.Parameters.AddWithValue("$label", label);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task SetLibraryStatusAsync(
        SqliteConnection connection,
        long userId,
        string status,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SteamAccounts
            SET LibraryCheckStatus = $status,
                LibraryCheckedAtUtc = CURRENT_TIMESTAMP,
                UpdatedAtUtc = CURRENT_TIMESTAMP
            WHERE UserId = $userId
                AND DataCollectionEnabled = 1;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$userId", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        long? userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (userId is not null)
        {
            command.Parameters.AddWithValue("$userId", userId.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? ReadNullableString(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? ReadNullableDateTimeOffset(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal));

    private sealed record SteamAccount(
        long UserId,
        long SteamId,
        bool DataCollectionEnabled);
}

public sealed record SteamSignInResult(
    long UserId,
    long SteamId,
    string? PersonaName,
    bool DataCollectionEnabled);

public sealed record SteamDashboard(
    long UserId,
    long SteamId,
    bool DataCollectionEnabled,
    string? PersonaName,
    string? ProfileUrl,
    string? AvatarUrl,
    DateTimeOffset? ProfileFetchedAtUtc,
    string LibraryCheckStatus,
    DateTimeOffset? LibraryCheckedAtUtc,
    DateTimeOffset? LibrarySyncedAtUtc,
    IReadOnlyList<SteamDashboardGame> MostPlayedGames);

public sealed record SteamDashboardGame(
    long GameId,
    int SteamAppId,
    string Name,
    int PlaytimeForeverMinutes,
    int? PlaytimeTwoWeeksMinutes,
    DateTimeOffset? LastPlayedAtUtc,
    double? MicroScore,
    double? MesoScore,
    double? MacroScore,
    bool IsPublic,
    bool NeedsAtlasIcon,
    bool HasAtlasIcon,
    string? LibraryCardUrl = null);
