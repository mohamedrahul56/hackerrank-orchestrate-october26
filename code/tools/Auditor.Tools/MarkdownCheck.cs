using System.Text.RegularExpressions;

namespace Auditor.Tools;

/// <summary>The submission's reports must render fully as Markdown (the code judge reads .md only): balanced fences, well-formed tables, no raw debug text.</summary>
public static class MarkdownCheck
{
    private static readonly string[] BadTokens = { "{{", "Counter(", "defaultdict(", "<object at", "Traceback (most recent", "nan%", "None%", "{'", "datetime.datetime(", "frozenset(", "System.Collections", "Unhandled exception" };
    private static readonly Regex Separator = new(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?$", RegexOptions.CultureInvariant);
    private static readonly Regex CodeSpan = new(@"`[^`]*`", RegexOptions.CultureInvariant);
    private static readonly Regex CodeSpanOneLine = new(@"`[^`\n]*`", RegexOptions.CultureInvariant);
    private static readonly Regex Fenced = new(@"```.*?```", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex UnescapedPipe = new(@"(?<!\\)\|", RegexOptions.CultureInvariant);

    public static int Cells(string line)
    {
        var s = line.Trim();
        if (s.StartsWith('|')) s = s[1..];
        if (s.EndsWith('|') && !s.EndsWith("\\|", StringComparison.Ordinal)) s = s[..^1];
        s = CodeSpan.Replace(s, m => m.Value.Replace("|", "\\|", StringComparison.Ordinal));
        return UnescapedPipe.Split(s).Length;
    }

    public static List<string> Problems(string path) => ProblemsOfText(File.ReadAllText(path));

    public static List<string> ProblemsOfText(string text)
    {
        var output = new List<string>();
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count(l => l.TrimStart().StartsWith("```", StringComparison.Ordinal)) % 2 == 1) output.Add("unbalanced code fences");
        bool inFence = false;
        var block = new List<string>();
        for (int i = 0; i <= lines.Count; i++)
        {
            var l = i < lines.Count ? lines[i] : "";
            if (l.TrimStart().StartsWith("```", StringComparison.Ordinal)) { inFence = !inFence; continue; }
            if (inFence) continue;
            if (l.StartsWith('|')) { block.Add(l); continue; }
            if (block.Count > 0)
            {
                if (block.Count < 2 || !Separator.IsMatch(block[1].Trim())) output.Add($"table before line {i + 1} has no separator row");
                else
                {
                    int n = Cells(block[0]);
                    foreach (var r in block.Skip(2))
                        if (Cells(r) != n)
                        {
                            output.Add($"table before line {i + 1}: row has {Cells(r)} cells, header {n}: {r[..Math.Min(80, r.Length)]}");
                            break;
                        }
                }
                block.Clear();
            }
        }
        var visible = CodeSpanOneLine.Replace(Fenced.Replace(text, ""), "");
        foreach (var t in BadTokens)
            if (visible.Contains(t, StringComparison.Ordinal)) output.Add($"raw debug text '{t}' in the rendered Markdown");
        return output;
    }

    /// <summary>The Markdown documents of the submission (reports, READMEs, notes).</summary>
    public static List<string> SubmissionDocuments(string root) =>
        Directory.GetFiles(Path.Combine(root, "code", "evaluation"), "*.md").OrderBy(p => p, StringComparer.Ordinal)
            .Concat(new[] { "README.md", "ARCHITECTURE.md", "code/NOTES.md", "code/SPEC.md", "code/INTERVIEW_PREP.md" }.Select(p => Path.Combine(root, p)))
            .Where(File.Exists).ToList();
}
