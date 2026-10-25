using System.IO.Compression;
using System.Text;

namespace Auditor.Tests;

public class ScoreTests
{
    private static Scorer.RawStep Step(long no, string phase, string op, string state, string system, string res, string ev = "") =>
        new(new Dictionary<string, object?>
        {
            ["step_no"] = no, ["phase"] = phase, ["system_id"] = system, ["resource_id"] = res, ["operation"] = op, ["state"] = state, ["evidence_id"] = ev,
        });

    private static Dictionary<string, Scorer.Row> Gold() => new()
    {
        ["a"] = new Scorer.Row("INCOMPLETE", new()
        {
            Step(1, "FINDING", "REPORT_REMAINING_RECORD", "PRESENT", "search_service", "search_1", "evidence_01"),
            Step(2, "REMEDIATION", "DELETE_RECORD", "ABSENT", "search_service", "search_1"),
            Step(3, "VERIFICATION", "VERIFY_RECORD_ABSENT", "ABSENT", "search_service", "search_1"),
        }),
        ["b"] = new Scorer.Row("NOT_APPLICABLE", new()),
    };

    private static Dictionary<string, Scorer.Row> Copy(Dictionary<string, Scorer.Row> g) =>
        g.ToDictionary(kv => kv.Key, kv => new Scorer.Row(kv.Value.Status, kv.Value.Steps.Select(s => new Scorer.RawStep(new Dictionary<string, object?>(s.Fields))).ToList()));

    [Fact]
    public void Perfect()
    {
        var m = Scorer.Score(Gold(), Copy(Gold()), new() { "a", "b" });
        Assert.Equal(2, m.StatusCorrect);
        Assert.Equal((1.0, 1.0, 1.0), m.StepStrict);
        Assert.Equal(2, m.ExactCases);
        Assert.Equal(1.0, m.MeanLcsFull);
        Assert.All(m.FieldAcc.Values, v => Assert.Equal(1.0, v));
    }

    [Fact]
    public void WrongStatusAndMissingRow()
    {
        var pred = new Dictionary<string, Scorer.Row> { ["a"] = new Scorer.Row("COMPLETE", new()) };
        var m = Scorer.Score(Gold(), pred, new() { "a", "b" });
        Assert.Equal(0, m.StatusCorrect);
        Assert.Equal(1, m.MissingCases);
        Assert.Equal(1, m.Confusion[("INCOMPLETE", "COMPLETE")]);
        Assert.Equal(1, m.Confusion[("NOT_APPLICABLE", "(missing)")]);
    }

    [Fact]
    public void OrderOnlyErrorKeepsSetMetricsButNotSequence()
    {
        var pred = Copy(Gold());
        pred["a"] = new Scorer.Row(pred["a"].Status, Enumerable.Reverse(pred["a"].Steps).ToList());
        var m = Scorer.Score(Gold(), pred, new() { "a", "b" });
        Assert.Equal(1.0, m.StepStrict.F1);
        Assert.Equal(1, m.ExactCases);
        Assert.True(m.MeanLcsFull < 1.0);
        Assert.True(m.Cases.First(c => c.CaseId == "a").OrderOnly);
    }

    [Fact]
    public void EvidenceOnlyErrorHitsStrictNotRelaxed()
    {
        var pred = Copy(Gold());
        pred["a"].Steps[0].Fields["evidence_id"] = "evidence_02";
        var m = Scorer.Score(Gold(), pred, new() { "a" });
        Assert.True(m.StepStrict.F1 < 1.0);
        Assert.Equal(1.0, m.StepRelaxed.F1);
        Assert.Equal(1.0, m.Discovery.F1);
        Assert.Equal(2.0 / 3, m.FieldAcc["evidence_id"], 9);
    }

