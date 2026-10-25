using System.Text;

namespace Auditor.Tests;

/// <summary>Bounded concurrent image reads: success, 429, timeout, invalid JSON, with a fake client (no network, no key).</summary>
public sealed class ImageClient : IModelClient
{
    private readonly object gate = new();
    private readonly Dictionary<int, Queue<object>> script;
    private readonly Dictionary<int, int> delayMs;
    private readonly Barrier? barrier;
    private int inFlight, firstWave;
    public int MaxInFlight { get; private set; }
    public List<(int Image, string Model, TimeSpan Timeout)> Calls { get; } = new();

    /// <param name="script">image number → outcomes of the calls about that image (an exception is thrown, a string is the reply text); once used up the image gets its normal reading</param>
    public ImageClient(Dictionary<int, object[]>? script = null, Dictionary<int, int>? delayMs = null, Barrier? barrier = null)
    {
        this.script = (script ?? new()).ToDictionary(kv => kv.Key, kv => new Queue<object>(kv.Value));
        this.delayMs = delayMs ?? new();
        this.barrier = barrier;
    }

    public static Dictionary<string, object?> ReadingFor(int k) => (Dictionary<string, object?>)Compat.ParseJson(
        $"{{\"screen\": \"console {k}\", \"records\": [{{\"resource_id\": \"data_flow_{1000 + k}\", \"selected\": true, \"emails\": [], \"user_ids\": [], \"phones\": [], \"status_words\": [\"Healthy\", \"Continuous\"], \"shows_direct_identifiers\": false, \"aliases\": []}}]}}")!;

    public ModelResponse Create(ModelRequest request, TimeSpan timeout)
    {
        var data = Encoding.UTF8.GetString(Convert.FromBase64String(request.Messages[0].Content.OfType<ImageBlock>().First().Base64Data));
        int k = int.Parse(data["IMG".Length..], System.Globalization.CultureInfo.InvariantCulture);
        object? outcome;
        bool waitHere;
        lock (gate)
        {
            inFlight++;
            MaxInFlight = Math.Max(MaxInFlight, inFlight);
            Calls.Add((k, request.Model, timeout));
            firstWave++;
            waitHere = barrier is not null && firstWave <= barrier.ParticipantCount;
            outcome = script.TryGetValue(k, out var q) && q.Count > 0 ? q.Dequeue() : null;
        }
        try
        {
            if (waitHere) Assert.True(barrier!.SignalAndWait(10000), "the barrier needs `parties` calls in flight together");
            if (delayMs.TryGetValue(k, out var d)) Thread.Sleep(d);
            if (outcome is Exception e) throw e;
            return FakeModelClient.Reply(outcome ?? ReadingFor(k), 10, 5);
        }
        finally { lock (gate) inFlight--; }
    }

    public int CallsFor(int k) { lock (gate) return Calls.Count(c => c.Image == k); }
}

public class ConcurrentImageReadsTests : IDisposable
{
    private const int N = 8;
    private readonly TempDir tmp = new();
    private readonly EnvScope env = EnvScope.Clean();
    private readonly List<double> sleeps = new();
    private string cache;

    public ConcurrentImageReadsTests()
    {
        cache = tmp.File("cache");
        Directory.CreateDirectory(tmp.File("evidence"));
        var lines = new List<string> { "file_id,file_path,source_origin,captured_at" };
        for (int k = 0; k < N; k++)
        {
            File.WriteAllBytes(tmp.File($"evidence/shot_{k}.png"), Encoding.ASCII.GetBytes($"IMG{k}"));
            lines.Add($"evidence_{k:D2},evidence/shot_{k}.png,screen,2026-09-01T10:{k:D2}:00Z");
        }
        File.WriteAllText(tmp.File("evidence_files.csv"), string.Join("\n", lines) + "\n");
    }

    public void Dispose() { env.Dispose(); tmp.Dispose(); }

