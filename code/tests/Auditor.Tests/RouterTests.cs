using System.Text;

namespace Auditor.Tests;

/// <summary>The per-file router: the decision is code, images are the only files that can reach a model, and the router changes no observation.</summary>
public abstract class RouterBase : IDisposable
{
    protected static readonly byte[] Png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(new byte[24]).ToArray();
    protected readonly TempDir Tmp = new();
    public void Dispose() => Tmp.Dispose();

    protected static Dictionary<string, object?> Reading() => (Dictionary<string, object?>)Py.ParseJson(
        "{\"screen\": \"x\", \"records\": [{\"resource_id\": \"search_1\", \"selected\": true, \"emails\": [], \"user_ids\": [\"user_1\"], \"phones\": [], \"status_words\": [\"Live\"], \"shows_direct_identifiers\": false, \"aliases\": []}]}")!;

    protected string MakeRoot(params (string Name, byte[] Data)[] files)
    {
        var rows = new List<string> { "file_id,file_path,origin,captured_at" };
        for (int i = 0; i < files.Length; i++)
        {
            var p = Path.Combine(Tmp.Path, files[i].Name);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, files[i].Data);
            rows.Add($"evidence_{i:D2},{files[i].Name},synthetic,2026-08-12T00:00:00Z");
        }
        File.WriteAllText(Path.Combine(Tmp.Path, "evidence_files.csv"), string.Join("\n", rows) + "\n");
        return Tmp.Path;
    }

    protected static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    protected static IImageReader Reader(Func<string, Dictionary<string, object?>> f) => new FuncImageReader((p, _, _, _, _) => f(p));
}

public class RouteDecisionTests : RouterBase
{
    private Route RouteOf(string name, byte[]? data = null, bool reader = true)
    {
        var root = MakeRoot((name, data ?? B("x")));
        return Router.RouteFile("evidence_00", Path.Combine(root, name), reader);
    }

    [Theory]
    [InlineData("a.sql", "sql")] [InlineData("a.json", "json")] [InlineData("a.txt", "text")] [InlineData("a.log", "log")] [InlineData("A.PDF", "pdf")]
    public void EachFormatGoesToItsDeterministicParser(string name, string handler)
    {
        var r = RouteOf(name);
        Assert.Equal((handler, "parser"), (r.Handler, r.Reader));
    }

    [Fact]
    public void OnlyImagesCanReachTheImageReaderAndOnlyWhenOneIsConfigured()
    {
        var r1 = RouteOf("s.png", Png);
        Assert.Equal(("image", "image_reader"), (r1.Handler, r1.Reader));
        var r = RouteOf("s.png", Png, reader: false);
        Assert.Equal(("image", "none"), (r.Handler, r.Reader));
        Assert.Equal("image not read (no image extractor configured)", r.Reason);
        foreach (var name in new[] { "a.sql", "a.json", "a.txt", "a.log" }) Assert.Equal("parser", RouteOf(name, reader: true).Reader);
    }

    [Fact]
    public void UnsupportedSuffixIsNotReadAndSaysWhy()
    {
        var r = RouteOf("a.xlsx");
        Assert.Equal(("none", "none", "unsupported file type .xlsx"), (r.Handler, r.Reader, r.Reason));
    }

    [Fact]
    public void AnUnambiguousSignatureBeatsTheName()
    {
        var r = RouteOf("report.json", B("%PDF-1.7 ..."));
        Assert.Equal(("pdf", "parser"), (r.Handler, r.Reader));
        Assert.Contains("content is a PDF", r.Reason, StringComparison.Ordinal);
        r = RouteOf("shot.txt", Png);
        Assert.Equal(("image", "image_reader"), (r.Handler, r.Reader));
    }

    [Fact]
    public void TextFormatsAreNeverGuessedFromContent()
    {
        Assert.Equal("text", RouteOf("a.txt", B("{\"a\": 1}")).Handler);
        Assert.Equal("log", RouteOf("a.log", B("SELECT 1;")).Handler);
    }

