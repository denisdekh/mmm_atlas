using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using MMMAtlas.Data;

namespace MMMAtlas.Api;

public static class FeedbackEndpoints
{
    public static IEndpointRouteBuilder MapFeedbackEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/feedback", SubmitAsync)
            .AllowAnonymous()
            .RequireRateLimiting("feedback")
            .WithTags("Feedback");

        return endpoints;
    }

    private static async Task<IResult> SubmitAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        DatabaseConnection database,
        CancellationToken cancellationToken)
    {
        await antiforgery.ValidateRequestAsync(context);

        if (!context.Request.HasFormContentType)
        {
            return Results.BadRequest(new { error = "Feedback must be submitted as a form." });
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var values = form["message"];
        if (values.Count != 1 || values[0] is not { } message
            || message.Length > 1000 || string.IsNullOrWhiteSpace(message))
        {
            return Results.BadRequest(new { error = "Enter feedback between 1 and 1000 characters." });
        }

        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Feedback (Message, SourceIp)
            VALUES ($message, $sourceIp);
            """;
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue(
            "$sourceIp",
            (object?)context.Connection.RemoteIpAddress?.ToString()
                ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return Results.Ok(new { message = "Thanks for your feedback." });
    }
}
