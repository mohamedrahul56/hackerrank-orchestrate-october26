using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Auditor.Evidence;
using static Auditor.Tools.ExtraCases;
using XPerson = Auditor.Tools.ExtraCases.Person;

namespace Auditor.Tools;

/// <summary>
/// Edge-case sweep: runs the real pipeline (library and command line) on small synthetic datasets that each isolate one awkward input and compares what it does with what it should do.
/// Every scenario states its EXPECTED behaviour in words before the code runs (from the problem statement and from the principle that an unusual input must give a valid, conservative,
/// deterministic answer and never an exception or a wrong claim). The check is a predicate over what was observed, not a snapshot of whatever the code did. Verdicts:
///   OK        observed behaviour meets the expectation
///   DEFECT    it does not (a real bug: it gets a regression test and a fix)
///   DECISION  the specification is silent; the observed behaviour is conservative and is written down as a decision, not a bug
/// The synthetic world is the one of <see cref="ExtraCases"/> (same builder, same rule table), so the scenarios use only participant-visible file shapes and no case-specific logic.
/// </summary>
public static partial class EdgeSweep
{
    public sealed record Row(string Sid, string Area, string Scenario, string Expected, string Observed, string Verdict, string Note = "");

    public delegate (string Observed, string Verdict, string Note) Body(string tmp);

    private sealed record Scenario(string Sid, string Area, string Title, string Expected, Body Run);

    private static readonly List<Scenario> Scenarios = new();

    private static readonly XPerson A = P(1, "Alma Reyes", "alma.reyes@example.test");

    private static readonly string[] Header = { "case_id", "email", "requested_at", "reported_status", "reported_at", "audit_at", "request_text" };

    private static readonly Dictionary<string, string> Short = new(StringComparer.Ordinal)
    {
        ["REPORT_REMAINING_RECORD"] = "found", ["REPORT_EXPOSED_PII"] = "exposed", ["REPORT_ACTIVE_DATA_FLOW"] = "flow", ["REPORT_RESTORE_RISK"] = "restore",
        ["DISABLE_DATA_FLOW"] = "disable", ["BLOCK_RESTORE"] = "block", ["DELETE_RECORD"] = "delete", ["REDACT_RECORD"] = "redact", ["ANONYMIZE_RECORD"] = "anonymize",
        ["VERIFY_RECORD_ABSENT"] = "v-absent", ["VERIFY_RECORD_PRESENT"] = "v-present", ["VERIFY_RECORD_REDACTED"] = "v-redacted",
        ["VERIFY_RECORD_ANONYMIZED"] = "v-anon", ["VERIFY_DATA_FLOW_DISABLED"] = "v-disabled", ["VERIFY_RESTORE_BLOCKED"] = "v-blocked",
    };

    // ----------------------------------------------------------------------------- observations
    public sealed class Seen
    {
        public string Status = "";
        public List<Step> Steps = new();
        public List<string> Notes = new();
        public string Error = "";
        public List<string> Cited = new();

        public List<(string P, string Op, string Rid)> Ops => Steps.Select(s => (s.Phase[..1], Short[s.Operation], s.ResourceId)).ToList();

        public bool Has(string op, string rid) => Ops.Any(o => o.Op == op && o.Rid == rid);

        public string Text()
        {
            if (Error.Length > 0) return $"EXCEPTION {Error}";
            if (Steps.Count == 0)
            {
                var note = Notes.Count > 0 ? $" ({Notes[0][..Math.Min(70, Notes[0].Length)]})" : "";
                return $"{Status}, no steps{note}";
            }
            var by = new List<((string P, string Op) Key, List<string> Rids)>();
            foreach (var (p, op, rid) in Ops)
            {
                var hit = by.FindIndex(x => x.Key == (p, op));
                if (hit < 0) by.Add(((p, op), new List<string> { rid })); else by[hit].Rids.Add(rid);
            }
            var parts = by.Where(x => x.Key.P is "F" or "R").Select(x => $"{x.Key.Op} {string.Join(", ", x.Rids)}").ToList();
            if (parts.Count == 0) parts.Add("no findings");
            var ver = by.Where(x => x.Key.P == "V").Select(x => $"{x.Key.Op} {string.Join(", ", x.Rids)}").ToList();
            return $"{Status}: " + string.Join("; ", parts) + (ver.Count > 0 ? " | verify: " + string.Join("; ", ver) : "");
        }
    }

