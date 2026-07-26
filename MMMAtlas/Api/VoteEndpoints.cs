using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
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

        return endpoints;
    }

    private static async Task<IResult> SubmitVoteAsync(
        long gameId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        VotingManager manager,
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

        var userIdValue = httpContext.User.FindFirstValue(SteamClaims.UserId);
        if (!long.TryParse(userIdValue, out var userId))
        {
            return Results.Unauthorized();
        }

        var result = await manager.SubmitAsync(
            userId,
            gameId,
            new VoteScores(microScore, mesoScore, macroScore),
            cancellationToken);

        return result is null
            ? Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Game not found")
            : Results.Ok(new VoteResponse(
                result.Scores.MicroScore,
                result.Scores.MesoScore,
                result.Scores.MacroScore,
                result.Amended));
    }

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

    private sealed record VoteResponse(
        decimal MicroScore,
        decimal MesoScore,
        decimal MacroScore,
        bool Amended);
}
