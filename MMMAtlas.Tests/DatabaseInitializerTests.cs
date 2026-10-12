using Microsoft.Data.Sqlite;
using MMMAtlas.Data;

namespace MMMAtlas.Tests;

[TestClass]
public sealed class DatabaseInitializerTests
{
    [TestMethod]
    public async Task InitializesAnEmptyDatabaseWithoutDevelopmentSeedData()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"mmm-atlas-initializer-{Guid.NewGuid():N}.db");

        try
        {
            var database = DatabaseConnection.Create(
                $"Data Source={databasePath};Foreign Keys=True",
                Directory.GetCurrentDirectory());

            await DatabaseInitializer.InitializeAsync(
                database,
                seedData: false);
            await DatabaseInitializer.InitializeAsync(
                database,
                seedData: false);

            await using var connection = new SqliteConnection(database.Value);
            await connection.OpenAsync();

            Assert.AreEqual(
                1L,
                await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_master "
                    + "WHERE type = 'table' AND name = 'SteamGames';"));
            Assert.AreEqual(
                0L,
                await ScalarAsync(connection, "SELECT COUNT(*) FROM Users;"));
            Assert.AreEqual(6L, await ScalarAsync(connection, """
                SELECT COUNT(*) FROM pragma_table_info('SteamGames') WHERE name IN
                    ('LibraryCardUrl', 'LibraryCardContent', 'LibraryCardContentType',
                     'LibraryCardHash', 'LibraryCardHashCheckedAtUtc', 'AtlasIconNeedsUpdate');
                """));
            Assert.AreEqual(2L, await ScalarAsync(connection, """
                SELECT COUNT(*) FROM pragma_table_info('SteamGames')
                WHERE name IN ('hasParentApp', 'isBeta') AND type='INTEGER' AND "notnull"=0;
                """));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[]
            {
                databasePath,
                databasePath + "-shm",
                databasePath + "-wal"
            })
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task MigratesExistingArtworkSchemaWithoutDiscardingLegacyCards()
    {
        var path = Path.Combine(Path.GetTempPath(), $"atlas-artwork-migration-{Guid.NewGuid():N}.db");
        var database = DatabaseConnection.Create($"Data Source={path}", Directory.GetCurrentDirectory());
        try
        {
            await DatabaseInitializer.InitializeAsync(database, false);
            await using (var connection = new SqliteConnection(database.Value))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO Games(GameId) VALUES(1);
                    INSERT INTO SteamGames(GameId, SteamAppId) VALUES(1,1234);
                    INSERT INTO GameAtlasIcons(GameId, ImageContent, DiameterPixels,
                        LibraryCardContentType, LibraryCardContent, LibraryCardWidthPixels, LibraryCardHeightPixels)
                    VALUES(1, X'01', 96, 'image/jpeg', X'0203', 300, 450);
                    ALTER TABLE SteamGames DROP COLUMN LibraryCardUrl;
                    ALTER TABLE SteamGames DROP COLUMN LibraryCardContent;
                    ALTER TABLE SteamGames DROP COLUMN LibraryCardContentType;
                    ALTER TABLE SteamGames DROP COLUMN LibraryCardHash;
                    ALTER TABLE SteamGames DROP COLUMN LibraryCardHashCheckedAtUtc;
                    ALTER TABLE SteamGames DROP COLUMN AtlasIconNeedsUpdate;
                    ALTER TABLE SteamGames DROP COLUMN hasParentApp;
                    ALTER TABLE SteamGames DROP COLUMN isBeta;
                    """;
                await command.ExecuteNonQueryAsync();
            }
            await DatabaseInitializer.InitializeAsync(database, false);
            await DatabaseInitializer.InitializeAsync(database, false);
            await using var verify = new SqliteConnection(database.Value);
            await verify.OpenAsync();
            Assert.AreEqual(1L, await ScalarAsync(verify, """
                SELECT COUNT(*) FROM SteamGames WHERE GameId=1 AND AtlasIconNeedsUpdate=0
                    AND LibraryCardHash IS NULL AND LibraryCardHashCheckedAtUtc IS NULL
                    AND hasParentApp IS NULL AND isBeta IS NULL;
                """));
            Assert.AreEqual(1L, await ScalarAsync(verify,
                "SELECT COUNT(*) FROM GameAtlasIcons WHERE LibraryCardContent=X'0203' AND ImageContent=X'01';"));
            await using var invalid = verify.CreateCommand();
            invalid.CommandText = "UPDATE SteamGames SET hasParentApp=2 WHERE GameId=1;";
            await Assert.ThrowsExactlyAsync<SqliteException>(() => invalid.ExecuteNonQueryAsync());
            invalid.CommandText = "UPDATE SteamGames SET isBeta=-1 WHERE GameId=1;";
            await Assert.ThrowsExactlyAsync<SqliteException>(() => invalid.ExecuteNonQueryAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
