using System.Diagnostics;
using System.Text;

namespace Auditor.Tools;

/// <summary>
/// Mutation check: break one rule at a time in a COPY of the code and show that the tests notice. For each mutation: copy `code/` to a scratch folder (the repository is never touched), apply
/// one textual edit that breaks one rule, run the invariant, golden-category, synthetic-case, sample-regression and validator tests, and list the tests that fail. A mutation that no test
/// notices ("SURVIVED") makes the exit code 1. A control run on the unmodified copy must be green, otherwise failures could not be attributed to the mutation.
/// </summary>
public static class MutationCheck
{
    public sealed record Mutation(string Key, string Rule, string File, string Old, string New, bool Primary = true);

    public const string TestFilter = "FullyQualifiedName~InvariantsTests|FullyQualifiedName~GoldenCategoriesTests|FullyQualifiedName~ExtraCasesTests|FullyQualifiedName~SampleRegressionGuard|FullyQualifiedName~ValidateTests";
    public static readonly string[] TestClasses = { "InvariantsTests", "GoldenCategoriesTests", "ExtraCasesTests", "SampleRegressionGuard", "ValidateTests" };

    public static readonly Mutation[] Mutations =
    {
        new("M1 canonical order", "inside REMEDIATION, flows must be disabled and restores blocked BEFORE records are deleted (spec 6.5; canonical operation order of problem_statement.md): DELETE_RECORD is moved ahead of DISABLE_DATA_FLOW in the shared operation table, so the pipeline and its own validator still agree with each other",
            "code/src/Auditor/Schema.cs",
            "        (\"DISABLE_DATA_FLOW\", \"REMEDIATION\", \"DISABLED\"),\n        (\"BLOCK_RESTORE\", \"REMEDIATION\", \"BLOCKED\"),\n        (\"DELETE_RECORD\", \"REMEDIATION\", \"ABSENT\"),\n",
            "        (\"DELETE_RECORD\", \"REMEDIATION\", \"ABSENT\"),\n        (\"DISABLE_DATA_FLOW\", \"REMEDIATION\", \"DISABLED\"),\n        (\"BLOCK_RESTORE\", \"REMEDIATION\", \"BLOCKED\"),\n"),
        new("M2 COMPLETE rows cite evidence", "every step of a COMPLETE row must carry an evidence id: verification steps of compliant records stop citing their file",
            "code/src/Auditor/Audit.cs", "st.System, st.Resource, citeOk ? st.FileId : \"\"),", "st.System, st.Resource, \"\"),"),
        new("M3 NOT_APPLICABLE equals []", "a request that does not authorize deletion must produce steps_json == []: the non-deletion early return now emits a step",
            "code/src/Auditor/Audit.cs",
            "return new CaseResult(caseId, \"NOT_APPLICABLE\", new(), new() { $\"not a deletion request:",
            "return new CaseResult(caseId, \"NOT_APPLICABLE\", new() { Schema.MakeStep(\"VERIFICATION\", \"VERIFY_RECORD_ABSENT\", \"account_service\", \"account_0\", \"\") }, new() { $\"not a deletion request:"),
        new("M4 policy table: REDACT", "REDACT categories (support message, delivery log, tombstone) must be fixed by REDACT_RECORD: the table now says ANONYMIZE_RECORD",
            "code/src/Auditor/Policy.cs", "[\"REDACT\"] = new(\"REPORT_EXPOSED_PII\", \"REDACT_RECORD\",", "[\"REDACT\"] = new(\"REPORT_EXPOSED_PII\", \"ANONYMIZE_RECORD\",", false),
        new("M5 legal hold expiry", "a legal hold that ended before audit_at is no longer a retain requirement: the expiry check is removed",
            "code/src/Auditor/Audit.cs", "if (st.Until is { } until && until <= auditAt)", "if (false && st.Until is { } until && until <= auditAt)", false),
    };

