using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using MMMAtlas.Observability;
using MMMAtlas.Steam;

namespace MMMAtlas.Tests;

[TestClass]
public sealed class OperationalRequestLoggingMiddlewareTests
{
    [TestMethod]
    public async Task MutationCompletionIncludesRouteAndInternalIds()
    {
        var logger = new RecordingLogger<OperationalRequestLoggingMiddleware>();
        var middleware = new OperationalRequestLoggingMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            logger);
        var context = CreateContext(
            HttpMethods.Post,
            "/api/me/votes/42",
            "/api/me/votes/{gameId:long}");
        context.TraceIdentifier = "trace-123";
        context.Request.RouteValues["gameId"] = "42";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(SteamClaims.UserId, "7")],
            "Test"));

        await middleware.InvokeAsync(context);

        var entry = AssertSingleEntry(logger);
        Assert.AreEqual(LogLevel.Information, entry.Level);
        Assert.AreEqual("/api/me/votes/{gameId:long}", entry["Route"]);
        Assert.AreEqual(7L, entry["UserId"]);
        Assert.AreEqual(42L, entry["GameId"]);
        Assert.AreEqual("trace-123", entry["TraceId"]);
        Assert.AreEqual(StatusCodes.Status200OK, entry["StatusCode"]);
    }

    [TestMethod]
    public async Task RoutineStaticRequestDoesNotProduceACompletionLog()
    {
        var logger = new RecordingLogger<OperationalRequestLoggingMiddleware>();
        var middleware = new OperationalRequestLoggingMiddleware(
            _ => Task.CompletedTask,
            logger);
        var context = CreateContext(
            HttpMethods.Get,
            "/css/site.css",
            "/css/site.css");

        await middleware.InvokeAsync(context);

        Assert.IsEmpty(logger.Entries);
    }

    [TestMethod]
    public async Task SuccessfulProfilerSampleDoesNotProduceACompletionLog()
    {
        var logger = new RecordingLogger<OperationalRequestLoggingMiddleware>();
        var middleware = new OperationalRequestLoggingMiddleware(
            _ => Task.CompletedTask,
            logger);
        var context = CreateContext(
            HttpMethods.Post,
            "/api/profiler/games",
            "/api/profiler/games");

        await middleware.InvokeAsync(context);

        Assert.IsEmpty(logger.Entries);
    }

    [TestMethod]
    public async Task UnhandledFailureIncludesIdsAndIsRethrown()
    {
        var logger = new RecordingLogger<OperationalRequestLoggingMiddleware>();
        var expected = new InvalidOperationException("database unavailable");
        var middleware = new OperationalRequestLoggingMiddleware(
            _ => throw expected,
            logger);
        var context = CreateContext(
            HttpMethods.Get,
            "/api/games/81",
            "/api/games/{gameId:long}");
        context.Request.RouteValues["gameId"] = 81L;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(context));

        Assert.AreSame(expected, actual);
        var entry = AssertSingleEntry(logger);
        Assert.AreEqual(LogLevel.Error, entry.Level);
        Assert.AreEqual(81L, entry["GameId"]);
        Assert.AreSame(expected, entry.Exception);
    }

    [TestMethod]
    public async Task ClientCancellationIsRethrownWithoutAnErrorLog()
    {
        var logger = new RecordingLogger<OperationalRequestLoggingMiddleware>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var expected = new OperationCanceledException(cancellation.Token);
        var middleware = new OperationalRequestLoggingMiddleware(
            _ => throw expected,
            logger);
        var context = CreateContext(
            HttpMethods.Get,
            "/api/games/81",
            "/api/games/{gameId:long}");
        context.RequestAborted = cancellation.Token;

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(
            () => middleware.InvokeAsync(context));

        Assert.AreSame(expected, actual);
        Assert.IsEmpty(logger.Entries);
    }

    private static DefaultHttpContext CreateContext(
        string method,
        string path,
        string route)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(route),
            0,
            EndpointMetadataCollection.Empty,
            route));
        return context;
    }

    private static LogEntry AssertSingleEntry<T>(RecordingLogger<T> logger)
    {
        Assert.HasCount(1, logger.Entries);
        return logger.Entries[0];
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state
                as IEnumerable<KeyValuePair<string, object?>>
                ?? [];
            Entries.Add(new LogEntry(
                logLevel,
                exception,
                properties.ToDictionary(pair => pair.Key, pair => pair.Value)));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties)
    {
        public object? this[string name] => Properties[name];
    }
}
