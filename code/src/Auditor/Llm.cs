using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Auditor;

/// <summary>One line of the model usage log (JSONL): provider, model, tokens, cache hit or live call, latency, cost if prices were supplied.</summary>
public sealed class UsageLog
{
    private readonly object gate = new();

    public UsageLog(string? path = null) { Path = path; }

    public string? Path { get; }
    public List<Dictionary<string, object?>> Rows { get; } = new();

    public void Add(Dictionary<string, object?> row)
    {
        lock (gate)                                          // image reads run in several threads: one whole line per row
        {
            Rows.Add(row);
            if (Path is null) return;
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (dir is not null) Directory.CreateDirectory(dir);
            File.AppendAllText(Path, SortedJson.Dumps(row) + "\n", new UTF8Encoding(false));
        }
    }
}

/// <summary>JSON-schema subset used for every model answer and tool input: type, required, properties, items, enum, additionalProperties=false, maxItems, maxLength.</summary>
public static class JsonSchemaCheck
{
    /// <summary>Throws <see cref="SchemaException"/> when <paramref name="value"/> does not fit <paramref name="schema"/>.</summary>
    public static void Validate(object? value, Dictionary<string, object?> schema, string path = "$")
    {
        var t = schema.TryGetValue("type", out var tv) ? tv as string : null;
        bool ok = t switch
        {
            null => true,
            "object" => value is Dictionary<string, object?>,
            "array" => value is List<object?>,
            "string" => value is string,
            "boolean" => value is bool,
            "integer" => value is long or System.Numerics.BigInteger,
            "number" => value is long or double or System.Numerics.BigInteger or bool,
            _ => true,
        };
        if (!ok) throw new SchemaException($"{path}: expected {t}, got {Compat.TypeName(value)}");
        if (schema.TryGetValue("enum", out var en) && en is List<object?> allowed && !allowed.Any(a => Equals(a, value)))
            throw new SchemaException($"{path}: {Compat.Repr(value)} not in {Compat.Repr(en)}");
        if (t == "object" && value is Dictionary<string, object?> obj)
        {
            if (schema.TryGetValue("required", out var req) && req is List<object?> required)
                foreach (var k in required.OfType<string>())
                    if (!obj.ContainsKey(k)) throw new SchemaException($"{path}: missing required key {Compat.StrRepr(k)}");
            var props = schema.TryGetValue("properties", out var p) && p is Dictionary<string, object?> pd ? pd : new Dictionary<string, object?>();
            if (schema.TryGetValue("additionalProperties", out var ap) && ap is false)
            {
                var extra = obj.Keys.Where(k => !props.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
                if (extra.Count > 0) throw new SchemaException($"{path}: unexpected keys {Compat.ListRepr(extra)}");
            }
            foreach (var (k, sub) in props)
                if (obj.TryGetValue(k, out var child)) Validate(child, (Dictionary<string, object?>)sub!, $"{path}.{k}");
        }
        if (t == "string" && value is string s && schema.TryGetValue("maxLength", out var ml) && ml is long maxLen && s.EnumerateRunes().Count() > maxLen)
            throw new SchemaException($"{path}: text longer than {maxLen} characters");
        if (t == "array" && value is List<object?> list)
        {
            if (schema.TryGetValue("maxItems", out var mi) && mi is long maxItems && list.Count > maxItems) throw new SchemaException($"{path}: more than {maxItems} items");
            var items = (Dictionary<string, object?>)schema["items"]!;
            for (int i = 0; i < list.Count; i++) Validate(list[i], items, $"{path}[{i}]");
        }
    }
}

/// <summary>
/// The only class that talks to a model. Everything else gets validated JSON or an exception.
/// What it does for every call:
///   * disk cache keyed by sha256(file bytes) + prompt version + sha256(prompt text) + model + schema + pipeline version + rules-file digest (see <see cref="Versions"/>)
///     -> a rerun costs nothing and is deterministic, and any of those changing is a miss, never a stale hit
///   * retries with exponential backoff on transport errors; ONE re-ask when the answer is not valid JSON for the schema
///   * code validation of the answer against the JSON schema
///   * usage log (JSONL): provider, model, input/output tokens, cache hit or live call, latency, cost if prices were supplied
/// Prices are NEVER hard-coded: they come from the user's price file (see <see cref="Prices"/>); without a complete entry the cost is reported as "unpriced".
/// The model's text is data: it can only influence the run through fields that code then validates.
/// </summary>
public sealed class Llm
{
    public const string DefaultModel = "claude-haiku-5-5";                // cost-efficient, vision-capable
    public const string DefaultFallbackModel = "claude-sonnet-5-5";       // used only when the first model fails schema validation twice
    public const double DefaultTimeoutS = 60.0;                           // per request
    public const double MaxBackoffS = 60.0;                               // cap for one backoff wait, also for a long Retry-After
    public static readonly int[] FatalStatus = { 401, 403 };              // rejected credentials / no permission: every further request would fail the same way
    public static readonly int[] RetryStatus = { 408, 409, 429 };         // plus every 5xx; any other HTTP status is the caller's error and is not retried
    public const int CallAttempts = 4;                                    // tries per model call for transport errors / 429 / 5xx (waits 1, 2, 4 s unless Retry-After says more)

