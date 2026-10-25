namespace Auditor.Tests;

/// <summary>
/// Invariants that must hold for EVERY output row, checked independently of Validate.cs (own tables, own code), on the pipeline's fresh answers for the 518 requests and the 32 samples,
/// the committed output.csv, the 32 gold answers (they prove the invariants themselves are right) and the 23 synthetic cases. A crafted bad row for each invariant proves the checker can fail.
/// </summary>
public class InvariantsTests
{
    // AGENTS.md 6.3 / 6.4, typed out again on purpose (not read from Schema.cs): phase order, operation order inside a phase, state of each operation.
    private static readonly string[] PhaseOrder = { "FINDING", "REMEDIATION", "VERIFICATION" };
    private static readonly (string Op, string Phase, string State)[] Operations =
    {
        ("REPORT_REMAINING_RECORD", "FINDING", "PRESENT"), ("REPORT_EXPOSED_PII", "FINDING", "PRESENT"), ("REPORT_ACTIVE_DATA_FLOW", "FINDING", "ACTIVE"), ("REPORT_RESTORE_RISK", "FINDING", "ACTIVE"),
        ("DISABLE_DATA_FLOW", "REMEDIATION", "DISABLED"), ("BLOCK_RESTORE", "REMEDIATION", "BLOCKED"), ("DELETE_RECORD", "REMEDIATION", "ABSENT"),
        ("REDACT_RECORD", "REMEDIATION", "REDACTED"), ("ANONYMIZE_RECORD", "REMEDIATION", "ANONYMIZED"),
        ("VERIFY_RECORD_ABSENT", "VERIFICATION", "ABSENT"), ("VERIFY_RECORD_PRESENT", "VERIFICATION", "PRESENT"), ("VERIFY_RECORD_REDACTED", "VERIFICATION", "REDACTED"),
        ("VERIFY_RECORD_ANONYMIZED", "VERIFICATION", "ANONYMIZED"), ("VERIFY_DATA_FLOW_DISABLED", "VERIFICATION", "DISABLED"), ("VERIFY_RESTORE_BLOCKED", "VERIFICATION", "BLOCKED"),
    };
    private static readonly string[] Fields = { "step_no", "phase", "system_id", "resource_id", "operation", "state", "evidence_id" };
    private static readonly Dictionary<string, string[]> FixOfFinding = new()
    {
        ["REPORT_REMAINING_RECORD"] = new[] { "DELETE_RECORD" }, ["REPORT_EXPOSED_PII"] = new[] { "REDACT_RECORD", "ANONYMIZE_RECORD" },
        ["REPORT_ACTIVE_DATA_FLOW"] = new[] { "DISABLE_DATA_FLOW" }, ["REPORT_RESTORE_RISK"] = new[] { "BLOCK_RESTORE" },
    };

    public sealed class St
    {
        public long StepNo; public string Phase = "", System = "", Resource = "", Operation = "", State = "", Evidence = "";
        public List<string> Keys = new();
    }

    private static string S(object? v) => v as string ?? "";

    internal static St FromFields(Dictionary<string, object?> d) => new()
    {
        StepNo = d["step_no"] is long l ? l : -1, Phase = S(d["phase"]), System = S(d["system_id"]), Resource = S(d["resource_id"]), Operation = S(d["operation"]),
        State = S(d["state"]), Evidence = S(d["evidence_id"]), Keys = d.Keys.ToList(),
    };

    internal static List<St> FromSteps(IEnumerable<Step> steps) => steps.Select(s => FromFields(s.ToJson())).ToList();

