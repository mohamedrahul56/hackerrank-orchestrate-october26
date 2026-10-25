namespace Auditor.Tests;

public class ValidateTests
{
    private static HashSet<string> Codes(IEnumerable<Issue> issues, string severity = "error") => issues.Where(i => i.Severity == severity).Select(i => i.Code).ToHashSet();

    private static List<Step> IncompleteSteps() => Schema.Canonicalize(new[]
    {
        Schema.MakeStep("FINDING", "REPORT_REMAINING_RECORD", "search_service", "search_1", "evidence_01"),
        Schema.MakeStep("REMEDIATION", "DELETE_RECORD", "search_service", "search_1"),
        Schema.MakeStep("VERIFICATION", "VERIFY_RECORD_ABSENT", "account_service", "account_1"),
        Schema.MakeStep("VERIFICATION", "VERIFY_RECORD_ABSENT", "search_service", "search_1"),
    });

    /// <summary>The steps as the loose JSON the validator sees, so that a test can break a field.</summary>
    private static List<object?> Js(IEnumerable<Step> steps) => steps.Select(s => (object?)s.ToJson()).ToList();
    private static Dictionary<string, object?> At(List<object?> js, int i) => (Dictionary<string, object?>)js[i]!;

    private static List<Issue> V(string status, object? steps, DateTime? at = null, Dataset? ds = null) => Validator.ValidateCase("c", status, steps, at, ds);

    [Fact]
    public void GoodIncompleteRowIsClean() => Assert.Empty(Validator.ValidateCase("c", "INCOMPLETE", IncompleteSteps()));

    [Fact]
    public void NotApplicableMustBeEmpty()
    {
        Assert.Empty(Validator.ValidateCase("c", "NOT_APPLICABLE", new List<Step>()));
        Assert.Contains("NA_NOT_EMPTY", Codes(Validator.ValidateCase("c", "NOT_APPLICABLE", IncompleteSteps())));
    }

    [Fact]
    public void BadStatusAndEmptySteps()
    {
        Assert.Contains("BAD_STATUS", Codes(Validator.ValidateCase("c", "DONE", new List<Step>())));
        Assert.Contains("EMPTY_STEPS", Codes(Validator.ValidateCase("c", "COMPLETE", new List<Step>())));
    }

    [Fact]
    public void OrderAndStepNo()
    {
        var s = IncompleteSteps();
        Assert.Contains("ORDER", Codes(V("INCOMPLETE", Js(Enumerable.Reverse(s).Select((x, i) => x with { StepNo = i + 1 })))));
        var t = Js(s);
        At(t, 2)["step_no"] = 9L;
        Assert.Contains("STEP_NO", Codes(V("INCOMPLETE", t)));
    }

    [Fact]
    public void TripleTypesAndNulls()
    {
        var s = Js(IncompleteSteps()); At(s, 0)["state"] = "ABSENT";
        Assert.Contains("BAD_TRIPLE", Codes(V("INCOMPLETE", s)));
        s = Js(IncompleteSteps()); At(s, 1)["evidence_id"] = null;
        Assert.Contains("FIELD_TYPE", Codes(V("INCOMPLETE", s)));
        s = Js(IncompleteSteps()); At(s, 0).Remove("state");
        Assert.Contains("STEP_FIELDS", Codes(V("INCOMPLETE", s)));
    }

    [Fact]
    public void Duplicates()
    {
        var steps = IncompleteSteps();
        steps.Add(steps[^1] with { StepNo = 5 });
        Assert.Contains("DUPLICATE", Codes(Validator.ValidateCase("c", "INCOMPLETE", steps)));
    }

    [Fact]
    public void CompleteMayOnlyVerifyAndMustCite()
    {
        var steps = Schema.Canonicalize(new[] { Schema.MakeStep("VERIFICATION", "VERIFY_RECORD_ABSENT", "account_service", "account_1", "evidence_01") });
        Assert.Empty(Validator.ValidateCase("c", "COMPLETE", steps));
        var uncited = new List<Step> { steps[0] with { EvidenceId = "" } };
        Assert.Contains("EVIDENCE_MISSING", Codes(Validator.ValidateCase("c", "COMPLETE", uncited)));
        Assert.Contains("COMPLETE_PHASES", Codes(Validator.ValidateCase("c", "COMPLETE", IncompleteSteps())));
    }

    [Fact]
    public void FutureWorkMustNotCite()
    {
        var s = IncompleteSteps(); s[1] = s[1] with { EvidenceId = "evidence_01" };
        Assert.Contains("EVIDENCE_ON_PROPOSAL", Codes(Validator.ValidateCase("c", "INCOMPLETE", s)));
        s = IncompleteSteps(); s[0] = s[0] with { EvidenceId = "" };
        Assert.Contains("EVIDENCE_MISSING", Codes(Validator.ValidateCase("c", "INCOMPLETE", s)));
    }

    [Fact]
    public void Pairing()
    {
        var s = IncompleteSteps().Where(x => x.Phase != "REMEDIATION");
        Assert.Contains("FINDING_NO_REMEDIATION", Codes(Validator.ValidateCase("c", "INCOMPLETE", Schema.Canonicalize(s))));
        s = IncompleteSteps().Where(x => !(x.Phase == "VERIFICATION" && x.SystemId == "search_service"));
        Assert.Contains("REMEDIATION_NO_VERIFICATION", Codes(Validator.ValidateCase("c", "INCOMPLETE", Schema.Canonicalize(s))));
        s = IncompleteSteps().Where(x => x.Phase != "FINDING");
        var c = Codes(Validator.ValidateCase("c", "INCOMPLETE", Schema.Canonicalize(s)));
        Assert.Contains("REMEDIATION_NO_FINDING", c);
        Assert.Contains("INCOMPLETE_MISSING_PHASE", c);
    }

