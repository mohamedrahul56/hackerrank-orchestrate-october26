using System.Security.Cryptography;

namespace Auditor.Tests;

/// <summary>`--explain CASE_ID`: read-only, faithful, and complete. Synthetic cases (always run) plus real cases of different kinds (need dataset/).</summary>
public abstract class ExplainBase : IDisposable
{
    protected readonly TempDir Tmp = new();
    protected static readonly Dictionary<string, ExtraCases.Case> Cases = ExtraCases.Definitions().ToDictionary(c => c.Id);
    public void Dispose() => Tmp.Dispose();

    protected CaseAuditor Synthetic(string id, string? sub = null)
    {
        var root = sub is null ? Tmp.Path : Path.Combine(Tmp.Path, sub);
        ExtraCases.BuildDataset(Cases[id], root);
        return new CaseAuditor(root);
    }

    protected static void Check(CaseAuditor a, string caseId, string text)
    {
        var res = a.AuditCase(caseId);
        Assert.Contains($"deletion_status = {res.Status}", text, StringComparison.Ordinal);
        foreach (var s in res.Steps) Assert.Contains($"{s.Operation,-26} {s.ResourceId}", text, StringComparison.Ordinal);        // every emitted step is listed with a reason
        Assert.DoesNotContain("(no reason recorded)", text, StringComparison.Ordinal);
        var again = a.AuditCase(caseId);
        Assert.Equal((res.Status, OutputIo.DumpsSteps(res.Steps)), (again.Status, OutputIo.DumpsSteps(again.Steps)));           // explaining changed nothing
    }

