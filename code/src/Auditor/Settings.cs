using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace Auditor;

public sealed class SettingsException : Exception
{
    public SettingsException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Numbers the activity-timeline discovery and the late-link rule depend on. Measured on this dataset (see evaluation/CHANGELOG.md).</summary>
public sealed record Tunables
{
    /// <summary>a deleted account's delete event relative to the case's reported_at</summary>
    public (double, double) AccountWindowMinutes { get; init; } = (-90.0, 15.0);
    /// <summary>an analytics anonymization event relative to reported_at</summary>
    public (double, double) AnalyticsWindowMinutes { get; init; } = (-45.0, -5.0);
    /// <summary>log-likelihood lead over the runner-up an account must have (about 7:1)</summary>
    public double PhaseMargin { get; init; } = 2.0;
    /// <summary>log-likelihood an analytics candidate's offset must reach</summary>
    public double AnalyticsMinLlr { get; init; } = 2.0;
    /// <summary>seconds between consecutive deletion-worker policy actions</summary>
    public (int, int) ChainGapsSeconds { get; init; } = (390, 449);
    public double ChainToleranceSeconds { get; init; } = 2.0;
    /// <summary>evidence captured later than audit_at + this may not link a resource</summary>
    public int LaterLinkMaxDays { get; init; } = 7;

    public (TimeSpan, TimeSpan) AccountWindow => (TimeSpan.FromMinutes(AccountWindowMinutes.Item1), TimeSpan.FromMinutes(AccountWindowMinutes.Item2));
    public (TimeSpan, TimeSpan) AnalyticsWindow => (TimeSpan.FromMinutes(AnalyticsWindowMinutes.Item1), TimeSpan.FromMinutes(AnalyticsWindowMinutes.Item2));
}

/// <summary>
/// Tunable settings in one place: defaults here, documented overrides in config/settings.toml, command-line overrides on top.
/// Precedence (lowest to highest): built-in defaults &lt; settings file &lt; AUDITOR_* environment (model names, cache dir only) &lt; command line.
/// Unknown keys, wrong types and out-of-range values are errors: a typo in a settings file must not silently run with the default.
/// <see cref="Fingerprint"/> is a hash of every setting that can change the answer (not paths); the run stamp in output.notes.md and the resume key use it, so a run with other
/// settings never reuses another run's results.
/// </summary>
public sealed record Settings
{
    // [pipeline]
    public bool LaterLinking { get; init; } = true;
    public bool Discovery { get; init; } = true;
    public bool TimingHeuristics { get; init; } = true;
    public bool CohortFilter { get; init; }
    public bool ProvisionalRows { get; init; } = true;
    // [discovery] / [audit]
    public Tunables Tune { get; init; } = new();
    // [llm]
    /// <summary>auto | on | off | file | agent</summary>
    public string Images { get; init; } = "auto";
    public string ReadingsFile { get; init; } = "";
    public string Model { get; init; } = "claude-haiku-5-5";
    public string FallbackModel { get; init; } = "claude-sonnet-5-5";
    public string CacheDir { get; init; } = "";
    public int MaxConcurrency { get; init; } = 4;
    public double TimeoutSeconds { get; init; } = 60.0;
    public long MaxRunTokens { get; init; }
    // [agent]
    public int AgentMaxIterations { get; init; } = 5;
    public int AgentMaxToolCalls { get; init; } = 6;
    public int AgentMaxZooms { get; init; } = 4;
    public double AgentWallSeconds { get; init; } = 120.0;
    public int AgentZoomMaxPx { get; init; } = 1200;

    /// <summary>Hash of everything that can change the answer. cache_dir is excluded (a path); the model names count only when images are read.</summary>
    public string Fingerprint()
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var k in SettingsFile.Keys.Where(k => k.Section is not "agent" || Images == "agent"))
        {
            if (k.Dotted is "llm.cache_dir" or "llm.max_concurrency" or "llm.timeout_seconds" or "llm.readings_file") continue;       // how readings are fetched, not a setting of the answer
            if (Images is "off" or "file" && k.Dotted is "llm.model" or "llm.fallback_model" or "llm.max_run_tokens") continue;          // no model is called
            d[k.Dotted] = k.Dump(this);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", d.Select(kv => $"\"{kv.Key}\": {kv.Value}"))))).ToLowerInvariant();
    }
}

