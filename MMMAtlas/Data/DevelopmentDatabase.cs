using System.Reflection;
using Microsoft.Data.Sqlite;

namespace MMMAtlas.Data;

public static class DevelopmentDatabase
{
    private const string SchemaResourceName =
        "MMMAtlas.Data.SQLiteTableSchemas.sql";

    private const string SeedResourceName =
        "MMMAtlas.Data.DevelopmentSeed.sql";

    public static async Task InitializeAsync(
        DatabaseConnection database,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);

        await ExecuteResourceAsync(
            connection,
            SchemaResourceName,
            cancellationToken);

        await EnsureUserVotePrivacyColumnAsync(
            connection,
            cancellationToken);

        await EnsureSteamDataColumnsAsync(
            connection,
            cancellationToken);

        await ExecuteResourceAsync(
            connection,
            SeedResourceName,
            cancellationToken);
    }

    private static async Task ExecuteResourceAsync(
        SqliteConnection connection,
        string resourceName,
        CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded SQL resource '{resourceName}' was not found.");

        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureUserVotePrivacyColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('Users')
            WHERE name = 'ShowVotesPublicly';
            """;

        var columnExists = Convert.ToInt64(
            await checkCommand.ExecuteScalarAsync(cancellationToken)) > 0;

        if (columnExists)
        {
            return;
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = """
            ALTER TABLE Users
            ADD COLUMN ShowVotesPublicly INTEGER NOT NULL DEFAULT 0
                CHECK (ShowVotesPublicly IN (0, 1));
            """;
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSteamDataColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(
            connection,
            "SteamAccounts",
            "DataCollectionEnabled",
            "INTEGER NOT NULL DEFAULT 0 CHECK (DataCollectionEnabled IN (0, 1))",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "SteamAccounts",
            "PersonaName",
            "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "SteamAccounts",
            "ProfileUrl",
            "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "SteamAccounts",
            "AvatarUrl",
            "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "SteamAccounts",
            "ProfileFetchedAtUtc",
            "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "SteamGames",
            "Name",
            "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "SteamGames",
            "IconUrl",
            "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "SteamLibraryGames",
            "PlaytimeForeverMinutes",
            "INTEGER NOT NULL DEFAULT 0 CHECK (PlaytimeForeverMinutes >= 0)",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "SteamLibraryGames",
            "PlaytimeTwoWeeksMinutes",
            "INTEGER NULL CHECK (PlaytimeTwoWeeksMinutes IS NULL OR PlaytimeTwoWeeksMinutes >= 0)",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "SteamLibraryGames",
            "LastPlayedAtUtc",
            "TEXT NULL",
            cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = $"""
            SELECT COUNT(*)
            FROM pragma_table_info('{table}')
            WHERE name = $column;
            """;
        checkCommand.Parameters.AddWithValue("$column", column);

        var columnExists = Convert.ToInt64(
            await checkCommand.ExecuteScalarAsync(cancellationToken)) > 0;

        if (columnExists)
        {
            return;
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText =
            $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
