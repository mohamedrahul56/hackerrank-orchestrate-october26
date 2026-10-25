using System.Text;

namespace Auditor;

public sealed record Issue(string CaseId, string Code, string Message, string Severity = "error")
{
    public override string ToString() => $"[{Severity}] {CaseId}: {Code}: {Message}";
}

public sealed class ValidationException : Exception
{
    public ValidationException(string message) : base(message) { }
}

/// <summary>
/// Output validator. Runs on every row before it is written and again on the re-read file. It checks the contract (schema, ordering, evidence rules, status/steps consistency); it never
/// decides what the right answer is. Hard problems are errors; things the 32 samples always do but the spec does not strictly demand are warnings.
/// </summary>
public static class Validator
{
    private static string ListRepr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Py.StrRepr)) + "]";

    private static List<Issue> SchemaIssues(string caseId, object? steps)
    {
        if (steps is not List<object?> list) return new() { new Issue(caseId, "STEPS_NOT_LIST", $"steps_json must be a JSON array, got {Py.TypeName(steps)}") };
        var output = new List<Issue>();
        int i = 0;
        foreach (var item in list)
        {
            i++;
            if (item is not Dictionary<string, object?> s) { output.Add(new Issue(caseId, "STEP_NOT_OBJECT", $"step #{i} is not an object")); continue; }
            if (!s.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Schema.StepFields))
            {
                output.Add(new Issue(caseId, "STEP_FIELDS", $"step #{i} fields {ListRepr(s.Keys.OrderBy(k => k, StringComparer.Ordinal))} != {ListRepr(Schema.StepFields.OrderBy(k => k, StringComparer.Ordinal))}"));
                continue;
            }
            if (s["step_no"] is not (long or System.Numerics.BigInteger)) output.Add(new Issue(caseId, "STEP_NO_TYPE", $"step #{i} step_no must be an integer"));
            foreach (var f in Schema.StepFields.Skip(1))
                if (s[f] is not string) output.Add(new Issue(caseId, "FIELD_TYPE", $"step #{i} {f} must be a string (use \"\" not null)"));
            if (s["phase"] is string phase && !Schema.Phases.Contains(phase)) output.Add(new Issue(caseId, "BAD_PHASE", $"step #{i} phase {Py.StrRepr(phase)}"));
            else if (s["phase"] is string p && s["operation"] is string op && s["state"] is string st && !Schema.AllowedTriples.Contains((p, op, st)))
                output.Add(new Issue(caseId, "BAD_TRIPLE", $"step #{i} ({p}, {op}, {st}) not allowed"));
            var sys = s["system_id"] as string; var rid = s["resource_id"] as string;
            if (string.IsNullOrEmpty(sys) || string.IsNullOrEmpty(rid)) output.Add(new Issue(caseId, "EMPTY_ID", $"step #{i} has empty system_id/resource_id"));
            else if (!Schema.ValidResourceId(rid))
                output.Add(new Issue(caseId, "BAD_RESOURCE_ID", $"step #{i} resource_id {Py.StrRepr(rid.Length > 40 ? rid[..40] : rid)} is not of the form <name>_<number>"));
        }
        return output;
    }

    /// <summary>All contract checks for one row. <paramref name="ds"/>/<paramref name="auditAt"/> enable the evidence-id existence/time checks.</summary>
    public static List<Issue> ValidateCase(string caseId, string status, IReadOnlyList<Step> steps, DateTime? auditAt = null, Dataset? ds = null) =>
        ValidateCase(caseId, status, steps.Select(s => (object?)s.ToJson()).ToList(), auditAt, ds);

    public static List<Issue> ValidateCase(string caseId, string status, object? stepsJson, DateTime? auditAt = null, Dataset? ds = null)
    {
        if (!Schema.Statuses.Contains(status)) return new() { new Issue(caseId, "BAD_STATUS", $"deletion_status {Py.StrRepr(status)} not in ({string.Join(", ", Schema.Statuses.Select(Py.StrRepr))})") };
        var issues = SchemaIssues(caseId, stepsJson);
        if (issues.Count > 0) return issues;                               // later checks assume well-formed steps
        var steps = ((List<object?>)stepsJson!).Cast<Dictionary<string, object?>>().ToList();

        if (status == "NOT_APPLICABLE")
        {
            if (steps.Count > 0) issues.Add(new Issue(caseId, "NA_NOT_EMPTY", "NOT_APPLICABLE rows must have steps_json == []"));
            return issues;
        }
        if (steps.Count == 0) return new() { new Issue(caseId, "EMPTY_STEPS", $"{status} rows must contain steps") };

        string Str(Dictionary<string, object?> s, string k) => (string)s[k]!;
        // step_no consecutive from 1
        if (!steps.Select(s => System.Convert.ToInt64(s["step_no"], System.Globalization.CultureInfo.InvariantCulture)).SequenceEqual(Enumerable.Range(1, steps.Count).Select(x => (long)x)))
            issues.Add(new Issue(caseId, "STEP_NO", "step_no must be 1..n in array order"));
        // canonical order
        var keys = steps.Select(s => Schema.SortKey(Str(s, "phase"), Str(s, "operation"), Str(s, "system_id"), Str(s, "resource_id"))).ToList();
        var sorted = keys.OrderBy(k => k, Comparer<(int, int, string, string)>.Create((a, b) => Schema.CompareSortKeys(a, b))).ToList();
        if (!keys.SequenceEqual(sorted)) issues.Add(new Issue(caseId, "ORDER", "steps are not in canonical order (phase, operation, system_id, resource_id)"));
        // duplicates
        var dup = steps.GroupBy(s => (Str(s, "phase"), Str(s, "operation"), Str(s, "system_id"), Str(s, "resource_id"))).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dup.Count > 0)
            issues.Add(new Issue(caseId, "DUPLICATE", "duplicate steps: [" + string.Join(", ", dup.Take(3).Select(k => $"({Py.StrRepr(k.Item1)}, {Py.StrRepr(k.Item2)}, {Py.StrRepr(k.Item3)}, {Py.StrRepr(k.Item4)})")) + "]"));

        var phases = steps.GroupBy(s => Str(s, "phase")).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        if (status == "COMPLETE")
        {
            if (phases.Keys.Count != 1 || !phases.ContainsKey("VERIFICATION"))
                issues.Add(new Issue(caseId, "COMPLETE_PHASES", $"COMPLETE rows may only contain VERIFICATION steps, got {{{string.Join(", ", phases.Select(kv => $"{Py.StrRepr(kv.Key)}: {kv.Value}"))}}}"));
        }
        else                                                                // INCOMPLETE
        {
            foreach (var ph in Schema.Phases)
                if (phases.GetValueOrDefault(ph) == 0) issues.Add(new Issue(caseId, "INCOMPLETE_MISSING_PHASE", $"INCOMPLETE rows need {ph} steps"));
            issues.AddRange(PairingIssues(caseId, steps));
        }

        // evidence_id rules: cited only on FINDING (INCOMPLETE) or on every step (COMPLETE).
        foreach (var s in steps)
        {
            var evidence = Str(s, "evidence_id");
            bool cites = evidence.Length > 0;
            bool mustCite = Str(s, "phase") == "FINDING" || status == "COMPLETE";
            var stepNo = System.Convert.ToInt64(s["step_no"], System.Globalization.CultureInfo.InvariantCulture);
            if (mustCite && !cites) issues.Add(new Issue(caseId, "EVIDENCE_MISSING", $"step {stepNo} ({Str(s, "operation")} {Str(s, "resource_id")}) must cite evidence"));
            if (!mustCite && cites) issues.Add(new Issue(caseId, "EVIDENCE_ON_PROPOSAL", $"step {stepNo} ({Str(s, "operation")}) is future work and must have evidence_id \"\""));
            if (cites && ds is not null)
            {
                var t = ds.CitableTime(evidence);
                if (t is null) issues.Add(new Issue(caseId, "EVIDENCE_UNKNOWN", $"step {stepNo} cites unknown id {Py.StrRepr(evidence)}"));
                else if (auditAt is not null && t > auditAt)
                    issues.Add(new Issue(caseId, "EVIDENCE_AFTER_AUDIT", $"step {stepNo} cites {evidence} captured {Py.FormatTs(t.Value)} after audit_at"));
            }
        }

        // Warning only: every deletion audit in the samples verifies that the primary account is absent.
        if (!steps.Any(s => Str(s, "operation") == "VERIFY_RECORD_ABSENT" && Str(s, "system_id") == "account_service"))
            issues.Add(new Issue(caseId, "NO_ACCOUNT_ABSENT_CHECK", "no VERIFY_RECORD_ABSENT on account_service (all 32 samples have one)", "warning"));
        return issues;
    }

    /// <summary>INCOMPLETE rows: finding -> remediation -> verification must line up per resource.</summary>
    private static List<Issue> PairingIssues(string caseId, List<Dictionary<string, object?>> steps)
    {
        var issues = new List<Issue>();
        string Str(Dictionary<string, object?> s, string k) => (string)s[k]!;
        List<Dictionary<string, object?>> By(string ph) => steps.Where(s => Str(s, "phase") == ph).ToList();
        var remediations = new Dictionary<(string, string), string>();
        foreach (var s in By("REMEDIATION")) remediations[(Str(s, "system_id"), Str(s, "resource_id"))] = Str(s, "operation");
        var verifications = By("VERIFICATION").Select(s => (Str(s, "system_id"), Str(s, "resource_id"), Str(s, "operation"))).ToHashSet();
        var findingKeys = new HashSet<(string, string)>();
        foreach (var f in By("FINDING"))
        {
            var key = (Str(f, "system_id"), Str(f, "resource_id"));
            findingKeys.Add(key);
            var allowed = Schema.FindingToRemediations[Str(f, "operation")];
            if (!remediations.TryGetValue(key, out var op) || !allowed.Contains(op))
                issues.Add(new Issue(caseId, "FINDING_NO_REMEDIATION", $"{Str(f, "operation")} {Str(f, "resource_id")} needs one of {ListRepr(allowed.OrderBy(a => a, StringComparer.Ordinal))}"));
        }
        foreach (var (key, op) in remediations)
        {
            if (!findingKeys.Contains(key)) issues.Add(new Issue(caseId, "REMEDIATION_NO_FINDING", $"{op} {key.Item2} has no matching finding"));
            if (!verifications.Contains((key.Item1, key.Item2, Schema.RemediationToVerification[op])))
                issues.Add(new Issue(caseId, "REMEDIATION_NO_VERIFICATION", $"{op} {key.Item2} needs {Schema.RemediationToVerification[op]}"));
        }
        return issues;
    }

    /// <summary>Row-level checks across a whole prediction set (one row per request, request order, no dupes).</summary>
    public static List<Issue> ValidateRows(IReadOnlyList<(string CaseId, string Status, object? Steps)> rows, Dataset ds)
    {
        var issues = new List<Issue>();
        var expected = ds.CaseOrder.Count > 0 ? ds.CaseOrder.ToList() : ds.Requests.Select(r => r.CaseId).ToList();
        var got = rows.Select(r => r.CaseId).ToList();
        var dupes = got.GroupBy(c => c, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0) issues.Add(new Issue("-", "DUPLICATE_CASE", $"duplicate case_ids: {ListRepr(dupes.Take(5))}"));
        if (!got.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
        {
            var missing = expected.Except(got, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).Take(5);
            var extra = got.Except(expected, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).Take(5);
            issues.Add(new Issue("-", "CASE_SET", $"missing={ListRepr(missing)} extra={ListRepr(extra)}"));
        }
        else if (!got.SequenceEqual(expected, StringComparer.Ordinal)) issues.Add(new Issue("-", "CASE_ORDER", "rows are not in the order of the requests file"));
        var audit = ds.Requests.ToDictionary(r => r.CaseId, r => r.AuditAt, StringComparer.Ordinal);
        foreach (var (caseId, status, steps) in rows)
            issues.AddRange(ValidateCase(caseId, status, steps, audit.TryGetValue(caseId, out var a) ? a : null, ds));
        return issues;
    }

    /// <summary>Re-read the written CSV from disk, parse every steps_json, and re-validate everything.</summary>
    public static List<Issue> ValidateOutputFile(string path, Dataset ds)
    {
        var (header, raw) = OutputIo.ReadRaw(path);
        var issues = new List<Issue>();
        if (!header.SequenceEqual(Schema.OutputHeader, StringComparer.Ordinal))
            issues.Add(new Issue("-", "HEADER", $"header {ListRepr(header)} != {ListRepr(Schema.OutputHeader)}"));
        var rows = new List<(string, string, object?)>();
        int line = 1;
        foreach (var r in raw)
        {
            line++;
            if (r.Count != 3) { issues.Add(new Issue($"line{line}", "ROW_WIDTH", $"expected 3 columns, got {r.Count}")); continue; }
            object? steps;
            try { steps = Py.ParseJson(r[2]); }
            catch (DataException e) { issues.Add(new Issue(r[0], "BAD_JSON", e.Message)); continue; }
            rows.Add((r[0], r[1], steps));
        }
        issues.AddRange(ValidateRows(rows, ds));
        return issues;
    }

    public static void RaiseOnErrors(IReadOnlyList<Issue> issues)
    {
        var errors = issues.Where(i => i.Severity == "error").ToList();
        if (errors.Count == 0) return;
        var sb = new StringBuilder(string.Join("\n", errors.Take(20).Select(i => i.ToString())));
        if (errors.Count > 20) sb.Append($"\n... {errors.Count} errors total");
        throw new ValidationException(sb.ToString());
    }
}
