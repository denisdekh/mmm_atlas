using System.Text.Json;

namespace MMMAtlas.Steam;

public interface ISteamStoreParentClient
{
    Task<IReadOnlyDictionary<int, bool>?> GetParentsAsync(IEnumerable<int> appIds, CancellationToken cancellationToken);
}

public sealed class SteamStoreParentClient(IHttpClientFactory clients,
    ILogger<SteamStoreParentClient> logger) : ISteamStoreParentClient
{
    public async Task<IReadOnlyDictionary<int, bool>?> GetParentsAsync(IEnumerable<int> appIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, bool>();
        var requested = appIds.Distinct().ToArray();
        try
        {
            foreach (var batch in requested.Chunk(100))
            {
                var input = JsonSerializer.Serialize(new {
                    ids = batch.Select(id => new { appid = id }),
                    context = new { language = "english", country_code = "US" },
                    data_request = new { include_related_items = true }
                });
                using var response = await clients.CreateClient("SteamStoreBrowse").GetAsync(
                    "IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(input), cancellationToken);
                response.EnsureSuccessStatusCode();
                using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var items = payload.RootElement.GetProperty("response").GetProperty("store_items");
                foreach (var item in items.EnumerateArray())
                {
                    var id = item.GetProperty("appid").GetInt32();
                    if (!batch.Contains(id)) continue;
                    if (item.TryGetProperty("success", out var success) && success.GetInt32() != 1) continue;
                    result[id] = item.TryGetProperty("related_items", out var related)
                        && related.ValueKind == JsonValueKind.Object
                        && related.TryGetProperty("parent_appid", out var parent)
                        && parent.ValueKind != JsonValueKind.Null;
                }
            }
            return result.Count > 0 || requested.Length == 0 ? result : null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Steam store parent lookup failed.");
            return result.Count > 0 ? result : null;
        }
    }
}