    private Llm MakeLlm(IModelClient client, double timeoutS = 60.0) =>
        new(cacheDir: cache, usagePath: tmp.File("usage.jsonl"), client: client, sleep: s => { lock (sleeps) sleeps.Add(s); }, clock: () => 100.0, timeoutS: timeoutS);        // a frozen clock: a pause that was set stays active

    private EvidenceBase LoadEvidence(Llm llm, int workers) => Load.LoadEvidence(tmp.Path, true, new SingleShotImageReader(llm), workers);

    private static (List<(string, string, string)> Obs, List<string> Warnings) Shape(EvidenceBase b) => (b.Obs.Select(o => (o.FileId, o.Resource, o.State)).ToList(), b.Warnings.ToList());

    private static ModelApiException RateLimit(double retryAfter = 0) => new("429", 429, "RateLimit", retryAfter);
    private static ModelApiException Timeout() => new("timed out", null, "Timeout");

    // ---------------------------------------------------------------- success, bound, stable order
    [Fact]
    public void SuccessIsBoundedAndReallyConcurrent()
    {
        var client = new ImageClient(barrier: new Barrier(3));
        var b = LoadEvidence(MakeLlm(client), 3);
        Assert.Equal(N, b.Obs.Count);
        Assert.Equal(3, client.MaxInFlight);                    // three calls were in flight together (the barrier needs that) and never more than the bound
    }

    [Fact]
    public void BoundOneIsSequential()
    {
        var client = new ImageClient();
        LoadEvidence(MakeLlm(client), 1);
        Assert.Equal(1, client.MaxInFlight);
        Assert.Equal(Enumerable.Range(0, N), client.Calls.Select(c => c.Image));
    }

    [Fact]
    public void OutputOrderIsTheManifestOrderWhateverOrderRepliesArriveIn()
    {
        var sequential = Shape(LoadEvidence(MakeLlm(new ImageClient()), 1));
        cache = tmp.File("cache2");                              // a cold cache again: the replies must really arrive concurrently
        var client = new ImageClient(delayMs: Enumerable.Range(0, N).ToDictionary(k => k, k => (N - k) * 30));        // the LAST image answers first
        var concurrent = Shape(LoadEvidence(MakeLlm(client), N));
        Assert.True(client.MaxInFlight > 1);
        Assert.Equal(sequential.Obs, concurrent.Obs);
        Assert.Equal(sequential.Warnings, concurrent.Warnings);
        Assert.Equal(Enumerable.Range(0, N).Select(k => $"evidence_{k:D2}"), concurrent.Obs.Select(o => o.Item1));
    }

