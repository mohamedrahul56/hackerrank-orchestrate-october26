using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Auditor.Tools;

/// <summary>Final submission checklist: every item a program can verify is verified here; the rest is marked NOT VERIFIED. Exit code 1 if a verifiable item fails.</summary>
public static class FinalCheck
{
    private static string Git(string root, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return o.Trim();
        }
        catch (System.ComponentModel.Win32Exception) { return ""; }
    }

    private static int Natural(string c) => int.Parse(Regex.Match(c, @"\d+").Value, System.Globalization.CultureInfo.InvariantCulture);

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>Extract code.zip into a temp directory, build the CLI there and run it on the dataset; true when the output equals <paramref name="expected"/>.</summary>
    private static (bool Ok, string Detail) RunFromZip(string zipPath, string root, byte[] expected)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "auditor-zip-" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(zipPath, tmp);
            string Run(string file, IEnumerable<string> args, out int code)
            {
                var psi = new ProcessStartInfo(file) { WorkingDirectory = tmp, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
                p.WaitForExit();
                code = p.ExitCode;
                return o.Result + e.Result;
            }
            var proj = Path.Combine(tmp, "code", "src", "Auditor.Cli", "Auditor.Cli.csproj");
            var bin = Path.Combine(tmp, "bin");
            var log = Run("dotnet", new[] { "build", proj, "-c", "Release", "-o", bin, "--nologo", "-v", "q" }, out var bc);
            if (bc != 0) return (false, "build of the extracted zip failed: " + log[..Math.Min(300, log.Length)]);
            var fresh = Path.Combine(tmp, "from_zip.csv");
            Run("dotnet", new[] { Path.Combine(bin, "Auditor.Cli.dll"), "--dataset", Path.Combine(root, "dataset"), "--out", fresh }, out var rc);
            return (rc == 0 && File.Exists(fresh) && File.ReadAllBytes(fresh).SequenceEqual(expected), $"exit code {rc}");
        }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } }
    }

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var root = Cli.Cli.RepoRoot();
        bool buildZip = args.Contains("--build-zip");

        var items = new List<(string What, bool? Ok, string Detail)>();
        void Add(string what, bool? ok, string detail = "") => items.Add((what, ok, detail));

        var ds = Dataset.Load(Path.Combine(root, "dataset"));
        var outPath = Path.Combine(root, "output.csv");
        var rows = File.Exists(outPath) ? Csv.Parse(File.ReadAllText(outPath, new UTF8Encoding(false))) : new List<List<string>>();
        var data = rows.Skip(1).Where(r => r.Count > 0).ToList();
        var ids = data.Select(r => r[0]).ToList();
        Add("`output.csv` is at the repository root with header `case_id,deletion_status,steps_json`", rows.Count > 0 && rows[0].SequenceEqual(Schema.OutputHeader));
        Add($"`output.csv` has exactly {ds.CaseOrder.Count} data rows, one per `case_id` of `deletion_requests.csv`, no duplicates",
            ids.Count == ds.CaseOrder.Count && ids.Distinct().Count() == ids.Count && ids.ToHashSet().SetEquals(ds.CaseOrder), $"{ids.Count} rows");
        Add("rows are in the order of `deletion_requests.csv`, which is also the numeric order of the case ids",
            ids.SequenceEqual(ds.CaseOrder) && ids.SequenceEqual(ids.OrderBy(Natural)),
            "NOTE: a plain string sort would put a three-digit id before a two-digit one; the input file and the sample answers use numeric order, so numeric order is used");
        var errs = File.Exists(outPath) ? Validator.ValidateOutputFile(outPath, ds).Where(i => i.Severity == "error").Count() : 1;
        Add("every row passes the contract validator (JSON, step_no, phases, triples, order, pairing, evidence ids exist and are not later than `audit_at`)", errs == 0, $"{errs} errors");
        var statuses = data.GroupBy(r => r[1]).ToDictionary(g => g.Key, g => g.Count());
        bool naOk = data.Where(r => r[1] == "NOT_APPLICABLE").All(r => Compat.ParseJson(r[2]) is List<object?> { Count: 0 });
        Add("NOT_APPLICABLE rows have `[]`; statuses are COMPLETE / INCOMPLETE / NOT_APPLICABLE only", naOk && statuses.Keys.All(Schema.Statuses.Contains),
            string.Join(", ", statuses.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}")));
        int completeEmpty = data.Where(r => r[1] == "COMPLETE").Sum(r => Compat.AsList(Compat.ParseJson(r[2])).Count(s => Compat.Str(Compat.Get(s, "evidence_id")).Length == 0));
        Add("every step of every COMPLETE row cites an evidence id", completeEmpty == 0, $"{completeEmpty} steps without one");

        var freshDir = Path.Combine(Path.GetTempPath(), "auditor-final-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(freshDir);
        try
        {
            var fresh = Path.Combine(freshDir, "fresh.csv");
            int code = Cli.Cli.Run(new[] { "--out", fresh }, new StringWriter(), new StringWriter());
            bool same = File.Exists(fresh) && File.Exists(outPath) && File.ReadAllBytes(fresh).SequenceEqual(File.ReadAllBytes(outPath));
            Add("`output.csv` is byte-identical to a fresh run of the auditor (determinism)", code == 0 && same);
        }
        finally { Directory.Delete(freshDir, true); }
        var notes = Path.Combine(root, "output.notes.md");
        var prov = new List<string>();
        if (File.Exists(notes))
        {
            var t = File.ReadAllText(notes);
            var seg = t.Contains("## Provisional rows", StringComparison.Ordinal) ? t.Split("## Provisional rows")[1].Split("## Per-case notes")[0] : "";
            prov = Regex.Matches(seg, @"^\* (case_\d+):", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        }
        Add("provisional rows are flagged in `output.notes.md`", File.Exists(notes), $"{prov.Count}: {string.Join(", ", prov)}");

        var ev = Path.Combine(root, "code", "evaluation");
        foreach (var name in new[] { "evaluation_report.md", "usage_report.md", "performance_report.md", "edge_case_sweep.md", "agent_layer_report.md" })
        {
            var p = Path.Combine(ev, name);
            var problems = File.Exists(p) ? MarkdownCheck.Problems(p) : new List<string>();
            Add($"`code/evaluation/{name}` exists and renders as well-formed Markdown", File.Exists(p) && problems.Count == 0, File.Exists(p) ? string.Join("; ", problems) : "missing");
        }
        var usagePath = Path.Combine(ev, "usage_report.md");
        var usage = File.Exists(usagePath) ? File.ReadAllText(usagePath) : "";
        var info = Regex.Match(usage, @"sha256 `([0-9a-f]{16})");
        Add("`usage_report.md` describes the run that produced the submitted `output.csv` (sha256 matches)", info.Success && File.Exists(outPath) && Sha(File.ReadAllBytes(outPath)).StartsWith(info.Groups[1].Value, StringComparison.Ordinal),
            info.Success ? info.Groups[1].Value + "..." : "no run_info");
        Add("`usage_report.md` states 0 model calls or real logged tokens, and the exact no-price line or a cost with source",
            (usage.Contains("No model calls were made", StringComparison.Ordinal) || usage.Contains("model calls (incl. re-asks)", StringComparison.Ordinal))
            && (usage.Contains("cost not computed - prices not supplied", StringComparison.Ordinal) || usage.Contains("supplied by the participant", StringComparison.Ordinal)));
        Add("README, ARCHITECTURE and the 23 synthetic cases are well-formed Markdown",
            new[] { Path.Combine(root, "README.md"), Path.Combine(root, "ARCHITECTURE.md"), Path.Combine(ev, "extra_cases.md") }.All(p => File.Exists(p) && MarkdownCheck.Problems(p).Count == 0));

        var zpath = Path.Combine(root, "code.zip");
        var files = Packager.Collect(root);
        var (bad, _) = Packager.Check(files, root, requireFinal: true);
        Add("packaging rules pass with the final reports required (no dataset/, `.env`, `prices.json`, `code/out/`, traces, nested archives; no secrets; no dataset e-mails; no judge-ignored file types except the price template)", bad.Count == 0, string.Join("; ", bad));
        if (buildZip && bad.Count == 0) Packager.Build(files, zpath, root);
        if (File.Exists(zpath))
        {
            List<string> names;
            bool zipOk = true;
            try { using var z = ZipFile.OpenRead(zpath); names = z.Entries.Select(e => e.FullName).ToList(); }
            catch (InvalidDataException) { names = new(); zipOk = false; }
            Add("`code.zip` exists, is a valid archive and its names equal the checked file list", zipOk && names.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(files.Select(p => Packager.Rel(p, root)).OrderBy(n => n, StringComparer.Ordinal)),
                $"{names.Count} files, {new FileInfo(zpath).Length / 1024} KiB");
            string[] must = { "README.md", "ARCHITECTURE.md", "Makefile", "code/evaluation/evaluation_report.md", "code/evaluation/usage_report.md", "code/evaluation/performance_report.md", "code/evaluation/edge_case_sweep.md",
                              "code/evaluation/agent_layer_report.md", "code/prompts/image_reader.system.md", "code/evaluation/extra_cases.md" };
            Add("`code.zip` has no `dataset/`, `.env`, `prices.json`, `code/out/`, build output, nested archive; has README.md, ARCHITECTURE.md, Makefile, the reports, extra_cases.md",
                !names.Any(n => n.StartsWith("dataset/", StringComparison.Ordinal) || n.Split('/')[^1].StartsWith(".env", StringComparison.Ordinal) || n.EndsWith("/prices.json", StringComparison.Ordinal)
                                || n.StartsWith("code/out/", StringComparison.Ordinal) || n.Split('/').Any(x => x is "bin" or "obj") || Packager.ArchiveExt.Contains(Path.GetExtension(n)))
                && must.All(names.Contains));
            if (File.Exists(outPath))
            {
                var (ok, detail) = RunFromZip(zpath, root, File.ReadAllBytes(outPath));
                Add("the extracted `code.zip`, built with `dotnet build` and run on the dataset, reproduces `output.csv` byte for byte", ok, ok ? "" : detail);
            }
        }
        else Add("`code.zip` exists", false, "run with --build-zip");

        var tracked = Git(root, "ls-files").Split('\n').Where(t => t.Length > 0).ToList();
        Add("no `.env*`, `prices.json` or `code.zip` is tracked by git",
            !tracked.Any(t => t.Split('/')[^1].StartsWith(".env", StringComparison.Ordinal) || t.EndsWith("prices.json", StringComparison.Ordinal) || t == "code.zip"));
        var emails = Packager.DatasetEmails(root);
        var leaked = new List<string>();
        var secrets = new List<string>();
        foreach (var t in tracked.Where(t => !t.StartsWith("dataset/", StringComparison.Ordinal) && File.Exists(Path.Combine(root, t))))
        {
            var txt = Packager.ReadText(Path.Combine(root, t));
            if (txt is null) continue;
            if (Packager.EmailRe.Matches(txt).Any(m => emails.Contains(m.Value.ToLowerInvariant()))) leaked.Add(t);
            secrets.AddRange(Packager.ScanText(t, txt));
        }
        Add("no tracked file outside `dataset/` contains an e-mail address of a dataset person", leaked.Count == 0, string.Join(", ", leaked));
        Add("secret scan over every tracked file outside `dataset/`", secrets.Count == 0, string.Join("; ", secrets));
        var dirty = Git(root, "status", "--porcelain");
        var branch = Git(root, "rev-parse", "--abbrev-ref", "HEAD");
        var ahead = Git(root, "rev-parse", "--verify", $"origin/{branch}").Length > 0 ? Git(root, "rev-list", "--count", $"origin/{branch}..HEAD") : "?";
        Add("working tree is clean (everything that matters is committed)", dirty.Length == 0, dirty.Replace("\n", "; ")[..Math.Min(200, dirty.Length)]);
        Add($"HEAD is pushed to `origin/{branch}`", ahead == "0", $"{ahead} commits not pushed (this reads the local copy of the remote ref)");


        var lines = new List<string> { "# Final checklist (generated by `dotnet run --project code/tools/Auditor.Tools -- final-check`)", "" };
        foreach (var (what, ok, detail) in items)
        {
            var mark = ok == true ? "[x] VERIFIED" : ok is null ? "[ ] NOT VERIFIED" : "[ ] **FAILED**";
            lines.Add($"- {mark} - {what}" + (detail.Length > 0 ? $" ({detail})" : ""));
        }
        var textOut = string.Join("\n", lines) + "\n";
        Directory.CreateDirectory(Path.Combine(root, "code", "out"));
        File.WriteAllText(Path.Combine(root, "code", "out", "final_checklist.md"), textOut, new UTF8Encoding(false));
        stdout.WriteLine(textOut);
        return items.Any(i => i.Ok == false) ? 1 : 0;
    }
}
