using System.Globalization;
using System.Net;
using System.Text;

namespace MMMAtlas.Profiler;

internal static class HtmlReportWriter
{
    public static async Task WriteAsync(ProfileRun run, string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(
            outputPath,
            Build(run),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string Build(ProfileRun run)
    {
        var requests = run.Requests;
        var elapsedSeconds = Math.Max(0.001, (run.FinishedAt - run.StartedAt).TotalSeconds);
        var failures = requests.Count(request => !request.Succeeded);
        var successRate = requests.Count == 0 ? 0 : 100d * (requests.Count - failures) / requests.Count;
        var failureRate = requests.Count == 0 ? 0 : 100d * failures / requests.Count;
        var latencies = requests.Select(request => request.LatencyMilliseconds).Order().ToArray();
        var summaries = requests
            .GroupBy(request => (request.Role, request.Operation))
            .Select(group => Summarize(group.Key.Role, group.Key.Operation, group.ToArray(), elapsedSeconds))
            .OrderBy(summary => summary.Role)
            .ThenBy(summary => summary.Operation)
            .ToArray();
        var timeline = BuildTimeline(run);

        var html = new StringBuilder(32_768);
        html.Append("""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>MMM Atlas API profile</title>
            <style>
            :root{color-scheme:dark;--bg:#09111e;--panel:#101c2d;--panel2:#14243a;--ink:#edf5ff;--muted:#9eb0c7;--line:#293c55;--cyan:#65d6e7;--amber:#f5bd63;--red:#ff7979;--green:#69d6a3}
            *{box-sizing:border-box}body{margin:0;background:radial-gradient(circle at 20% 0,#132c42 0,transparent 38%),var(--bg);color:var(--ink);font:15px/1.5 system-ui,-apple-system,Segoe UI,sans-serif}
            main{width:min(1180px,calc(100% - 32px));margin:0 auto;padding:42px 0 64px}h1{font-size:clamp(2rem,5vw,3.5rem);line-height:1;margin:0 0 10px;letter-spacing:-.04em}h2{margin:34px 0 14px;font-size:1.35rem}p{color:var(--muted)}code{color:var(--cyan)}
            .eyebrow{text-transform:uppercase;letter-spacing:.14em;color:var(--cyan);font-weight:700;font-size:.76rem}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px;margin:24px 0}.card,.panel{background:linear-gradient(145deg,var(--panel2),var(--panel));border:1px solid var(--line);border-radius:14px;box-shadow:0 16px 36px #0004}.card{padding:16px}.card strong{display:block;font-size:1.65rem;line-height:1.2;margin-top:5px}.card span{color:var(--muted);font-size:.82rem}.good{color:var(--green)!important}.warn{color:var(--amber)!important}.bad{color:var(--red)!important}.panel{padding:18px;margin:14px 0;overflow:auto}.split{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:14px}
            table{border-collapse:collapse;width:100%;white-space:nowrap}th,td{padding:10px 12px;border-bottom:1px solid var(--line);text-align:right}th{color:var(--muted);font-size:.76rem;text-transform:uppercase;letter-spacing:.06em}th:first-child,td:first-child,th:nth-child(2),td:nth-child(2){text-align:left}tr:last-child td{border-bottom:0}.chart{width:100%;height:210px;display:block}.chart-grid{stroke:var(--line);stroke-width:1}.chart-line{fill:none;stroke:var(--cyan);stroke-width:2;vector-effect:non-scaling-stroke}.chart-line.secondary{stroke:var(--amber)}.chart-fill{fill:#65d6e719}.chart-label{fill:var(--muted);font-size:11px}.kv{display:grid;grid-template-columns:minmax(140px,220px) 1fr;gap:8px 16px}.kv div:nth-child(odd){color:var(--muted)}.assessment{border-left:4px solid var(--cyan)}details{margin-top:10px}summary{cursor:pointer;color:var(--cyan)}footer{margin-top:34px;color:var(--muted);font-size:.82rem}@media(max-width:640px){main{width:min(100% - 20px,1180px)}.kv{grid-template-columns:1fr}.kv div:nth-child(even){margin-bottom:8px}}
            </style>
            </head>
            <body><main>
            <div class="eyebrow">Scalability profile</div>
            <h1>MMM Atlas API</h1>
            """);
        html.Append("<p>").Append(E($"{run.StartedAt:u} · {run.Options.BaseUri} · {run.HostDescription}"))
            .AppendLine("</p>");

        html.AppendLine("<section class=\"cards\">");
        Card(html, "Requests", requests.Count.ToString("N0"), $"{requests.Count / elapsedSeconds:0.##} requests/s");
        Card(html, "Failure rate", $"{failureRate:0.###}%", $"{failures:N0} failed", failureRate == 0 ? "good" : failureRate <= 1 ? "warn" : "bad");
        Card(html, "Latency p50", FormatMilliseconds(Percentile(latencies, 0.50)), "all operations");
        Card(html, "Latency p95", FormatMilliseconds(Percentile(latencies, 0.95)), "all operations", Percentile(latencies, 0.95) <= 500 ? "good" : "warn");
        Card(html, "Latency p99", FormatMilliseconds(Percentile(latencies, 0.99)), "all operations");
        Card(
            html,
            "Clients",
            run.Options.Connections.ToString("N0"),
            $"{run.Options.VoteWriterClients} voters / "
            + $"{run.Options.GameCreatorClients} creators / "
            + $"{run.Options.ReaderClients} readers");
        if (run.DatabasePreparation is not null)
        {
            Card(
                html,
                "Database size",
                $"{run.FinalDatabase?.Games ?? run.DatabasePreparation.Games:N0} games",
                $"started at {run.DatabasePreparation.Games:N0}; "
                + $"target {run.DatabasePreparation.TargetGames:N0}");
        }
        html.AppendLine("</section>");

        html.AppendLine("<section class=\"panel assessment\"><h2>Scalability signal</h2>");
        html.Append("<p>").Append(E(Assessment(run, failureRate, Percentile(latencies, 0.95)))).AppendLine("</p></section>");

        html.AppendLine("<h2>Workload configuration</h2><section class=\"panel kv\">");
        KeyValue(html, "Target", run.Options.BaseUri.ToString());
        KeyValue(html, "Duration / ramp", $"{run.Options.Duration.TotalSeconds:0.###}s / {run.Options.RampUp.TotalSeconds:0.###}s");
        KeyValue(
            html,
            "Virtual clients",
            $"{run.Options.Connections:N0} ({run.Options.VoteWriterClients:N0} voters, "
            + $"{run.Options.GameCreatorClients:N0} game creators, "
            + $"{run.Options.ReaderClients:N0} readers)");
        KeyValue(html, "Writer ratio", $"{run.Options.WriterRatio:P1}");
        KeyValue(html, "Reader cadence", $"{run.Options.ReaderPollInterval.TotalSeconds:0.###}s between atlas polls");
        KeyValue(html, "Calculated atlas load", $"{run.Options.ReaderClients:N0} readers ÷ {run.Options.ReaderPollInterval.TotalSeconds:0.###}s = {run.Options.ExpectedAtlasPollsPerSecond:0.###} polls/s");
        KeyValue(html, "Expected read traffic", $"{run.Options.ExpectedReaderRequestsPerSecond:0.###} requests/s including {run.Options.ReaderDetailProbability:P0} detail-view probability");
        KeyValue(html, "Writer cadence", $"{run.Options.WriterInterval.TotalSeconds:0.###}s between submissions/changes per writer");
        KeyValue(html, "Request timeout", $"{run.Options.RequestTimeout.TotalSeconds:0.###}s");
        KeyValue(html, "Seed", run.Options.RandomSeed.ToString(CultureInfo.InvariantCulture));
        KeyValue(
            html,
            "Dedicated database mode",
            run.Options.DatabaseMode.ToString().ToLowerInvariant());
        if (run.DatabasePreparation is not null)
        {
            KeyValue(
                html,
                "Database targets",
                $"{run.DatabasePreparation.InitialGames:N0} initial → "
                + $"{run.DatabasePreparation.TargetGames:N0} target games, "
                + $"{run.DatabasePreparation.TargetUsers:N0} users, "
                + $"{run.DatabasePreparation.TargetVotes:N0} votes");
            KeyValue(
                html,
                "Database after setup",
                $"{run.DatabasePreparation.Games:N0} games "
                + $"({run.DatabasePreparation.RatedGames:N0} rated), "
                + $"{run.DatabasePreparation.Users:N0} users, "
                + $"{run.DatabasePreparation.Votes:N0} votes; "
                + $"prepared in {FormatMilliseconds(run.DatabasePreparation.ElapsedMilliseconds)}");
            if (run.Options.GameCreatorClients > 0)
            {
                KeyValue(
                    html,
                    "Game creation cadence",
                    $"{run.Options.GameCreationInterval.TotalSeconds:0.###}s per creator; "
                    + $"{run.Options.GameCreationWriterRatio:P0} of writer clients");
            }
            if (run.FinalDatabase is not null)
            {
                KeyValue(
                    html,
                    "Database after workload",
                    $"{run.FinalDatabase.Games:N0} games "
                    + $"({run.FinalDatabase.RatedGames:N0} rated), "
                    + $"{run.FinalDatabase.Users:N0} users, "
                    + $"{run.FinalDatabase.Votes:N0} votes");
            }
        }
        html.AppendLine("</section>");

        html.AppendLine("<h2>Traffic over time</h2><section class=\"split\">");
        html.AppendLine("<div class=\"panel\"><div class=\"eyebrow\">Requests per second</div>");
        AppendChart(html, timeline.Select(point => (double)point.Requests).ToArray(), null, "req/s");
        html.AppendLine("</div><div class=\"panel\"><div class=\"eyebrow\">p95 latency and failures</div>");
        AppendChart(
            html,
            timeline.Select(point => point.P95Milliseconds).ToArray(),
            timeline.Select(point => (double)point.Failures).ToArray(),
            "ms / failures");
        html.AppendLine("</div></section>");

        if (run.Options.DatabaseMode == ProfilerDatabaseMode.Grow)
        {
            html.AppendLine("<h2>Performance while the database grows</h2><section class=\"split\">");
            html.AppendLine("<div class=\"panel\"><div class=\"eyebrow\">Game count over time</div>");
            AppendChart(
                html,
                timeline.Select(point => (double)point.DatabaseGames).ToArray(),
                null,
                "games");
            html.AppendLine("</div><div class=\"panel\">");
            AppendDatabaseSizeTable(html, run);
            html.AppendLine("</div></section>");
        }

        html.AppendLine("<h2>Endpoint metrics</h2><section class=\"panel\"><table><thead><tr><th>Role</th><th>Operation</th><th>Requests</th><th>Failures</th><th>RPS</th><th>Average</th><th>p50</th><th>p95</th><th>p99</th><th>Max</th><th>Response</th></tr></thead><tbody>");
        foreach (var summary in summaries)
        {
            html.Append("<tr><td>").Append(E(summary.Role)).Append("</td><td>")
                .Append(E(summary.Operation)).Append("</td><td>").Append(summary.Requests.ToString("N0"))
                .Append("</td><td class=\"").Append(summary.Failures == 0 ? "good" : "bad").Append("\">")
                .Append(summary.Failures.ToString("N0")).Append("</td><td>").Append(summary.RequestsPerSecond.ToString("0.###"))
                .Append("</td><td>").Append(FormatMilliseconds(summary.AverageMilliseconds))
                .Append("</td><td>").Append(FormatMilliseconds(summary.P50Milliseconds))
                .Append("</td><td>").Append(FormatMilliseconds(summary.P95Milliseconds))
                .Append("</td><td>").Append(FormatMilliseconds(summary.P99Milliseconds))
                .Append("</td><td>").Append(FormatMilliseconds(summary.MaximumMilliseconds))
                .Append("</td><td>").Append(FormatBytes(summary.ResponseBytes)).AppendLine("</td></tr>");
        }
        html.AppendLine("</tbody></table></section>");

        AppendVerification(html, run);
        AppendHardware(html, run);
        AppendFailures(html, requests);

        html.Append("<footer>Generated at ").Append(E(DateTimeOffset.UtcNow.ToString("u")))
            .Append(". Success rate: ").Append(successRate.ToString("0.###"))
            .Append("%. The report is self-contained and can be archived or shared as one HTML file.</footer>")
            .AppendLine("</main></body></html>");
        return html.ToString();
    }

    private static EndpointSummary Summarize(
        string role,
        string operation,
        IReadOnlyList<RequestMeasurement> requests,
        double elapsedSeconds)
    {
        var latencies = requests.Select(request => request.LatencyMilliseconds).Order().ToArray();
        return new EndpointSummary(
            role,
            operation,
            requests.Count,
            requests.Count(request => !request.Succeeded),
            requests.Count / elapsedSeconds,
            latencies.Length == 0 ? 0 : latencies.Average(),
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            Percentile(latencies, 0.99),
            latencies.Length == 0 ? 0 : latencies[^1],
            requests.Sum(request => request.ResponseBytes));
    }

    private static IReadOnlyList<TimelinePoint> BuildTimeline(ProfileRun run)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling((run.FinishedAt - run.StartedAt).TotalSeconds));
        var groups = run.Requests
            .GroupBy(request => Math.Clamp(
                (int)(request.StartedAt - run.StartedAt).TotalSeconds,
                0,
                seconds - 1))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var currentGames = run.DatabasePreparation?.Games ?? 0;
        var points = new List<TimelinePoint>(seconds);
        foreach (var second in Enumerable.Range(0, seconds))
        {
            var requests = groups.GetValueOrDefault(second) ?? [];
            var observedGames = requests
                .Where(request => request.DatabaseGames is not null)
                .Select(request => request.DatabaseGames!.Value)
                .DefaultIfEmpty(currentGames)
                .Max();
            currentGames = Math.Max(currentGames, observedGames);
            var latencies = requests.Select(request => request.LatencyMilliseconds).Order().ToArray();
            points.Add(new TimelinePoint(
                second,
                requests.Length,
                requests.Count(request => !request.Succeeded),
                Percentile(latencies, 0.95),
                currentGames));
        }

        return points;
    }

