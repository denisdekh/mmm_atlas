using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.Sqlite;
using MMMAtlas.Configuration;
using MMMAtlas.Data;
using MMMAtlas.Steam;

namespace MMMAtlas.Api;

/// <summary>
/// Opt-in helpers used by the external API profiler. These routes are never
/// mapped in Production builds and still require a configured access token in
/// Development and Test builds.
/// </summary>
public static class ProfilerEndpoints
{
    public const string AccessTokenHeader = "X-MMM-Atlas-Profiler-Token";
    private static readonly SemaphoreSlim DatabaseSetupLock = new(1, 1);

    public static IEndpointRouteBuilder MapProfilerEndpoints(
        this IEndpointRouteBuilder endpoints,
        AtlasBuildPolicy buildPolicy,
        IConfiguration configuration)
    {
        if (!buildPolicy.TestFeaturesEnabled
            || !configuration.GetValue<bool>("ApiProfiler:Enabled"))
        {
            return endpoints;
        }

        var accessToken = configuration["ApiProfiler:AccessToken"];
        if (string.IsNullOrWhiteSpace(accessToken)
            || Encoding.UTF8.GetByteCount(accessToken) < 16)
        {
            throw new InvalidOperationException(
                "ApiProfiler:AccessToken must contain at least 16 UTF-8 bytes "
                + "when the API profiler helpers are enabled.");
        }

        var group = endpoints.MapGroup("/api/profiler")
            .WithTags("API profiler");
        group.MapPost("/database/prepare", PrepareDatabaseAsync);
        group.MapGet("/database/status", GetDatabaseStatusAsync);
        group.MapPost("/games", CreateGameAsync);
        group.MapPost("/session/{userId:long}", StartSessionAsync);
        group.MapGet("/verify", VerifyVoteAggregatesAsync);

        return endpoints;
    }

    private static async Task<IResult> PrepareDatabaseAsync(
        ProfilerDatabaseRequest request,
        HttpContext httpContext,
        IConfiguration configuration,
        DatabaseConnection database,
        CancellationToken cancellationToken)
    {
        if (!HasValidAccessToken(httpContext.Request, configuration))
        {
            return Results.Unauthorized();
        }

        var mode = request.Mode.Trim().ToLowerInvariant();
        var reset = mode is "empty" or "seed" or "grow";
        if (!reset && mode != "grow")
        {
            return Results.BadRequest(new
            {
                error = "Mode must be 'empty', 'seed', or 'grow'."
            });
        }

        var targets = mode == "empty"
            ? new ProfilerSeedTargets(0, 0, 0, request.RandomSeed)
            : new ProfilerSeedTargets(
                request.Games,
                request.Users,
                request.Votes,
                request.RandomSeed);

        await DatabaseSetupLock.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await ProfilerDatabaseSeeder.PrepareAsync(
                database,
                reset,
                targets,
                cancellationToken);
            return Results.Ok(snapshot);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        finally
        {
            DatabaseSetupLock.Release();
        }
    }

