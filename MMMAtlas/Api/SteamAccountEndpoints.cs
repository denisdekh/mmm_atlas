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
        endpoints.MapGet("/api/me/games/search", SearchOwnedGamesAsync)
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

    private static async Task<IResult> SearchOwnedGamesAsync(
        string? q,
        HttpContext httpContext,
        DatabaseConnection database,
        CancellationToken cancellationToken)
    {
        var query = q?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["q"] = ["Enter a game name to search your Steam library."]
            });
        }

        if (query.Length > 100)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["q"] = ["The game search must be 100 characters or fewer."]
            });
        }

        const string sql = """
            WITH OwnedLibrary AS
            (
                SELECT
                    steam.GameId,
                    steam.SteamAppId,
                    COALESCE(
                        steam.Name,
                        development.Name,
                        'Steam app ' || steam.SteamAppId) AS Name,
                    library.PlaytimeForeverMinutes,
                    CASE WHEN vote.GameId IS NULL THEN 0 ELSE 1 END AS HasVoted,
                    vote.MicroScore,
                    vote.MesoScore,
                    vote.MacroScore,
                    COALESCE(vote.IsPublic, 0) AS IsPublic,
                    CASE WHEN aggregate.GameId IS NULL THEN 1 ELSE 0 END AS NeedsAtlasIcon,
                    CASE WHEN atlas.GameId IS NULL THEN 0 ELSE 1 END AS HasAtlasIcon
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
            )
            SELECT
                GameId,
                SteamAppId,
                Name,
                PlaytimeForeverMinutes,
                HasVoted,
                MicroScore,
                MesoScore,
                MacroScore,
                IsPublic,
                NeedsAtlasIcon,
                HasAtlasIcon
            FROM OwnedLibrary
            WHERE instr(lower(Name), lower($query)) > 0
            ORDER BY
                CASE
                    WHEN lower(Name) = lower($query) THEN 0
                    WHEN lower(Name) LIKE lower($query) || '%' THEN 1
                    ELSE 2
                END,
                Name COLLATE NOCASE,
                SteamAppId;
            """;

        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(
            "$userId",
            GetUserId(httpContext.User));
        command.Parameters.AddWithValue("$query", query);

        var games = new List<LibrarySearchGameDto>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            games.Add(new LibrarySearchGameDto(
                GameId: reader.GetInt64(0),
                SteamAppId: reader.GetInt32(1),
                Name: reader.GetString(2),
                PlaytimeForeverMinutes: reader.GetInt32(3),
                HasVoted: reader.GetBoolean(4),
                MicroScore: reader.IsDBNull(5) ? null : reader.GetDouble(5),
                MesoScore: reader.IsDBNull(6) ? null : reader.GetDouble(6),
                MacroScore: reader.IsDBNull(7) ? null : reader.GetDouble(7),
                IsPublic: reader.GetBoolean(8),
                NeedsAtlasIcon: reader.GetBoolean(9),
                HasAtlasIcon: reader.GetBoolean(10)));
        }

        httpContext.Response.Headers.CacheControl = "private, no-store";
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

    private sealed record LibrarySearchGameDto(
        long GameId,
        int SteamAppId,
        string Name,
        int PlaytimeForeverMinutes,
        bool HasVoted,
        double? MicroScore,
        double? MesoScore,
        double? MacroScore,
        bool IsPublic,
        bool NeedsAtlasIcon,
        bool HasAtlasIcon);
}