    internal static List<string> Violations(string status, List<St> steps, Func<string, DateTime?>? citable = null)
    {
        var output = new List<string>();
        if (status is not ("COMPLETE" or "INCOMPLETE" or "NOT_APPLICABLE")) return new() { $"status '{status}'" };
        if (status == "NOT_APPLICABLE") return steps.Count != 0 ? new() { "NOT_APPLICABLE row is not []" } : output;
        if (steps.Count == 0) return new() { $"{status} row has no steps" };
        for (int i = 0; i < steps.Count; i++)
            if (!steps[i].Keys.ToHashSet().SetEquals(Fields)) return new() { $"step {i + 1}: fields {string.Join(",", steps[i].Keys)}" };      // key order is irrelevant (AGENTS.md 6.3)
        if (!steps.Select(s => s.StepNo).SequenceEqual(Enumerable.Range(1, steps.Count).Select(x => (long)x))) output.Add("step_no is not 1..n in array order");
        var phases = steps.Select(s => Array.IndexOf(PhaseOrder, s.Phase)).ToList();
        if (!phases.SequenceEqual(phases.OrderBy(x => x))) output.Add("phases are not in order FINDING, REMEDIATION, VERIFICATION");
        var keys = steps.Select(s => (Array.IndexOf(PhaseOrder, s.Phase), Array.FindIndex(Operations, o => o.Op == s.Operation), s.System, s.Resource)).ToList();
        var sorted = keys.OrderBy(k => k, Comparer<(int, int, string, string)>.Create((a, b) =>
        {
            int c = a.Item1.CompareTo(b.Item1); if (c != 0) return c;
            c = a.Item2.CompareTo(b.Item2); if (c != 0) return c;
            c = string.CompareOrdinal(a.Item3, b.Item3); return c != 0 ? c : string.CompareOrdinal(a.Item4, b.Item4);
        })).ToList();
        if (!keys.SequenceEqual(sorted)) output.Add("steps are not in canonical order (phase, operation, system_id, resource_id)");
        if (keys.Distinct().Count() != keys.Count) output.Add("duplicate step");
        foreach (var s in steps)
        {
            if (!Operations.Any(o => o.Phase == s.Phase && o.Op == s.Operation && o.State == s.State)) output.Add($"step {s.StepNo}: {s.Phase}/{s.Operation}/{s.State} is not an allowed combination");
            if (s.System.Length == 0 || s.Resource.Length == 0) output.Add($"step {s.StepNo}: empty system_id/resource_id");
        }
        if (status == "COMPLETE")
        {
            if (!steps.Select(s => s.Phase).ToHashSet().SetEquals(new[] { "VERIFICATION" })) output.Add("COMPLETE row contains a non-VERIFICATION step");
            foreach (var s in steps.Where(s => s.Evidence.Length == 0)) output.Add($"COMPLETE step {s.StepNo} ({s.Operation} {s.Resource}) has no evidence_id");
        }
        else
        {
            if (!steps.Select(s => s.Phase).ToHashSet().SetEquals(PhaseOrder)) output.Add("INCOMPLETE row lacks one of the three phases");
            foreach (var s in steps)
            {
                if (s.Phase == "FINDING" && s.Evidence.Length == 0) output.Add($"finding {s.StepNo} has no evidence_id");
                if (s.Phase != "FINDING" && s.Evidence.Length > 0) output.Add($"future-work step {s.StepNo} carries evidence_id '{s.Evidence}'");
            }
            foreach (var f in steps.Where(s => s.Phase == "FINDING"))
            {
                var fixes = steps.Where(s => s.Phase == "REMEDIATION" && s.System == f.System && s.Resource == f.Resource).Select(s => s.Operation);
                if (!fixes.Any(FixOfFinding[f.Operation].Contains)) output.Add($"finding {f.Operation} {f.Resource} has no matching remediation");
            }
        }
        if (citable is not null)
            foreach (var s in steps.Where(s => s.Evidence.Length > 0 && citable(s.Evidence) is null)) output.Add($"step {s.StepNo} cites unknown id '{s.Evidence}'");
        return output;
    }