    protected static Dictionary<string, string> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
            .ToDictionary(p => Path.GetRelativePath(folder, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

    protected static string Between(string text, string from, string to) => text.Split(from)[1].Split(to)[0];
}

public class ExplainSyntheticKindsTests : ExplainBase
{
    [Fact]
    public void Kind1ViolationWithARuleAndACitation()
    {
        // XC-11: a retained legal-hold record that also carries the e-mail address.
        var a = Synthetic("XC-11");
        var text = Explainer.Explain(a, "XC-11");
        Check(a, "XC-11", text);
        Assert.Contains("katya.volkova@example.test -> user_9011", text, StringComparison.Ordinal);                       // identity resolution
        Assert.Contains("the CURRENT e-mail", text, StringComparison.Ordinal);
        Assert.Contains("legal_hold_1101", text, StringComparison.Ordinal);
        Assert.Contains("rule_13 LEGAL_HOLD_RECORD / A supplied legal hold is active / RETAIN", text, StringComparison.Ordinal);   // the rule applied
        Assert.Contains("state EXPOSED -> VIOLATION (treatment REDACT)", text, StringComparison.Ordinal);                // why it is a violation
        Assert.Contains("-> state used: EXPOSED (LEGAL_HOLD_RECORD), from evidence_x2", text, StringComparison.Ordinal);
        Assert.Contains("REPORT_EXPOSED_PII", text, StringComparison.Ordinal);
        Assert.Contains("cites evidence_x2", text, StringComparison.Ordinal);                                           // the finding cites the file that shows it
        Assert.Contains("proposed fix for the finding above", text, StringComparison.Ordinal);                          // remediation: future work, no evidence id
        Assert.Contains("deletion_status = INCOMPLETE", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Kind2ARecordThatNeedsNoStepAndAHoldThatExpired()
    {
        var a = Synthetic("XC-12");
        var text = Explainer.Explain(a, "XC-12");
        Check(a, "XC-12", text);
        Assert.Contains("no step: its retention / legal hold ended 2026-08-05T00:00:00Z", text, StringComparison.Ordinal);
        Assert.DoesNotContain("legal_hold_1201 ", Between(text, "5. THE STEPS", "6. WHAT"), StringComparison.Ordinal);       // not among the steps
        Assert.Contains("deletion_status = COMPLETE", text, StringComparison.Ordinal);
        var a2 = Synthetic("XC-02", "second");                                                                          // an already-compliant DELETE-type copy is omitted with its reason
        var t2 = Explainer.Explain(a2, "XC-02");
        Assert.Contains("a compliant CACHE_ENTRY copy adds no step", t2, StringComparison.Ordinal);
        Check(a2, "XC-02", t2);
    }

    [Fact]
    public void Kind3RequestThatDoesNotAskForDeletion()
    {
        var a = Synthetic("XC-15");
        var text = Explainer.Explain(a, "XC-15");
        Assert.Contains("decision : NOT_APPLICABLE", text, StringComparison.Ordinal);
        Assert.Contains("steps_json = []", text, StringComparison.Ordinal);
        Assert.Contains("does not authorize deletion", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2. WHO THE PERSON IS", text, StringComparison.Ordinal);                                  // nothing else is examined
        Assert.Contains("deletion_status = NOT_APPLICABLE", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LaterEvidenceIsShownAsIgnoredForState()
    {
        var a = Synthetic("XC-20");
        var text = Explainer.Explain(a, "XC-20");
        Check(a, "XC-20", text);
        Assert.Contains("captured AFTER audit_at: ignored for state", text, StringComparison.Ordinal);
        Assert.Contains("-> state used: PRESENT (CACHE_ENTRY), from evidence_x1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("evidence_x9", Between(text, "5. THE STEPS", "6. WHAT"), StringComparison.Ordinal);          // never cited
    }

    [Fact]
    public void HistoricalEmailIsReportedAsHistorical()
    {
        var a = Synthetic("XC-21");
        var text = Explainer.Explain(a, "XC-21");
        Assert.Contains("a HISTORICAL e-mail", text, StringComparison.Ordinal);
        Assert.Contains("vera.old@example.test -> user_9022", text, StringComparison.Ordinal);
    }
}

public class ExplainReadOnlyTests : ExplainBase
{
    [Fact]
    public void CliExplainWritesNothingAndKeepsAnExistingOutputFile()
    {
        var root = Path.Combine(Tmp.Path, "ds"); var work = Path.Combine(Tmp.Path, "work");
        Directory.CreateDirectory(work);
        ExtraCases.BuildDataset(Cases["XC-01"], root);
        var outPath = Path.Combine(work, "output.csv");
        File.WriteAllText(outPath, "case_id,deletion_status,steps_json\nsentinel,COMPLETE,[]\n");
        File.WriteAllText(Path.Combine(work, "output.usage.jsonl"), "sentinel\n");
        var beforeWork = Snapshot(work); var beforeDs = Snapshot(root);
        var (code, stdout, err) = Run.Cli("--dataset", root, "--out", outPath, "--explain", "XC-01");
        Assert.True(code == 0, err);
        Assert.Contains("EXPLAIN XC-01", stdout, StringComparison.Ordinal);
        Assert.Equal(beforeWork, Snapshot(work));                                      // no output, notes or usage file touched or created
        Assert.Equal(beforeDs, Snapshot(root));                                        // the dataset is read-only too
    }

    [Fact]
    public void UnknownCaseIsACleanError()
    {
        var root = Path.Combine(Tmp.Path, "ds");
        ExtraCases.BuildDataset(Cases["XC-01"], root);
        var outPath = Path.Combine(Tmp.Path, "o.csv");
        var (code, _, err) = Run.Cli("--dataset", root, "--out", outPath, "--explain", "nope");
        Assert.Equal(2, code);
        Assert.Contains("no case 'nope'", err, StringComparison.Ordinal);
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void InstrumentationDoesNotChangeTheNormalRun()
    {
        var a = Synthetic("XC-14");
        var res = a.AuditCase("XC-14");
        Assert.Equal("INCOMPLETE", res.Status);
        Assert.Equal(Enumerable.Range(1, res.Steps.Count), res.Steps.Select(s => s.StepNo));
    }
}

public class ExplainRealCasesTests
{
    private static readonly Lazy<CaseAuditor> Auditor = new(() => new CaseAuditor(Repo.Dataset));

    [DatasetFact]
    public void Kind1WorkflowSaysCompleteButEvidenceShowsRemainingCopies()
    {
        var text = Explainer.Explain(Auditor.Value, "case_36");
        Assert.Contains("reported_status     : COMPLETE", text, StringComparison.Ordinal);
        Assert.Contains("the workflow REPORTED COMPLETE; that claim was not used as proof. The evidence at audit_at says INCOMPLETE.", text, StringComparison.Ordinal);
        Assert.Contains("rule_09 USER_EXPORT / Always / DELETE; state PRESENT -> VIOLATION", text, StringComparison.Ordinal);
        Assert.Contains("rule_03 SEARCH_INDEX_ENTRY", text, StringComparison.Ordinal);
        Assert.Contains("rule_12 SECURITY_AUDIT_LOG", text, StringComparison.Ordinal);
        Assert.Contains("evidence_03", text, StringComparison.Ordinal);
    }

    [DatasetFact]
    public void Kind2AccountFoundThroughTheActivityTimelineWithAHistoricalEmail()
    {
        var text = Explainer.Explain(Auditor.Value, "case_48");
        Assert.Contains("a HISTORICAL e-mail", text, StringComparison.Ordinal);
        Assert.Contains("account discovery   : phase -> account_483", text, StringComparison.Ordinal);
        Assert.Contains("worker delete event", text, StringComparison.Ordinal);
        Assert.Contains("no evidence file observes this resource", text, StringComparison.Ordinal);
        Assert.Contains("treated as deleted (claim from the worker's delete event)", text, StringComparison.Ordinal);
        Assert.Contains("`--no-timing-heuristics` switches it off", text, StringComparison.Ordinal);
        Assert.Contains("REPORT_ACTIVE_DATA_FLOW", text, StringComparison.Ordinal);
        Assert.Contains("DISABLE_FLOW", text, StringComparison.Ordinal);
    }

    [DatasetFact]
    public void Kind3AccountLeftOutBecauseCandidatesCannotBeSeparated()
    {
        var text = Explainer.Explain(Auditor.Value, "case_58");
        Assert.Contains("account discovery   : ambiguous; candidates ['account_5715', 'account_9862']", text, StringComparison.Ordinal);
        Assert.Contains("NO account step is emitted", text, StringComparison.Ordinal);
        Assert.Contains("account: no account step (ambiguous), see section 3", text, StringComparison.Ordinal);
        Assert.Contains("anonymized analytics: several candidates fit", text, StringComparison.Ordinal);
        Assert.Contains("the workflow REPORTED NOT_STARTED", text, StringComparison.Ordinal);
    }

    [DatasetFact]
    public void ProvisionalRowIsLabelledAsSuch()
    {
        var text = Explainer.Explain(Auditor.Value, "case_34");
        Assert.Contains("PROVISIONAL", text, StringComparison.Ordinal);
        Assert.Contains("evidence_04", text, StringComparison.Ordinal);
    }

    [DatasetFact]
    public void EveryCaseOfTheMainSetCanBeExplainedAndAgreesWithItsRow()
    {
        var ds = Dataset.Load(Repo.Dataset);
        foreach (var cid in ds.CaseOrder)
        {
            var text = Explainer.Explain(Auditor.Value, cid);
            var res = Auditor.Value.AuditCase(cid);
            Assert.True(text.Contains($"deletion_status = {res.Status}", StringComparison.Ordinal), cid);
            Assert.False(text.Contains("(no reason recorded)", StringComparison.Ordinal), cid);
            Assert.True(text.Split('\n').Count(l => l.StartsWith("  #", StringComparison.Ordinal)) == res.Steps.Count, cid);
        }
    }

    [DatasetFact]
    public void TheRepositoryRunFilesAreUntouchedByExplain()
    {
        var names = new[] { "output.csv", "output.notes.md", "output.usage.jsonl" };
        var present = names.Select(n => Path.Combine(Repo.Root, n)).Where(File.Exists).ToList();
        string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
        var before = present.ToDictionary(p => Path.GetFileName(p)!, Hash);
        Assert.Equal(0, Run.Cli("--explain", "case_48").Code);
        Assert.Equal(before, present.ToDictionary(p => Path.GetFileName(p)!, Hash));
        Assert.Equal(present.Select(p => Path.GetFileName(p)!).OrderBy(x => x, StringComparer.Ordinal),
                     Directory.GetFiles(Repo.Root, "output*").Select(p => Path.GetFileName(p)!).OrderBy(x => x, StringComparer.Ordinal));
    }
}