    private static void AppendDatabaseSizeTable(
        StringBuilder html,
        ProfileRun run)
    {
        var span = Math.Max(1, run.Options.TargetGames - run.Options.InitialGames);
        var bucketWidth = span <= 100 ? 10 : span <= 1_000 ? 100 : 500;
        html.AppendLine("<div class=\"eyebrow\">Request latency by database size</div><table><thead><tr><th>Games</th><th>Requests</th><th>Failures</th><th>p50</th><th>p95</th><th>p99</th></tr></thead><tbody>");
        foreach (var group in run.Requests
            .Where(request => request.DatabaseGames is not null)
            .GroupBy(request => request.DatabaseGames!.Value / bucketWidth)
            .OrderBy(group => group.Key))
        {
            var lower = group.Key * bucketWidth;
            var upper = lower + bucketWidth - 1;
            var latencies = group
                .Select(request => request.LatencyMilliseconds)
                .Order()
                .ToArray();
            html.Append("<tr><td>").Append($"{lower:N0}–{upper:N0}")
                .Append("</td><td>").Append(group.Count().ToString("N0"))
                .Append("</td><td>").Append(group.Count(request => !request.Succeeded).ToString("N0"))
                .Append("</td><td>").Append(FormatMilliseconds(Percentile(latencies, 0.50)))
                .Append("</td><td>").Append(FormatMilliseconds(Percentile(latencies, 0.95)))
                .Append("</td><td>").Append(FormatMilliseconds(Percentile(latencies, 0.99)))
                .AppendLine("</td></tr>");
        }
        html.AppendLine("</tbody></table>");
    }

