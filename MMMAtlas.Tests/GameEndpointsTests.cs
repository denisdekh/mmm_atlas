using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMMAtlas.Data;
using MMMAtlas.Steam;

namespace MMMAtlas.Tests;

[TestClass]
public sealed class GameEndpointsTests
{
    [TestMethod]
    public async Task GetVotesOnlyIdentifiesPublicVoters()
    {
        await using var factory = new AtlasApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/games/1/votes");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var responseJson = await response.Content.ReadAsStringAsync();
        var votes = JsonSerializer.Deserialize<VoteResponse[]>(
            responseJson,
            JsonSerializerOptions.Web);
        Assert.IsNotNull(votes);
        Assert.HasCount(2, votes);
        Assert.AreEqual(
            new VoteResponse(8, 6, 2, "01234567"),
            votes[0]);
        Assert.AreEqual(
            new VoteResponse(4, 5, 7, null),
            votes[1]);
        Assert.DoesNotContain("81985529216486895", responseJson);
    }

    [TestMethod]
    public async Task GetVotesReturnsProblemDetailsWhenGameDoesNotExist()
    {
        await using var factory = new AtlasApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/games/999/votes");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        Assert.IsNotNull(problem);
        Assert.AreEqual(404, problem.Status);
        Assert.AreEqual("Game not found", problem.Title);
    }

    [TestMethod]
    public async Task GetVotesSupportsBoundedPagination()
    {
        await using var factory = new AtlasApplicationFactory();
        using var client = factory.CreateClient();

        var response =
            await client.GetAsync("/api/games/1/votes?page=2&pageSize=1");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var votes = await response.Content.ReadFromJsonAsync<VoteResponse[]>();
        Assert.IsNotNull(votes);
        Assert.HasCount(1, votes);
        Assert.AreEqual(new VoteResponse(4, 5, 7, null), votes[0]);
    }

    [TestMethod]
    public async Task GetVotesRejectsPageSizesAboveTheMaximum()
    {
        await using var factory = new AtlasApplicationFactory();
        using var client = factory.CreateClient();

        var response =
            await client.GetAsync("/api/games/1/votes?pageSize=101");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task GetVotesReturnsEmptyCollectionForGameWithoutVotes()
    {
        await using var factory = new AtlasApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/games/2/votes");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var votes = await response.Content.ReadFromJsonAsync<VoteResponse[]>();
        Assert.IsNotNull(votes);
        Assert.IsEmpty(votes);
    }

    [TestMethod]
    public async Task GetGameReturnsDetailsAndAggregate()
    {
        await using var factory = new AtlasApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/games/1");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var game = await response.Content.ReadFromJsonAsync<GameResponse>();
        Assert.IsNotNull(game);
        Assert.AreEqual(1, game.GameId);
        Assert.AreEqual(1234, game.SteamAppId);
        Assert.AreEqual("Test Game", game.Name);
        Assert.AreEqual("Summary", game.Summary);
        Assert.AreEqual("/api/games/1/image", game.ImageUrl);
        Assert.AreEqual(2, game.VoteCount);
        Assert.AreEqual(0.375, game.MicroRatio!.Value, 0.000001);
        Assert.AreEqual(0.34375, game.MesoRatio!.Value, 0.000001);
        Assert.AreEqual(0.28125, game.MacroRatio!.Value, 0.000001);
    }

    [TestMethod]
    public async Task GetGameReturnsProblemDetailsWhenGameDoesNotExist()
    {
        await using var factory = new AtlasApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/games/999");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        Assert.IsNotNull(problem);
        Assert.AreEqual(404, problem.Status);
        Assert.AreEqual("Game not found", problem.Title);
    }

    [TestMethod]
    public async Task EraseSteamDataRequiresAuthentication()
    {
        await using var factory = new AtlasApplicationFactory();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

        var response = await client.PostAsync(
            "/api/me/steam-data/erase",
            content: null);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task SubmitVoteRequiresAuthentication()
    {
        await using var factory = new AtlasApplicationFactory();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

        var response = await client.PostAsync(
            "/api/me/votes/1",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["microScore"] = "5.0",
                ["mesoScore"] = "5.0",
                ["macroScore"] = "5.0"
            }));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task SubmitVoteAmendsTheUsersVote()
    {
        await using var factory = new AtlasApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", "10");

        var response = await client.PostAsync(
            "/api/me/votes/1",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["microScore"] = "9.1",
                ["mesoScore"] = "2.2",
                ["macroScore"] = "3.3"
            }));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var submitted =
            await response.Content.ReadFromJsonAsync<VoteSubmissionResponse>();
        Assert.IsNotNull(submitted);
        Assert.AreEqual(9.1m, submitted.MicroScore);
        Assert.AreEqual(2.2m, submitted.MesoScore);
        Assert.AreEqual(3.3m, submitted.MacroScore);
        Assert.IsTrue(submitted.Amended);