    private static void CopyCode(string root, string dst)
    {
        var ignore = new HashSet<string>(StringComparer.Ordinal) { "bin", "obj", "out", ".cache", "TestResults" };
        void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(from))
                if (!ignore.Contains(Path.GetFileName(d))) Copy(d, Path.Combine(to, Path.GetFileName(d)));
        }
        Copy(Path.Combine(root, "code"), Path.Combine(dst, "code"));
        Directory.CreateSymbolicLink(Path.Combine(dst, "dataset"), Path.Combine(root, "dataset"));
        foreach (var name in new[] { "README.md", "output.csv", "AGENTS.md" })
            if (File.Exists(Path.Combine(root, name))) File.Copy(Path.Combine(root, name), Path.Combine(dst, name));
    }

    private static void Apply(Mutation m, string repo)
    {
        var p = Path.Combine(repo, m.File);
        var text = File.ReadAllText(p);
        int count = text.Split(m.Old).Length - 1;
        if (count != 1) throw new InvalidOperationException($"{m.Key}: the text to mutate occurs {count} times in {m.File} (must be exactly once); update the mutation definition");
        File.WriteAllText(p, text.Replace(m.Old, m.New, StringComparison.Ordinal), new UTF8Encoding(false));
    }

    private static (int Run, int Code, List<string> Failing) RunTests(string repo)
    {
        var trx = Path.Combine(repo, "mutation.trx");
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "test", Path.Combine(repo, "code", "tests", "Auditor.Tests"), "--filter", TestFilter, "--nologo", "--logger", $"trx;LogFileName={trx}" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (!File.Exists(trx)) return (0, p.ExitCode == 0 ? 1 : p.ExitCode, new List<string> { "(the test project did not build or run: " + (o.Result + e.Result).Split('\n').FirstOrDefault(l => l.Contains("error", StringComparison.Ordinal))?.Trim() + ")" });
        var summary = TrxSummary.TryRead(trx)!;
        return (summary.Total, p.ExitCode, TrxSummary.FailedTests(trx));
    }

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var root = Cli.Cli.RepoRoot();
        bool write = args.Contains("--write");
        int oi = Array.IndexOf(args, "--only");
        var only = oi >= 0 ? args.Skip(oi + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList() : new List<string>();
        var chosen = Mutations.Where(m => only.Count == 0 || only.Any(k => m.Key.StartsWith(k, StringComparison.Ordinal))).ToList();
        var results = new List<(Mutation M, int N, List<string> Failing)>();
        var scratch = Directory.CreateTempSubdirectory("mutation_").FullName;
        try
        {
            var control = Path.Combine(scratch, "control");
            CopyCode(root, control);
            var (controlN, rc, controlFailing) = RunTests(control);
            stdout.WriteLine($"control (no mutation): {controlN} tests, exit {rc}, failing {(controlFailing.Count > 0 ? string.Join(", ", controlFailing) : "none")}");
            if (rc != 0) { stdout.WriteLine("the control run must be green"); return 2; }
            foreach (var m in chosen)
            {
                var repo = Path.Combine(scratch, m.Key.Split(' ')[0]);
                CopyCode(root, repo);
                Apply(m, repo);
                var (n, _, failing) = RunTests(repo);
                results.Add((m, n, failing));
                stdout.WriteLine($"{m.Key}: {failing.Count} failing test(s) of {n} -> {(failing.Count > 0 ? "KILLED" : "SURVIVED")}");
                foreach (var f in failing.Take(8)) stdout.WriteLine("     " + f);
            }
            var lines = new List<string>
            {
                "# Mutation check (generated by `dotnet run --project code/tools/Auditor.Tools -- mutation-check --write`)", "",
                $"Each row breaks ONE rule in a scratch copy of the code (the repository is not touched) and runs the invariant tests, the per-category golden tests, the synthetic cases, the sample regression guard and the validator tests ({string.Join(", ", TestClasses)}). Control: the unmodified copy passes all {controlN} of those tests, so every failure below is caused by the mutation. M1-M3 are the three requested mutations; M4-M5 are extra.", "",
                "| mutation | broken rule | edited file | tests run | failing tests | verdict |", "|---|---|---|---|---|---|",
            };
            lines.AddRange(results.Select(r => $"| {r.M.Key}{(r.M.Primary ? "" : " (extra)")} | {r.M.Rule} | `{r.M.File}` | {r.N} | {r.Failing.Count} | **{(r.Failing.Count > 0 ? "KILLED" : "SURVIVED")}** |"));
            foreach (var (m, _, failing) in results)
            {
                lines.AddRange(new[] { "", $"### {m.Key}", "", $"Edit in `{m.File}`:", "", "```diff" });
                lines.AddRange(m.Old.TrimEnd('\n').Split('\n').Select(l => "- " + l));
                lines.AddRange(m.New.TrimEnd('\n').Split('\n').Select(l => "+ " + l));
                lines.AddRange(new[] { "```", "", $"Failing tests ({failing.Count}):", "" });
                lines.AddRange(failing.Count > 0 ? failing.Select(f => $"* `{f}`") : new[] { "* (none: the mutation SURVIVED)" });
            }
            if (write)
            {
                File.WriteAllText(Path.Combine(root, "code", "evaluation", "mutation_check.md"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
                stdout.WriteLine("wrote code/evaluation/mutation_check.md");
            }
        }
        finally { try { Directory.Delete(scratch, true); } catch (IOException) { } }
        return results.All(r => r.Failing.Count > 0) ? 0 : 1;
    }
}