    internal static Dictionary<string, List<string>> CheckRows(IEnumerable<(string Id, string Status, List<St> Steps)> rows, IReadOnlyDictionary<string, DateTime>? auditAt = null, Func<string, DateTime?>? citable = null)
    {
        var bad = new Dictionary<string, List<string>>();
        foreach (var (cid, status, steps) in rows)
        {
            var v = Violations(status, steps, citable);
            if (auditAt is not null && citable is not null)
                foreach (var s in steps.Where(s => s.Evidence.Length > 0))
                    if (citable(s.Evidence) is { } t && t > auditAt[cid]) v.Add($"step {s.StepNo} cites {s.Evidence} captured after audit_at");
            if (v.Count > 0) bad[cid] = v;
        }
        return bad;
    }

    // ------------------------------------------------------------------------------------------------------------------ the checker can fail
    private static St Step(long no, string phase, string op, string rid = "x_1", string system = "s", string ev = "") =>
        new() { StepNo = no, Phase = phase, System = system, Resource = rid, Operation = op, State = Operations.First(o => o.Op == op).State, Evidence = ev, Keys = Fields.ToList() };

    private static List<St> Good() => new()
    {
        Step(1, "FINDING", "REPORT_REMAINING_RECORD", ev: "e1"), Step(2, "REMEDIATION", "DELETE_RECORD"), Step(3, "VERIFICATION", "VERIFY_RECORD_ABSENT"),
    };

    private static St With(St s, long? no = null, string? ev = null) { var c = new St { StepNo = no ?? s.StepNo, Phase = s.Phase, System = s.System, Resource = s.Resource, Operation = s.Operation, State = s.State, Evidence = ev ?? s.Evidence, Keys = s.Keys }; return c; }

    [Fact]
    public void GoodRowHasNoViolation() => Assert.Empty(Violations("INCOMPLETE", Good()));

    [Fact]
    public void EachInvariantIsDetected()
    {
        var g = Good();
        var cases = new Dictionary<string, (string, List<St>)>
        {
            ["phase order"] = ("INCOMPLETE", new() { g[1], g[0], g[2] }),
            ["step_no"] = ("INCOMPLETE", new() { With(g[0]), With(g[1], no: 3), With(g[2], no: 2) }),
            ["canonical order"] = ("INCOMPLETE", new() { g[0], Step(2, "REMEDIATION", "DELETE_RECORD", rid: "b"), Step(3, "REMEDIATION", "DISABLE_DATA_FLOW", rid: "a"), Step(4, "VERIFICATION", "VERIFY_RECORD_ABSENT") }),
            ["COMPLETE verification-only"] = ("COMPLETE", new() { With(g[0]), With(g[1]), With(g[2], ev: "e1") }),
            ["COMPLETE needs evidence"] = ("COMPLETE", new() { Step(1, "VERIFICATION", "VERIFY_RECORD_ABSENT") }),
            ["NOT_APPLICABLE is []"] = ("NOT_APPLICABLE", g),
            ["future work has no evidence"] = ("INCOMPLETE", new() { g[0], With(g[1], ev: "e1"), g[2] }),
            ["finding needs evidence"] = ("INCOMPLETE", new() { With(g[0], ev: ""), g[1], g[2] }),
            ["finding needs remediation"] = ("INCOMPLETE", new() { g[0], Step(2, "REMEDIATION", "REDACT_RECORD"), g[2] }),
            ["unknown evidence id"] = ("INCOMPLETE", new() { With(g[0], ev: "e_nope"), g[1], g[2] }),
        };
        foreach (var (name, (status, steps)) in cases)
            Assert.True(Violations(status, steps, citable: i => i == "e_nope" ? null : DateTime.MinValue).Count > 0, name);
    }

    // ------------------------------------------------------------------------------------------------------------------ real data
    private static readonly Lazy<(Dataset Ds, Dataset Samples, List<(string, string, List<St>)> Rows, Dictionary<string, DateTime> Audit)> Fresh = new(() =>
    {
        var ds = Dataset.Load(Repo.Dataset);
        var samples = Dataset.Load(Repo.Dataset, "sample_deletion_requests.csv");
        var a = new CaseAuditor(Repo.Dataset);
        var rows = ds.CaseOrder.Concat(samples.CaseOrder).Select(c => { var r = a.AuditCase(c); return (c, r.Status, FromSteps(r.Steps)); }).ToList();
        return (ds, samples, rows, ds.Requests.Concat(samples.Requests).ToDictionary(r => r.CaseId, r => r.AuditAt));
    });

