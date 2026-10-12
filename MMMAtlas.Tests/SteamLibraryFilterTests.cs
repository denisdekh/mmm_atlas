using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMMAtlas.Steam;
using SteamKit2;

namespace MMMAtlas.Tests;

[TestClass]
public sealed class SteamLibraryFilterTests
{
    [TestMethod]
    [DataRow("Game", EAppType.Game)]
    [DataRow(" game ", EAppType.Game)]
    [DataRow("bEtA", EAppType.Beta)]
    [DataRow("DLC", EAppType.DLC)]
    [DataRow("Demo", EAppType.Demo)]
    [DataRow(null, EAppType.Invalid)]
    public void CommonTypeMapsToSteamAppType(string? type, EAppType expected) =>
        Assert.AreEqual(expected, SteamAppInfo.FromCommonType(type).AppType);

    [TestMethod]
    [DataRow(true, true, "1,2")]
    [DataRow(true, false, "1,2,4")]
    [DataRow(false, true, "1,2,3")]
    [DataRow(false, false, "1,2,3,4,5")]
    public async Task ReadsRetainUnknownAndFalseFlagsAndHonorReloadedSettings(
        bool requireGameType, bool excludeBeta, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        using var services = new ServiceCollection()
            .Configure<SteamLibraryFilterOptions>(configuration.GetSection("Steam:LibraryFilters"))
            .BuildServiceProvider();
        var filter = new SteamLibraryFilter(services.GetRequiredService<IOptionsMonitor<SteamLibraryFilterOptions>>());
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE SteamGames(GameId INTEGER, hasParentApp INTEGER NULL, isBeta INTEGER NULL);
            INSERT INTO SteamGames VALUES (1,NULL,NULL),(2,0,0),(3,1,NULL),(4,NULL,1),(5,1,1);
            """;
        await command.ExecuteNonQueryAsync();
        Assert.AreEqual("1,2", await ReadAsync());
        configuration["Steam:LibraryFilters:RequireGameType"] = requireGameType.ToString();
        configuration["Steam:LibraryFilters:ExcludeBeta"] = excludeBeta.ToString();
        configuration.Reload();
        Assert.AreEqual(expected, await ReadAsync());

        async Task<string> ReadAsync()
        {
            command.Parameters.Clear();
            command.CommandText = $"SELECT GameId FROM SteamGames AS steam WHERE {SteamLibraryFilter.VisibleSql} ORDER BY GameId;";
            SteamLibraryFilter.AddParameters(command, filter.Settings);
            var ids = new List<long>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) ids.Add(reader.GetInt64(0));
            return string.Join(',', ids);
        }
    }
}
