using System.Xml.Linq;

namespace Auditor.Tools;

/// <summary>Reads the Visual Studio test result file (TRX) that <c>dotnet test --logger trx</c> writes: per test class, how many tests ran, failed and were skipped.</summary>
public sealed class TrxSummary
{
    public sealed record Counts(int Total, int Failed, int Skipped);

    public Dictionary<string, Counts> ByClass { get; } = new(StringComparer.Ordinal);
    public int Total => ByClass.Values.Sum(c => c.Total);
    public int Failed => ByClass.Values.Sum(c => c.Failed);
    public int Skipped => ByClass.Values.Sum(c => c.Skipped);

    /// <summary>The names of the failed tests (class.method) of a TRX file.</summary>
    public static List<string> FailedTests(string path)
    {
        var doc = XDocument.Load(path);
        XNamespace ns = doc.Root!.Name.Namespace;
        return doc.Descendants(ns + "UnitTestResult").Where(r => (string?)r.Attribute("outcome") == "Failed").Select(r => (string)r.Attribute("testName")!).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    public static string DefaultPath(string root) => Path.Combine(root, "code", "out", "tests.trx");

    public static TrxSummary? TryRead(string path)
    {
        if (!File.Exists(path)) return null;
        var doc = XDocument.Load(path);
        XNamespace ns = doc.Root!.Name.Namespace;
        var classOf = doc.Descendants(ns + "UnitTest").ToDictionary(u => (string)u.Attribute("id")!, u => ((string)u.Element(ns + "TestMethod")!.Attribute("className")!).Split('.')[^1].Split('+')[0]);
        var summary = new TrxSummary();
        foreach (var r in doc.Descendants(ns + "UnitTestResult"))
        {
            var cls = classOf.GetValueOrDefault((string)r.Attribute("testId")!, "?");
            var outcome = (string?)r.Attribute("outcome") ?? "";
            var c = summary.ByClass.GetValueOrDefault(cls, new Counts(0, 0, 0));
            summary.ByClass[cls] = new Counts(c.Total + 1, c.Failed + (outcome == "Failed" ? 1 : 0), c.Skipped + (outcome is "NotExecuted" or "Skipped" ? 1 : 0));
        }
        return summary;
    }

    public Counts Of(IEnumerable<string> classNames)
    {
        var cs = classNames.Select(n => ByClass.GetValueOrDefault(n, new Counts(0, 0, 0))).ToList();
        return new Counts(cs.Sum(c => c.Total), cs.Sum(c => c.Failed), cs.Sum(c => c.Skipped));
    }
}
