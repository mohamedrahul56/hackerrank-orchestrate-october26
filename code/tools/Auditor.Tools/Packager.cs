using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Auditor.Tools;

/// <summary>
/// Builds (or dry-runs) code.zip and checks every submission rule before anything is uploaded (AGENTS.md 6.9):
/// no dataset/, no .env, no build output or caches, no code/out/, no agent traces, no nested archives, no local prices file;
/// nothing that looks like a secret in anything zipped, in log.txt or in prices.json; no e-mail address of a dataset person in any zipped file;
/// no file type the code judge ignores (.csv .tsv .json .jsonl .sql .log) except the price-file template; deterministic output (sorted entries, fixed timestamps).
/// </summary>
public static class Packager
{
    public static readonly HashSet<string> ArchiveExt = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".7z", ".rar", ".jar", ".whl", ".nupkg" };
    public static readonly HashSet<string> JudgeIgnoredExt = new(StringComparer.OrdinalIgnoreCase) { ".csv", ".tsv", ".json", ".jsonl", ".sql", ".log" };
    public static readonly HashSet<string> AllowedIgnored = new(StringComparer.Ordinal) { "code/config/prices.example.json" };
    public static readonly HashSet<string> ExcludedDirs = new(StringComparer.Ordinal) { ".git", ".vs", ".idea", ".vscode", "bin", "obj", "TestResults", "node_modules", ".cache" };
    public static readonly string[] ExcludedPrefixes = { "code/out/", "code/.cache/", "dataset/", "agent-trace", "output" };
    public static readonly HashSet<string> ExcludedNames = new(StringComparer.Ordinal) { ".env", "prices.json", "log.txt", ".DS_Store" };
    public static readonly string[] Required =
    {
        "README.md", "ARCHITECTURE.md", "Makefile", "code/Auditor.sln", "code/src/Auditor/Auditor.csproj", "code/src/Auditor.Cli/Program.cs",
        "code/evaluation/extra_cases.md",
    };
    public static readonly string[] FinalRequired =
    {
        "code/evaluation/evaluation_report.md", "code/evaluation/usage_report.md", "code/evaluation/performance_report.md", "code/evaluation/agent_layer_report.md",
    };
    public const long MaxFileBytes = 5_000_000;

    public static readonly (string Label, Regex Rx)[] SecretPatterns =
    {
        ("Anthropic key", new Regex(@"sk-ant-[A-Za-z0-9_\-]{10,}", RegexOptions.CultureInvariant)),
        ("OpenAI-style key", new Regex(@"\bsk-[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant)),
        ("AWS access key", new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)),
        ("GitHub token", new Regex(@"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}\b|github_pat_[A-Za-z0-9_]{30,}", RegexOptions.CultureInvariant)),
        ("Slack token", new Regex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}", RegexOptions.CultureInvariant)),
        ("private key", new Regex(@"-----BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----", RegexOptions.CultureInvariant)),
        ("JWT", new Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", RegexOptions.CultureInvariant)),
        ("key-like assignment", new Regex(@"(?i)\b(?:api[_-]?key|secret|passwd|password|auth[_-]?token|access[_-]?token)\b[""']?\s*[:=]\s*[""'][A-Za-z0-9_\-/+=]{20,}[""']", RegexOptions.CultureInvariant)),
    };

    public static readonly Regex EmailRe = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant);
    public static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".props", ".md", ".txt", ".csv", ".json", ".sql", ".log", ".toml", ".cfg", ".ini", ".yml", ".yaml", ".sh", ".jsonl", ".slnx", "",
    };

    public static string Rel(string path, string root) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>README.md, ARCHITECTURE.md, Makefile plus everything under code/ that is not excluded, sorted by relative path (ordinal).</summary>
    public static List<string> Collect(string root)
    {
        var found = new List<string>();
        foreach (var name in new[] { "README.md", "ARCHITECTURE.md", "Makefile" })
        {
            var p = Path.Combine(root, name);
            if (File.Exists(p)) found.Add(p);
        }
        var code = Path.Combine(root, "code");
        if (Directory.Exists(code)) found.AddRange(Directory.EnumerateFiles(code, "*", SearchOption.AllDirectories));
        var kept = new List<string>();
        foreach (var p in found)
        {
            var r = Rel(p, root);
            var parts = r.Split('/');
            var name = parts[^1];
            if (parts.Any(ExcludedDirs.Contains) || ExcludedNames.Contains(name) || name.StartsWith(".env", StringComparison.Ordinal)
                || ExcludedPrefixes.Any(x => r.StartsWith(x, StringComparison.Ordinal)) || r.EndsWith(".pyc", StringComparison.Ordinal)) continue;
            kept.Add(p);
        }
        return kept.OrderBy(p => Rel(p, root), StringComparer.Ordinal).ToList();
    }

    public static List<string> ScanText(string name, string text) =>
        SecretPatterns.Where(s => s.Rx.IsMatch(text)).Select(s => $"{name}: looks like a {s.Label}").ToList();

    public static string? ReadText(string path)
    {
        if (!TextExt.Contains(Path.GetExtension(path))) return null;
        try { return new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)); }
        catch (Exception e) when (e is IOException or DecoderFallbackException or UnauthorizedAccessException) { return null; }
    }

    public static HashSet<string> DatasetEmails(string root)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, cols) in new[] { ("user_identities.csv", new[] { "email" }), ("deletion_requests.csv", new[] { "email" }), ("user_audit_log.csv", new[] { "old_value", "new_value" }) })
        {
            var f = Path.Combine(root, "dataset", name);
            if (!File.Exists(f)) continue;
            foreach (var r in Csv.ReadRows(f))
                foreach (var c in cols)
                    foreach (Match m in EmailRe.Matches(r.G(c))) found.Add(m.Value.ToLowerInvariant());
        }
        return found;
    }

    /// <summary>(violations, notes). Violations make the build fail.</summary>
    public static (List<string> Bad, List<string> Notes) Check(List<string> files, string root, bool requireFinal = false)
    {
        var bad = new List<string>();
        var notes = new List<string>();
        var names = files.Select(p => Rel(p, root)).ToList();
        foreach (var need in Required.Concat(requireFinal ? FinalRequired : Array.Empty<string>()))
            if (!names.Contains(need)) bad.Add($"missing required file {need}");
        if (!requireFinal) notes.AddRange(FinalRequired.Where(n => !names.Contains(n)).Select(n => $"not yet present: {n}"));
        var emails = DatasetEmails(root);
        for (int i = 0; i < files.Count; i++)
        {
            var p = files[i]; var n = names[i];
            var ext = Path.GetExtension(p).ToLowerInvariant();
            if (n.StartsWith("dataset/", StringComparison.Ordinal) || n.Contains("/dataset/", StringComparison.Ordinal)) bad.Add($"{n}: dataset content in the zip");
            if (ArchiveExt.Contains(ext)) bad.Add($"{n}: nested archive");
            if (Path.GetFileName(p).StartsWith(".env", StringComparison.Ordinal)) bad.Add($"{n}: environment file");
            if (JudgeIgnoredExt.Contains(ext) && !AllowedIgnored.Contains(n)) bad.Add($"{n}: file type the code judge ignores (put the content in Markdown)");
            var size = new FileInfo(p).Length;
            if (size > MaxFileBytes) bad.Add($"{n}: {size} bytes is too large");
            var text = ReadText(p);
            if (text is null) continue;
            bad.AddRange(ScanText(n, text));
            var leaked = EmailRe.Matches(text).Select(m => m.Value.ToLowerInvariant()).Where(emails.Contains).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (leaked.Count > 0) bad.Add($"{n}: contains {leaked.Count} e-mail address(es) of dataset people, e.g. {leaked[0]}");
        }
        foreach (var extra in new[] { Path.Combine(root, "log.txt"), Path.Combine(root, "code", "config", "prices.json") })
            if (File.Exists(extra)) bad.AddRange(ScanText(Rel(extra, root) + " (not zipped, scanned anyway)", ReadText(extra) ?? ""));
        return (bad, notes);
    }

    /// <summary>Deterministic zip: entries in the given (sorted) order, fixed timestamp, fixed permissions.</summary>
    public static void Build(List<string> files, string output, string root)
    {
        if (File.Exists(output)) File.Delete(output);
        using var fs = new FileStream(output, FileMode.CreateNew);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var p in files)
        {
            var entry = zip.CreateEntry(Rel(p, root), CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            entry.ExternalAttributes = (Path.GetExtension(p) == ".sh" ? 0x1ED : 0x1A4) << 16;       // 0755 / 0644
            using var es = entry.Open();
            var bytes = File.ReadAllBytes(p);
            es.Write(bytes, 0, bytes.Length);
        }
    }

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var root = Cli.Cli.RepoRoot();
        bool dry = args.Contains("--dry-run"), final = args.Contains("--require-final");
        var outPath = Path.Combine(root, "code.zip");
        int oi = Array.IndexOf(args, "--out");
        if (oi >= 0 && oi + 1 < args.Length) outPath = args[oi + 1];
        var files = Collect(root);
        var (bad, notes) = Check(files, root, final);
        stdout.WriteLine($"{files.Count} files, {files.Sum(p => new FileInfo(p).Length) / 1024} KiB uncompressed");
        var byExt = files.GroupBy(p => Path.GetExtension(p) is { Length: > 0 } e ? e : "(none)").OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        stdout.WriteLine("by extension: " + string.Join(", ", byExt.Select(kv => $"{kv.Key}: {kv.Value}")));
        foreach (var p in files) stdout.WriteLine("  " + Rel(p, root));
        foreach (var n in notes) stdout.WriteLine("note: " + n);
        if (bad.Count > 0)
        {
            stdout.WriteLine("\nVIOLATIONS:");
            foreach (var b in bad) stdout.WriteLine("  - " + b);
            return 1;
        }
        stdout.WriteLine("\nall checks passed" + (dry ? " (dry run, nothing written)" : ""));
        if (!dry)
        {
            Build(files, outPath, root);
            stdout.WriteLine($"wrote {outPath}");
        }
        return 0;
    }
}
