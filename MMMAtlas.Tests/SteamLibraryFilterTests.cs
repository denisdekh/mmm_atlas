using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMMAtlas.Steam;
using SteamKit2;

namespace MMMAtlas.Tests;

[TestClass]
public sealed class SteamLibraryFilterTests
{
    [TestMethod]
    [DataRow("Game", EAppType.Game)]
    [DataRow(" game ", EAppType.Game)]
    [DataRow("bEtA", EAppType.Beta)]
    [DataRow("DLC", EAppType.DLC)]
    [DataRow("Demo", EAppType.Demo)]
    [DataRow("Application", EAppType.Application)]
    [DataRow("Tool", EAppType.Tool)]
    [DataRow("Music", EAppType.Music)]
    [DataRow("UnknownType", EAppType.Invalid)]
    [DataRow(null, EAppType.Invalid)]
    public void CommonTypeMapsToSteamAppType(string? type, EAppType expected) =>
        Assert.AreEqual(expected, SteamAppInfo.FromCommonType(type).AppType);

    [TestMethod]
    [DataRow(true, true, "1,3")]
    [DataRow(true, false, "1,2,3")]
    [DataRow(false, true, "1,3,4,5,6,7,8")]
    [DataRow(false, false, "1,2,3,4,5,6,7,8")]
    public async Task FiltersRespectConfiguredFlags(bool requireGameType, bool excludeBeta, string expectedIds)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Steam:LibraryFilters:RequireGameType"] = requireGameType.ToString(),
            ["Steam:LibraryFilters:ExcludeBeta"] = excludeBeta.ToString()
        }).Build();
        using var services = new ServiceCollection()
            .Configure<SteamLibraryFilterOptions>(configuration.GetSection("Steam:LibraryFilters"))
            .BuildServiceProvider();
        var client = new TestAppInfoClient();
        var filter = new SteamLibraryFilter(client, new TestStoreParentClient(),
            services.GetRequiredService<IOptionsMonitor<SteamLibraryFilterOptions>>(),
            NullLogger<SteamLibraryFilter>.Instance);

        var result = await filter.FilterAsync(Library(), CancellationToken.None);

        Assert.AreEqual(SteamLibraryAvailability.Available, result.Availability);
        Assert.AreEqual(expectedIds, string.Join(',', result.Games.Select(game => game.AppId)));
        Assert.AreEqual(excludeBeta ? 1 : 0, client.RequestCount);
        // Names do not drive classification: a real game can contain 'Demo' or 'Beta'.
        Assert.AreEqual("Demo Beta Prologue", result.Games[0].Name);
        Assert.AreEqual(120, result.Games[0].PlaytimeForeverMinutes);
    }

    [TestMethod]
    public async Task FiltersUseReloadedConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        using var services = new ServiceCollection()
            .Configure<SteamLibraryFilterOptions>(configuration.GetSection("Steam:LibraryFilters"))
            .BuildServiceProvider();
        var filter = new SteamLibraryFilter(new TestAppInfoClient(), new TestStoreParentClient(),
            services.GetRequiredService<IOptionsMonitor<SteamLibraryFilterOptions>>(),
            NullLogger<SteamLibraryFilter>.Instance);

        Assert.HasCount(2, (await filter.FilterAsync(Library(), CancellationToken.None)).Games);
        configuration["Steam:LibraryFilters:RequireGameType"] = "false";
        configuration["Steam:LibraryFilters:ExcludeBeta"] = "false";
        configuration.Reload();
        Assert.HasCount(8, (await filter.FilterAsync(Library(), CancellationToken.None)).Games);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedOrPartialLookupDefersLibrarySync(bool partial)
    {
        using var services = new ServiceCollection().AddOptions().BuildServiceProvider();
        var client = new TestAppInfoClient { Fail = !partial, OmitLast = partial };
        var filter = new SteamLibraryFilter(client, new TestStoreParentClient(),
            services.GetRequiredService<IOptionsMonitor<SteamLibraryFilterOptions>>(),
            NullLogger<SteamLibraryFilter>.Instance);

        var result = await filter.FilterAsync(Library(), CancellationToken.None);
        Assert.AreEqual(SteamLibraryAvailability.Unavailable, result.Availability);
        Assert.IsEmpty(result.Games);
    }

    [TestMethod]
    public async Task PrivateUnavailableAndEmptyLibrariesDoNotFetchClassifications()
    {
        using var services = new ServiceCollection().AddOptions().BuildServiceProvider();
        var client = new TestAppInfoClient();
        var filter = new SteamLibraryFilter(client, new TestStoreParentClient(),
            services.GetRequiredService<IOptionsMonitor<SteamLibraryFilterOptions>>(),
            NullLogger<SteamLibraryFilter>.Instance);
        foreach (var library in new[] { SteamLibraryResult.Private(), SteamLibraryResult.Unavailable(), SteamLibraryResult.Available([]) })
            Assert.AreSame(library, await filter.FilterAsync(library, CancellationToken.None));
        Assert.AreEqual(0, client.RequestCount);
    }

    private static SteamLibraryResult Library() => SteamLibraryResult.Available(
        Enumerable.Range(1, 8).Select(id => new SteamOwnedGame(id,
            id == 1 ? "Demo Beta Prologue" : $"App {id}", 120, null, null)));

    private sealed class TestAppInfoClient : ISteamAppInfoClient
    {
        public int RequestCount { get; private set; }
        public bool Fail { get; init; }
        public bool OmitLast { get; init; }

        public Task<IReadOnlyDictionary<int, SteamAppInfo>?> GetAppsAsync(
            IEnumerable<int> appIds, CancellationToken cancellationToken)
        {
            RequestCount++;
            string[] types = ["Game", " bEtA ", "Demo", "DLC", "Tool", "Application", "Music", "Video"];
            return Task.FromResult<IReadOnlyDictionary<int, SteamAppInfo>?>(Fail ? null :
                appIds.Where(id => !OmitLast || id != 8)
                    .ToDictionary(id => id, id => SteamAppInfo.FromCommonType(types[id - 1])));
        }
    }
}
