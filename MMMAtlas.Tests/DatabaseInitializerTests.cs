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

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
