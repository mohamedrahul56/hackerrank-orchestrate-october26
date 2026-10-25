using System.Text.RegularExpressions;
using Auditor.Tools;

namespace Auditor.Tests;

/// <summary>ARCHITECTURE.md must stay true: its performance table is the generated report's, and the modules and tests it names exist.</summary>
public class ArchitectureDocTests
{
    private static string Doc => Repo.Read("ARCHITECTURE.md");
    private static string Report => Repo.Read("code/evaluation/performance_report.md");

    [Fact]
    public void TheDocumentHasEveryRequestedPart()
    {
        foreach (var needle in new[] { "```mermaid", "flowchart", "## 2. Modules", "## 3. Failure modes", "## 4. Measured performance", "### D1.", "### D2.", "### D3.", "### D4.", "### D5." })
            Assert.Contains(needle, Doc, StringComparison.Ordinal);
    }

    [Fact]
    public void PerformanceTableIsCopiedVerbatimFromTheGeneratedReport()
    {
        var doc = Doc;
        int a = doc.IndexOf(PerformanceReport.MarkStart, StringComparison.Ordinal) + PerformanceReport.MarkStart.Length;
        int b = doc.IndexOf(PerformanceReport.MarkEnd, StringComparison.Ordinal);
        Assert.Equal(PerformanceReport.WallTimeTable(Report), doc[a..b].Trim());
    }

    [Fact]
    public void TheNumbersQuotedInProseAreInTheGeneratedReport()
    {
        var report = Report;
        // Every "<n>x" growth factor and per-request figure in section 4 of the document comes from the report.
        foreach (var needle in new[] { "0.422 ms", "R^2 = 0.9785", "1.85 s", "| x40 | 20720 |" })
            Assert.Contains(needle, report, StringComparison.Ordinal);
        Assert.Contains("| x40 | 493 |", report, StringComparison.Ordinal);
    }

    [DatasetFact]
    public void TheExampleCaseIdInTheMakefileAndReadmeExists()
    {
        var ids = File.ReadAllLines(Path.Combine(Repo.Dataset, "deletion_requests.csv")).Select(l => l.Split(',')[0]).ToHashSet();
        foreach (var name in new[] { "Makefile", "README.md" })
            foreach (Match m in Regex.Matches(Repo.Read(name), @"(?:CASE=|--explain )(case_\d+)"))
                Assert.Contains(m.Groups[1].Value, ids);
    }

    [Fact]
    public void EveryLibraryFileIsInTheModuleTable()
    {
        var section = Doc.Split("## 2. Modules")[1].Split("## 3.")[0];
        var src = Path.Combine(Repo.Code, "src", "Auditor");
        foreach (var p in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var name = Path.GetFileName(p);
            Assert.True(section.Contains(name, StringComparison.Ordinal) || section.Contains(Path.GetFileNameWithoutExtension(p) + "`", StringComparison.Ordinal), $"{name} is not mentioned in the module table");
        }
    }

    [Fact]
    public void EveryTestClassFileNamedInTheFailureTableExists()
    {
        var section = Doc.Split("## 3. Failure modes")[1].Split("## 4.")[0];
        var named = Regex.Matches(section, @"`([A-Z][A-Za-z]+Tests)`").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.True(named.Count >= 6);
        foreach (var n in named)
            Assert.True(File.Exists(Path.Combine(Repo.Code, "tests", "Auditor.Tests", n + ".cs")), n);
    }

    [Fact]
    public void EveryDecisionRecordStatesItsRiskOrCost()
    {
        foreach (var d in new[] { "D2", "D3", "D4", "D5" })
        {
            var body = Doc.Split($"### {d}.")[1].Split("### D")[0];
            Assert.Matches(@"\*\*(Cost|Risk|Rejected)\.?\*\*", body);
        }
    }
}
