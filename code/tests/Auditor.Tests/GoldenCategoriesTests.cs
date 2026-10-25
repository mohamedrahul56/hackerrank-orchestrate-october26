namespace Auditor.Tests;

/// <summary>
/// One golden test per policy category of deletion_rules.csv (plus the active data flow of spec 6.5, which is not a row of that file).
/// Each golden answer is typed out by hand below (status + every step: phase, operation, system, resource, evidence_id) from the rule text and problem statement,
/// NOT computed by any code of this repository. The pipeline runs on a minimal dataset built for the case and must reproduce it exactly.
/// Case n has the account `account_n01` (already deleted, so it is only verified), the retained audit record `audit_record_n02`, and the resource under test `&lt;prefix&gt;_n03`.
/// </summary>
public class GoldenCategoriesTests
{
    private const string E = "evidence_x1", F = "FINDING", V = "VERIFICATION", R = "REMEDIATION";

    private static ExtraCases.Case Case(int n, string category, string rid, string state, string accountState = "ABSENT", string file = "evidence_x1")
    {
        var u = ExtraCases.P(n, $"Golden Person {n}", $"golden{n}@example.test");
        var resources = new List<ExtraCases.Res> { ExtraCases.R($"account_{n}01", "USER_PROFILE", accountState), ExtraCases.R($"audit_record_{n}02", "SECURITY_AUDIT_LOG", "PRESENT") };
        if (category is not ("USER_PROFILE" or "SECURITY_AUDIT_LOG")) resources.Add(ExtraCases.R(rid, category, state, file));
        return ExtraCases.Mk($"G-{n:D2}", $"golden {category}", "", u, resources, new());
    }

    private static List<string> Audit(int n, string ev = "") => new() { V, "VERIFY_RECORD_PRESENT", "security_service", $"audit_record_{n}02", ev };
    private static List<string> Acct(int n, string ev = "") => new() { V, "VERIFY_RECORD_ABSENT", "account_service", $"account_{n}01", ev };
    private static List<string> S(string phase, string op, string sys, string rid, string ev) => new() { phase, op, sys, rid, ev };

    private static ExtraCases.Case LegalHoldCase()
    {
        const int n = 13;
        var u = ExtraCases.P(n, "Golden Person 13", "golden13@example.test");
        var resources = new List<ExtraCases.Res>
        {
            ExtraCases.R("account_1301", "USER_PROFILE", "ABSENT"), ExtraCases.R("audit_record_1302", "SECURITY_AUDIT_LOG", "PRESENT"),
            ExtraCases.R("legal_hold_1303", "LEGAL_HOLD_RECORD", "PRESENT", "evidence_x2", retainedUntil: "2033-12-31T23:59:59Z"),
        };
        var c = ExtraCases.Mk("G-13", "golden LEGAL_HOLD_RECORD", "", u, resources, new());
        c.Evidence.Add(ExtraCases.LegalHoldFile("legal_hold_1303", u.UserId, "2033-12-31T23:59:59Z"));
        return c;
    }

