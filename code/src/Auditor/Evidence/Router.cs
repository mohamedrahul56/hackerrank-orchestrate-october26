namespace Auditor.Evidence;

/// <summary>
/// Per-file routing of the evidence manifest: which reader handles each file is decided here, by code, from what the file IS.
/// A route has two parts. <c>Handler</c> names the parser for the file's format (sql, json, text, log, pdf, image) and <c>Reader</c> says who does the reading:
///   parser         a deterministic parser in this package (every format except images)
///   image_reader   the screenshot reader (the single-shot model, a readings file, or the agent loop): used only for images, and only when one is configured
///   none           the file is not read; <c>Reason</c> says why, and the run notes repeat it
/// The model never chooses a route and never sees anything but an image: a route is a function of the file name, the file's first bytes and whether a reader is configured.
/// The agent loop is therefore an escalation for the one format code cannot read, not a second opinion on formats code can. When no reader is configured, or the reader fails,
/// the file is "not read" and the deterministic evidence stands.
/// Content sniffing is limited to two unambiguous signatures (PNG, PDF): a text format is never guessed from its content. A file whose first bytes are a PNG or a PDF is routed by its bytes
/// even if the name says otherwise. A ".png" without the signature still goes to the reader, which refuses it itself; the route only records the doubt.
/// </summary>
public static class Router
{
    private static readonly Dictionary<string, string> SuffixHandler = new(StringComparer.Ordinal)
    {
        [".sql"] = "sql", [".json"] = "json", [".txt"] = "text", [".log"] = "log", [".pdf"] = "pdf", [".png"] = "image",
    };

    private static readonly (byte[] Magic, string Kind)[] Signatures =
    {
        (new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, "png"),
        (new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-' }, "pdf"),
    };

    private const int HeadBytes = 8;

    public static string Sniff(byte[] head) =>
        Signatures.FirstOrDefault(s => head.Length >= s.Magic.Length && head.AsSpan(0, s.Magic.Length).SequenceEqual(s.Magic)).Kind ?? "";

    public static byte[] ReadHead(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var buf = new byte[HeadBytes];
            int n = f.Read(buf, 0, HeadBytes);
            return buf[..n];
        }
        catch (IOException) { return Array.Empty<byte>(); }                       // the handler will report the unreadable file with its own message
        catch (UnauthorizedAccessException) { return Array.Empty<byte>(); }
    }

    /// <summary>Decide the route of one manifest file. <paramref name="imageReader"/> says whether a screenshot reader is configured for this run.</summary>
    public static Route RouteFile(string fileId, string path, bool imageReader)
    {
        var suffix = Path.GetExtension(path).ToLowerInvariant();
        var handler = SuffixHandler.GetValueOrDefault(suffix, "none");
        var reason = handler != "none" ? $"{(suffix.Length > 0 ? suffix : "no extension")} file" : $"unsupported file type {suffix}";
        var kind = Sniff(ReadHead(path));
        if (kind is "png" or "pdf" && handler != (kind == "png" ? "image" : "pdf"))
        {
            handler = kind == "png" ? "image" : "pdf";
            reason = $"content is a {kind.ToUpperInvariant()}, the name says {(suffix.Length > 0 ? suffix : "no extension")}";
        }
        else if (handler == "image" && kind != "png" && File.Exists(path))
        {
            reason = "name says PNG but the PNG signature is missing";
        }
        if (handler == "none") return new Route(fileId, path, "none", "none", reason);
        if (handler == "image")
            return imageReader ? new Route(fileId, path, "image", "image_reader", reason) : new Route(fileId, path, "image", "none", "image not read (no image extractor configured)");
        return new Route(fileId, path, handler, "parser", reason);
    }

    /// <summary>Markdown for the run notes: how many files went to which reader, and every file that was not read or read nothing.</summary>
    public static List<string> SummaryLines(IReadOnlyList<Routed> routed)
    {
        var by = routed.GroupBy(r => (r.Route.Handler, r.Route.Reader, r.Outcome)).OrderBy(g => g.Key.Handler, StringComparer.Ordinal).ThenBy(g => g.Key.Reader, StringComparer.Ordinal).ThenBy(g => g.Key.Outcome, StringComparer.Ordinal);
        var lines = new List<string> { "| handler | reader | outcome | files |", "|---|---|---|---|" };
        lines.AddRange(by.Select(g => $"| {g.Key.Handler} | {g.Key.Reader} | {g.Key.Outcome} | {g.Count()} |"));
        var odd = routed.Where(r => r.Outcome is "not read" or "error" or "no records").OrderBy(r => r.Route.FileId, StringComparer.Ordinal).ToList();
        if (odd.Count > 0)
        {
            lines.Add("");
            lines.Add("Files that were not read or gave no record:");
            lines.AddRange(odd.Select(r => $"* {r.Route.FileId} ({r.Route.Handler}, {r.Outcome}): {r.Route.Reason}"));
        }
        return lines;
    }
}

public sealed record Route(string FileId, string Path, string Handler, string Reader, string Reason);

/// <summary>A route and what running it produced (kept on <see cref="EvidenceBase.Routes"/>, summarised in the run notes).</summary>
public sealed record Routed(Route Route, string Outcome, int Observations = 0);
