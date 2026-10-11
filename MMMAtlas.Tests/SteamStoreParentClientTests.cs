using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MMMAtlas.Steam;

namespace MMMAtlas.Tests;

[TestClass]
public sealed class SteamStoreParentClientTests
{
    [TestMethod]
    public async Task ReadsParentPresenceAndCachesStoreResults()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new Handler("""
            {"response":{"store_items":[
              {"appid":1,"related_items":{"parent_appid":42}},
              {"appid":2,"related_items":{}},
              {"appid":3},
              {"appid":4,"related_items":{"parent_appid":null}},
              {"appid":5,"related_items":{"parent_appid":0}}
            ]}}
            """);
        var client = new SteamStoreParentClient(handler, cache, NullLogger<SteamStoreParentClient>.Instance);
        var result = await client.GetParentsAsync([1,2,3,4,5], CancellationToken.None);
        Assert.IsNotNull(result);
        Assert.IsTrue(result[1]);
        Assert.IsFalse(result[2]);
        Assert.IsFalse(result[3]);
        Assert.IsFalse(result[4]);
        Assert.IsTrue(result[5]);
        await client.GetParentsAsync([1,2,3,4,5], CancellationToken.None);
        Assert.AreEqual(1, handler.Requests);
    }

    [TestMethod]
    [DataRow("{\"response\":{\"store_items\":[]}}")]
    [DataRow("invalid")]
    [DataRow("{\"response\":{\"store_items\":[{\"appid\":1,\"success\":2}]}}")]
    public async Task IncompleteOrInvalidResponseDefersFiltering(string payload)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var client = new SteamStoreParentClient(new Handler(payload), cache, NullLogger<SteamStoreParentClient>.Instance);
        Assert.IsNull(await client.GetParentsAsync([1], CancellationToken.None));
    }

    private sealed class Handler(string payload) : HttpMessageHandler, IHttpClientFactory
    {
        public int Requests { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://api.steampowered.com/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            StringAssert.Contains(Uri.UnescapeDataString(request.RequestUri!.Query), "\"include_related_items\":true");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
        }
    }
}