        var votes = await client.GetFromJsonAsync<VoteResponse[]>(
            "/api/games/1/votes");
        Assert.IsNotNull(votes);
        Assert.AreEqual(9.1, votes[0].MicroScore);
        Assert.AreEqual(2.2, votes[0].MesoScore);
        Assert.AreEqual(3.3, votes[0].MacroScore);
    }

    private sealed record VoteResponse(
        double MicroScore,
        double MesoScore,
        double MacroScore,
        string? PublicSteamId);

    private sealed record VoteSubmissionResponse(
        decimal MicroScore,
        decimal MesoScore,
        decimal MacroScore,
        bool Amended);

    private sealed record ProblemResponse(int Status, string Title);

    private sealed record GameResponse(
        long GameId,
        int SteamAppId,
        string Name,
        string Summary,
        string ImageUrl,
        int VoteCount,
        double? MicroRatio,
        double? MesoRatio,
        double? MacroRatio);

    private sealed class AtlasApplicationFactory :
        WebApplicationFactory<Program>
    {
        private readonly string _databasePath =
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            InitializeDatabase();
            builder.UseEnvironment("Production");
            builder.UseSetting("Steam:ApiKey", "integration-test-key");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DatabaseConnection>();
                services.AddSingleton(DatabaseConnection.Create(
                    $"Data Source={_databasePath};Foreign Keys=True",
                    Directory.GetCurrentDirectory()));
                services.RemoveAll<IAntiforgery>();
                services.AddSingleton<IAntiforgery, TestAntiforgery>();
                services
                    .AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme =
                            TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme =
                            TestAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<
                        AuthenticationSchemeOptions,
                        TestAuthenticationHandler>(
                        TestAuthenticationHandler.SchemeName,
                        _ => { });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            SqliteConnection.ClearAllPools();

            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }

        private void InitializeDatabase()
        {
            using var connection =
                new SqliteConnection($"Data Source={_databasePath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE DevelopmentGameInfo
                (
                    GameId INTEGER PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Summary TEXT NOT NULL,
                    ImageContentType TEXT NOT NULL,
                    ImageContent BLOB NOT NULL
                );

                CREATE TABLE Votes
                (
                    UserId INTEGER NOT NULL,
                    GameId INTEGER NOT NULL,
                    MicroScore REAL NOT NULL,
                    MesoScore REAL NOT NULL,
                    MacroScore REAL NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    PRIMARY KEY (UserId, GameId)
                );

                CREATE TABLE Users
                (
                    UserId INTEGER PRIMARY KEY,
                    ShowVotesPublicly INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE SteamAccounts
                (
                    UserId INTEGER PRIMARY KEY,
                    SteamId INTEGER NOT NULL UNIQUE
                );

                CREATE TABLE SteamGames
                (
                    GameId INTEGER PRIMARY KEY,
                    SteamAppId INTEGER NOT NULL
                );

                CREATE TABLE GameVoteAggregates
                (
                    GameId INTEGER PRIMARY KEY,
                    VoteCount INTEGER NOT NULL,
                    MicroRatio REAL NOT NULL,
                    MesoRatio REAL NOT NULL,
                    MacroRatio REAL NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );

                INSERT INTO DevelopmentGameInfo
                VALUES
                    (1, 'Test Game', 'Summary', 'image/png', X'00'),
                    (2, 'Game Without Votes', 'Summary', 'image/png', X'00');

                INSERT INTO SteamGames VALUES (1, 1234);

                INSERT INTO GameVoteAggregates
                VALUES
                    (1, 2, 0.375, 0.34375, 0.28125, CURRENT_TIMESTAMP);

                INSERT INTO Users VALUES
                    (10, 1),
                    (20, 0);

                INSERT INTO SteamAccounts VALUES
                    (10, 81985529216486895),
                    (20, 1147797409030816545);

                INSERT INTO Votes VALUES
                    (10, 1, 8, 6, 2, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                    (20, 1, 4, 5, 7, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                """;
            command.ExecuteNonQuery();
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(
            options,
            logger,
            encoder)
    {
        public const string SchemeName = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(
                "X-Test-UserId",
                out var userId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [new Claim(SteamClaims.UserId, userId.ToString())],
                SchemeName);
            var ticket = new AuthenticationTicket(
                new ClaimsPrincipal(identity),
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class TestAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) =>
            throw new NotSupportedException();

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) =>
            throw new NotSupportedException();

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) =>
            Task.FromResult(true);

        public void SetCookieTokenAndHeader(HttpContext httpContext) =>
            throw new NotSupportedException();

        public Task ValidateRequestAsync(HttpContext httpContext) =>
            Task.CompletedTask;
    }
}
