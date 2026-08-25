using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MMMAtlas.Profiler;

internal sealed class WorkloadRunner(ProfilerOptions options)
{
    private const string AccessTokenHeader = "X-MMM-Atlas-Profiler-Token";
    private readonly ConcurrentQueue<RequestMeasurement> _requests = new();
    private readonly ConcurrentQueue<HardwareSample> _hardwareSamples = new();
    private readonly ConcurrentDictionary<long, byte> _gameIds = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private int _currentGameCount;

    public async Task<ProfileRun> RunAsync()
    {
        using var preflightClient = CreateClient();
        var databasePreparation = await PrepareDatabaseAsync(preflightClient);
        if (databasePreparation is not null)
        {
            _currentGameCount = databasePreparation.Games;
        }
        await DiscoverGameIdsAsync(preflightClient);
        if (databasePreparation is null)
        {
            _currentGameCount = _gameIds.Count;
        }
        if (_gameIds.IsEmpty && options.VoteWriterClients > 0)
        {
            throw new InvalidOperationException(
                "The atlas contains no rated games. Enable DevelopmentData:SeedOnStartup "
                + "or populate the target before starting writer clients.");
        }

        var writerSessions = await CreateWriterSessionsAsync();
        var startedAt = DateTimeOffset.UtcNow;
        using var runCancellation = new CancellationTokenSource(options.Duration);

        var hardwareTask = SampleHardwareAsync(runCancellation.Token);
        var workers = new List<Task>(options.Connections);
        for (var index = 0; index < options.VoteWriterClients; index++)
        {
            var clientIndex = index;
            var session = writerSessions[index];
            workers.Add(RunWriterAsync(
                clientIndex,
                session,
                runCancellation.Token));
        }

        for (var index = 0; index < options.GameCreatorClients; index++)
        {
            var clientIndex = options.VoteWriterClients + index;
            workers.Add(RunGameCreatorAsync(
                clientIndex,
                options.WriterUserIds[index % options.WriterUserIds.Count],
                runCancellation.Token));
        }

        for (var index = 0; index < options.ReaderClients; index++)
        {
            var clientIndex = options.WriterClients + index;
            workers.Add(RunReaderAsync(
                clientIndex,
                runCancellation.Token));
        }

        await Task.WhenAll(workers);
        await hardwareTask;
        var finishedAt = DateTimeOffset.UtcNow;
        DatabaseSnapshot? finalDatabase = null;
        if (!string.IsNullOrWhiteSpace(options.AccessToken))
        {
            try
            {
                finalDatabase = await ReadDatabaseStatusAsync(preflightClient);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"Final database status was unavailable: {exception.Message}");
            }
        }

        AggregateVerification? verification = null;
        string? verificationError = null;
        if (!string.IsNullOrWhiteSpace(options.AccessToken))
        {
            try
            {
                verification = await VerifyAggregatesAsync(preflightClient);
            }
            catch (Exception exception)
            {
                verificationError = exception.Message;
            }
        }

        foreach (var session in writerSessions)
        {
            session.Dispose();
        }

