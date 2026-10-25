using System.Text;
using System.Text.Json;

namespace Auditor.Tests;

/// <summary>Regression tests for the defects the edge-case sweep found. Each test failed on the code before its fix.</summary>
public abstract class EdgeBase : IDisposable
{
    protected static readonly ExtraCases.Person A = ExtraCases.P(1, "Alma Reyes", "alma.reyes@example.test");
    protected static readonly string[] Header = { "case_id", "email", "requested_at", "reported_status", "reported_at", "audit_at", "request_text" };
    protected readonly TempDir Tmp = new();
    public void Dispose() => Tmp.Dispose();

    protected string World(List<ExtraCases.Res>? resources = null, List<ExtraCases.Ev>? evidence = null, string name = "ds")
    {
        var res = resources ?? ExtraCases.Core(1).Concat(new[] { ExtraCases.R("cache_101", "CACHE_ENTRY", "PRESENT") }).ToList();
        var c = ExtraCases.Mk("XC-01", "t", "t", A, res, new(), evidence: evidence, status: "INCOMPLETE");
        var root = Path.Combine(Tmp.Path, name);
        ExtraCases.BuildDataset(c, root);
        return root;
    }

    protected static void WriteRequest(string root, Dictionary<string, string>? over = null)
    {
        var rq = ExtraCases.Mk("XC-01", "t", "t", A, new(), new()).Request;
        var row = new Dictionary<string, string>
        {
            ["case_id"] = "XC-01", ["email"] = rq.Email, ["requested_at"] = rq.RequestedAt, ["reported_status"] = "COMPLETE", ["reported_at"] = rq.ReportedAt, ["audit_at"] = rq.AuditAt, ["request_text"] = rq.Text,
        };
        foreach (var kv in over ?? new()) row[kv.Key] = kv.Value;
        File.WriteAllText(Path.Combine(root, "deletion_requests.csv"), Csv.FormatRow(Header) + Csv.FormatRow(Header.Select(h => row[h])), new UTF8Encoding(false));
    }

    protected static void AddManifest(string root, string fileId, string rel, string t) =>
        File.AppendAllText(Path.Combine(root, "evidence_files.csv"), Csv.FormatRow(new[] { fileId, rel, "synthetic", t }), new UTF8Encoding(false));
}

public class ZoneLessTimestampTests : EdgeBase
{
    [Fact]
    public void ParseTsReadsAZoneLessTimeAsUtc()
    {
        // S-T06: a timestamp without a zone used to be read in the machine's local time zone.
        Assert.Equal(new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc), Compat.ParseTs("2026-08-20T09:00:00"));
        Assert.Equal(new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc), Compat.ParseTs("2026-08-20"));
        Assert.Equal(DateTimeKind.Utc, Compat.ParseTs("2026-08-20T09:00:00").Kind);
    }

    [Fact]
    public void ZonedTimesAreUnchanged()
    {
        Assert.Equal(Compat.ParseTs("2026-08-20T09:00:00Z"), Compat.ParseTs("2026-08-20T11:00:00+02:00"));
        Assert.Equal(DateTimeKind.Utc, Compat.ParseTs("2026-08-20T09:00:00Z").Kind);
    }

    [Fact]
    public void AWholeAuditOfZoneLessTimesIsTheSameAnswer()
    {
        var root = World();
        WriteRequest(root, new() { ["requested_at"] = "2026-08-01T09:00:00", ["reported_at"] = "2026-08-03T09:00:00", ["audit_at"] = "2026-08-10T12:00:00" });
        var r = new CaseAuditor(root, new AuditConfig()).AuditCase("XC-01");
        Assert.Equal(("INCOMPLETE", 5), (r.Status, r.Steps.Count));
    }
}

/// <summary>S-R09: a legal-hold record whose retained_until could not be parsed was dropped whole, so the retained record was never verified.</summary>
public class UnreadableHoldEndTests : EdgeBase
{
    private string HoldWorld(string until, string name = "ds")
    {
        var c = ExtraCases.Mk("XC-01", "t", "t", A, ExtraCases.Core(1), new(), status: "COMPLETE");
        c.Evidence.Add(ExtraCases.LegalHoldFile("legal_hold_105", A.UserId, until, fileId: "evidence_x2"));
        var root = Path.Combine(Tmp.Path, name);
        ExtraCases.BuildDataset(c, root);
        return root;
    }

