using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MMMAtlas.Observability;

namespace MMMAtlas.Tests;

[TestClass]
public sealed class FileLoggingTests
{
    [TestMethod]
    [DataRow("Information", false)]
    [DataRow("Debug", true)]
    public void FileOutputHonorsLevelsAndPreservesStructuredFields(string level, bool debugExpected)
    {
        var directory = Directory.CreateTempSubdirectory("atlas-logging-");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Logging:LogLevel:Default"] = level,
                    ["Logging:LogLevel:Microsoft"] = "Warning"
                }).Build();
            using (var factory = LoggerFactory.Create(logging =>
            {
                logging.AddConfiguration(configuration.GetSection("Logging"));
                logging.AddAtlasFileLogging(configuration, directory.FullName);
            }))
            {
                var logger = factory.CreateLogger("MMMAtlas.Tests");
                logger.LogDebug("Debug details");
                logger.LogInformation("Request {UserId} {GameId} {StatusCode} {ElapsedMilliseconds}", 7, 42, 200, 12.5);
                factory.CreateLogger("Microsoft.Test").LogInformation("Suppressed framework noise");
            }

            var file = Directory.GetFiles(Path.Combine(directory.FullName, "App_Data", "Logs"), "*.log").Single();
            var lines = File.ReadAllLines(file);
            Assert.HasCount(debugExpected ? 2 : 1, lines);
            using var entry = JsonDocument.Parse(lines[^1]);
            Assert.AreEqual("Information", entry.RootElement.GetProperty("Level").GetString());
            var properties = entry.RootElement.GetProperty("Properties");
            Assert.AreEqual(7, properties.GetProperty("UserId").GetInt32());
            Assert.AreEqual(42, properties.GetProperty("GameId").GetInt32());
            Assert.AreEqual(200, properties.GetProperty("StatusCode").GetInt32());
            Assert.AreEqual(12.5, properties.GetProperty("ElapsedMilliseconds").GetDouble());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void SizeRollingRetainsOnlyConfiguredNumberOfFiles()
    {
        var directory = Directory.CreateTempSubdirectory("atlas-logging-");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Logging:File:Path"] = "rolling-.log",
                    ["Logging:File:FileSizeLimitBytes"] = "200",
                    ["Logging:File:RetainedFileCountLimit"] = "2"
                }).Build();
            using (var factory = LoggerFactory.Create(logging =>
                logging.AddAtlasFileLogging(configuration, directory.FullName)))
            {
                var logger = factory.CreateLogger("MMMAtlas.Tests");
                for (var index = 0; index < 20; index++)
                    logger.LogInformation("Record {Index}: {Detail}", index, new string('x', 200));
            }

            var files = Directory.GetFiles(directory.FullName, "*.log");
            Assert.HasCount(2, files);
            Assert.IsTrue(files.Any(file => File.ReadAllText(file).Contains("\"Index\":19")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
