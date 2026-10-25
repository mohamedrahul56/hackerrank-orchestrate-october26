using System.Diagnostics;
using System.Security.Cryptography;
using Auditor.Evidence;

namespace Auditor.Agent;

/// <summary>The loop ended without a usable submission (reason in the message). <see cref="Trace"/> holds what the loop had already spent, so that the usage log is true even for a failure.</summary>
public sealed class AgentFailure : SchemaException
{
    public AgentFailure(string reason, AgentTrace? trace = null, Exception? inner = null) : base(reason, inner) { Trace = trace?.Copy() ?? new AgentTrace(); }

    public AgentTrace Trace { get; }
}

/// <summary>What one reading used: model requests, tool calls, zooms, tokens, time.</summary>
public sealed class AgentTrace
{
    public int ApiCalls { get; set; }
    public int ToolCalls { get; set; }
    public int Zooms { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheRead { get; set; }
    public double LatencyS { get; set; }

    public AgentTrace Copy() => (AgentTrace)MemberwiseClone();
}

/// <param name="MaxIterations">model turns per screenshot</param>
/// <param name="MaxToolCalls">tool calls per screenshot (a submit counts)</param>
/// <param name="MaxZooms">zoom_region calls per screenshot</param>
/// <param name="WallSeconds">wall-clock budget per screenshot (each request also has the llm timeout)</param>
/// <param name="ZoomMaxPx">longest side of a returned zoom, in pixels</param>
/// <param name="MaxResubmits">how often an invalid submission is sent back before the loop gives up</param>
public sealed record AgentLimits(int MaxIterations = 5, int MaxToolCalls = 6, int MaxZooms = 4, double WallSeconds = 120.0, int ZoomMaxPx = 1200, int MaxResubmits = 1);

/// <summary>
/// The bounded tool-calling loop that reads ONE screenshot (opt-in: <c>--images agent</c>).
/// Shape of one reading: the model gets the screenshot and the task, may call <c>zoom_region</c> a few times to read small text, and ends with exactly one <c>submit_reading</c>. Code, not the
/// model, runs the loop and enforces the limits:
///   max_iterations   model turns per screenshot            max_tool_calls   tool calls per screenshot (a submit counts)
///   max_zooms        zoom_region calls per screenshot      wall_seconds     wall-clock budget per screenshot (each request also has the llm timeout)
///   repeated call    the same tool with the same input a second time is answered from memory once; a third time ends the loop
///   invalid submit   one re-ask (a tool error that says what was wrong), then the loop ends
/// Transport problems (429, timeouts, 5xx) are retried with back-off inside <see cref="Llm.Send"/>; refusals, an invalid or missing submission, a cap, a repeated call or an unexpected
/// tool all end in an <see cref="AgentFailure"/>, which is a <see cref="SchemaException"/>: the screenshot is then "not read", exactly like a missing key, and the deterministic result
/// stands. A valid submission is cached (key: image bytes, both prompt files, tool definitions, model, pipeline and rules versions) BEFORE the gate, so a change in a gate rule never needs
/// a new model call. No API key, no network and no real model are involved in any test of this class: they use a fake client.
/// </summary>
public sealed class AgentImageReader : IImageReader
{
    public const string Source = "image:agent";

    private readonly Llm llm;
    private readonly Dictionary<string, string> fileIds;
    private readonly Func<double> clock;
    private readonly object gate = new();

    public AgentImageReader(Llm llm, IReadOnlyDictionary<string, string> fileIdsByPath, AgentLimits? limits = null, Func<double>? clock = null, string? promptDir = null)
    {
        this.llm = llm;
        Limits = limits ?? new AgentLimits();
        this.clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        fileIds = fileIdsByPath.ToDictionary(kv => Path.GetFullPath(kv.Key), kv => kv.Value, StringComparer.Ordinal);
        System = PromptFile.Load("image_reader.system.md", promptDir);
        Task = PromptFile.Load("image_reader.task.md", promptDir);
    }

    public string ObsSource => Source;
    public AgentLimits Limits { get; }
    public PromptFile System { get; }
    public PromptFile Task { get; }
    /// <summary>gate rejections and failures, for the run notes</summary>
    public List<string> Notes { get; } = new();
    public Dictionary<string, int> Stats { get; } = new(StringComparer.Ordinal) { ["read"] = 0, ["failed"] = 0 };