    private static readonly List<(string P, string Op, string Rid)> BaseOps = new()
    {
        ("F", "found", "cache_101"), ("R", "delete", "cache_101"), ("V", "v-absent", "account_101"), ("V", "v-absent", "cache_101"), ("V", "v-present", "audit_record_102"),
    };

    private static Seen SeenFrom(CaseResult r) => new() { Status = r.Status, Steps = r.Steps, Notes = r.Notes.ToList(), Cited = r.Steps.Where(s => s.EvidenceId.Length > 0).Select(s => s.EvidenceId).ToList() };

    private static bool IsBase(Seen s) => s.Status == "INCOMPLETE" && s.Ops.SequenceEqual(BaseOps);

    private static bool Fallback(Seen s) => s.Status == "NOT_APPLICABLE" && s.Steps.Count == 0 && s.Error.Length == 0;

    // ----------------------------------------------------------------------------- scenario registration and verdict helpers
    private static void Add(string sid, string area, string title, string expected, Body run) => Scenarios.Add(new Scenario(sid, area, title, expected, run));

    private static (string, string, string) OkIf(bool cond, string observed, string note = "") => (observed, cond ? "OK" : "DEFECT", note);

    private static (string, string, string) Decision(string observed, string note) => (observed, "DECISION", note);

    private static string Last(string err) => err.Trim().Length == 0 ? "-" : err.Trim().Split('\n')[^1];

    private static string Clip(string s, int n) => s[..Math.Min(n, s.Length)];

    // ----------------------------------------------------------------------------- the synthetic world
    /// <summary>A temporary dataset folder built from one case definition of <see cref="ExtraCases"/>, with helpers to damage or extend it.</summary>
    public sealed class World
    {
        public XPerson User;
        public string Root;
        public string Tmp;
        public string CaseId;
        public Case Case;
        public string RulesCsv;

        public World(string tmp, List<Res>? resources = null, XPerson? user = null, List<Ev>? evidence = null, string text = DeleteText, Action<Req>? request = null, List<XPerson>? others = null,
                     string? rulesCsv = null, string caseId = "XC-01")
        {
            User = user ?? A;
            Tmp = tmp;
            Directory.CreateDirectory(tmp);
            Root = Path.Combine(tmp, "ds");
            var res = resources ?? Core(1).Concat(new[] { R("cache_101", "CACHE_ENTRY", "PRESENT") }).ToList();
            Case = Mk(caseId, "sweep", "sweep", User, res, new(), text: text, evidence: evidence, others: others, status: "INCOMPLETE");
            request?.Invoke(Case.Request);
            RulesCsv = rulesCsv ?? ExtraCases.RulesCsv;
            BuildDataset(Case, Root, RulesCsv);
            CaseId = caseId;
        }

        public string[] RowOf(Dictionary<string, string>? over = null)
        {
            var rq = Case.Request;
            var b = new Dictionary<string, string>
            {
                ["case_id"] = CaseId, ["email"] = rq.Email, ["requested_at"] = rq.RequestedAt, ["reported_status"] = rq.ReportedStatus, ["reported_at"] = rq.ReportedAt,
                ["audit_at"] = rq.AuditAt, ["request_text"] = rq.Text,
            };
            foreach (var kv in over ?? new()) b[kv.Key] = kv.Value;
            return Header.Select(h => b[h]).ToArray();
        }

        public string[] RowOf(params (string Key, string Value)[] over) => RowOf(over.ToDictionary(x => x.Key, x => x.Value));

        public void WriteRequests(IEnumerable<string[]> rows, string[]? header = null) =>
            File.WriteAllText(Path.Combine(Root, "deletion_requests.csv"), Csv.FormatRow(header ?? Header) + string.Concat(rows.Select(r => Csv.FormatRow(r))), new UTF8Encoding(false));

        public void ManifestAdd(string fileId, string rel, string capturedAt) =>
            File.AppendAllText(Path.Combine(Root, "evidence_files.csv"), Csv.FormatRow(new[] { fileId, rel, "synthetic", capturedAt }), new UTF8Encoding(false));

