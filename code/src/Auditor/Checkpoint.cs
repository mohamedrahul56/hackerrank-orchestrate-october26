using System.Security.Cryptography;
using System.Text;

namespace Auditor;

/// <summary>
/// Per-case results written as they are finished, so a killed run resumes instead of starting over.
/// File: <c>&lt;out&gt;.partial.jsonl</c>. Line 1 is a header holding the RUN KEY; every further line is one finished case. Each line carries the sha256 of its own payload, so a line cut
/// short by a kill (or damaged on disk) is detected and dropped, together with everything after it.
/// The run key is a hash of everything the answer depends on:
///   * the input: every file under dataset/ (names and sha256), plus which requests file is answered;
///   * the versions: pipeline version, rules-file digest, prompt digests, a digest of the auditor's own code (Versions);
///   * the settings that can change an answer (Settings.Fingerprint);
///   * whether screenshots were read by a model, and a digest of the observations that reading produced.
/// A checkpoint whose key differs from the current run's is discarded (and says so), never mixed in.
/// What a resume saves, plainly: the per-case audit. Start-up work that needs every case at once (parsing the evidence, the activity-timeline discovery that assigns id-less accounts across
/// cases, classification) is recomputed on every start - it is deterministic, so the recomputed values are identical to the ones that produced the checkpointed cases, which is exactly
/// what the key guarantees. Model calls are not repeated either way: readings live in the LLM cache. A resumed run writes the same output.csv and output.notes.md, byte for byte, as an
/// uninterrupted one.
/// </summary>
public sealed class Checkpoint : IDisposable
{
    public const int Format = 1;
    /// <summary>flushed to the OS after every case (survives a killed process); fsync'd to disk every this many cases (survives a power cut)</summary>
    public const int FsyncEvery = 25;

    private readonly string path;
    private readonly string key;
    private FileStream? stream;
    private StreamWriter? writer;
    private int sinceSync;

    /// <summary>The finished cases of a matching checkpoint.</summary>
    public Dictionary<string, CaseResult> Done { get; } = new(StringComparer.Ordinal);
    /// <summary>What happened to an existing file.</summary>
    public string How { get; private set; } = "fresh";

