using System.Net;
using System.Text.Json;

namespace MMMAtlas.Tests;

internal sealed class ArtworkTestHttpClientFactory : IHttpClientFactory
{
    public int MetadataRequests;
    public int ImageRequests;
    public string? MetadataOverride { get; set; }
    public HttpStatusCode ImageStatus { get; set; } = HttpStatusCode.OK;
    public byte[] ImageContent { get; set; } = [255, 216, 255, 192, 0, 11, 8, 1, 194, 1, 44, 1, 1, 17, 0, 255, 217];
    public string? LastImageUrl { get; private set; }

    public HttpClient CreateClient(string name) => new(new Handler(this, name))
    {
        BaseAddress = new Uri("https://api.steampowered.com/")
    };

    public HttpMessageHandler CreateHandler(string name) => new Handler(this, name);

    private sealed class Handler(ArtworkTestHttpClientFactory owner, string name) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (name == "SteamLibraryImages")
            {
                Interlocked.Increment(ref owner.ImageRequests);
                owner.LastImageUrl = request.RequestUri!.AbsoluteUri;
                return Task.FromResult(new HttpResponseMessage(owner.ImageStatus)
                {
                    Content = new ByteArrayContent(owner.ImageContent)
                });
            }
            Interlocked.Increment(ref owner.MetadataRequests);
            var input = Uri.UnescapeDataString(request.RequestUri!.Query.Split("input_json=")[1]);
            using var json = JsonDocument.Parse(input);
            var payload = owner.MetadataOverride ?? JsonSerializer.Serialize(new
            {
                response = new
                {
                    store_items = json.RootElement.GetProperty("ids").EnumerateArray().Select(id => new
                    {
                        appid = id.GetProperty("appid").GetInt32(),
                        assets = new
                        {
                            asset_url_format = $"steam/apps/{id.GetProperty("appid").GetInt32()}/${{FILENAME}}?t=123",
                            library_capsule = "hash/library_capsule.jpg"
                        }
                    }).ToArray()
                }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });
        }
    }
}
