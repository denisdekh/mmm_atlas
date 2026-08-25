using System.Globalization;

namespace MMMAtlas.Profiler;

internal enum ProfilerDatabaseMode
{
    Seed,
    Empty,
    Grow,
    Keep
}

internal sealed record ProfilerOptions
{
    public const int DefaultConnections = 100;
    public const double DefaultWriterRatio = 0.10;
    public const double UiAtlasPollSeconds = 45;

    public Uri BaseUri { get; init; } = new("http://localhost:5163");
    public int Connections { get; init; } = DefaultConnections;
    public double WriterRatio { get; init; } = DefaultWriterRatio;
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan RampUp { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ReaderPollInterval { get; init; } =
        TimeSpan.FromSeconds(UiAtlasPollSeconds);
    public double ReaderDetailProbability { get; init; } = 0.20;
    public TimeSpan WriterInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public IReadOnlyList<long> WriterUserIds { get; init; } = [1, 2, 3, 4];
    public string? AccessToken { get; init; }
    public string OutputPath { get; init; } = Path.GetFullPath(
        $"api-profile-{DateTime.Now:yyyyMMdd-HHmmss}.html");
    public int? ServerProcessId { get; init; }
    public bool AllowInvalidCertificate { get; init; }
    public int RandomSeed { get; init; } = 20260822;
    public ProfilerDatabaseMode DatabaseMode { get; init; } =
        ProfilerDatabaseMode.Seed;
    public int TargetGames { get; init; } = 1_000;
    public int InitialGames { get; init; } = 1_000;
    public int TargetUsers { get; init; } = 1_000;
    public int TargetVotes { get; init; } = 5_000;
    public double GameCreationWriterRatio { get; init; } = 0.25;
    public TimeSpan? GameCreationIntervalOverride { get; init; }

    public int WriterClients => WriterRatio <= 0
        ? 0
        : Math.Max(
            1,
            Math.Min(
                Connections,
                (int)Math.Round(
                    Connections * WriterRatio,
                    MidpointRounding.AwayFromZero)));

    public int ReaderClients => Connections - WriterClients;

    public int GameCreatorClients =>
        DatabaseMode != ProfilerDatabaseMode.Grow
        || TargetGames <= InitialGames
        || WriterClients == 0
            ? 0
            : Math.Max(
                1,
                Math.Min(
                    WriterClients,
                    (int)Math.Round(
                        WriterClients * GameCreationWriterRatio,
                        MidpointRounding.AwayFromZero)));

    public int VoteWriterClients => WriterClients - GameCreatorClients;

    public int PrepopulatedGames => DatabaseMode == ProfilerDatabaseMode.Grow
        ? InitialGames
        : TargetGames;

    public TimeSpan GameCreationInterval
    {
        get
        {
            if (GameCreationIntervalOverride is not null)
            {
                return GameCreationIntervalOverride.Value;
            }

            var additions = Math.Max(1, TargetGames - InitialGames);
            var seconds = Duration.TotalSeconds
                * 0.90
                * Math.Max(1, GameCreatorClients)
                / additions;
            return TimeSpan.FromSeconds(Math.Max(0.01, seconds));
        }
    }

    public double ExpectedAtlasPollsPerSecond =>
        ReaderClients / ReaderPollInterval.TotalSeconds;

    public double ExpectedReaderRequestsPerSecond =>
        ExpectedAtlasPollsPerSecond * (1 + (4 * ReaderDetailProbability));

    public static ParseResult Parse(string[] args)
    {
        if (args.Any(value => value is "--help" or "-h" or "/?"))
        {
            return new ParseResult(null, null, true);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var switches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument == "--insecure")
            {
                switches.Add(argument);
                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal)
                || index + 1 >= args.Length
                || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return Error($"Expected a value after '{argument}'.");
            }

            values[argument] = args[++index];
        }

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--base-url", "--connections", "--writer-ratio", "--duration-seconds",
            "--ramp-up-seconds", "--reader-poll-seconds", "--detail-probability",
            "--writer-interval-seconds", "--request-timeout-seconds",
            "--writer-user-ids", "--access-token", "--output", "--server-pid",
            "--seed", "--database-mode", "--initial-games", "--target-games",
            "--target-users", "--target-votes", "--game-writer-ratio",
            "--game-creation-interval-seconds"
        };
        var unknown = values.Keys.FirstOrDefault(key => !known.Contains(key));
        if (unknown is not null)
        {
            return Error($"Unknown option '{unknown}'.");
        }

        try
        {
            var options = new ProfilerOptions
            {
                BaseUri = ReadUri(values, "--base-url", "http://localhost:5163"),
                Connections = ReadInt(values, "--connections", DefaultConnections, 1, 100_000),
                WriterRatio = ReadRatio(values, "--writer-ratio", DefaultWriterRatio),
                Duration = ReadSeconds(values, "--duration-seconds", 120, 1, 86_400),
                RampUp = ReadSeconds(values, "--ramp-up-seconds", 10, 0, 3_600),
                ReaderPollInterval = ReadSeconds(
                    values,
                    "--reader-poll-seconds",
                    UiAtlasPollSeconds,
                    0.1,
                    86_400),
                ReaderDetailProbability = ReadRatio(
                    values,
                    "--detail-probability",
                    0.20),
                WriterInterval = ReadSeconds(
                    values,
                    "--writer-interval-seconds",
                    15,
                    0.1,
                    86_400),
                RequestTimeout = ReadSeconds(
                    values,
                    "--request-timeout-seconds",
                    15,
                    0.1,
                    3_600),
                WriterUserIds = ReadIds(values, "--writer-user-ids", [1, 2, 3, 4]),
                AccessToken = ReadString(values, "--access-token")
                    ?? Environment.GetEnvironmentVariable("MMM_ATLAS_PROFILER_TOKEN"),
                OutputPath = Path.GetFullPath(
                    ReadString(values, "--output")
                    ?? $"api-profile-{DateTime.Now:yyyyMMdd-HHmmss}.html"),
                ServerProcessId = ReadNullableInt(values, "--server-pid", 1, int.MaxValue),
                AllowInvalidCertificate = switches.Contains("--insecure"),
                RandomSeed = ReadInt(values, "--seed", 20260822, int.MinValue, int.MaxValue),
                DatabaseMode = ReadDatabaseMode(values),
                InitialGames = ReadInt(values, "--initial-games", 1_000, 0, 10_000),
                TargetGames = ReadInt(values, "--target-games", 1_000, 0, 10_000),
                TargetUsers = ReadInt(values, "--target-users", 1_000, 0, 10_000),
                TargetVotes = ReadInt(values, "--target-votes", 5_000, 0, 10_000),
                GameCreationWriterRatio = ReadRatio(
                    values,
                    "--game-writer-ratio",
                    0.25),
                GameCreationIntervalOverride = ReadNullableSeconds(
                    values,
                    "--game-creation-interval-seconds",
                    0.01,
                    86_400)
            };

            if (options.RampUp >= options.Duration)
            {
                return Error("--ramp-up-seconds must be less than --duration-seconds.");
            }

            if ((options.WriterClients > 0
                    || options.DatabaseMode != ProfilerDatabaseMode.Keep)
                && string.IsNullOrWhiteSpace(options.AccessToken))
            {
                return Error(
                    "Database setup and writer clients require --access-token or the "
                    + "MMM_ATLAS_PROFILER_TOKEN environment variable.");
            }

            if (options.DatabaseMode == ProfilerDatabaseMode.Empty
                && options.WriterClients > 0)
            {
                return Error(
                    "--database-mode empty requires --writer-ratio 0 because an "
                    + "empty database has no users or games to receive votes.");
            }

            if (options.DatabaseMode is ProfilerDatabaseMode.Seed
                or ProfilerDatabaseMode.Grow)
            {
                if ((long)options.PrepopulatedGames * options.TargetUsers
                    < options.TargetVotes)
                {
                    return Error(
                        "--target-votes cannot exceed prepopulated games × target users.");
                }

                if (options.TargetVotes > 0
                    && (options.PrepopulatedGames == 0 || options.TargetUsers == 0))
                {
                    return Error(
                        "A positive --target-votes value requires games and users.");
                }

                if (options.VoteWriterClients > 0 && options.TargetVotes == 0)
                {
                    return Error(
                        "Writer clients require at least one prepopulated vote so "
                        + "the game appears in the atlas workload.");
                }

                if (options.WriterClients > 0
                    && options.WriterUserIds.Max() > options.TargetUsers)
                {
                    return Error(
                        "--target-users must include every --writer-user-ids value "
                        + "when resetting and seeding the database.");
                }
            }

            if (options.DatabaseMode == ProfilerDatabaseMode.Grow)
            {
                if (options.InitialGames >= options.TargetGames)
                {
                    return Error(
                        "Grow mode requires --initial-games to be smaller than "
                        + "--target-games.");
                }

                if (options.WriterClients == 0
                    || options.GameCreationWriterRatio <= 0)
                {
                    return Error(
                        "Grow mode requires writer traffic and a positive "
                        + "--game-writer-ratio.");
                }
            }

            return new ParseResult(options, null, false);
        }
        catch (ArgumentException exception)
        {
            return Error(exception.Message);
        }
    }

    public static string HelpText => """
        MMM Atlas API profiler

        Usage:
          dotnet run --project MMMAtlas.Profiler -c Test -- [options]

        Workload:
          --base-url URL                  API root (default http://localhost:5163)
          --connections N                 Virtual client connections (default 100)
          --writer-ratio R                Writers as 0..1 or percent, e.g. 0.1 or 10%
          --duration-seconds N            Total run duration (default 120)
          --ramp-up-seconds N             Client start ramp (default 10)
          --reader-poll-seconds N         Atlas poll cadence (default 45, matching UI)
          --detail-probability R          Chance each poll also opens game + votes (default 20%)
          --writer-interval-seconds N     Vote/change cadence per writer (default 15)
          --game-writer-ratio R           Share of writers creating games in grow mode
                                          (default 25%)
          --game-creation-interval-seconds N
                                          Creation cadence; default spreads growth
                                          across roughly 90% of the run
          --writer-user-ids IDS           Seeded user IDs, comma separated (default 1,2,3,4)
          --access-token TOKEN            ApiProfiler access token; env alternative:
                                          MMM_ATLAS_PROFILER_TOKEN

        Dedicated database setup (default: reset and seed):
          --database-mode MODE            seed  Reset, then seed exact targets (default)
                                          empty Reset to a truly empty database
                                          grow  Reset to initial size, then create games
                                                during the measured run
                                          keep  Preserve data without setup
          --initial-games N               Grow-mode starting size (default 1000)
          --target-games N                Final game target (default 1000, maximum 10000)
          --target-users N                Users after setup (default 1000, maximum 10000)
          --target-votes N                Prepopulated unique votes (default 5000, maximum 10000)

        Reporting and transport:
          --output PATH                   Standalone HTML report path
          --server-pid PID                Sample server CPU/memory/thread usage
          --request-timeout-seconds N     Per-request timeout (default 15)
          --seed N                        Deterministic workload seed
          --insecure                      Accept an invalid development HTTPS certificate
          --help                          Show this help

        The default reader load is calculated from the UI's 45-second polling cadence:
        reader clients / 45 = steady atlas polls per second. Occasional detail views add
        game, vote, atlas-icon, and library-card requests on 20% of polls.
        """;

    private static ParseResult Error(string message) => new(null, message, false);

    private static string? ReadString(
        IReadOnlyDictionary<string, string> values,
        string name) => values.TryGetValue(name, out var value) ? value : null;

    private static int ReadInt(
        IReadOnlyDictionary<string, string> values,
        string name,
        int fallback,
        int minimum,
        int maximum)
    {
        var text = ReadString(values, name);
        if (text is null)
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < minimum
            || value > maximum)
        {
            throw new ArgumentException(
                $"{name} must be an integer from {minimum} through {maximum}.");
        }

        return value;
    }

    private static int? ReadNullableInt(
        IReadOnlyDictionary<string, string> values,
        string name,
        int minimum,
        int maximum) => ReadString(values, name) is null
            ? null
            : ReadInt(values, name, minimum, minimum, maximum);

    private static double ReadRatio(
        IReadOnlyDictionary<string, string> values,
        string name,
        double fallback)
    {
        var text = ReadString(values, name);
        if (text is null)
        {
            return fallback;
        }

        var percent = text.EndsWith('%');
        if (percent)
        {
            text = text[..^1];
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"{name} must be a ratio or percentage.");
        }

        value = percent ? value / 100 : value;
        if (value is < 0 or > 1)
        {
            throw new ArgumentException($"{name} must be between 0 and 1 (0% and 100%).");
        }

        return value;
    }

    private static TimeSpan ReadSeconds(
        IReadOnlyDictionary<string, string> values,
        string name,
        double fallback,
        double minimum,
        double maximum)
    {
        var text = ReadString(values, name);
        var value = fallback;
        if (text is not null
            && (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || value < minimum
                || value > maximum))
        {
            throw new ArgumentException($"{name} must be from {minimum} through {maximum}.");
        }

        return TimeSpan.FromSeconds(value);
    }

    private static TimeSpan? ReadNullableSeconds(
        IReadOnlyDictionary<string, string> values,
        string name,
        double minimum,
        double maximum) => ReadString(values, name) is null
            ? null
            : ReadSeconds(values, name, minimum, minimum, maximum);

    private static Uri ReadUri(
        IReadOnlyDictionary<string, string> values,
        string name,
        string fallback)
    {
        var text = ReadString(values, name) ?? fallback;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException($"{name} must be an absolute HTTP or HTTPS URL.");
        }

        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    private static IReadOnlyList<long> ReadIds(
        IReadOnlyDictionary<string, string> values,
        string name,
        IReadOnlyList<long> fallback)
    {
        var text = ReadString(values, name);
        if (text is null)
        {
            return fallback;
        }

        var result = new List<long>();
        foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || id <= 0)
            {
                throw new ArgumentException($"{name} must contain positive comma-separated IDs.");
            }

            result.Add(id);
        }

        return result.Count > 0
            ? result.Distinct().ToArray()
            : throw new ArgumentException($"{name} cannot be empty.");
    }

    private static ProfilerDatabaseMode ReadDatabaseMode(
        IReadOnlyDictionary<string, string> values)
    {
        var text = ReadString(values, "--database-mode") ?? "seed";
        return text.ToLowerInvariant() switch
        {
            "seed" => ProfilerDatabaseMode.Seed,
            "empty" => ProfilerDatabaseMode.Empty,
            "grow" => ProfilerDatabaseMode.Grow,
            "keep" => ProfilerDatabaseMode.Keep,
            _ => throw new ArgumentException(
                "--database-mode must be seed, empty, grow, or keep.")
        };
    }
}

internal sealed record ParseResult(
    ProfilerOptions? Options,
    string? Error,
    bool ShowHelp);
