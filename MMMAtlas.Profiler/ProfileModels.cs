namespace MMMAtlas.Profiler;

internal sealed record RequestMeasurement(
    DateTimeOffset StartedAt,
    string Role,
    string Operation,
    string Method,
    int? StatusCode,
    bool Succeeded,
    double LatencyMilliseconds,
    long ResponseBytes,
    string? Error,
    int? DatabaseGames);

internal sealed record HardwareSample(
    DateTimeOffset SampledAt,
    string Process,
    int ProcessId,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateBytes,
    int ThreadCount);

internal sealed record AggregateVerification(
    int CheckedGames,
    int InconsistentGames,
    IReadOnlyList<AggregateInconsistency> Inconsistencies);

internal sealed record AggregateInconsistency(
    long GameId,
    int? AggregateVoteCount,
    int? ActualVoteCount,
    double? MaximumRatioDelta);

internal sealed record DatabasePreparation(
    string Mode,
    int InitialGames,
    int TargetGames,
    int TargetUsers,
    int TargetVotes,
    int Games,
    int RatedGames,
    int Users,
    int Votes,
    double ElapsedMilliseconds);

internal sealed record DatabaseSnapshot(
    int Games,
    int RatedGames,
    int Users,
    int Votes);

internal sealed record ProfileRun(
    ProfilerOptions Options,
    DatabasePreparation? DatabasePreparation,
    DatabaseSnapshot? FinalDatabase,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<RequestMeasurement> Requests,
    IReadOnlyList<HardwareSample> HardwareSamples,
    AggregateVerification? Verification,
    string? VerificationError,
    string HostDescription);

internal sealed record EndpointSummary(
    string Role,
    string Operation,
    int Requests,
    int Failures,
    double RequestsPerSecond,
    double AverageMilliseconds,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaximumMilliseconds,
    long ResponseBytes);
