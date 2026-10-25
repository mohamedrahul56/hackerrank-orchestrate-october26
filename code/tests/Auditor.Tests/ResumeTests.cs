using System.Security.Cryptography;

namespace Auditor.Tests;

/// <summary>Kill a run in the middle, resume it, and require the same output.csv (and notes) as a run that was never interrupted.</summary>
public class KillAndResumeTests : IDisposable
{
    private static readonly TempDir Base = new();
    private static string cleanSha = "", cleanNotes = "";
    private static int total;
    private static string cleanOut = "";
    private static bool prepared;

    private static string Sha(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();

    private static (int Code, string Err) RunMain(string outPath, params string[] args)
    {
        var (code, _, err) = Proc.Run(new[] { "--out", outPath, "--images", "off" }.Concat(args));
        return (code, err);
    }

    private static void RunAndKill(string outPath, int after, params string[] args)
    {
        // Start a real run that stops itself after `after` finished cases, and kill it there (no cleanup code gets to run).
        using var p = Process.Start(Proc.Start(new[] { "--out", outPath, "--images", "off" }.Concat(args), env: new Dictionary<string, string?> { ["AUDITOR_TEST_PAUSE_AFTER"] = after.ToString() }))!;
        bool reached = false;
        string? line;
        while ((line = p.StandardError.ReadLine()) is not null)
            if (line.StartsWith("CHECKPOINT-REACHED", StringComparison.Ordinal)) { reached = true; break; }
        Assert.True(reached, "the run ended before reaching the kill point");
        p.Kill(entireProcessTree: true);
        p.WaitForExit(30000);
        Assert.NotEqual(0, p.ExitCode);
    }

    private static void Prepare()
    {
        if (prepared) return;
        Directory.CreateDirectory(Path.Combine(Base.Path, "clean"));
        cleanOut = Path.Combine(Base.Path, "clean", "o.csv");
        var (rc, err) = RunMain(cleanOut);
        Assert.True(rc == 0, err);
        cleanSha = Sha(cleanOut); cleanNotes = Sha(Path.ChangeExtension(cleanOut, ".notes.md"));
        total = File.ReadAllLines(cleanOut).Length - 1;
        prepared = true;
    }

    private static string WorkDir(string name)
    {
        var d = Path.Combine(Base.Path, name);
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return Path.Combine(d, "o.csv");
    }

    private static string Partial(string outPath) => Path.ChangeExtension(outPath, ".partial.jsonl");
    private static string[] PartialLines(string outPath) => File.ReadAllLines(Partial(outPath));

    public void Dispose() { }

    [DatasetFact]
    public void ACleanRunLeavesNoCheckpoint()
    {
        Prepare();
        Assert.False(File.Exists(Partial(cleanOut)));
        Assert.Equal(518, total);
    }

    [DatasetFact]
    public void SigkillMidwayThenResumeGivesTheIdenticalOutput()
    {
        Prepare();
        var outPath = WorkDir("killed");
        RunAndKill(outPath, 200);
        Assert.False(File.Exists(outPath), "a killed run must not have written an output file");
        Assert.Equal(1 + 200, PartialLines(outPath).Length);                               // a header and the 200 finished cases
        var (rc, err) = RunMain(outPath);
        Assert.True(rc == 0, err);
        Assert.Contains("resumed (200 cases kept)", err, StringComparison.Ordinal);
        Assert.Contains($"200 of {total} cases reused, {total - 200} audited", err, StringComparison.Ordinal);
        Assert.Equal(cleanSha, Sha(outPath));                                              // the resumed output.csv must equal the clean run's byte for byte
        Assert.Equal(cleanNotes, Sha(Path.ChangeExtension(outPath, ".notes.md")));
        Assert.False(File.Exists(Partial(outPath)), "the checkpoint is removed after a successful run");
    }

    [DatasetFact]
    public void KilledTwiceStillResumes()
    {
        Prepare();
        var outPath = WorkDir("twice");
        RunAndKill(outPath, 50);
        RunAndKill(outPath, 300);                                                          // the second run reuses 50, audits 250 more, and is killed
        Assert.Equal(1 + 300, PartialLines(outPath).Length);
        var (rc, err) = RunMain(outPath);
        Assert.True(rc == 0, err);
        Assert.Contains("300 of 518 cases reused", err, StringComparison.Ordinal);
        Assert.Equal(cleanSha, Sha(outPath));
    }

    [DatasetFact]
    public void ALineCutShortByTheKillIsDroppedAndRedone()
    {
        Prepare();
        var outPath = WorkDir("torn");
        RunAndKill(outPath, 120);
        File.AppendAllText(Partial(outPath), "{\"d\":{\"case_id\":\"case_999\",\"kind\":\"case\",\"status\":\"COMPL");           // a write that was cut off
        var (rc, err) = RunMain(outPath);
        Assert.True(rc == 0, err);
        Assert.Contains("resumed (120 cases kept, damaged tail dropped)", err, StringComparison.Ordinal);
        Assert.Equal(cleanSha, Sha(outPath));
    }

    [DatasetFact]
    public void ADamagedLineInTheMiddleDropsItAndEverythingAfter()
    {
        Prepare();
        var outPath = WorkDir("damaged");
        RunAndKill(outPath, 100);
        var lines = PartialLines(outPath);
        var obj = Compat.AsDict(Compat.ParseJson(lines[41]));
        var d = Compat.AsDict(obj["d"]);
        d["status"] = (string)d["status"]! != "COMPLETE" ? "COMPLETE" : "INCOMPLETE";               // content no longer matches its checksum
        lines[41] = Compat.DumpsCompact(obj);
        File.WriteAllText(Partial(outPath), string.Join("\n", lines) + "\n");
        var (rc, err) = RunMain(outPath);
        Assert.True(rc == 0, err);
        Assert.Contains("resumed (40 cases kept", err, StringComparison.Ordinal);                  // header + 40 good lines before the damaged one
        Assert.Equal(cleanSha, Sha(outPath));                                                      // the tampered result must not reach output.csv
    }

    [DatasetFact]
    public void ACheckpointForOtherSettingsIsDiscardedNotMixedIn()
    {
        Prepare();
        var outPath = WorkDir("settings");
        RunAndKill(outPath, 200);
        var (rc, err) = RunMain(outPath, "--set", "discovery.phase_margin=3.0");
        Assert.True(rc == 0, err);
        Assert.Contains("discarded: the checkpoint was made for other inputs, versions or settings", err, StringComparison.Ordinal);
        Assert.Contains($"0 of {total} cases reused", err, StringComparison.Ordinal);
        var reference = WorkDir("settings_ref");
        Assert.Equal(0, RunMain(reference, "--set", "discovery.phase_margin=3.0").Code);
        Assert.Equal(Sha(reference), Sha(outPath));                                                // same as a clean run with those settings
    }

    [DatasetFact]
    public void RestartIgnoresTheCheckpointAndKeepCheckpointKeepsIt()
    {
        Prepare();
        var outPath = WorkDir("restart");
        RunAndKill(outPath, 150);
        var (rc, err) = RunMain(outPath, "--restart", "--keep-checkpoint");
        Assert.True(rc == 0, err);
        Assert.Contains("restart requested", err, StringComparison.Ordinal);
        Assert.Contains($"0 of {total} cases reused", err, StringComparison.Ordinal);
        Assert.Equal(1 + total, PartialLines(outPath).Length);
        (rc, err) = RunMain(outPath);                                                              // everything is in the kept checkpoint: nothing is audited
        Assert.True(rc == 0, err);
        Assert.Contains($"{total} of {total} cases reused, 0 audited", err, StringComparison.Ordinal);
        Assert.Equal(cleanSha, Sha(outPath));
    }
}

public class ResumeSkipsFinishedCasesTests
{
    [DatasetFact]
    public void FinishedCasesAreNotAuditedAgain()
    {
        var a = new CaseAuditor(Repo.Dataset, new AuditConfig());
        var order = a.Requests.Keys.OrderBy(c => c.Length).ThenBy(c => c, StringComparer.Ordinal).ToList();
        using var d = new TempDir();
        var path = d.File("p.jsonl");
        using (var first = new Checkpoint(path, "key1"))
            foreach (var cid in order.Take(100)) first.Add(a.AuditCase(cid));
        using var second = new Checkpoint(path, "key1");
        Assert.Equal(100, second.Done.Count);
        var (results, reused) = Checkpoint.AuditAll(a, order, second);
        Assert.Equal(100, reused);
        Assert.Equal(order, results.Select(r => r.CaseId));
        foreach (var r in results.Take(100))
        {
            var fresh = a.AuditCase(r.CaseId);                                                     // what was stored equals what a fresh audit gives
            Assert.Equal((fresh.Status, OutputIo.DumpsSteps(fresh.Steps), string.Join("\n", fresh.Notes)), (r.Status, OutputIo.DumpsSteps(r.Steps), string.Join("\n", r.Notes)));
        }
    }
}

public class RunKeyTests : IDisposable
{
    private readonly TempDir tmp = new();
    private readonly VersionStamp stamp;

