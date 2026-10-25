using System.Text;

namespace Auditor;

/// <summary>Reading and writing output.csv exactly like the sample answers: LF endings, quoting only where needed, compact JSON in steps_json (no spaces).</summary>
public static class OutputIo
{
    public sealed record Row(string CaseId, string Status, IReadOnlyList<Step> Steps);

    public static string DumpsSteps(IEnumerable<Step> steps) => Compat.DumpsCompact(steps.Select(s => s.ToJson()).ToList());

    public static void Write(string path, IEnumerable<Row> rows)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        var sb = new StringBuilder(Csv.FormatRow(Schema.OutputHeader));
        foreach (var r in rows) sb.Append(Csv.FormatRow(new[] { r.CaseId, r.Status, DumpsSteps(r.Steps) }));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Header and raw string rows (no JSON parsing) so that the validator can report bad JSON per case.</summary>
    public static (List<string> Header, List<List<string>> Rows) ReadRaw(string path)
    {
        var rows = Csv.Parse(new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)));
        return rows.Count == 0 ? (new List<string>(), new List<List<string>>()) : (rows[0], rows.Skip(1).ToList());
    }
}