        return new ProfileRun(
            options,
            databasePreparation,
            finalDatabase,
            startedAt,
            finishedAt,
            _requests.OrderBy(item => item.StartedAt).ToArray(),
            _hardwareSamples.OrderBy(item => item.SampledAt).ThenBy(item => item.Process).ToArray(),
            verification,
            verificationError,
            $"{RuntimeInformation.OSDescription}; {RuntimeInformation.FrameworkDescription}; "
            + $"{RuntimeInformation.ProcessArchitecture}; {Environment.ProcessorCount} logical processors");
    }

    private async Task<DatabasePreparation?> PrepareDatabaseAsync(HttpClient client)
    {
        if (options.DatabaseMode == ProfilerDatabaseMode.Keep)
        {
            return null;
        }

        var mode = options.DatabaseMode switch
        {
            ProfilerDatabaseMode.Empty => "empty",
            ProfilerDatabaseMode.Grow => "grow",
            _ => "seed"
        };
        var requestBody = new DatabasePreparationRequest(
            mode,
            options.PrepopulatedGames,
            options.TargetUsers,
            options.TargetVotes,
            options.RandomSeed);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "api/profiler/database/prepare")
        {
            Content = JsonContent.Create(requestBody, options: _jsonOptions)
        };
        request.Headers.Add(AccessTokenHeader, options.AccessToken);

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        stopwatch.Stop();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Profiler database setup returned HTTP {(int)response.StatusCode}: "
                + (string.IsNullOrWhiteSpace(content) ? "no response body" : content));
        }

        var snapshot = JsonSerializer.Deserialize<DatabaseSnapshot>(
            content,
            _jsonOptions)
            ?? throw new InvalidOperationException(
                "Profiler database setup returned no data.");
        Console.WriteLine(
            $"Dedicated database ready in {stopwatch.Elapsed.TotalMilliseconds:0.##} ms: "
            + $"{snapshot.Games:N0} games ({snapshot.RatedGames:N0} rated), "
            + $"{snapshot.Users:N0} users, {snapshot.Votes:N0} votes.");
        return new DatabasePreparation(
            mode,
            options.DatabaseMode == ProfilerDatabaseMode.Grow
                ? options.InitialGames
                : options.DatabaseMode == ProfilerDatabaseMode.Empty
                    ? 0
                    : options.TargetGames,
            options.DatabaseMode == ProfilerDatabaseMode.Empty ? 0 : options.TargetGames,
            options.DatabaseMode == ProfilerDatabaseMode.Empty ? 0 : options.TargetUsers,
            options.DatabaseMode == ProfilerDatabaseMode.Empty ? 0 : options.TargetVotes,
            snapshot.Games,
            snapshot.RatedGames,
            snapshot.Users,
            snapshot.Votes,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    private async Task DiscoverGameIdsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("api/games/atlas");
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Preflight GET /api/games/atlas returned HTTP {(int)response.StatusCode}.");
        }

        var games = await response.Content.ReadFromJsonAsync<AtlasGame[]>(_jsonOptions)
            ?? [];
        foreach (var game in games)
        {
            _gameIds.TryAdd(game.GameId, 0);
        }
    }

    private async Task<IReadOnlyList<WriterSession>> CreateWriterSessionsAsync()
    {
        if (options.WriterClients == 0)
        {
            return [];
        }

        var tasks = Enumerable.Range(0, options.VoteWriterClients)
            .Select(async index =>
            {
                var userId = options.WriterUserIds[index % options.WriterUserIds.Count];
                var client = CreateClient();
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"api/profiler/session/{userId}");
                request.Headers.Add(AccessTokenHeader, options.AccessToken);
                using var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    client.Dispose();
                    throw new InvalidOperationException(
                        $"Could not start writer session for user {userId}: "
                        + $"HTTP {(int)response.StatusCode}. Ensure this is a Development "
                        + "or Test build with ApiProfiler enabled and matching access token.");
                }

                var session = await response.Content.ReadFromJsonAsync<SessionResponse>(_jsonOptions)
                    ?? throw new InvalidOperationException(
                        "The profiler session endpoint returned an empty response.");
                return new WriterSession(
                    client,
                    userId,
                    session.AntiforgeryHeaderName,
                    session.AntiforgeryRequestToken);
            });

        return await Task.WhenAll(tasks);
    }

    private async Task RunReaderAsync(
        int clientIndex,
        CancellationToken cancellationToken)
    {
        await DelayRampAsync(clientIndex, cancellationToken);
        using var client = CreateClient();
        var random = new Random(unchecked(options.RandomSeed * 397) ^ clientIndex);

        while (!cancellationToken.IsCancellationRequested)
        {
            await MeasureAsync(
                client,
                "reader",
                "atlas_poll",
                () => new HttpRequestMessage(HttpMethod.Get, "api/games/atlas"),
                cancellationToken);

            var gameId = SelectGameId(random);
            if (gameId is not null
                && random.NextDouble() < options.ReaderDetailProbability)
            {
                await MeasureAsync(
                    client,
                    "reader",
                    "game_detail",
                    () => new HttpRequestMessage(HttpMethod.Get, $"api/games/{gameId.Value}"),
                    cancellationToken);
                await MeasureAsync(
                    client,
                    "reader",
                    "public_votes",
                    () => new HttpRequestMessage(
                        HttpMethod.Get,
                        $"api/games/{gameId.Value}/votes?publicOnly=true&page=1&pageSize=20"),
                    cancellationToken);
                await MeasureAsync(
                    client,
                    "reader",
                    "atlas_icon",
                    () => new HttpRequestMessage(
                        HttpMethod.Get,
                        $"api/games/{gameId.Value}/atlas-icon"),
                    cancellationToken);
                await MeasureAsync(
                    client,
                    "reader",
                    "library_card",
                    () => new HttpRequestMessage(
                        HttpMethod.Get,
                        $"api/games/{gameId.Value}/library-card"),
                    cancellationToken);
            }

            if (!await DelayAsync(options.ReaderPollInterval, cancellationToken))
            {
                return;
            }
        }
    }

    private async Task RunWriterAsync(
        int clientIndex,
        WriterSession session,
        CancellationToken cancellationToken)
    {
        await DelayRampAsync(clientIndex, cancellationToken);
        var random = new Random(unchecked(options.RandomSeed * 7919) ^ clientIndex);

        while (!cancellationToken.IsCancellationRequested)
        {
            var gameId = SelectGameId(random);
            if (gameId is null)
            {
                if (!await DelayAsync(options.WriterInterval, cancellationToken))
                {
                    return;
                }
                continue;
            }
            var micro = random.Next(1, 101) / 10m;
            var meso = random.Next(1, 101) / 10m;
            var macro = random.Next(1, 101) / 10m;
            var fields = new Dictionary<string, string>
            {
                ["microScore"] = micro.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["mesoScore"] = meso.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["macroScore"] = macro.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["isPublic"] = random.NextDouble() < 0.75 ? "true" : "false"
            };

            await MeasureAsync(
                session.Client,
                "writer",
                "vote_submit_or_change",
                () =>
                {
                    var request = new HttpRequestMessage(
                        HttpMethod.Post,
                        $"api/me/votes/{gameId.Value}")
                    {
                        Content = new FormUrlEncodedContent(fields)
                    };
                    request.Headers.Add(
                        session.AntiforgeryHeaderName,
                        session.AntiforgeryRequestToken);
                    return request;
                },
                cancellationToken,
                ResolveVoteOperation);

            if (!await DelayAsync(options.WriterInterval, cancellationToken))
            {
                return;
            }
        }
    }

    private async Task RunGameCreatorAsync(
        int clientIndex,
        long userId,
        CancellationToken cancellationToken)
    {
        await DelayRampAsync(clientIndex, cancellationToken);
        using var client = CreateClient();

        while (!cancellationToken.IsCancellationRequested)
        {
            var body = new GameCreationRequest(
                options.TargetGames,
                userId,
                options.RandomSeed);
            var content = await MeasureAsync(
                client,
                "writer",
                "game_create",
                () =>
                {
                    var request = new HttpRequestMessage(
                        HttpMethod.Post,
                        "api/profiler/games")
                    {
                        Content = JsonContent.Create(body, options: _jsonOptions)
                    };
                    request.Headers.Add(AccessTokenHeader, options.AccessToken);
                    return request;
                },
                cancellationToken,
                ResolveGameCreationOperation);
            if (content is not null)
            {
                try
                {
                    var result = JsonSerializer.Deserialize<GameCreationResponse>(
                        content,
                        _jsonOptions);
                    if (result?.GameId is long gameId)
                    {
                        _gameIds.TryAdd(gameId, 0);
                    }
                    if (result is not null)
                    {
                        Interlocked.Exchange(
                            ref _currentGameCount,
                            result.GameCount);
                    }
                    if (result?.TargetReached == true)
                    {
                        return;
                    }
                }
                catch (JsonException)
                {
                    // The request measurement retains the raw success/failure;
                    // a malformed success response will be visible as creators
                    // continuing until the run timeout.
                }
            }

            if (!await DelayAsync(options.GameCreationInterval, cancellationToken))
            {
                return;
            }
        }
    }

    private long? SelectGameId(Random random)
    {
        var ids = _gameIds.Keys.ToArray();
        return ids.Length == 0 ? null : ids[random.Next(ids.Length)];
    }

    private async Task<byte[]?> MeasureAsync(
        HttpClient client,
        string role,
        string operation,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken,
        Func<byte[], string>? successOperation = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        int? statusCode = null;
        var succeeded = false;
        long responseBytes = 0;
        string? error = null;
        string method = "UNKNOWN";
        byte[]? responseContent = null;

        try
        {
            using var request = requestFactory();
            method = request.Method.Method;
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            statusCode = (int)response.StatusCode;
            responseContent = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            responseBytes = responseContent.LongLength;
            succeeded = response.IsSuccessStatusCode;
            if (succeeded && successOperation is not null)
            {
                operation = successOperation(responseContent);
            }
            else if (!succeeded)
            {
                var body = System.Text.Encoding.UTF8.GetString(responseContent);
                error = string.IsNullOrWhiteSpace(body)
                    ? $"HTTP {statusCode}"
                    : body.Length <= 300 ? body : body[..300];
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            error = exception is TaskCanceledException
                ? $"Request timed out after {options.RequestTimeout.TotalSeconds:0.###} seconds"
                : exception.Message;
        }
        finally
        {
            stopwatch.Stop();
        }

        _requests.Enqueue(new RequestMeasurement(
            startedAt,
            role,
            operation,
            method,
            statusCode,
            succeeded,
            stopwatch.Elapsed.TotalMilliseconds,
            responseBytes,
            error,
            Volatile.Read(ref _currentGameCount)));
        return succeeded ? responseContent : null;
    }

    private static string ResolveVoteOperation(byte[] content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.GetProperty("amended").GetBoolean()
                ? "vote_change"
                : "vote_submit";
        }
        catch (JsonException)
        {
            return "vote_submit_or_change";
        }
    }

    private static string ResolveGameCreationOperation(byte[] content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.GetProperty("created").GetBoolean()
                ? "game_create"
                : "game_target_reached";
        }
        catch (JsonException)
        {
            return "game_create";
        }
    }

    private async Task DelayRampAsync(int clientIndex, CancellationToken cancellationToken)
    {
        if (options.RampUp <= TimeSpan.Zero || options.Connections <= 1)
        {
            return;
        }

        var fraction = clientIndex / (double)(options.Connections - 1);
        await DelayAsync(options.RampUp * fraction, cancellationToken);
    }

    private static async Task<bool> DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task SampleHardwareAsync(CancellationToken cancellationToken)
    {
        var processIds = new List<(string Name, int Id)>
        {
            ("load generator", Environment.ProcessId)
        };
        if (options.ServerProcessId is int serverId && serverId != Environment.ProcessId)
        {
            processIds.Add(("API server", serverId));
        }

        var samplers = new List<ProcessSampler>();
        foreach (var (name, id) in processIds)
        {
            try
            {
                samplers.Add(new ProcessSampler(name, Process.GetProcessById(id)));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Hardware sampling skipped for PID {id}: {exception.Message}");
            }
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var sampler in samplers)
                {
                    var sample = sampler.Sample();
                    if (sample is not null)
                    {
                        _hardwareSamples.Enqueue(sample);
                    }
                }

                if (!await DelayAsync(TimeSpan.FromSeconds(1), cancellationToken))
                {
                    break;
                }
            }
        }
        finally
        {
            foreach (var sampler in samplers)
            {
                sampler.Dispose();
            }
        }
    }

    private async Task<AggregateVerification> VerifyAggregatesAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/profiler/verify");
        request.Headers.Add(AccessTokenHeader, options.AccessToken);
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Aggregate verification returned HTTP {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<AggregateVerification>(_jsonOptions)
            ?? throw new InvalidOperationException("Aggregate verification returned no data.");
    }

    private async Task<DatabaseSnapshot> ReadDatabaseStatusAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "api/profiler/database/status");
        request.Headers.Add(AccessTokenHeader, options.AccessToken);
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Database status returned HTTP {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<DatabaseSnapshot>(_jsonOptions)
            ?? throw new InvalidOperationException("Database status returned no data.");
    }

    private HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 1,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        if (options.AllowInvalidCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        return new HttpClient(handler)
        {
            BaseAddress = options.BaseUri,
            Timeout = options.RequestTimeout
        };
    }

    private sealed record AtlasGame(long GameId);

    private sealed record DatabasePreparationRequest(
        string Mode,
        int Games,
        int Users,
        int Votes,
        int RandomSeed);

    private sealed record GameCreationRequest(
        int TargetGames,
        long UserId,
        int RandomSeed);

    private sealed record GameCreationResponse(
        bool Created,
        bool TargetReached,
        long? GameId,
        int GameCount);

    private sealed record SessionResponse(
        string AntiforgeryHeaderName,
        string AntiforgeryRequestToken);

    private sealed record WriterSession(
        HttpClient Client,
        long UserId,
        string AntiforgeryHeaderName,
        string AntiforgeryRequestToken) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private sealed class ProcessSampler(string name, Process process) : IDisposable
    {
        private TimeSpan _lastCpu = process.TotalProcessorTime;
        private DateTimeOffset _lastTime = DateTimeOffset.UtcNow;

        public HardwareSample? Sample()
        {
            try
            {
                process.Refresh();
                var now = DateTimeOffset.UtcNow;
                var cpu = process.TotalProcessorTime;
                var elapsed = (now - _lastTime).TotalMilliseconds;
                var cpuPercent = elapsed <= 0
                    ? 0
                    : (cpu - _lastCpu).TotalMilliseconds
                        / elapsed
                        / Environment.ProcessorCount
                        * 100;
                _lastCpu = cpu;
                _lastTime = now;
                return new HardwareSample(
                    now,
                    name,
                    process.Id,
                    Math.Max(0, cpuPercent),
                    process.WorkingSet64,
                    process.PrivateMemorySize64,
                    process.Threads.Count);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public void Dispose() => process.Dispose();
    }
}