    public RunKeyTests()
    {
        tmp.Write("deletion_requests.csv", "case_id\ncase_1\n");
        tmp.Write("deletion_rules.csv", "a,b\n1,2\n");
        tmp.Write("evidence/e.txt", "evidence");
        stamp = Versions.Stamp(tmp.Path, SettingsFile.Load(env: _ => null).Fingerprint());
    }

    public void Dispose() => tmp.Dispose();

    private string Key(string requestsFile = "deletion_requests.csv", VersionStamp? st = null, bool imagesOn = false, string imageObs = "") =>
        Checkpoint.RunKey(tmp.Path, requestsFile, st ?? stamp, imagesOn, imageObs);

    [Fact]
    public void SameInputsSameKey() => Assert.Equal(Key(), Key());

    [Fact]
    public void EveryIngredientChangesTheKey()
    {
        var baseKey = Key();
        var changed = new Dictionary<string, string>
        {
            ["requests file"] = Key("sample_deletion_requests.csv"),
            ["pipeline version"] = Key(st: stamp with { Pipeline = "other" }),
            ["rules digest"] = Key(st: stamp with { Rules = "other" }),
            ["prompt digest"] = Key(st: stamp with { Prompts = new Dictionary<string, PromptStamp> { ["image_reading"] = new("v2", "x") } }),
            ["code digest"] = Key(st: stamp with { Code = "other" }),
            ["settings"] = Key(st: stamp with { Settings = SettingsFile.Apply(new Settings(), "pipeline.discovery", "false").Fingerprint() }),
            ["images on"] = Key(imagesOn: true),
            ["image readings"] = Key(imagesOn: true, imageObs: "abc"),
        };
        foreach (var (what, k) in changed) Assert.True(k != baseKey, what);
        Assert.Equal(changed.Count, changed.Values.Distinct().Count());                          // no two different changes collide
    }