/// <summary>Loading, layering and validating <see cref="Settings"/>.</summary>
public static class SettingsFile
{
    public enum Kind { Bool, Int, Float, Text, FloatPair, IntPair }

    public sealed record Key(string Section, string Name, Kind Kind, Func<Settings, object> Get, Func<Settings, object, Settings> Set)
    {
        public string Dotted => $"{Section}.{Name}";

        public string Dump(Settings s) => Get(s) switch
        {
            bool b => b ? "true" : "false",
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => Compat.FloatRepr(d),
            string t => Compat.DumpsCompact(t),
            (double a, double b) => $"[{Compat.FloatRepr(a)}, {Compat.FloatRepr(b)}]",
            (int a, int b) => $"[{a}, {b}]",
            var other => throw new InvalidOperationException($"unexpected setting type {other.GetType().Name}"),
        };
    }

    // The registry of every setting: one row each (section, name, type, getter, setter). A new setting is one row here and one in settings.toml.
    public static readonly IReadOnlyList<Key> Keys = new List<Key>
    {
        new("pipeline", "later_linking", Kind.Bool, s => s.LaterLinking, (s, v) => s with { LaterLinking = (bool)v }),
        new("pipeline", "discovery", Kind.Bool, s => s.Discovery, (s, v) => s with { Discovery = (bool)v }),
        new("pipeline", "timing_heuristics", Kind.Bool, s => s.TimingHeuristics, (s, v) => s with { TimingHeuristics = (bool)v }),
        new("pipeline", "cohort_filter", Kind.Bool, s => s.CohortFilter, (s, v) => s with { CohortFilter = (bool)v }),
        new("pipeline", "provisional_rows", Kind.Bool, s => s.ProvisionalRows, (s, v) => s with { ProvisionalRows = (bool)v }),
        new("llm", "images", Kind.Text, s => s.Images, (s, v) => s with { Images = (string)v }),
        new("llm", "readings_file", Kind.Text, s => s.ReadingsFile, (s, v) => s with { ReadingsFile = (string)v }),
        new("llm", "model", Kind.Text, s => s.Model, (s, v) => s with { Model = (string)v }),
        new("llm", "fallback_model", Kind.Text, s => s.FallbackModel, (s, v) => s with { FallbackModel = (string)v }),
        new("llm", "cache_dir", Kind.Text, s => s.CacheDir, (s, v) => s with { CacheDir = (string)v }),
        new("llm", "max_concurrency", Kind.Int, s => s.MaxConcurrency, (s, v) => s with { MaxConcurrency = (int)(long)v }),
        new("llm", "timeout_seconds", Kind.Float, s => s.TimeoutSeconds, (s, v) => s with { TimeoutSeconds = (double)v }),
        new("llm", "max_run_tokens", Kind.Int, s => s.MaxRunTokens, (s, v) => s with { MaxRunTokens = (long)v }),
        new("discovery", "account_window_minutes", Kind.FloatPair, s => s.Tune.AccountWindowMinutes, (s, v) => s with { Tune = s.Tune with { AccountWindowMinutes = ((double, double))v } }),
        new("discovery", "analytics_window_minutes", Kind.FloatPair, s => s.Tune.AnalyticsWindowMinutes, (s, v) => s with { Tune = s.Tune with { AnalyticsWindowMinutes = ((double, double))v } }),
        new("discovery", "phase_margin", Kind.Float, s => s.Tune.PhaseMargin, (s, v) => s with { Tune = s.Tune with { PhaseMargin = (double)v } }),
        new("discovery", "analytics_min_llr", Kind.Float, s => s.Tune.AnalyticsMinLlr, (s, v) => s with { Tune = s.Tune with { AnalyticsMinLlr = (double)v } }),
        new("discovery", "chain_gaps_seconds", Kind.IntPair, s => s.Tune.ChainGapsSeconds, (s, v) => s with { Tune = s.Tune with { ChainGapsSeconds = ((int, int))v } }),
        new("discovery", "chain_tolerance_seconds", Kind.Float, s => s.Tune.ChainToleranceSeconds, (s, v) => s with { Tune = s.Tune with { ChainToleranceSeconds = (double)v } }),
        new("audit", "later_link_max_days", Kind.Int, s => s.Tune.LaterLinkMaxDays, (s, v) => s with { Tune = s.Tune with { LaterLinkMaxDays = (int)(long)v } }),
        new("agent", "max_iterations", Kind.Int, s => s.AgentMaxIterations, (s, v) => s with { AgentMaxIterations = (int)(long)v }),
        new("agent", "max_tool_calls", Kind.Int, s => s.AgentMaxToolCalls, (s, v) => s with { AgentMaxToolCalls = (int)(long)v }),
        new("agent", "max_zooms", Kind.Int, s => s.AgentMaxZooms, (s, v) => s with { AgentMaxZooms = (int)(long)v }),
        new("agent", "wall_seconds", Kind.Float, s => s.AgentWallSeconds, (s, v) => s with { AgentWallSeconds = (double)v }),
        new("agent", "zoom_max_px", Kind.Int, s => s.AgentZoomMaxPx, (s, v) => s with { AgentZoomMaxPx = (int)(long)v }),
    };

