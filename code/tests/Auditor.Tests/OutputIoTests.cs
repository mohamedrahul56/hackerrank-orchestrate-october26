using System.Text;

namespace Auditor.Tests;

public class OutputIoTests
{
    [Fact]
    public void CompactJsonAndQuotingRoundtrip()
    {
        var steps = new List<Step> { new(1, "FINDING", "s", "r", "REPORT_REMAINING_RECORD", "PRESENT", "e") };
        Assert.DoesNotContain(" ", OutputIo.DumpsSteps(steps), StringComparison.Ordinal);
        Assert.Equal("[]", OutputIo.DumpsSteps(Array.Empty<Step>()));
        using var d = new TempDir();
        var p = d.File("o.csv");
        OutputIo.Write(p, new[] { new OutputIo.Row("c1", "INCOMPLETE", steps), new OutputIo.Row("c2", "NOT_APPLICABLE", Array.Empty<Step>()) });
        var raw = File.ReadAllBytes(p);
        Assert.DoesNotContain((byte)'\r', raw);                                  // LF only, like the samples
        var rows = Csv.Parse(Encoding.UTF8.GetString(raw));
        Assert.Equal(new[] { "case_id", "deletion_status", "steps_json" }, rows[0]);
        Assert.Equal(OutputIo.DumpsSteps(steps), rows[1][2]);
        Assert.Equal(new[] { "c2", "NOT_APPLICABLE", "[]" }, rows[2]);
    }

    [Fact]
    public void FieldWithCommaQuoteAndNewlineSurvives()
    {
        using var d = new TempDir();
        var p = d.Write("q.csv", Csv.FormatRow(new[] { "a,b", "he said \"x\"", "line1\nline2" }));
        var rows = Csv.Parse(File.ReadAllText(p));
        Assert.Equal(new[] { "a,b", "he said \"x\"", "line1\nline2" }, rows[0]);
    }

    [DatasetFact]
    public void WrittenGoldRevalidatesAndMatchesSampleFile()
    {
        var ds = Dataset.Load(Repo.Dataset, "sample_deletion_requests.csv");
        var gold = Scorer.ReadRows(Path.Combine(Repo.Dataset, "sample_output.csv"));
        using var d = new TempDir();
        var p = d.File("o.csv");
        OutputIo.Write(p, ds.Requests.Select(r => new OutputIo.Row(r.CaseId, gold[r.CaseId].Status, gold[r.CaseId].Steps.Select(ToStep).ToList())));
        Assert.Empty(Validator.ValidateOutputFile(p, ds));
        var again = Scorer.ReadRows(p);
        Assert.Equal(gold.Keys.OrderBy(k => k, StringComparer.Ordinal), again.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (id, row) in gold)
        {
            Assert.Equal(row.Status, again[id].Status);
            Assert.Equal(row.Steps.Select(s => Scorer.Full(s)), again[id].Steps.Select(s => Scorer.Full(s)));
        }
    }

    internal static Step ToStep(Scorer.RawStep s) =>
        new((int)Convert.ToInt64(s.Get("step_no")), s.Text("phase"), s.Text("system_id"), s.Text("resource_id"), s.Text("operation"), s.Text("state"), s.Text("evidence_id"));

    [DatasetFact]
    public void TemplateHeaderMatches() => Assert.Equal("case_id,deletion_status,steps_json", File.ReadAllLines(Path.Combine(Repo.Dataset, "output.csv"))[0]);
}
