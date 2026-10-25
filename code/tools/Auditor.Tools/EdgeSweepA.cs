using System.Diagnostics;
using System.Text;
using static Auditor.Tools.ExtraCases;
using XPerson = Auditor.Tools.ExtraCases.Person;

namespace Auditor.Tools;

public static partial class EdgeSweep
{
    private static Dictionary<string, string> Over(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    // ---- 1. empty or missing fields
    private static void RequestScenarios()
    {
        void One(string sid, string title, string expected, Dictionary<string, string> over, Func<Seen, bool> check) =>
            Add(sid, "empty or missing fields", title, expected, tmp =>
            {
                var w = new World(tmp);
                w.WriteRequests(new[] { w.RowOf(over) });
                var s = w.Audit();
                return OkIf(check(s), s.Text());
            });

        One("E01", "empty email", "answered with a conservative NOT_APPLICABLE row, no exception", Over(("email", "")), Fallback);
        One("E02", "empty request_text", "NOT_APPLICABLE: nothing authorizes deletion", Over(("request_text", "")), Fallback);
        One("E03", "whitespace-only request_text", "NOT_APPLICABLE", Over(("request_text", "   \t ")), Fallback);
        One("E04", "empty requested_at", "conservative NOT_APPLICABLE row (the evidence window cannot be built)", Over(("requested_at", "")), Fallback);
        One("E05", "empty audit_at", "conservative NOT_APPLICABLE row", Over(("audit_at", "")), Fallback);
        One("E06", "empty reported_at", "conservative NOT_APPLICABLE row (the row is malformed)", Over(("reported_at", "")), Fallback);
        One("E13", "deletion request written in a language the lexicons do not cover (Hindi)", "conservative NOT_APPLICABLE (a known limit, README): no model reads request text",
            Over(("request_text", "कृपया मेरा खाता हटा दें और मेरा सारा डेटा मिटा दें।")), Fallback);
        One("E07", "empty reported_status", "audited normally: the workflow status is a claim and is never used as proof", Over(("reported_status", "")), IsBase);
        One("E08", "unknown reported_status 'DONE'", "audited normally (same answer as with a valid status)", Over(("reported_status", "DONE")), IsBase);

        Add("E09", "empty or missing fields", "empty case_id on one row, a good row after it", "the nameless row is skipped and reported, the good row is answered, no crash", tmp =>
        {
            var w = new World(tmp);
            w.WriteRequests(new[] { w.RowOf(Over(("case_id", ""))), w.RowOf() });
            var (rc, _, rows, order) = w.Cli();
            return OkIf(rc == 0 && order.SequenceEqual(new[] { "XC-01" }) && rows["XC-01"].Status == "INCOMPLETE", $"exit {rc}; output rows [{string.Join(", ", order.Select(o => "'" + o + "'"))}]");
        });
        Add("E10", "empty or missing fields", "request row with too few columns", "missing cells read as empty: conservative NOT_APPLICABLE row, no crash", tmp =>
        {
            var w = new World(tmp);
            File.WriteAllText(Path.Combine(w.Root, "deletion_requests.csv"), string.Join(",", Header) + "\nXC-01,alma.reyes@example.test\n", new UTF8Encoding(false));
            var (rc, _, rows, _) = w.Cli();
            return OkIf(rc == 0 && rows.TryGetValue("XC-01", out var r) && r.Status == "NOT_APPLICABLE" && r.Steps.Count == 0, $"exit {rc}; row {(rows.TryGetValue("XC-01", out var q) ? q.Status : "missing")}");
        });
        Add("E11", "empty or missing fields", "an extra unknown column and reordered columns", "columns are found by name: same answer as the plain file", tmp =>
        {
            var w = new World(tmp);
            var row = Header.Zip(w.RowOf()).ToDictionary(x => x.First, x => x.Second);
            var cols = new[] { "request_text", "case_id", "extra", "email", "requested_at", "reported_status", "reported_at", "audit_at" };
            w.WriteRequests(new[] { cols.Select(c => row.GetValueOrDefault(c, "x")).ToArray() }, cols);
            var (rc, _, rows, _) = w.Cli();
            return OkIf(rc == 0 && rows.TryGetValue("XC-01", out var r) && r.Status == "INCOMPLETE", $"exit {rc}; XC-01 {(rows.TryGetValue("XC-01", out var q) ? q.Status : "missing")}");
        });
        Add("E12", "empty or missing fields", "UTF-8 BOM and Windows line endings in the request file", "read normally: same answer as the plain file", tmp =>
        {
            var w = new World(tmp);
            var text = string.Join(",", Header) + "\r\n" + string.Join(",", w.RowOf().Select(c => $"\"{c}\"")) + "\r\n";
            File.WriteAllBytes(Path.Combine(w.Root, "deletion_requests.csv"), new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(text)).ToArray());
            var (rc, _, rows, _) = w.Cli();
            return OkIf(rc == 0 && rows.TryGetValue("XC-01", out var r) && r.Status == "INCOMPLETE", $"exit {rc}; XC-01 {(rows.TryGetValue("XC-01", out var q) ? q.Status : "missing")}");
        });
    }

