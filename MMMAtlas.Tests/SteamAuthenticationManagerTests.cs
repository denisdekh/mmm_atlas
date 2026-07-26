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
    public async Task ErasureRequiresExplicitOptInBeforeDataIsCollectedAgain()
    {
        var manager = CreateManager();

        var firstSignIn = await manager.CompleteSignInAsync(
            76561198012345678,
            collectionConsent: true,
            CancellationToken.None);

        var dashboard = await manager.GetDashboardAsync(
            firstSignIn.UserId,
            CancellationToken.None);

        Assert.IsNotNull(dashboard);
        Assert.IsTrue(dashboard.DataCollectionEnabled);
        Assert.AreEqual("Atlas Tester", dashboard.PersonaName);
        Assert.HasCount(2, dashboard.MostPlayedGames);
        Assert.AreEqual("Long Game", dashboard.MostPlayedGames[0].Name);
        Assert.AreEqual(
            9000,
            dashboard.MostPlayedGames[0].PlaytimeForeverMinutes);

        AddVote(firstSignIn.UserId);

        var votedDashboard = await manager.GetDashboardAsync(
            firstSignIn.UserId,
            CancellationToken.None);
        Assert.IsNotNull(votedDashboard);
        Assert.AreEqual(8d, votedDashboard.MostPlayedGames[0].MicroScore);
        Assert.AreEqual(6d, votedDashboard.MostPlayedGames[0].MesoScore);
        Assert.AreEqual(2d, votedDashboard.MostPlayedGames[0].MacroScore);

        await manager.EraseStoredDataAsync(
            firstSignIn.UserId,
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
            collectionConsent: false,
            CancellationToken.None);

        var stillDisabled = await manager.GetDashboardAsync(
            firstSignIn.UserId,
            CancellationToken.None);
        Assert.IsNotNull(stillDisabled);
        Assert.IsFalse(stillDisabled.DataCollectionEnabled);
        Assert.IsNull(stillDisabled.PersonaName);

        await manager.CompleteSignInAsync(
            76561198012345678,
            collectionConsent: true,
            CancellationToken.None);

        var optedBackIn = await manager.GetDashboardAsync(
            firstSignIn.UserId,
            CancellationToken.None);
        Assert.IsNotNull(optedBackIn);
        Assert.IsTrue(optedBackIn.DataCollectionEnabled);
        Assert.AreEqual("Atlas Tester", optedBackIn.PersonaName);
        Assert.HasCount(2, optedBackIn.MostPlayedGames);

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

    private SteamAuthenticationManager CreateManager()
    {
        var handler = new SteamApiMessageHandler();
        var httpClient = new HttpClient(handler)
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

        return new SteamAuthenticationManager(database, apiClient);
    }

    private sealed class SteamApiMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
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
