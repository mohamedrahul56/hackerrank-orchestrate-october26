using System.Security.Cryptography;

namespace Auditor.Tests;

/// <summary>Determinism across environments that must not matter: working directory, locale / culture, time zone (and .NET's per-process randomised string hashing).</summary>
public class DeterminismTests : IDisposable
{
    private static readonly string[] Files = { "output.csv", "output.notes.md", "output.usage.jsonl" };
    private readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    private sealed record Run(int N, string Cwd, string Locale, string Tz, int Exit, Dictionary<string, string> Hashes, string Dir);

    private static string Sha(string p) => File.Exists(p) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant() : "(missing)";

    /// <summary>Quick matrix: each run uses another working directory, locale and time zone (three runs; the full 3 x 3 matrix is `make determinism`).</summary>
    private List<Run> RunMatrix(string dataset, bool full)
    {
        var cwds = new[] { Repo.Root, tmp.Path, "/" };
        var locales = new[] { ("C", "UTC"), ("C.utf8", "America/Los_Angeles"), ("POSIX", "Asia/Kolkata") };
        var runs = new List<Run>();
        int n = 0;
        for (int ci = 0; ci < cwds.Length; ci++)
            for (int li = 0; li < locales.Length; li++)
            {
                if (!full && ci != li) continue;
                n++;
                var outDir = Path.Combine(tmp.Path, $"run{n:D2}");
                Directory.CreateDirectory(outDir);
                var env = new Dictionary<string, string?> { ["LC_ALL"] = locales[li].Item1, ["LANG"] = locales[li].Item1, ["TZ"] = locales[li].Item2, ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = li == 1 ? "0" : "1" };
                var (code, _, _) = Proc.Run(new[] { "--dataset", dataset, "--out", Path.Combine(outDir, "output.csv"), "--images", "off" }, cwds[ci], env);
                runs.Add(new Run(n, cwds[ci], locales[li].Item1, locales[li].Item2, code, Files.ToDictionary(f => f, f => Sha(Path.Combine(outDir, f))), outDir));
            }
        return runs;
    }

    [DatasetFact]
    public void OutputNotesAndUsageLogAreByteIdenticalAcrossTheEnvironmentMatrix()
    {
        var runs = RunMatrix(Repo.Dataset, full: false);
        Assert.All(runs, r => Assert.Equal(0, r.Exit));
        Assert.Equal(3, runs.Count);
        Assert.Equal(3, runs.Select(r => r.Cwd).Distinct().Count());
        Assert.Equal(3, runs.Select(r => r.Locale).Distinct().Count());
        foreach (var f in Files)
        {
            Assert.Single(runs.Select(r => r.Hashes[f]).Distinct());
            Assert.NotEqual("(missing)", runs[0].Hashes[f]);
        }
    }

    [DatasetFact]
    public void TheMatrixOutputIsTheSubmittedFile()
    {
        var runs = RunMatrix(Repo.Dataset, full: false);
        var submitted = Sha(Path.Combine(Repo.Root, "output.csv"));
        Assert.All(runs, r => Assert.Equal(submitted, r.Hashes["output.csv"]));
    }
}
