using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Data.Sqlite;
using MMMAtlas.Data;
using MMMAtlas.Steam;

namespace MMMAtlas.Api;

public static class SteamAccountEndpoints
{
    public static IEndpointRouteBuilder MapSteamAccountEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var steamData = endpoints.MapGroup("/api/me/steam-data")
            .RequireAuthorization()
            .WithTags("Steam account");

        steamData.MapPost("/erase", EraseStoredDataAsync);
        steamData.MapPost("/consent", SetConsentAsync);

        endpoints.MapGet("/api/me/games", GetOwnedGamesAsync)
            .RequireAuthorization()
            .WithTags("Steam account");

        return endpoints;
    }

    private static async Task<IResult> GetOwnedGamesAsync(
        HttpContext httpContext,
        DatabaseConnection database,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                library.GameId,
                CASE WHEN vote.GameId IS NULL THEN 0 ELSE 1 END,
                vote.MicroScore,
                vote.MesoScore,
                vote.MacroScore,
                COALESCE(vote.IsPublic, 0)
            FROM SteamLibraryGames AS library
            LEFT JOIN Votes AS vote
                ON vote.UserId = library.UserId
                AND vote.GameId = library.GameId
            WHERE library.UserId = $userId
            ORDER BY library.GameId;
            """;

        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DataCollectionEnabled
            FROM SteamAccounts
            WHERE UserId = $userId;
            """;
        command.Parameters.AddWithValue(
            "$userId",
            GetUserId(httpContext.User));

        var dataCollectionEnabled = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken)) == 1;

        command.CommandText = sql;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        var games = new List<OwnedGameDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            games.Add(new OwnedGameDto(
                GameId: reader.GetInt64(0),
                HasVoted: reader.GetBoolean(1),
                MicroScore: reader.IsDBNull(2) ? null : reader.GetDouble(2),
                MesoScore: reader.IsDBNull(3) ? null : reader.GetDouble(3),
                MacroScore: reader.IsDBNull(4) ? null : reader.GetDouble(4),
                IsPublic: reader.GetBoolean(5)));
        }

        httpContext.Response.Headers.CacheControl = "private, no-store";
        httpContext.Response.Headers["X-Data-Collection-Enabled"] =
            dataCollectionEnabled.ToString().ToLowerInvariant();
        return Results.Ok(games);
    }

    private static async Task<IResult> EraseStoredDataAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        SteamAuthenticationManager manager,
        CancellationToken cancellationToken)
    {
        await antiforgery.ValidateRequestAsync(httpContext);
        await manager.EraseStoredDataAsync(
            GetUserId(httpContext.User),
            cancellationToken);

        return WantsHtml(httpContext.Request)
            ? Results.Redirect("/dashboard?notice=erased")
            : Results.NoContent();
    }

    private static async Task<IResult> SetConsentAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        SteamAuthenticationManager manager,
        CancellationToken cancellationToken)
    {
        await antiforgery.ValidateRequestAsync(httpContext);
        var form = await httpContext.Request.ReadFormAsync(cancellationToken);
        var enabled = form["enabled"].Any(value =>
            string.Equals(value, bool.TrueString, StringComparison.OrdinalIgnoreCase));

        await manager.SetDataCollectionEnabledAsync(
            GetUserId(httpContext.User),
            enabled,
            cancellationToken);

        return WantsHtml(httpContext.Request)
            ? Results.Redirect(enabled
                ? "/dashboard?notice=collection-enabled"
                : "/dashboard?notice=erased")
            : Results.NoContent();
    }

    private static long GetUserId(ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(SteamClaims.UserId);
        return long.TryParse(value, out var userId)
            ? userId
            : throw new InvalidOperationException(
                "The authentication cookie does not contain a valid user ID.");
    }

    private static bool WantsHtml(HttpRequest request) =>
        request.HasFormContentType
        || request.GetTypedHeaders().Accept?.Any(
            value => string.Equals(
                value.MediaType.Value,
                "text/html",
                StringComparison.OrdinalIgnoreCase)) == true;

    private sealed record OwnedGameDto(
        long GameId,
        bool HasVoted,
        double? MicroScore,
        double? MesoScore,
        double? MacroScore,
        bool IsPublic);
}