    private static void AppendVerification(StringBuilder html, ProfileRun run)
    {
        html.AppendLine("<h2>Concurrency safety</h2><section class=\"panel\">");
        if (run.Verification is not null)
        {
            var css = run.Verification.InconsistentGames == 0 ? "good" : "bad";
            html.Append("<p class=\"").Append(css).Append("\"><strong>")
                .Append(run.Verification.InconsistentGames == 0
                    ? "Vote aggregates are consistent."
                    : $"{run.Verification.InconsistentGames:N0} inconsistent aggregate(s) detected.")
                .Append("</strong> Checked ").Append(run.Verification.CheckedGames.ToString("N0"))
                .AppendLine(" games by recomputing vote counts and normalized score averages from raw votes.</p>");
            if (run.Verification.Inconsistencies.Count > 0)
            {
                html.AppendLine("<table><thead><tr><th>Game</th><th>Stored votes</th><th>Actual votes</th><th>Maximum ratio delta</th></tr></thead><tbody>");
                foreach (var item in run.Verification.Inconsistencies)
                {
                    html.Append("<tr><td>").Append(item.GameId).Append("</td><td>")
                        .Append(item.AggregateVoteCount?.ToString(CultureInfo.InvariantCulture) ?? "missing")
                        .Append("</td><td>").Append(item.ActualVoteCount?.ToString(CultureInfo.InvariantCulture) ?? "missing")
                        .Append("</td><td>").Append(item.MaximumRatioDelta?.ToString("0.############", CultureInfo.InvariantCulture) ?? "n/a")
                        .AppendLine("</td></tr>");
                }
                html.AppendLine("</tbody></table>");
            }
        }
        else
        {
            html.Append("<p class=\"warn\">Aggregate verification was not available. ")
                .Append(E(run.VerificationError ?? "No profiler access token was supplied for this read-only run."))
                .AppendLine("</p>");
        }
        html.AppendLine("</section>");
    }

