using System.Globalization;
using System.Text;

namespace Auditor.Tools;

/// <summary>
/// Model usage report from the usage log the CLI writes next to output.csv (output.usage.jsonl). Everything in it is computed from that log; no figure is typed in by hand.
/// Costs are computed only from the participant's price file (rates + source URL + date); otherwise the report says "cost not computed - prices not supplied". Nothing here invents a price.
/// </summary>
public static class UsageReport
{
    /// <summary>{"A": 1, "B": 2} → "A 1, B 2" (sorted by key), so reports never print a raw collection.</summary>
    public static string Kv(IEnumerable<KeyValuePair<string, object?>> items)
    {
        var parts = items.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {Py.Str(kv.Value)}").ToList();
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    public static List<Dictionary<string, object?>> ReadLog(string path)
    {
        var rows = new List<Dictionary<string, object?>>();
        if (!File.Exists(path)) return rows;
        foreach (var line in File.ReadAllText(path, new UTF8Encoding(false)).Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            try { if (Py.ParseJson(line) is Dictionary<string, object?> d) rows.Add(d); }
            catch (DataException) { }                                  // a torn last line must not break the report
        }
        return rows;
    }

    private static string S(Dictionary<string, object?> r, string k, string dflt = "") => r.TryGetValue(k, out var v) && v is not null ? Py.Str(v) : dflt;
    private static long N(Dictionary<string, object?> r, string k, long dflt = 0) => r.TryGetValue(k, out var v) && v is long l ? l : v is double d ? (long)d : dflt;
    private static bool B(Dictionary<string, object?> r, string k) => r.TryGetValue(k, out var v) && Py.Truthy(v);
    private static string Money(double x, int places) => "$" + x.ToString("F" + places, CultureInfo.InvariantCulture);

    private sealed class Agg { public long Requests, Calls, Input, Output, CacheRead; }

    public static string Summarise(List<Dictionary<string, object?>> allRows, int nRequests, Dictionary<string, Prices.Price> prices, List<string> priceProblems, string logPath, string root)
    {
        var info = allRows.FirstOrDefault(r => S(r, "kind") == "run_info");
        var fromFile = allRows.Where(r => S(r, "provider") == "file").ToList();
        var rows = allRows.Where(r => S(r, "kind") != "run_info" && S(r, "provider") != "file").ToList();
        var live = rows.Where(r => !B(r, "cache_hit") && !B(r, "error")).ToList();
        var hits = rows.Where(r => B(r, "cache_hit")).ToList();
        var failed = rows.Where(r => B(r, "error")).ToList();
        var L = new List<string>
        {
            "# Usage report", "",
            $"Source: the model-usage log of the final full run (`{(logPath.Length > 0 ? logPath : "AUDITOR_USAGE_LOG")}`), {nRequests} requests in `dataset/deletion_requests.csv`.",
            "Everything below is computed from that log; no figure is typed in by hand.", "",
        };
        if (info is not null)
        {
            var counts = Py.Get(info, "status_counts") as Dictionary<string, object?> ?? new();
            var prov = (Py.Get(info, "provisional_rows") as List<object?>) ?? new();
            L.Add($"* run that produced `{S(info, "output_file")}` (sha256 `{S(info, "output_sha256")[..Math.Min(16, S(info, "output_sha256").Length)]}...`): {N(info, "requests")} requests, status counts {Kv(counts)}, "
                  + $"images read by a vision model: **{(B(info, "images_read_by_model") ? "yes" : "no")}**, images read from a readings file: **{(B(info, "images_read_from_file") ? "yes" : "no")}**, "
                  + $"API key present in the environment: **{(B(info, "api_key_present") ? "yes" : "no")}** (presence only)");
            L.Add($"* provisional rows in that output ({prov.Count}): {(prov.Count > 0 ? string.Join(", ", prov.Select(Py.Str)) : "none")}");
            L.Add("");
        }
        if (hits.Count > 0) L.AddRange(new[] { $"> **Not a cold-cache run**: the log contains {hits.Count} cache hits. Delete the cache directory and rerun before submitting this report.", "" });
        if (rows.Count == 0)
        {
            L.Add("**No model calls were made in this run: 0 calls, 0 input tokens, 0 output tokens.** Every decision, including every status and every step, was made by deterministic code. "
                  + "The vision path (`claude-haiku-5-5`, fallback `claude-sonnet-5-5`, configured but not called) was **tested only with fakes** because no API key was available; "
                  + "the screenshots were not read, which is why the rows that depend only on a screenshot are provisional.");
            L.Add("**Agent layer built and tested with fakes only; 0 calls, 0 tokens, no cost.** The opt-in tool-calling reader (`--images agent`, `Auditor/Agent`, report `agent_layer_report.md`) was never run against a real model.");
            L.Add("");
        }
        var byModel = new SortedDictionary<(string, string), Agg>(Comparer<(string, string)>.Create((a, b) => { int c = string.CompareOrdinal(a.Item1, b.Item1); return c != 0 ? c : string.CompareOrdinal(a.Item2, b.Item2); }));
        var byKind = new SortedDictionary<(string, string, string), Agg>(Comparer<(string, string, string)>.Create((a, b) =>
        {
            int c = string.CompareOrdinal(a.Item1, b.Item1); if (c != 0) return c;
            c = string.CompareOrdinal(a.Item2, b.Item2); return c != 0 ? c : string.CompareOrdinal(a.Item3, b.Item3);
        }));
        foreach (var r in live)
        {
            var mk = (S(r, "provider"), S(r, "model"));
            var kk = (S(r, "provider"), S(r, "model"), S(r, "kind", "?"));
            if (!byModel.TryGetValue(mk, out var m)) byModel[mk] = m = new Agg();
            if (!byKind.TryGetValue(kk, out var k)) byKind[kk] = k = new Agg();
            foreach (var a in new[] { m, k })
            {
                a.Requests++; a.Calls += N(r, "calls", 1); a.Input += N(r, "input_tokens"); a.Output += N(r, "output_tokens"); a.CacheRead += N(r, "cache_read_input_tokens");
            }
        }
        L.AddRange(new[] { "## Providers and models", "", "| provider | model | files processed | model calls (incl. re-asks) | input tokens | output tokens | total tokens | cost |", "|---|---|---|---|---|---|---|---|" });
        var tot = new Agg();
        double totalCost = 0; bool pricedAll = true;
        foreach (var ((prov, model), c) in byModel)
        {
            prices.TryGetValue(model, out var p);
            double? cost = p?.Cost(c.Input, c.Output, c.CacheRead);
            pricedAll &= p is not null;
            totalCost += cost ?? 0;
            L.Add($"| {prov} | {model} | {c.Requests} | {c.Calls} | {c.Input} | {c.Output} | {c.Input + c.Output} | {(cost is { } v ? Money(v, 4) : Prices.NotSupplied)} |");
            tot.Requests += c.Requests; tot.Calls += c.Calls; tot.Input += c.Input; tot.Output += c.Output;
        }
        L.Add($"| **all models** | | {tot.Requests} | {tot.Calls} | {tot.Input} | {tot.Output} | {tot.Input + tot.Output} | {(byModel.Count > 0 && pricedAll ? Money(totalCost, 4) : Prices.NotSupplied)} |");
        L.AddRange(new[] { "", "## By purpose", "", "| provider | model | purpose | files | calls | input tokens | output tokens |", "|---|---|---|---|---|---|---|" });
        foreach (var ((prov, model, kind), c) in byKind) L.Add($"| {prov} | {model} | {kind} | {c.Requests} | {c.Calls} | {c.Input} | {c.Output} |");
        if (byKind.Count == 0) L.Add("| - | - | - | 0 | 0 | 0 | 0 |");
        long tokens = tot.Input + tot.Output;
        bool cost_ok = byModel.Count > 0 && pricedAll;
        L.AddRange(new[]
        {
            "", "## Per request", "",
            $"* total tokens: **{tokens}**; average per request: **{((double)tokens / nRequests).ToString("F1", CultureInfo.InvariantCulture)}** ({nRequests} requests; a model is called only to read a screenshot)",
            $"* model calls per request: {((double)tot.Calls / nRequests).ToString("F3", CultureInfo.InvariantCulture)}",
            "* estimated total cost: " + (cost_ok ? $"**{Money(totalCost, 4)}**" : $"**{Prices.NotSupplied}**"),
            "* estimated cost per request: " + (cost_ok ? $"**{Money(totalCost / nRequests, 6)}**" : $"**{Prices.NotSupplied}**"), "",
            "## Prices", "",
        });
        if (prices.Count > 0)
            L.AddRange(prices.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
                $"* {kv.Key}: input {Py.FloatRepr(kv.Value.InputPerMtok)}/Mtok, output {Py.FloatRepr(kv.Value.OutputPerMtok)}/Mtok"
                + (kv.Value.CacheReadPerMtok is { } cr ? $", cached input {Py.FloatRepr(cr)}/Mtok" : "") + $" - source {kv.Value.SourceUrl}, dated {kv.Value.Date} (supplied by the participant)"));
        else
            L.Add($"* {Prices.NotSupplied}." + (priceProblems.Count > 0 ? "  " + string.Join("; ", priceProblems).Replace(root + "/", "", StringComparison.Ordinal) : ""));
        if (byModel.Count > 0 && !pricedAll && prices.Count > 0)
            L.Add($"* models without a complete price entry: {Py.ListRepr(byModel.Keys.Select(k => k.Item2).Where(m => !prices.ContainsKey(m)).Distinct().OrderBy(m => m, StringComparer.Ordinal))}");
        if (fromFile.Count > 0)
            L.AddRange(new[]
            {
                "", "## Readings taken from a file (no model call)", "",
                $"* {fromFile.Count} screenshot readings were taken from a readings file ({S(fromFile[0], "source")}): provider `file`, 0 model calls, 0 tokens, no cost. They are not model output; see `Auditor/Readings.cs` for how they are validated and sanitised.",
            });
        L.AddRange(new[]
        {
            "", "## Cache and failures", "",
            $"* live calls: {live.Count}; cache hits: {hits.Count}; calls that failed schema validation twice: {failed.Count} (those images fall back to the deterministic result and are listed in the run notes)",
        });
        return string.Join("\n", L) + "\n";
    }

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var root = Cli.Cli.RepoRoot();
        bool final = args.Contains("--final"), requireRunInfo = args.Contains("--require-run-info");
        string logPath = Environment.GetEnvironmentVariable("AUDITOR_USAGE_LOG") is { Length: > 0 } e ? e : Path.Combine(root, "output.usage.jsonl");
        int li = Array.IndexOf(args, "--log");
        if (li >= 0 && li + 1 < args.Length) logPath = args[li + 1];
        int n = Csv.ReadRows(Path.Combine(root, "dataset", "deletion_requests.csv")).Count;
        var rows = ReadLog(logPath);
        if (requireRunInfo && !rows.Any(r => S(r, "kind") == "run_info"))
        {
            stderr.WriteLine($"error: {logPath} has no run_info row: run the auditor first (it writes the log next to output.csv)");
            return 2;
        }
        var (prices, problems) = Prices.Load();
        var rel = Path.GetFullPath(logPath).StartsWith(root, StringComparison.Ordinal) ? Path.GetRelativePath(root, logPath).Replace('\\', '/') : Path.GetFileName(logPath);
        var text = Summarise(rows, n, prices, problems, rel, root);
        var outPath = final ? Path.Combine(root, "code", "evaluation", "usage_report.md") : Path.Combine(root, "code", "out", "usage_report_preview.md");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, text, new UTF8Encoding(false));
        stdout.WriteLine(text);
        stdout.WriteLine($"wrote {outPath}");
        return 0;
    }
}
