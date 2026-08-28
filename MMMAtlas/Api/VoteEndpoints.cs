using System.Buffers.Binary;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using MMMAtlas.AtlasEvents;
using MMMAtlas.Steam;
using MMMAtlas.Voting;

namespace MMMAtlas.Api;

public static class VoteEndpoints
{
    public static IEndpointRouteBuilder MapVoteEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/me/votes/{gameId:long}", SubmitVoteAsync)
            .RequireAuthorization()
            .WithTags("Votes");
        endpoints.MapPost("/api/me/votes/private", MakeAllPrivateAsync)
            .RequireAuthorization()
            .WithTags("Votes");

        return endpoints;
    }

    private static async Task<IResult> MakeAllPrivateAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        VotingManager manager,
        CancellationToken cancellationToken)
    {
        await antiforgery.ValidateRequestAsync(httpContext);

        var userId = GetUserId(httpContext.User);
        var updatedVotes = await manager.MakeAllPrivateAsync(
            userId,
            cancellationToken);

        return WantsHtml(httpContext.Request)
            ? Results.Redirect("/dashboard?notice=votes-private")
            : Results.Ok(new { UpdatedVotes = updatedVotes });
    }

    private static async Task<IResult> SubmitVoteAsync(
        long gameId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        VotingManager manager,
        AtlasEventBroadcaster broadcaster,
        ILogger<AtlasEventBroadcaster> logger,
        CancellationToken cancellationToken)
    {
        await antiforgery.ValidateRequestAsync(httpContext);
        var form = await httpContext.Request.ReadFormAsync(cancellationToken);

        if (!TryReadScore(form["microScore"], out var microScore)
            || !TryReadScore(form["mesoScore"], out var mesoScore)
            || !TryReadScore(form["macroScore"], out var macroScore))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["scores"] =
                [
                    "Each score must be from 0.0 to 10.0 in 0.1 increments."
                ]
            });
        }

        if (microScore + mesoScore + macroScore == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["scores"] = ["At least one score must be greater than zero."]
            });
        }

        var userId = GetUserId(httpContext.User);

        var (atlasIcon, iconError) =
            await ReadAtlasIconAsync(form, cancellationToken);
        if (iconError is not null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["atlasIcon"] = [iconError]
            });
        }

        VoteSubmissionResult? result;
        try
        {
            result = await manager.SubmitAsync(
                userId,
                gameId,
                new VoteScores(
                    microScore,
                    mesoScore,
                    macroScore,
                    form["isPublic"].Any(value => string.Equals(
                        value,
                        bool.TrueString,
                        StringComparison.OrdinalIgnoreCase))),
                cancellationToken,
                atlasIcon);
        }
        catch (AtlasIconRequiredException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["atlasIcon"] = [exception.Message]
            });
        }

        if (result is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Game not found");
        }

        // The database transaction has committed before this best-effort,
        // non-blocking notification. Slow or disconnected viewers cannot hold up
        // the vote request, and the authoritative game endpoint remains the source
        // of truth if a notification is missed.
        try
        {
            broadcaster.PublishGameUpdated(gameId);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "The vote for game {GameId} committed, but its atlas update notification failed.",
                gameId);
        }

        return Results.Ok(new VoteResponse(
            result.Scores.MicroScore,
            result.Scores.MesoScore,
            result.Scores.MacroScore,
            result.Scores.IsPublic,
            result.Amended));
    }

    private static async Task<(AtlasIcon? Icon, string? Error)>
        ReadAtlasIconAsync(
            IFormCollection form,
            CancellationToken cancellationToken)
    {
        var file = form.Files.GetFile("atlasIcon");
        if (file is null)
        {
            return (null, null);
        }

        if (file.Length is 0 or > AtlasIcon.MaximumContentLength)
        {
            return (
                null,
                $"The cropped icon must be no larger than {AtlasIcon.MaximumContentLength / 1024} KB.");
        }

        await using var stream = new MemoryStream((int)file.Length);
        await file.CopyToAsync(stream, cancellationToken);
        var content = stream.ToArray();

        if (!TryReadPngDimensions(content, out var width, out var height))
        {
            return (null, "The cropped icon must be a valid PNG image.");
        }

        if (width != AtlasIcon.MaximumDiameterPixels
            || height != AtlasIcon.MaximumDiameterPixels)
        {
            return (
                null,
                $"The cropped icon must be {AtlasIcon.MaximumDiameterPixels} by {AtlasIcon.MaximumDiameterPixels} pixels.");
        }

        var libraryCardFile = form.Files.GetFile("libraryCard");
        if (libraryCardFile is null)
        {
            return (
                null,
                "The first vote must include the full Steam library card.");
        }

        if (libraryCardFile.Length is 0 or > LibraryCard.MaximumContentLength)
        {
            return (
                null,
                $"The library card must be no larger than {LibraryCard.MaximumContentLength / (1024 * 1024)} MB.");
        }

        await using var libraryCardStream =
            new MemoryStream((int)libraryCardFile.Length);
        await libraryCardFile.CopyToAsync(
            libraryCardStream,
            cancellationToken);
        var libraryCardContent = libraryCardStream.ToArray();
        if (!TryReadJpegDimensions(
            libraryCardContent,
            out var libraryCardWidth,
            out var libraryCardHeight))
        {
            return (null, "The Steam library card must be a valid JPEG image.");
        }

        if (libraryCardWidth > LibraryCard.MaximumWidthPixels
            || libraryCardHeight > LibraryCard.MaximumHeightPixels
            || (long)libraryCardWidth * 3 != (long)libraryCardHeight * 2)
        {
            return (
                null,
                "The Steam library card must use a 2:3 aspect ratio and be no larger than 600 by 900 pixels.");
        }

        var libraryCard = new LibraryCard(
            libraryCardContent,
            "image/jpeg",
            libraryCardWidth,
            libraryCardHeight);
        return (new AtlasIcon(content, width, libraryCard), null);
    }

    private static bool TryReadPngDimensions(
        ReadOnlySpan<byte> content,
        out int width,
        out int height)
    {
        ReadOnlySpan<byte> signature =
            [137, 80, 78, 71, 13, 10, 26, 10];

        width = 0;
        height = 0;
        if (content.Length < 24
            || !content[..8].SequenceEqual(signature)
            || BinaryPrimitives.ReadUInt32BigEndian(content[8..12]) != 13
            || !content[12..16].SequenceEqual("IHDR"u8))
        {
            return false;
        }

        var pngWidth = BinaryPrimitives.ReadUInt32BigEndian(content[16..20]);
        var pngHeight = BinaryPrimitives.ReadUInt32BigEndian(content[20..24]);
        if (pngWidth > int.MaxValue || pngHeight > int.MaxValue)
        {
            return false;
        }

        width = (int)pngWidth;
        height = (int)pngHeight;
        return true;
    }

    private static bool TryReadJpegDimensions(
        ReadOnlySpan<byte> content,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        if (content.Length < 4 || content[0] != 0xff || content[1] != 0xd8)
        {
            return false;
        }

        var index = 2;
        while (index + 3 < content.Length)
        {
            while (index < content.Length && content[index] != 0xff)
            {
                index++;
            }

            while (index < content.Length && content[index] == 0xff)
            {
                index++;
            }

            if (index >= content.Length)
            {
                return false;
            }

            var marker = content[index++];
            if (marker is 0xd8 or 0xd9 or 0x01
                || marker is >= 0xd0 and <= 0xd7)
            {
                continue;
            }

            if (index + 1 >= content.Length)
            {
                return false;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(
                content[index..(index + 2)]);
            if (segmentLength < 2 || index + segmentLength > content.Length)
            {
                return false;
            }

            if (IsStartOfFrame(marker))
            {
                if (segmentLength < 7)
                {
                    return false;
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(
                    content[(index + 3)..(index + 5)]);
                width = BinaryPrimitives.ReadUInt16BigEndian(
                    content[(index + 5)..(index + 7)]);
                return width > 0 && height > 0;
            }

            if (marker == 0xda)
            {
                return false;
            }

            index += segmentLength;
        }

        return false;
    }

    private static bool IsStartOfFrame(byte marker) =>
        marker is 0xc0 or 0xc1 or 0xc2 or 0xc3
            or 0xc5 or 0xc6 or 0xc7
            or 0xc9 or 0xca or 0xcb
            or 0xcd or 0xce or 0xcf;

    private static bool TryReadScore(
        string? value,
        out decimal score) =>
        decimal.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out score)
        && score is >= 0 and <= 10
        && decimal.Truncate(score * 10) == score * 10;

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

    private sealed record VoteResponse(
        decimal MicroScore,
        decimal MesoScore,
        decimal MacroScore,
        bool IsPublic,
        bool Amended);
}
