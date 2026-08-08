using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MMMAtlas.Steam;

public interface ISteamStoreClient
{
    Task<SteamStoreGame?> GetGameAsync(
        int appId,
        CancellationToken cancellationToken);
}

public sealed partial class SteamStoreClient(
    HttpClient httpClient,
    ILogger<SteamStoreClient> logger) : ISteamStoreClient
{
    public async Task<SteamStoreGame?> GetGameAsync(
        int appId,
        CancellationToken cancellationToken)
    {
        var path = "api/appdetails"
            + $"?appids={appId}"
            + "&l=english"
            + "&cc=us";

        try
        {
            using var response = await httpClient.GetAsync(
                path,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Steam store request for app {AppId} failed with status {StatusCode}.",
                    appId,
                    (int)response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<
                Dictionary<string, StoreResponse>>(
                    cancellationToken);
            if (payload is null
                || !payload.TryGetValue(appId.ToString(), out var result)
                || !result.Success
                || result.Data is null)
            {
                return null;
            }

            return new SteamStoreGame(
                NormalizeDescription(result.Data.ShortDescription),
                ReadReleaseYear(result.Data.ReleaseDate?.Date));
        }
        catch (Exception exception)
            when (exception is HttpRequestException
                or TaskCanceledException
                or System.Text.Json.JsonException)
        {
            logger.LogWarning(
                exception,
                "Steam store data for app {AppId} could not be fetched.",
                appId);
            return null;
        }
    }

    private static string? NormalizeDescription(string? description)
    {
        var value = description?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var withoutTags = HtmlTagPattern().Replace(value, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        var normalized = WhitespacePattern().Replace(decoded, " ").Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static int? ReadReleaseYear(string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate))
        {
            return null;
        }

        var match = ReleaseYearPattern().Match(releaseDate);
        return match.Success
            && int.TryParse(match.Value, out var year)
            && year is >= 1970 and <= 2100
                ? year
                : null;
    }

    [GeneratedRegex(@"(?<!\d)(?:19[7-9]\d|20\d{2}|2100)(?!\d)")]
    private static partial Regex ReleaseYearPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    private sealed record StoreResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("data")] StoreGameData? Data);

    private sealed record StoreGameData(
        [property: JsonPropertyName("short_description")] string? ShortDescription,
        [property: JsonPropertyName("release_date")] StoreReleaseDate? ReleaseDate);

    private sealed record StoreReleaseDate(
        [property: JsonPropertyName("date")] string? Date);
}

public sealed record SteamStoreGame(
    string? Description,
    int? ReleaseYear);