    /// <summary>category → (rule id in deletion_rules.csv or "spec 6.5", case, golden status, golden steps)</summary>
    private static readonly Dictionary<string, (string Rule, ExtraCases.Case Case, string Status, List<List<string>> Steps)> Golden = new()
    {
        ["USER_PROFILE"] = ("rule_01", Case(1, "USER_PROFILE", "account_103", "PRESENT", accountState: "PRESENT"), "INCOMPLETE", new()
        {
            S(F, "REPORT_REMAINING_RECORD", "account_service", "account_101", E), S(R, "DELETE_RECORD", "account_service", "account_101", ""),
            S(V, "VERIFY_RECORD_ABSENT", "account_service", "account_101", ""), Audit(1),
        }),
        ["SECONDARY_IDENTIFIER"] = ("rule_02", Case(2, "SECONDARY_IDENTIFIER", "contact_203", "PRESENT"), "INCOMPLETE", new()
        {
            S(F, "REPORT_REMAINING_RECORD", "account_service", "contact_203", E), S(R, "DELETE_RECORD", "account_service", "contact_203", ""),
            Acct(2), S(V, "VERIFY_RECORD_ABSENT", "account_service", "contact_203", ""), Audit(2),
        }),
        ["SEARCH_INDEX_ENTRY"] = ("rule_03", Case(3, "SEARCH_INDEX_ENTRY", "search_303", "PRESENT"), "INCOMPLETE", new()
        {
            S(F, "REPORT_REMAINING_RECORD", "search_service", "search_303", E), S(R, "DELETE_RECORD", "search_service", "search_303", ""),
            Acct(3), S(V, "VERIFY_RECORD_ABSENT", "search_service", "search_303", ""), Audit(3),
        }),
        ["CACHE_ENTRY"] = ("rule_04", Case(4, "CACHE_ENTRY", "cache_403", "PRESENT"), "INCOMPLETE", new()
        {
            S(F, "REPORT_REMAINING_RECORD", "cache_service", "cache_403", E), S(R, "DELETE_RECORD", "cache_service", "cache_403", ""),
            Acct(4), S(V, "VERIFY_RECORD_ABSENT", "cache_service", "cache_403", ""), Audit(4),
        }),
        ["SUPPORT_MESSAGE"] = ("rule_05", Case(5, "SUPPORT_MESSAGE", "support_503", "EXPOSED"), "INCOMPLETE", new()
        {
            S(F, "REPORT_EXPOSED_PII", "support_platform", "support_503", E), S(R, "REDACT_RECORD", "support_platform", "support_503", ""),
            Acct(5), Audit(5), S(V, "VERIFY_RECORD_REDACTED", "support_platform", "support_503", ""),
        }),
        ["SUPPORT_ATTACHMENT"] = ("rule_06", Case(6, "SUPPORT_ATTACHMENT", "object_603", "PRESENT"), "INCOMPLETE", new()
        {
            S(F, "REPORT_REMAINING_RECORD", "object_storage", "object_603", E), S(R, "DELETE_RECORD", "object_storage", "object_603", ""),
            Acct(6), S(V, "VERIFY_RECORD_ABSENT", "object_storage", "object_603", ""), Audit(6),
        }),
        ["ANALYTICS_PROFILE"] = ("rule_07", Case(7, "ANALYTICS_PROFILE", "analytics_703", "EXPOSED"), "INCOMPLETE", new()
        {
            S(F, "REPORT_EXPOSED_PII", "analytics_platform", "analytics_703", E), S(R, "ANONYMIZE_RECORD", "analytics_platform", "analytics_703", ""),
            Acct(7), Audit(7), S(V, "VERIFY_RECORD_ANONYMIZED", "analytics_platform", "analytics_703", ""),
        }),
        ["PRODUCT_TELEMETRY"] = ("rule_08", Case(8, "PRODUCT_TELEMETRY", "telemetry_803", "EXPOSED"), "INCOMPLETE", new()
        {
            S(F, "REPORT_EXPOSED_PII", "analytics_platform", "telemetry_803", E), S(R, "ANONYMIZE_RECORD", "analytics_platform", "telemetry_803", ""),
            Acct(8), Audit(8), S(V, "VERIFY_RECORD_ANONYMIZED", "analytics_platform", "telemetry_803", ""),
        }),
        ["USER_EXPORT"] = ("rule_09", Case(9, "USER_EXPORT", "export_903", "PRESENT"), "INCOMPLETE", new()
        {
            S(F, "REPORT_REMAINING_RECORD", "export_service", "export_903", E), S(R, "DELETE_RECORD", "export_service", "export_903", ""),
            Acct(9), S(V, "VERIFY_RECORD_ABSENT", "export_service", "export_903", ""), Audit(9),
        }),
        ["MARKETING_PROFILE"] = ("rule_10", Case(10, "MARKETING_PROFILE", "marketing_1003", "PRESENT"), "INCOMPLETE", new()
        {
            S(F, "REPORT_REMAINING_RECORD", "marketing_platform", "marketing_1003", E), S(R, "DELETE_RECORD", "marketing_platform", "marketing_1003", ""),
            Acct(10), S(V, "VERIFY_RECORD_ABSENT", "marketing_platform", "marketing_1003", ""), Audit(10),
        }),
        ["EMAIL_DELIVERY_LOG"] = ("rule_11", Case(11, "EMAIL_DELIVERY_LOG", "email_delivery_1103", "EXPOSED"), "INCOMPLETE", new()
        {
            S(F, "REPORT_EXPOSED_PII", "email_delivery_service", "email_delivery_1103", E), S(R, "REDACT_RECORD", "email_delivery_service", "email_delivery_1103", ""),
            Acct(11), Audit(11), S(V, "VERIFY_RECORD_REDACTED", "email_delivery_service", "email_delivery_1103", ""),
        }),
        ["SECURITY_AUDIT_LOG"] = ("rule_12", Case(12, "SECURITY_AUDIT_LOG", "", ""), "COMPLETE", new() { Acct(12, E), Audit(12, E) }),
        ["LEGAL_HOLD_RECORD"] = ("rule_13", LegalHoldCase(), "COMPLETE", new() { Acct(13, E), S(V, "VERIFY_RECORD_PRESENT", "legal_service", "legal_hold_1303", "evidence_x2"), Audit(13, E) }),
        ["BACKUP_ARCHIVE"] = ("rule_14", Case(14, "BACKUP_ARCHIVE", "backup_1403", "RESTORABLE"), "INCOMPLETE", new()
        {
            S(F, "REPORT_RESTORE_RISK", "backup_service", "backup_1403", E), S(R, "BLOCK_RESTORE", "backup_service", "backup_1403", ""),
            Acct(14), Audit(14), S(V, "VERIFY_RESTORE_BLOCKED", "backup_service", "backup_1403", ""),
        }),
        ["ACCOUNT_TOMBSTONE"] = ("rule_15", Case(15, "ACCOUNT_TOMBSTONE", "tombstone_1503", "EXPOSED"), "INCOMPLETE", new()
        {
            S(F, "REPORT_EXPOSED_PII", "identity_service", "tombstone_1503", E), S(R, "REDACT_RECORD", "identity_service", "tombstone_1503", ""),
            Acct(15), Audit(15), S(V, "VERIFY_RECORD_REDACTED", "identity_service", "tombstone_1503", ""),
        }),
        ["DATA_FLOW"] = ("spec 6.5", Case(16, "DATA_FLOW", "data_flow_1603", "ACTIVE"), "INCOMPLETE", new()
        {
            S(F, "REPORT_ACTIVE_DATA_FLOW", "integration_service", "data_flow_1603", E), S(R, "DISABLE_DATA_FLOW", "integration_service", "data_flow_1603", ""),
            Acct(16), Audit(16), S(V, "VERIFY_DATA_FLOW_DISABLED", "integration_service", "data_flow_1603", ""),
        }),
    };