    private static async Task<IResult> GetDatabaseStatusAsync(
        HttpContext httpContext,
        IConfiguration configuration,
        DatabaseConnection database,
        CancellationToken cancellationToken)
    {
        if (!HasValidAccessToken(httpContext.Request, configuration))
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await ProfilerDatabaseSeeder.ReadSnapshotAsync(
            database,
            cancellationToken));
    }

    private static async Task<IResult> CreateGameAsync(
        ProfilerGameRequest request,
        HttpContext httpContext,
        IConfiguration configuration,
        DatabaseConnection database,
        CancellationToken cancellationToken)
    {
        if (!HasValidAccessToken(httpContext.Request, configuration))
        {
            return Results.Unauthorized();
        }

        try
        {
            return Results.Ok(await ProfilerDatabaseSeeder.CreateNextGameAsync(
                database,
                request.TargetGames,
                request.UserId,
                request.RandomSeed,
                cancellationToken));
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> StartSessionAsync(
        long userId,
        HttpContext httpContext,
        IConfiguration configuration,
        DatabaseConnection database,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (!HasValidAccessToken(httpContext.Request, configuration))
        {
            return Results.Unauthorized();
        }

        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM Users WHERE UserId = $userId);";
        command.Parameters.AddWithValue("$userId", userId);
        var exists = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken)) == 1;
        if (!exists)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Profiler user not found",
                detail: "Seed or sign in the requested user before profiling writes.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, $"Profiler user {userId}"),
                new Claim(SteamClaims.UserId, userId.ToString())
            ],
            AuthenticationSchemes.ApplicationCookie,
            ClaimTypes.Name,
            ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        await httpContext.SignInAsync(
            AuthenticationSchemes.ApplicationCookie,
            principal);
        // SignInAsync writes the cookie but does not replace the principal for
        // the current request. Antiforgery binds its token to User, so make the
        // newly signed-in identity visible before issuing that token.
        httpContext.User = principal;

        var tokens = antiforgery.GetAndStoreTokens(httpContext);
        return Results.Ok(new ProfilerSessionResponse(
            tokens.HeaderName ?? "RequestVerificationToken",
            tokens.RequestToken
                ?? throw new InvalidOperationException(
                    "Antiforgery did not issue a request token.")));
    }

    private static async Task<IResult> VerifyVoteAggregatesAsync(
        HttpContext httpContext,
        IConfiguration configuration,
        DatabaseConnection database,
        CancellationToken cancellationToken)
    {
        if (!HasValidAccessToken(httpContext.Request, configuration))
        {
            return Results.Unauthorized();
        }

        const string sql = """
            WITH game_ids AS
            (
                SELECT GameId FROM Votes
                UNION
                SELECT GameId FROM GameVoteAggregates
            ),
            computed AS
            (
                SELECT
                    GameId,
                    COUNT(*) AS VoteCount,
                    AVG(MicroScore / (MicroScore + MesoScore + MacroScore)) AS MicroRatio,
                    AVG(MesoScore / (MicroScore + MesoScore + MacroScore)) AS MesoRatio,
                    AVG(MacroScore / (MicroScore + MesoScore + MacroScore)) AS MacroRatio
                FROM Votes
                GROUP BY GameId
            )
            SELECT
                ids.GameId,
                aggregate.VoteCount,
                computed.VoteCount,
                aggregate.MicroRatio,
                computed.MicroRatio,
                aggregate.MesoRatio,
                computed.MesoRatio,
                aggregate.MacroRatio,
                computed.MacroRatio
            FROM game_ids AS ids
            LEFT JOIN GameVoteAggregates AS aggregate
                ON aggregate.GameId = ids.GameId
            LEFT JOIN computed
                ON computed.GameId = ids.GameId
            ORDER BY ids.GameId;
            """;

        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var checkedGames = 0;
        var inconsistencies = new List<AggregateInconsistency>();
        while (await reader.ReadAsync(cancellationToken))
        {
            checkedGames++;
            int? aggregateCount = reader.IsDBNull(1) ? null : reader.GetInt32(1);
            int? computedCount = reader.IsDBNull(2) ? null : reader.GetInt32(2);
            var maximumRatioDelta = MaximumDelta(reader, 3, 4, 5, 6, 7, 8);
            if (aggregateCount != computedCount || maximumRatioDelta is null or > 0.000000001)
            {
                inconsistencies.Add(new AggregateInconsistency(
                    reader.GetInt64(0),
                    aggregateCount,
                    computedCount,
                    maximumRatioDelta));
            }
        }

        return Results.Ok(new AggregateVerificationResponse(
            checkedGames,
            inconsistencies.Count,
            inconsistencies.Take(20).ToArray()));
    }

    private static double? MaximumDelta(
        SqliteDataReader reader,
        params int[] ordinals)
    {
        double maximum = 0;
        for (var index = 0; index < ordinals.Length; index += 2)
        {
            if (reader.IsDBNull(ordinals[index])
                || reader.IsDBNull(ordinals[index + 1]))
            {
                return null;
            }

            maximum = Math.Max(
                maximum,
                Math.Abs(
                    reader.GetDouble(ordinals[index])
                    - reader.GetDouble(ordinals[index + 1])));
        }

        return maximum;
    }

    private static bool HasValidAccessToken(
        HttpRequest request,
        IConfiguration configuration)
    {
        var expected = configuration["ApiProfiler:AccessToken"] ?? string.Empty;
        var actual = request.Headers[AccessTokenHeader].ToString();
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private sealed record ProfilerSessionResponse(
        string AntiforgeryHeaderName,
        string AntiforgeryRequestToken);

    private sealed record ProfilerDatabaseRequest(
        string Mode,
        int Games,
        int Users,
        int Votes,
        int RandomSeed);

    private sealed record ProfilerGameRequest(
        int TargetGames,
        long UserId,
        int RandomSeed);

    private sealed record AggregateVerificationResponse(
        int CheckedGames,
        int InconsistentGames,
        IReadOnlyList<AggregateInconsistency> Inconsistencies);

    private sealed record AggregateInconsistency(
        long GameId,
        int? AggregateVoteCount,
        int? ActualVoteCount,
        double? MaximumRatioDelta);
}
