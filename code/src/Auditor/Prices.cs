using System.Text.RegularExpressions;

namespace Auditor;

/// <summary>
/// Model prices, supplied by the user - never hard-coded, never guessed.
/// File format (JSON), default <c>code/config/prices.json</c>, override with <c>AUDITOR_PRICES_FILE</c>:
///   {"claude-haiku-5-5": {"input_per_mtok": 0.0, "output_per_mtok": 0.0, "cache_read_per_mtok": 0.0, "source_url": "https://...", "date": "YYYY-MM-DD"}}
/// A model entry counts as supplied only when it has numeric input_per_mtok and output_per_mtok (&gt;= 0), an http(s) source_url and an ISO date.
/// Anything else (missing file, null values, no source or date) is "prices not supplied", and reports then say exactly: "cost not computed - prices not supplied".
/// </summary>
public static class Prices
{
    public const string NotSupplied = "cost not computed - prices not supplied";

    /// <summary><c>code/config/prices.json</c> of the repository the process runs in (found by walking up from the working directory), else next to the binaries. The file is local and never committed.</summary>
    public static string DefaultPath
    {
        get
        {
            for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "dataset"))) return Path.Combine(dir.FullName, "code", "config", "prices.json");
            return Path.Combine(AppContext.BaseDirectory, "config", "prices.json");
        }
    }

    public sealed record Price(double InputPerMtok, double OutputPerMtok, double? CacheReadPerMtok, string SourceUrl, string Date)
    {
        public double Cost(long inputTokens, long outputTokens, long cacheReadTokens = 0)
        {
            double cacheRate = CacheReadPerMtok ?? InputPerMtok;
            long fresh = Math.Max(inputTokens - cacheReadTokens, 0);
            return (fresh * InputPerMtok + cacheReadTokens * cacheRate + outputTokens * OutputPerMtok) / 1e6;
        }
    }

    private static bool Num(object? v) => v is long or double && Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) >= 0;

    private static readonly Regex UrlRe = new(@"^https?://\S+$", RegexOptions.CultureInvariant);
    private static readonly Regex DateRe = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant);

    /// <summary>(supplied prices by model, problems). A model with any problem is simply absent from the first dictionary.</summary>
    public static (Dictionary<string, Price> Prices, List<string> Problems) Parse(Dictionary<string, object?> doc)
    {
        var prices = new Dictionary<string, Price>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (model, entry) in doc.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (model.StartsWith('_') || entry is not Dictionary<string, object?> e) continue;
            var bad = new[] { "input_per_mtok", "output_per_mtok" }.Where(k => !Num(Py.Get(e, k))).ToList();
            var cache = Py.Get(e, "cache_read_per_mtok");
            if (cache is not null && !Num(cache)) bad.Add("cache_read_per_mtok");
            if (!UrlRe.IsMatch(Py.Str(Py.Or(Py.Get(e, "source_url"), ""))) ) bad.Add("source_url");
            if (!DateRe.IsMatch(Py.Str(Py.Or(Py.Get(e, "date"), "")))) bad.Add("date");
            if (bad.Count > 0) { problems.Add($"{model}: missing or invalid {string.Join(", ", bad)}"); continue; }
            prices[model] = new Price(Convert.ToDouble(e["input_per_mtok"], System.Globalization.CultureInfo.InvariantCulture), Convert.ToDouble(e["output_per_mtok"], System.Globalization.CultureInfo.InvariantCulture),
                                      cache is null ? null : Convert.ToDouble(cache, System.Globalization.CultureInfo.InvariantCulture), (string)e["source_url"]!, (string)e["date"]!);
        }
        return (prices, problems);
    }

    public static (Dictionary<string, Price> Prices, List<string> Problems) Load(string? path = null)
    {
        var p = path ?? Environment.GetEnvironmentVariable("AUDITOR_PRICES_FILE");
        if (string.IsNullOrEmpty(p)) p = DefaultPath;
        if (!File.Exists(p)) return (new(), new() { $"no price file at {p}" });
        try { return Parse(Py.AsDict(Py.ParseJson(Py.ReadText(p)))); }
        catch (Exception e) when (e is IOException or DataException) { return (new(), new() { $"unreadable price file {p}: {e.Message}" }); }
    }
}
