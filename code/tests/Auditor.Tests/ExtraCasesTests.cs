
namespace Auditor.Tests;

/// <summary>The synthetic cases in evaluation/extra_cases.md: the pipeline, the independent oracle and the hand-written expectations must agree,
/// and the Markdown must be what the generator produces (so reviewers read exactly what is tested).</summary>
public class ExtraCasesTests
{
    private static readonly string MdPath = Path.Combine(Repo.Code, "evaluation", "extra_cases.md");
    private static readonly (List<ExtraCases.Case> Cases, string Rules) Parsed = ExtraCases.Parse(MdPath);

    [Fact]
    public void MarkdownIsWhatTheGeneratorWrites() =>
        Assert.Equal(File.ReadAllText(MdPath).ReplaceLineEndings("\n"), ExtraCases.Render(ExtraCases.Definitions()));

    [Fact]
    public void EveryCasePasses()
    {
        var results = ExtraCases.RunAll(Parsed.Cases, Parsed.Rules);
        Assert.True(results.Count >= 20);
        Assert.Empty(results.Where(r => !r.Ok).Select(r => r.Id));
    }

    [Fact]
    public void HandWrittenExpectationsMatchTheOracle() => Assert.Empty(Parsed.Cases.SelectMany(ExtraCases.CheckDefinition));

    [Fact]
    public void AllUncoveredCategoriesAreExercised()
    {
        var cats = Parsed.Cases.SelectMany(c => c.Resources.Select(r => r.Category)).ToHashSet();
        foreach (var need in new[] { "CACHE_ENTRY", "EMAIL_DELIVERY_LOG", "PRODUCT_TELEMETRY", "MARKETING_PROFILE", "LEGAL_HOLD_RECORD", "SECONDARY_IDENTIFIER", "SUPPORT_ATTACHMENT" })
            Assert.Contains(need, cats);
    }

    [Fact]
    public void AtLeastThreeNonDeletionOrEdgeCasesWithExpectedOutputs() => Assert.True(Parsed.Cases.Count(c => !c.DeletionRequest) >= 3);

    [DatasetFact]
    public void EmbeddedRulesAreTheDatasetRules()
    {
        var real = Csv.Parse(File.ReadAllText(Path.Combine(Repo.Dataset, "deletion_rules.csv")));
        var mine = Csv.Parse(Parsed.Rules);
        Assert.Equal(real.Where(r => r.Count > 0), mine.Where(r => r.Count > 0));
    }

    [Fact]
    public void EveryCaseIsFullyReadableAsMarkdown()
    {
        var text = File.ReadAllText(MdPath);
        foreach (var c in Parsed.Cases)
        {
            int start = text.IndexOf($"## {c.Id} - ", StringComparison.Ordinal);
            int next = text.IndexOf("\n## XC-", start + 1, StringComparison.Ordinal);
            var section = text[start..(next == -1 ? text.Length : next)];
            Assert.Contains(c.Request.Text, section, StringComparison.Ordinal);
            Assert.Contains("**rule basis**", section, StringComparison.Ordinal);
            Assert.Contains("**evidence files**", section, StringComparison.Ordinal);
            Assert.Contains($"**expected**: `{c.Expected.Status}`", section, StringComparison.Ordinal);
            var tableRows = section.Split('\n').Count(l => l.StartsWith("| ", StringComparison.Ordinal) && int.TryParse(l.Split('|')[1].Trim(), out _));
            Assert.Equal(c.Expected.Steps.Count, tableRows);
            if (c.Expected.Steps.Count == 0) Assert.Contains("`steps_json` = `[]`", section, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ABrokenExpectationIsDetected()
    {
        var c = Parsed.Cases.First(x => x.Id == "XC-01");
        c.Expected.Status = "COMPLETE";
        try { Assert.False(ExtraCases.RunAll(new() { c }, Parsed.Rules)[0].Ok); }
        finally { c.Expected.Status = "INCOMPLETE"; }
    }

    [Fact]
    public void PipelineIsDrivenByTheRuleTable()
    {
        // Change rule_04 from DELETE to RETAIN: the cache copy is now a required retained record, for oracle and pipeline alike.
        var c = Parsed.Cases.First(x => x.Id == "XC-01");
        var rules = Parsed.Rules.Replace("rule_04,CACHE_ENTRY,Always,DELETE", "rule_04,CACHE_ENTRY,Always,RETAIN", StringComparison.Ordinal);
        var (status, steps) = ExtraCases.RunPipeline(c, rules);
        var (oStatus, oSteps) = ExtraCases.Oracle(c, rules);
        Assert.Equal(oStatus, status);
        Assert.True(ExtraCases.SameSteps(ExtraCases.Compact(steps), oSteps));
        Assert.Equal("COMPLETE", status);
        Assert.Contains(oSteps, s => s.SequenceEqual(new[] { "VERIFICATION", "VERIFY_RECORD_PRESENT", "cache_service", "cache_101", "evidence_x1" }));
    }
}