    [DatasetFact]
    public void All550RowsHoldEveryInvariant()
    {
        var f = Fresh.Value;
        Assert.Equal(550, f.Rows.Count);
        Assert.Empty(CheckRows(f.Rows, f.Audit, f.Ds.CitableTime));
    }

    [DatasetFact]
    public void CompleteRowsAreVerificationOnlyAndAlwaysCite()
    {
        var complete = Fresh.Value.Rows.Where(r => r.Item2 == "COMPLETE").SelectMany(r => r.Item3.Select(s => (r.Item1, s))).ToList();
        Assert.True(complete.Count > 400);
        Assert.Empty(complete.Where(x => x.s.Phase != "VERIFICATION" || x.s.Evidence.Length == 0).Select(x => (x.Item1, x.s.Operation)));
    }

    [DatasetFact]
    public void NotApplicableRowsAreEmpty()
    {
        var na = Fresh.Value.Rows.Where(r => r.Item2 == "NOT_APPLICABLE").ToList();
        Assert.True(na.Count >= 10);
        Assert.Empty(na.Where(r => r.Item3.Count != 0).Select(r => r.Item1));
    }

    [DatasetFact]
    public void RowCountsByStatusAreAllThreeKinds() =>
        Assert.True(Fresh.Value.Rows.Select(r => r.Item2).ToHashSet().SetEquals(new[] { "COMPLETE", "INCOMPLETE", "NOT_APPLICABLE" }));

    private static List<(string, string, List<St>)> RowsOf(string path) =>
        Csv.ReadRows(path).Select(r => (r.G("case_id"), r.G("deletion_status"), Py.AsList(Py.ParseJson(r.G("steps_json"))).Select(x => FromFields(Py.AsDict(x))).ToList())).ToList();

    [DatasetFact]
    public void CommittedOutputCsvHoldsEveryInvariant()
    {
        var path = Path.Combine(Repo.Root, "output.csv");
        if (!File.Exists(path)) return;
        var ds = Dataset.Load(Repo.Dataset);
        var rows = RowsOf(path);
        Assert.Equal(ds.CaseOrder, rows.Select(r => r.Item1));
        Assert.Empty(CheckRows(rows, ds.Requests.ToDictionary(r => r.CaseId, r => r.AuditAt), ds.CitableTime));
    }

    [DatasetFact]
    public void GoldAnswersHoldTheSameInvariants()
    {
        // The invariants are not stricter than the organisers' own answers.
        var ds = Dataset.Load(Repo.Dataset, "sample_deletion_requests.csv");
        var rows = RowsOf(Path.Combine(Repo.Dataset, "sample_output.csv"));
        Assert.Equal(32, rows.Count);
        Assert.Empty(CheckRows(rows, ds.Requests.ToDictionary(r => r.CaseId, r => r.AuditAt), ds.CitableTime));
    }

    [Fact]
    public void AllSyntheticCasesHoldEveryInvariant()
    {
        var (cases, rules) = ExtraCases.Parse(Path.Combine(Repo.Code, "evaluation", "extra_cases.md"));
        foreach (var c in cases)
        {
            using var d = new TempDir();
            ExtraCases.BuildDataset(c, d.Path, rules);
            var (status, steps) = ExtraCases.RunPipeline(c, rules);
            var ds = Dataset.Load(d.Path);
            var bad = CheckRows(new[] { (c.Id, status, FromSteps(steps)) }, new Dictionary<string, DateTime> { [c.Id] = ds.Requests[0].AuditAt }, ds.CitableTime);
            Assert.True(bad.Count == 0, c.Id + ": " + string.Join("; ", bad.Values.SelectMany(x => x)));
        }
    }
}
