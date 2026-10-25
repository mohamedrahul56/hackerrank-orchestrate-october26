using System.Text.RegularExpressions;

namespace Auditor.Tests;

/// <summary>Every version that goes into the cache key must make a stored reading unusable when it changes.</summary>
public class CacheInvalidationTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly EnvScope env = EnvScope.Clean();
    private readonly string img;
    private readonly FakeModelClient fake = new(Enumerable.Range(0, 12).Select(_ => (object)FakeModelClient.Reply(Samples.Good())));

    public CacheInvalidationTests()
    {
        img = dir.File("shot.png");
        File.WriteAllBytes(img, Samples.PngBytes);
    }

    public void Dispose() { env.Dispose(); dir.Dispose(); }

    private static VersionStamp Stamp(string pipeline, string rules) => new(pipeline, rules, new Dictionary<string, PromptStamp>(), "", "");

    private Llm MakeLlm(VersionStamp? stamp = null, string? model = null) =>
        new(cacheDir: dir.File("cache"), usagePath: dir.File("u.jsonl"), client: fake, sleep: _ => { }, stamp: stamp, model: model);

    private int CallsAfter(Llm llm, string? system = null, string? instruction = null, Dictionary<string, object?>? schema = null, string? version = null)
    {
        int before = fake.Calls.Count;
        llm.ReadImageJson("image", img, system ?? ImageObs.System, instruction ?? ImageObs.Instruction, schema ?? ImageObs.Schema, version ?? ImageObs.PromptVersion);
        return fake.Calls.Count - before;
    }

    [Fact]
    public void SameVersionsHitTheCache()
    {
        var stamp = Stamp("p1", "r1");
        Assert.Equal(1, CallsAfter(MakeLlm(stamp)));
        Assert.Equal(0, CallsAfter(MakeLlm(stamp)));               // a second Llm object with the same versions must reuse the stored reading
    }

    [Fact]
    public void APipelineVersionChangeInvalidates()
    {
        Assert.Equal(1, CallsAfter(MakeLlm(Stamp("p1", "r1"))));
        Assert.Equal(1, CallsAfter(MakeLlm(Stamp("p2", "r1"))));
        Assert.Equal(0, CallsAfter(MakeLlm(Stamp("p1", "r1"))));  // the old entry is still valid for the old version
    }

    [Fact]
    public void ARulesFileChangeInvalidates()
    {
        Assert.Equal(1, CallsAfter(MakeLlm(Stamp("p1", "r1"))));
        Assert.Equal(1, CallsAfter(MakeLlm(Stamp("p1", "r2"))));
    }

    [Fact]
    public void APromptLabelChangeInvalidates()
    {
        var llm = MakeLlm(Stamp("p1", "r1"));
        Assert.Equal(1, CallsAfter(llm));
        Assert.Equal(1, CallsAfter(llm, version: "img-v2"));
    }

    [Fact]
    public void APromptTextChangeInvalidatesEvenWhenTheLabelIsNotBumped()
    {
        var llm = MakeLlm(Stamp("p1", "r1"));
        Assert.Equal(1, CallsAfter(llm));
        Assert.Equal(1, CallsAfter(llm, instruction: ImageObs.Instruction + " Also list tooltips."));
        Assert.Equal(1, CallsAfter(llm, system: ImageObs.System + " Be brief."));
    }

    [Fact]
    public void ASchemaOrModelChangeInvalidates()
    {
        var llm = MakeLlm(Stamp("p1", "r1"));
        Assert.Equal(1, CallsAfter(llm));
        var changed = new Dictionary<string, object?>(ImageObs.Schema) { ["description"] = "changed" };
        Assert.Equal(1, CallsAfter(llm, schema: changed));
        Assert.Equal(1, CallsAfter(MakeLlm(Stamp("p1", "r1"), model: "other-model")));
    }

    [Fact]
    public void TheDefaultStampFollowsThePipelineVersion()
    {
        Assert.Equal(1, CallsAfter(MakeLlm()));
        Assert.Equal(0, CallsAfter(MakeLlm()));
        Assert.Equal(Versions.PipelineVersion, Versions.DefaultStamp().Pipeline);
    }

    [Fact]
    public void TheCacheStampIsNotTheCodeDigest()
    {
        // editing code that cannot change what a model says about an image must not throw readings away
        var st = Versions.CacheStamp(Versions.Stamp(Repo.HasDataset ? Repo.Dataset : dir.Path, "fp"));
        Assert.Equal((Versions.PipelineVersion, "", ""), (st.Pipeline, st.Code, st.Settings));
        Assert.Empty(st.Prompts);
    }
}