    [Fact]
    public void MissingAccountCheckIsOnlyAWarning()
    {
        var s = Schema.Canonicalize(IncompleteSteps().Where(x => x.SystemId != "account_service"));
        var issues = Validator.ValidateCase("c", "INCOMPLETE", s);
        Assert.Empty(Codes(issues));
        Assert.Equal(new[] { "NO_ACCOUNT_ABSENT_CHECK" }, Codes(issues, "warning"));
    }

    [Fact]
    public void ResourceIdMustHaveTheIdShape()
    {
        var s = Js(IncompleteSteps()); At(s, 0)["resource_id"] = "not an id";
        Assert.Contains("BAD_RESOURCE_ID", Codes(V("INCOMPLETE", s)));
    }

    [Fact]
    public void WrongTopLevelShapeIsReportedNotThrown()
    {
        Assert.NotEmpty(V("INCOMPLETE", "not a list"));
        Assert.NotEmpty(V("INCOMPLETE", new List<object?> { 5L }));
    }

    [DatasetFact]
    public void GoldPassesValidatorWithNoErrorsOrWarnings()
    {
        var ds = Dataset.Load(Repo.Dataset, "sample_deletion_requests.csv");
        Assert.Empty(Validator.ValidateOutputFile(Path.Combine(Repo.Dataset, "sample_output.csv"), ds).Select(i => i.ToString()));
    }

    private static Dictionary<string, Scorer.Row> Gold() => Scorer.ReadRows(Path.Combine(Repo.Dataset, "sample_output.csv"));

    [DatasetFact]
    public void EvidenceMustExistAndPrecedeAuditAt()
    {
        var ds = Dataset.Load(Repo.Dataset, "sample_deletion_requests.csv");
        var row = Gold()["case_21"];
        var steps = row.Steps.Select(s => (object?)new Dictionary<string, object?>(s.Fields)).ToList();
        var audit = ds.Requests.First(r => r.CaseId == "case_21").AuditAt;
        At(steps, 0)["evidence_id"] = "evidence_999";
        Assert.Contains("EVIDENCE_UNKNOWN", Codes(Validator.ValidateCase("case_21", "COMPLETE", steps, audit, ds)));
        At(steps, 0)["evidence_id"] = "evidence_50";             // security ledger captured 2026-10-01, after case_21's audit_at
        Assert.Contains("EVIDENCE_AFTER_AUDIT", Codes(Validator.ValidateCase("case_21", "COMPLETE", steps, audit, ds)));
        var early = ds.ActivityTime.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;      // earliest activity id (pre-dates audit_at)
        At(steps, 0)["evidence_id"] = early;                    // activity ids are legal citations when they pre-date audit_at
        Assert.Empty(Codes(Validator.ValidateCase("case_21", "COMPLETE", steps, audit, ds)));
    }

    [DatasetFact]
    public void RowLevelChecks()
    {
        var ds = Dataset.Load(Repo.Dataset, "sample_deletion_requests.csv");
        var rows = Gold().Select(kv => (kv.Key, kv.Value.Status, (object?)kv.Value.Steps.Select(s => (object?)s.Fields).ToList())).ToList();
        Assert.Empty(Validator.ValidateRows(rows, ds));
        Assert.Contains("CASE_SET", Codes(Validator.ValidateRows(rows.Take(rows.Count - 1).ToList(), ds)));
        Assert.Contains("DUPLICATE_CASE", Codes(Validator.ValidateRows(rows.Concat(rows.Take(1)).ToList(), ds)));
        var reversed = Enumerable.Reverse(rows).ToList();
        Assert.Contains("CASE_ORDER", Codes(Validator.ValidateRows(reversed, ds)));
    }

    [DatasetFact]
    public void SampleFactsAboutEvidenceIds()
    {
        // Facts confirmed from the 32 labeled samples: INCOMPLETE rows: FINDING always cites evidence; REMEDIATION and every VERIFICATION step has evidence_id "".
        // COMPLETE rows: every step cites evidence.
        var tally = new Dictionary<(string, string, bool), int>();
        foreach (var r in Gold().Values)
            foreach (var s in r.Steps)
            {
                var k = (r.Status, s.Text("phase"), s.Text("evidence_id").Length > 0);
                tally[k] = tally.GetValueOrDefault(k) + 1;
            }
        Assert.DoesNotContain(tally.Keys, k => k.Item1 == "INCOMPLETE" && k.Item2 == "FINDING" && !k.Item3);
        Assert.DoesNotContain(tally.Keys, k => k.Item1 == "INCOMPLETE" && k.Item2 != "FINDING" && k.Item3);
        Assert.DoesNotContain(tally.Keys, k => k.Item1 == "COMPLETE" && !k.Item3);
        Assert.True(tally.GetValueOrDefault(("INCOMPLETE", "VERIFICATION", false)) > 0);
    }

    [DatasetFact]
    public void EverySampleHasAccountAbsentCheck()
    {
        foreach (var (cid, r) in Gold())
            Assert.True(r.Steps.Any(s => s.Text("operation") == "VERIFY_RECORD_ABSENT" && s.Text("system_id") == "account_service"), cid);
    }
}
