using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Caching.Memory;
using SteamKit2;

namespace MMMAtlas.Steam;

public interface ISteamAppInfoClient
{
    // Null means the lookup failed; an absent ID means Steam could not classify it.
    Task<IReadOnlyDictionary<int, SteamAppInfo>?> GetAppsAsync(
        IEnumerable<int> appIds,
        CancellationToken cancellationToken);
}

public sealed record SteamAppInfo(EAppType AppType, string? CommonType)
{
    public static SteamAppInfo FromCommonType(string? commonType)
    {
        var type = commonType?.Trim();
        return new SteamAppInfo(
            Enum.TryParse<EAppType>(type, ignoreCase: true, out var appType)
                && Enum.IsDefined(appType)
                ? appType
                : EAppType.Invalid,
            type);
    }
}

/// <summary>Fetches public PICS app classifications without a user's Steam credentials.</summary>
public sealed class SteamAppInfoClient(
    IMemoryCache cache,
    ILogger<SteamAppInfoClient> logger) : ISteamAppInfoClient, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyDictionary<int, SteamAppInfo>?> GetAppsAsync(
        IEnumerable<int> appIds,
        CancellationToken cancellationToken)
    {
        var requested = appIds.Distinct().ToArray();
        if (requested.Length == 0) return new Dictionary<int, SteamAppInfo>();

        await gate.WaitAsync(cancellationToken);
        try
        {
            var result = new Dictionary<int, SteamAppInfo>();
            var missing = new List<int>();
            foreach (var appId in requested)
            {
                if (cache.TryGetValue<SteamAppInfo>((typeof(SteamAppInfo), appId), out var cached)
                    && cached is not null)
                    result[appId] = cached;
                else
                    missing.Add(appId);
            }

            if (missing.Count == 0) return result;
            var fetched = await FetchAsync(missing, cancellationToken);
            if (fetched is null) return null;

            foreach (var (appId, info) in fetched)
            {
                result[appId] = info;
                // Retry unknown types sooner; app classifications may also change over time.
                cache.Set((typeof(SteamAppInfo), appId), info,
                    info.AppType == EAppType.Invalid
                        ? TimeSpan.FromMinutes(5)
                        : TimeSpan.FromHours(24));
            }
            return result;
        }
        finally { gate.Release(); }
    }

    private async Task<Dictionary<int, SteamAppInfo>?> FetchAsync(
        IReadOnlyList<int> appIds,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var client = new SteamClient(SteamConfiguration.Create(configuration =>
            configuration.WithProtocolTypes(ProtocolTypes.WebSocket)));
        var callbacks = new CallbackManager(client);
        var user = client.GetHandler<SteamUser>()!;
        var apps = client.GetHandler<SteamApps>()!;
        var loggedOn = new TaskCompletionSource<EResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connected = callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => user.LogOnAnonymous());
        using var login = callbacks.Subscribe<SteamUser.LoggedOnCallback>(callback =>
            loggedOn.TrySetResult(callback.Result));
        using var disconnected = callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ => timeout.Cancel());
        var callbackTask = PumpCallbacksAsync(callbacks, token);

        try
        {
            client.Connect();
            var loginResult = await loggedOn.Task.WaitAsync(token);
            if (loginResult != EResult.OK)
                throw new IOException($"Steam anonymous logon failed: {loginResult}.");

            var result = new Dictionary<int, SteamAppInfo>();
            foreach (var batch in appIds.Chunk(100))
            {
                var ids = batch.Select(appId => checked((uint)appId)).ToArray();
                var access = await apps.PICSGetAccessTokens(ids, []).ToTask().WaitAsync(token);
                var requests = ids.Select(id => new SteamApps.PICSRequest(
                    id, access.AppTokens.GetValueOrDefault(id)));
                var products = await apps.PICSGetProductInfo(requests, []).ToTask().WaitAsync(token);
                if (!products.Complete || products.Results is null)
                    throw new IOException("Steam returned incomplete PICS product information.");

                foreach (var response in products.Results)
                {
                    foreach (var (id, product) in response.Apps)
                    {
                        if (!ids.Contains(id)) continue;
                        var commonType = product.KeyValues["common"]["type"].Value;
                        if (!string.IsNullOrWhiteSpace(commonType))
                            result[checked((int)id)] = SteamAppInfo.FromCommonType(commonType);
                    }
                }
            }
            return result;
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && exception is OperationCanceledException or AsyncJobFailedException
                or HttpRequestException or IOException or WebException or SocketException)
        {
            logger.LogWarning(exception, "Steam PICS app classifications could not be fetched.");
            return null;
        }
        finally
        {
            timeout.Cancel();
            client.Disconnect();
            await callbackTask;
        }
    }

    private static async Task PumpCallbacksAsync(CallbackManager callbacks, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
                await callbacks.RunWaitCallbackAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public void Dispose() => gate.Dispose();
}
