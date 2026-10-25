namespace Auditor.Tests;

/// <summary>The failure-table rows of ARCHITECTURE.md that no other test covers: an unknown email, and a written file that fails validation.</summary>
public class FailureModesTests
{
    [DatasetFact]
    public void AnEmailThatMatchesNobodyGetsAFallbackRow()
    {
        var a = new CaseAuditor(Repo.Dataset, new AuditConfig());
        var cid = a.Classification.First(kv => kv.Value.Label == "DELETION" && a.Users[kv.Key].Count > 0).Key;
        a.Users[cid] = new HashSet<string>();                        // what the resolver returns for an address no identity ever had
        var r = a.AuditCase(cid);
        Assert.Equal(("NOT_APPLICABLE", 0), (r.Status, r.Steps.Count));
        Assert.StartsWith("FALLBACK: request email matches no known identity", r.Notes[0], StringComparison.Ordinal);
    }

    [DatasetFact]
    public void ValidatorErrorsGiveExitCode1AndKeepTheCheckpoint()
    {
        using var d = new TempDir();
        var outPath = d.File("o.csv");
        var partial = Path.ChangeExtension(outPath, ".partial.jsonl");
        Auditor.Cli.Cli.ValidatorOverride.Value = (_, _) => new() { new Issue("case_1", "X", "forced for the test") };
        try
        {
            var (rc, _, err) = Run.Cli("--requests", "sample_deletion_requests.csv", "--out", outPath, "--images", "off");
            Assert.Equal(1, rc);
            Assert.Contains("forced for the test", err, StringComparison.Ordinal);
            Assert.True(File.Exists(partial), "kept for inspection when validation failed");
        }
        finally { Auditor.Cli.Cli.ValidatorOverride.Value = null; }
        Assert.Equal(0, Run.Cli("--requests", "sample_deletion_requests.csv", "--out", outPath, "--images", "off").Code);
        Assert.False(File.Exists(partial), "a clean run removes it");
    }
}