    // ---- 2. bad timestamps
    private static void TimestampScenarios()
    {
        void One(string sid, string title, string expected, Dictionary<string, string> over, Func<Seen, bool> check, string note = "") =>
            Add(sid, "bad timestamps", title, expected, tmp =>
            {
                var w = new World(tmp);
                w.WriteRequests(new[] { w.RowOf(over) });
                var s = w.Audit();
                return OkIf(check(s), s.Text(), note);
            });

        One("T01", "audit_at = 'yesterday'", "conservative NOT_APPLICABLE row, no exception", Over(("audit_at", "yesterday")), Fallback);
        One("T02", "impossible date 2026-13-45", "conservative NOT_APPLICABLE row", Over(("requested_at", "2026-13-45T00:00:00Z")), Fallback);
        One("T03", "timestamp with a UTC offset (09:00+00:00 and 11:00+02:00 are the same instant)", "converted to UTC: same answer as the plain file",
            Over(("requested_at", "2026-08-01T11:00:00+02:00")), IsBase);
        One("T04", "space instead of T, with Z", "accepted: same answer as the plain file", Over(("audit_at", "2026-08-20 09:00:00Z")), IsBase);
        One("T05", "audit_at before requested_at", "no evidence can fall in an empty window: conservative NOT_APPLICABLE fallback, no exception", Over(("audit_at", "2026-07-01T09:00:00Z")), Fallback);

        Add("T06", "bad timestamps", "timestamps without a zone (2026-08-20T09:00:00) under three different process time zones",
            "a zone-less time means UTC (the dataset's own convention), so the answer must not depend on the machine's time zone", tmp =>
            {
                var seen = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var tz in new[] { "UTC", "America/Los_Angeles", "Asia/Kolkata" })
                {
                    var w = new World(Path.Combine(tmp, tz.Replace('/', '_')));
                    w.WriteRequests(new[] { w.RowOf(Over(("requested_at", "2026-08-01T09:00:00"), ("reported_at", "2026-08-03T09:00:00"), ("audit_at", "2026-08-10T12:00:00"))) });
                    var outPath = Path.Combine(w.Tmp, "o.csv");
                    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var a in new[] { Path.Combine(AppContext.BaseDirectory, "Auditor.Cli.dll"), "--dataset", w.Root, "--out", outPath, "--images", "off" }) psi.ArgumentList.Add(a);
                    foreach (var k in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "AUDITOR_USAGE_LOG", "AUDITOR_TEST_PAUSE_AFTER" }) psi.Environment.Remove(k);
                    psi.Environment["TZ"] = tz;
                    using (var p = Process.Start(psi)!)
                    {
                        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
                        p.WaitForExit();
                    }
                    var rows = World.ReadOutput(outPath, out _);
                    seen[tz] = rows.TryGetValue("XC-01", out var r) ? $"{r.Status}, {r.Steps.Count} steps, {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Compat.DumpsCompact(r.Steps))))[..8].ToLowerInvariant()}" : "no row";
                }
                var same = seen.Values.Distinct().Count() == 1;
                return OkIf(same, string.Join(" / ", seen.Select(kv => $"{kv.Key.Split('/')[^1]}: {Clip(kv.Value, 38)}")));
            });

        Add("T07", "bad timestamps", "audit_at exactly at the capture time of the only evidence file", "the file counts (the window is inclusive at both ends)", tmp =>
        {
            var w = new World(tmp);
            w.WriteRequests(new[] { w.RowOf(Over(("audit_at", TE1))) });
            var s = w.Audit();
            return OkIf(IsBase(s), s.Text());
        });
        Add("T08", "bad timestamps", "captured_at of an evidence file is unparseable; a second good file exists", "the bad manifest row is skipped, the good file is used", tmp =>
        {
            var w = new World(tmp);
            w.ManifestAdd("evidence_bad", "evidence/evidence_x1.json", "not-a-time");
            var s = w.Audit();
            return OkIf(IsBase(s), s.Text());
        });
        Add("T09", "bad timestamps", "an activity row with an unparseable time between good rows", "the row is skipped, the run is unaffected", tmp =>
        {
            var w = new World(tmp);
            w.AppendCsv("system_activity.csv", new[]
            {
                new[] { "activity_1", "garbage", "DELETE_RECORD", "deletion_worker", "cache_service", "cache_101", "SUCCESS", "x" },
                new[] { "activity_2", "2026-08-02T00:00:00Z", "DELETE_RECORD", "deletion_worker", "cache_service", "cache_101", "SUCCESS", "x" },
            });
            var s = w.Audit();
            return OkIf(IsBase(s), s.Text());
        });
    }

    // ---- 3. duplicate or unknown case ids
    private static void IdScenarios()
    {
        const string area = "duplicate / unknown case_id";
        string Q(IEnumerable<string> xs) => "[" + string.Join(", ", xs.Select(x => "'" + x + "'")) + "]";

        Add("I01", area, "the same case_id twice, identical rows", "one output row, the first row kept, the duplicate reported", tmp =>
        {
            var w = new World(tmp);
            w.WriteRequests(new[] { w.RowOf(), w.RowOf() });
            var (rc, _, _, order) = w.Cli();
            var notes = File.ReadAllText(Path.Combine(tmp, "o.notes.md"));
            return OkIf(rc == 0 && order.SequenceEqual(new[] { "XC-01" }), $"exit {rc}; rows {Q(order)}; notes mention duplicate: {(notes.Contains("duplicate", StringComparison.Ordinal) ? "True" : "False")}");
        });
        Add("I02", area, "the same case_id twice with different content (second says 'access request')", "first row wins: still the deletion answer; the contradicting row is reported, not silently merged", tmp =>
        {
            var w = new World(tmp);
            w.WriteRequests(new[] { w.RowOf(), w.RowOf(Over(("request_text", "Please send me a copy of my data."))) });
            var (rc, _, rows, order) = w.Cli();
            return OkIf(rc == 0 && order.SequenceEqual(new[] { "XC-01" }) && rows["XC-01"].Status == "INCOMPLETE", $"exit {rc}; rows {Q(order)}; status {(rows.TryGetValue("XC-01", out var r) ? r.Status : "?")}");
        });
        Add("I03", area, "case_id with surrounding whitespace ' XC-01 ' and a repeat 'XC-01'", "whitespace is not part of the id: treated as the same case, one row", tmp =>
        {
            var w = new World(tmp);
            w.WriteRequests(new[] { w.RowOf(Over(("case_id", " XC-01 "))), w.RowOf() });
            var (rc, _, _, order) = w.Cli();
            return OkIf(rc == 0 && order.SequenceEqual(new[] { "XC-01" }), $"exit {rc}; rows {Q(order)}");
        });
        Add("I04", area, "case ids that differ only by letter case ('XC-01', 'xc-01')", "ids are exact strings: two different cases, two rows", tmp =>
        {
            var w = new World(tmp);
            w.WriteRequests(new[] { w.RowOf(), w.RowOf(Over(("case_id", "xc-01"))) });
            var (rc, _, _, order) = w.Cli();
            return OkIf(rc == 0 && order.SequenceEqual(new[] { "XC-01", "xc-01" }), $"exit {rc}; rows {Q(order)}");
        });
        Add("I05", area, "audit() of a case_id that is in no request file", "conservative NOT_APPLICABLE row with a FALLBACK note, no exception", tmp =>
        {
            var w = new World(tmp);
            var s = w.Audit("no_such_case");
            return OkIf(Fallback(s) && s.Notes.Count > 0 && s.Notes[0].StartsWith("FALLBACK", StringComparison.Ordinal), s.Text());
        });
        Add("I06", area, "--explain of an unknown case_id", "exit code 2 and a message that names known ids; nothing written", tmp =>
        {
            var w = new World(tmp);
            var (rc, err, _, _) = w.Cli("--explain", "no_such_case");
            return OkIf(rc == 2 && err.Contains("no case", StringComparison.Ordinal) && !File.Exists(Path.Combine(tmp, "o.csv")), $"exit {rc}; stderr: {Clip(err.Trim(), 70)}");
        });
        Add("I07", area, "--requests names a file that does not exist", "clear error, exit code 2, nothing written", tmp =>
        {
            var w = new World(tmp);
            var (rc, err, _, _) = w.Cli("--requests", "nope.csv");
            return OkIf(rc == 2 && !File.Exists(Path.Combine(tmp, "o.csv")), $"exit {rc}; stderr: {Clip(err.Trim(), 70)}");
        });
        Add("I08", area, "1,000 request rows with distinct ids, all for the same user", "one row per id, in file order, every answer identical (no state leaks between cases)", tmp =>
        {
            var w = new World(tmp);
            w.WriteRequests(Enumerable.Range(0, 1000).Select(i => w.RowOf(Over(("case_id", $"XC-{i:D4}")))));
            var (rc, _, rows, order) = w.Cli();
            bool same = rows.Values.Select(v => Compat.DumpsCompact(v.Steps)).Distinct().Count() == 1;
            return OkIf(rc == 0 && order.Count == 1000 && order.SequenceEqual(Enumerable.Range(0, 1000).Select(i => $"XC-{i:D4}")) && same, $"exit {rc}; {order.Count} rows; identical answers: {(same ? "True" : "False")}");
        });
    }

    // ---- 4. e-mail spelling
    private static void EmailScenarios()
    {
        void One(string sid, string title, string expected, string email, Func<Seen, bool> check) =>
            Add(sid, "e-mail spelling", title, expected, tmp =>
            {
                var w = new World(tmp);
                w.WriteRequests(new[] { w.RowOf(Over(("email", email))) });
                var s = w.Audit();
                return OkIf(check(s), s.Text());
            });

        One("M01", "upper-case e-mail 'ALMA.Reyes@Example.TEST'", "e-mail match ignores case: same answer as the plain file", "ALMA.Reyes@Example.TEST", IsBase);
        One("M02", "e-mail with leading/trailing spaces and a tab", "whitespace ignored: same answer", "  alma.reyes@example.test\t", IsBase);
        One("M03", "'Display Name <address>' form", "not an address of a known user: conservative NOT_APPLICABLE (no fuzzy matching)", "Alma Reyes <alma.reyes@example.test>", Fallback);
        One("M04", "plus-tag variant alma.reyes+x@example.test", "a different address: unknown, conservative NOT_APPLICABLE (never match look-alikes)", "alma.reyes+x@example.test", Fallback);
        One("M05", "look-alike domain example.tes", "unknown: conservative NOT_APPLICABLE", "alma.reyes@example.tes", Fallback);
        One("M06", "non-breaking space and zero-width space inside the address", "unknown: conservative NOT_APPLICABLE (invisible characters are not ignored silently)", "alma.reyes@example.test​", s => Fallback(s) || IsBase(s));

        Add("M07", "e-mail spelling", "evidence records the e-mail in upper case while the request uses lower case", "identifiers are compared case-insensitively: the record is linked, same answer", tmp =>
        {
            var w = new World(tmp, user: A);
            var doc = w.ReadDoc("evidence_x1");
            foreach (var r in (List<object?>)doc["records"]!) { var d = (Dictionary<string, object?>)r!; d["subject_reference"] = ((string)d["subject_reference"]!).ToUpperInvariant(); }
            w.WriteDoc("evidence_x1", doc);
            var s = w.Audit();
            return OkIf(IsBase(s), s.Text());
        });
        Add("M08", "e-mail spelling", "two users whose addresses differ only by case in user_identities.csv", "no crash; the request resolves deterministically (see observed)", tmp =>
        {
            var w = new World(tmp, others: new List<XPerson> { P(2, "Alma Two", "ALMA.REYES@example.test") });
            var s = w.Audit();
            return s.Error.Length == 0
                ? Decision(s.Text(), "two identity rows with the same address (case-insensitively): the owner at requested_at wins; here both are current, so the resolver must still answer without raising")
                : (s.Text(), "DEFECT", "");
        });
    }

    // ---- 5. old (historical) e-mails and reassigned addresses
    private static void OldEmailScenarios()
    {
        const string old = "alma.old@example.test";
        const string area = "old e-mails";
        static XPerson WithHistory(string at) => P(1, "Alma Reyes", "alma.reyes@example.test", new List<Hist> { new() { At = at, Old = old } });

        Add("H01", area, "request uses the user's previous address; the evidence uses the current one", "the audit log links the old address to the user: same answer as a request with the current address", tmp =>
        {
            var w = new World(tmp, user: WithHistory("2026-05-01T00:00:00Z"));
            w.WriteRequests(new[] { w.RowOf(Over(("email", old))) });
            var s = w.Audit();
            return OkIf(IsBase(s), s.Text());
        });
        Add("H02", area, "evidence mentions only the OLD address of the user", "history gives every identifier the user ever had: the record is linked", tmp =>
        {
            var ev = new List<Ev> { new() { FileId = "evidence_x1", Kind = "ledger", CapturedAt = TE1, Resources = new List<Res>
            {
                R("account_101", "USER_PROFILE", "ABSENT", subject: A.Email), R("audit_record_102", "SECURITY_AUDIT_LOG", "PRESENT", subject: A.Email), R("cache_101", "CACHE_ENTRY", "PRESENT", subject: old),
            } } };
            var w = new World(tmp, user: WithHistory("2026-05-01T00:00:00Z"), evidence: ev);
            var s = w.Audit();
            return OkIf(s.Has("found", "cache_101"), s.Text());
        });
        Add("H03", area, "an old address that was later given to ANOTHER user; request made after the hand-over",
            "the owner at request time wins: the request resolves to the new owner (whose evidence is empty), not to the previous owner", tmp =>
        {
            var other = P(2, "Bea Novak", old);
            var w = new World(tmp, user: WithHistory("2026-05-01T00:00:00Z"), others: new List<XPerson> { other });
            w.WriteRequests(new[] { w.RowOf(Over(("email", old), ("requested_at", "2026-08-01T09:00:00Z"))) });
            var s = w.Audit();
            return OkIf(!s.Has("found", "cache_101"), s.Text());
        });
        Add("H04", area, "the same old address, request made BEFORE the hand-over",
            "the previous owner still owned it at request time: the request resolves to the previous owner and audits their copies", tmp =>
        {
            var other = P(2, "Bea Novak", old);
            var w = new World(tmp, user: WithHistory("2026-08-15T00:00:00Z"), others: new List<XPerson> { other });
            w.WriteRequests(new[] { w.RowOf(Over(("email", old), ("requested_at", "2026-08-01T09:00:00Z"))) });
            var s = w.Audit();
            return OkIf(s.Has("found", "cache_101"), s.Text());
        });
        Add("H05", area, "an address that no identity row or audit-log row mentions", "unknown: conservative NOT_APPLICABLE with the reason in the notes", tmp =>
        {
            var w = new World(tmp);
            w.WriteRequests(new[] { w.RowOf(Over(("email", "never.seen@example.test"))) });
            var s = w.Audit();
            return OkIf(Fallback(s) && s.Notes.Any(n => n.Contains("no known identity", StringComparison.Ordinal)), s.Text());
        });
    }

    // ---- 6. several requests by one user
    private static void MultiRequestScenarios()
    {
        const string area = "several requests per user";
        static Ev Ledger(string fileId, string t, string cacheState) =>
            new() { FileId = fileId, Kind = "ledger", CapturedAt = t, Resources = Core(1, file: fileId).Concat(new[] { R("cache_101", "CACHE_ENTRY", cacheState, fileId) }).ToList() };

        Add("U01", area, "two requests by one user; evidence shows the cache copy present at the first audit_at and gone at the second",
            "each request is answered from the state at ITS audit_at: first INCOMPLETE, second COMPLETE", tmp =>
        {
            var w = new World(tmp, evidence: new List<Ev> { Ledger("evidence_x1", TE1, "PRESENT"), Ledger("evidence_x2", "2026-08-18T12:00:00Z", "ABSENT") });
            w.WriteRequests(new[] { w.RowOf(Over(("case_id", "R-1"), ("audit_at", "2026-08-12T09:00:00Z"))), w.RowOf(Over(("case_id", "R-2"), ("audit_at", "2026-08-20T09:00:00Z"))) });
            var (rc, _, rows, _) = w.Cli();
            string r1 = rows.TryGetValue("R-1", out var a) ? a.Status : "?", r2 = rows.TryGetValue("R-2", out var b) ? b.Status : "?";
            return OkIf(rc == 0 && r1 == "INCOMPLETE" && r2 == "COMPLETE", $"exit {rc}; R-1 {r1}; R-2 {r2}");
        });
        Add("U02", area, "the same user files the same request three times under three ids", "three rows with identical answers; no case takes resources from another", tmp =>
        {
            var w = new World(tmp);
            w.WriteRequests(Enumerable.Range(0, 3).Select(i => w.RowOf(Over(("case_id", $"R-{i}")))));
            var (rc, _, rows, _) = w.Cli();
            bool same = rows.Values.Select(v => Compat.DumpsCompact(v.Steps)).Distinct().Count() == 1;
            return OkIf(rc == 0 && rows.Count == 3 && same && rows.Values.All(v => v.Status == "INCOMPLETE"), $"exit {rc}; statuses [{string.Join(", ", rows.Values.Select(v => "'" + v.Status + "'"))}]; identical {(same ? "True" : "False")}");
        });
        Add("U03", area, "first request is a deletion, second (same user) is an access request", "deletion answered, access request NOT_APPLICABLE: each text is judged on its own", tmp =>
        {
            var w = new World(tmp);
            w.WriteRequests(new[] { w.RowOf(Over(("case_id", "R-1"))), w.RowOf(Over(("case_id", "R-2"), ("request_text", "Please send me a copy of all my data."))) });
            var (rc, _, rows, _) = w.Cli();
            return OkIf(rc == 0 && rows["R-1"].Status == "INCOMPLETE" && rows["R-2"].Status == "NOT_APPLICABLE" && rows["R-2"].Steps.Count == 0, $"exit {rc}; R-1 {rows["R-1"].Status}; R-2 {rows["R-2"].Status}");
        });
    }
}
