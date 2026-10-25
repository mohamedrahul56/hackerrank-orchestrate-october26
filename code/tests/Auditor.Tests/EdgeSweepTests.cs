using Auditor.Tools;

namespace Auditor.Tests;

/// <summary>The edge-case sweep (Auditor.Tools edge-sweep): every awkward-input scenario must meet the expectation written for it before the run.</summary>
public class EdgeSweepTests
{
    private static readonly Lazy<List<EdgeSweep.Row>> Rows = new(() => EdgeSweep.RunAll(null, Repo.HasDataset ? Repo.Root : null));

    [Fact]
    public void NoScenarioIsADefect()
    {
        var defects = Rows.Value.Where(r => r.Verdict == "DEFECT").Select(r => $"{r.Sid}: {r.Scenario} -> {r.Observed}").ToList();
        Assert.Empty(defects);
    }

    [Fact]
    public void ThereAreScenariosForEveryFamilyAndEachStatesItsExpectation()
    {
        var rows = Rows.Value;
        Assert.True(rows.Count >= 116, $"{rows.Count} scenarios");
        Assert.Equal(rows.Count, rows.Select(r => r.Sid).Distinct().Count());
        Assert.All(rows, r => Assert.True(r.Expected.Length > 0 && r.Scenario.Length > 0 && r.Observed.Length > 0, r.Sid));
        foreach (var area in new[] { "empty or missing fields", "bad timestamps", "old e-mails", "events after audit_at", "corrupt or missing evidence", "corrupt PNG", "prompt injection in request_text" })
            Assert.Contains(rows, r => r.Area == area);
    }

    [Fact]
    public void DecisionsAreRareAndAlwaysExplained()
    {
        var decisions = Rows.Value.Where(r => r.Verdict == "DECISION").ToList();
        Assert.InRange(decisions.Count, 0, 5);
        Assert.All(decisions, d => Assert.True(d.Note.Length > 20, d.Sid));
    }

    [Fact]
    public void TheRenderedReportIsWellFormedMarkdownWithATotalsLine()
    {
        var text = EdgeSweep.Render(Rows.Value);
        Assert.Empty(MarkdownCheck.ProblemsOfText(text));
        Assert.Matches(@"Totals: \d+ scenarios, OK \d+, DECISION \d+, DEFECT 0\.", text);
    }

    [DatasetFact]
    public void TheCommittedReportIsWhatTheSweepProduces() =>
        Assert.Equal(EdgeSweep.Render(Rows.Value), Repo.Read("code/evaluation/edge_case_sweep.md"));
}