    public static IEnumerable<object[]> Categories() => Golden.Keys.Select(k => new object[] { k });

    private static List<List<string>> Compact(IEnumerable<Step> steps) => ExtraCases.Compact(steps);

    [Theory]
    [MemberData(nameof(Categories))]
    public void GoldenPerCategory(string category)
    {
        var (rule, c, status, expected) = Golden[category];
        var (gotStatus, steps) = ExtraCases.RunPipeline(c);
        Assert.True(gotStatus == status, $"{category} ({rule}): status {gotStatus}");
        Assert.True(ExtraCases.SameSteps(Compact(steps), expected), $"{category} ({rule}): {System.Text.Json.JsonSerializer.Serialize(Compact(steps))}");
        Assert.Equal(Enumerable.Range(1, steps.Count), steps.Select(s => s.StepNo));
        Assert.Equal(expected.Select(s => Schema.Operations.First(o => o.Operation == s[1]).State), steps.Select(s => s.State));         // the state implied by each operation
    }

    [Fact]
    public void EveryCategoryOfTheRulesFileHasAGoldenTest()
    {
        var embedded = Csv.ReadRows(WriteRules()).ToDictionary(r => r["data_category"], r => r["rule_id"]);
        if (Repo.HasDataset)
        {
            var real = Csv.ReadRows(Path.Combine(Repo.Dataset, "deletion_rules.csv")).ToDictionary(r => r["data_category"], r => r["rule_id"]);
            Assert.Equal(real.OrderBy(kv => kv.Key, StringComparer.Ordinal), embedded.OrderBy(kv => kv.Key, StringComparer.Ordinal));
        }
        Assert.Equal(Golden.Keys.Where(k => k != "DATA_FLOW").OrderBy(k => k, StringComparer.Ordinal), embedded.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (cat, rid) in embedded) Assert.Equal(rid, Golden[cat].Rule);
    }

