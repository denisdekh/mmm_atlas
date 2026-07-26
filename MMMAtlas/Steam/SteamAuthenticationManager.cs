using Microsoft.Data.Sqlite;
using MMMAtlas.Data;

namespace MMMAtlas.Steam;

public sealed class SteamAuthenticationManager(
    DatabaseConnection database,
    SteamApiClient steamApiClient)
{
    public async Task<SteamSignInResult> CompleteSignInAsync(
        long steamId,
        bool collectionConsent,
        CancellationToken cancellationToken)
    {
        var account = await GetOrCreateAccountAsync(
            steamId,
            collectionConsent,
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

        return new SteamSignInResult(
            account.UserId,
            steamId,
            dashboard?.PersonaName,
            account.DataCollectionEnabled);
    }

    public async Task RefreshStoredDataAsync(
        long userId,
        CancellationToken cancellationToken)
    {
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
        await SaveFetchedDataAsync(
            userId,
            await profileTask,
            await libraryTask,
            cancellationToken);
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
                steam.IconUrl,
                library.PlaytimeForeverMinutes,
                library.PlaytimeTwoWeeksMinutes,
                library.LastPlayedAtUtc,
                vote.MicroScore,
                vote.MesoScore,
                vote.MacroScore
            FROM SteamLibraryGames AS library
            INNER JOIN SteamGames AS steam
                ON steam.GameId = library.GameId
            LEFT JOIN DevelopmentGameInfo AS development
                ON development.GameId = library.GameId
            LEFT JOIN Votes AS vote
                ON vote.UserId = library.UserId
                AND vote.GameId = library.GameId
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
                ReadNullableString(gamesReader, 3),
                gamesReader.GetInt32(4),
                gamesReader.IsDBNull(5) ? null : gamesReader.GetInt32(5),
                ReadNullableDateTimeOffset(gamesReader, 6),
                gamesReader.IsDBNull(7) ? null : gamesReader.GetDouble(7),
                gamesReader.IsDBNull(8) ? null : gamesReader.GetDouble(8),
                gamesReader.IsDBNull(9) ? null : gamesReader.GetDouble(9)));
        }

        return dashboard with { MostPlayedGames = games };
    }

    private async Task<SteamAccount> GetOrCreateAccountAsync(
        long steamId,
        bool collectionConsent,
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

                if (collectionConsent && !enabled)
                {
                    await ExecuteAsync(
                        connection,
                        """
                        UPDATE SteamAccounts
                        SET DataCollectionEnabled = 1,
                            UpdatedAtUtc = CURRENT_TIMESTAMP
                        WHERE UserId = $userId;
                        """,
                        userId,
                        cancellationToken);
                    enabled = true;
                }

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
                SteamId,
                DataCollectionEnabled
            )
            VALUES
            (
                $userId,
                $steamId,
                $dataCollectionEnabled
            );
            """;
        createAccountCommand.Parameters.AddWithValue("$userId", newUserId);
        createAccountCommand.Parameters.AddWithValue("$steamId", steamId);
        createAccountCommand.Parameters.AddWithValue(
            "$dataCollectionEnabled",
            collectionConsent ? 1 : 0);
        await createAccountCommand.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new SteamAccount(newUserId, steamId, collectionConsent);
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

    private static async Task SaveAvailableLibraryAsync(
        SqliteConnection connection,
        long userId,
        IReadOnlyList<SteamOwnedGame> games,
        CancellationToken cancellationToken)
    {
        var verifiedAtUtc = DateTimeOffset.UtcNow.ToString("O");

        foreach (var game in games)
        {
            var gameId = await GetOrCreateGameAsync(
                connection,
                game,
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
                    IconUrl = COALESCE($iconUrl, IconUrl),
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
                "$iconUrl",
                (object?)game.IconUrl ?? DBNull.Value);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);

            return Convert.ToInt64(existingGameId);
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
            INSERT INTO SteamGames (GameId, SteamAppId, Name, IconUrl)
            VALUES ($gameId, $steamAppId, $name, $iconUrl);
            """;
        createSteamGameCommand.Parameters.AddWithValue("$gameId", gameId);
        createSteamGameCommand.Parameters.AddWithValue("$steamAppId", game.AppId);
        createSteamGameCommand.Parameters.AddWithValue(
            "$name",
            (object?)game.Name ?? DBNull.Value);
        createSteamGameCommand.Parameters.AddWithValue(
            "$iconUrl",
            (object?)game.IconUrl ?? DBNull.Value);
        await createSteamGameCommand.ExecuteNonQueryAsync(cancellationToken);

        return gameId;
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
    string? IconUrl,
    int PlaytimeForeverMinutes,
    int? PlaytimeTwoWeeksMinutes,
    DateTimeOffset? LastPlayedAtUtc,
    double? MicroScore,
    double? MesoScore,
    double? MacroScore);