    [Fact]
    public void AlignmentSurvivesAMissingStep()
    {
        // Dropping the first step must not zero the field accuracy of the others (index shift).
        var pred = Copy(Gold());
        pred["a"] = new Scorer.Row(pred["a"].Status, pred["a"].Steps.Skip(1).ToList());
        var m = Scorer.Score(Gold(), pred, new() { "a" });
        Assert.Equal(2.0 / 3, m.FieldAcc["resource_id"], 9);
        Assert.Equal(2.0 / 3, m.StepStrict.R, 9);
    }

    [Fact]
    public void AlignPrefersExactMatch()
    {
        var g = new List<Scorer.RawStep> { Step(1, "VERIFICATION", "VERIFY_RECORD_ABSENT", "ABSENT", "a", "x") };
        var p = new List<Scorer.RawStep> { Step(1, "VERIFICATION", "VERIFY_RECORD_PRESENT", "PRESENT", "a", "x"), new(new Dictionary<string, object?>(g[0].Fields)) };
        Assert.Same(p[1], Scorer.Align(g, p)[0]);
    }

    [Fact]
    public void LcsAndPrfEdges()
    {
        Assert.Equal(1.0, Scorer.LcsRatio(new List<string>(), new List<string>()));
        Assert.Equal(0.0, Scorer.LcsRatio(new List<string> { "a" }, new List<string>()));
        Assert.Equal(2.0 * 2 / 6, Scorer.LcsRatio(new List<string> { "a", "b", "c" }, new List<string> { "a", "c", "b" }), 9);
        Assert.Equal((1.0, 1.0, 1.0), Scorer.Prf(0, 0, 0));
    }

    [Fact]
    public void BadJsonRowDoesNotCrash()
    {
        using var d = new TempDir();
        var p = d.Write("p.csv", "case_id,deletion_status,steps_json\na,INCOMPLETE,\"not json\"\nb,NOT_APPLICABLE,[]\n");
        var rows = Scorer.ReadRows(p);
        var m = Scorer.Score(Gold(), rows, new() { "a", "b" });
        Assert.Equal(1, m.BadRows);
        Assert.Equal(2, m.StatusCorrect);
    }

    [DatasetFact]
    public void SplitIsDeterministicDisjointAndNontrivial()
    {
        var gold = Scorer.ReadRows(Path.Combine(Repo.Dataset, "sample_output.csv"));
        var s1 = Scorer.AssignSplits(gold);
        var s2 = Scorer.AssignSplits(gold.Reverse().ToDictionary(kv => kv.Key, kv => kv.Value));
        Assert.Equal(s1.OrderBy(kv => kv.Key, StringComparer.Ordinal), s2.OrderBy(kv => kv.Key, StringComparer.Ordinal));        // independent of row order
        Assert.True(s1.Keys.ToHashSet().SetEquals(gold.Keys));
        var held = s1.Where(kv => kv.Value == "heldout").Select(kv => kv.Key).ToList();
        Assert.True(held.Count >= 8);
        Assert.True(held.Select(c => gold[c].Status).ToHashSet().SetEquals(new[] { "COMPLETE", "INCOMPLETE" }));
    }

    [DatasetFact]
    public void GoldScoresPerfectlyAgainstItself()
    {
        var gold = Scorer.ReadRows(Path.Combine(Repo.Dataset, "sample_output.csv"));
        var m = Scorer.Score(gold, gold, gold.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
        Assert.Equal((32, 32, 1.0), (m.StatusCorrect, m.ExactCases, m.StepStrict.F1));
    }
}

/// <summary>The packaging tool: what goes into code.zip, what must stay out, and the secrets / dataset-leak scans.</summary>
public class PackagingRulesTests : IDisposable
{
    private static readonly string FakeKey = "sk-" + "ant-" + "api03-" + "A1b2C3d4E5f6G7h8I9j0";          // assembled at run time so this file itself holds no key-shaped literal
    private readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    private static readonly Dictionary<string, string> Base = new()
    {
        ["README.md"] = "# run it", ["ARCHITECTURE.md"] = "# design", ["Makefile"] = "test:\n\ttrue\n", ["code/Auditor.sln"] = "sln", ["code/src/Auditor/Auditor.csproj"] = "<Project/>",
        ["code/src/Auditor.Cli/Program.cs"] = "// main", ["code/notes.md"] = "notes", ["code/evaluation/extra_cases.md"] = "cases", ["code/config/prices.example.json"] = "{}",
    };