    private static string WriteRules()
    {
        var p = Path.Combine(Path.GetTempPath(), "auditor-rules-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(p, ExtraCases.RulesCsv);
        return p;
    }

    /// <summary>problem statement: disable data flows and block unsafe restores BEFORE changing dependent records. One case with all three kinds of remediation.</summary>
    [Fact]
    public void FlowThenRestoreThenDelete()
    {
        var u = ExtraCases.P(17, "Golden Person 17", "golden17@example.test");
        var c = ExtraCases.Mk("G-17", "golden order", "", u, new()
        {
            ExtraCases.R("account_1701", "USER_PROFILE", "ABSENT"), ExtraCases.R("audit_record_1702", "SECURITY_AUDIT_LOG", "PRESENT"), ExtraCases.R("data_flow_1703", "DATA_FLOW", "ACTIVE"),
            ExtraCases.R("backup_1704", "BACKUP_ARCHIVE", "RESTORABLE"), ExtraCases.R("search_1705", "SEARCH_INDEX_ENTRY", "PRESENT"),
        }, new());
        var (status, steps) = ExtraCases.RunPipeline(c);
        Assert.Equal("INCOMPLETE", status);
        Assert.Equal(new[]
        {
            (F, "REPORT_REMAINING_RECORD", "search_1705"), (F, "REPORT_ACTIVE_DATA_FLOW", "data_flow_1703"), (F, "REPORT_RESTORE_RISK", "backup_1704"),
            (R, "DISABLE_DATA_FLOW", "data_flow_1703"), (R, "BLOCK_RESTORE", "backup_1704"), (R, "DELETE_RECORD", "search_1705"),
            (V, "VERIFY_RECORD_ABSENT", "account_1701"), (V, "VERIFY_RECORD_ABSENT", "search_1705"), (V, "VERIFY_RECORD_PRESENT", "audit_record_1702"),
            (V, "VERIFY_DATA_FLOW_DISABLED", "data_flow_1703"), (V, "VERIFY_RESTORE_BLOCKED", "backup_1704"),
        }, steps.Select(s => (s.Phase, s.Operation, s.ResourceId)));
    }

    /// <summary>The same categories when the copy is already compliant: nothing to fix (DELETE) or a verification of the required end state.</summary>
    [Fact]
    public void DeletedCopyAddsNoStepButARedactedCopyIsVerified()
    {
        var (status, steps) = ExtraCases.RunPipeline(Case(21, "CACHE_ENTRY", "cache_2103", "ABSENT"));
        Assert.Equal("COMPLETE", status);
        Assert.True(ExtraCases.SameSteps(Compact(steps), new() { Acct(21, E), Audit(21, E) }));
        (status, steps) = ExtraCases.RunPipeline(Case(22, "EMAIL_DELIVERY_LOG", "email_delivery_2203", "REDACTED"));
        Assert.Equal("COMPLETE", status);
        Assert.True(ExtraCases.SameSteps(Compact(steps), new() { Acct(22, E), Audit(22, E), S(V, "VERIFY_RECORD_REDACTED", "email_delivery_service", "email_delivery_2203", E) }));
        (status, steps) = ExtraCases.RunPipeline(Case(23, "BACKUP_ARCHIVE", "backup_2303", "BLOCKED"));
        Assert.Equal("COMPLETE", status);
        Assert.True(ExtraCases.SameSteps(Compact(steps), new() { Acct(23, E), Audit(23, E), S(V, "VERIFY_RESTORE_BLOCKED", "backup_service", "backup_2303", E) }));
    }
}