        public string Raw(string fileId, string name, byte[] data, string capturedAt = TE2)
        {
            var p = Path.Combine(Root, "evidence", name);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, data);
            ManifestAdd(fileId, $"evidence/{name}", capturedAt);
            return p;
        }

        public void AppendCsv(string name, IEnumerable<string[]> rows) =>
            File.AppendAllText(Path.Combine(Root, name), string.Concat(rows.Select(r => Csv.FormatRow(r))), new UTF8Encoding(false));

        public string EvidencePath(string fileId) => Path.Combine(Root, "evidence", fileId + ".json");

        public Dictionary<string, object?> ReadDoc(string fileId) => (Dictionary<string, object?>)Compat.ParseJson(File.ReadAllText(EvidencePath(fileId)))!;

        public void WriteDoc(string fileId, Dictionary<string, object?> doc) => File.WriteAllText(EvidencePath(fileId), Compat.DumpsCompact(doc), new UTF8Encoding(false));

        public Seen Audit(string? caseId = null, AuditConfig? config = null)
        {
            try { return SeenFrom(new CaseAuditor(Root, config ?? new AuditConfig()).AuditCase(caseId ?? CaseId)); }
            catch (Exception e) { return new Seen { Error = $"{e.GetType().Name}: {e.Message}" }; }          // an escaped exception is itself the finding
        }

        public CaseAuditor NewAuditor(AuditConfig? config = null) => new(Root, config ?? new AuditConfig());

        /// <summary>The command line on this dataset: (exit code, stderr, rows by case id, case ids in file order).</summary>
        public (int Rc, string Err, Dictionary<string, (string Status, List<object?> Steps)> Rows, List<string> Order) Cli(params string[] extra)
        {
            var outPath = Path.Combine(Tmp, "o.csv");
            var err = new StringWriter();
            int rc;
            try { rc = global::Auditor.Cli.Cli.Run(new[] { "--dataset", Root, "--out", outPath, "--images", "off" }.Concat(extra).ToArray(), new StringWriter(), err); }
            catch (Exception e) { return (99, $"EXCEPTION {e.GetType().Name}: {e.Message}", new(), new()); }
            return (rc, err.ToString(), ReadOutput(outPath, out var order), order);
        }