    [Fact]
    public void UsageLogHasOneCompleteRowPerImage()
    {
        LoadEvidence(MakeLlm(new ImageClient(delayMs: Enumerable.Range(0, N).ToDictionary(k => k, _ => 10))), 4);
        var rows = File.ReadAllLines(tmp.File("usage.jsonl")).Select(l => Compat.AsDict(Compat.ParseJson(l))).ToList();          // every line is whole JSON
        Assert.Equal(Enumerable.Range(0, N).Select(k => $"shot_{k}.png").OrderBy(x => x, StringComparer.Ordinal), rows.Select(r => (string)r["file"]!).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(rows, r => { Assert.Equal(10L, r["input_tokens"]); Assert.False((bool)r["cache_hit"]!); });
    }

    [Fact]
    public void TheSameImageTwiceAtOnceCostsOneCall()
    {
        File.WriteAllBytes(tmp.File("evidence/shot_7.png"), Encoding.ASCII.GetBytes("IMG3"));                         // file 7 is a copy of file 3
        var client = new ImageClient(delayMs: new() { [3] = 100 });
        var b = LoadEvidence(MakeLlm(client), N);
        Assert.Equal(1, client.CallsFor(3));
        Assert.Equal(N, b.Obs.Count);
    }

    // ---------------------------------------------------------------- 429
    [Fact]
    public void A429IsRetriedWithBackoffAndTheImageIsStillRead()
    {
        var client = new ImageClient(script: new() { [2] = new object[] { RateLimit(), RateLimit() } });
        var b = LoadEvidence(MakeLlm(client), 1);
        Assert.Equal(N, b.Obs.Count);
        Assert.Empty(b.Warnings);
        Assert.Equal(new[] { 1.0, 2.0 }, sleeps.Take(2));                                           // backoff doubles
        Assert.True(sleeps.Skip(2).All(s => s == 2.0));                                              // with a frozen clock the later images wait out the same shared pause, and nothing else
    }

    [Fact]
    public void RetryAfterLongerThanTheBackoffWinsAndIsCapped()
    {
        var client = new ImageClient(script: new() { [0] = new object[] { RateLimit(7) }, [1] = new object[] { RateLimit(9999) } });
        LoadEvidence(MakeLlm(client), 1);
        Assert.Equal(7.0, sleeps[0]);
        Assert.Contains(60.0, sleeps);                                                                // MaxBackoffS
    }

    [Fact]
    public void One429PausesTheNextCallToo()
    {
        // the pause is shared: image 1's call, made after image 0 was rate limited, sits out the remaining pause first
        var client = new ImageClient(script: new() { [0] = new object[] { RateLimit(5) } });
        LoadEvidence(MakeLlm(client), 1);
        Assert.Equal(5.0, sleeps[0]);
        Assert.Equal(1 + (N - 1), sleeps.Count);                  // the rate-limited call retries without a second wait; each of the other 7 images sits out the shared pause once
        Assert.True(sleeps.All(s => s == 5.0));
    }

    [Fact]
    public void A429ThatNeverClearsGivesUpForThatImageOnly()
    {
        var client = new ImageClient(script: new() { [4] = Enumerable.Repeat((object)RateLimit(), 4).ToArray() });
        var b = LoadEvidence(MakeLlm(client), 3);
        Assert.Equal(N - 1, b.Obs.Count);
        var w = Assert.Single(b.Warnings);
        Assert.Contains("evidence_04", w, StringComparison.Ordinal);
        Assert.Contains("LLMUnavailable", w, StringComparison.Ordinal);
        Assert.Contains("RateLimit", w, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- timeout
    [Fact]
    public void TimeoutIsRetriedAndTheRequestCarriesTheTimeout()
    {
        var client = new ImageClient(script: new() { [1] = new object[] { Timeout() } });
        var b = LoadEvidence(MakeLlm(client, timeoutS: 12.5), 2);
        Assert.Equal(N, b.Obs.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(12.5) }, client.Calls.Select(c => c.Timeout).Distinct());
        Assert.Equal(2, client.CallsFor(1));
        Assert.Equal(new[] { 1.0 }, sleeps);                                                         // a timeout backs off only the call that timed out
    }

    [Fact]
    public void TimeoutEveryTimeIsAWarningNotACrash()
    {
        var client = new ImageClient(script: new() { [6] = Enumerable.Repeat((object)Timeout(), 4).ToArray() });
        var b = LoadEvidence(MakeLlm(client), 4);
        Assert.Equal(N - 1, b.Obs.Count);
        Assert.Contains(b.Warnings, w => w.Contains("evidence_06", StringComparison.Ordinal) && w.Contains("Timeout", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- invalid JSON
    [Fact]
    public void InvalidJsonGetsOneReaskAndSucceeds()
    {
        var client = new ImageClient(script: new() { [5] = new object[] { "this is not json" } });
        var b = LoadEvidence(MakeLlm(client), 4);
        Assert.Equal(N, b.Obs.Count);
        Assert.Equal(2, client.CallsFor(5));
        var rows = File.ReadAllLines(tmp.File("usage.jsonl")).Select(l => Compat.AsDict(Compat.ParseJson(l))).ToList();
        Assert.Equal(2L, rows.First(r => (string)r["file"]! == "shot_5.png")["calls"]);
    }

    [Fact]
    public void InvalidJsonFromBothModelsIsAWarningAndOthersAreUnaffected()
    {
        var client = new ImageClient(script: new() { [3] = Enumerable.Repeat((object)"nope", 4).ToArray() });
        var b = LoadEvidence(MakeLlm(client), 4);
        Assert.Equal(Enumerable.Range(0, N).Where(k => k != 3).Select(k => $"evidence_{k:D2}"), Shape(b).Obs.Select(o => o.Item1));
        Assert.Contains(b.Warnings, w => w.Contains("evidence_03", StringComparison.Ordinal) && w.Contains("SchemaError", StringComparison.Ordinal));
        Assert.Equal(new[] { "claude-haiku-5-5", "claude-haiku-5-5", "claude-sonnet-5-5", "claude-sonnet-5-5" }, client.Calls.Where(c => c.Image == 3).Select(c => c.Model));
    }

    [Fact]
    public void AMixOfAllFailuresGivesTheSameOutputSequentiallyAndConcurrently()
    {
        Dictionary<int, object[]> Script() => new()
        {
            [0] = new object[] { RateLimit() }, [2] = new object[] { Timeout() }, [3] = new object[] { "bad" },
            [5] = Enumerable.Repeat((object)RateLimit(), 4).ToArray(), [6] = Enumerable.Repeat((object)"x", 4).ToArray(),
        };
        var seq = Shape(LoadEvidence(MakeLlm(new ImageClient(Script())), 1));
        cache = tmp.File("cache2");
        var con = Shape(LoadEvidence(MakeLlm(new ImageClient(Script(), Enumerable.Range(0, N).ToDictionary(k => k, k => 10 * (N - k)))), 5));
        Assert.Equal(seq.Obs, con.Obs);
        Assert.Equal(seq.Warnings, con.Warnings);
        Assert.Equal(N - 2, seq.Obs.Count);
        Assert.Equal(2, seq.Warnings.Count);
    }
}

public class PrefetchTests
{
    [Fact]
    public void ExceptionsAreReplayedInOrderAndWorkersAreClamped()
    {
        var seen = new List<string>();
        var inner = new FuncImageReader((p, _, _, _, _) =>
        {
            lock (seen) seen.Add(Path.GetFileName(p));
            return Path.GetFileName(p) == "b" ? throw new InvalidOperationException("boom") : new Dictionary<string, object?> { ["p"] = Path.GetFileName(p) };
        });
        var replay = ImageJobs.Prefetch(inner, new[] { "a", "b", "c" }, "s", "i", new(), "v", workers: 99);
        Assert.Equal(new[] { "a", "b", "c" }, seen.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("a", replay.Read("a", "s", "i", new(), "v")["p"]);
        Assert.Throws<InvalidOperationException>(() => replay.Read("b", "s", "i", new(), "v"));
        Assert.Equal(3, seen.Count);                                                                  // replaying does not call the model again
    }

    [Fact]
    public void OneWorkerOrOneImageReturnsThePlainReader()
    {
        var f = new FuncImageReader((_, _, _, _, _) => new());
        Assert.Same(f, ImageJobs.Prefetch(f, new[] { "a", "b" }, "s", "i", new(), "v", 1));
        Assert.Same(f, ImageJobs.Prefetch(f, new[] { "a" }, "s", "i", new(), "v", 8));
    }
}

public class SettingsForConcurrencyTests
{
    [Fact]
    public void BoundsAndThatTheyDoNotChangeTheFingerprint()
    {
        var b = new Settings();
        Assert.Equal((4, 60.0), (b.MaxConcurrency, b.TimeoutSeconds));
        foreach (var bad in new[] { "0", "17", "x" }) Assert.Throws<SettingsException>(() => SettingsFile.Apply(b, "llm.max_concurrency", bad));
        Assert.Throws<SettingsException>(() => SettingsFile.Apply(b, "llm.timeout_seconds", "0"));
        Assert.Equal(b.Fingerprint(), SettingsFile.Apply(SettingsFile.Apply(b, "llm.max_concurrency", "9"), "llm.timeout_seconds", "5").Fingerprint());
    }

    [Fact]
    public void ConfigCarriesTheWorkerCount() =>
        Assert.Equal(7, AuditConfig.FromSettings(SettingsFile.Apply(new Settings(), "llm.max_concurrency", "7")).ImageWorkers);
}
