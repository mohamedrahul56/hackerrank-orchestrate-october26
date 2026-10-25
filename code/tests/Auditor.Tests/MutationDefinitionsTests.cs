
namespace Auditor.Tests;

/// <summary>The mutation check (Auditor.Tools mutation-check) is only meaningful while each mutation still applies to the current source.</summary>
public class MutationDefinitionsTests
{
    private static int Count(string text, string needle)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Fact]
    public void EveryMutationAppliesExactlyOnceAndChangesSomething()
    {
        foreach (var m in MutationCheck.Mutations)
        {
            var text = File.ReadAllText(Path.Combine(Repo.Root, m.File));
            Assert.True(Count(text, m.Old) == 1, $"{m.Key}: the text to replace occurs {Count(text, m.Old)} times in {m.File}");
            Assert.NotEqual(m.Old, m.New);
        }
    }

    [Fact]
    public void ThreePrimaryMutationsWithDistinctKeys()
    {
        Assert.Equal(3, MutationCheck.Mutations.Count(m => m.Primary));
        Assert.Equal(MutationCheck.Mutations.Length, MutationCheck.Mutations.Select(m => m.Key).Distinct().Count());
    }

    [Fact]
    public void TheMutationTestSetExists()
    {
        var sources = string.Join("\n", Directory.GetFiles(Path.Combine(Repo.Code, "tests", "Auditor.Tests"), "*.cs").Select(File.ReadAllText));
        foreach (var cls in MutationCheck.TestClasses)
            Assert.Matches($@"\bclass\s+{cls}\b", sources);
    }
}