        public static Dictionary<string, (string Status, List<object?> Steps)> ReadOutput(string path, out List<string> order)
        {
            var rows = new Dictionary<string, (string, List<object?>)>(StringComparer.Ordinal);
            order = new();
            if (!File.Exists(path)) return rows;
            foreach (var r in Csv.Parse(File.ReadAllText(path, new UTF8Encoding(false))).Skip(1).Where(r => r.Count >= 3))
            {
                rows[r[0]] = (r[1], (List<object?>)Compat.ParseJson(r[2])!);
                order.Add(r[0]);
            }
            return rows;
        }
    }

    // ----------------------------------------------------------------------------- running
    private static void RegisterAll()
    {
        Scenarios.Clear();
        RequestScenarios();
        TimestampScenarios();
        IdScenarios();
        EmailScenarios();
        OldEmailScenarios();
        MultiRequestScenarios();
        AfterAuditScenarios();
        ClaimScenarios();
        LinkScenarios();
        RetainScenarios();
        CorruptScenarios();
        PngScenarios();
        ReadingsScenarios();
        InjectionScenarios();
    }

    /// <summary>Questions about the real 518 requests that the synthetic worlds cannot answer: several requests per user, shared reported_at.</summary>
    private static List<Row> RealDataRows(string root)
    {
        var rows = new List<Row>();
        var ds = Path.Combine(root, "dataset");
        if (!File.Exists(Path.Combine(ds, "deletion_requests.csv"))) return rows;
        var (reqs, _, _) = Dataset.LoadRequestsLenient(Path.Combine(ds, "deletion_requests.csv"));
        var a = new CaseAuditor(ds);
        var byUser = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in reqs)
            foreach (var u in a.Res.Resolve(r.Email, r.RequestedAt))
            {
                if (!byUser.TryGetValue(u.UserId, out var l)) byUser[u.UserId] = l = new();
                l.Add(r.CaseId);
            }
        var multi = byUser.Where(kv => kv.Value.Count > 1).ToList();
        var shared = reqs.GroupBy(r => Compat.FormatTs(r.ReportedAt)).Where(g => g.Count() > 1).ToList();
        var detail = string.Join("; ", multi.Select(kv => $"{kv.Key}: " + string.Join(", ", kv.Value.Select(c =>
        {
            var res = a.AuditCase(c);
            return $"{c} {res.Status} (audit_at {a.Requests[c].AuditAt:yyyy-MM-dd}, {res.Steps.Count} steps)";
        }))));
        rows.Add(new Row("D01", "real data", "users with more than one request in deletion_requests.csv",
            "each request answered independently from the state at its own audit_at (different audit_at, evidence windows)",
            multi.Count > 0 ? $"{multi.Count} user(s) with 2+ requests ({multi.Sum(kv => kv.Value.Count)} requests): {detail}" : "none: every request belongs to a different user",
            "OK", "each is answered through the same per-case window; see U01 for the synthetic check"));
        var assigned = a.AccountAsg.Values.Where(x => x.Resource is not null).Select(x => x.Resource!).ToList();
        rows.Add(new Row("D02", "real data", "requests that share the same reported_at", "no account assigned to two cases; ambiguity leaves the step out",
            $"{shared.Count} reported_at values are shared by 2+ requests ({shared.Sum(g => g.Count())} requests)",
            assigned.Distinct().Count() == assigned.Count ? "OK" : "DEFECT", "no account id is assigned to more than one case"));
        return rows;
    }

    public static List<Row> RunAll(string? only = null, string? repoRoot = null)
    {
        RegisterAll();
        var output = new List<Row>();
        foreach (var s in Scenarios)
        {
            if (only is not null && !s.Sid.StartsWith(only, StringComparison.Ordinal)) continue;
            var d = Directory.CreateTempSubdirectory("edge_").FullName;
            string observed, verdict, note;
            try
            {
                (observed, verdict, note) = s.Run(d);
                observed = observed.Replace(d, "<tmp>", StringComparison.Ordinal);              // temp folder names would make the report differ on every run
            }
            catch (Exception e)                                                                // a scenario that itself crashes is a finding about the harness or the code
            {
                (observed, verdict, note) = ($"EXCEPTION in the scenario: {e.GetType().Name}: {e.Message}", "DEFECT", "");
            }
            finally { try { Directory.Delete(d, true); } catch (IOException) { } }
            output.Add(new Row(s.Sid, s.Area, s.Title, s.Expected, observed, verdict, note));
        }
        if (only is null && repoRoot is not null) output.AddRange(RealDataRows(repoRoot));
        return output;
    }

    // ----------------------------------------------------------------------------- the report
    private static readonly Dictionary<string, (string Why, string Fix, string Test)> Fixes = new(StringComparer.Ordinal)
    {
        ["T06"] = ("a time without a zone was read in the machine's local time zone, so the answer depended on where the program ran",
                   "`Compat.ParseTs` reads a zone-less time as UTC (`Compat.cs`)", "ZoneLessTimestampTests"),
        ["R09"] = ("a legal-hold record whose `retained_until` could not be parsed was dropped whole, so the retained record was never verified",
                   "an unreadable end date counts as no end date (the hold stays active) and adds a warning (`Evidence/`)", "UnreadableHoldEndTests"),
        ["F12"] = ("a manifest path such as `../outside.json` or an absolute path was opened: a file outside dataset/ could change an answer",
                   "manifest paths are normalised without following symlinks and must stay inside the dataset folder; others are skipped with a warning (`Evidence/Load.cs`)", "ManifestPathsStayInsideTheDatasetTests"),
        ["O06"] = ("a request_text above 128 KiB made the CSV reader raise and ended the whole run (every answer lost)",
                   "the CSV field limit is 16 MiB; a field beyond it becomes a clear `DatasetException` (`Csv.cs`)", "VeryLongRequestTextTests"),
        ["O09"] = ("a `resource_id` such as `cache_101; DROP TABLE users` read from evidence was copied into an output step",
                   "observations whose resource id is not `<name>_<number>` are dropped with a warning, and the validator rejects such a step (`Schema.cs`, `Evidence/Load.cs`, `Validate.cs`)", "OnlyRealResourceIdsReachTheOutputTests"),
        ["F18"] = ("an unknown `required_action` was accepted and then raised for every case touching that category: quiet NOT_APPLICABLE rows",
                   "the rules file is read strictly and an unknown action stops the run with a message naming the rule (`Policy.cs`)", "RuleFileProblemsAreLoudTests"),
        ["F20"] = ("a missing `deletion_rules.csv` was an unhandled error", "same: a `DatasetException`, exit code 2", "RuleFileProblemsAreLoudTests"),
        ["F21"] = ("a rules file without the `required_action` column was an unhandled error", "same: a `DatasetException`, exit code 2", "RuleFileProblemsAreLoudTests"),
    };

    private static string Cell(string s) => Regex.Replace(s, @"\{[^{}]*\}", m => m.Value.Replace("'", "", StringComparison.Ordinal), RegexOptions.CultureInvariant).Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    public static string Render(List<Row> rows)
    {
        int Count(string v) => rows.Count(r => r.Verdict == v);
        var L = new List<string>
        {
            "# Edge-case sweep", "",
            "Generated by `dotnet run --project code/tools/Auditor.Tools -- edge-sweep` (also run by `EdgeSweepTests`). Every scenario builds a small synthetic dataset, runs the real pipeline, "
            + "and compares what it did with an expectation written in words before the run. Verdicts: **OK** meets the expectation; **DEFECT** does not; **DECISION** the specification is silent, "
            + "the behaviour is conservative and written down.", "",
            $"Totals: {rows.Count} scenarios, OK {Count("OK")}, DECISION {Count("DECISION")}, DEFECT {Count("DEFECT")}.", "",
            "| id | area | scenario | expected behaviour | observed behaviour | verdict |", "|---|---|---|---|---|---|",
        };
        foreach (var r in rows)
            L.Add($"| {r.Sid} | {Cell(r.Area)} | {Cell(r.Scenario)} | {Cell(r.Expected)} | {Cell(r.Observed)}{(r.Note.Length > 0 ? " (" + Cell(r.Note) + ")" : "")} | **{r.Verdict}** |");
        var now = rows.ToDictionary(r => r.Sid, r => r, StringComparer.Ordinal);
        L.AddRange(new[]
        {
            "", "## Defects this sweep found, and what was done", "",
            "The first run of this sweep (on the earlier Python implementation, change log row 44) found **8 defects** among its first 112 scenarios. Each was fixed with the smallest general change, "
            + "no new heuristic and nothing case-specific, and has a regression test in `code/tests/Auditor.Tests/EdgeRegressionsTests.cs` that fails without the fix. The sweep was then ported to C# "
            + "(this file is its output on the C# code), where all of them stay fixed.", "",
            "| id | what was wrong | fix | regression test | verdict now |", "|---|---|---|---|---|",
        });
        foreach (var (sid, f) in Fixes)
            L.Add($"| {sid} | {Cell(f.Why)} | {Cell(f.Fix)} | `{f.Test}` | **{(now.TryGetValue(sid, out var r) ? r.Verdict : "?")}** |");
        var decisions = rows.Where(r => r.Verdict == "DECISION").ToList();
        if (decisions.Count > 0)
        {
            L.AddRange(new[] { "", "## Decisions (specification silent)", "" });
            L.AddRange(decisions.Select(r => $"* **{r.Sid}** {r.Scenario}: {r.Note}"));
        }
        return string.Join("\n", L) + "\n";
    }

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var root = global::Auditor.Cli.Cli.RepoRoot();
        var only = AgentProofs.Option(args, "--only");
        var outPath = AgentProofs.Option(args, "--out") ?? Path.Combine(root, "code", "evaluation", "edge_case_sweep.md");
        var rows = RunAll(only, root);
        var text = Render(rows);
        if (only is null) File.WriteAllText(outPath, text, new UTF8Encoding(false));
        if (!args.Contains("--quiet")) stdout.WriteLine(text);
        stderr.WriteLine(only is null ? $"wrote {outPath}" : "partial run: nothing written");
        return rows.Any(r => r.Verdict == "DEFECT") ? 1 : 0;
    }
}