    public static string DatasetDigest(string root)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var full = Path.GetFullPath(root);
        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Select(f => (Full: f, Rel: Path.GetRelativePath(full, f).Replace('\\', '/'))).OrderBy(x => x.Rel, StringComparer.Ordinal))
        {
            h.AppendData(Encoding.UTF8.GetBytes(file.Rel));
            h.AppendData(new byte[] { 0 });
            h.AppendData(SHA256.HashData(File.ReadAllBytes(file.Full)));
        }
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Digest of what the screenshots were read as (empty when no model read them).</summary>
    public static string ImageObservationDigest(CaseAuditor auditor)
    {
        var rows = auditor.Base.Obs.Where(o => o.Source.StartsWith("image:", StringComparison.Ordinal))
                          .Select(o => $"({Compat.StrRepr(o.FileId)}, {Compat.StrRepr(o.Resource)}, {Compat.StrRepr(o.Category)}, {Compat.StrRepr(o.State)}, {Compat.ListRepr(o.Idents.OrderBy(i => i, StringComparer.Ordinal))}, {Compat.StrRepr(o.Source)})")
                          .OrderBy(x => x, StringComparer.Ordinal).ToList();
        return rows.Count == 0 ? "" : Sha256Hex(string.Join("\n", rows));
    }

    public static string RunKey(string datasetRoot, string requestsFile, VersionStamp stamp, bool imagesOn, string imageObs)
    {
        var material = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["format"] = Format.ToString(System.Globalization.CultureInfo.InvariantCulture), ["dataset"] = DatasetDigest(datasetRoot), ["requests_file"] = requestsFile,
            ["pipeline"] = stamp.Pipeline, ["rules"] = stamp.Rules, ["prompts"] = string.Join(";", stamp.Prompts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value.Version}/{p.Value.Digest}")),
            ["code"] = stamp.Code, ["settings"] = stamp.Settings, ["images_on"] = imagesOn ? "true" : "false", ["image_observations"] = imageObs,
        };
        return Sha256Hex(string.Join("\n", material.Select(kv => kv.Key + "=" + kv.Value)));
    }

    private static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Line(Dictionary<string, object?> payload)
    {
        // key order is kept as it is (no sorting): steps_json is written to output.csv in this order, and a resumed file must be byte-identical
        var body = Compat.DumpsCompact(payload);
        return Compat.DumpsCompact(new Dictionary<string, object?> { ["sha"] = Sha256Hex(body), ["d"] = payload }) + "\n";
    }

    private static Dictionary<string, object?>? ParseLine(string text)
    {
        try
        {
            var obj = Compat.AsDict(Compat.ParseJson(text));
            var d = Compat.AsDict(obj["d"]);
            return Sha256Hex(Compat.DumpsCompact(d)) == (obj["sha"] as string) ? d : null;
        }
        catch (Exception e) when (e is DataException or KeyNotFoundException) { return null; }
    }

    public static Dictionary<string, object?> ToPayload(CaseResult r) => new(StringComparer.Ordinal)
    {
        ["kind"] = "case", ["case_id"] = r.CaseId, ["status"] = r.Status, ["steps"] = r.Steps.Select(s => (object?)s.ToJson()).ToList(), ["notes"] = r.Notes.Cast<object?>().ToList(),
        ["later_links"] = r.LaterLinks.Select(x => (object?)new List<object?> { x.Resource, x.FileId, x.Time }).ToList(), ["provisional"] = r.Provisional,
    };

    public static CaseResult FromPayload(Dictionary<string, object?> d)
    {
        var steps = Compat.AsList(d["steps"]).Select(x =>
        {
            var s = Compat.AsDict(x);
            return new Step((int)System.Convert.ToInt64(s["step_no"], System.Globalization.CultureInfo.InvariantCulture), (string)s["phase"]!, (string)s["system_id"]!, (string)s["resource_id"]!,
                            (string)s["operation"]!, (string)s["state"]!, (string)s["evidence_id"]!);
        }).ToList();
        var later = Compat.AsList(d["later_links"]).Select(x => { var l = Compat.AsList(x); return ((string)l[0]!, (string)l[1]!, (string)l[2]!); }).ToList();
        return new CaseResult((string)d["case_id"]!, (string)d["status"]!, steps, Compat.AsList(d["notes"]).Select(n => (string)n!).ToList(), later, Compat.Truthy(d["provisional"]));
    }

    /// <summary>Opened with the current run key.</summary>
    public Checkpoint(string path, string key, bool restart = false)
    {
        this.path = path; this.key = key;
        if (File.Exists(path) && restart) How = "restart requested: the old checkpoint was ignored";
        else if (File.Exists(path)) Load();                                  // sets How to "resumed ..." or "discarded: ..."
        if (How.StartsWith("resumed", StringComparison.Ordinal)) Open(FileMode.Append);
        else StartNew();
    }

    // ---------------------------------------------------------------------------------------- reading
    private void Load()
    {
        var text = new UTF8Encoding(false, false).GetString(File.ReadAllBytes(path));
        var lines = text.Split('\n');
        var header = lines.Length > 0 && lines[0].Length > 0 ? ParseLine(lines[0]) : null;
        if (header is null || !Equals(header.GetValueOrDefault("kind"), "header") || !Compat.EqualsNumber(header.GetValueOrDefault("format"), Format)) { How = "discarded: header unreadable"; return; }
        if (!Equals(header.GetValueOrDefault("key"), key)) { How = "discarded: the checkpoint was made for other inputs, versions or settings"; return; }
        var valid = new List<string> { lines[0] };
        foreach (var ln in lines.Skip(1))
        {
            if (ln.Length == 0) continue;
            var d = ParseLine(ln);
            if (d is null || !Equals(d.GetValueOrDefault("kind"), "case")) break;              // a torn or damaged line: everything from here on is unfinished work
            try { Done[(string)d["case_id"]!] = FromPayload(d); }
            catch (Exception e) when (e is KeyNotFoundException or InvalidCastException or NullReferenceException or DataException) { break; }
            valid.Add(ln);
        }
        var clean = string.Join("\n", valid) + "\n";
        if (clean != text)                                                      // drop the torn tail so new lines start on a clean boundary
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, clean, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            How = $"resumed ({Done.Count} cases kept, damaged tail dropped)";
        }
        else How = $"resumed ({Done.Count} cases kept)";
    }

    private void Open(FileMode mode)
    {
        stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read);
        writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
    }

    private void StartNew()
    {
        Done.Clear();
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        Open(FileMode.Create);
        Out.Write(Line(new Dictionary<string, object?> { ["kind"] = "header", ["format"] = (long)Format, ["key"] = key }));
        Sync();
    }

    private StreamWriter Out => writer ?? throw new InvalidOperationException("the checkpoint file is not open");

    // ---------------------------------------------------------------------------------------- writing
    public void Add(CaseResult r)
    {
        Done[r.CaseId] = r;
        Out.Write(Line(ToPayload(r)));
        Out.Flush();
        if (++sinceSync >= FsyncEvery) Sync();
    }

    public void Sync()
    {
        Out.Flush();
        stream!.Flush(flushToDisk: true);
        sinceSync = 0;
    }

    public void Close(bool remove = false)
    {
        if (writer is not null)
        {
            Sync();
            writer.Dispose();
            writer = null; stream = null;
        }
        if (remove && File.Exists(path)) File.Delete(path);
    }

    public void Dispose() => Close();

    /// <summary>Results in <paramref name="caseOrder"/>; cases already in the checkpoint are not audited again. Returns (results, number reused).</summary>
    public static (List<CaseResult> Results, int Reused) AuditAll(CaseAuditor auditor, IEnumerable<string> caseOrder, Checkpoint ckpt)
    {
        // test hook only: lets a test kill a genuinely running process at a known point
        int pauseAfter = int.TryParse(Environment.GetEnvironmentVariable("AUDITOR_TEST_PAUSE_AFTER"), out var p) ? p : 0;
        int reused = 0;
        var results = new List<CaseResult>();
        foreach (var cid in caseOrder)
        {
            if (ckpt.Done.TryGetValue(cid, out var r)) reused++;
            else
            {
                r = auditor.AuditCase(cid);
                ckpt.Add(r);
                if (pauseAfter > 0 && ckpt.Done.Count >= pauseAfter)
                {
                    ckpt.Sync();
                    Console.Error.WriteLine($"CHECKPOINT-REACHED {ckpt.Done.Count}");
                    Thread.Sleep(TimeSpan.FromHours(1));
                }
            }
            results.Add(r);
        }
        return (results, reused);
    }
}