    private static readonly string[] SectionOrder = { "pipeline", "llm", "discovery", "audit", "agent" };

    public static string DefaultFile => Path.Combine(AppContext.BaseDirectory, "config", "settings.toml");

    private static object Coerce(string name, Kind kind, object? value)
    {
        switch (kind)
        {
            case Kind.Bool:
                if (value is string bs && bs.ToLowerInvariant() is "true" or "false") value = bs.ToLowerInvariant() == "true";
                return value is bool b ? b : throw new SettingsException($"{name}: expected true/false, got {Compat.Repr(value)}");
            case Kind.FloatPair:
            case Kind.IntPair:
                if (value is string js)
                {
                    try { value = Compat.ParseJson(js); }
                    catch (DataException e) { throw new SettingsException($"{name}: expected a list like [1, 2], got {Compat.Repr(js)}", e); }
                }
                if (value is TomlArray ta) value = ta.Select(x => (object?)x).ToList();
                if (value is not List<object?> list || list.Count != 2 || !list.All(x => x is long or double or int))
                    throw new SettingsException($"{name}: expected a list of 2 numbers, got {Compat.Repr(value)}");
                double x0 = System.Convert.ToDouble(list[0], CultureInfo.InvariantCulture), x1 = System.Convert.ToDouble(list[1], CultureInfo.InvariantCulture);
                return kind == Kind.FloatPair ? (object)(x0, x1) : (object)((int)x0, (int)x1);
            case Kind.Int:
            case Kind.Float:
                if (value is bool) throw new SettingsException($"{name}: expected a number, got {Compat.Repr(value)}");
                try
                {
                    if (kind == Kind.Float)
                        return value switch { string s => double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture), long l => (double)l, double d => d, _ => throw new FormatException() };
                    return value switch { string s => long.Parse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), long l => l, double d => (long)d, _ => throw new FormatException() };
                }
                catch (Exception e) when (e is FormatException or OverflowException) { throw new SettingsException($"{name}: expected a number, got {Compat.Repr(value)}", e); }
            default:
                return value as string ?? throw new SettingsException($"{name}: expected text, got {Compat.Repr(value)}");
        }
    }

    private static Settings Validate(Settings s)
    {
        if (s.Images is not ("auto" or "on" or "off" or "file" or "agent")) throw new SettingsException($"llm.images: must be auto, on, off, file or agent, got {Compat.Repr(s.Images)}");
        foreach (var (name, lo, hi, value) in new (string, double, double, double)[]
        {
            ("max_iterations", 1, 20, s.AgentMaxIterations), ("max_tool_calls", 1, 40, s.AgentMaxToolCalls), ("max_zooms", 0, 20, s.AgentMaxZooms),
            ("wall_seconds", 5, 1800, s.AgentWallSeconds), ("zoom_max_px", 200, 2000, s.AgentZoomMaxPx),
        })
            if (value < lo || value > hi) throw new SettingsException($"agent.{name}: must be between {lo:0.##} and {hi:0.##}, got {value:0.##}");
        var t = s.Tune;
        foreach (var (n, win) in new[] { ("account_window_minutes", t.AccountWindowMinutes), ("analytics_window_minutes", t.AnalyticsWindowMinutes) })
            if (win.Item1 >= win.Item2) throw new SettingsException($"discovery.{n}: the start ({Compat.FloatRepr(win.Item1)}) must be before the end ({Compat.FloatRepr(win.Item2)})");
        if (s.MaxConcurrency is < 1 or > 16) throw new SettingsException($"llm.max_concurrency: must be between 1 and 16, got {s.MaxConcurrency}");
        if (s.TimeoutSeconds <= 0) throw new SettingsException("llm.timeout_seconds: must be positive");
        if (s.MaxRunTokens < 0) throw new SettingsException($"llm.max_run_tokens: must be 0 (no limit) or positive, got {s.MaxRunTokens}");
        if (t.PhaseMargin < 0 || t.AnalyticsMinLlr < 0 || t.ChainToleranceSeconds < 0 || t.LaterLinkMaxDays < 0) throw new SettingsException("discovery/audit numbers must not be negative");
        if (Math.Min(t.ChainGapsSeconds.Item1, t.ChainGapsSeconds.Item2) <= 0) throw new SettingsException("discovery.chain_gaps_seconds: gaps must be positive");
        return s;
    }

    /// <summary>Return a copy of <paramref name="s"/> with one <c>section.key</c> replaced (the unit both the file loader and --set use).</summary>
    public static Settings Apply(Settings s, string dotted, object? value)
    {
        if (!dotted.Contains('.', StringComparison.Ordinal)) throw new SettingsException($"{Compat.Repr(dotted)}: use section.key, e.g. pipeline.discovery");
        var key = Keys.FirstOrDefault(k => k.Dotted == dotted);
        if (key is null)
        {
            var known = string.Join(", ", Keys.OrderBy(k => k.Section, StringComparer.Ordinal).ThenBy(k => k.Name, StringComparer.Ordinal).Select(k => k.Dotted));
            throw new SettingsException($"unknown setting {Compat.Repr(dotted)}; known settings: {known}");
        }
        return Validate(key.Set(s, Coerce(dotted, key.Kind, value)));
    }

    public static Settings FromFile(string? path = null, Settings? baseSettings = null)
    {
        var s = baseSettings ?? new Settings();
        var file = path ?? DefaultFile;
        if (!File.Exists(file))
        {
            if (path is not null && path != DefaultFile) throw new SettingsException($"settings file not found: {path}");
            return s;                                              // the shipped file is optional; the built-in defaults are the same numbers
        }
        TomlTable data;
        try { data = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(file, new UTF8Encoding(false, true))) ?? new TomlTable(); }
        catch (Exception e) when (e is IOException or TomlException or DecoderFallbackException) { throw new SettingsException($"cannot read {file}: {e.Message}", e); }
        foreach (var (section, body) in data)
        {
            if (!SectionOrder.Contains(section) || body is not TomlTable table) throw new SettingsException($"{Path.GetFileName(file)}: unknown section [{section}]");
            foreach (var (key, value) in table) s = Apply(s, $"{section}.{key}", value);
        }
        return s;
    }

    public static Settings FromEnvironment(Settings s, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        foreach (var (variable, dotted) in new[] { ("AUDITOR_VLM_MODEL", "llm.model"), ("AUDITOR_VLM_FALLBACK_MODEL", "llm.fallback_model"), ("AUDITOR_CACHE_DIR", "llm.cache_dir") })
        {
            var v = env(variable);
            if (!string.IsNullOrEmpty(v)) s = Apply(s, dotted, v);
        }
        return s;
    }

    /// <summary>defaults &lt; file &lt; environment &lt; <paramref name="overrides"/> (a list of 'section.key=value' strings).</summary>
    public static Settings Load(string? path = null, IEnumerable<string>? overrides = null, Func<string, string?>? env = null)
    {
        var s = FromEnvironment(FromFile(path), env);
        foreach (var item in overrides ?? Array.Empty<string>())
        {
            int eq = item.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0) throw new SettingsException($"--set {Compat.Repr(item)}: use section.key=value");
            s = Apply(s, item[..eq].Trim(), item[(eq + 1)..].Trim());
        }
        return s;
    }

    /// <summary>(section.key, value) in file order, for the run stamp.</summary>
    public static List<(string Key, string Value)> AsTable(Settings s) =>
        SectionOrder.SelectMany(sec => Keys.Where(k => k.Section == sec).OrderBy(k => k.Name, StringComparer.Ordinal)).Select(k => (k.Dotted, k.Dump(s))).ToList();
}
