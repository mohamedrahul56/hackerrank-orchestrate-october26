namespace Auditor;

/// <summary>What must happen to a record of one kind of data, as the operations of the answer.</summary>
public sealed record Treatment(string? Finding, string? Fix, string Verify, IReadOnlySet<string> Bad, IReadOnlySet<string> Ok, bool VerifyWhenOk = true);

public sealed record Verdict(string Category, string State, string Action, Treatment Treatment, bool Violation, bool EmitVerification);

/// <summary>
/// Policy engine, driven by dataset/deletion_rules.csv. The CSV says what must happen to each data category (DELETE / REDACT / ANONYMIZE / RETAIN). This class turns (category, observed
/// state) into a verdict. The mapping action -> operations is a small constant table (<see cref="Treatments"/>); nothing else in the code branches on a category name, so a new rule row
/// with a known action works without code.
/// Two rows need a note:
///   * BACKUP_ARCHIVE is RETAIN "when restore access can be blocked": the archive stays, but a restorable archive is a finding (REPORT_RESTORE_RISK -> BLOCK_RESTORE -> VERIFY_RESTORE_BLOCKED).
///   * DATA_FLOW is not a data category in the CSV; the problem statement (section 6.5) requires disabling active flows, so it carries a fixed treatment below.
/// </summary>
public sealed class PolicyEngine
{
    private static IReadOnlySet<string> Set(params string[] items) => new HashSet<string>(items, StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, Treatment> Treatments = new Dictionary<string, Treatment>(StringComparer.Ordinal)
    {
        ["DELETE"] = new("REPORT_REMAINING_RECORD", "DELETE_RECORD", "VERIFY_RECORD_ABSENT", Set("PRESENT"), Set("ABSENT"), VerifyWhenOk: false),
        ["REDACT"] = new("REPORT_EXPOSED_PII", "REDACT_RECORD", "VERIFY_RECORD_REDACTED", Set("EXPOSED"), Set("REDACTED")),
        ["ANONYMIZE"] = new("REPORT_EXPOSED_PII", "ANONYMIZE_RECORD", "VERIFY_RECORD_ANONYMIZED", Set("EXPOSED"), Set("ANONYMIZED")),
        ["RETAIN"] = new(null, null, "VERIFY_RECORD_PRESENT", Set(), Set("PRESENT")),
        ["RETAIN_BLOCK_RESTORE"] = new("REPORT_RESTORE_RISK", "BLOCK_RESTORE", "VERIFY_RESTORE_BLOCKED", Set("RESTORABLE"), Set("BLOCKED")),
        ["DISABLE_FLOW"] = new("REPORT_ACTIVE_DATA_FLOW", "DISABLE_DATA_FLOW", "VERIFY_DATA_FLOW_DISABLED", Set("ACTIVE"), Set("DISABLED")),
    };

    /// <summary>the actions deletion_rules.csv may name (AGENTS.md section 1)</summary>
    public static readonly IReadOnlySet<string> KnownActions = Set("DELETE", "REDACT", "ANONYMIZE", "RETAIN");

    // The account is the subject of the request: its absence is always verified even though other absent DELETE-type records (search entries, exports, ...) are only reported when they
    // still exist. (All 32 samples verify the account.)
    private static readonly IReadOnlySet<string> AlwaysVerifyAbsent = Set("USER_PROFILE");

    public Dictionary<string, (string Action, string Condition)> Rules { get; } = new(StringComparer.Ordinal);
    /// <summary>category -> rule_id (for explanations)</summary>
    public Dictionary<string, string> RuleIds { get; } = new(StringComparer.Ordinal);

    public PolicyEngine(string rulesCsv)
    {
        foreach (var r in Csv.ReadRows(rulesCsv, "data_category", "required_action"))                // a missing file or column is a DatasetException (exit 2), not a crash
        {
            var action = r["required_action"].Trim().ToUpperInvariant();
            if (!KnownActions.Contains(action))                                                       // an invented action must stop the run, not quietly turn cases into NOT_APPLICABLE
            {
                var id = r.G("rule_id").Length > 0 ? r["rule_id"] : r["data_category"];
                throw new DatasetException($"{Path.GetFileName(rulesCsv)}: {id}: unknown required_action {Py.StrRepr(r["required_action"])} for {r["data_category"]} (known: {string.Join(", ", KnownActions.OrderBy(a => a, StringComparer.Ordinal))})");
            }
            Rules[r["data_category"]] = (action, r.G("condition"));
            RuleIds[r["data_category"]] = r.G("rule_id");
        }
    }

    public string ActionFor(string category)
    {
        if (category == "DATA_FLOW") return "DISABLE_FLOW";
        if (!Rules.TryGetValue(category, out var rule)) throw new KeyNotFoundException($"no rule for data category {Py.StrRepr(category)} in deletion_rules.csv");
        return rule.Action == "RETAIN" && rule.Condition.ToLowerInvariant().Contains("restore", StringComparison.Ordinal) ? "RETAIN_BLOCK_RESTORE" : rule.Action;
    }

    public Treatment TreatmentFor(string category) => Treatments[ActionFor(category)];

    /// <summary>Null when the state says nothing about policy (unknown state).</summary>
    public Verdict? Judge(string category, string state)
    {
        var action = ActionFor(category);
        var t = Treatments[action];
        if (action == "RETAIN" && state == "EXPOSED")
        {
            // a retained record keeps its place but may only carry the stable reference (rule_15's principle): redact the rest
            action = "REDACT"; t = Treatments["REDACT"];
        }
        if (t.Bad.Contains(state)) return new Verdict(category, state, action, t, true, true);
        if (t.Ok.Contains(state)) return new Verdict(category, state, action, t, false, t.VerifyWhenOk || AlwaysVerifyAbsent.Contains(category));
        return null;
    }
}