    private void Tree(Dictionary<string, string> files)
    {
        foreach (var (name, content) in files)
        {
            var p = Path.Combine(tmp.Path, name);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, Encoding.UTF8.GetBytes(content));
        }
    }

    private List<string> Names(Dictionary<string, string> files)
    {
        Tree(Base.Concat(files).ToDictionary(kv => kv.Key, kv => kv.Value));
        return Packager.Collect(tmp.Path).Select(p => Packager.Rel(p, tmp.Path)).ToList();
    }

    [Fact]
    public void ExcludedPathsNeverEnterTheZip()
    {
        var names = Names(new()
        {
            ["dataset/deletion_requests.csv"] = "x", [".env"] = "K=1", ["code/.env.local"] = "K=1", ["code/out/pred.csv"] = "x", ["code/.cache/llm/a.json"] = "{}", ["code/config/prices.json"] = "{}",
            ["code/src/Auditor/bin/Release/Auditor.dll"] = "MZ", ["code/src/Auditor/obj/project.assets.json"] = "{}", ["code/tests/Auditor.Tests/TestResults/x.trx"] = "x",
            ["output.csv"] = "a",
        });
        Assert.Equal(Base.Keys.OrderBy(k => k, StringComparer.Ordinal), names.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void ChecksFlagArchivesJudgeIgnoredFilesAndDatasetContent()
    {
        Tree(Base.Concat(new Dictionary<string, string> { ["code/data.csv"] = "a,b", ["code/notes.sql"] = "select 1", ["code/old.zip"] = "PK", ["code/dataset/x.md"] = "x" }).ToDictionary(kv => kv.Key, kv => kv.Value));
        var (bad, _) = Packager.Check(Packager.Collect(tmp.Path), tmp.Path);
        var text = string.Join("\n", bad);
        Assert.Contains("code/data.csv: file type the code judge ignores", text, StringComparison.Ordinal);
        Assert.Contains("code/notes.sql", text, StringComparison.Ordinal);
        Assert.Contains("code/old.zip: nested archive", text, StringComparison.Ordinal);
        Assert.Contains("dataset content in the zip", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePriceTemplateIsTheOnlyJudgeIgnoredFileAllowed()
    {
        Tree(Base);
        Assert.Empty(Packager.Check(Packager.Collect(tmp.Path), tmp.Path).Bad);
    }

    [Fact]
    public void SecretShapesAreDetectedEverywhereIncludingMarkdownAndThePriceFile()
    {
        Tree(Base.ToDictionary(kv => kv.Key, kv => kv.Value));
        Tree(new() { ["code/notes.md"] = $"key: {FakeKey}", ["code/token.md"] = "token ghp_" + new string('a', 36), ["code/config/prices.json"] = "{\"api_key\": \"" + new string('x', 24) + "\"}" });
        var (bad, _) = Packager.Check(Packager.Collect(tmp.Path), tmp.Path);
        var text = string.Join("\n", bad);
        Assert.Contains("code/notes.md: looks like a Anthropic key", text, StringComparison.Ordinal);
        Assert.Contains("code/token.md: looks like a GitHub token", text, StringComparison.Ordinal);
        Assert.Contains("prices.json (not zipped, scanned anyway): looks like a key-like assignment", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryTextIsNotFlagged()
    {
        Tree(Base);
        Tree(new() { ["code/notes.md"] = "Set ANTHROPIC_API_KEY in your shell. Use sk-... never. password = prompt for it. token_count=5" });
        Assert.Empty(Packager.Check(Packager.Collect(tmp.Path), tmp.Path).Bad);
    }

    [Fact]
    public void DatasetPeopleMustNotLeakIntoTheZip()
    {
        Tree(Base);
        Tree(new() { ["dataset/user_identities.csv"] = "user_id,name,email,phone,email_hash,updated_at\nu1,A,real.person@corp.test,1,h,t\n", ["code/notes.md"] = "mentions Real.Person@corp.test once" });
        var (bad, _) = Packager.Check(Packager.Collect(tmp.Path), tmp.Path);
        Assert.Contains(bad, b => b.Contains("e-mail address(es) of dataset people", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingRequiredFilesFailAndFinalFilesAreRequiredOnlyOnRequest()
    {
        Tree(Base.Where(kv => kv.Key != "README.md").ToDictionary(kv => kv.Key, kv => kv.Value));
        Assert.Contains(Packager.Check(Packager.Collect(tmp.Path), tmp.Path).Bad, b => b.Contains("missing required file README.md", StringComparison.Ordinal));
        Tree(Base);
        Assert.Empty(Packager.Check(Packager.Collect(tmp.Path), tmp.Path).Bad);
        Assert.Equal(Packager.FinalRequired.Length, Packager.Check(Packager.Collect(tmp.Path), tmp.Path, requireFinal: true).Bad.Count);
    }

    [Fact]
    public void TwoBuildsAreByteIdenticalAndHoldExactlyTheCollectedFiles()
    {
        Tree(Base);
        var files = Packager.Collect(tmp.Path);
        string a = Path.Combine(tmp.Path, "a.zip"), b = Path.Combine(tmp.Path, "b.zip");
        Packager.Build(files, a, tmp.Path);
        Packager.Build(files, b, tmp.Path);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        using var z = ZipFile.OpenRead(a);
        Assert.Equal(Base.Keys.OrderBy(k => k, StringComparer.Ordinal), z.Entries.Select(e => e.FullName).OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(z.Entries, e => Assert.Equal(new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero), e.LastWriteTime));
    }
}

public class PackagingTheRealRepositoryTests : IDisposable
{
    private readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    [Fact]
    public void DryRunPassesAndTheZipIsClean()
    {
        var files = Packager.Collect(Repo.Root);
        var (bad, _) = Packager.Check(files, Repo.Root);
        Assert.Empty(bad);
        var names = files.Select(p => Packager.Rel(p, Repo.Root)).ToList();
        Assert.DoesNotContain(names, n => n.StartsWith("dataset/", StringComparison.Ordinal) || n.Contains("/dataset/", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Split('/')[^1].StartsWith(".env", StringComparison.Ordinal) || n.EndsWith("prices.json", StringComparison.Ordinal) || n.StartsWith("code/out/", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Split('/').Any(x => x is "bin" or "obj"));
        Assert.DoesNotContain(names, n => Packager.ArchiveExt.Contains(Path.GetExtension(n)));
        Assert.Contains("code/evaluation/extra_cases.md", names);
        Assert.Equal(new[] { "code/config/prices.example.json" }, names.Where(n => Packager.JudgeIgnoredExt.Contains(Path.GetExtension(n))));
    }

    [Fact]
    public void RealBuildIsDeterministic()
    {
        string a = tmp.File("a.zip"), b = tmp.File("b.zip");
        Packager.Build(Packager.Collect(Repo.Root), a, Repo.Root);
        Packager.Build(Packager.Collect(Repo.Root), b, Repo.Root);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        using var z = ZipFile.OpenRead(a);
        Assert.NotEmpty(z.Entries);
    }

    [Fact]
    public void ThePriceFileHoldsNoSecret()
    {
        foreach (var f in new[] { Path.Combine(Repo.Code, "config", "prices.json") })
            if (File.Exists(f)) Assert.Empty(Packager.ScanText(Path.GetFileName(f), File.ReadAllText(f)));
    }

    [Fact]
    public void EveryTrackedTextFileIsFreeOfSecretShapes()
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Repo.Root, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("ls-files");
        string output;
        try
        {
            using var p = Process.Start(psi)!;
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) return;                         // not a git checkout (an extracted code.zip): nothing to scan
        }
        catch (System.ComponentModel.Win32Exception) { return; }
        var hits = new List<string>();
        foreach (var n in output.Split('\n').Where(n => n.Length > 0 && !n.StartsWith("dataset/", StringComparison.Ordinal)))
        {
            var full = Path.Combine(Repo.Root, n);
            if (!File.Exists(full)) continue;
            var t = Packager.ReadText(full);
            if (t is not null) hits.AddRange(Packager.ScanText(n, t));
        }
        Assert.Empty(hits);
    }
}

public class UsageReportTests
{
    private static Dictionary<string, object?> Live(string model, long input, long output, string kind = "vision") =>
        new() { ["provider"] = "anthropic", ["model"] = model, ["kind"] = kind, ["cache_hit"] = false, ["calls"] = 1L, ["input_tokens"] = input, ["output_tokens"] = output };

    private static string Summarise(List<Dictionary<string, object?>> rows, int n, Dictionary<string, Prices.Price> prices, params string[] problems) =>
        UsageReport.Summarise(rows, n, prices, problems.ToList(), "", "/");

    [Fact]
    public void WithoutPricesTheCostLineSaysSoAndTokensAreExact()
    {
        var rows = new List<Dictionary<string, object?>> { Live("claude-haiku-5-5", 1000, 100), Live("claude-haiku-5-5", 3000, 300), Live("claude-sonnet-5-5", 500, 50) };
        var text = Summarise(rows, 10, new(), "no price file at x");
        Assert.Contains("cost not computed - prices not supplied", text, StringComparison.Ordinal);
        Assert.Contains("| anthropic | claude-haiku-5-5 | 2 | 2 | 4000 | 400 | 4400 | cost not computed - prices not supplied |", text, StringComparison.Ordinal);
        Assert.Contains("total tokens: **4950**; average per request: **495.0**", text, StringComparison.Ordinal);
        Assert.DoesNotContain("$", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithCompletePricesCostIsComputedAndSourceIsCited()
    {
        var (p, _) = Prices.Parse(new() { ["claude-haiku-5-5"] = new Dictionary<string, object?> { ["input_per_mtok"] = 1L, ["output_per_mtok"] = 5L, ["source_url"] = "https://example.test/p", ["date"] = "2026-10-25" } });
        var text = Summarise(new() { Live("claude-haiku-5-5", 1_000_000, 200_000) }, 4, p);
        Assert.Contains("$2.0000", text, StringComparison.Ordinal);
        Assert.Contains("https://example.test/p", text, StringComparison.Ordinal);
        Assert.Contains("$0.500000", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CacheHitsAreFlaggedAsNotCold()
    {
        var text = Summarise(new() { Live("claude-haiku-5-5", 1, 1), new() { ["provider"] = "anthropic", ["model"] = "claude-haiku-5-5", ["cache_hit"] = true } }, 2, new());
        Assert.Contains("Not a cold-cache run", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunInfoRowTiesTheReportToTheOutputFileAndIsNotACall()
    {
        var info = new Dictionary<string, object?>
        {
            ["kind"] = "run_info", ["images_read_by_model"] = false, ["api_key_present"] = false, ["requests"] = 518L, ["output_file"] = "output.csv", ["output_sha256"] = string.Concat(Enumerable.Repeat("ab", 32)),
            ["status_counts"] = new Dictionary<string, object?> { ["COMPLETE"] = 1L }, ["provisional_rows"] = new List<object?> { "case_34" },
        };
        var text = Summarise(new() { info }, 518, new());
        Assert.Contains("output.csv", text, StringComparison.Ordinal);
        Assert.Contains("images read by a vision model: **no**", text, StringComparison.Ordinal);
        Assert.Contains("API key present in the environment: **no**", text, StringComparison.Ordinal);
        Assert.Contains("case_34", text, StringComparison.Ordinal);
        Assert.Contains("0 calls, 0 input tokens, 0 output tokens", text, StringComparison.Ordinal);
        Assert.Contains("tested only with fakes", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyLogReportsZeroCalls()
    {
        var text = Summarise(new(), 518, new());
        Assert.Contains("No model calls were made", text, StringComparison.Ordinal);
        Assert.Contains("average per request: **0.0**", text, StringComparison.Ordinal);
    }
}

/// <summary>show-readings: with a fake model the page shows the picture, the raw JSON and the derived observations; without a key it says what to do.</summary>
public class ShowReadingsTests : IDisposable
{
    private readonly TempDir tmp = new();
    private readonly EnvScope env = EnvScope.Clean();

    public ShowReadingsTests()
    {
        Directory.CreateDirectory(tmp.File("evidence"));
        File.WriteAllBytes(tmp.File("evidence/shot.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x20, 0x66, 0x61, 0x6B, 0x65 });
        tmp.Write("evidence_files.csv", "file_id,file_path,origin,captured_at\nevidence_91,evidence/shot.png,connector_console,2026-08-10T12:00:00Z\nevidence_92,evidence/notes.txt,other,2026-08-10T12:00:00Z\n");
        tmp.Write("system_activity.csv", "activity_id,event_time,event_type,source_system,target_system,resource_id,reported_result,details\n");
    }

    public void Dispose() { env.Dispose(); tmp.Dispose(); }

    private static IImageReader Reader(Func<Dictionary<string, object?>> f) => new FuncImageReader((_, _, _, _, _) => f());

    [Fact]
    public void OnlyPngsAreListedAndEachGetsPictureJsonAndObservations()
    {
        var items = ShowReadings.ReadAll(tmp.Path, Reader(Samples.Good));
        Assert.Equal(new[] { "evidence_91" }, items.Select(i => i.Row.FileId));
        var page = ShowReadings.RenderHtml(items);
        Assert.Contains("data:image/png;base64,", page, StringComparison.Ordinal);
        Assert.Contains("data_flow_1121", page, StringComparison.Ordinal);
        Assert.Contains("ACTIVE", page, StringComparison.Ordinal);                  // derived from the status words "Healthy, Continuous"
        var text = ShowReadings.RenderText(items);
        Assert.Contains("data_flow_1121", text, StringComparison.Ordinal);
        Assert.Contains("--- derived observations:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedReadingIsShownNotRaised()
    {
        var items = ShowReadings.ReadAll(tmp.Path, Reader(() => throw new InvalidOperationException("model unavailable")));
        Assert.Contains("model unavailable", items[0].Error, StringComparison.Ordinal);
        Assert.Contains("READING FAILED", ShowReadings.RenderHtml(items), StringComparison.Ordinal);
    }

    [Fact]
    public void ModelTextIsEscapedInThePage()
    {
        var evil = (Dictionary<string, object?>)Compat.ParseJson(
            "{\"screen\": \"<script>alert(1)</script>\", \"records\": [{\"resource_id\": \"<img src=x onerror=alert(1)>\", \"selected\": false, \"emails\": [], \"user_ids\": [], \"phones\": [], \"status_words\": [], \"shows_direct_identifiers\": false, \"aliases\": []}]}")!;
        var page = ShowReadings.RenderHtml(ShowReadings.ReadAll(tmp.Path, Reader(() => evil)));
        Assert.DoesNotContain("<script>alert(1)", page, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAKeyTheToolExplainsAndExits2()
    {
        var err = new StringWriter();
        var code = ShowReadings.Run(new[] { "--dataset", tmp.Path }, new StringWriter(), err);
        Assert.Equal(2, code);
        Assert.Contains("export ANTHROPIC_API_KEY", err.ToString(), StringComparison.Ordinal);
        Assert.Contains("never written to a file", err.ToString(), StringComparison.Ordinal);
    }
}