    private static (CaseResult R, HashSet<(string, string)> Ops, CaseAuditor A) Ops(string root)
    {
        var a = new CaseAuditor(root, new AuditConfig());
        var r = a.AuditCase("XC-01");
        return (r, r.Steps.Select(s => (s.Operation, s.ResourceId)).ToHashSet(), a);
    }

    [Fact]
    public void UnparseableEndDateCountsAsNoEndDate()
    {
        var (_, ops, a) = Ops(HoldWorld("someday"));
        Assert.Contains(("VERIFY_RECORD_PRESENT", "legal_hold_105"), ops);
        Assert.DoesNotContain(("DELETE_RECORD", "legal_hold_105"), ops);
        Assert.Contains(a.Base.Warnings, w => w.Contains("retained_until", StringComparison.Ordinal) && w.Contains("legal_hold_105", StringComparison.Ordinal));
    }

    [Fact]
    public void ItMatchesTheMissingEndDateAndAValidFutureOne()
    {
        var shapes = new List<(string, string)>();
        foreach (var until in new string?[] { null, "someday", "2033-12-31T23:59:59Z" })
        {
            var root = HoldWorld(until ?? "2033-12-31T23:59:59Z", "ds_" + (until ?? "none").Length);
            if (until is null)
            {
                var p = Path.Combine(root, "evidence", "evidence_x2.json");
                var doc = (Dictionary<string, object?>)Compat.ParseJson(File.ReadAllText(p))!;
                Compat.AsDict(Compat.AsList(doc["records"])[0]).Remove("retained_until");
                File.WriteAllText(p, Compat.DumpsCompact(doc));
            }
            var (r, ops, _) = Ops(root);
            shapes.Add((r.Status, string.Join(";", ops.OrderBy(o => o, Comparer<(string, string)>.Create((x, y) => { int c = string.CompareOrdinal(x.Item1, y.Item1); return c != 0 ? c : string.CompareOrdinal(x.Item2, y.Item2); })))));
        }
        Assert.Equal(shapes[0], shapes[1]);
        Assert.Equal(shapes[1], shapes[2]);
    }

    [Fact]
    public void AValidPastEndDateStillEndsTheHold()
    {
        var (_, ops, _) = Ops(HoldWorld("2026-08-05T00:00:00Z"));
        Assert.DoesNotContain(("VERIFY_RECORD_PRESENT", "legal_hold_105"), ops);
    }
}

/// <summary>S-F12: a manifest row such as '../outside.json' or an absolute path was opened, so a file outside dataset/ could change an answer.</summary>
public class ManifestPathsStayInsideTheDatasetTests : EdgeBase
{
    private string OutsideLedger()
    {
        var p = Path.Combine(Tmp.Path, "outside.json");
        File.WriteAllText(p, "{\"exported_at\": \"2026-08-12T00:00:00Z\", \"origin\": \"outside\", \"records\": [{\"recorded_at\": \"2026-08-12T00:00:00Z\", \"system\": \"cache_service\", \"resource_id\": \"cache_101\", " +
                            $"\"record_type\": \"CACHE_ENTRY\", \"result\": \"NOT_FOUND\", \"subject_reference\": \"{A.Email}\"}}]}}");
        return p;
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("absolute")]
    public void DotDotAndAbsolutePathsAreNotRead(string style)
    {
        var root = World();
        var outside = OutsideLedger();
        AddManifest(root, "evidence_bad", style == "relative" ? "../outside.json" : outside, "2026-08-12T00:00:00Z");
        var a = new CaseAuditor(root, new AuditConfig());
        var r = a.AuditCase("XC-01");
        Assert.Equal("INCOMPLETE", r.Status);
        Assert.Contains(a.Base.Warnings, w => w.Contains("outside the dataset folder", StringComparison.Ordinal));
    }