    private static void AppendHardware(StringBuilder html, ProfileRun run)
    {
        html.AppendLine("<h2>Hardware usage</h2><section class=\"panel\">");
        html.AppendLine("<table><thead><tr><th>Process</th><th>PID</th><th>CPU average</th><th>CPU peak</th><th>Working set peak</th><th>Private memory peak</th><th>Threads peak</th></tr></thead><tbody>");
        foreach (var group in run.HardwareSamples.GroupBy(sample => (sample.Process, sample.ProcessId)))
        {
            html.Append("<tr><td>").Append(E(group.Key.Process)).Append("</td><td>")
                .Append(group.Key.ProcessId).Append("</td><td>").Append(group.Average(sample => sample.CpuPercent).ToString("0.##"))
                .Append("%</td><td>").Append(group.Max(sample => sample.CpuPercent).ToString("0.##"))
                .Append("%</td><td>").Append(FormatBytes(group.Max(sample => sample.WorkingSetBytes)))
                .Append("</td><td>").Append(FormatBytes(group.Max(sample => sample.PrivateBytes)))
                .Append("</td><td>").Append(group.Max(sample => sample.ThreadCount).ToString("N0"))
                .AppendLine("</td></tr>");
        }
        html.AppendLine("</tbody></table>");
        if (run.Options.ServerProcessId is null)
        {
            html.AppendLine("<p>Only load-generator usage was sampled. Pass <code>--server-pid</code> when the API runs on the same host to include server CPU, memory, and thread usage.</p>");
        }
        html.AppendLine("</section>");
    }

