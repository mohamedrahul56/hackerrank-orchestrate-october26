using Auditor.Evidence;

namespace Auditor.Tools;

/// <summary>
/// Proof that the per-file router changed no observation: the old suffix dispatch is kept here as a frozen reference and compared with the router-based <see cref="Load.LoadEvidence"/>.
/// <see cref="Compare"/> loads the whole evidence set both ways and returns what differs (observations, coverage, warnings). The tests assert it is empty for all 94 files of the dataset
/// (and for synthetic folders); the agent-layer report prints the same comparison.
/// </summary>
public static class RouterEquivalence
{
    /// <summary>The suffix dispatch as it was before the router existed (a frozen copy; the router must reproduce it exactly).</summary>
    public static EvidenceBase LegacyLoadEvidence(string root, bool includeImages, IImageReader? reader, int workers)
    {
        var b = new EvidenceBase();
        var manifest = Load.ManifestRows(root);
        if (manifest.Count == 0) b.Warnings.Add("evidence manifest missing, empty or unreadable: no evidence files were used");
        var safe = new List<ManifestRow>();
        foreach (var r in manifest)
        {
            if (Load.InsideRoot(root, r.FilePath)) safe.Add(r);
            else b.Warnings.Add($"{r.FileId}: manifest path {Py.StrRepr(r.FilePath.Length > 60 ? r.FilePath[..60] : r.FilePath)} points outside the dataset folder; not read");
        }
        if (includeImages && reader is not null && workers > 1)
        {
            var pngs = safe.Select(r => Path.Combine(root, r.FilePath)).Where(p => Path.GetExtension(p).ToLowerInvariant() == ".png").ToList();
            reader = ImageJobs.Prefetch(reader, pngs, ImageObs.System, ImageObs.Instruction, ImageObs.Schema, ImageObs.PromptVersion, workers);
        }
        foreach (var r in safe)
        {
            var path = Path.Combine(root, r.FilePath); var fid = r.FileId; var t = r.T;
            try
            {
                switch (Path.GetExtension(path).ToLowerInvariant())
                {
                    case ".sql": Loaders.LoadSql(b, fid, path, t); break;
                    case ".json": Loaders.LoadJson(b, fid, path, t); break;
                    case ".txt": Loaders.LoadText(b, fid, path, t); break;
                    case ".log": LogObs.LoadLog(b, fid, path, t); break;
                    case ".pdf": PdfObs.LoadPdf(b, fid, path, t, Load.CatalogRows(root)); break;
                    case ".png" when includeImages && reader is not null: ImageObs.LoadImage(b, fid, path, t, reader, Load.CatalogRows(root)); break;
                    case ".png": b.Warnings.Add($"{fid}: image not read (no image extractor configured)"); break;
                    case var s: b.Warnings.Add($"{fid}: unsupported file type {s}"); break;
                }
            }
            catch (Exception e) { b.Warnings.Add($"{fid}: could not read {Path.GetFileName(path)}: {e.GetType().Name}: {e.Message}"); }
        }
        var odd = b.Obs.Where(o => !Schema.ValidResourceId(o.Resource)).ToList();
        if (odd.Count > 0)
        {
            b.Obs = b.Obs.Where(o => Schema.ValidResourceId(o.Resource)).ToList();
            b.Warnings.Add($"{odd.Count} observation(s) dropped: resource id is not of the form <name>_<number> (first: {odd[0].FileId} {Py.StrRepr(odd[0].Resource.Length > 40 ? odd[0].Resource[..40] : odd[0].Resource)})");
        }
        return b;
    }

    private static string Project(Obs o) =>
        string.Join("|", o.FileId, Py.FormatTs(o.FileT), Py.FormatTs(o.T), o.System, o.Resource, o.Category, o.State, string.Join(",", o.Idents.OrderBy(i => i, StringComparer.Ordinal)), o.Source, o.Detail, o.Until is { } u ? Py.FormatTs(u) : "-");

    /// <summary>Loads <paramref name="root"/> with the legacy dispatch and with the router; returns counts and the list of differences (empty = identical).</summary>
    public static (List<string> Differences, int Files, int Routes, int Observations) Compare(string root, bool includeImages, IImageReader? reader, int workers)
    {
        var old = LegacyLoadEvidence(root, includeImages, reader, workers);
        var now = Load.LoadEvidence(root, includeImages, reader, workers);
        var diffs = new List<string>();
        if (!old.Obs.Select(Project).SequenceEqual(now.Obs.Select(Project))) diffs.Add($"obs: {old.Obs.Count} legacy vs {now.Obs.Count} routed");
        if (!old.Coverage.SequenceEqual(now.Coverage)) diffs.Add($"coverage: {old.Coverage.Count} legacy vs {now.Coverage.Count} routed");
        if (!old.Warnings.SequenceEqual(now.Warnings)) diffs.Add($"warnings: {old.Warnings.Count} legacy vs {now.Warnings.Count} routed");
        return (diffs, Load.ManifestRows(root).Count, now.Routes.Count, now.Obs.Count);
    }

}