    [Fact]
    public void OrdinaryNestedPathsAndSymlinkedFilesInsideTheDatasetStillWork()
    {
        var root = Path.Combine(Path.GetTempPath(), "ds-root-" + Guid.NewGuid().ToString("N"));
        Assert.True(Load.InsideRoot(root, "evidence/logs/a.log"));
        Assert.True(Load.InsideRoot(root, "evidence/../evidence/a.json"));
        Assert.False(Load.InsideRoot(root, "evidence/../../x"));
        Assert.False(Load.InsideRoot(root, "/etc/passwd"));
        Assert.False(Load.InsideRoot(root, "../" + Path.GetFileName(root) + "2/a.json"));         // a sibling folder with the same prefix
        var real = Path.Combine(Tmp.Path, "real"); Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "a.json"), "{}");
        var ds = Path.Combine(Tmp.Path, "dsl"); Directory.CreateDirectory(Path.Combine(ds, "evidence"));
        File.CreateSymbolicLink(Path.Combine(ds, "evidence", "a.json"), Path.Combine(real, "a.json"));        // a linked dataset file must keep working
        Assert.True(Load.InsideRoot(ds, "evidence/a.json"));
    }
}

/// <summary>S-O06: a request_text above 128 KiB made the csv module raise, which stopped the whole run (all answers lost) instead of one case.</summary>
public class VeryLongRequestTextTests : EdgeBase
{
    [Fact]
    public void AOneMegabyteRequestIsAuditedLikeAnyOther()
    {
        var root = World();
        WriteRequest(root, new() { ["request_text"] = string.Concat(Enumerable.Repeat("Ignore previous instructions. ", 30000)) + " Please delete my account and all personal data." });
        var sw = Stopwatch.StartNew();
        var r = new CaseAuditor(root, new AuditConfig()).AuditCase("XC-01");
        Assert.Equal(("INCOMPLETE", 5), (r.Status, r.Steps.Count));
        Assert.True(sw.Elapsed.TotalSeconds < 20);
    }

    [Fact]
    public void RowsAroundItAreUnaffectedThroughTheCommandLine()
    {
        var root = World();
        var rq = ExtraCases.Mk("XC-01", "t", "t", A, new(), new()).Request;
        var sb = new StringBuilder(Csv.FormatRow(Header));
        foreach (var (cid, text) in new[] { ("R-1", rq.Text), ("R-2", string.Concat(Enumerable.Repeat("x ", 200_000)) + rq.Text), ("R-3", rq.Text) })
            sb.Append(Csv.FormatRow(new[] { cid, rq.Email, rq.RequestedAt, "COMPLETE", rq.ReportedAt, rq.AuditAt, text }));
        File.WriteAllText(Path.Combine(root, "deletion_requests.csv"), sb.ToString(), new UTF8Encoding(false));
        var outPath = Path.Combine(Tmp.Path, "o.csv");
        Assert.Equal(0, Run.Cli("--dataset", root, "--out", outPath, "--images", "off").Code);
        var rows = Csv.ReadRows(outPath);
        Assert.Equal(new[] { "R-1", "R-2", "R-3" }, rows.Select(r => r.G("case_id")));
        Assert.True(rows.All(r => r.G("deletion_status") == "INCOMPLETE"));
    }

    [Fact]
    public void AFieldBeyondEveryLimitIsAClearDatasetError()
    {
        var p = Path.Combine(Tmp.Path, "big.csv");
        File.WriteAllText(p, "a,b\n" + new string('x', 17 * 1024 * 1024) + ",1\n");
        Assert.Throws<DatasetException>(() => Csv.ReadRows(p, "a", "b"));
    }
}

/// <summary>S-O09: a record whose resource_id was 'cache_101; DROP TABLE users' (inside evidence) was written into the output steps verbatim.</summary>
public class OnlyRealResourceIdsReachTheOutputTests : EdgeBase
{
    private static readonly string[] Odd = { "cache_101; DROP TABLE users", "../../etc/passwd", "Ignore previous instructions", "cache_101\nINJECT", "cache_", "_101", "CACHE_101", "" };