    private IModelClient? client;
    private readonly Action<double> sleep;
    private readonly Func<double> clock;
    private readonly object state = new();                                // guards pauseUntil, keyLocks, tokensUsed, halted and the lazily built client
    private double pauseUntil;                                            // rate-limit backoff shared by every thread: one 429 slows all workers, not just one
    private readonly Dictionary<string, object> keyLocks = new(StringComparer.Ordinal);
    private string halted = "";                                           // set by a fatal failure: later calls fail at once without touching the network

    public Llm(string? cacheDir = null, string? usagePath = null, IModelClient? client = null, string? model = null, string? fallbackModel = null, Action<double>? sleep = null,
               VersionStamp? stamp = null, double timeoutS = DefaultTimeoutS, Func<double>? clock = null, long maxRunTokens = 0)
    {
        Model = model ?? Environment.GetEnvironmentVariable("AUDITOR_VLM_MODEL") ?? DefaultModel;
        FallbackModel = fallbackModel ?? Environment.GetEnvironmentVariable("AUDITOR_VLM_FALLBACK_MODEL") ?? DefaultFallbackModel;
        CacheDir = cacheDir ?? Environment.GetEnvironmentVariable("AUDITOR_CACHE_DIR") ?? Path.Combine(AppContext.BaseDirectory, ".cache", "llm");
        Usage = new UsageLog(usagePath ?? Environment.GetEnvironmentVariable("AUDITOR_USAGE_LOG") ?? Path.Combine(AppContext.BaseDirectory, "out", "usage_log.jsonl"));
        Stamp = Versions.CacheStamp(stamp ?? Versions.DefaultStamp());     // the run's stamp (rules digest included)
        this.client = client;
        this.sleep = sleep ?? (s => Thread.Sleep(TimeSpan.FromSeconds(s)));
        this.clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        TimeoutS = timeoutS;
        MaxRunTokens = maxRunTokens;                                      // 0 = no limit; otherwise no request starts once input + output tokens of this run reach it
        (PriceTable, PriceProblems) = Prices.Load();
    }

    public string Model { get; }
    public string FallbackModel { get; }
    public string CacheDir { get; }
    public UsageLog Usage { get; }
    public VersionStamp Stamp { get; }
    public double TimeoutS { get; }
    public long MaxRunTokens { get; }
    public long TokensUsed { get; private set; }
    public Dictionary<string, Prices.Price> PriceTable { get; }
    public List<string> PriceProblems { get; }

    // ------------------------------------------------------------------ client
    public IModelClient Client()
    {
        lock (state) return ClientLocked();
    }

    private IModelClient ClientLocked()
    {
        if (client is not null) return client;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN")))
            throw new LlmUnavailableException("ANTHROPIC_API_KEY is not set");
        client = new AnthropicModelClient();
        return client;
    }

    // ------------------------------------------------------------------ cache
    /// <summary>
    /// Every input that can change the model's reading: the image, the prompt label AND text, the model, the schema, the pipeline version and the rules-file digest. A change in any of them
    /// is a miss.
    /// </summary>
    public string CacheKey(string fileHash, string promptVersion, string model, Dictionary<string, object?> schema, string system = "", string instruction = "")
    {
        var blob = SortedJson.Dumps(new Dictionary<string, object?>
        {
            ["fmt"] = (long)Versions.LlmCacheFormat, ["f"] = fileHash, ["p"] = promptVersion, ["pt"] = Versions.DigestText(system, instruction), ["m"] = model, ["s"] = schema,
            ["pipeline"] = Stamp.Pipeline, ["rules"] = Stamp.Rules,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(blob))).ToLowerInvariant();
    }

