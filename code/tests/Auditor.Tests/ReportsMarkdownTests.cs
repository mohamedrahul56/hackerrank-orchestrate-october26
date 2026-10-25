
namespace Auditor.Tests;

/// <summary>The submission's reports must render fully as Markdown (the code judge reads .md only): balanced fences, well-formed tables, no raw debug text.</summary>
public class ReportsMarkdownTests
{
    private static readonly string[] Required =
    {
        "code/evaluation/evaluation_report.md", "code/evaluation/usage_report.md", "README.md", "code/evaluation/extra_cases.md",
    };

    [Fact]
    public void RequiredDocumentsExist() =>
        Assert.All(Required, p => Assert.True(File.Exists(Path.Combine(Repo.Root, p)), p));

    [Fact]
    public void EveryMarkdownReportIsWellFormed()
    {
        var bad = MarkdownCheck.SubmissionDocuments(Repo.Root)
            .Select(p => (Path: Path.GetRelativePath(Repo.Root, p), Problems: MarkdownCheck.Problems(p))).Where(x => x.Problems.Count > 0)
            .ToDictionary(x => x.Path, x => string.Join("; ", x.Problems));
        Assert.Empty(bad);
    }

    [Fact]
    public void TheCheckerCatchesWhatItShould()
    {
        var found = string.Join(" ", MarkdownCheck.ProblemsOfText("| a | b |\n|---|---|\n| 1 |\n\n```\nopen\n"));
        Assert.Contains("unbalanced", found, StringComparison.Ordinal);
        Assert.Contains("row has 1 cells", found, StringComparison.Ordinal);
        Assert.Contains("raw debug text", string.Join(" ", MarkdownCheck.ProblemsOfText("value Counter({'a': 1})\n")), StringComparison.Ordinal);
        Assert.Empty(MarkdownCheck.ProblemsOfText("| a | b |\n|---|---|\n| `x|y` | 2 |\n"));
    }
}
