using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Auditor.Evidence;

/// <summary>
/// PDF evidence: extract the text layer and turn the resources it names into observations.
/// PDFs here are ordinary business documents (statement, support attachment, compliance journal, retention register). A document that names a resource (by id or by a catalog alias such
/// as a statement number) shows that the record exists at capture time. The register of disputed orders names no resource id, so it yields nothing - exactly like the labeled samples,
/// whose users appear in it but have no legal-hold step.
/// </summary>
public static class PdfObs
{
    public static string PdfText(string path)
    {
        using var doc = PdfDocument.Open(path);
        return string.Join("\n", doc.GetPages().Select(p => ContentOrderTextExtractor.GetText(p)));
    }

    public static void TextArtifactObs(EvidenceBase b, string fileId, DateTime fileT, string text, IReadOnlyList<CatalogRow> catalog, string source)
    {
        var low = text.ToLowerInvariant();
        var idents = Records.IdentsFrom(new[] { ("text", text) });
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in Records.ResourceRe.Matches(text))
        {
            var rid = m.Value;
            if (!seen.Add(rid)) continue;
            var prefix = Records.Prefix(rid);
            string category, state;
            if (prefix == "support") (category, state) = low.Contains("attachment", StringComparison.Ordinal) ? ("SUPPORT_ATTACHMENT", "PRESENT") : ("SUPPORT_MESSAGE", "EXPOSED");
            else if (prefix == "audit_record") (category, state) = ("SECURITY_AUDIT_LOG", "PRESENT");
            else if (prefix == "export") (category, state) = ("USER_EXPORT", "PRESENT");
            else continue;                                   // other prefixes in free text are references, not records we can judge
            b.Obs.Add(new Obs(fileId, fileT, fileT, Records.SystemFor(rid), rid, category, state, idents, source));
        }
        foreach (var row in catalog)                         // resources the document mentions only through a catalog alias (e.g. a statement number)
        {
            if (row.Alias.Length == 0 || row.Alias == row.ResourceId || !low.Contains(row.Alias.ToLowerInvariant(), StringComparison.Ordinal) || seen.Contains(row.ResourceId)) continue;
            seen.Add(row.ResourceId);
            var aliasCategory = Records.Prefix(row.ResourceId) switch { "export" => "USER_EXPORT", "object" => "SUPPORT_ATTACHMENT", _ => null };
            if (aliasCategory is null) continue;
            var joined = new HashSet<string>(idents, StringComparer.Ordinal);
            joined.UnionWith(row.Idents);
            b.Obs.Add(new Obs(fileId, fileT, fileT, row.System, row.ResourceId, aliasCategory, "PRESENT", joined, source + ":alias"));
        }
    }

    public static void LoadPdf(EvidenceBase b, string fileId, string path, DateTime fileT, IReadOnlyList<CatalogRow> catalog) =>
        TextArtifactObs(b, fileId, fileT, PdfText(path), catalog, "pdf");
}
