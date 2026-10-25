using System.Text.RegularExpressions;

namespace Auditor;

/// <summary>One answer step (the seven fields of the output contract); <see cref="StepNo"/> is 0 until the steps are canonicalised.</summary>
public sealed record Step(int StepNo, string Phase, string SystemId, string ResourceId, string Operation, string State, string EvidenceId)
{
    /// <summary>The step as an ordered JSON object, in the order the contract lists the fields.</summary>
    public Dictionary<string, object?> ToJson() => new(StringComparer.Ordinal)
    {
        ["step_no"] = (long)StepNo, ["phase"] = Phase, ["system_id"] = SystemId, ["resource_id"] = ResourceId,
        ["operation"] = Operation, ["state"] = State, ["evidence_id"] = EvidenceId,
    };
}

/// <summary>
/// The output contract (AGENTS.md section 6.3/6.4) as data, plus canonical ordering.
/// Everything that needs to know "which operations exist, in which order, with which state" reads it from here, so the validator, the step generator and the scorer cannot disagree.
/// </summary>
public static class Schema
{
    // A resource id is "<words>_<digits>" (for example "account_7" or "data_flow_12"). Anything else read from evidence (a path, a sentence, SQL) is not a resource and must never reach an output step.
    private static readonly Regex ResourceIdShape = new(@"^[a-z][a-z0-9]*(?:_[a-z0-9]+)*_\d+$", RegexOptions.CultureInvariant);

    public static bool ValidResourceId(object? value) => value is string s && ResourceIdShape.IsMatch(s) && !s.Contains('\n', StringComparison.Ordinal);

    public static readonly string[] Statuses = { "COMPLETE", "INCOMPLETE", "NOT_APPLICABLE" };
    public static readonly string[] Phases = { "FINDING", "REMEDIATION", "VERIFICATION" };
    public static readonly string[] StepFields = { "step_no", "phase", "system_id", "resource_id", "operation", "state", "evidence_id" };
    public static readonly string[] OutputHeader = { "case_id", "deletion_status", "steps_json" };

    /// <summary>(operation, phase, state) in the canonical operation order of the problem statement; the position is the sort rank inside a phase.</summary>
    public static readonly (string Operation, string Phase, string State)[] Operations =
    {
        ("REPORT_REMAINING_RECORD", "FINDING", "PRESENT"),
        ("REPORT_EXPOSED_PII", "FINDING", "PRESENT"),
        ("REPORT_ACTIVE_DATA_FLOW", "FINDING", "ACTIVE"),
        ("REPORT_RESTORE_RISK", "FINDING", "ACTIVE"),
        ("DISABLE_DATA_FLOW", "REMEDIATION", "DISABLED"),
        ("BLOCK_RESTORE", "REMEDIATION", "BLOCKED"),
        ("DELETE_RECORD", "REMEDIATION", "ABSENT"),
        ("REDACT_RECORD", "REMEDIATION", "REDACTED"),
        ("ANONYMIZE_RECORD", "REMEDIATION", "ANONYMIZED"),
        ("VERIFY_RECORD_ABSENT", "VERIFICATION", "ABSENT"),
        ("VERIFY_RECORD_PRESENT", "VERIFICATION", "PRESENT"),
        ("VERIFY_RECORD_REDACTED", "VERIFICATION", "REDACTED"),
        ("VERIFY_RECORD_ANONYMIZED", "VERIFICATION", "ANONYMIZED"),
        ("VERIFY_DATA_FLOW_DISABLED", "VERIFICATION", "DISABLED"),
        ("VERIFY_RESTORE_BLOCKED", "VERIFICATION", "BLOCKED"),
    };

    public static readonly IReadOnlyDictionary<string, int> OpRank = Operations.Select((o, i) => (o.Operation, i)).ToDictionary(x => x.Operation, x => x.i, StringComparer.Ordinal);
    public static readonly IReadOnlyDictionary<string, int> PhaseRank = Phases.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i, StringComparer.Ordinal);
    public static readonly HashSet<(string Phase, string Operation, string State)> AllowedTriples = Operations.Select(o => (o.Phase, o.Operation, o.State)).ToHashSet();

    /// <summary>What each finding must be followed by, and what each remediation must be proven by.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> FindingToRemediations = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["REPORT_REMAINING_RECORD"] = new[] { "DELETE_RECORD" },
        ["REPORT_EXPOSED_PII"] = new[] { "REDACT_RECORD", "ANONYMIZE_RECORD" },
        ["REPORT_ACTIVE_DATA_FLOW"] = new[] { "DISABLE_DATA_FLOW" },
        ["REPORT_RESTORE_RISK"] = new[] { "BLOCK_RESTORE" },
    };

    public static readonly IReadOnlyDictionary<string, string> RemediationToVerification = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DISABLE_DATA_FLOW"] = "VERIFY_DATA_FLOW_DISABLED",
        ["BLOCK_RESTORE"] = "VERIFY_RESTORE_BLOCKED",
        ["DELETE_RECORD"] = "VERIFY_RECORD_ABSENT",
        ["REDACT_RECORD"] = "VERIFY_RECORD_REDACTED",
        ["ANONYMIZE_RECORD"] = "VERIFY_RECORD_ANONYMIZED",
    };

    /// <summary>Canonical order: phase, operation (table order), then plain-string system_id, resource_id (ids are not zero-padded, so 'audit_record_1000' sorts before 'audit_record_999').</summary>
    public static (int Phase, int Op, string System, string Resource) SortKey(string phase, string operation, string systemId, string resourceId) =>
        (PhaseRank[phase], OpRank[operation], systemId, resourceId);

    public static int CompareSortKeys((int Phase, int Op, string System, string Resource) a, (int Phase, int Op, string System, string Resource) b)
    {
        int c = a.Phase.CompareTo(b.Phase);
        if (c != 0) return c;
        c = a.Op.CompareTo(b.Op);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.System, b.System);
        return c != 0 ? c : string.CompareOrdinal(a.Resource, b.Resource);
    }

    /// <summary>Build a step; the state is implied by the operation so that callers cannot get it wrong.</summary>
    public static Step MakeStep(string phase, string operation, string systemId, string resourceId, string evidenceId = "")
    {
        var state = Operations.First(o => o.Operation == operation).State;
        return new Step(0, phase, systemId, resourceId, operation, state, evidenceId);
    }

    /// <summary>Sort into canonical order (stable) and renumber step_no from 1.</summary>
    public static List<Step> Canonicalize(IEnumerable<Step> steps)
    {
        var ordered = steps.Select(s => (Step: s, Key: SortKey(s.Phase, s.Operation, s.SystemId, s.ResourceId)))
                           .OrderBy(x => x.Key, Comparer<(int Phase, int Op, string System, string Resource)>.Create(CompareSortKeys))
                           .Select(x => x.Step);
        return ordered.Select((s, i) => s with { StepNo = i + 1 }).ToList();
    }
}