    [Fact]
    public void APngWithoutTheSignatureStillGoesToTheReaderWhichRefusesItItself()
    {
        var r = RouteOf("shot.png", B("not a png"));
        Assert.Equal(("image", "image_reader"), (r.Handler, r.Reader));
        Assert.Contains("signature is missing", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingFileIsRoutedByItsNameAndReportedByTheHandler()
    {
        var r = Router.RouteFile("evidence_00", "/nonexistent/a.json", true);
        Assert.Equal(("json", "parser"), (r.Handler, r.Reader));
    }
}

public class RoutingInTheLoaderTests : RouterBase
{
    [Fact]
    public void TheExtractorIsCalledForImagesOnly()
    {
        var root = MakeRoot(("a.json", B("{\"search_1\": 1}")), ("a.txt", B("hello")), ("s.png", Png), ("t.png", Png), ("a.sql", B("select 1;")));
        var seen = new List<string>();
        var b = Load.LoadEvidence(root, true, Reader(p => { lock (seen) seen.Add(Path.GetExtension(p)); return Reading(); }));
        Assert.Equal(new[] { ".png", ".png" }, seen.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("image_reader", b.Routes.First(r => r.Route.FileId == "evidence_02").Route.Reader);
    }

    [Fact]
    public void EveryManifestFileGetsARouteAndAnOutcome()
    {
        var root = MakeRoot(("s.png", Png), ("a.bin", B("x")), ("bad.json", B("{not json")));
        var b = Load.LoadEvidence(root, true, null);
        var by = b.Routes.ToDictionary(r => r.Route.FileId);
        Assert.Equal(3, b.Routes.Count);
        Assert.Equal("not read", by["evidence_00"].Outcome);
        Assert.Equal("not read", by["evidence_01"].Outcome);
        Assert.Contains(by["evidence_02"].Outcome, new[] { "error", "no records", "not read" });
        Assert.Contains(b.Warnings, w => w.Contains("unsupported file type .bin", StringComparison.Ordinal));
    }

    [Fact]
    public void AReaderThatFailsLeavesTheFileNotReadAndTheRestIntact()
    {
        var root = MakeRoot(("s.png", Png), ("a.json", B("{\"x\": 1}")));
        var b = Load.LoadEvidence(root, true, Reader(_ => throw new LlmUnavailableException("no key")));
        Assert.Equal("not read", b.Routes.First(r => r.Route.FileId == "evidence_00").Outcome);
        Assert.Contains(b.Warnings, w => w.Contains("not read", StringComparison.Ordinal) && w.Contains("no key", StringComparison.Ordinal));
    }

    [Fact]
    public void SummaryLinesListTheTableAndTheFilesThatGaveNothing()
    {
        var root = MakeRoot(("s.png", Png), ("a.bin", B("x")));
        var text = string.Join("\n", Router.SummaryLines(Load.LoadEvidence(root, true, null).Routes));
        Assert.Contains("| handler | reader | outcome | files |", text, StringComparison.Ordinal);
        Assert.Contains("evidence_01 (none, not read): unsupported file type .bin", text, StringComparison.Ordinal);
    }
}

/// <summary>The old suffix dispatch (frozen below) and the router give identical observations, coverage and warnings.</summary>
public class RouterChangesNoObservationTests : RouterBase
{
    /// <summary>The suffix dispatch as it was before the router existed (a frozen copy; the router must reproduce it exactly).</summary>
    private static EvidenceBase LegacyLoadEvidence(string root, bool includeImages, IImageReader? reader, int workers)
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
    internal static (List<string> Differences, int Files, int Routes, int Observations) Compare(string root, bool includeImages, IImageReader? reader, int workers)
    {
        var old = LegacyLoadEvidence(root, includeImages, reader, workers);
        var now = Load.LoadEvidence(root, includeImages, reader, workers);
        var diffs = new List<string>();
        if (!old.Obs.Select(Project).SequenceEqual(now.Obs.Select(Project))) diffs.Add($"obs: {old.Obs.Count} legacy vs {now.Obs.Count} routed");
        if (!old.Coverage.SequenceEqual(now.Coverage)) diffs.Add($"coverage: {old.Coverage.Count} legacy vs {now.Coverage.Count} routed");
        if (!old.Warnings.SequenceEqual(now.Warnings)) diffs.Add($"warnings: {old.Warnings.Count} legacy vs {now.Warnings.Count} routed");
        return (diffs, Load.ManifestRows(root).Count, now.Routes.Count, now.Obs.Count);
    }

    [Fact]
    public void SyntheticFolderWithOddFiles()
    {
        var root = MakeRoot(("s.png", Png), ("a.bin", B("x")), ("bad.json", B("{not json")), ("ok.json", B("{\"search_1\": {\"email\": \"a@example.test\"}}")), ("r.log", Array.Empty<byte>()), ("t.txt", B("search_1")));
        foreach (var (reader, ex) in new (bool, IImageReader?)[] { (false, null), (true, Reader(_ => Reading())) })
            foreach (var workers in new[] { 1, 3 })
                Assert.Empty(Compare(root, reader, ex, workers).Differences);
    }

    [DatasetFact]
    public void All94FilesOfTheDataset()
    {
        foreach (var (reader, ex, workers) in new (bool, IImageReader?, int)[] { (false, null, 1), (true, Reader(_ => Reading()), 1), (true, Reader(_ => Reading()), 4) })
        {
            var r = Compare(Repo.Dataset, reader, ex, workers);
            Assert.Empty(r.Differences);
            Assert.Equal((94, 94), (r.Files, r.Routes));
            Assert.True(r.Observations > 2000);
        }
    }
}