    private static void AppendFailures(
        StringBuilder html,
        IReadOnlyList<RequestMeasurement> requests)
    {
        var failures = requests.Where(request => !request.Succeeded).ToArray();
        html.AppendLine("<h2>Failures</h2><section class=\"panel\">");
        if (failures.Length == 0)
        {
            html.AppendLine("<p class=\"good\"><strong>No request failures recorded.</strong></p>");
        }
        else
        {
            html.AppendLine("<table><thead><tr><th>Role</th><th>Operation</th><th>Status / error</th><th>Count</th></tr></thead><tbody>");
            foreach (var group in failures
                .GroupBy(request => (request.Role, request.Operation, Status: request.StatusCode?.ToString() ?? request.Error ?? "unknown"))
                .OrderByDescending(group => group.Count()))
            {
                html.Append("<tr><td>").Append(E(group.Key.Role)).Append("</td><td>")
                    .Append(E(group.Key.Operation)).Append("</td><td>").Append(E(group.Key.Status))
                    .Append("</td><td>").Append(group.Count().ToString("N0")).AppendLine("</td></tr>");
            }
            html.AppendLine("</tbody></table>");
        }
        html.AppendLine("</section>");
    }

    private static string Assessment(ProfileRun run, double failureRate, double p95)
    {
        var signals = new List<string>();
        signals.Add(failureRate switch
        {
            0 => "No request failures were observed",
            <= 1 => $"The {failureRate:0.###}% failure rate is low but should be investigated",
            _ => $"The {failureRate:0.###}% failure rate indicates the tested load is not reliable"
        });
        signals.Add(p95 <= 500
            ? $"overall p95 latency was {FormatMilliseconds(p95)}"
            : $"overall p95 latency reached {FormatMilliseconds(p95)}");
        if (run.Verification is not null)
        {
            signals.Add(run.Verification.InconsistentGames == 0
                ? "post-run vote aggregate invariants held"
                : $"{run.Verification.InconsistentGames} vote aggregate invariants failed");
        }
        else
        {
            signals.Add("database invariants were not verified");
        }

        if (run.Options.DatabaseMode == ProfilerDatabaseMode.Grow
            && run.FinalDatabase is not null)
        {
            signals.Add(run.FinalDatabase.Games >= run.Options.TargetGames
                ? $"the database reached its {run.Options.TargetGames:N0}-game target during the run"
                : $"the database reached {run.FinalDatabase.Games:N0} of the {run.Options.TargetGames:N0}-game target");
        }

        return string.Join(", ", signals) + ". Compare several connection levels and watch where latency, failures, or server CPU turn upward; one run alone does not establish maximum capacity.";
    }

