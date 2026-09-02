using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using MMMAtlas.Steam;

namespace MMMAtlas.Observability;

public sealed class OperationalRequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<OperationalRequestLoggingMiddleware> logger)
{
    private const double SlowRequestThresholdMilliseconds = 1_000;

    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            await next(context);
        }
        catch (OperationCanceledException)
            when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnects are expected and are not operational failures.
            throw;
        }
        catch (Exception exception)
        {
            var elapsedMilliseconds = Stopwatch.GetElapsedTime(started)
                .TotalMilliseconds;
            var request = ReadRequest(context);

            logger.LogError(
                exception,
                "HTTP {Method} {Route} failed after {ElapsedMilliseconds:F1} ms. "
                + "TraceId {TraceId}, UserId {UserId}, GameId {GameId}, "
                + "TargetUserId {TargetUserId}.",
                context.Request.Method,
                request.Route,
                elapsedMilliseconds,
                context.TraceIdentifier,
                request.UserId,
                request.GameId,
                request.TargetUserId);
            throw;
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var requestDetails = ReadRequest(context);
        if (!ShouldLogCompletion(
            context.Request.Method,
            context.Request.Path,
            requestDetails.Route,
            context.Response.StatusCode,
            elapsed))
        {
            return;
        }

        if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError
            || elapsed >= SlowRequestThresholdMilliseconds)
        {
            logger.LogWarning(
                "HTTP {Method} {Route} completed with {StatusCode} in "
                + "{ElapsedMilliseconds:F1} ms. TraceId {TraceId}, "
                + "UserId {UserId}, GameId {GameId}, TargetUserId {TargetUserId}.",
                context.Request.Method,
                requestDetails.Route,
                context.Response.StatusCode,
                elapsed,
                context.TraceIdentifier,
                requestDetails.UserId,
                requestDetails.GameId,
                requestDetails.TargetUserId);
            return;
        }

        logger.LogInformation(
            "HTTP {Method} {Route} completed with {StatusCode} in "
            + "{ElapsedMilliseconds:F1} ms. TraceId {TraceId}, "
            + "UserId {UserId}, GameId {GameId}, TargetUserId {TargetUserId}.",
            context.Request.Method,
            requestDetails.Route,
            context.Response.StatusCode,
            elapsed,
            context.TraceIdentifier,
            requestDetails.UserId,
            requestDetails.GameId,
            requestDetails.TargetUserId);
    }

    private static bool ShouldLogCompletion(
        string method,
        PathString path,
        string route,
        int statusCode,
        double elapsedMilliseconds)
    {
        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            return true;
        }

        if (!HttpMethods.IsGet(method)
            && !HttpMethods.IsHead(method)
            && !HttpMethods.IsOptions(method))
        {
            if (route == "/api/profiler/games")
            {
                // This endpoint is intentionally called at high volume by the
                // profiler. Keep failures and slow calls, not every sample.
                return statusCode >= StatusCodes.Status400BadRequest
                    || elapsedMilliseconds >= SlowRequestThresholdMilliseconds;
            }

            return path.StartsWithSegments("/api")
                || path.StartsWithSegments("/login")
                || path.StartsWithSegments("/logout");
        }

        var isCriticalRead = route is "/api/games/atlas"
            or "/api/games/{gameId:long}"
            or "/api/games/{gameId:long}/votes"
            or "/api/me/games"
            or "/dashboard";

        return isCriticalRead
            || (elapsedMilliseconds >= SlowRequestThresholdMilliseconds
                && path.StartsWithSegments("/api")
                && !path.StartsWithSegments("/api/atlas/events"));
    }

    private static RequestDetails ReadRequest(HttpContext context)
    {
        var route = (context.GetEndpoint() as RouteEndpoint)?
            .RoutePattern.RawText
            ?? context.Request.Path.Value
            ?? "(unknown)";

        return new RequestDetails(
            route,
            ReadPositiveInt64(
                context.User.FindFirstValue(SteamClaims.UserId)),
            ReadPositiveInt64(context.Request.RouteValues["gameId"]),
            ReadPositiveInt64(context.Request.RouteValues["userId"]));
    }

    private static long? ReadPositiveInt64(object? value) =>
        long.TryParse(value?.ToString(), out var result) && result > 0
            ? result
            : null;

    private sealed record RequestDetails(
        string Route,
        long? UserId,
        long? GameId,
        long? TargetUserId);
}