public class DigestsTests
{
    [Fact]
    public void RulesDigestFollowsTheFileBytes()
    {
        using var d = new TempDir();
        Assert.Equal("missing", Versions.RulesDigest(d.Path));
        d.Write("deletion_rules.csv", "a,b\n1,2\n");
        var first = Versions.RulesDigest(d.Path);
        d.Write("deletion_rules.csv", "a,b\n1,3\n");
        Assert.NotEqual(first, Versions.RulesDigest(d.Path));
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void PromptDigestCoversTextAndSchema()
    {
        var p = Versions.Prompts()["image_reading"];
        Assert.Equal(ImageObs.PromptVersion, p.Version);
        Assert.Equal(Versions.DigestText(ImageObs.System, ImageObs.Instruction, SortedJson.Dumps(ImageObs.Schema)), p.Digest);
        Assert.NotEqual(p.Digest, Versions.DigestText(ImageObs.System, ImageObs.Instruction + "!", SortedJson.Dumps(ImageObs.Schema)));
        Assert.True(Versions.Prompts().ContainsKey("agent_image_reader"));
    }

    [Fact]
    public void CodeDigestIsStableBetweenCalls() => Assert.Equal(Versions.CodeDigest(), Versions.CodeDigest());

    [Fact]
    public void StampLinesNameEveryVersion()
    {
        var text = string.Join("\n", Versions.StampLines(Versions.Stamp("/nonexistent", "fp123")));
        foreach (var needle in new[] { Versions.PipelineVersion, "image_reading", ImageObs.PromptVersion, "rules file", "code sha256", "fp123" })
            Assert.Contains(needle, text, StringComparison.Ordinal);
    }
}

public class SettingsLayersTests
{
    private static string Write(TempDir d, string text) => d.Write("s.toml", text);

    [Fact]
    public void ShippedFileEqualsTheBuiltInDefaults()
    {
        Assert.Equal(new Settings(), SettingsFile.FromFile(SettingsFile.DefaultFile));
        Assert.Equal(new Settings(), SettingsFile.Load(env: _ => null));
    }

    [Fact]
    public void EverySettingIsDocumentedInTheShippedFile()
    {
        var lines = File.ReadAllLines(SettingsFile.DefaultFile).ToList();
        foreach (var k in SettingsFile.Keys)
        {
            int start = lines.IndexOf($"[{k.Section}]");
            Assert.True(start >= 0, k.Section);
            int idx = Enumerable.Range(start + 1, lines.Count - start - 1).First(i => Regex.IsMatch(lines[i], $@"^{k.Name}\s*="));
            Assert.True(lines[idx - 1].TrimStart().StartsWith('#'), $"{k.Dotted} has no comment line above it");
        }
    }

    [Fact]
    public void PrecedenceFileThenEnvThenCli()
    {
        using var d = new TempDir();
        var f = Write(d, "[llm]\nmodel = \"from-file\"\n[discovery]\nphase_margin = 3.5\n");
        Assert.Equal("from-file", SettingsFile.Load(f, env: _ => null).Model);
        Assert.Equal("from-env", SettingsFile.Load(f, env: n => n == "AUDITOR_VLM_MODEL" ? "from-env" : null).Model);
        var s = SettingsFile.Load(f, new[] { "llm.model=from-cli" }, n => n == "AUDITOR_VLM_MODEL" ? "from-env" : null);
        Assert.Equal(("from-cli", 3.5), (s.Model, s.Tune.PhaseMargin));
    }

    [Fact]
    public void BadInputIsAnErrorNotADefault()
    {
        using var d = new TempDir();
        foreach (var text in new[] { "[nope]\na = 1\n", "[pipeline]\ntypo = true\n", "[pipeline]\ndiscovery = \"yes\"\n", "[discovery]\nchain_gaps_seconds = [1]\n",
                                      "[discovery]\naccount_window_minutes = [10, -10]\n", "[llm]\nimages = \"maybe\"\n", "[pipeline\n" })
            Assert.Throws<SettingsException>(() => SettingsFile.FromFile(Write(d, text)));
        Assert.Throws<SettingsException>(() => SettingsFile.FromFile("/nonexistent/settings.toml"));
        foreach (var item in new[] { "pipeline.discovery", "pipeline.discovery=2", "nodot=1", "audit.later_link_max_days=abc" })
            Assert.Throws<SettingsException>(() => SettingsFile.Load(overrides: new[] { item }, env: _ => null));
    }

    [Fact]
    public void FingerprintChangesWithAnswerRelevantSettingsOnly()
    {
        var b = new Settings();
        foreach (var (dotted, value) in new[] { ("pipeline.discovery", "false"), ("discovery.phase_margin", "2.5"), ("discovery.chain_gaps_seconds", "[390, 450]"), ("audit.later_link_max_days", "3"), ("pipeline.provisional_rows", "false") })
            Assert.NotEqual(b.Fingerprint(), SettingsFile.Apply(b, dotted, value).Fingerprint());
        Assert.Equal(b.Fingerprint(), SettingsFile.Apply(b, "llm.cache_dir", "/elsewhere").Fingerprint());
        var off = SettingsFile.Apply(b, "llm.images", "off");
        Assert.Equal(off.Fingerprint(), SettingsFile.Apply(off, "llm.model", "x").Fingerprint());           // with images off the model names cannot matter
        Assert.NotEqual(b.Fingerprint(), SettingsFile.Apply(b, "llm.model", "x").Fingerprint());
    }

