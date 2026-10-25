using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Auditor.Tools;

/// <summary>
/// Scores a predictions CSV against a labeled CSV (default: the 32 public samples). Reports are Markdown so the same text goes to the terminal and to evaluation reports:
/// case-level status accuracy + confusion matrix; step-level P/R/F1 (strict = all six content fields, relaxed = without evidence_id); resource discovery P/R/F1;
/// field-level accuracy over gold steps (gold steps aligned to predicted steps); sequence accuracy (exact ordered match, mean LCS ratio); per-case failure diffs.
/// The labeled cases are split deterministically (stratified by status and answer size, tie-broken by sha256(case_id); no case-id literals): failure diffs are shown for dev cases only.
/// </summary>
public static class Scorer
{
    public static readonly string[] ContentFields = { "phase", "system_id", "resource_id", "operation", "state", "evidence_id" };

    /// <summary>One step as read from a CSV: the raw JSON object (so a wrong type in a prediction stays visible and compares unequal).</summary>
    public sealed class RawStep
    {
        public Dictionary<string, object?> Fields { get; }
        public RawStep(Dictionary<string, object?> fields) => Fields = fields;
        public object? Get(string f) => Fields.TryGetValue(f, out var v) ? v : "";
        public string Text(string f) => Compat.Str(Get(f));
    }

    public sealed record Row(string Status, List<RawStep> Steps, string Error = "");

