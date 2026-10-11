using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using MMMAtlas.Data;
using MMMAtlas.Steam;
using MMMAtlas.Voting;

namespace MMMAtlas.Tests;

[TestClass]
public sealed class SteamArtworkServiceTests
{
    private string path = null!;
    private DatabaseConnection database = null!;
    private ArtworkTestHttpClientFactory http = null!;
    private SteamArtworkService service = null!;
    private MemoryCache cache = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        path = Path.Combine(Path.GetTempPath(), $"atlas-artwork-{Guid.NewGuid():N}.db");
        database = DatabaseConnection.Create($"Data Source={path};Foreign Keys=True", Directory.GetCurrentDirectory());
        await DatabaseInitializer.InitializeAsync(database, false);
        await ExecuteAsync("""
            INSERT INTO Users(UserId) VALUES(1);
            INSERT INTO Games(GameId) VALUES(1),(2);
            INSERT INTO SteamGames(GameId, SteamAppId) VALUES(1,1234),(2,5678);
            """);
        http = new ArtworkTestHttpClientFactory();
        cache = new MemoryCache(new MemoryCacheOptions());
        service = new SteamArtworkService(database, http, NullLogger<SteamArtworkService>.Instance, cache);
    }

    [TestCleanup]
    public void Cleanup()
    {
        cache.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(path);
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
    }

    [TestMethod]
    [DataRow("small_capsule_2x")]
    [DataRow("small_capsule")]
    [DataRow("header")]
    public async Task StoreThumbnailWorksWithoutALibraryCardAndCachesItsOwnBytes(string field)
    {
        http.MetadataOverride = JsonSerializer.Serialize(new
        {
            response = new
            {
                store_items = new[] { new
                {
                    appid = 1234,
                    assets = new Dictionary<string, string>
                    {
                        ["asset_url_format"] = "steam/apps/1234/${FILENAME}",
                        [field] = "hash/capsule_231x87.jpg"
                    }
                } }
            }
        });
        http.ImageContent[7] = 0;
        http.ImageContent[8] = 87;
        http.ImageContent[9] = 0;
        http.ImageContent[10] = 231;

        var thumbnail = await service.GetStoreThumbnailAsync(1, CancellationToken.None);
        Assert.IsNotNull(thumbnail);
        CollectionAssert.AreEqual(http.ImageContent, thumbnail.Content);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(http.ImageContent)), thumbnail.Hash);
        StringAssert.Contains(http.LastImageUrl!, "steam/apps/1234/hash/capsule_231x87.jpg");
        Assert.AreSame(thumbnail, await service.GetStoreThumbnailAsync(1, CancellationToken.None));
        Assert.AreEqual(1, http.MetadataRequests);
        Assert.AreEqual(1, http.ImageRequests);
        var libraryCard = await service.ReadCachedAsync(1, CancellationToken.None);
        Assert.IsNotNull(libraryCard);
        Assert.IsNull(libraryCard.Content);
        Assert.IsNull(libraryCard.Hash);
        Assert.IsFalse(libraryCard.AtlasIconNeedsUpdate);
    }

    [TestMethod]
    [DataRow("http")]
    [DataRow("invalid-image")]
    [DataRow("oversized")]
    [DataRow("unsafe-path")]
    [DataRow("missing-assets")]
    public async Task FailedStoreThumbnailsAreUnavailableAndFailuresAreCachedBriefly(string failure)
    {
        http.MetadataOverride = failure switch
        {
            "unsafe-path" => """
                {"response":{"store_items":[{"appid":1234,"assets":{
                    "asset_url_format":"steam/apps/1234/../../../${FILENAME}",
                    "small_capsule":"thumbnail.jpg"}}]}}
                """,
            "missing-assets" => "{\"response\":{\"store_items\":[]}}",
            _ => """
                {"response":{"store_items":[{"appid":1234,"assets":{
                    "asset_url_format":"steam/apps/1234/${FILENAME}",
                    "small_capsule":"thumbnail.jpg"}}]}}
                """
        };
        if (failure == "http") http.ImageStatus = System.Net.HttpStatusCode.NotFound;
        if (failure == "invalid-image") http.ImageContent = [1, 2, 3];
        if (failure == "oversized") http.ImageContent = new byte[LibraryCard.MaximumContentLength + 1];

        Assert.IsNull(await service.GetStoreThumbnailAsync(1, CancellationToken.None));
        var requests = http.ImageRequests;
        Assert.IsNull(await service.GetStoreThumbnailAsync(1, CancellationToken.None));
        Assert.AreEqual(1, http.MetadataRequests);
        Assert.AreEqual(requests, http.ImageRequests);
    }

    [TestMethod]
    public async Task UnknownGamesDoNotRequestStoreThumbnails()
    {
        Assert.IsNull(await service.GetStoreThumbnailAsync(999, CancellationToken.None));
        Assert.AreEqual(0, http.MetadataRequests);
        Assert.AreEqual(0, http.ImageRequests);
    }

    [TestMethod]
    public async Task LibraryImportBatchesMetadataAndPersistsUrlBytesHashAndTimestamp()
    {
        await service.ImportMissingAsync([1234, 5678], CancellationToken.None);
        Assert.AreEqual(1, http.MetadataRequests);
        Assert.AreEqual(2, http.ImageRequests);
        var artwork = await service.GetAsync(1, CancellationToken.None);
        Assert.IsNotNull(artwork);
        StringAssert.Contains(artwork.Url!, "steam/apps/1234/hash/library_capsule.jpg");
        CollectionAssert.AreEqual(http.ImageContent, artwork.Content);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(http.ImageContent)), artwork.Hash);
        Assert.IsNotNull(artwork.HashCheckedAtUtc);
        Assert.IsFalse(artwork.AtlasIconNeedsUpdate);
        await service.ImportMissingAsync([1234, 5678], CancellationToken.None);
        Assert.AreEqual(1, http.MetadataRequests);
        Assert.AreEqual(2, http.ImageRequests);
    }

    [TestMethod]
    public async Task FreshImagesAreSkippedAndAnUnchangedStaleImageOnlyUpdatesTheTimestamp()
    {
        await service.ImportMissingAsync([1234], CancellationToken.None);
        await service.RefreshIfStaleAsync(1, CancellationToken.None);
        Assert.AreEqual(1, http.ImageRequests);
        await AgeAsync();
        http.MetadataOverride = """
            {"response":{"store_items":[{"appid":1234,"assets":{
                "asset_url_format":"steam/apps/1234/${FILENAME}?t=456",
                "library_capsule":"new-path/library_capsule.jpg"}}]}}
            """;
        await service.RefreshIfStaleAsync(1, CancellationToken.None);
        Assert.AreEqual(2, http.ImageRequests);
        var artwork = await service.ReadCachedAsync(1, CancellationToken.None);
        Assert.IsNotNull(artwork);
        Assert.IsTrue(artwork.HashCheckedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.IsFalse(artwork.AtlasIconNeedsUpdate);
        StringAssert.Contains(artwork.Url!, "new-path/library_capsule.jpg?t=456");
    }

    [TestMethod]
    public async Task ChangedHashRequiresANewMatchingCropAndClearsOnlyAfterReplacement()
    {
        await service.ImportMissingAsync([1234], CancellationToken.None);
        var oldCard = http.ImageContent;
        var manager = new VotingManager(database);
        await manager.SubmitAsync(1, 1, new VoteScores(5, 5, 5), CancellationToken.None,
            new AtlasIcon([1], 96, new LibraryCard(oldCard, "image/jpeg", 300, 450)));
        http.ImageContent = [.. oldCard, 42];
        await AgeAsync();
        await service.RefreshIfStaleAsync(1, CancellationToken.None);
        Assert.IsTrue((await service.ReadCachedAsync(1, CancellationToken.None))!.AtlasIconNeedsUpdate);
        await Assert.ThrowsAsync<AtlasIconRequiredException>(() => manager.SubmitAsync(
            1, 1, new VoteScores(6, 5, 5), CancellationToken.None));
        await Assert.ThrowsAsync<AtlasIconRequiredException>(() => manager.SubmitAsync(
            1, 1, new VoteScores(6, 5, 5), CancellationToken.None,
            new AtlasIcon([2], 96, new LibraryCard(oldCard, "image/jpeg", 300, 450))));
        await AgeAsync();
        await service.RefreshIfStaleAsync(1, CancellationToken.None);
        Assert.IsTrue((await service.ReadCachedAsync(1, CancellationToken.None))!.AtlasIconNeedsUpdate);
        await manager.SubmitAsync(1, 1, new VoteScores(6, 5, 5), CancellationToken.None,
            new AtlasIcon([2], 96, new LibraryCard(http.ImageContent, "image/jpeg", 300, 450)));
        Assert.IsFalse((await service.ReadCachedAsync(1, CancellationToken.None))!.AtlasIconNeedsUpdate);
        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ImageContent FROM GameAtlasIcons WHERE GameId=1;";
        CollectionAssert.AreEqual(new byte[] { 2 }, (byte[])(await command.ExecuteScalarAsync())!);
    }

    [TestMethod]
    public async Task FailedRefreshPreservesBackupHashTimestampAndCropFlag()
    {
        await service.ImportMissingAsync([1234], CancellationToken.None);
        await AgeAsync();
        var before = (await service.ReadCachedAsync(1, CancellationToken.None))!;
        foreach (var failure in new[] { "http", "invalid-image", "missing-assets", "invalid-json", "unsafe-path" })
        {
            http.ImageStatus = failure == "http" ? System.Net.HttpStatusCode.NotFound : System.Net.HttpStatusCode.OK;
            http.ImageContent = failure == "invalid-image" ? [1, 2, 3] : before.Content!;
            http.MetadataOverride = failure switch
            {
                "missing-assets" => "{\"response\":{\"store_items\":[]}}",
                "invalid-json" => "invalid",
                "unsafe-path" => """
                    {"response":{"store_items":[{"appid":1234,"assets":{
                    "asset_url_format":"steam/apps/1234/../../../${FILENAME}","library_capsule":"card.jpg"}}]}}
                    """,
                _ => null
            };
            await service.RefreshIfStaleAsync(1, CancellationToken.None);
            var after = (await service.GetAsync(1, CancellationToken.None))!;
            Assert.AreEqual(before.Url, after.Url);
            Assert.AreEqual(before.Hash, after.Hash);
            Assert.AreEqual(before.HashCheckedAtUtc, after.HashCheckedAtUtc);
            Assert.IsFalse(after.AtlasIconNeedsUpdate);
            CollectionAssert.AreEqual(before.Content, after.Content);
        }
    }

    [TestMethod]
    public async Task LegacyStoredCardIsUsedAsTheHashBaseline()
    {
        await new VotingManager(database).SubmitAsync(1, 1, new VoteScores(5, 5, 5), CancellationToken.None,
            new AtlasIcon([1], 96, new LibraryCard(http.ImageContent, "image/jpeg", 300, 450)));
        http.ImageContent = [.. http.ImageContent, 42];
        await service.ImportMissingAsync([1234], CancellationToken.None);
        Assert.IsTrue((await service.ReadCachedAsync(1, CancellationToken.None))!.AtlasIconNeedsUpdate);
    }

    private Task AgeAsync() => ExecuteAsync("UPDATE SteamGames SET LibraryCardHashCheckedAtUtc = datetime('now', '-8 days') WHERE GameId=1;");

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
