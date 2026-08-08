using Microsoft.Data.Sqlite;
using MMMAtlas.Data;

namespace MMMAtlas.Api;

public static class GameEndpoints
{
    public static IEndpointRouteBuilder MapGameEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var games = endpoints.MapGroup("/api/games")
            .WithTags("Games");

        games.MapGet("", GetGamesAsync);
        games.MapGet("/{gameId:long}", GetGameAsync);
        games.MapGet("/{gameId:long}/image", GetGameImageAsync)
            .WithName("GetGameImage");
        games.MapGet("/{gameId:long}/atlas-icon", GetAtlasIconAsync)
            .WithName("GetAtlasIcon");
        games.MapGet("/{gameId:long}/library-card", GetLibraryCardAsync)
            .WithName("GetLibraryCard");
        games.MapGet("/{gameId:long}/votes", GetGameVotesAsync);

        return endpoints;
    }

    private static async Task<IResult> GetGamesAsync(
        HttpContext httpContext,
        DatabaseConnection database,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                steam.GameId,
                steam.SteamAppId,
                COALESCE(
                    steam.Name,
                    info.Name,
                    'Steam app ' || steam.SteamAppId
                ),
                COALESCE(
                    steam.Description,
                    info.Summary,
                    'Community ratings for '
                        || COALESCE(
                            steam.Name,
                            'Steam app ' || steam.SteamAppId
                        )
                        || '.'
                ),
                steam.ReleaseYear,
                steam.IconUrl,
                CASE WHEN atlas.ImageContent IS NULL THEN 0 ELSE 1 END,
                CASE WHEN info.ImageContent IS NULL THEN 0 ELSE 1 END,
                aggregate.VoteCount,
                aggregate.MicroRatio,
                aggregate.MesoRatio,
                aggregate.MacroRatio
            FROM SteamGames AS steam
            INNER JOIN GameVoteAggregates AS aggregate
                ON aggregate.GameId = steam.GameId
            LEFT JOIN DevelopmentGameInfo AS info
                ON info.GameId = steam.GameId
            LEFT JOIN GameAtlasIcons AS atlas
                ON atlas.GameId = steam.GameId
            ORDER BY
                COALESCE(
                    steam.Name,
                    info.Name,
                    'Steam app ' || steam.SteamAppId
                );
            """;

        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<GameDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadGame(reader, httpContext.Request));
        }

        return Results.Ok(results);
    }

    private static async Task<IResult> GetGameAsync(
        long gameId,
        HttpContext httpContext,
        DatabaseConnection database,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                steam.GameId,
                steam.SteamAppId,
                COALESCE(
                    steam.Name,
                    info.Name,
                    'Steam app ' || steam.SteamAppId
                ),
                COALESCE(
                    steam.Description,
                    info.Summary,
                    'Community ratings for '
                        || COALESCE(
                            steam.Name,
                            'Steam app ' || steam.SteamAppId
                        )
                        || '.'
                ),
                steam.ReleaseYear,
                steam.IconUrl,
                CASE WHEN atlas.ImageContent IS NULL THEN 0 ELSE 1 END,
                CASE WHEN info.ImageContent IS NULL THEN 0 ELSE 1 END,
                aggregate.VoteCount,
                aggregate.MicroRatio,
                aggregate.MesoRatio,
                aggregate.MacroRatio
            FROM SteamGames AS steam
            INNER JOIN GameVoteAggregates AS aggregate
                ON aggregate.GameId = steam.GameId
            LEFT JOIN DevelopmentGameInfo AS info
                ON info.GameId = steam.GameId
            LEFT JOIN GameAtlasIcons AS atlas
                ON atlas.GameId = steam.GameId
            WHERE steam.GameId = $gameId;
            """;

        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS
            (
                SELECT 1
                FROM SteamGames
                WHERE GameId = $gameId
            );
            """;
        command.Parameters.AddWithValue("$gameId", gameId);

        var gameExists = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken)) == 1;

        if (!gameExists)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Game not found");
        }

        command.CommandText = sql;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Game not found");
        }

        return Results.Ok(ReadGame(reader, httpContext.Request));
    }

    private static async Task<IResult> GetGameImageAsync(
        long gameId,
        HttpContext httpContext,
        DatabaseConnection database,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ImageContentType, ImageContent
            FROM DevelopmentGameInfo
            WHERE GameId = $gameId;
            """;

        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$gameId", gameId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return Results.NotFound();
        }

        var contentType = reader.GetString(0);
        var content = (byte[])reader.GetValue(1);

        httpContext.Response.Headers.CacheControl =
            "public, max-age=3600";

        return Results.File(content, contentType);
    }

    private static async Task<IResult> GetAtlasIconAsync(
        long gameId,
        HttpContext httpContext,
        DatabaseConnection database,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ImageContentType, ImageContent
            FROM GameAtlasIcons
            WHERE GameId = $gameId;
            """;

        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$gameId", gameId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return Results.NotFound();
        }

        httpContext.Response.Headers.CacheControl =
            "public, max-age=86400";
        return Results.File(
            (byte[])reader.GetValue(1),
            reader.GetString(0));
    }

    private static async Task<IResult> GetLibraryCardAsync(
        long gameId,
        HttpContext httpContext,
        DatabaseConnection database,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                steam.SteamAppId,
                atlas.LibraryCardContentType,
                atlas.LibraryCardContent
            FROM SteamGames AS steam
            LEFT JOIN GameAtlasIcons AS atlas
                ON atlas.GameId = steam.GameId
            WHERE steam.GameId = $gameId;
            """;
        command.Parameters.AddWithValue("$gameId", gameId);

        int appId;
        await using (var reader =
            await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return Results.NotFound();
            }

            appId = reader.GetInt32(0);
            if (!reader.IsDBNull(2))
            {
                httpContext.Response.Headers.CacheControl =
                    "public, max-age=86400";
                return Results.File(
                    (byte[])reader.GetValue(2),
                    reader.IsDBNull(1)
                        ? "image/jpeg"
                        : reader.GetString(1));
            }
        }

        var client = httpClientFactory.CreateClient("SteamLibraryImages");
        using var response = await client.GetAsync(
            $"steam/apps/{appId}/library_600x900.jpg",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Steam library card unavailable");
        }

        const int maximumLibraryCardBytes = 5 * 1024 * 1024;
        if (response.Content.Headers.ContentLength
            is > maximumLibraryCardBytes)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Steam library card is too large");
        }

        var content = await response.Content.ReadAsByteArrayAsync(
            cancellationToken);
        if (content.Length > maximumLibraryCardBytes)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Steam library card is too large");
        }

        httpContext.Response.Headers.CacheControl =
            "public, max-age=86400";
        return Results.File(
            content,
            response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }

    private static async Task<IResult> GetGameVotesAsync(
        long gameId,
        HttpContext httpContext,
        DatabaseConnection database,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 50,
        bool publicOnly = false)
    {
        if (page < 1 || pageSize is < 1 or > 100)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid pagination",
                detail: "Page must be positive and pageSize must be between 1 and 100.");
        }

        const string gameExistsSql = """
            SELECT EXISTS
            (
                SELECT 1
                FROM SteamGames
                WHERE GameId = $gameId
            );
            """;

        const string sql = """
            SELECT
                vote.MicroScore,
                vote.MesoScore,
                vote.MacroScore,
                CASE
                    WHEN vote.IsPublic = 1
                        AND account.PersonaName IS NOT NULL
                    THEN account.PersonaName
                    ELSE NULL
                END AS PublicProfileName
            FROM Votes AS vote
            LEFT JOIN SteamAccounts AS account
                ON account.UserId = vote.UserId
            WHERE vote.GameId = $gameId
                AND
                (
                    $publicOnly = 0
                    OR
                    (
                        vote.IsPublic = 1
                        AND account.PersonaName IS NOT NULL
                    )
                )
            ORDER BY vote.UserId
            LIMIT $pageSize
            OFFSET $offset;
            """;

        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);

        await using (var existsCommand = connection.CreateCommand())
        {
            existsCommand.CommandText = gameExistsSql;
            existsCommand.Parameters.AddWithValue("$gameId", gameId);

            var exists = (long)(await existsCommand.ExecuteScalarAsync(
                cancellationToken) ?? 0L);

            if (exists == 0)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Game not found");
            }
        }

        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = """
                SELECT COUNT(*)
                FROM Votes AS vote
                LEFT JOIN SteamAccounts AS account
                    ON account.UserId = vote.UserId
                WHERE vote.GameId = $gameId
                    AND
                    (
                        $publicOnly = 0
                        OR
                        (
                            vote.IsPublic = 1
                            AND account.PersonaName IS NOT NULL
                        )
                    );
                """;
            countCommand.Parameters.AddWithValue("$gameId", gameId);
            countCommand.Parameters.AddWithValue(
                "$publicOnly",
                publicOnly ? 1 : 0);
            var totalCount = Convert.ToInt32(
                await countCommand.ExecuteScalarAsync(cancellationToken));
            httpContext.Response.Headers["X-Total-Count"] =
                totalCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$gameId", gameId);
        command.Parameters.AddWithValue("$pageSize", pageSize);
        command.Parameters.AddWithValue(
            "$offset",
            (long)(page - 1) * pageSize);
        command.Parameters.AddWithValue("$publicOnly", publicOnly ? 1 : 0);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<VoteDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new VoteDto(
                MicroScore: reader.GetDouble(0),
                MesoScore: reader.GetDouble(1),
                MacroScore: reader.GetDouble(2),
                PublicProfileName:
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return Results.Ok(results);
    }

    private static GameDto ReadGame(
        SqliteDataReader reader,
        HttpRequest request)
    {
        var gameId = reader.GetInt64(0);
        var storedImageUrl =
            reader.IsDBNull(5) ? null : reader.GetString(5);
        var hasAtlasIcon = reader.GetBoolean(6);
        var hasDevelopmentImage = reader.GetBoolean(7);
        var imageUrl = hasAtlasIcon
            ? $"{request.PathBase}/api/games/{gameId}/atlas-icon"
            : storedImageUrl
                ?? (hasDevelopmentImage
                    ? $"{request.PathBase}/api/games/{gameId}/image"
                    : null);

        return new GameDto(
            GameId: gameId,
            SteamAppId: reader.GetInt32(1),
            Name: reader.GetString(2),
            Summary: reader.GetString(3),
            ReleaseYear: reader.IsDBNull(4) ? null : reader.GetInt32(4),
            ImageUrl: imageUrl,
            VoteCount: reader.GetInt32(8),
            MicroRatio: reader.GetDouble(9),
            MesoRatio: reader.GetDouble(10),
            MacroRatio: reader.GetDouble(11));
    }

    private static double? ReadNullableDouble(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private sealed record GameDto(
        long GameId,
        int SteamAppId,
        string Name,
        string Summary,
        int? ReleaseYear,
        string? ImageUrl,
        int VoteCount,
        double? MicroRatio,
        double? MesoRatio,
        double? MacroRatio);

    private sealed record VoteDto(
        double MicroScore,
        double MesoScore,
        double MacroScore,
        string? PublicProfileName);
}
