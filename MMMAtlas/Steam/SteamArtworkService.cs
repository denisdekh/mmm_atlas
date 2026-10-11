using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using MMMAtlas.Data;
using MMMAtlas.Voting;

namespace MMMAtlas.Steam;

/// <summary>StoreBrowse discovers artwork; the database retains an independent image backup.</summary>
public sealed class SteamArtworkService(
    DatabaseConnection database,
    IHttpClientFactory httpClientFactory,
    ILogger<SteamArtworkService> logger,
    IMemoryCache cache)
{
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task ImportMissingAsync(IEnumerable<int> ownedAppIds, CancellationToken cancellationToken)
    {
        var owned = ownedAppIds.ToHashSet();
        var missing = new List<(long GameId, int AppId)>();
        await using (var connection = new SqliteConnection(database.Value))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT GameId, SteamAppId FROM SteamGames
                WHERE LibraryCardUrl IS NULL OR LibraryCardContent IS NULL;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (owned.Contains(reader.GetInt32(1)))
                    missing.Add((reader.GetInt64(0), reader.GetInt32(1)));
        }

        // Batch discovery and bound downloads rather than issuing one metadata request per card.
        foreach (var batch in missing.Chunk(50))
        {
            var urls = await GetUrlsAsync(batch.Select(game => game.AppId), cancellationToken);
            await Parallel.ForEachAsync(batch, new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = cancellationToken
            }, async (game, token) =>
            {
                if (!urls.TryGetValue(game.AppId, out var assets) || assets.LibraryCardUrl is not { } url) return;
                var gate = gates[(int)(game.GameId % gates.Length)];
                await gate.WaitAsync(token);
                try
                {
                    var cached = await ReadAsync(game.GameId, token);
                    if (cached?.Url is null || cached.Content is null)
                        await DownloadAndSaveAsync(game.GameId, url, token);
                }
                finally { gate.Release(); }
            });
        }
    }

    public async Task<SteamArtwork?> GetAsync(long gameId, CancellationToken cancellationToken)
    {
        var artwork = await ReadAsync(gameId, cancellationToken);
        if (artwork is not null && artwork.Content is null)
        {
            await RefreshAsync(gameId, onlyIfStale: false, cancellationToken);
            artwork = await ReadAsync(gameId, cancellationToken);
        }
        return artwork;
    }

    public Task RefreshIfStaleAsync(long gameId, CancellationToken cancellationToken) =>
        RefreshAsync(gameId, onlyIfStale: true, cancellationToken);

    public Task<SteamArtwork?> ReadCachedAsync(long gameId, CancellationToken cancellationToken) =>
        ReadAsync(gameId, cancellationToken);

    public async Task<SteamStoreThumbnail?> GetStoreThumbnailAsync(long gameId, CancellationToken cancellationToken)
    {
        var key = (typeof(SteamStoreThumbnail), gameId);
        if (cache.TryGetValue<SteamStoreThumbnail?>(key, out var cached)) return cached;
        var gate = gates[(int)((ulong)gameId % (uint)gates.Length)];
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue<SteamStoreThumbnail?>(key, out cached)) return cached;
            var artwork = await ReadAsync(gameId, cancellationToken);
            if (artwork is null) return null;
            SteamStoreThumbnail? thumbnail = null;
            try
            {
                var assets = await GetUrlsAsync([artwork.AppId], cancellationToken);
                if (assets.TryGetValue(artwork.AppId, out var urls) && urls.StoreThumbnailUrl is { } url)
                {
                    var content = await DownloadAsync(url, cancellationToken);
                    if (!JpegDimensions.TryRead(content, out var width, out var height)
                        || width > 1200 || height > 900)
                        throw new HttpRequestException("Steam store thumbnail is not a supported JPEG.");
                    thumbnail = new SteamStoreThumbnail(content, Convert.ToHexString(SHA256.HashData(content)));
                }
            }
            catch (Exception exception) when (IsRemoteFailure(exception, cancellationToken))
            {
                logger.LogWarning(exception, "Steam store thumbnail for GameId {GameId} could not be fetched.", gameId);
            }
            cache.Set(key, thumbnail, thumbnail is null ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(1));
            return thumbnail;
        }
        finally { gate.Release(); }
    }

    private async Task RefreshAsync(long gameId, bool onlyIfStale, CancellationToken cancellationToken)
    {
        var gate = gates[(int)((ulong)gameId % (uint)gates.Length)];
        await gate.WaitAsync(cancellationToken);
        try
        {
            var artwork = await ReadAsync(gameId, cancellationToken);
            if (artwork is null) return;
            if (onlyIfStale && artwork.HashCheckedAtUtc is { } checkedAt
                && checkedAt >= DateTimeOffset.UtcNow.AddDays(-7)) return;
            if (!onlyIfStale && artwork.Url is not null && artwork.Content is not null) return;
            var urls = await GetUrlsAsync([artwork.AppId], cancellationToken);
            if (urls.TryGetValue(artwork.AppId, out var assets) && assets.LibraryCardUrl is { } url)
                await DownloadAndSaveAsync(gameId, url, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task<Dictionary<int, SteamArtworkUrls>> GetUrlsAsync(IEnumerable<int> appIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, SteamArtworkUrls>();
        var requested = appIds.ToHashSet();
        var input = JsonSerializer.Serialize(new
        {
            ids = requested.Select(appId => new { appid = appId }),
            context = new { language = "english", country_code = "US" },
            data_request = new { include_assets = true }
        });
        try
        {
            using var response = await httpClientFactory.CreateClient("SteamStoreBrowse").GetAsync(
                "IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(input), cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            foreach (var item in json.RootElement.GetProperty("response").GetProperty("store_items").EnumerateArray())
            {
                var appId = item.GetProperty("appid").GetInt32();
                if (!requested.Contains(appId)
                    || !item.TryGetProperty("assets", out var assets)) continue;
                result[appId] = new SteamArtworkUrls(
                    ReadAssetUrl(assets, appId, "library_capsule"),
                    ReadAssetUrl(assets, appId, "small_capsule_2x", "small_capsule", "header"));
            }
        }
        catch (Exception exception) when (IsRemoteFailure(exception, cancellationToken))
        {
            logger.LogWarning(exception, "Steam StoreBrowse artwork discovery failed.");
        }
        return result;
    }

    private static string? ReadAssetUrl(JsonElement assets, int appId, params string[] fields)
    {
        if (!assets.TryGetProperty("asset_url_format", out var format)) return null;
        foreach (var field in fields)
        {
            if (!assets.TryGetProperty(field, out var asset)) continue;
            var filename = asset.GetString();
            if (string.IsNullOrWhiteSpace(filename)) continue;
            var path = format.GetString()?.Replace("${FILENAME}", filename);
            if (path is not null && path.StartsWith($"steam/apps/{appId}/", StringComparison.Ordinal)
                && Uri.TryCreate("https://shared.fastly.steamstatic.com/store_item_assets/" + path,
                    UriKind.Absolute, out var url)
                && url.Host == "shared.fastly.steamstatic.com"
                && url.AbsolutePath.StartsWith($"/store_item_assets/steam/apps/{appId}/", StringComparison.Ordinal))
                return url.AbsoluteUri;
        }
        return null;
    }

    private async Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        using var response = await httpClientFactory.CreateClient("SteamLibraryImages").GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > LibraryCard.MaximumContentLength)
            throw new HttpRequestException("Steam artwork exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > LibraryCard.MaximumContentLength)
                throw new HttpRequestException("Steam artwork exceeds the size limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private async Task DownloadAndSaveAsync(long gameId, string url, CancellationToken cancellationToken)
    {
        try
        {
            var content = await DownloadAsync(url, cancellationToken);
            if (!JpegDimensions.TryRead(content, out var width, out var height)
                || width > 600 || height > 900 || (long)width * 3 != (long)height * 2)
                throw new HttpRequestException("Steam library card is not a supported 2:3 JPEG.");
            var hash = Convert.ToHexString(SHA256.HashData(content));
            // Compare inside the write transaction, including migrated legacy bytes. A first
            // successful download is a baseline, not an artwork change.
            await using var connection = new SqliteConnection(database.Value);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var previous = connection.CreateCommand();
            previous.Transaction = transaction;
            previous.CommandText = """
                SELECT steam.LibraryCardHash,
                    COALESCE(steam.LibraryCardContent, atlas.LibraryCardContent)
                FROM SteamGames steam LEFT JOIN GameAtlasIcons atlas ON atlas.GameId = steam.GameId
                WHERE steam.GameId = $gameId;
                """;
            previous.Parameters.AddWithValue("$gameId", gameId);
            string? oldHash = null;
            await using (var reader = await previous.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)) return;
                if (!reader.IsDBNull(0)) oldHash = reader.GetString(0);
                else if (!reader.IsDBNull(1)) oldHash = Convert.ToHexString(SHA256.HashData((byte[])reader.GetValue(1)));
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE SteamGames SET LibraryCardUrl = $url, LibraryCardContent = $content,
                    LibraryCardContentType = 'image/jpeg', LibraryCardHash = $hash,
                    LibraryCardHashCheckedAtUtc = $checkedAt,
                    AtlasIconNeedsUpdate = CASE WHEN $changed THEN 1 ELSE AtlasIconNeedsUpdate END
                WHERE GameId = $gameId;
                """;
            command.Parameters.AddWithValue("$url", url);
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$checkedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$changed", oldHash is not null && oldHash != hash);
            command.Parameters.AddWithValue("$gameId", gameId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (IsRemoteFailure(exception, cancellationToken))
        {
            // Keep the previous URL, backup, hash, timestamp, and crop flag on failure.
            logger.LogWarning(exception, "Steam library artwork for GameId {GameId} could not be refreshed.", gameId);
        }
    }

    private async Task<SteamArtwork?> ReadAsync(long gameId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT steam.SteamAppId, steam.LibraryCardUrl,
                COALESCE(steam.LibraryCardContent, atlas.LibraryCardContent),
                COALESCE(steam.LibraryCardContentType, atlas.LibraryCardContentType, 'image/jpeg'),
                steam.LibraryCardHash, steam.LibraryCardHashCheckedAtUtc, steam.AtlasIconNeedsUpdate
            FROM SteamGames steam LEFT JOIN GameAtlasIcons atlas ON atlas.GameId = steam.GameId
            WHERE steam.GameId = $gameId;
            """;
        command.Parameters.AddWithValue("$gameId", gameId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new SteamArtwork(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            !reader.IsDBNull(5) && DateTimeOffset.TryParse(reader.GetString(5), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var checkedAt) ? checkedAt : null,
            reader.GetBoolean(6));
    }

    private static bool IsRemoteFailure(Exception exception, CancellationToken token) =>
        exception is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
            or IOException || exception is OperationCanceledException && !token.IsCancellationRequested;

    private sealed record SteamArtworkUrls(string? LibraryCardUrl, string? StoreThumbnailUrl);
}

public sealed record SteamStoreThumbnail(byte[] Content, string Hash);

public sealed record SteamArtwork(int AppId, string? Url, byte[]? Content, string ContentType,
    string? Hash, DateTimeOffset? HashCheckedAtUtc, bool AtlasIconNeedsUpdate);
