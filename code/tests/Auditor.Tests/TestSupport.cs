global using Auditor.Evidence;
global using Auditor.Agent;
global using Auditor.Tools;
using System.Text;

namespace Auditor.Tests;

/// <summary>Paths and helpers shared by the tests. Tests that need the real dataset skip themselves when <c>dataset/</c> is absent (it is deliberately not shipped in code.zip).</summary>
public static class Repo
{
    public static readonly string Root = FindRoot();
    public static string Dataset => Path.Combine(Root, "dataset");
    public static string Code => Path.Combine(Root, "code");
    public static bool HasDataset => File.Exists(Path.Combine(Dataset, "sample_output.csv"));

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) || Directory.Exists(Path.Combine(dir.FullName, "code", "src"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }

    public static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative), new UTF8Encoding(false));
}

/// <summary>A fact that skips itself when the real dataset is not present.</summary>
public sealed class DatasetFactAttribute : FactAttribute
{
    public DatasetFactAttribute() { if (!Repo.HasDataset) Skip = "dataset/ not present"; }
}

/// <summary>A scratch directory that is removed when disposed.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "auditor-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public string Write(string name, string text)
    {
        var p = File(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, text, new UTF8Encoding(false));
        return p;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public static class Run
{
    /// <summary>Runs the command line in-process; returns (exit code, stdout, stderr).</summary>
    public static (int Code, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var code = Auditor.Cli.Cli.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }
}

/// <summary>Sets environment variables for the lifetime of the object and restores them afterwards (tests run one after another, see xunit.runner.json).</summary>
public sealed class EnvScope : IDisposable
{
    private readonly Dictionary<string, string?> old = new();

    public EnvScope Set(string name, string? value)
    {
        if (!old.ContainsKey(name)) old[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    /// <summary>No price file, no API key: the state of a machine that has never been configured.</summary>
    public static EnvScope Clean() => new EnvScope().Set("ANTHROPIC_API_KEY", null).Set("ANTHROPIC_AUTH_TOKEN", null).Set("AUDITOR_PRICES_FILE", "/nonexistent/prices.json")
        .Set("AUDITOR_USAGE_LOG", null).Set("AUDITOR_CACHE_DIR", null).Set("AUDITOR_VLM_MODEL", null).Set("AUDITOR_VLM_FALLBACK_MODEL", null);

    public void Dispose() { foreach (var (k, v) in old) Environment.SetEnvironmentVariable(k, v); }
}

/// <summary>A model client that plays back a script: each entry is a <see cref="ModelResponse"/> to return or an exception to throw. Every request is recorded.</summary>
public sealed class FakeModelClient : IModelClient
{
    private readonly Queue<object> script;
    private readonly object gate = new();
    public List<ModelRequest> Calls { get; } = new();

    public FakeModelClient(IEnumerable<object> script) => this.script = new Queue<object>(script);

    public ModelResponse Create(ModelRequest request, TimeSpan timeout)
    {
        object next;
        lock (gate)
        {
            Calls.Add(request);
            if (script.Count == 0) throw new InvalidOperationException("the fake model client ran out of scripted replies");
            next = script.Dequeue();
        }
        return next is Exception e ? throw e : (ModelResponse)next;
    }

    public static ModelResponse Reply(object jsonOrText, long input = 100, long output = 50, string stop = "end_turn") =>
        new(stop, new ContentBlock[] { new TextBlock(jsonOrText as string ?? Py.DumpsCompact(jsonOrText)) }, new ModelUsage(input, output, 0));

    public static ModelResponse Empty(long input = 100, long output = 20) => new("end_turn", Array.Empty<ContentBlock>(), new ModelUsage(input, output, 0));

    public static ModelApiException Status(int code, double retryAfter = 0) => new($"HTTP {code}", code, "Other", retryAfter);
}

public static class Samples
{
    /// <summary>A reading the single-shot image reader would produce for a connector console.</summary>
    public static Dictionary<string, object?> Good() => (Dictionary<string, object?>)Py.ParseJson(
        "{\"screen\": \"connector console\", \"records\": [" +
        "{\"resource_id\": \"data_flow_1121\", \"selected\": true, \"emails\": [\"aditi.example@example.test\"], \"user_ids\": [], \"phones\": [], \"status_words\": [\"Healthy\", \"Continuous\"], \"shows_direct_identifiers\": true, \"aliases\": []}," +
        "{\"resource_id\": \"Order notifications\", \"selected\": false, \"emails\": [], \"user_ids\": [], \"phones\": [], \"status_words\": [], \"shows_direct_identifiers\": false, \"aliases\": []}]}")!;

    public static readonly byte[] PngBytes = { 0x89, (byte)'P', (byte)'N', (byte)'G', (byte)' ', (byte)'f', (byte)'a', (byte)'k', (byte)'e' };
}
