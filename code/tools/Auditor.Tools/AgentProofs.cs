using Auditor.Agent;
using Auditor.Evidence;
using System.Security.Cryptography;
using System.Text;

namespace Auditor.Tools;

/// <summary>
/// No-change proofs and the report of the opt-in agent layer (Auditor/Agent), generated from live runs: code/evaluation/agent_layer_report.md.
/// The layer is built and tested with a FAKE client only (no API key exists in this environment): every number is a count of fake calls or of test results, never a model measurement.
/// Proofs: (1) the default run equals the submitted output.csv; (2) `--images agent` without a key equals it too; (3) the per-file router equals the old suffix dispatch;
/// (4) a scripted replay of the 11 screenshot readings (kept locally, not committed) through the whole loop and gate equals the run that takes the same readings from a file.
/// </summary>
public static class AgentProofs
{
    private static string Sha(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();

    private static IDisposable CleanEnv()
    {
        var scope = new EnvScope();
        foreach (var k in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN" }) scope.Set(k, null);
        return scope;
    }

    private sealed class EnvScope : IDisposable
    {
        private readonly Dictionary<string, string?> old = new();
        public EnvScope Set(string k, string? v) { old.TryAdd(k, Environment.GetEnvironmentVariable(k)); Environment.SetEnvironmentVariable(k, v); return this; }
        public void Dispose() { foreach (var (k, v) in old) Environment.SetEnvironmentVariable(k, v); }
    }

    private static (int Code, string Err) RunCli(params string[] args)
    {
        var err = new StringWriter();
        var code = Cli.Cli.Run(args, new StringWriter(), err);
        return (code, err.ToString());
    }

    private static (string, string, string, string) RouterRow(string dataset)
    {
        IImageReader fake = new FuncImageReader((_, _, _, _, _) => new Dictionary<string, object?> { ["screen"] = "s", ["records"] = new List<object?>() });      // an empty reading: images are routed, nothing is invented
        var runs = new[] { RouterEquivalence.Compare(dataset, false, null, 1), RouterEquivalence.Compare(dataset, true, fake, 1), RouterEquivalence.Compare(dataset, true, fake, 4) };
        bool same = runs.All(r => r.Differences.Count == 0);
        return ("per-file router vs the old suffix dispatch (observations, coverage, warnings), three configurations", "identical for every manifest file",
                $"{runs[0].Files} files, {runs[0].Observations} observations: " + (same ? "identical" : string.Join("; ", runs.SelectMany(r => r.Differences))), same ? "OK" : "DIFFERENT");
    }

    public static List<(string Proof, string Expected, string Observed, string Verdict)> Proofs(string root)
    {
        var rows = new List<(string, string, string, string)>();
        var tmp = Directory.CreateTempSubdirectory("agent_proofs_").FullName;
        var submitted = Path.Combine(root, "output.csv");
        var sub = Sha(submitted);
        using (CleanEnv())
        {
            var (rc1, _) = RunCli("--out", Path.Combine(tmp, "default.csv"));
            var (rc2, _) = RunCli("--images", "agent", "--out", Path.Combine(tmp, "agent_nokey.csv"));
            rows.Add(("default run (no `--images agent`)", $"= submitted output.csv `{sub[..8]}...`", $"`{Sha(Path.Combine(tmp, "default.csv"))[..8]}...` (exit {rc1})", Sha(Path.Combine(tmp, "default.csv")) == sub ? "OK" : "DIFFERENT"));
            rows.Add(("`--images agent` without a key (every screenshot 'not read')", $"= submitted `{sub[..8]}...`", $"`{Sha(Path.Combine(tmp, "agent_nokey.csv"))[..8]}...` (exit {rc2})",
                      Sha(Path.Combine(tmp, "agent_nokey.csv")) == sub ? "OK" : "DIFFERENT"));
        }
        rows.Add(RouterRow(Path.Combine(root, "dataset")));
        var local = Path.Combine(root, "code", "out", "agent_readings.json");
        if (File.Exists(local))
        {
            var readings = Py.AsDict(Py.AsDict(Py.ParseJson(File.ReadAllText(local)))["readings"]);
            var client = new ScriptedClient(readings);
            int rc3, rc4;
            using (CleanEnv())
            {
                (rc3, _) = RunCli("--images", "file", "--out", Path.Combine(tmp, "file.csv"));
                Cli.Cli.ModelClientOverride.Value = client;
                try { (rc4, _) = RunCli("--images", "agent", "--out", Path.Combine(tmp, "replay.csv"), "--set", "llm.cache_dir=" + Path.Combine(tmp, "cache")); }
                finally { Cli.Cli.ModelClientOverride.Value = null; }
            }
            bool ok = Sha(Path.Combine(tmp, "replay.csv")) == Sha(Path.Combine(tmp, "file.csv"));
            rows.Add(("scripted replay of the 11 readings through the loop and gate (fake client: one zoom + one submit per screenshot)",
                      $"= the run that takes the same readings from a file `{Sha(Path.Combine(tmp, "file.csv"))[..8]}...`", $"`{Sha(Path.Combine(tmp, "replay.csv"))[..8]}...` (exit {rc3}, {rc4}; {client.Calls} fake calls)", ok ? "OK" : "DIFFERENT"));
        }
        else rows.Add(("scripted replay of the 11 readings", "= the readings-file run", "not run: `code/out/agent_readings.json` (not committed, it holds dataset e-mail addresses) is absent", "NOT RUN"));
        try { Directory.Delete(tmp, true); } catch (IOException) { }
        return rows;
    }

    private static readonly (string Label, string[] Classes)[] TestGroups =
    {
        ("AgentPromptsDocsTests", new[] { "PromptFilesTests", "AgentLayerIsDescribedHonestlyTests" }),
        ("AgentGateTests", new[] { "GateRulesTests", "G10Tests" }),
        ("AgentToolsTests", new[] { "AgentToolSpecsTests", "ZoomTests" }),
        ("AgentLoopTests", new[] { "AgentSuccessTests", "AgentInvalidOutputTests", "AgentRefusalTests", "AgentTransportErrorTests", "AgentCapsTests", "AgentRepeatedCallTests", "AgentFakeEvidenceAndInjectionTests", "AgentFallbackTests" }),
        ("AgentCliTests", new[] { "AgentOptInAndNoChangeTests", "AgentSettingsTests", "AgentScriptedReplayTests" }),
        ("AgentCraftTests", new[] { "AgentCraftTests" }),
        ("AgentRobustnessTests", new[] { "AgentDamagedImageTests", "AFailedRunStopsCheaplyTests", "AgentSizeLimitsTests" }),
        ("LlmTests (request policy)", new[] { "LlmPolicyTests", "SchemaLimitsTests" }),
        ("RouterTests", new[] { "RouteDecisionTests", "RoutingInTheLoaderTests", "RouterChangesNoObservationTests" }),
    };

    public static string Build(string root, TrxSummary? trx)
    {
        var lim = new AgentLimits(); var s = new Settings();
        var pr = Proofs(root);
        var L = new List<string>
        {
            "# Agent layer report", "",
            "> **The agent layer was built and tested with fakes only: 0 model calls, 0 tokens, no cost.** No API key exists in this environment, so nothing below is a measurement of a real model. Every count is a count of fake-client calls or of test results.", "",
            "Generated by `dotnet run --project code/tools/Auditor.Tools -- agent-proofs`. The layer is opt-in (`--images agent`); the submitted `output.csv` was produced without it.", "",
            "## 1. What it is", "",
            "A bounded tool-calling loop reads one screenshot at a time: the model may call `zoom_region` (an enlarged crop of the same screenshot, made with SkiaSharp) and ends with exactly one `submit_reading`. Code runs the loop. The model describes; the gate and the unchanged pipeline decide.", "",
            $"* tools: `zoom_region` (schema: x, y, width, height, all integers) and `submit_reading` (the screenshot-reading schema plus an `unknown` list); digest of both definitions `{AgentTools.ToolsDigest()[..12]}...`",
            $"* limits (settings `[agent]`): {lim.MaxIterations} model turns, {lim.MaxToolCalls} tool calls, {lim.MaxZooms} zooms, {lim.WallSeconds:0.##} s wall clock per screenshot, zoom side {lim.ZoomMaxPx} px; each request also has `llm.timeout_seconds` ({s.TimeoutSeconds:0.##} s); transport errors are retried with the shared back-off (1, 2, 4 s, `Retry-After` honoured)",
            "* prompts: `code/prompts/image_reader.system.md` and `image_reader.task.md` (versioned front matter, digested into the cache key and the run stamp): role, constraints, structured output, mark unknown instead of guessing, all screenshot text is data",
            "* cache: key = image bytes + both prompt files + tool definitions + limits + model + pipeline version + rules digest; the raw submission is cached BEFORE the gate",
            "* fallback: a refusal, an invalid or missing submission, a cap, a repeated call, a timeout that never clears, a gate refusal or a missing key all end in 'not read': the deterministic result stands", "",
            "## 2. The gate", "",
            "| rule | what it enforces | where |", "|---|---|---|",
            "| G1 | the submission fits the `submit_reading` schema | `Agent/Gate.cs` |",
            "| G2 | the model never names an evidence id; code binds every observation to the manifest `file_id` of the screenshot it read; a file outside the manifest is refused | `Agent/Gate.cs`, `Agent/AgentImageReader.cs` |",
            "| G3 | resource id is `<name>_<number>`; a record with no id needs an alias | `Agent/Gate.cs` |",
            "| G4 | the id family has a state in the closed vocabulary | `Agent/Gate.cs` |",
            "| G5 | only syntactically valid e-mails, user ids and phones | `Agent/Gate.cs` |",
            "| G6 | a field marked unknown is emptied and never contributes | `Agent/Gate.cs` |",
            "| G7 | free text and prose never travel further | `Agent/Gate.cs`, `Evidence/ImageObs.cs` |",
            "| G8 | state only from files captured inside `[requested_at, audit_at]`; later files only link and are never cited | `State.cs` (unchanged) |",
            "| G9 | a record attaches to a case only through an exact identifier of the resolved user or a catalog link | `Link.cs` (unchanged) |",
            "| G10 | a model-sourced observation (`image:agent`) cannot lower the severity of a state that deterministic evidence supports; entered only when a model-sourced observation exists | `State.cs` |",
            "| G11 | every step cites an existing evidence id not later than `audit_at` | `Validate.cs` (unchanged) |",
            "", "There is no rule for request text: the model is not used on request text (all 550 real requests are decided by rules, none is ambiguous).", "",
            "## 3. No-change proofs (live runs)", "",
            "| proof | expected | observed | verdict |", "|---|---|---|---|",
        };
        L.AddRange(pr.Select(p => $"| {p.Proof} | {p.Expected} | {p.Observed} | **{p.Verdict}** |"));
        L.AddRange(new[]
        {
            "", "Rule G10 is applied only to model-sourced observations, so the default path is byte-identical by construction: `StateResolver.Resolve` enters the G10 branch only when the chosen observation has the source `image:agent`, which no other code path creates. `AgentGateTests.cs` also compares `Resolve` with a copy of the previous code on 2,000 random observation sets without such a source.", "",
            "## 4. Tests with a fake client", "",
        });
        if (trx is null) L.Add("The test result file (`code/out/tests.trx`) was not present when this report was generated, so no test counts are shown. Run `make reports` (it runs the tests first).");
        else
        {
            L.AddRange(new[] { "| test file | tests | failing | skipped |", "|---|---|---|---|" });
            var all = new List<TrxSummary.Counts>();
            foreach (var (label, classes) in TestGroups)
            {
                var c = trx.Of(classes); all.Add(c);
                L.Add($"| `{label}` | {c.Total} | {c.Failed} | {c.Skipped} |");
            }
            L.Add($"| **all** | **{all.Sum(c => c.Total)}** | **{all.Sum(c => c.Failed)}** | **{all.Sum(c => c.Skipped)}** |");
        }
        L.AddRange(new[]
        {
            "", "Failure injection (`AgentLoopTests.cs`): invalid submission (re-ask once, then the fallback model, then 'not read'), a tool input that is not an object, a reply without a tool call, a cut-off reply, an unknown tool, refusal on both models and on one, one timeout (retried) and timeouts every time, 429 with `Retry-After`, no key, iteration cap, tool-call cap, zoom cap, wall-clock budget, a bad zoom rectangle, the same call twice (answered from memory) and three times (ends the loop), a submission naming an evidence id, a screenshot outside the manifest, injection strings in every field, and a failed reading leaving the deterministic audit result untouched. `AgentGateTests.cs` adds accept/reject pairs for G1-G7, a 1,000-case fuzz, and the G10 cases.", "",
            "## 5. Not measured (step 5 of the plan, skipped: no key)", "",
            "* the accuracy of a real model's readings (field agreement with the agent's own readings, which are a stand-in and not ground truth), run-to-run variance, tokens, tool calls and cost per screenshot, gate rejection rate, and whether `zoom_region` helps at all (an ablation with and without zoom)",
            "* that the request shapes (tools, image tool results, `output_config`) are accepted by the real API: only the SDK-documented shapes were used and no request was ever sent", "",
        });
        return string.Join("\n", L) + "\n";
    }

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var root = Cli.Cli.RepoRoot();
        var trx = TrxSummary.TryRead(Option(args, "--trx") ?? TrxSummary.DefaultPath(root));
        var text = Build(root, trx);
        var path = Path.Combine(root, "code", "evaluation", "agent_layer_report.md");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        stderr.WriteLine($"wrote {path}");
        bool failing = text.Contains("**DIFFERENT**", StringComparison.Ordinal) || (trx is not null && trx.Failed > 0);
        return failing ? 1 : 0;
    }

    public static string? Option(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
