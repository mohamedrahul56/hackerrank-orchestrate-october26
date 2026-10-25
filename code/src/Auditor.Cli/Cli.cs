using System.Security.Cryptography;
using System.Text;
using Auditor;
using Auditor.Agent;
using Auditor.Evidence;

namespace Auditor.Cli;

/// <summary>
/// Run the deletion-audit agent.
///
///     auditor                                   # all 518 requests -> ./output.csv
///     auditor --requests sample_deletion_requests.csv --out code/out/pred_samples.csv
///
/// Reads only files under dataset/. Writes the answer CSV, re-reads it from disk and validates every row.
/// Anything unusual (fallbacks, assumptions, later-evidence links) is written to &lt;out&gt;.notes.md and summarised on stderr.
/// </summary>
public static class Cli
{
    private sealed class Options
    {
        public string? Dataset, Out, Config, Explain, Images;
        public string Requests = "deletion_requests.csv";
        public List<string> Overrides = new();
        public bool ShowSettings, NoLaterLinking, NoDiscovery, NoTimingHeuristics, CohortFilter, Restart, KeepCheckpoint, Verbose, Help;
    }

    private const string Usage = """
        usage: auditor [--dataset DIR] [--requests FILE] [--out FILE] [--config FILE] [--set SECTION.KEY=VALUE]... [--show-settings]
                       [--no-later-linking] [--no-discovery] [--no-timing-heuristics] [--cohort-filter]
                       [--images {auto,on,off,file,agent}] [--explain CASE_ID] [--restart] [--keep-checkpoint] [-v]

          --dataset DIR            the dataset folder (default: ./dataset of the repository)
          --requests FILE          file inside --dataset with the cases to answer (default deletion_requests.csv)
          --out FILE               the answer CSV (default ./output.csv of the repository)
          --config FILE            settings file (TOML; default config/settings.toml, documented there)
          --set SECTION.KEY=VALUE  override one setting, e.g. --set discovery.phase_margin=3.0 (repeatable; wins over the file and the environment)
          --show-settings          print the effective settings, their fingerprint and the version stamp, then exit
          --no-later-linking       forbid linking resources to users with evidence captured after audit_at
          --no-discovery           disable activity-timeline discovery of id-less accounts / analytics
          --no-timing-heuristics   disable the two second-of-minute heuristics (account offsets, analytics 40/41 s offset); they exploit this dataset's event timing
          --cohort-filter          ablation only: narrow ambiguous accounts by cohort snapshot (measured harmful, off by default)
          --images MODE            read the PNG evidence with a vision model (needs ANTHROPIC_API_KEY); auto = only when a key is set; file = take the readings from a
                                   readings file (settings llm.readings_file, default code/out/agent_readings.json) instead of a model; agent = the bounded tool-calling
                                   reader of Auditor.Agent (needs a key, limits in settings [agent]) (default: settings llm.images)
          --explain CASE_ID        print how this one case got its answer (identity, evidence, rule, why each step exists or is omitted) and exit; read-only: writes no
                                   output file, notes or usage log; screenshots are read only with --images on
          --restart                ignore a checkpoint left by an interrupted run and audit every case again
          --keep-checkpoint        keep <out>.partial.jsonl after a successful run (it is removed otherwise)
          -v, --verbose
        """;

