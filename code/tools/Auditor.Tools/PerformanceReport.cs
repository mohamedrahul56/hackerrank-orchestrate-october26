using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Auditor.Tools;

/// <summary>
/// Profiles the whole run: wall time per stage, peak memory, growth with the number of requests. `perf-run` (internal) does ONE in-process run of the work the command line does, stage by stage,
/// and prints it as one JSON line; `performance-report` starts a fresh process per (scale, run), takes the median run of each scale and writes code/evaluation/performance_report.md.
/// Scaled inputs are built in a temporary folder: every file of dataset/ is linked unchanged except deletion_requests.csv, whose rows are repeated K times with case ids `&lt;id&gt;__rK`.
/// The repeated rows are NOT independent cases (same person, same evidence), so their ANSWERS differ from the real run (they compete for the same free accounts); only the cost is meaningful.
/// </summary>
public static class PerformanceReport
{
    public const string MarkStart = "<!-- perf-table:start -->", MarkEnd = "<!-- perf-table:end -->";

    private static string ScaledDataset(string src, string dst, int repeat)
    {
        Directory.CreateDirectory(dst);
        foreach (var p in Directory.EnumerateFileSystemEntries(src))
        {
            var name = Path.GetFileName(p);
            if (name == "deletion_requests.csv") continue;
            var link = Path.Combine(dst, name);
            if (Directory.Exists(p)) Directory.CreateSymbolicLink(link, p); else File.CreateSymbolicLink(link, p);
        }
        var rows = Csv.ReadRows(Path.Combine(src, "deletion_requests.csv"));
        var sb = new StringBuilder(Csv.FormatRow(Dataset.RequestColumns));
        for (int k = 1; k <= repeat; k++)
            foreach (var r in rows)
                sb.Append(Csv.FormatRow(Dataset.RequestColumns.Select(c => c == "case_id" && k > 1 ? $"{r.G("case_id")}__r{k}" : r.G(c))));
        File.WriteAllText(Path.Combine(dst, "deletion_requests.csv"), sb.ToString(), new UTF8Encoding(false));
        return dst;
    }