    // ------------------------------------------------------------------ public
    public Dictionary<string, object?> Read(string path, string system, string instruction, Dictionary<string, object?> schema, string promptVersion)
    {
        // the arguments are ignored: the reader uses its own versioned prompt files
        if (!fileIds.TryGetValue(Path.GetFullPath(path), out var fileId))              // gate rule G2: only screenshots of the manifest are read
            throw new LlmUnavailableException($"{Path.GetFileName(path)} is not an evidence file of the manifest");
        var data = File.ReadAllBytes(path);
        var fhash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        Dictionary<string, object?>? raw = null;
        Exception? last = null;
        foreach (var model in new[] { llm.Model, llm.FallbackModel })
        {
            var key = Key(fhash, model);
            lock (llm.KeyLock(key))                                                      // the same screenshot twice at once costs one reading
            {
                var cached = llm.CacheGet(key);
                if (cached is not null)
                {
                    llm.Usage.Add(new() { ["ts"] = Llm.Now(), ["provider"] = "anthropic", ["model"] = model, ["kind"] = "agent_image", ["file"] = Path.GetFileName(path), ["file_sha256"] = fhash,
                                          ["cache_hit"] = true, ["input_tokens"] = 0L, ["output_tokens"] = 0L, ["calls"] = 0L });
                    raw = cached;
                    break;
                }
                var trace = new AgentTrace();
                try { raw = Run(model, path, data, trace); }
                catch (LlmUnavailableException e)                                        // transport gave up or a fatal stop: what the earlier turns cost is still logged, then the error goes up
                {
                    if (trace.ApiCalls > 0)                                              // at least one turn was paid for (no key or an immediate refusal is not a failed loop)
                    {
                        var row = Row(path, fhash, model, trace);
                        row["error"] = $"model unavailable during the loop: {e.Message}";
                        llm.Usage.Add(row);
                        Count("failed");
                    }
                    throw;
                }
                catch (AgentFailure e)
                {
                    last = e;
                    var row = Row(path, fhash, model, e.Trace);
                    row["error"] = $"agent loop ended without a reading: {e.Message}";
                    llm.Usage.Add(row);
                    Count("failed");
                    continue;
                }
                llm.CachePut(key, raw);
                llm.Usage.Add(Row(path, fhash, model, trace));
                break;
            }
        }
        if (raw is null)
        {
            Note($"{fileId}: not read ({last?.Message})");
            throw new AgentFailure($"no model produced a valid reading for {Path.GetFileName(path)}: {last?.Message}");
        }
        GateResult result;
        try { result = Gate.GateReading(raw, fileId, new HashSet<string>(fileIds.Values, StringComparer.Ordinal)); }
        catch (Exception e) when (e is SchemaException or GateException)                 // a cached or fresh submission that the gate refuses is "not read"
        {
            Note($"{fileId}: not read (gate: {e.Message})");
            throw new LlmUnavailableException($"gate refused the reading of {Path.GetFileName(path)}: {e.Message}", inner: e);
        }
        foreach (var r in result.Rejections) Note($"{fileId}: {r}");
        Count("read");
        return result.Reading;
    }

