using System.Text.RegularExpressions;

namespace Auditor.Tests;

/// <summary>The claim-to-code map in README.md cannot rot: every file, symbol and test it names must exist.</summary>
public class ClaimsMapTests
{
    private static List<(string Claim, string Reference, string Test)> Rows()
    {
        var text = Repo.Read("README.md");
        var body = text.Split("<!-- claims:start -->")[1].Split("<!-- claims:end -->")[0];
        var rows = new List<(string, string, string)>();
        foreach (var line in body.Split('\n'))
        {
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length == 3 && cells[1].StartsWith('`') && cells[1].Contains("::", StringComparison.Ordinal))
                rows.Add((cells[0], cells[1].Trim('`'), cells[2].Trim('`')));
        }
        return rows;
    }

    /// <summary>A declaration of <paramref name="symbol"/>: a type, or a member line with a modifier that names it before `(`, `=`, `{`, `;` or `&lt;`.</summary>
    internal static bool IsDeclared(string source, string symbol)
    {
        var s = Regex.Escape(symbol);
        const string mods = @"(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|const|override|async)\s+)";
        return Regex.IsMatch(source, $@"^\s*(?:\[[^\]]*\]\s*)*{mods}*(?:class|record(?:\s+struct)?|struct|enum|interface)\s+{s}\b", RegexOptions.Multiline)
            || Regex.IsMatch(source, $@"^\s*{mods}+[^=\n]*?\b{s}\s*[(=;{{<]", RegexOptions.Multiline);
    }

    [Fact]
    public void TheMapHasRows() => Assert.True(Rows().Count >= 15);

    [Fact]
    public void EveryFileSymbolAndTestExists()
    {
        foreach (var (claim, reference, test) in Rows())
        {
            var parts = reference.Split("::");
            var file = Path.Combine(Repo.Root, parts[0]);
            Assert.True(File.Exists(file), $"{parts[0]} does not exist (claim: {claim})");
            Assert.True(IsDeclared(File.ReadAllText(file), parts[1]), $"{parts[1]} is not declared in {parts[0]}");
            Assert.True(File.Exists(Path.Combine(Repo.Code, "tests", "Auditor.Tests", test + ".cs")), $"code/tests/Auditor.Tests/{test}.cs does not exist");
        }
    }

    [Fact]
    public void TheDeclarationCheckRecognisesTypesAndMembersAndRejectsAbsentNames()
    {
        const string src = "public sealed record Foo(int A);\n    public static string Bar(int x) => \"\";\n    private const int Baz = 3;\n    // class Ghost\n";
        Assert.True(IsDeclared(src, "Foo"));
        Assert.True(IsDeclared(src, "Bar"));
        Assert.True(IsDeclared(src, "Baz"));
        Assert.False(IsDeclared(src, "Ghost"));
        Assert.False(IsDeclared(src, "Missing"));
    }
}
