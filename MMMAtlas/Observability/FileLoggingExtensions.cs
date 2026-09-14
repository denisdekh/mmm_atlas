using Serilog;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Json;

namespace MMMAtlas.Observability;

public static class FileLoggingExtensions
{
    public static ILoggingBuilder AddAtlasFileLogging(
        this ILoggingBuilder logging,
        IConfiguration configuration,
        string contentRoot)
    {
        var settings = configuration.GetSection("Logging:File");
        if (!settings.GetValue("Enabled", true))
            return logging;

        var path = Path.GetFullPath(
            settings["Path"] ?? "App_Data/Logs/mmm-atlas-.log", contentRoot);
        var sizeLimit = settings.GetValue<long>("FileSizeLimitBytes", 10 * 1024 * 1024);
        var retainedCount = settings.GetValue("RetainedFileCountLimit", 14);
        if (sizeLimit <= 0 || retainedCount <= 0)
            throw new InvalidOperationException("Logging:File size and retention limits must be positive.");

        // Register directly: AddSerilog() installs a Trace filter that would
        // override the standard Logging:LogLevel configuration.
        logging.Services.AddSingleton<ILoggerProvider>(_ =>
            new SerilogLoggerProvider(new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .Enrich.FromLogContext()
                .WriteTo.File(
                    new JsonFormatter(),
                    path,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: sizeLimit,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: retainedCount,
                    shared: true)
                .CreateLogger(), dispose: true));
        return logging;
    }
}