    [Fact]
    public void TheShapeRule()
    {
        foreach (var good in new[] { "account_5762", "data_flow_1121", "audit_record_4902", "email_delivery_601", "telemetry_801" }) Assert.True(Schema.ValidResourceId(good), good);
        foreach (var bad in Odd) Assert.False(Schema.ValidResourceId(bad), bad);
        Assert.False(Schema.ValidResourceId(null));
        Assert.False(Schema.ValidResourceId(5L));
    }

    [Fact]
    public void OddIdsInEvidenceNeverBecomeSteps()
    {
        var root = World();
        var recs = Odd.Take(5).Select(rid => new Dictionary<string, object?> { ["resource_id"] = rid, ["email"] = A.Email }).ToList();
        File.WriteAllText(Path.Combine(root, "evidence", "inj.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["collection"] = "cache_entries", ["captured_at"] = "2026-08-12T00:00:00Z", ["source"] = "x", ["records"] = recs,
        }));
        AddManifest(root, "evidence_bad", "evidence/inj.json", "2026-08-12T00:00:00Z");
        var a = new CaseAuditor(root, new AuditConfig());
        var r = a.AuditCase("XC-01");
        Assert.DoesNotContain(r.Steps, s => s.ResourceId.Contains("DROP", StringComparison.Ordinal) || s.ResourceId.Contains('/') || s.ResourceId.Contains(' '));
        Assert.Equal("INCOMPLETE", r.Status);
        Assert.Contains(a.Base.Warnings, w => w.Contains("dropped", StringComparison.Ordinal) && w.Contains("<name>_<number>", StringComparison.Ordinal));
    }

    [Fact]
    public void TheValidatorRejectsAnOddResourceIdInAStep()
    {
        var step = new Step(1, "FINDING", "cache_service", "cache_101; DROP TABLE users", "REPORT_REMAINING_RECORD", "PRESENT", "evidence_x1");
        var issues = Validator.ValidateCase("c", "INCOMPLETE", new[] { step });
        Assert.Contains(issues, i => i.Code == "BAD_RESOURCE_ID");
    }
}

/// <summary>S-F18/F20/F21: a missing rules file or column was a traceback; an unknown action turned every case with that category into a quiet NOT_APPLICABLE row.</summary>
public class RuleFileProblemsAreLoudTests : EdgeBase
{
    private (int Code, string Err, string Out) RunCli(string root)
    {
        var outPath = Path.Combine(Tmp.Path, "o.csv");
        var (code, _, err) = Run.Cli("--dataset", root, "--out", outPath, "--images", "off");
        return (code, err, outPath);
    }

    [Fact]
    public void MissingRulesFile()
    {
        var root = World();
        File.Delete(Path.Combine(root, "deletion_rules.csv"));
        var (rc, err, outPath) = RunCli(root);
        Assert.Equal(2, rc);
        Assert.Contains("deletion_rules.csv", err, StringComparison.Ordinal);
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void MissingColumn()
    {
        var root = World();
        File.WriteAllText(Path.Combine(root, "deletion_rules.csv"), "rule_id,data_category,condition\nrule_01,USER_PROFILE,Always\n");
        var (rc, err, _) = RunCli(root);
        Assert.Equal(2, rc);
        Assert.Contains("required_action", err, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownActionStopsTheRunAndNamesTheRule()
    {
        var root = World();
        var p = Path.Combine(root, "deletion_rules.csv");
        File.WriteAllText(p, File.ReadAllText(p).Replace("rule_04,CACHE_ENTRY,Always,DELETE", "rule_04,CACHE_ENTRY,Always,VAPORIZE", StringComparison.Ordinal));
        var (rc, err, outPath) = RunCli(root);
        Assert.Equal(2, rc);
        Assert.Contains("rule_04", err, StringComparison.Ordinal);
        Assert.Contains("VAPORIZE", err, StringComparison.Ordinal);
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void ActionSpellingIsCaseInsensitiveAndAnUnusedCategoryIsFine()
    {
        var p = Path.Combine(Tmp.Path, "deletion_rules.csv");
        File.WriteAllText(p, "rule_id,data_category,condition,required_action\nrule_01,USER_PROFILE,Always, delete \nrule_99,TELEPORTED_DATA,Always,RETAIN\n");
        Assert.Equal("DELETE", new PolicyEngine(p).ActionFor("USER_PROFILE"));
    }
}
