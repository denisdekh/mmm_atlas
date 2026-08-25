using MMMAtlas.Profiler;

#if ATLAS_BUILD_PRODUCTION
Console.Error.WriteLine(
    "MMMAtlas.Profiler is disabled in Production builds. "
    + "Run it with -c Development or -c Test.");
return 2;
#else
var parsed = ProfilerOptions.Parse(args);
if (parsed.ShowHelp)
{
    Console.WriteLine(ProfilerOptions.HelpText);
    return 0;
}

if (parsed.Error is not null || parsed.Options is null)
{
    Console.Error.WriteLine($"Error: {parsed.Error}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(ProfilerOptions.HelpText);
    return 2;
}

var options = parsed.Options;
Console.WriteLine(
    $"Profiling {options.BaseUri} with {options.Connections} clients "
    + $"({options.VoteWriterClients} voters, {options.GameCreatorClients} game creators, "
    + $"{options.ReaderClients} readers)." );
if (options.GameCreatorClients > 0)
{
    Console.WriteLine(
        $"Database growth: {options.InitialGames:N0} to {options.TargetGames:N0} games "
        + $"at {options.GameCreationInterval.TotalSeconds:0.###}s per creator request.");
}
Console.WriteLine(
    $"UI-equivalent atlas load: {options.ReaderClients} / "
    + $"{options.ReaderPollInterval.TotalSeconds:0.###}s = "
    + $"{options.ExpectedAtlasPollsPerSecond:0.###} polls/s; "
    + $"expected total reader traffic {options.ExpectedReaderRequestsPerSecond:0.###} requests/s.");

try
{
    var runner = new WorkloadRunner(options);
    var run = await runner.RunAsync();
    await HtmlReportWriter.WriteAsync(run, options.OutputPath);

    var failures = run.Requests.Count(request => !request.Succeeded);
    var failureRate = run.Requests.Count == 0
        ? 0
        : failures * 100d / run.Requests.Count;
    Console.WriteLine(
        $"Completed {run.Requests.Count:N0} requests with "
        + $"{failureRate:0.##}% failures.");
    if (run.FinalDatabase is not null)
    {
        Console.WriteLine(
            $"Final database: {run.FinalDatabase.Games:N0} games, "
            + $"{run.FinalDatabase.Users:N0} users, {run.FinalDatabase.Votes:N0} votes.");
    }
    Console.WriteLine($"HTML report: {options.OutputPath}");

    var growthTargetReached = options.DatabaseMode != ProfilerDatabaseMode.Grow
        || run.FinalDatabase?.Games >= options.TargetGames;
    if (!growthTargetReached)
    {
        Console.Error.WriteLine(
            $"Growth target was not reached: {run.FinalDatabase?.Games ?? 0:N0} "
            + $"of {options.TargetGames:N0} games. Increase duration, creator share, "
            + "or game-creation cadence.");
    }

    return failures == 0
        && run.Verification?.InconsistentGames is not > 0
        && growthTargetReached
        ? 0
        : 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Profiler failed: {exception.Message}");
    return 2;
}
#endif