    // ------------------------------------------------------------------ the loop
    /// <summary>The loop proper. <paramref name="trace"/> is filled as it goes (by the caller's object, so it survives any exception); returns the submitted reading.</summary>
    private Dictionary<string, object?> Run(string model, string path, byte[] data, AgentTrace trace)
    {
        var lim = Limits;
        int width, height;
        try { (width, height) = AgentTools.ImageSize(path); }
        catch (ToolException e) { throw new AgentFailure($"cannot read the image header: {e.Message}", trace, e); }
        var specs = AgentTools.ToolSpecs();
        bool zoomOn = specs.Any(t => t.Name == "zoom_region");
        var task = Task.Fill(new Dictionary<string, object> { ["width"] = width, ["height"] = height, ["max_zooms"] = zoomOn ? lim.MaxZooms : 0, ["id_families"] = AgentTools.IdFamilies() });
        var media = Path.GetExtension(path).ToLowerInvariant() == ".png" ? "image/png" : "image/jpeg";
        var messages = new List<ChatMessage> { new("user", new ContentBlock[] { AgentTools.ImageBlock(data, media), new TextBlock(task) }) };
        double started = clock();
        var seen = new Dictionary<string, IReadOnlyList<ContentBlock>>(StringComparer.Ordinal);
        int repeats = 0, invalid = 0;
        for (int iteration = 0; iteration < lim.MaxIterations; iteration++)
        {
            if (clock() - started > lim.WallSeconds) throw new AgentFailure($"wall-clock budget of {lim.WallSeconds:0.##} s used up", trace);
            var (resp, usage) = llm.Send(new ModelRequest(model, 4000, System.Text, messages.ToList(), specs, "low"));
            trace.ApiCalls++;
            trace.InputTokens += usage.InputTokens; trace.OutputTokens += usage.OutputTokens; trace.CacheRead += usage.CacheRead;
            trace.LatencyS = Math.Round(trace.LatencyS + usage.LatencyS, 3);
            if (resp.StopReason == "refusal") throw new AgentFailure("the model refused", trace);
            var uses = resp.Content.OfType<ToolUseBlock>().ToList();
            messages.Add(new ChatMessage("assistant", resp.Content));
            if (uses.Count == 0)
            {
                if (resp.StopReason == "max_tokens") throw new AgentFailure("the answer was cut off (max_tokens)", trace);
                messages.Add(new ChatMessage("user", new ContentBlock[] { new TextBlock("Call submit_reading now with what you can read; list anything unreadable in `unknown`.") }));
                continue;
            }
            trace.ToolCalls += uses.Count;
            if (trace.ToolCalls > lim.MaxToolCalls) throw new AgentFailure($"tool-call cap of {lim.MaxToolCalls} exceeded", trace);
            var results = new List<ContentBlock>();
            foreach (var tu in uses)
            {
                if (tu.Input is not Dictionary<string, object?> args) { results.Add(Err(tu, "the tool input must be a JSON object")); continue; }
                if (tu.Name == "submit_reading")
                {
                    try { JsonSchemaCheck.Validate(args, AgentTools.SubmitSchema); }
                    catch (SchemaException e)
                    {
                        invalid++;
                        if (invalid > lim.MaxResubmits) throw new AgentFailure($"invalid submission twice ({Cut(e.Message, 120)})", trace, e);
                        results.Add(Err(tu, $"submission rejected: {Cut(e.Message, 200)}. Call submit_reading again with a structure that fits the schema."));
                        continue;
                    }
                    return args;
                }
                var sig = tu.Name + PyJsonSorted.Dumps(args);
                if (seen.TryGetValue(sig, out var earlier))
                {
                    repeats++;
                    if (repeats >= 2) throw new AgentFailure("the same tool call was repeated", trace);
                    results.Add(new ToolResultBlock(tu.Id, earlier));
                    continue;
                }
                if (tu.Name == "zoom_region" && zoomOn) results.Add(Zoom(tu, args, path, trace, seen, sig));
                else results.Add(Err(tu, $"unknown tool {Py.StrRepr(Cut(tu.Name, 40))}; the tools are zoom_region and submit_reading"));
            }
            messages.Add(new ChatMessage("user", results));
        }
        throw new AgentFailure($"iteration cap of {lim.MaxIterations} reached without a submission", trace);
    }

    private ToolResultBlock Zoom(ToolUseBlock tu, Dictionary<string, object?> args, string path, AgentTrace trace, Dictionary<string, IReadOnlyList<ContentBlock>> seen, string sig)
    {
        try
        {
            JsonSchemaCheck.Validate(args, AgentTools.ZoomSchema);
            if (trace.Zooms >= Limits.MaxZooms) throw new ToolException($"the zoom limit of {Limits.MaxZooms} is used up; call submit_reading");
            trace.Zooms++;
            var (png, media) = AgentTools.ZoomRegion(path, args, Limits.ZoomMaxPx);
            IReadOnlyList<ContentBlock> content = new ContentBlock[] { AgentTools.ImageBlock(png, media) };
            seen[sig] = content;
            return new ToolResultBlock(tu.Id, content);
        }
        catch (Exception e) when (e is SchemaException or ToolException) { return Err(tu, Cut(e.Message, 200)); }
    }

    // ------------------------------------------------------------------ bookkeeping
    private string Key(string fhash, string model)
    {
        var spec = new Dictionary<string, object?>
        {
            ["submit"] = AgentTools.SubmitSchema, ["tools"] = AgentTools.ToolsDigest(),
            ["limits"] = new List<object?> { (long)Limits.MaxIterations, (long)Limits.MaxToolCalls, (long)Limits.MaxZooms, (long)Limits.ZoomMaxPx },
        };
        return llm.CacheKey(fhash, $"{System.Version}/{Task.Version}", model, spec, System.Text, Task.Text);
    }

    private Dictionary<string, object?> Row(string path, string fhash, string model, AgentTrace trace)
    {
        var row = llm.UsageRow("agent_image", path, fhash, model, new Llm.UsageInfo(trace.InputTokens, trace.OutputTokens, trace.CacheRead, trace.LatencyS), Math.Max(trace.ApiCalls - 1, 0));
        row["calls"] = (long)trace.ApiCalls; row["tool_calls"] = (long)trace.ToolCalls; row["zooms"] = (long)trace.Zooms;
        return row;
    }

    private void Note(string text) { lock (gate) Notes.Add(text); }

    private void Count(string what) { lock (gate) Stats[what] = Stats.GetValueOrDefault(what) + 1; }

    private static string Cut(string s, int n) => s.Length > n ? s[..n] : s;

    private static ToolResultBlock Err(ToolUseBlock tu, string text) => new(tu.Id, new ContentBlock[] { new TextBlock(text) }, IsError: true);
}