    private static void AppendChart(
        StringBuilder html,
        IReadOnlyList<double> primary,
        IReadOnlyList<double>? secondary,
        string unit)
    {
        const double width = 720;
        const double height = 180;
        const double padding = 22;
        var all = secondary is null ? primary : primary.Concat(secondary).ToArray();
        var maximum = Math.Max(1, all.Count == 0 ? 1 : all.Max());
        html.Append("<svg class=\"chart\" viewBox=\"0 0 720 210\" role=\"img\" aria-label=\"")
            .Append(E(unit)).AppendLine(" over time\">");
        for (var line = 0; line <= 4; line++)
        {
            var y = padding + ((height - padding) * line / 4);
            html.Append("<line class=\"chart-grid\" x1=\"").Append(padding).Append("\" y1=\"")
                .Append(F(y)).Append("\" x2=\"").Append(width - padding).Append("\" y2=\"")
                .Append(F(y)).AppendLine("\"/>");
        }
        AppendPolyline(html, primary, maximum, width, height, padding, "chart-line");
        if (secondary is not null)
        {
            AppendPolyline(html, secondary, maximum, width, height, padding, "chart-line secondary");
        }
        html.Append("<text class=\"chart-label\" x=\"24\" y=\"204\">0s → ")
            .Append(Math.Max(0, primary.Count - 1)).Append("s</text><text class=\"chart-label\" x=\"696\" y=\"16\" text-anchor=\"end\">max ")
            .Append(maximum.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ').Append(E(unit))
            .AppendLine("</text></svg>");
    }

    private static void AppendPolyline(
        StringBuilder html,
        IReadOnlyList<double> values,
        double maximum,
        double width,
        double height,
        double padding,
        string cssClass)
    {
        if (values.Count == 0)
        {
            return;
        }

        html.Append("<polyline class=\"").Append(cssClass).Append("\" points=\"");
        for (var index = 0; index < values.Count; index++)
        {
            var x = values.Count == 1
                ? padding
                : padding + ((width - 2 * padding) * index / (values.Count - 1));
            var y = height - ((height - 2 * padding) * values[index] / maximum);
            html.Append(F(x)).Append(',').Append(F(y)).Append(' ');
        }
        html.AppendLine("\"/>");
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }
        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        var rank = percentile * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        var weight = rank - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * weight);
    }

    private static void Card(
        StringBuilder html,
        string label,
        string value,
        string note,
        string cssClass = "") => html.Append("<div class=\"card\"><span>")
            .Append(E(label)).Append("</span><strong class=\"").Append(cssClass).Append("\">")
            .Append(E(value)).Append("</strong><span>").Append(E(note)).AppendLine("</span></div>");

    private static void KeyValue(StringBuilder html, string key, string value) =>
        html.Append("<div>").Append(E(key)).Append("</div><div>").Append(E(value)).AppendLine("</div>");

    private static string FormatMilliseconds(double value) => value < 1
        ? $"{value:0.###} ms"
        : value < 1_000 ? $"{value:0.##} ms" : $"{value / 1_000:0.##} s";

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824d:0.##} GiB",
        >= 1_048_576 => $"{bytes / 1_048_576d:0.##} MiB",
        >= 1_024 => $"{bytes / 1_024d:0.##} KiB",
        _ => $"{bytes:N0} B"
    };

    private static string E(string value) => WebUtility.HtmlEncode(value);
    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private sealed record TimelinePoint(
        int Second,
        int Requests,
        int Failures,
        double P95Milliseconds,
        int DatabaseGames);
}