    [Fact]
    public void ConfigIsBuiltFromSettings()
    {
        var s = SettingsFile.Load(overrides: new[] { "pipeline.discovery=false", "discovery.phase_margin=3", "pipeline.timing_heuristics=false" }, env: _ => null);
        var cfg = AuditConfig.FromSettings(s);
        Assert.False(cfg.UseDiscovery);
        Assert.False(cfg.UseTimingHeuristics);
        Assert.Equal(3.0, cfg.Tune.PhaseMargin);
        Assert.True(cfg.AllowLaterLinking);
    }
}

public class CliStampsAndOverridesTests
{
    [DatasetFact]
    public void NotesAndUsageLogCarryTheVersionStamp()
    {
        using var d = new TempDir();
        var outPath = d.File("o.csv");
        var (rc, _, err) = Run.Cli("--requests", "sample_deletion_requests.csv", "--out", outPath, "--images", "off");
        Assert.True(rc == 0, err);
        var notes = File.ReadAllText(Path.ChangeExtension(outPath, ".notes.md"));
        Assert.Contains("## Version stamp", notes, StringComparison.Ordinal);
        Assert.Contains(Versions.PipelineVersion, notes, StringComparison.Ordinal);
        Assert.Contains(Versions.RulesDigest(Repo.Dataset), notes, StringComparison.Ordinal);
        Assert.Contains(ImageObs.PromptVersion, notes, StringComparison.Ordinal);
        Assert.Contains(SettingsFile.Apply(new Settings(), "llm.images", "off").Fingerprint(), notes, StringComparison.Ordinal);
        Assert.Contains("`pipeline.discovery=true`", notes, StringComparison.Ordinal);
        var runInfo = File.ReadAllLines(Path.ChangeExtension(outPath, ".usage.jsonl")).Where(l => l.Contains("\"run_info\"", StringComparison.Ordinal)).Select(l => Compat.AsDict(Compat.ParseJson(l))).First();
        Assert.Equal(Versions.RulesDigest(Repo.Dataset), Compat.AsDict(runInfo["stamp"])["rules"]);
    }

    [DatasetFact]
    public void SetFlagChangesTheStampAndADedicatedFlagWinsOverSet()
    {
        using var d = new TempDir();
        string a = d.File("a.csv"), b = d.File("b.csv");
        Assert.Equal(0, Run.Cli("--requests", "sample_deletion_requests.csv", "--out", a, "--images", "off").Code);
        Assert.Equal(0, Run.Cli("--requests", "sample_deletion_requests.csv", "--out", b, "--images", "off", "--set", "discovery.phase_margin=9", "--set", "pipeline.discovery=true", "--no-discovery").Code);
        var na = File.ReadAllText(Path.ChangeExtension(a, ".notes.md")); var nb = File.ReadAllText(Path.ChangeExtension(b, ".notes.md"));
        string Fp(string t) => Regex.Match(t, @"settings fingerprint: `(\w+)`").Groups[1].Value;
        Assert.NotEqual(Fp(na), Fp(nb));
        Assert.Contains("`pipeline.discovery=false`", nb, StringComparison.Ordinal);             // --no-discovery wins over --set pipeline.discovery=true
        Assert.Contains("`discovery.phase_margin=9.0`", nb, StringComparison.Ordinal);
    }

    [DatasetFact]
    public void ConfigFileAndShowSettings()
    {
        using var d = new TempDir();
        var f = d.Write("s.toml", "[discovery]\nphase_margin = 4.0\n");
        var (rc, stdout, _) = Run.Cli("--config", f, "--set", "audit.later_link_max_days=2", "--show-settings");
        Assert.Equal(0, rc);
        Assert.Contains("discovery.phase_margin = 4.0", stdout, StringComparison.Ordinal);
        Assert.Contains("audit.later_link_max_days = 2", stdout, StringComparison.Ordinal);
        Assert.Contains("settings fingerprint", stdout, StringComparison.Ordinal);
    }

    [DatasetFact]
    public void BadSettingsStopTheRunWithExit2AndWriteNothing()
    {
        using var d = new TempDir();
        var outPath = d.File("o.csv");
        var (rc, _, err) = Run.Cli("--out", outPath, "--set", "pipeline.typo=1");
        Assert.Equal(2, rc);
        Assert.Contains("unknown setting", err, StringComparison.Ordinal);
        Assert.False(File.Exists(outPath));
    }
}
