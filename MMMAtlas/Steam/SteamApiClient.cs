using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MMMAtlas.Steam;

public sealed class SteamApiOptions
{
    public required string ApiKey { get; init; }
}

public sealed class SteamApiClient(
    HttpClient httpClient,
    SteamApiOptions options,
    ILogger<SteamApiClient> logger)
{
    public async Task<SteamProfile?> GetProfileAsync(
        long steamId,
        CancellationToken cancellationToken)
    {
        var apiKey = options.ApiKey;

        var path =
            "ISteamUser/GetPlayerSummaries/v2/"
            + $"?key={Uri.EscapeDataString(apiKey)}&steamids={steamId}";

        try
        {
            using var response = await httpClient.GetAsync(path, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Steam profile request failed with status {StatusCode}.",
                    (int)response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<PlayerEnvelope>(
                cancellationToken);
            var player = payload?.Response?.Players?.SingleOrDefault();

            return player is null
                ? null
                : new SteamProfile(
                    player.PersonaName,
                    player.ProfileUrl,
                    player.AvatarFull);
        }
        catch (Exception exception)
            when (exception is HttpRequestException
                or TaskCanceledException
                or System.Text.Json.JsonException)
        {
            logger.LogWarning(
                exception,
                "Steam profile data could not be fetched.");
            return null;
        }
    }

    public async Task<SteamLibraryResult> GetOwnedGamesAsync(
        long steamId,
        CancellationToken cancellationToken)
    {
        var apiKey = options.ApiKey;

        var path =
            "IPlayerService/GetOwnedGames/v1/"
            + $"?key={Uri.EscapeDataString(apiKey)}"
            + $"&steamid={steamId}"
            + "&include_appinfo=true"
            + "&include_played_free_games=true"
            + "&format=json";

        try
        {
            using var response = await httpClient.GetAsync(path, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden)
            {
                return SteamLibraryResult.Private();
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Steam library request failed with status {StatusCode}.",
                    (int)response.StatusCode);
                return SteamLibraryResult.Unavailable();
            }

            var payload = await response.Content.ReadFromJsonAsync<OwnedGamesEnvelope>(
                cancellationToken);

            // Steam returns an empty response object when game details are private.
            if (payload?.Response?.GameCount is null)
            {
                return SteamLibraryResult.Private();
            }

            var games = payload.Response.Games ?? [];
            return SteamLibraryResult.Available(games.Select(game =>
                new SteamOwnedGame(
                    game.AppId,
                    game.Name,
                    game.PlaytimeForever,
                    game.PlaytimeTwoWeeks,
                    FromUnixTime(game.LastPlayed))));
        }
        catch (Exception exception)
            when (exception is HttpRequestException
                or TaskCanceledException
                or System.Text.Json.JsonException)
        {
            logger.LogWarning(
                exception,
                "Steam library data could not be fetched.");
            return SteamLibraryResult.Unavailable();
        }
    }

    private static DateTimeOffset? FromUnixTime(long? seconds) =>
        seconds is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value)
            : null;

    private sealed record PlayerEnvelope(PlayerResponse? Response);

    private sealed record PlayerResponse(List<Player>? Players);

    private sealed record Player(
        [property: JsonPropertyName("personaname")] string PersonaName,
        [property: JsonPropertyName("profileurl")] string? ProfileUrl,
        [property: JsonPropertyName("avatarfull")] string? AvatarFull);

    private sealed record OwnedGamesEnvelope(OwnedGamesResponse? Response);

    private sealed record OwnedGamesResponse(
        [property: JsonPropertyName("game_count")] int? GameCount,
        [property: JsonPropertyName("games")] List<OwnedGame>? Games);

    private sealed record OwnedGame(
        [property: JsonPropertyName("appid")] int AppId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("playtime_forever")] int PlaytimeForever,
        [property: JsonPropertyName("playtime_2weeks")] int? PlaytimeTwoWeeks,
        [property: JsonPropertyName("rtime_last_played")] long? LastPlayed);
}

public sealed record SteamProfile(
    string PersonaName,
    string? ProfileUrl,
    string? AvatarUrl);

public sealed record SteamOwnedGame(
    int AppId,
    string? Name,
    int PlaytimeForeverMinutes,
    int? PlaytimeTwoWeeksMinutes,
    DateTimeOffset? LastPlayedAtUtc);

public enum SteamLibraryAvailability
{
    Available,
    Private,
    Unavailable
}

public sealed record SteamLibraryResult(
    SteamLibraryAvailability Availability,
    IReadOnlyList<SteamOwnedGame> Games)
{
    public static SteamLibraryResult Available(
        IEnumerable<SteamOwnedGame> games) =>
        new(SteamLibraryAvailability.Available, games.ToArray());

    public static SteamLibraryResult Private() =>
        new(SteamLibraryAvailability.Private, []);

    public static SteamLibraryResult Unavailable() =>
        new(SteamLibraryAvailability.Unavailable, []);
}
