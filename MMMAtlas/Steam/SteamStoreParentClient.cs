using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace MMMAtlas.Steam;

public interface ISteamStoreParentClient
{
    Task<IReadOnlyDictionary<int, bool>?> GetParentsAsync(IEnumerable<int> appIds, CancellationToken cancellationToken);
}

public sealed class SteamStoreParentClient(IHttpClientFactory clients, IMemoryCache cache,
    ILogger<SteamStoreParentClient> logger) : ISteamStoreParentClient
{
    public async Task<IReadOnlyDictionary<int, bool>?> GetParentsAsync(IEnumerable<int> appIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, bool>();
        var missing = new List<int>();
        foreach (var id in appIds.Distinct())
            if (cache.TryGetValue<bool>((typeof(SteamStoreParentClient), id), out var parent)) result[id] = parent;
            else missing.Add(id);
        try
        {
            foreach (var batch in missing.Chunk(100))
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
                if (batch.Any(id => !result.ContainsKey(id))) return null;
                foreach (var id in batch) cache.Set((typeof(SteamStoreParentClient), id), result[id], TimeSpan.FromHours(24));
            }
            return result;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Steam store parent lookup failed.");
            return null;
        }
    }
}