    /// <summary>The repository root: the nearest folder, from the working directory up, that holds a dataset/ folder (falls back to the working directory).</summary>
    /// <summary>Test seam: replaces the output validator for the current async flow only (so parallel tests never see each other's override).</summary>
    public static readonly AsyncLocal<Func<string, Dataset, List<Issue>>?> ValidatorOverride = new();

    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "dataset"))) return dir.FullName;
        return Directory.GetCurrentDirectory();
    }

    private static Options? Parse(string[] args, TextWriter err)
    {
        var o = new Options();
        string Value(ref int i, string name)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"{name} needs a value");
            return args[++i];
        }

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                string? inline = null;
                if (a.StartsWith("--", StringComparison.Ordinal) && a.Contains('=', StringComparison.Ordinal)) { inline = a[(a.IndexOf('=', StringComparison.Ordinal) + 1)..]; a = a[..a.IndexOf('=', StringComparison.Ordinal)]; }
                string V(string name) => inline ?? Value(ref i, name);
                switch (a)
                {
                    case "--dataset": o.Dataset = V(a); break;
                    case "--requests": o.Requests = V(a); break;
                    case "--out": o.Out = V(a); break;
                    case "--config": o.Config = V(a); break;
                    case "--set": o.Overrides.Add(V(a)); break;
                    case "--images":
                        o.Images = V(a);
                        if (o.Images is not ("auto" or "on" or "off" or "file" or "agent")) throw new ArgumentException($"argument --images: invalid choice: '{o.Images}' (choose from auto, on, off, file, agent)");
                        break;
                    case "--explain": o.Explain = V(a); break;
                    case "--show-settings": o.ShowSettings = true; break;
                    case "--no-later-linking": o.NoLaterLinking = true; break;
                    case "--no-discovery": o.NoDiscovery = true; break;
                    case "--no-timing-heuristics": o.NoTimingHeuristics = true; break;
                    case "--cohort-filter": o.CohortFilter = true; break;
                    case "--restart": o.Restart = true; break;
                    case "--keep-checkpoint": o.KeepCheckpoint = true; break;
                    case "-v": case "--verbose": o.Verbose = true; break;
                    case "-h": case "--help": o.Help = true; break;
                    default: throw new ArgumentException($"unrecognized arguments: {a}");
                }
            }
        }
        catch (ArgumentException e)
        {
            err.WriteLine(Usage.Split('\n')[0]);
            err.WriteLine($"error: {e.Message}");
            return null;
        }
        return o;
    }

    public static int Run(string[] args, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;
        var o = Parse(args, stderr);
        if (o is null) return 2;
        if (o.Help) { stdout.WriteLine(Usage); return 0; }
        try { return RunChecked(o, stdout, stderr); }
        catch (DatasetException e)               // a required input file is missing or has no usable header: nothing sensible can be written
        {
            stderr.WriteLine($"error: {e.Message}");
            return 2;
        }
    }

    /// <summary>Test seam: a model client (a fake) used instead of the real SDK client, for the current async flow only.</summary>
    public static readonly AsyncLocal<IModelClient?> ModelClientOverride = new();

    private static Llm MakeLlm(Settings st, VersionStamp stamp, string usagePath) =>
        new(cacheDir: st.CacheDir.Length > 0 ? st.CacheDir : null, usagePath: usagePath, client: ModelClientOverride.Value, model: st.Model, fallbackModel: st.FallbackModel, stamp: stamp,
            timeoutS: st.TimeoutSeconds, sleep: ModelClientOverride.Value is null ? null : _ => { }, maxRunTokens: st.MaxRunTokens);

    private static FileReadings FileReadingsSource(Settings st, string usagePath, string root) =>
        new(st.ReadingsFile.Length > 0 ? st.ReadingsFile : Path.Combine(root, "code", "out", "agent_readings.json"), new UsageLog(usagePath));

    private static AgentImageReader AgentReader(Settings st, VersionStamp stamp, string usagePath, string dataset) =>
        new(MakeLlm(st, stamp, usagePath), Load.ManifestRows(dataset).ToDictionary(r => Path.Combine(dataset, r.FilePath), r => r.FileId, StringComparer.Ordinal),
            new AgentLimits(st.AgentMaxIterations, st.AgentMaxToolCalls, st.AgentMaxZooms, st.AgentWallSeconds, st.AgentZoomMaxPx));

    private static int ExplainCase(Options o, AuditConfig cfg, Settings st, VersionStamp stamp, string dataset, TextWriter stdout, TextWriter stderr)
    {
        // build the auditor exactly as a normal run does, print the explanation, write nothing (no output.csv, notes or usage log)
        IImageReader? reader = null;
        if (st.Images is "on" or "file" or "agent")
        {
            var tmpUsage = Path.Combine(Directory.CreateTempSubdirectory("explain_").FullName, "explain_usage.jsonl");   // the usage of an explanation is not part of any run's log
            reader = st.Images switch
            {
                "agent" => AgentReader(st, stamp, tmpUsage, dataset),
                "on" => new SingleShotImageReader(MakeLlm(st, stamp, tmpUsage)),
                _ => FileReadingsSource(st, tmpUsage, Path.GetDirectoryName(Path.GetFullPath(dataset))!),
            };
        }
        var auditor = new CaseAuditor(dataset, AuditConfigWith(cfg, reader));
        try { stdout.Write(Explainer.Explain(auditor, o.Explain!)); }
        catch (KeyNotFoundException)
        {
            var known = auditor.Requests.Keys.OrderBy(c => c.Length).ThenBy(c => c, StringComparer.Ordinal).ToList();
            stderr.WriteLine($"error: no case {Py.StrRepr(o.Explain!)} in the request files (e.g. {string.Join(", ", known.Take(3))} ... {known[^1]})");
            return 2;
        }
        return 0;
    }

    private static AuditConfig AuditConfigWith(AuditConfig c, IImageReader? reader) => new()
    {
        AllowLaterLinking = c.AllowLaterLinking, UseDiscovery = c.UseDiscovery, ProvisionalRows = c.ProvisionalRows, UseCohortFilter = c.UseCohortFilter,
        UseTimingHeuristics = c.UseTimingHeuristics, Tune = c.Tune, ImageWorkers = c.ImageWorkers, ImageReader = reader,
    };

    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static int RunChecked(Options o, TextWriter stdout, TextWriter stderr)
    {
        var root = RepoRoot();
        var dataset = o.Dataset ?? Path.Combine(root, "dataset");
        var outPath = o.Out ?? Path.Combine(root, "output.csv");
        Settings st;
        try                                                    // precedence: defaults < settings file < environment < --set < dedicated flags
        {
            st = SettingsFile.Load(o.Config, o.Overrides);
            foreach (var (item, on) in new[]
            {
                ("pipeline.later_linking=false", o.NoLaterLinking), ("pipeline.discovery=false", o.NoDiscovery), ("pipeline.cohort_filter=true", o.CohortFilter),
                ("pipeline.timing_heuristics=false", o.NoTimingHeuristics), ($"llm.images={o.Images}", o.Images is not null),
            })
                if (on) { var parts = item.Split('=', 2); st = SettingsFile.Apply(st, parts[0], parts[1]); }
        }
        catch (SettingsException e)
        {
            stderr.WriteLine($"error: settings: {e.Message}");
            return 2;
        }
        var stamp = Versions.Stamp(dataset, st.Fingerprint());
        if (o.ShowSettings)
        {
            stdout.WriteLine(string.Join("\n", SettingsFile.AsTable(st).Select(kv => $"{kv.Key} = {kv.Value}")));
            stdout.WriteLine(string.Join("\n", new[] { "" }.Concat(Versions.StampLines(stamp))));
            return 0;
        }
        var cfg = AuditConfig.FromSettings(st);
        if (o.Explain is not null) return ExplainCase(o, cfg, st, stamp, dataset, stdout, stderr);
        var usagePath = Environment.GetEnvironmentVariable("AUDITOR_USAGE_LOG") is { Length: > 0 } envUsage ? envUsage : Path.ChangeExtension(outPath, ".usage.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(usagePath))!);
        File.WriteAllText(usagePath, "", new UTF8Encoding(false));            // one log per run: the usage report summarises exactly this run
        bool keyPresent = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"));   // presence only, never the value
        bool imagesByModel = st.Images == "on" || (st.Images == "auto" && keyPresent);
        bool imagesFromFile = st.Images == "file";
        bool imagesByAgent = st.Images == "agent";
        bool imagesOn = imagesByModel || imagesFromFile || imagesByAgent;   // some source reads the screenshots
        FileReadings? readings = null;
        AgentImageReader? agent = null;
        IImageReader? reader = null;
        if (imagesByAgent) { agent = AgentReader(st, stamp, usagePath, dataset); reader = agent; }
        else if (imagesByModel) reader = new SingleShotImageReader(MakeLlm(st, stamp, usagePath));
        else if (imagesFromFile)
        {
            try { readings = FileReadingsSource(st, usagePath, root); }
            catch (ReadingsException e) { stderr.WriteLine($"error: {e.Message}"); return 2; }
            stamp = stamp with { Readings = readings.Describe() };
            reader = readings;
        }
        var auditor = new CaseAuditor(dataset, AuditConfigWith(cfg, reader));
        var ds = Dataset.Load(dataset, o.Requests);
        // Finished cases are appended to <out>.partial.jsonl as they complete. If this run is killed, the next run with the same inputs, versions and settings (the run key) reads the file
        // and audits only the cases that are missing; any other run discards it.
        var key = Checkpoint.RunKey(dataset, o.Requests, stamp, imagesOn, Checkpoint.ImageObservationDigest(auditor));
        using var ckpt = new Checkpoint(Path.ChangeExtension(outPath, ".partial.jsonl"), key, o.Restart);
        var (results, reused) = Checkpoint.AuditAll(auditor, ds.CaseOrder, ckpt);
        stderr.WriteLine($"checkpoint {Path.GetFileName(Path.ChangeExtension(outPath, ".partial.jsonl"))}: {ckpt.How}; {reused} of {results.Count} cases reused, {results.Count - reused} audited");
        OutputIo.Write(outPath, results.Select(x => new OutputIo.Row(x.CaseId, x.Status, x.Steps)));
        var issues = (ValidatorOverride.Value ?? Validator.ValidateOutputFile)(outPath, ds);
        var errors = issues.Where(i => i.Severity == "error").ToList();
        var counts = StatusCounts(results);

        new UsageLog(usagePath).Add(new()
        {
            ["kind"] = "run_info", ["images_read_by_model"] = imagesByModel || imagesByAgent, ["images_read_by_agent"] = imagesByAgent, ["images_read_from_file"] = imagesFromFile,
            ["agent_screenshots_read"] = (long)(agent?.Stats["read"] ?? 0), ["agent_screenshots_failed"] = (long)(agent?.Stats["failed"] ?? 0),
            ["readings_file"] = readings?.Describe() ?? "", ["api_key_present"] = keyPresent, ["requests"] = (long)results.Count, ["output_file"] = Path.GetFileName(outPath),
            ["output_sha256"] = Sha256File(outPath), ["status_counts"] = counts.ToDictionary(kv => kv.Key, kv => (object?)(long)kv.Value),
            ["provisional_rows"] = results.Where(x => x.Provisional).Select(x => (object?)x.CaseId).ToList(), ["stamp"] = StampJson(stamp),
            ["checkpoint"] = ckpt.How, ["cases_reused_from_checkpoint"] = (long)reused,
        });
        List<string> notes =
        [
            $"# Run notes for {Path.GetFileName(outPath)}", "", "## Version stamp (what produced this file)", "", .. Versions.StampLines(stamp), "",
            "Effective settings: " + string.Join(", ", SettingsFile.AsTable(st).Select(kv => $"`{kv.Key}={kv.Value}`")), "",
            $"cases: {results.Count}, status counts: {CountsRepr(counts)}", $"evidence warnings: {auditor.Base.Warnings.Count}", "",
        ];
        notes.AddRange(auditor.Base.Warnings.Take(50).Select(w => $"* {w}"));
        notes.AddRange(auditor.Res.Warnings.Take(20).Select(w => $"* {w}"));
        notes.AddRange(new[] { "", "## Evidence routing (each manifest file: handler, reader, outcome; the model never chooses a route)", "" });
        notes.AddRange(Router.SummaryLines(auditor.Base.Routes));
        notes.Add("");
        if (agent is not null)
        {
            notes.AddRange(new[] { "", "## Agent layer (`--images agent`): what the gate did", "", $"screenshots read: {agent.Stats["read"]}, loops that ended without a reading: {agent.Stats["failed"]}; limits {agent.Limits}", "" });
            notes.AddRange(agent.Notes.OrderBy(n => n, StringComparer.Ordinal).Take(100).Select(n => $"* {n}"));
        }
        notes.AddRange(ds.BadRows.Select(b => $"* request file line {b.Line} ({(b.CaseId.Length > 0 ? b.CaseId : "no case_id")}): {b.Reason}"));
        var provisional = results.Where(x => x.Provisional).ToList();
        notes.AddRange(new[] { "", $"## Provisional rows ({provisional.Count}): built from structured data because the only state evidence is a screenshot that was not read", "" });
        notes.AddRange(provisional.Select(x => $"* {x.CaseId}: {x.Status}, {x.Steps.Count} steps - " + string.Join("; ", x.Notes.Where(n => n.StartsWith("PROVISIONAL", StringComparison.Ordinal)))));
        notes.AddRange(new[] { "", "## Per-case notes (assumptions, fallbacks)", "" });
        notes.AddRange(results.SelectMany(x => x.Notes.Select(n => $"* {x.CaseId}: {n}")));
        notes.AddRange(new[] { "", "## Later-evidence links (D3: evidence captured after audit_at used ONLY to link a resource to the user)", "" });
        notes.AddRange(results.SelectMany(x => x.LaterLinks.Select(l => $"* {x.CaseId}: {l.Resource} linked via {l.FileId} (captured {l.Time})")));
        File.WriteAllText(Path.ChangeExtension(outPath, ".notes.md"), string.Join("\n", notes) + "\n", new UTF8Encoding(false));

        int fallbacks = results.Sum(x => x.Notes.Count(n => n.StartsWith("FALLBACK", StringComparison.Ordinal)));
        stderr.WriteLine($"wrote {outPath} ({results.Count} rows); status counts {CountsRepr(counts)}; fallbacks {fallbacks}; provisional {provisional.Count} {Py.ListRepr(provisional.Select(x => x.CaseId))}; validator errors {errors.Count}");
        foreach (var i in errors.Take(10)) stderr.WriteLine(i.ToString());
        ckpt.Close(remove: errors.Count == 0 && !o.KeepCheckpoint);                // on validator errors the file is kept for inspection
        return errors.Count > 0 ? 1 : 0;
    }

    private static Dictionary<string, int> StatusCounts(IEnumerable<CaseResult> results)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in results) counts[r.Status] = counts.GetValueOrDefault(r.Status) + 1;
        return counts;
    }

    private static string CountsRepr(Dictionary<string, int> counts) => "{" + string.Join(", ", counts.Select(kv => $"{Py.StrRepr(kv.Key)}: {kv.Value}")) + "}";

    private static Dictionary<string, object?> StampJson(VersionStamp s) => new()
    {
        ["pipeline"] = s.Pipeline, ["rules"] = s.Rules,
        ["prompts"] = s.Prompts.ToDictionary(p => p.Key, p => (object?)new Dictionary<string, object?> { ["version"] = p.Value.Version, ["digest"] = p.Value.Digest }),
        ["code"] = s.Code, ["settings"] = s.Settings, ["readings"] = s.Readings,
    };
}