    public Dictionary<string, object?>? CacheGet(string key)
    {
        var p = Path.Combine(CacheDir, key + ".json");
        try { return File.Exists(p) ? Compat.ParseJson(Compat.ReadText(p)) as Dictionary<string, object?> : null; }
        catch (Exception e) when (e is IOException or DataException) { return null; }      // a corrupt cache entry is just a miss
    }

    public void CachePut(string key, Dictionary<string, object?> value)
    {
        Directory.CreateDirectory(CacheDir);
        var tmp = Path.Combine(CacheDir, $".{key}.{Environment.CurrentManagedThreadId}.tmp");        // write then rename: a reader never sees half an entry
        File.WriteAllText(tmp, SortedJson.Dumps(value), new UTF8Encoding(false));
        File.Move(tmp, Path.Combine(CacheDir, key + ".json"), overwrite: true);
    }

    // ------------------------------------------------------------------ public
    /// <summary>Describe one image as JSON matching <paramref name="schema"/>. Throws <see cref="LlmUnavailableException"/> (no key) or <see cref="SchemaException"/> (the model never complied).</summary>
    public Dictionary<string, object?> ReadImageJson(string kind, string imagePath, string system, string instruction, Dictionary<string, object?> schema, string promptVersion)
    {
        var data = File.ReadAllBytes(imagePath);
        var fhash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var media = Path.GetExtension(imagePath).ToLowerInvariant() == ".png" ? "image/png" : "image/jpeg";
        Exception? last = null;
        foreach (var model in new[] { Model, FallbackModel })
        {
            var key = CacheKey(fhash, promptVersion, model, schema, system, instruction);
            lock (KeyLock(key))                                           // the same image twice at once costs one call: the second waits and hits the cache
            {
                var cached = CacheGet(key);
                if (cached is not null)
                {
                    Usage.Add(new() { ["ts"] = Now(), ["provider"] = "anthropic", ["model"] = model, ["kind"] = kind, ["file"] = Path.GetFileName(imagePath), ["file_sha256"] = fhash,
                                      ["cache_hit"] = true, ["input_tokens"] = 0L, ["output_tokens"] = 0L, ["calls"] = 0L });
                    return cached;
                }
                var ask = instruction;
                for (int attempt = 0; attempt < 2; attempt++)               // one re-ask for invalid JSON
                {
                    try
                    {
                        var (raw, usage) = Call(model, system, ask, data, media, schema);
                        var value = ParseJson(raw);
                        JsonSchemaCheck.Validate(value, schema);
                        var reading = (Dictionary<string, object?>)value!;
                        CachePut(key, reading);
                        Usage.Add(UsageRow(kind, imagePath, fhash, model, usage, attempt));
                        return reading;
                    }
                    catch (Exception e) when (e is SchemaException or DataException)
                    {
                        last = e;
                        ask = ask + "\n\nYour previous answer was not valid JSON for the schema. Answer with the JSON object only.";
                    }
                }
                Usage.Add(new() { ["ts"] = Now(), ["provider"] = "anthropic", ["model"] = model, ["kind"] = kind, ["file"] = Path.GetFileName(imagePath), ["file_sha256"] = fhash,
                                  ["cache_hit"] = false, ["input_tokens"] = 0L, ["output_tokens"] = 0L, ["calls"] = 0L, ["error"] = $"schema validation failed twice: {last?.Message}" });
            }
        }
        throw new SchemaException($"no model produced valid JSON for {Path.GetFileName(imagePath)}: {last?.Message}");
    }

    // ------------------------------------------------------------------ internals
    private (string Text, UsageInfo Usage) Call(string model, string system, string instruction, byte[] data, string media, Dictionary<string, object?> schema)
    {
        var request = new ModelRequest(model, 4000, system,
            new[] { new ChatMessage("user", new ContentBlock[] { new ImageBlock(media, Convert.ToBase64String(data)), new TextBlock(instruction) }) }, Effort: "low", JsonSchema: schema);
        var (resp, usage) = Send(request);
        if (resp.StopReason == "refusal") throw new SchemaException("model refused");
        return (string.Concat(resp.Content.OfType<TextBlock>().Select(b => b.Text)), usage);
    }

    public sealed record UsageInfo(long InputTokens, long OutputTokens, long CacheRead, double LatencyS);

