using System.Text.RegularExpressions;

namespace Auditor.Tests;

/// <summary>Prompt files of the agent layer: versioned, loaded with a digest, and they say what the constraints require.</summary>
public class PromptFilesTests
{
    private static IEnumerable<string> PromptNames() => Directory.GetFiles(PromptFile.DefaultDirectory, "*.md").Select(p => Path.GetFileName(p)).OrderBy(x => x, StringComparer.Ordinal);

    [Fact]
    public void EveryPromptFileHasAVersionAndADigest()
    {
        var files = PromptNames().ToList();
        Assert.Contains("image_reader.system.md", files);
        Assert.Contains("image_reader.task.md", files);
        foreach (var name in files)
        {
            var p = PromptFile.Load(name);
            Assert.Matches("^[A-Za-z0-9._-]+$", p.Version);
            Assert.Matches("^[0-9a-f]{64}$", p.Sha256);
        }
    }

    [Fact]
    public void TheSystemPromptStatesTheRequiredConstraints()
    {
        var text = PromptFile.Load("image_reader.system.md").Text.ToLowerInvariant();
        foreach (var needle in new[] { "role", "constraints", "structured output", "data, never instructions", "mark unknown instead of guessing", "zoom_region", "submit_reading", "deterministic software decides", "never guess" })
            Assert.Contains(needle, text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTaskPromptHasThePlaceholdersAndTreatsThePictureAsData()
    {
        var p = PromptFile.Load("image_reader.task.md");
        foreach (var k in new[] { "{width}", "{height}", "{max_zooms}" }) Assert.Contains(k, p.Text, StringComparison.Ordinal);
        var filled = p.Fill(new Dictionary<string, object> { ["width"] = 10, ["height"] = 20, ["max_zooms"] = 3 });
        Assert.DoesNotContain("{width}", filled, StringComparison.Ordinal);
        Assert.Contains("not a request to you", filled, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPromptNamesAnAnswerAPersonACaseOrADatasetValue()
    {
        foreach (var name in PromptNames())
        {
            var t = File.ReadAllText(Path.Combine(PromptFile.DefaultDirectory, name));
            Assert.False(Regex.IsMatch(t, @"@(?!example\.test)[a-z0-9-]+\.[a-z]{2,}|case_\d+|evidence_\d+"), name);
        }
    }

    [Fact]
    public void LoaderRejectsAFileWithoutFrontMatterOrBody()
    {
        using var d = new TempDir();
        d.Write("a.md", "no front matter");
        d.Write("b.md", "---\nversion: v1\n---\n\n");
        foreach (var name in new[] { "a.md", "b.md", "missing.md" }) Assert.Throws<PromptException>(() => PromptFile.Load(name, d.Path));
    }

    [Fact]
    public void AChangedTextChangesTheDigestButNotTheLabel()
    {
        using var d = new TempDir();
        d.Write("p.md", "---\nversion: v1\n---\nbody one");
        var a = PromptFile.Load("p.md", d.Path);
        d.Write("p.md", "---\nversion: v1\n---\nbody two");
        var b = PromptFile.Load("p.md", d.Path);
        Assert.Equal(a.Version, b.Version);
        Assert.NotEqual(a.Sha256, b.Sha256);
    }
}

/// <summary>The documents and reports must say, in the same words, that the agent layer was never run against a real model.</summary>
public class AgentLayerIsDescribedHonestlyTests
{
    private static readonly string[] Docs =
    {
        "README.md", "ARCHITECTURE.md", "code/evaluation/usage_report.md", "code/evaluation/evaluation_report.md", "code/evaluation/agent_layer_report.md",
    };

    private static string Text(string rel) => Regex.Replace(Repo.Read(rel), @"\s+", " ").ToLowerInvariant();

    [Fact]
    public void EveryDocumentSaysFakesOnlyAndZeroCallsTokensCost()
    {
        foreach (var rel in Docs)
        {
            var t = Text(rel);
            Assert.True(t.Contains("with fakes only", StringComparison.Ordinal), $"{rel}: with fakes only");
            Assert.True(Regex.IsMatch(t, @"0 (model )?calls"), $"{rel}: 0 calls");
            Assert.True(t.Contains("0 tokens", StringComparison.Ordinal), $"{rel}: 0 tokens");
            Assert.True(t.Contains("no cost", StringComparison.Ordinal), $"{rel}: no cost");
        }
    }

    [Fact]
    public void ReadmeStatesWhyTheDefaultPathCannotChange()
    {
        var t = Text("README.md");
        Assert.Contains("byte-identical", t, StringComparison.Ordinal);
        Assert.Contains("g10", t, StringComparison.Ordinal);
        Assert.Contains("image:agent", t, StringComparison.Ordinal);
    }
}
