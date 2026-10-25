using System.Text.RegularExpressions;

namespace Auditor.Evidence;

/// <summary>One row of evidence_files.csv with its parsed capture time.</summary>
public sealed record ManifestRow(string FileId, string FilePath, string Origin, DateTime T);

/// <summary>An explicit (resource, alias, email, user_id) link published by the data catalog (a LINK_RESOURCE_IDENTITY activity row).</summary>
public sealed record CatalogRow(string ActivityId, DateTime T, string ResourceId, string System, string Alias, IReadOnlySet<string> Idents, bool OnlySubject);

/// <summary>Assemble every Obs from every evidence file + the activity stream's catalog rows.</summary>
public static class Load
{
    /// <summary>evidence_files.csv rows with parsed capture time (a malformed row is skipped, never fatal; a missing manifest means no evidence).</summary>
    public static List<ManifestRow> ManifestRows(string root)
    {
        List<Dictionary<string, string>> rows;
        try { rows = Csv.ReadRows(Path.Combine(root, "evidence_files.csv"), "file_id", "file_path", "captured_at"); }
        catch (DatasetException) { return new(); }
        var output = new List<ManifestRow>();
        foreach (var r in rows)
        {
            var t = Py.TryTs(r["captured_at"]);
            if (t is not null && r["file_id"].Trim().Length > 0) output.Add(new ManifestRow(r["file_id"], r["file_path"], r.G("origin"), t.Value));
        }
        return output;
    }

    /// <summary>True when <paramref name="rel"/>, joined to <paramref name="root"/> and normalised WITHOUT following symlinks, stays inside <paramref name="root"/> (no '..' escape, no absolute path). Only dataset/ may be read.</summary>
    public static bool InsideRoot(string root, string rel)
    {
        var baseDir = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var target = Path.IsPathRooted(rel) ? Path.GetFullPath(rel) : Path.GetFullPath(Path.Combine(baseDir, rel));
        return target == baseDir || target.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void RunRoute(EvidenceBase b, Route route, DateTime t, string root, IImageReader? reader)
    {
        string fid = route.FileId, path = route.Path;
        switch (route.Handler)
        {
            case "sql": Loaders.LoadSql(b, fid, path, t); break;
            case "json": Loaders.LoadJson(b, fid, path, t); break;
            case "text": Loaders.LoadText(b, fid, path, t); break;
            case "log": LogObs.LoadLog(b, fid, path, t); break;
            case "pdf": PdfObs.LoadPdf(b, fid, path, t, CatalogRows(root)); break;
            case "image" when route.Reader == "image_reader": ImageObs.LoadImage(b, fid, path, t, reader!, CatalogRows(root)); break;
            default: b.Warnings.Add($"{fid}: {route.Reason}"); break;
        }
    }

    /// <summary>
    /// Never raises: an unreadable file becomes a warning and the rest of the evidence is still used. Every manifest file is routed first (<see cref="Router.RouteFile"/>); the route and what it
    /// produced are kept in <c>Routes</c>.
    /// </summary>
    public static EvidenceBase LoadEvidence(string root, bool includeImages = true, IImageReader? imageReader = null, int imageWorkers = 1)
    {
        var b = new EvidenceBase();
        var manifest = ManifestRows(root);
        if (manifest.Count == 0) b.Warnings.Add("evidence manifest missing, empty or unreadable: no evidence files were used");
        var safe = new List<ManifestRow>();
        foreach (var r in manifest)
        {
            if (InsideRoot(root, r.FilePath)) safe.Add(r);
            else b.Warnings.Add($"{r.FileId}: manifest path {Py.StrRepr(r.FilePath.Length > 60 ? r.FilePath[..60] : r.FilePath)} points outside the dataset folder; not read");
        }
        bool reader = includeImages && imageReader is not null;
        var routes = safe.Select(r => (Route: Router.RouteFile(r.FileId, Path.Combine(root, r.FilePath), reader), r.T)).ToList();
        if (reader && imageWorkers > 1)
        {
            var pngs = routes.Where(x => x.Route.Reader == "image_reader").Select(x => x.Route.Path).ToList();
            imageReader = ImageJobs.Prefetch(imageReader!, pngs, ImageObs.System, ImageObs.Instruction, ImageObs.Schema, ImageObs.PromptVersion, imageWorkers);
        }
        foreach (var (route, t) in routes)
        {
            int n0 = b.Obs.Count, w0 = b.Warnings.Count;
            bool failed;
            try { RunRoute(b, route, t, root, imageReader); failed = false; }
            catch (Exception e)                                                       // a corrupt file must never stop the run
            {
                b.Warnings.Add($"{route.FileId}: could not read {Path.GetFileName(route.Path)}: {e.GetType().Name}: {e.Message}");
                failed = true;
            }
            int n = b.Obs.Count - n0;
            var outcome = failed ? "error" : n > 0 ? "parsed" : b.Warnings.Count > w0 || route.Reader == "none" ? "not read" : "no records";
            b.Routes.Add(new Routed(route, outcome, n));
        }
        var odd = b.Obs.Where(o => !Schema.ValidResourceId(o.Resource)).ToList();
        if (odd.Count > 0)             // text that is not a resource id (a path, a sentence, SQL) is not a resource: drop it, say so
        {
            b.Obs = b.Obs.Where(o => Schema.ValidResourceId(o.Resource)).ToList();
            var first = odd[0].Resource;
            b.Warnings.Add($"{odd.Count} observation(s) dropped: resource id is not of the form <name>_<number> (first: {odd[0].FileId} {Py.StrRepr(first.Length > 40 ? first[..40] : first)})");
        }
        return b;
    }

    private static readonly Dictionary<(string Root, long Mtime, long Size), List<CatalogRow>> CatalogCache = new();
    private static readonly object CatalogLock = new();
    private static readonly Regex LinkedRe = new(@"(?:linked|confirmed)\s+(\S+)\s+\(([^)]*)\)", RegexOptions.CultureInvariant);

    /// <summary>LINK_RESOURCE_IDENTITY activity rows: explicit (resource, alias, email, user_id) links published by the data catalog.</summary>
    public static List<CatalogRow> CatalogRows(string root)
    {
        var f = new FileInfo(Path.Combine(root, "system_activity.csv"));
        var stamp = (Path.GetFullPath(root), f.Exists ? f.LastWriteTimeUtc.Ticks : 0, f.Exists ? f.Length : 0);
        lock (CatalogLock)
        {
            if (CatalogCache.TryGetValue(stamp, out var cached)) return cached;
            var rows = new List<CatalogRow>();
            List<Dictionary<string, string>> activity;
            try { activity = Csv.ReadRows(f.FullName, "activity_id", "event_time", "event_type"); }
            catch (DatasetException) { activity = new(); }
            foreach (var r in activity)
            {
                if (r["event_type"] != "LINK_RESOURCE_IDENTITY" || Py.TryTs(r["event_time"]) is null) continue;
                var d = r.G("details");
                var m = LinkedRe.Match(d);
                var alias = m.Success ? m.Groups[2].Value : "";
                rows.Add(new CatalogRow(r["activity_id"], Py.ParseTs(r["event_time"]), r.G("resource_id"), r.G("target_system"), alias,
                                        Records.IdentsFrom(new[] { ("d", d) }), d.Contains("belongs only to", StringComparison.Ordinal)));
            }
            CatalogCache[stamp] = rows;
            return rows;
        }
    }
}
