using Microsoft.Data.Sqlite;
using MMMAtlas.Data;
using MMMAtlas.Steam;

namespace MMMAtlas.Api;

public static class GameEndpoints
{
    public static IEndpointRouteBuilder MapGameEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var games = endpoints.MapGroup("/api/games")
            .WithTags("Games");

        games.MapGet("", GetGamesAsync);
        games.MapGet("/atlas", GetAtlasGamesAsync);
        games.MapGet("/{gameId:long}", GetGameAsync);
        games.MapGet("/{gameId:long}/image", GetGameImageAsync)
            .WithName("GetGameImage");
        games.MapGet("/{gameId:long}/atlas-icon", GetAtlasIconAsync)
            .WithName("GetAtlasIcon");
        games.MapGet("/{gameId:long}/library-card", GetLibraryCardAsync)
            .WithName("GetLibraryCard");
        games.MapGet("/{gameId:long}/library-card-url", GetLibraryCardUrlAsync);
        games.MapGet("/{gameId:long}/store-thumbnail", GetStoreThumbnailAsync);
        games.MapGet("/{gameId:long}/votes", GetGameVotesAsync);

        return endpoints;
    }

    private static async Task<IResult> GetLibraryCardUrlAsync(
        long gameId,
        HttpContext httpContext,
        SteamArtworkService artworkService,
        CancellationToken cancellationToken)
    {
        var artwork = await artworkService.GetAsync(gameId, cancellationToken);
        if (artwork is null) return Results.NotFound();
        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new
        {
            url = artwork.Url,
            hash = artwork.Hash,
            needsAtlasIconUpdate = artwork.AtlasIconNeedsUpdate
        });
    }

    private static async Task<IResult> GetStoreThumbnailAsync(
        long gameId,
        HttpContext httpContext,
        SteamArtworkService artworkService,
        CancellationToken cancellationToken)
    {
        var thumbnail = await artworkService.GetStoreThumbnailAsync(gameId, cancellationToken);
        if (thumbnail is null)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.NotFound();
        }
        httpContext.Response.Headers.CacheControl = "public, max-age=3600";
        return Results.File(thumbnail.Content, "image/jpeg",
            entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{thumbnail.Hash}\""));
    }

    private static async Task<IResult> GetGamesAsync(
        HttpContext httpContext,
        DatabaseConnection database,
        SteamLibraryFilter filter,
        CancellationToken cancellationToken)
    {
        var sql = $"""
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
                steam.IsFree,
                (
                    SELECT group_concat(developer.DeveloperName, char(31))
                    FROM SteamGameDevelopers AS developer
                    WHERE developer.GameId = steam.GameId
                ),
                (
                    SELECT group_concat(publisher.PublisherName, char(31))
                    FROM SteamGamePublishers AS publisher
                    WHERE publisher.GameId = steam.GameId
                ),
                (
                    SELECT group_concat(genre.GenreName, char(31))
                    FROM SteamGameGenres AS genre
                    WHERE genre.GameId = steam.GameId
                ),
                CASE WHEN atlas.ImageContent IS NULL THEN 0 ELSE 1 END,
                CASE WHEN info.ImageContent IS NULL THEN 0 ELSE 1 END,
                aggregate.VoteCount,
                aggregate.MicroRatio,
                aggregate.MesoRatio,
                aggregate.MacroRatio,
                steam.LibraryCardUrl,
                steam.AtlasIconNeedsUpdate
            FROM SteamGames AS steam
            INNER JOIN GameVoteAggregates AS aggregate
                ON aggregate.GameId = steam.GameId
            LEFT JOIN DevelopmentGameInfo AS info
                ON info.GameId = steam.GameId
            LEFT JOIN GameAtlasIcons AS atlas
                ON atlas.GameId = steam.GameId
            WHERE {SteamLibraryFilter.VisibleSql}
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
        SteamLibraryFilter.AddParameters(command, filter.Settings);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<GameDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadGame(reader, httpContext.Request));
        }

        return Results.Ok(results);
    }

    private static async Task<IResult> GetAtlasGamesAsync(
        HttpContext httpContext,
        DatabaseConnection database,
        SteamLibraryFilter filter,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT
                steam.GameId,
                COALESCE(
                    steam.Name,
                    info.Name,
                    'Steam app ' || steam.SteamAppId
                ),
                steam.ReleaseYear,
                steam.IsFree,
                (
                    SELECT group_concat(developer.DeveloperName, char(31))
                    FROM SteamGameDevelopers AS developer
                    WHERE developer.GameId = steam.GameId
                ),
                (
                    SELECT group_concat(publisher.PublisherName, char(31))
                    FROM SteamGamePublishers AS publisher
                    WHERE publisher.GameId = steam.GameId
                ),
                (
                    SELECT group_concat(genre.GenreName, char(31))
                    FROM SteamGameGenres AS genre
                    WHERE genre.GameId = steam.GameId
                ),
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
            WHERE {SteamLibraryFilter.VisibleSql}
            ORDER BY steam.GameId;
            """;

        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        SteamLibraryFilter.AddParameters(command, filter.Settings);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<AtlasGameDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var gameId = reader.GetInt64(0);
            var imageUrl = reader.GetBoolean(7)
                ? $"{httpContext.Request.PathBase}/api/games/{gameId}/atlas-icon"
                : reader.GetBoolean(8)
                    ? $"{httpContext.Request.PathBase}/api/games/{gameId}/image"
                    : null;

            results.Add(new AtlasGameDto(
                GameId: gameId,
                Name: reader.GetString(1),
                ReleaseYear: reader.IsDBNull(2) ? null : reader.GetInt32(2),
                IsFree: reader.IsDBNull(3) ? null : reader.GetBoolean(3),
                Developers: ReadNames(reader, 4),
                Publishers: ReadNames(reader, 5),
                Genres: ReadNames(reader, 6),
                ImageUrl: imageUrl,
                VoteCount: reader.GetInt32(9),
                MicroRatio: reader.GetDouble(10),
                MesoRatio: reader.GetDouble(11),
                MacroRatio: reader.GetDouble(12)));
        }

        return Results.Ok(results);
    }

    private static async Task<IResult> GetGameAsync(
        long gameId,
        HttpContext httpContext,
        DatabaseConnection database,
        SteamLibraryFilter filter,
        CancellationToken cancellationToken)
    {
        var sql = $"""
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
                steam.IsFree,
                (
                    SELECT group_concat(developer.DeveloperName, char(31))
                    FROM SteamGameDevelopers AS developer
                    WHERE developer.GameId = steam.GameId
                ),
                (
                    SELECT group_concat(publisher.PublisherName, char(31))
                    FROM SteamGamePublishers AS publisher
                    WHERE publisher.GameId = steam.GameId
                ),
                (
                    SELECT group_concat(genre.GenreName, char(31))
                    FROM SteamGameGenres AS genre
                    WHERE genre.GameId = steam.GameId
                ),
                CASE WHEN atlas.ImageContent IS NULL THEN 0 ELSE 1 END,
                CASE WHEN info.ImageContent IS NULL THEN 0 ELSE 1 END,
                COALESCE(aggregate.VoteCount, 0),
                COALESCE(aggregate.MicroRatio, 0),
                COALESCE(aggregate.MesoRatio, 0),
                COALESCE(aggregate.MacroRatio, 0),
                steam.LibraryCardUrl,
                steam.AtlasIconNeedsUpdate
            FROM SteamGames AS steam
            LEFT JOIN GameVoteAggregates AS aggregate
                ON aggregate.GameId = steam.GameId
            LEFT JOIN DevelopmentGameInfo AS info
                ON info.GameId = steam.GameId
            LEFT JOIN GameAtlasIcons AS atlas
                ON atlas.GameId = steam.GameId
            WHERE steam.GameId = $gameId
                AND {SteamLibraryFilter.VisibleSql};
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

        SteamLibraryFilter.AddParameters(command, filter.Settings);

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
        SteamArtworkService artworkService,
        CancellationToken cancellationToken)
    {
        var artwork = await artworkService.GetAsync(gameId, cancellationToken);
        if (artwork is null) return Results.NotFound();
        if (artwork.Content is null) return Results.NotFound();
        httpContext.Response.Headers.CacheControl = "public, max-age=86400";
        return Results.File(artwork.Content, artwork.ContentType,
            entityTag: artwork.Hash is null ? null
                : new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{artwork.Hash}\""));
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
                END AS PublicProfileName,
                CASE
                    WHEN vote.IsPublic = 1
                        AND account.PersonaName IS NOT NULL
                    THEN account.ProfileUrl
                    ELSE NULL
                END AS PublicProfileUrl,
                CASE
                    WHEN vote.IsPublic = 1
                        AND account.DataCollectionEnabled = 1
                        AND account.PersonaName IS NOT NULL
                    THEN library.PlaytimeForeverMinutes
                    ELSE NULL
                END AS PlaytimeForeverMinutes
            FROM Votes AS vote
            LEFT JOIN SteamAccounts AS account
                ON account.UserId = vote.UserId
            LEFT JOIN SteamLibraryGames AS library
                ON library.UserId = vote.UserId
                AND library.GameId = vote.GameId
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
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                PublicProfileUrl:
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                PlaytimeForeverMinutes:
                    reader.IsDBNull(5) ? null : reader.GetInt32(5)));
        }

        return Results.Ok(results);
    }

    private static GameDto ReadGame(
        SqliteDataReader reader,
        HttpRequest request)
    {
        var gameId = reader.GetInt64(0);
        var hasAtlasIcon = reader.GetBoolean(9);
        var hasDevelopmentImage = reader.GetBoolean(10);
        var imageUrl = hasAtlasIcon
            ? $"{request.PathBase}/api/games/{gameId}/atlas-icon"
            : hasDevelopmentImage
                ? $"{request.PathBase}/api/games/{gameId}/image"
                : null;

        return new GameDto(
            GameId: gameId,
            SteamAppId: reader.GetInt32(1),
            Name: reader.GetString(2),
            Summary: reader.GetString(3),
            ReleaseYear: reader.IsDBNull(4) ? null : reader.GetInt32(4),
            IsFree: reader.IsDBNull(5) ? null : reader.GetBoolean(5),
            Developers: ReadNames(reader, 6),
            Publishers: ReadNames(reader, 7),
            Genres: ReadNames(reader, 8),
            ImageUrl: imageUrl,
            VoteCount: reader.GetInt32(11),
            MicroRatio: reader.GetDouble(12),
            MesoRatio: reader.GetDouble(13),
            MacroRatio: reader.GetDouble(14),
            LibraryCardUrl: reader.IsDBNull(15) ? null : reader.GetString(15),
            NeedsAtlasIcon: reader.GetBoolean(16));
    }

    private static IReadOnlyList<string> ReadNames(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? []
            : reader.GetString(ordinal).Split(
                (char)31,
                StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries);

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
        bool? IsFree,
        IReadOnlyList<string> Developers,
        IReadOnlyList<string> Publishers,
        IReadOnlyList<string> Genres,
        string? ImageUrl,
        int VoteCount,
        double? MicroRatio,
        double? MesoRatio,
        double? MacroRatio,
        string? LibraryCardUrl,
        bool NeedsAtlasIcon);

    private sealed record AtlasGameDto(
        long GameId,
        string Name,
        int? ReleaseYear,
        bool? IsFree,
        IReadOnlyList<string> Developers,
        IReadOnlyList<string> Publishers,
        IReadOnlyList<string> Genres,
        string? ImageUrl,
        int VoteCount,
        double MicroRatio,
        double MesoRatio,
        double MacroRatio);

    private sealed record VoteDto(
        double MicroScore,
        double MesoScore,
        double MacroScore,
        string? PublicProfileName,
        string? PublicProfileUrl,
        int? PlaytimeForeverMinutes);
}