    [Fact]
    public void AnyDatasetFileChangeChangesTheKey()
    {
        var baseKey = Key();
        tmp.Write("evidence/e.txt", "evidence!");
        Assert.NotEqual(baseKey, Key());
        tmp.Write("evidence/e.txt", "evidence");
        Assert.Equal(baseKey, Key());
        tmp.Write("evidence/new.txt", "x");
        Assert.NotEqual(baseKey, Key());
    }

    [Fact]
    public void CheckpointWithWrongKeyOrFormatIsDiscarded()
    {
        var path = tmp.File("p.jsonl");
        using (var c = new Checkpoint(path, "k1")) c.Add(new CaseResult("case_1", "COMPLETE", new(), new(), new(), false));
        using (var again = new Checkpoint(path, "k1")) Assert.Equal("resumed (1 cases kept)", again.How);
        using (var other = new Checkpoint(path, "k2"))
        {
            Assert.StartsWith("discarded", other.How, StringComparison.Ordinal);
            Assert.Empty(other.Done);
        }
        Assert.Single(File.ReadAllLines(path));                                                  // the old cases are gone, only the new header remains
        File.WriteAllText(path, "not a checkpoint\n");
        using var bad = new Checkpoint(path, "k1");
        Assert.Equal("discarded: header unreadable", bad.How);
    }

    [Fact]
    public void RoundTripKeepsEveryField()
    {
        var r = new CaseResult("case_7", "INCOMPLETE", new() { new Step(1, "FINDING", "s", "r_1", "REPORT_REMAINING_RECORD", "PRESENT", "e") }, new() { "a note" },
                               new() { ("account_1", "evidence_02", "2026-09-17T12:00:00Z") }, true);
        var back = Checkpoint.FromPayload((Dictionary<string, object?>)Compat.ParseJson(Compat.DumpsCompact(Checkpoint.ToPayload(r)))!);
        Assert.Equal(r.CaseId, back.CaseId);
        Assert.Equal(r.Status, back.Status);
        Assert.Equal(r.Steps, back.Steps);
        Assert.Equal(r.Notes, back.Notes);
        Assert.Equal(r.LaterLinks, back.LaterLinks);
        Assert.Equal(r.Provisional, back.Provisional);
    }
}
