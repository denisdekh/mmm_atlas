using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MMMAtlas.Steam;

namespace MMMAtlas.Tests;

[TestClass]
public sealed class SteamStoreClientTests
{
    [TestMethod]
    public async Task GetGameReadsFilterMetadata()
    {
        using var httpClient = new HttpClient(new StoreMessageHandler())
        {
            BaseAddress = new Uri("https://store.steampowered.com/")
        };
        var client = new SteamStoreClient(
            httpClient,
            NullLogger<SteamStoreClient>.Instance);

        var game = await client.GetGameAsync(620, CancellationToken.None);

        Assert.IsNotNull(game);
        Assert.AreEqual("Test & description", game.Description);
        Assert.AreEqual(2011, game.ReleaseYear);
        CollectionAssert.AreEqual(
            new[] { "Valve" },
            game.Developers.ToArray());
        CollectionAssert.AreEqual(
            new[] { "Valve", "Electronic Arts" },
            game.Publishers.ToArray());
        CollectionAssert.AreEqual(
            new[] { "Action", "Adventure" },
            game.Genres.ToArray());
        Assert.IsFalse(game.IsFree);
    }

    private sealed class StoreMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.AreEqual(
                "/api/appdetails?appids=620&l=english&cc=us",
                request.RequestUri?.PathAndQuery);

            const string json = """
                {
                  "620": {
                    "success": true,
                    "data": {
                      "short_description": " <strong>Test</strong> &amp; description ",
                      "developers": [" Valve ", "Valve"],
                      "publishers": ["Valve", "Electronic Arts"],
                      "genres": [
                        { "id": "1", "description": "Action" },
                        { "id": "25", "description": "Adventure" }
                      ],
                      "is_free": false,
                      "release_date": {
                        "coming_soon": false,
                        "date": "18 Apr, 2011"
                      }
                    }
                  }
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    json,
                    System.Text.Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