    private static Dictionary<string, object?> OneRun(string dataset)
    {
        var stages = new Dictionary<string, double>(StringComparer.Ordinal);
        var all = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        var aud = new CaseAuditor(dataset, new AuditConfig());
        stages["auditor_start_up_total"] = sw.Elapsed.TotalSeconds;
        sw.Restart();
        var ds = Dataset.Load(dataset);
        stages["load_dataset_for_validation"] = sw.Elapsed.TotalSeconds;
        sw.Restart();
        var results = ds.CaseOrder.Select(aud.AuditCase).ToList();
        stages["audit_cases_loop"] = sw.Elapsed.TotalSeconds;
        var tmp = Directory.CreateTempSubdirectory("perf_").FullName;
        var outPath = Path.Combine(tmp, "output.csv");
        sw.Restart();
        OutputIo.Write(outPath, results.Select(r => new OutputIo.Row(r.CaseId, r.Status, r.Steps)));
        stages["write_output"] = sw.Elapsed.TotalSeconds;
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(outPath))).ToLowerInvariant();
        sw.Restart();
        int errors = Validator.ValidateOutputFile(outPath, ds).Count(i => i.Severity == "error");
        stages["validate_output"] = sw.Elapsed.TotalSeconds;
        try { Directory.Delete(tmp, true); } catch (IOException) { }
        var total = all.Elapsed.TotalSeconds;
        foreach (var (k, v) in aud.Timings) stages[k] = v;
        return new()
        {
            ["cases"] = (long)results.Count, ["total_s"] = total, ["stages"] = stages.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            ["peak_rss_mb"] = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0, ["output_sha256"] = sha, ["validator_errors"] = (long)errors,
            ["status_counts"] = results.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => (object?)(long)g.Count()),
        };
    }

    private sealed record Measured(int Repeat, int Cases, double Total, List<double> Runs, double Rss, string Sha, Dictionary<string, double> Stages);

    private static double D(object? v) => Convert.ToDouble(v, CultureInfo.InvariantCulture);

    private static Measured Measure(string dataset, int repeat, int runs)
    {
        var all = new List<(double Total, double Rss, string Sha, int Cases, Dictionary<string, double> Stages)>();
        for (int i = 0; i < runs; i++)
        {
            var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Cli.Cli.RepoRoot() };
            foreach (var a in new[] { typeof(PerformanceReport).Assembly.Location, "perf-run", "--repeat", repeat.ToString(CultureInfo.InvariantCulture), "--dataset", dataset }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException($"perf-run x{repeat} failed: {e.Result}");
            var d = Compat.AsDict(Compat.ParseJson(o.Result.Trim().Split('\n')[^1]));
            all.Add((D(d["total_s"]), D(d["peak_rss_mb"]), (string)d["output_sha256"]!, (int)D(d["cases"]), Compat.AsDict(d["stages"]).ToDictionary(kv => kv.Key, kv => D(kv.Value))));
        }
        if (all.Select(a => a.Sha).Distinct().Count() != 1) throw new InvalidOperationException($"x{repeat}: the runs produced different output files");
        var sorted = all.OrderBy(a => a.Total).ToList();
        var median = sorted[sorted.Count / 2];
        return new Measured(repeat, median.Cases, median.Total, sorted.Select(s => s.Total).ToList(), median.Rss, median.Sha, median.Stages);
    }

    private static string F(double x, int places = 2) => x.ToString("F" + places, CultureInfo.InvariantCulture);

    private static string Runs(Measured m) => $"{F(m.Total)} ({string.Join("/", m.Runs.Select(r => F(r)))})";

    public static string Build(string dataset, int[] scales, int runs)
    {
        var ms = scales.Select(k => Measure(dataset, k, runs)).ToList();          // the child process builds the scaled copy of the dataset itself
        var b = ms[0];
        var L = new List<string>
        {
            "# Performance report", "",
            $"Generated by `dotnet run --project code/tools/Auditor.Tools -- performance-report` from {ms.Count} scales x {runs} fresh-process runs each (median reported). Nothing here is estimated: every number is a measurement on the machine below.", "",
            $"Machine: {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical CPUs, {RuntimeInformation.FrameworkDescription}, Release build. The auditor is single-threaded here (no model calls in these runs), so CPU count does not matter; absolute seconds do depend on the machine, the ratios are the finding. The first run of a process includes JIT compilation, which is part of the fixed start-up cost below.", "",
            "## 1. What was measured", "",
            "* **x1** is the real input: the 518 requests of `dataset/deletion_requests.csv` (plus the 32 sample requests the auditor always loads).",
            "* **x2 ... x40** repeat every request row K times under new case ids (`case_7__r3`), in a temporary folder where every other dataset file is linked unchanged. x40 is 20,720 requests. The repeated rows are the same people with the same evidence, so they are **not independent cases**: their answers differ from the real run (copies compete for the same free accounts). Only the cost is meaningful, not the statuses.",
            "* This report measures the C# implementation only. The earlier Python implementation was profiled with the same method (see the change log, rows 40-43: the timeline discovery was quadratic and was given time indexes, 16.0x for 40x the requests); the C# port carries those indexes over, so there is no before/after comparison here.", "",
            "## 2. Wall time and peak memory", "",
            "| input | cases | seconds (median, all runs) | growth vs x1 (time / cases) | peak RSS MB | output sha256 |", "|---|---|---|---|---|---|",
        };
        L.AddRange(ms.Select(m => $"| x{m.Repeat} | {m.Cases} | {Runs(m)} | {F(m.Total / b.Total, 1)}x / {F((double)m.Cases / b.Cases, 1)}x | {m.Rss:F0} | `{m.Sha[..10]}` |"));
        L.AddRange(new[]
        {
            "", "Peak memory is the process's peak working set, measured in its own fresh process. " + $"It grows from {b.Rss:F0} MB at x1 to {ms[^1].Rss:F0} MB at x{ms[^1].Repeat}, about {F((ms[^1].Rss - b.Rss) * 1024 / (ms[^1].Cases - b.Cases), 1)} KB per additional request.", "",
            "## 3. Where the time went, per stage (seconds, median run)", "",
        });
        var stageList = new[]
        {
            ("parse evidence files (`evidence_parse`)", "evidence_parse"), ("classify request text (`classify_requests`)", "classify_requests"), ("timeline discovery (all cases at once) (`discovery_total`)", "discovery_total"),
            ("audit each case (`audit_cases_loop`)", "audit_cases_loop"), ("write output.csv (`write_output`)", "write_output"), ("re-read and validate output.csv (`validate_output`)", "validate_output"),
            ("&nbsp;&nbsp;per-case inputs (`discovery.case_inputs`)", "discovery.case_inputs"), ("&nbsp;&nbsp;assign id-less accounts (`discovery.assign_accounts`)", "discovery.assign_accounts"),
            ("&nbsp;&nbsp;assign anonymized analytics (`discovery.analytics`)", "discovery.analytics"),
        };
        string Ratio(double a, double z) => a < 0.005 ? "-" : F(z / a, 1) + "x";
        L.AddRange(new[] { $"| stage | x1 | x{ms[^1].Repeat} | growth x1 to x{ms[^1].Repeat} |", "|---|---|---|---|" });
        foreach (var (label, key) in stageList)
        {
            double a = b.Stages.GetValueOrDefault(key), z = ms[^1].Stages.GetValueOrDefault(key);
            L.Add($"| {label} | {F(a, 3)} | {F(z, 3)} | {Ratio(a, z)} |");
        }
        double scale = (double)ms[^1].Cases / b.Cases;
        L.AddRange(new[]
        {
            "", $"Input grows {F(scale, 0)}x from x1 to x{ms[^1].Repeat}, so a stage that grows about {F(scale, 0)}x is linear, a stage far above that is super-linear, and a stage far below it is dominated by a fixed cost (parsing the evidence does not depend on the number of requests). Stages whose x1 time is below 5 ms show `-`: a ratio of two timer-noise values means nothing.", "",
            "### Cost per request (microseconds, median run)", "",
            "| input | whole run | timeline discovery | per-case audit | validation | classification |", "|---|---|---|---|---|---|",
        });
        foreach (var m in ms)
            L.Add($"| x{m.Repeat} | {m.Total * 1e6 / m.Cases:F0} | {m.Stages.GetValueOrDefault("discovery_total") * 1e6 / m.Cases:F0} | {m.Stages.GetValueOrDefault("audit_cases_loop") * 1e6 / m.Cases:F0} | {m.Stages.GetValueOrDefault("validate_output") * 1e6 / m.Cases:F0} | {m.Stages.GetValueOrDefault("classify_requests") * 1e6 / m.Cases:F0} |");
        L.AddRange(new[] { "", "A linear stage has a flat column. The whole-run column falls at first because the fixed start-up cost is spread over more requests.", "", "## 4. Observed growth", "",
            "Local exponent p in time ~ cases^p between neighbouring scales (p = 1 is linear, p = 2 quadratic). Computed from the medians above; time includes the fixed start-up cost, which pulls p below 1 at small scales, so look at the large scales.", "" });
        string Exp(Func<Measured, double> f, Measured m0, Measured m1) => f(m0) < 0.005 || f(m1) < 0.005 ? "-" : F(Math.Log(f(m1) / f(m0)) / Math.Log((double)m1.Cases / m0.Cases));
        var heads = ms.Zip(ms.Skip(1), (x, y) => $"x{x.Repeat} to x{y.Repeat}").ToList();
        L.Add("| series | " + string.Join(" | ", heads) + " |");
        L.Add("|---|" + string.Concat(heads.Select(_ => "---|")));
        foreach (var (name, f) in new (string, Func<Measured, double>)[]
                 {
                     ("total", m => m.Total), ("timeline discovery", m => m.Stages.GetValueOrDefault("discovery_total")), ("per-case audit loop", m => m.Stages.GetValueOrDefault("audit_cases_loop")),
                     ("validation", m => m.Stages.GetValueOrDefault("validate_output")), ("classification", m => m.Stages.GetValueOrDefault("classify_requests")),
                 })
            L.Add($"| {name} | " + string.Join(" | ", ms.Zip(ms.Skip(1), (x, y) => Exp(f, x, y))) + " |");
        // least squares: time = a + b * cases
        double n = ms.Count, sx = ms.Sum(m => (double)m.Cases), sy = ms.Sum(m => m.Total), sxx = ms.Sum(m => (double)m.Cases * m.Cases), sxy = ms.Sum(m => m.Cases * m.Total);
        double slope = (n * sxy - sx * sy) / (n * sxx - sx * sx), icpt = (sy - slope * sx) / n;
        double ssTot = ms.Sum(m => Math.Pow(m.Total - sy / n, 2)), ssRes = ms.Sum(m => Math.Pow(m.Total - (icpt + slope * m.Cases), 2));
        L.AddRange(new[]
        {
            "", $"Linear fit of the totals, time = a + b * cases: a = {F(icpt)} s fixed, b = {F(slope * 1000, 3)} ms per request, R^2 = {F(1 - ssRes / ssTot, 4)}.", "",
            "## 5. What this does not show", "",
            "* Only the number of requests was scaled. The evidence files (103 files), the activity stream and the identity tables were not, so nothing is known about their growth.",
            "* This is a batch auditor, not a service: everything is held in memory in one process, and the timeline discovery needs every case at once, so cases cannot be audited in independent shards.",
            "* Model latency and cost were not benchmarked: no model call is made in these runs.",
            "* One machine, one operating system; the absolute seconds do not transfer to another machine.", "",
        });
        return string.Join("\n", L) + "\n";
    }

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var root = Cli.Cli.RepoRoot();
        var dataset = AgentProofs.Option(args, "--dataset") ?? Path.Combine(root, "dataset");
        if (args.Contains("perf-run") || args.Length > 0 && args[0] == "perf-run")
        {
            int repeat = int.Parse(AgentProofs.Option(args, "--repeat") ?? "1", CultureInfo.InvariantCulture);
            var tmp = Directory.CreateTempSubdirectory("perf_one_").FullName;
            var ds = repeat > 1 ? ScaledDataset(dataset, Path.Combine(tmp, "ds"), repeat) : dataset;
            stdout.WriteLine(Compat.DumpsCompact(OneRun(ds)));
            try { Directory.Delete(tmp, true); } catch (IOException) { }
            return 0;
        }
        int runs = int.Parse(AgentProofs.Option(args, "--runs") ?? "3", CultureInfo.InvariantCulture);
        var scales = (AgentProofs.Option(args, "--scales") ?? "1,2,5,10,20,40").Split(',').Select(int.Parse).ToArray();
        var text = Build(dataset, scales, runs);
        var path = Path.Combine(root, "code", "evaluation", "performance_report.md");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        stderr.WriteLine($"wrote {path}");
        return 0;
    }

    /// <summary>The wall-time table of a performance report (header, separator, rows): what ARCHITECTURE.md copies between its markers.</summary>
    public static string WallTimeTable(string reportText)
    {
        var lines = reportText.Split('\n').ToList();
        int start = lines.FindIndex(l => l.StartsWith("| input | cases |", StringComparison.Ordinal));
        int end = start;
        while (end < lines.Count && lines[end].StartsWith('|')) end++;
        return string.Join("\n", lines.GetRange(start, end - start)).Trim();
    }
}