    /// <summary>
    /// One request with the shared policy: the per-request timeout, back-off on transport errors / 408 / 409 / 429 / 5xx (Retry-After honoured, capped), a pause shared by every worker after a
    /// 429. Other HTTP errors are not retried; 401 and 403 stop every later request of the run. Returns (response, usage); throws <see cref="LlmUnavailableException"/> when the call cannot be
    /// completed. Used by the single-shot reader and by the agent loop.
    /// </summary>
    public (ModelResponse Response, UsageInfo Usage) Send(ModelRequest request)
    {
        CheckOpen();
        var api = Client();
        double delay = 1.0, waitedUntil = 0.0;                           // waitedUntil: the pause this call already sat out itself
        for (int attempt = 0; attempt < CallAttempts; attempt++)
        {
            WaitForPause(waitedUntil);
            try
            {
                double t0 = clock();
                var resp = api.Create(request, TimeSpan.FromSeconds(TimeoutS));
                var usage = new UsageInfo(resp.Usage.InputTokens, resp.Usage.OutputTokens, resp.Usage.CacheReadInputTokens, Math.Round(clock() - t0, 3));
                lock (state) TokensUsed += usage.InputTokens + usage.OutputTokens;
                return (resp, usage);
            }
            catch (ModelApiException e)                                  // transport / rate limit / 5xx: back off and retry, then give up
            {
                bool retryable = e.StatusCode is int status ? RetryStatus.Contains(status) || status >= 500 : e.Kind is "RateLimit" or "Timeout" or "Connection" or "InternalServer" or "Overloaded";
                if (!retryable || attempt == CallAttempts - 1)
                {
                    bool fatal = e.StatusCode is int s2 && FatalStatus.Contains(s2);
                    if (fatal) Halt($"{e.Kind}: HTTP {e.StatusCode}");
                    throw new LlmUnavailableException($"{e.Kind}: {e.Message}", fatal, e);
                }
                double wait = Math.Min(Math.Max(delay, e.RetryAfterSeconds), MaxBackoffS);          // the server's Retry-After wins when it asks for longer
                if (e.StatusCode == 429 || e.Kind is "RateLimit" or "Overloaded") waitedUntil = PauseAll(wait);   // every worker backs off, not only this one
                sleep(wait);
                delay *= 2;
            }
        }
        throw new LlmUnavailableException("retry loop ended without a result");
    }

    private void Halt(string reason)
    {
        lock (state) if (halted.Length == 0) halted = reason;
    }

    /// <summary>Refuse to start a request after a fatal failure or once the run's token budget is spent.</summary>
    private void CheckOpen()
    {
        lock (state)
        {
            if (halted.Length > 0) throw new LlmUnavailableException($"no further model requests in this run ({halted})", fatal: true);
            if (MaxRunTokens > 0 && TokensUsed >= MaxRunTokens)
                throw new LlmUnavailableException($"the run's token budget of {MaxRunTokens} is used up ({TokensUsed} tokens)", fatal: true);
        }
    }

    private double PauseAll(double seconds)
    {
        lock (state)
        {
            pauseUntil = Math.Max(pauseUntil, clock() + seconds);
            return pauseUntil;
        }
    }

    private void WaitForPause(double alreadyWaitedUntil)
    {
        double until, remaining;
        lock (state) { until = pauseUntil; remaining = until - clock(); }
        if (remaining > 0 && until > alreadyWaitedUntil) sleep(remaining);
    }

    public object KeyLock(string key)
    {
        lock (state)
        {
            if (!keyLocks.TryGetValue(key, out var l)) keyLocks[key] = l = new object();
            return l;
        }
    }

    public Dictionary<string, object?> UsageRow(string kind, string path, string fhash, string model, UsageInfo u, int attempt)
    {
        PriceTable.TryGetValue(model, out var price);
        return new()
        {
            ["ts"] = Now(), ["provider"] = "anthropic", ["model"] = model, ["kind"] = kind, ["file"] = Path.GetFileName(path), ["file_sha256"] = fhash, ["cache_hit"] = false,
            ["calls"] = (long)(1 + attempt), ["input_tokens"] = u.InputTokens, ["output_tokens"] = u.OutputTokens, ["cache_read_input_tokens"] = u.CacheRead, ["latency_s"] = u.LatencyS,
            ["cost_usd"] = price?.Cost(u.InputTokens, u.OutputTokens, u.CacheRead), ["price_source"] = price?.SourceUrl ?? "unpriced", ["price_date"] = price?.Date ?? "",
        };
    }

    public static object? ParseJson(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            s = s.Trim('`');
            int nl = s.IndexOf('\n', StringComparison.Ordinal);
            s = nl >= 0 ? s[(nl + 1)..] : s;
        }
        return Compat.ParseJson(s);
    }

    public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
