using Auditor.Evidence;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Auditor.Tools;

/// <summary>
/// Puts every screenshot next to what the vision model read from it, so the readings can be checked by eye. For each image the page shows the picture, the raw JSON the model returned and the
/// observations the code derives from it (resource, data category, state, identifiers) - exactly what the pipeline would use. Readings are cached (AUDITOR_CACHE_DIR), so the pipeline run
/// that follows costs nothing extra. The key is read from the environment only: this tool never asks for it and never writes it anywhere.
/// </summary>
public static class ShowReadings
{
    public sealed class Item
    {
        public ManifestRowView Row = new("", "", "", "");
        public string Path = "";
        public Dictionary<string, object?>? Reading;
        public string Error = "";
        public List<Obs> Observations = new();
    }

    public sealed record ManifestRowView(string FileId, string FilePath, string Origin, string CapturedAt);

    public const string NoKey =
        "ANTHROPIC_API_KEY is not set, so no screenshot can be read.\n" +
        "Set it in your own shell (it is never written to a file or asked for in chat):\n" +
        "    export ANTHROPIC_API_KEY=...\n" +
        "and run this tool again. Nothing was sent anywhere.";

    public static List<ManifestRowView> PngRows(string root, IReadOnlyCollection<string>? only = null) =>
        Csv.ReadRows(System.IO.Path.Combine(root, "evidence_files.csv")).Where(r => r.G("file_path").EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .Select(r => new ManifestRowView(r.G("file_id"), r.G("file_path"), r.G("origin"), r.G("captured_at"))).Where(r => only is null || only.Count == 0 || only.Contains(r.FileId)).ToList();

    /// <summary>One entry per PNG, in manifest order. A failed reading is shown next to the picture instead of stopping the tool.</summary>
    public static List<Item> ReadAll(string root, IImageReader reader, IReadOnlyCollection<string>? only = null)
    {
        var catalog = Load.CatalogRows(root);
        var items = new List<Item>();
        foreach (var r in PngRows(root, only))
        {
            var item = new Item { Row = r, Path = System.IO.Path.Combine(root, r.FilePath) };
            try
            {
                item.Reading = reader.Read(item.Path, ImageObs.System, ImageObs.Instruction, ImageObs.Schema, ImageObs.PromptVersion);
                var b = new EvidenceBase();
                ImageObs.ObservationsFromReading(b, r.FileId, Py.ParseTs(r.CapturedAt), item.Reading, catalog);
                item.Observations = b.Obs;
            }
            catch (Exception e) { item.Error = $"{e.GetType().Name}: {e.Message}"; }
            items.Add(item);
        }
        return items;
    }

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, IndentSize = 1, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Json(object? v) => JsonSerializer.Serialize(v, Pretty);

    public static string RenderText(IEnumerable<Item> items)
    {
        var o = new List<string>();
        foreach (var it in items)
        {
            var r = it.Row;
            o.Add($"=== {r.FileId}  {r.FilePath}  (captured {r.CapturedAt}, origin {r.Origin})");
            if (it.Error.Length > 0) { o.Add($"READING FAILED: {it.Error}"); continue; }
            o.Add("--- model reading:"); o.Add(Json(it.Reading)); o.Add("--- derived observations:");
            if (it.Observations.Count == 0) o.Add("  (none)");
            o.AddRange(it.Observations.Select(x => $"  {x.Resource,-22} {x.Category,-20} {x.State,-11} ids=[{string.Join(", ", x.Idents.OrderBy(i => i, StringComparer.Ordinal))}] ({x.Detail})"));
            o.Add("");
        }
        return string.Join("\n", o);
    }

    private static string H(string s) => WebUtility.HtmlEncode(s);

    public static string RenderHtml(IReadOnlyList<Item> items)
    {
        var cards = new StringBuilder();
        foreach (var it in items)
        {
            var r = it.Row;
            var img = Convert.ToBase64String(File.ReadAllBytes(it.Path));
            string right;
            if (it.Error.Length > 0) right = $"<p class='bad'>READING FAILED: {H(it.Error)}</p>";
            else
            {
                var obs = string.Concat(it.Observations.Select(o =>
                    $"<tr><td>{H(o.Resource)}</td><td>{H(o.Category)}</td><td><b>{H(o.State)}</b></td><td>{H(string.Join(", ", o.Idents.OrderBy(i => i, StringComparer.Ordinal)))}</td><td>{H(o.Detail)}</td></tr>"));
                right = $"<h4>Model reading (raw JSON)</h4><pre>{H(Json(it.Reading))}</pre>" +
                        $"<h4>Observations the code derives</h4><table><tr><th>resource</th><th>category</th><th>state</th><th>identifiers</th><th>shown as</th></tr>{(obs.Length > 0 ? obs : "<tr><td colspan=5>(none)</td></tr>")}</table>";
            }
            cards.Append($"<section><h3>{H(r.FileId)} - {H(r.FilePath)}</h3><p>captured {H(r.CapturedAt)}, origin {H(r.Origin)}</p>" +
                         $"<div class='row'><div class='img'><img src='data:image/png;base64,{img}' alt='{H(r.FileId)}'></div><div class='txt'>{right}</div></div></section>");
        }
        const string css = "body{font-family:system-ui,sans-serif;margin:16px;background:#fafafa;color:#111}section{border:1px solid #ccc;background:#fff;margin:0 0 24px;padding:12px;border-radius:6px}" +
                           ".row{display:flex;gap:16px;flex-wrap:wrap}.img{flex:1 1 480px}.img img{max-width:100%;border:1px solid #ddd}.txt{flex:1 1 420px;min-width:0}" +
                           "pre{background:#f2f2f2;padding:8px;overflow:auto;max-height:420px;font-size:12px}table{border-collapse:collapse;font-size:13px}td,th{border:1px solid #ccc;padding:3px 6px;text-align:left}.bad{color:#b00020}";
        return $"<!doctype html><meta charset='utf-8'><title>Image readings</title><style>{css}</style><h1>Screenshots and what the model read ({items.Count})</h1>{cards}";
    }

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var root = Cli.Cli.RepoRoot();
        var dataset = Path_(args, "--dataset") ?? System.IO.Path.Combine(root, "dataset");
        var outPath = Path_(args, "--out") ?? System.IO.Path.Combine(root, "code", "out", "image_readings.html");
        var only = new List<string>();
        int oi = Array.IndexOf(args, "--only");
        if (oi >= 0) only.AddRange(args.Skip(oi + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)));
        bool text = args.Contains("--text");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN")))
        {
            stderr.WriteLine(NoKey);
            return 2;
        }
        var llm = new Llm(usagePath: System.IO.Path.Combine(root, "code", "out", "image_preview_usage.jsonl"));
        var items = ReadAll(dataset, new SingleShotImageReader(llm), only);
        if (items.Count == 0)
        {
            stderr.WriteLine("no PNG files in the manifest" + (only.Count > 0 ? $" matching [{string.Join(", ", only)}]" : ""));
            return 1;
        }
        if (text) stdout.WriteLine(RenderText(items));
        else
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, RenderHtml(items), new UTF8Encoding(false));
            stdout.WriteLine($"wrote {outPath} ({items.Count} images, {items.Count(i => i.Error.Length > 0)} failed) - open it in a browser");
        }
        return items.Any(i => i.Error.Length > 0) ? 1 : 0;
    }

    private static string? Path_(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
