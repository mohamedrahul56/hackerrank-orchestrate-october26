
namespace Auditor.Tests;

/// <summary>The agent layer through the command line: opt-in, no change without a key, settings, and a scripted replay of the 11 screenshot readings through the full loop.</summary>
public class AgentOptInAndNoChangeTests : IDisposable
{
    private static readonly string Submitted = Path.Combine(Repo.Root, "output.csv");
    private readonly EnvScope env = EnvScope.Clean();
    private readonly TempDir dir = new();
    public void Dispose() { env.Dispose(); dir.Dispose(); }

    [Fact]
    public void DefaultIsUnchangedAndAgentIsNotTheDefault()
    {
        Assert.Equal("auto", new Settings().Images);
        Assert.NotEqual("agent", SettingsFile.Load(env: _ => null).Images);
    }

    [DatasetFact]
    public void AgentModeWithoutAKeyReproducesTheSubmittedOutputByteForByte()
    {
        var outPath = dir.File("o.csv");
        var (rc, _, err) = Run.Cli("--images", "agent", "--out", outPath);
        Assert.True(rc == 0, err);
        Assert.Equal(File.ReadAllBytes(Submitted), File.ReadAllBytes(outPath));
        var notes = File.ReadAllText(Path.ChangeExtension(outPath, ".notes.md"));
        Assert.Contains("Agent layer", notes, StringComparison.Ordinal);
        Assert.Contains("not read", notes, StringComparison.Ordinal);
        var rows = File.ReadAllLines(Path.ChangeExtension(outPath, ".usage.jsonl")).Select(l => Compat.AsDict(Compat.ParseJson(l))).ToList();
        var info = rows.First(r => Compat.Get(r, "kind") as string == "run_info");
        Assert.Equal((true, 0L, 0L), ((bool)info["images_read_by_agent"]!, (long)info["agent_screenshots_read"]!, (long)info["agent_screenshots_failed"]!));
        Assert.DoesNotContain(rows, r => Compat.Get(r, "kind") as string == "agent_image");        // no key: no call, no tokens, no usage row
    }

    [DatasetFact]
    public void TheDefaultRunIsTheSameBytes()
    {
        var outPath = dir.File("o.csv");
        Assert.Equal(0, Run.Cli("--out", outPath).Code);
        Assert.Equal(File.ReadAllBytes(Submitted), File.ReadAllBytes(outPath));
    }
}

public class AgentSettingsTests
{
    [Fact]
    public void ImagesAgentAndTheLimitsAreValidSettings()
    {
        var s = SettingsFile.Apply(new Settings(), "llm.images", "agent");
        Assert.Equal("agent", s.Images);
        s = SettingsFile.Apply(s, "agent.max_zooms", "2");
        Assert.Equal(2, s.AgentMaxZooms);
        foreach (var (dotted, bad) in new[] { ("agent.max_iterations", "0"), ("agent.max_iterations", "99"), ("agent.wall_seconds", "1"), ("agent.zoom_max_px", "10"), ("agent.max_zooms", "-1") })
            Assert.Throws<SettingsException>(() => SettingsFile.Apply(new Settings(), dotted, bad));
    }

    [Fact]
    public void TheLoopLimitsAreInTheFingerprintOnlyWhenTheLoopRuns()
    {
        var b = new Settings();
        Assert.Equal(b.Fingerprint(), SettingsFile.Apply(b, "agent.max_zooms", "1").Fingerprint());
        var a = SettingsFile.Apply(b, "llm.images", "agent");
        Assert.NotEqual(a.Fingerprint(), SettingsFile.Apply(a, "agent.max_zooms", "1").Fingerprint());
    }

    [Fact]
    public void TheShippedFileDocumentsEveryAgentKey()
    {
        Assert.Contains("[agent]", File.ReadAllText(SettingsFile.DefaultFile), StringComparison.Ordinal);
        Assert.Equal(new Settings(), SettingsFile.FromFile(SettingsFile.DefaultFile));
    }
}

/// <summary>The loop and the gate must not change what a reading says: replaying the 11 readings through them equals the readings-file run.</summary>
public class AgentScriptedReplayTests : IDisposable
{
    private static readonly string Local = Path.Combine(Repo.Code, "out", "agent_readings.json");
    private readonly EnvScope env = EnvScope.Clean();
    private readonly TempDir dir = new();
    public void Dispose() { env.Dispose(); dir.Dispose(); }

    [Fact]
    public void ReplayThroughTheLoopEqualsTheReadingsFileRun()
    {
        if (!File.Exists(Local) || !Repo.HasDataset) return;          // the local readings file is git-ignored (it holds dataset e-mail addresses): the replay needs it
        var readings = Compat.AsDict(Compat.AsDict(Compat.ParseJson(File.ReadAllText(Local)))["readings"]);
        var client = new ScriptedClient(readings);
        string fromFile = dir.File("file.csv"), viaAgent = dir.File("agent.csv");
        Assert.Equal(0, Run.Cli("--images", "file", "--out", fromFile).Code);
        Auditor.Cli.Cli.ModelClientOverride.Value = client;
        int rc; string err;
        try { (rc, _, err) = Run.Cli("--images", "agent", "--out", viaAgent, "--set", "llm.cache_dir=" + dir.File("cache")); }
        finally { Auditor.Cli.Cli.ModelClientOverride.Value = null; }
        Assert.True(rc == 0, err);
        Assert.Equal(File.ReadAllBytes(fromFile), File.ReadAllBytes(viaAgent));                       // the agent loop and gate changed an answer
        Assert.Equal(22, client.Calls);                                                              // 11 screenshots x (one zoom + one submit)
        var rows = File.ReadAllLines(Path.ChangeExtension(viaAgent, ".usage.jsonl")).Select(l => Compat.AsDict(Compat.ParseJson(l))).ToList();
        var agentRows = rows.Where(r => Compat.Get(r, "kind") as string == "agent_image").ToList();
        Assert.Equal((11, 11L, 22L), (agentRows.Count, agentRows.Sum(r => (long)r["zooms"]!), agentRows.Sum(r => (long)r["tool_calls"]!)));
        var notes = File.ReadAllText(Path.ChangeExtension(viaAgent, ".notes.md"));
        Assert.Contains("screenshots read: 11", notes, StringComparison.Ordinal);
        Assert.DoesNotContain("G10", notes, StringComparison.Ordinal);                               // no model reading contradicted deterministic evidence on the real data
        var gateLines = notes.Split('\n').Where(l => l.StartsWith("* evidence_", StringComparison.Ordinal) && l.Contains(" G", StringComparison.Ordinal)).ToList();
        var line = Assert.Single(gateLines);
        Assert.Contains("evidence_42: G3 record 0: no resource id and no alias", line, StringComparison.Ordinal);        // the registry screenshot that shows no id: nothing to record, as for the file source
    }
}
