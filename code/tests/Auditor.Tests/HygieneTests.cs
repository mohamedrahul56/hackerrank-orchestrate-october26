using System.Text.RegularExpressions;

namespace Auditor.Tests;

/// <summary>Guards for the submission rules: no secrets, no dataset in the zip, no tracked .env; the code builds without warnings; nothing is hard-coded to the labeled answers.</summary>
public class HygieneTests
{
    private static IEnumerable<string> SourceFiles(params string[] roots) =>
        roots.SelectMany(r => Directory.EnumerateFiles(Path.Combine(Repo.Code, r), "*.cs", SearchOption.AllDirectories))
             .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(x => x is "bin" or "obj"));

    [Fact]
    public void GitignoreBlocksEnvAndZip()
    {
        var path = Path.Combine(Repo.Root, ".gitignore");
        if (!File.Exists(path)) return;                                    // .gitignore is not part of code.zip
        var lines = File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var needed in new[] { ".env", "code.zip", "bin/", "obj/", "code/out/", "code/config/prices.json" }) Assert.Contains(needed, lines);
    }

    [Fact]
    public void NoEnvFileIsTracked()
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Repo.Root, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("ls-files");
        try
        {
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) return;
            Assert.DoesNotContain(output.Split('\n'), f => f.Split('/')[^1].StartsWith(".env", StringComparison.Ordinal));
        }
        catch (System.ComponentModel.Win32Exception) { }                    // git not available
    }

    [Fact]
    public void NoApiKeyLiteralsInCode()
    {
        foreach (var p in SourceFiles("src", "tools", "tests"))
            Assert.DoesNotMatch(@"sk-ant-[A-Za-z0-9_-]{10,}", File.ReadAllText(p));
    }
}

/// <summary>The code stays clean under the compiler settings of Directory.Build.props: nullable reference types on, every warning an error.</summary>
public class StaticChecksTests
{
    [Fact]
    public void TheBuildTreatsWarningsAsErrorsAndNullableIsOn()
    {
        var text = File.ReadAllText(Path.Combine(Repo.Code, "Directory.Build.props"));
        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", text, StringComparison.Ordinal);
        Assert.Contains("<Nullable>enable</Nullable>", text, StringComparison.Ordinal);
        Assert.Contains("<Deterministic>true</Deterministic>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSolutionHoldsTheLibraryTheCliTheToolsAndTheTests()
    {
        var sln = File.ReadAllText(Path.Combine(Repo.Code, "Auditor.sln"));
        foreach (var project in new[] { "Auditor.csproj", "Auditor.Cli.csproj", "Auditor.Tools.csproj", "Auditor.Tests.csproj" }) Assert.Contains(project, sln, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPythonIsLeftInTheRepository()
    {
        var found = Directory.EnumerateFiles(Repo.Root, "*", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(x => x is ".git" or "bin" or "obj" or "dataset" or "out" or ".cache"))
            .Where(p => Path.GetExtension(p) is ".py" or ".pyc" || Path.GetFileName(p) is "requirements.txt" or "requirements-dev.txt" or "pyproject.toml" || p.Contains("__pycache__", StringComparison.Ordinal))
            .Select(p => Path.GetRelativePath(Repo.Root, p)).ToList();
        Assert.Empty(found);
    }
}

/// <summary>No hardcoded labels: executable code must not mention case ids, and must not contain any resource id that appears in the labeled sample answers.
/// (Tests, the annotated sentence audit and the synthetic-case definitions are excluded: they are fixtures, not the solution.)</summary>
public class HardcodingScanTests
{
    private static List<string> CodeFiles() =>
        new[] { "src", "tools" }.SelectMany(r => Directory.EnumerateFiles(Path.Combine(Repo.Code, r), "*.cs", SearchOption.AllDirectories))
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(x => x is "bin" or "obj")).ToList();

    [Fact]
    public void ThereIsCodeToScan() => Assert.True(CodeFiles().Count > 10);

    [Fact]
    public void NoCaseIdLiterals()
    {
        var hits = CodeFiles().SelectMany(p => Regex.Matches(File.ReadAllText(p), @"\bcase_\d+\b").Select(m => $"{Path.GetRelativePath(Repo.Root, p)}: {m.Value}")).ToList();
        Assert.Empty(hits);
    }

    [DatasetFact]
    public void NoSampleAnswerFragments()
    {
        var goldIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in Csv.ReadRows(Path.Combine(Repo.Dataset, "sample_output.csv")))
            foreach (var s in Compat.AsList(Compat.ParseJson(r.G("steps_json")))) goldIds.Add((string)Compat.Get(s, "resource_id")!);
        var evidenceIds = Csv.ReadRows(Path.Combine(Repo.Dataset, "evidence_files.csv")).Select(r => r.G("file_id")).ToHashSet();
        var text = CodeFiles().ToDictionary(p => p, File.ReadAllText);
        var hits = new List<string>();
        foreach (var (p, t) in text)
            foreach (var g in goldIds.Concat(evidenceIds))
                if (Regex.IsMatch(t, $@"\b{Regex.Escape(g)}\b")) hits.Add($"{Path.GetRelativePath(Repo.Root, p)}: {g}");
        Assert.Empty(hits);
    }
}
