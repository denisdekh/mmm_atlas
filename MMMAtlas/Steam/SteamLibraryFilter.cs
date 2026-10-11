using Microsoft.Extensions.Options;

namespace MMMAtlas.Steam;

public sealed class SteamLibraryFilterOptions
{
    public bool RequireGameType { get; set; } = true;
    public bool ExcludeBeta { get; set; } = true;
}

public sealed class SteamLibraryFilter(
    ISteamAppInfoClient appInfoClient,
    ISteamStoreParentClient storeParentClient,
    IOptionsMonitor<SteamLibraryFilterOptions> options,
    ILogger<SteamLibraryFilter> logger)
{
    public async Task<SteamLibraryResult> FilterAsync(
        SteamLibraryResult library,
        CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (library.Availability != SteamLibraryAvailability.Available
            || library.Games.Count == 0
            || (!settings.RequireGameType && !settings.ExcludeBeta))
            return library;

        var parents = settings.RequireGameType
            ? await storeParentClient.GetParentsAsync(library.Games.Select(game => game.AppId), cancellationToken)
            : null;
        if (settings.RequireGameType && (parents is null || library.Games.Any(game => !parents.ContainsKey(game.AppId))))
            return SteamLibraryResult.Unavailable();
        var metadata = settings.ExcludeBeta ? await appInfoClient.GetAppsAsync(
            library.Games.Select(game => game.AppId), cancellationToken) : null;
        if (settings.ExcludeBeta && (metadata is null || library.Games.Any(game =>
                !metadata.TryGetValue(game.AppId, out var info)
                || string.IsNullOrWhiteSpace(info.CommonType))))
        {
            // A partial lookup must not overwrite a previously verified library.
            logger.LogWarning("Steam library filtering was deferred because app classifications are unavailable.");
            return SteamLibraryResult.Unavailable();
        }

        var games = library.Games.Where(game =>
        {
            return (!settings.RequireGameType || !parents![game.AppId])
                && (!settings.ExcludeBeta || !string.Equals(
                    metadata![game.AppId].CommonType, "Beta", StringComparison.OrdinalIgnoreCase));
        }).ToArray();
        logger.LogInformation("Steam library type filters excluded {ExcludedCount} of {OwnedCount} apps.",
            library.Games.Count - games.Length, library.Games.Count);
        return SteamLibraryResult.Available(games);
    }
}