    public static Dictionary<string, Row> ReadRows(string path)
    {
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var r in Csv.ReadRows(path))
        {
            string err = "";
            var steps = new List<RawStep>();
            try
            {
                var parsed = Compat.ParseJson(r.G("steps_json"));
                if (parsed is List<object?> list && list.All(x => x is Dictionary<string, object?>))
                    steps = list.Select(x => new RawStep((Dictionary<string, object?>)x!)).ToList();
                else err = "steps_json is not an array of objects";
            }
            catch (DataException e) { err = $"bad JSON: {e.Message}"; }
            rows[r.G("case_id")] = new Row(r.G("deletion_status"), steps, err);
        }
        return rows;
    }

    // ----------------------------------------------------------------------------- split
    public static Dictionary<string, string> AssignSplits(Dictionary<string, Row> gold)
    {
        static string Bucket(int n) => n <= 3 ? "small" : n <= 8 ? "medium" : "large";
        var strata = new Dictionary<(string, string), List<string>>();
        foreach (var (cid, row) in gold)
        {
            var key = (row.Status, Bucket(row.Steps.Count));
            if (!strata.TryGetValue(key, out var list)) strata[key] = list = new List<string>();
            list.Add(cid);
        }
        var split = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var members in strata.Values)
        {
            members.Sort((a, b) => string.CompareOrdinal(Sha(a), Sha(b)));
            for (int i = 0; i < members.Count; i++) split[members[i]] = i % 3 == 2 ? "heldout" : "dev";
        }
        return split;
    }

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    // ----------------------------------------------------------------------------- step helpers
    private static string Tag(object? o) => o switch
    {
        null => "n",
        string s => "s" + s,
        long l => "i" + l.ToString(CultureInfo.InvariantCulture),
        _ => "o" + Compat.Repr(o),
    };

    public static string Full(RawStep s) => string.Join("\u0001", ContentFields.Select(f => Tag(s.Get(f))));
    public static string Relaxed(RawStep s) => string.Join("\u0001", ContentFields.Where(f => f != "evidence_id").Select(f => Tag(s.Get(f))));
    public static string Resource(RawStep s) => Tag(s.Get("system_id")) + "\u0001" + Tag(s.Get("resource_id"));

    private static bool SameRaw(RawStep a, RawStep b) =>
        a.Fields.Count == b.Fields.Count && a.Fields.All(kv => b.Fields.TryGetValue(kv.Key, out var v) && Tag(kv.Value) == Tag(v));

    public static int LcsLen(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var prev = new int[b.Count + 1];
        foreach (var x in a)
        {
            var cur = new int[b.Count + 1];
            for (int j = 1; j <= b.Count; j++) cur[j] = x == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            prev = cur;
        }
        return prev[b.Count];
    }

    public static double LcsRatio(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == 0 && b.Count == 0 ? 1.0 : 2.0 * LcsLen(a, b) / (a.Count + b.Count);

    public static (double P, double R, double F1) Prf(int tp, int nPred, int nGold)
    {
        double p = nPred > 0 ? (double)tp / nPred : (nGold == 0 ? 1.0 : 0.0);
        double r = nGold > 0 ? (double)tp / nGold : (nPred == 0 ? 1.0 : 0.0);
        return (p, r, p + r > 0 ? 2 * p * r / (p + r) : 0.0);
    }

    /// <summary>Size of the multiset intersection.</summary>
    public static int MultisetTp(IEnumerable<string> g, IEnumerable<string> p)
    {
        var cg = g.GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        var cp = p.GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        return cg.Sum(kv => cp.TryGetValue(kv.Key, out var n) ? Math.Min(kv.Value, n) : 0);
    }

    /// <summary>Elements of <paramref name="a"/> minus <paramref name="b"/> as multisets, in first-appearance order (collections.Counter subtraction).</summary>
    private static List<int> MultisetDiff(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var remaining = b.GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        var firstSeen = new List<string>();
        var count = new Dictionary<string, int>();
        var firstIndex = new Dictionary<string, int>();
        for (int i = 0; i < a.Count; i++)
        {
            if (!count.ContainsKey(a[i])) { count[a[i]] = 0; firstSeen.Add(a[i]); firstIndex[a[i]] = i; }
            count[a[i]]++;
        }
        var result = new List<int>();
        foreach (var k in firstSeen)
        {
            int extra = count[k] - (remaining.TryGetValue(k, out var n) ? n : 0);
            for (int j = 0; j < extra; j++) result.Add(firstIndex[k]);
        }
        return result;
    }

    /// <summary>
    /// For each gold step, the predicted step it most plausibly corresponds to, so per-field accuracy is not destroyed by one missing step shifting every later index.
    /// Greedy tiers, each predicted step used once: exact content, (phase, system, resource, op), (phase, resource), (resource), (phase, operation).
    /// </summary>
    public static RawStep?[] Align(List<RawStep> gold, List<RawStep> pred)
    {
        string T(RawStep s, params string[] fs) => string.Join("\u0001", fs.Select(f => Tag(s.Get(f))));
        var tiers = new Func<RawStep, string>[]
        {
            Full,
            s => T(s, "phase", "system_id", "resource_id", "operation"),
            s => T(s, "phase", "resource_id"),
            s => T(s, "resource_id"),
            s => T(s, "phase", "operation"),
        };
        var match = new RawStep?[gold.Count];
        var used = new HashSet<int>();
        foreach (var key in tiers)
            for (int gi = 0; gi < gold.Count; gi++)
            {
                if (match[gi] is not null) continue;
                for (int pi = 0; pi < pred.Count; pi++)
                    if (!used.Contains(pi) && key(gold[gi]) == key(pred[pi])) { match[gi] = pred[pi]; used.Add(pi); break; }
            }
        return match;
    }

    // ----------------------------------------------------------------------------- metrics
    public sealed class CaseResult
    {
        public string CaseId = "", GoldStatus = "", PredStatus = "";
        public bool Exact, OrderOnly, Present = true;
        public double LcsFull, LcsRelaxed, F1Full;
        public List<RawStep> Missing = new(), Extra = new();
        public string Error = "";
    }

    public sealed class Metrics
    {
        public int NCases, StatusCorrect, NGoldSteps, ExactCases, MissingCases, BadRows;
        public Dictionary<(string, string), int> Confusion = new();
        public (double P, double R, double F1) StepStrict, StepRelaxed, Discovery;
        public Dictionary<string, double> FieldAcc = new();
        public double MeanLcsFull, MeanLcsRelaxed;
        public List<CaseResult> Cases = new();
    }

    public static Metrics Score(Dictionary<string, Row> gold, Dictionary<string, Row> pred, List<string> caseIds)
    {
        var conf = new Dictionary<(string, string), int>();
        int tpF = 0, tpR = 0, tpD = 0, npF = 0, ngF = 0, npD = 0, ngD = 0, nGoldSteps = 0, missingCases = 0, badRows = 0;
        var fieldOk = Schema.StepFields.ToDictionary(f => f, _ => 0);
        var results = new List<CaseResult>();
        foreach (var cid in caseIds)
        {
            var g = gold[cid];
            bool present = pred.TryGetValue(cid, out var p);
            if (!present) { p = new Row("", new List<RawStep>()); missingCases++; }
            if (p!.Error.Length > 0) badRows++;
            var ck = (g.Status, p.Status.Length > 0 ? p.Status : "(missing)");
            conf[ck] = conf.GetValueOrDefault(ck) + 1;
            var gf = g.Steps.Select(Full).ToList(); var pf = p.Steps.Select(Full).ToList();
            var gr = g.Steps.Select(Relaxed).ToList(); var pr = p.Steps.Select(Relaxed).ToList();
            var gd = g.Steps.Select(Resource).ToHashSet(); var pd = p.Steps.Select(Resource).ToHashSet();
            int tp = MultisetTp(gf, pf);
            tpF += tp; npF += pf.Count; ngF += gf.Count;
            tpR += MultisetTp(gr, pr);
            tpD += gd.Intersect(pd).Count(); npD += pd.Count; ngD += gd.Count;
            var aligned = Align(g.Steps, p.Steps);
            for (int i = 0; i < g.Steps.Count; i++)
            {
                nGoldSteps++;
                foreach (var f in Schema.StepFields)
                    if (aligned[i] is { } ps && Tag(ps.Get(f)) == Tag(g.Steps[i].Get(f)) && ps.Fields.ContainsKey(f) == g.Steps[i].Fields.ContainsKey(f)) fieldOk[f]++;
            }
            bool exact = g.Status == p.Status && g.Steps.Count == p.Steps.Count && g.Steps.Zip(p.Steps).All(t => SameRaw(t.First, t.Second));
            var missing = MultisetDiff(gf, pf).Select(i => g.Steps[i]).ToList();
            var extra = MultisetDiff(pf, gf).Select(i => p.Steps[i]).ToList();
            results.Add(new CaseResult
            {
                CaseId = cid, GoldStatus = g.Status, PredStatus = p.Status, Exact = exact, LcsFull = LcsRatio(gf, pf), LcsRelaxed = LcsRatio(gr, pr),
                F1Full = Prf(tp, pf.Count, gf.Count).F1, Missing = missing, Extra = extra,
                OrderOnly = missing.Count == 0 && extra.Count == 0 && !gf.SequenceEqual(pf), Error = p.Error, Present = present,
            });
        }
        int n = caseIds.Count;
        return new Metrics
        {
            NCases = n,
            StatusCorrect = Schema.Statuses.Sum(s => conf.GetValueOrDefault((s, s))),
            Confusion = conf,
            StepStrict = Prf(tpF, npF, ngF), StepRelaxed = Prf(tpR, npF, ngF), Discovery = Prf(tpD, npD, ngD),
            FieldAcc = Schema.StepFields.ToDictionary(f => f, f => nGoldSteps > 0 ? (double)fieldOk[f] / nGoldSteps : 1.0),
            NGoldSteps = nGoldSteps, ExactCases = results.Count(r => r.Exact),
            MeanLcsFull = n > 0 ? results.Sum(r => r.LcsFull) / n : 1.0, MeanLcsRelaxed = n > 0 ? results.Sum(r => r.LcsRelaxed) / n : 1.0,
            Cases = results, MissingCases = missingCases, BadRows = badRows,
        };
    }

    // ----------------------------------------------------------------------------- report
    public static string Pct(double x) => (100 * x).ToString("F1", CultureInfo.InvariantCulture) + "%";

    private static string StepStr(RawStep s) =>
        $"{Compat.Str(s.Get("phase"))[..Math.Min(4, Compat.Str(s.Get("phase")).Length)]} {s.Text("operation")} {s.Text("system_id")}/{s.Text("resource_id")}" + (s.Text("evidence_id").Length > 0 ? $" @{s.Text("evidence_id")}" : "");

    public static List<string> SummaryLines(Metrics m, string title)
    {
        var L = new List<string>
        {
            $"### {title}", "",
            $"* cases: **{m.NCases}**, gold steps: **{m.NGoldSteps}**" + (m.MissingCases > 0 ? $", missing prediction rows: {m.MissingCases}" : "") + (m.BadRows > 0 ? $", unparseable steps_json: {m.BadRows}" : ""),
            $"* case-level status accuracy: **{m.StatusCorrect}/{m.NCases} = {Pct(m.NCases > 0 ? (double)m.StatusCorrect / m.NCases : 1)}**",
            $"* step-level strict (all 6 content fields): P {Pct(m.StepStrict.P)} / R {Pct(m.StepStrict.R)} / **F1 {Pct(m.StepStrict.F1)}**",
            $"* step-level relaxed (ignoring evidence_id): P {Pct(m.StepRelaxed.P)} / R {Pct(m.StepRelaxed.R)} / F1 {Pct(m.StepRelaxed.F1)}",
            $"* resource discovery ((system_id, resource_id) pairs): P {Pct(m.Discovery.P)} / R {Pct(m.Discovery.R)} / F1 {Pct(m.Discovery.F1)}",
            $"* sequence: exact ordered match **{m.ExactCases}/{m.NCases} = {Pct(m.NCases > 0 ? (double)m.ExactCases / m.NCases : 1)}**; mean LCS ratio strict {Pct(m.MeanLcsFull)}, relaxed {Pct(m.MeanLcsRelaxed)}",
            "", "| field | accuracy over gold steps |", "|---|---|",
        };
        L.AddRange(m.FieldAcc.Select(kv => $"| {kv.Key} | {Pct(kv.Value)} |"));
        var labels = Schema.Statuses.Append("(missing)").ToList();
        L.AddRange(new[] { "", "Confusion matrix (rows = gold, columns = predicted):", "", "| gold \\ pred | " + string.Join(" | ", labels) + " |", "|---|" + string.Concat(labels.Select(_ => "---|")) });
        foreach (var g in Schema.Statuses)
            L.Add($"| {g} | " + string.Join(" | ", labels.Select(p => m.Confusion.GetValueOrDefault((g, p)).ToString(CultureInfo.InvariantCulture))) + " |");
        return L;
    }

    public static List<string> FailureLines(Metrics m, int limit, HashSet<string>? only = null)
    {
        var L = new List<string> { "", "#### Per-case failures", "" };
        int shown = 0;
        foreach (var r in m.Cases)
        {
            if (r.Exact || (only is not null && !only.Contains(r.CaseId))) continue;
            shown++;
            if (shown > limit)
            {
                L.Add($"... {m.Cases.Count(x => !x.Exact) - limit} more not shown (use --failures N)");
                break;
            }
            var head = $"* **{r.CaseId}** gold={r.GoldStatus} pred={(r.PredStatus.Length > 0 ? r.PredStatus : "(missing)")} step-F1={Pct(r.F1Full)} LCS={Pct(r.LcsFull)}";
            if (r.Error.Length > 0) head += $" ERROR: {r.Error}";
            L.Add(head);
            if (r.OrderOnly) L.Add("  * same steps, wrong ORDER");
            L.AddRange(r.Missing.Select(s => $"  * - missing: {StepStr(s)}"));
            L.AddRange(r.Extra.Select(s => $"  * + extra:   {StepStr(s)}"));
        }
        if (shown == 0) L.Add("(none)");
        return L;
    }

    public static (string Text, Dictionary<string, Metrics> Metrics) BuildReport(Dictionary<string, Row> gold, Dictionary<string, Row> pred, string splitArg, int failures, bool revealHeldout, string label)
    {
        var splits = AssignSplits(gold);
        var dev = gold.Keys.Where(c => splits[c] == "dev").OrderBy(c => c, StringComparer.Ordinal).ToList();
        var held = gold.Keys.Where(c => splits[c] == "heldout").OrderBy(c => c, StringComparer.Ordinal).ToList();
        var all = gold.Keys.OrderBy(c => c, StringComparer.Ordinal).ToList();
        var output = new List<string>
        {
            $"## Score: {label}", "",
            $"labeled cases {all.Count} = dev {dev.Count} + not-tuned-against-in-Phase-2-3 {held.Count} (split is deterministic and stratified; see AssignSplits)", "",
        };
        var ms = new Dictionary<string, Metrics>();
        var wanted = splitArg switch
        {
            "all" => new[] { ("all", all) },
            "dev" => new[] { ("dev", dev) },
            "heldout" => new[] { ("not-tuned", held) },
            "both" => new[] { ("dev", dev), ("not-tuned", held), ("all", all) },
            _ => throw new ArgumentException($"unknown split {splitArg}"),
        };
        foreach (var (name, ids) in wanted)
        {
            var m = Score(gold, pred, ids);
            ms[name] = m;
            output.AddRange(SummaryLines(m, $"{name} ({ids.Count} cases)"));
            if (name == "dev" || (name == "not-tuned" && revealHeldout) || (name == "all" && splitArg == "all"))
                output.AddRange(FailureLines(m, failures, name == "all" && !revealHeldout ? dev.ToHashSet() : null));
            else if (name == "not-tuned")
                output.AddRange(new[] { "", "#### Per-case failures", "", "(hidden on purpose: the not-tuned cases are only used for the aggregate number)" });
            output.Add("");
        }
        return (string.Join("\n", output), ms);
    }
}
