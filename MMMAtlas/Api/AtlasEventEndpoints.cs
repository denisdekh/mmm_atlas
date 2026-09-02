using Microsoft.AspNetCore.Http.Features;
using MMMAtlas.AtlasEvents;

namespace MMMAtlas.Api;

public static class AtlasEventEndpoints
{
    private static readonly TimeSpan HeartbeatInterval =
        TimeSpan.FromSeconds(15);

    public static IEndpointRouteBuilder MapAtlasEventEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/atlas/events", StreamEventsAsync)
            .AllowAnonymous()
            .WithTags("Atlas");

        return endpoints;
    }

    private static async Task StreamEventsAsync(
        HttpContext httpContext,
        AtlasEventBroadcaster broadcaster)
    {
        var response = httpContext.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var cancellationToken = httpContext.RequestAborted;
        using var subscription = broadcaster.Subscribe();

        try
        {
            await response.StartAsync(cancellationToken);
            await response.WriteAsync(": connected\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);

            var eventAvailable = subscription.Reader
                .WaitToReadAsync(cancellationToken)
                .AsTask();
            using var heartbeatTimer = new PeriodicTimer(HeartbeatInterval);
            var heartbeatDue = heartbeatTimer
                .WaitForNextTickAsync(cancellationToken)
                .AsTask();

            while (!cancellationToken.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(
                    eventAvailable,
                    heartbeatDue);

                if (completed == eventAvailable)
                {
                    if (!await eventAvailable)
                    {
                        break;
                    }

                    while (subscription.Reader.TryRead(out var atlasEvent))
                    {
                        await response.WriteAsync(
                            AtlasEventStream.Format(atlasEvent),
                            cancellationToken);
                    }

                    await response.Body.FlushAsync(cancellationToken);
                    eventAvailable = subscription.Reader
                        .WaitToReadAsync(cancellationToken)
                        .AsTask();
                }
                else
                {
                    await response.WriteAsync(
                        AtlasEventStream.Heartbeat,
                        cancellationToken);
                    await response.Body.FlushAsync(cancellationToken);
                    heartbeatDue = heartbeatTimer
                        .WaitForNextTickAsync(cancellationToken)
                        .AsTask();
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // The viewer disconnected; disposing the subscription removes it.
        }
        catch (IOException)
        {
            // A disconnected client can close the response before cancellation
            // is observed. The subscription is still removed in the finally path.
        }
    }
}
