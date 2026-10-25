using System.Text;
using static Auditor.Tools.ExtraCases;
using XPerson = Auditor.Tools.ExtraCases.Person;

namespace Auditor.Tools;

public static partial class EdgeSweep
{
    private static Ev LedgerFile(string fileId, string t, string cacheState) =>
        new() { FileId = fileId, Kind = "ledger", CapturedAt = t, Resources = Core(1, file: fileId).Concat(new[] { R("cache_101", "CACHE_ENTRY", cacheState, fileId) }).ToList() };

    private static List<string> CitedSet(Seen s) => s.Cited.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

    private static string Q(IEnumerable<string> xs) => "[" + string.Join(", ", xs.Select(x => "'" + x + "'")) + "]";

    // ---- 7. evidence after audit_at, window edges
    private static void AfterAuditScenarios()
    {
        const string area = "events after audit_at";
        Add("L01", area, "cache copy present at audit_at; a LATER file (after audit_at) shows it deleted", "later evidence must not change the answer: INCOMPLETE, citing only evidence captured before audit_at", tmp =>
        {
            var w = new World(tmp, evidence: new List<Ev> { LedgerFile("evidence_x1", TE1, "PRESENT"), LedgerFile("evidence_x2", TLate, "ABSENT") });
            var s = w.Audit();
            return OkIf(IsBase(s) && !s.Cited.Contains("evidence_x2"), s.Text() + $" | cited {Q(CitedSet(s))}");
        });
        Add("L02", area, "cache copy absent at audit_at; a LATER file shows it present again", "COMPLETE: the later re-appearance is outside the audit window", tmp =>
        {
            var w = new World(tmp, resources: Core(1).Concat(new[] { R("cache_101", "CACHE_ENTRY", "ABSENT") }).ToList(),
                              evidence: new List<Ev> { LedgerFile("evidence_x1", TE1, "ABSENT"), LedgerFile("evidence_x2", TLate, "PRESENT") });
            var s = w.Audit();
            return OkIf(s.Status == "COMPLETE" && !s.Cited.Contains("evidence_x2"), s.Text() + $" | cited {Q(CitedSet(s))}");
        });
        Add("L03", area, "evidence captured BEFORE requested_at shows the copy; nothing inside the window mentions it",
            "the pre-request file describes the world before the request: it is not state at audit_at (no finding from it)", tmp =>
        {
            var w = new World(tmp, evidence: new List<Ev> { LedgerFile("evidence_x1", TOld, "PRESENT") });
            var s = w.Audit();
            return OkIf(!s.Has("found", "cache_101") && s.Error.Length == 0, s.Text());
        });
        Add("L04", area, "evidence captured exactly at requested_at", "included: the window is inclusive at the start", tmp =>
        {
            var w = new World(tmp, evidence: new List<Ev> { LedgerFile("evidence_x1", TReq, "PRESENT") });
            var s = w.Audit();
            return OkIf(IsBase(s), s.Text());
        });
        Add("L05", area, "activity rows after audit_at claiming a successful DELETE of the cache copy", "activity is a claim and is after audit_at anyway: no effect, still INCOMPLETE", tmp =>
        {
            var w = new World(tmp);
            w.AppendCsv("system_activity.csv", new[] { new[] { "activity_9", "2026-08-25T00:00:00Z", "DELETE_RECORD", "deletion_worker", "cache_service", "cache_101", "SUCCESS", "Policy action completed for cache_101." } });
            var s = w.Audit();
            return OkIf(IsBase(s), s.Text());
        });
        Add("L06", area, "the only evidence for a resource is captured after audit_at", "no state is known at audit_at: nothing is claimed about that resource (conservative), no exception", tmp =>
        {
            var w = new World(tmp, evidence: new List<Ev> { new() { FileId = "evidence_x1", Kind = "ledger", CapturedAt = TE1, Resources = Core(1) }, LedgerFile("evidence_x2", TLate, "PRESENT") });
            var s = w.Audit();
            bool good = !s.Has("found", "cache_101") && s.Error.Length == 0 && !s.Cited.Contains("evidence_x2");
            return (s.Text(), good ? "DECISION" : "DEFECT", "an unknown state is not reported as a violation: the audit never claims what the evidence at audit_at does not show");
        });
    }

    // ---- 8. claims versus evidence
    private static void ClaimScenarios()
    {
        const string area = "failed-but-claimed-success";
        Add("C01", area, "workflow reports COMPLETE, the activity log says the delete FAILED, evidence still shows the copy", "INCOMPLETE: the evidence decides", tmp =>
        {
            var w = new World(tmp);
            w.AppendCsv("system_activity.csv", new[] { new[] { "activity_1", "2026-08-02T00:00:00Z", "DELETE_RECORD", "deletion_worker", "cache_service", "cache_101", "FAILED", "Deletion worker received the final result for cache_101." } });
            var s = w.Audit();
            return OkIf(IsBase(s), s.Text());
        });
        Add("C02", area, "workflow COMPLETE and the activity log says SUCCESS, but the evidence still shows the copy", "INCOMPLETE: a SUCCESS result is a claim, not proof", tmp =>
        {
            var w = new World(tmp);
            w.AppendCsv("system_activity.csv", new[] { new[] { "activity_1", "2026-08-02T00:00:00Z", "DELETE_RECORD", "deletion_worker", "cache_service", "cache_101", "SUCCESS", "Policy action completed for cache_101." } });
            var s = w.Audit();
            return OkIf(IsBase(s), s.Text());
        });
        Add("C03", area, "workflow says NOT_STARTED and the activity log says FAILED, but the evidence shows everything clean", "COMPLETE: the evidence decides in this direction too", tmp =>
        {
            var w = new World(tmp, resources: Core(1).Concat(new[] { R("cache_101", "CACHE_ENTRY", "ABSENT") }).ToList(), request: r => r.ReportedStatus = "NOT_STARTED");
            w.AppendCsv("system_activity.csv", new[] { new[] { "activity_1", "2026-08-02T00:00:00Z", "DELETE_RECORD", "deletion_worker", "cache_service", "cache_101", "FAILED", "x" } });
            var s = w.Audit();
            return OkIf(s.Status == "COMPLETE", s.Text());
        });
        Add("C04", area, "activity log claims SUCCESS for a resource that no evidence file observes", "no step is invented for it (claims alone create no finding)", tmp =>
        {
            var w = new World(tmp);
            w.AppendCsv("system_activity.csv", new[] { new[] { "activity_1", "2026-08-02T00:00:00Z", "DELETE_RECORD", "deletion_worker", "search_service", "search_555", "SUCCESS", "Policy action completed for search_555." } });
            var s = w.Audit();
            return OkIf(IsBase(s), s.Text());
        });
        Add("C05", area, "a data flow the activity log says was DISABLED successfully, while the evidence says it is still enabled", "INCOMPLETE with the active-flow finding", tmp =>
        {
            var w = new World(tmp, resources: Core(1).Concat(new[] { R("data_flow_105", "DATA_FLOW", "ACTIVE") }).ToList());
            w.AppendCsv("system_activity.csv", new[] { new[] { "activity_1", "2026-08-02T00:00:00Z", "DISABLE_DATA_FLOW", "deletion_worker", "integration_service", "data_flow_105", "SUCCESS", "Policy action completed for data_flow_105." } });
            var s = w.Audit();
            return OkIf(s.Status == "INCOMPLETE" && s.Has("flow", "data_flow_105"), s.Text());
        });
    }

    // ---- 9. no identifier, shared reported_at
    private static World TwoUserWorld(string tmp, bool sameTime)
    {
        var b = P(2, "Bea Novak", "bea.novak@example.test");
        var res = new List<Res> { R("account_101", "USER_PROFILE", "ABSENT"), R("audit_record_102", "SECURITY_AUDIT_LOG", "PRESENT") };
        var w = new World(tmp, resources: res, others: new List<XPerson> { b });
        // both deleted accounts are id-less 404s in the evidence; the worker deleted both inside both windows
        var doc = w.ReadDoc("evidence_x1");
        var recs = ((List<object?>)doc["records"]!).Cast<Dictionary<string, object?>>().Where(r => (string)r["resource_id"]! != "account_101").Cast<object?>().ToList();
        foreach (var rid in new[] { "account_901", "account_902" })
            recs.Add(new Dictionary<string, object?> { ["recorded_at"] = TE1, ["system"] = "account_service", ["resource_id"] = rid, ["record_type"] = "USER_PROFILE", ["result"] = "NOT_FOUND", ["subject_reference"] = "" });
        doc["records"] = recs;
        w.WriteDoc("evidence_x1", doc);
        w.AppendCsv("system_activity.csv", new[]
        {
            new[] { "activity_901", "2026-08-03T08:10:00Z", "DELETE_RECORD", "deletion_worker", "account_service", "account_901", "SUCCESS", "x" },
            new[] { "activity_902", "2026-08-03T08:20:00Z", "DELETE_RECORD", "deletion_worker", "account_service", "account_902", "SUCCESS", "x" },
        });
        var rq2 = w.RowOf(Over(("case_id", "XC-02"), ("email", b.Email), ("reported_at", sameTime ? TRep : "2026-08-03T10:00:00Z")));
        w.WriteRequests(new[] { w.RowOf(), rq2 });
        return w;
    }

    private static void LinkScenarios()
    {
        const string area = "resources with no identifier";
        Add("N01", area, "an evidence record whose subject_reference is empty and that no catalog row links to the user", "not attributed to anybody: no step for it", tmp =>
        {
            var ev = new List<Ev> { new() { FileId = "evidence_x1", Kind = "ledger", CapturedAt = TE1, Resources = Core(1).Concat(new[] { R("cache_101", "CACHE_ENTRY", "PRESENT", subject: "") }).ToList() } };
            var s = new World(tmp, evidence: ev).Audit();
            return OkIf(!s.Has("found", "cache_101"), s.Text());
        });
        Add("N02", area, "an evidence record that carries ANOTHER user's identifier", "never attributed to this user: no step for it", tmp =>
        {
            var ev = new List<Ev> { new() { FileId = "evidence_x1", Kind = "ledger", CapturedAt = TE1, Resources = Core(1).Concat(new[] { R("cache_101", "CACHE_ENTRY", "PRESENT", subject: "someone.else@example.test") }).ToList() } };
            var s = new World(tmp, evidence: ev).Audit();
            return OkIf(!s.Has("found", "cache_101"), s.Text());
        });
        Add("N03", area, "the data catalog links an id-less record to the user (LINK_RESOURCE_IDENTITY row), evidence shows it present", "linked through the catalog: INCOMPLETE with the finding", tmp =>
        {
            var ev = new List<Ev> { new() { FileId = "evidence_x1", Kind = "ledger", CapturedAt = TE1, Resources = Core(1).Concat(new[] { R("cache_101", "CACHE_ENTRY", "PRESENT", subject: "") }).ToList() } };
            var w = new World(tmp, evidence: ev);
            w.AppendCsv("system_activity.csv", new[] { new[] { "activity_1", "2026-07-30T00:00:00Z", "LINK_RESOURCE_IDENTITY", "data_catalog", "cache_service", "cache_101", "SUCCESS", $"Data catalog linked cache_101 ({A.UserId}) to {A.Email} and {A.UserId}." } });
            var s = w.Audit();
            return OkIf(s.Has("found", "cache_101"), s.Text());
        });
        Add("N04", "shared reported_at", "two users with the same reported_at and two id-less deleted accounts that fit both windows", "nothing separates the candidates: no account step for either (a guess is right half the time)", tmp =>
        {
            var w = TwoUserWorld(tmp, true);
            var a = w.NewAuditor();
            Seen s1 = SeenFrom(a.AuditCase("XC-01")), s2 = SeenFrom(a.AuditCase("XC-02"));
            bool noAcc = !s1.Ops.Concat(s2.Ops).Any(o => o.Rid.StartsWith("account_9", StringComparison.Ordinal));
            return OkIf(noAcc && s1.Error.Length == 0 && s2.Error.Length == 0, $"XC-01: {Clip(s1.Text(), 60)} / XC-02: {Clip(s2.Text(), 60)}");
        });
        Add("N05", "shared reported_at", "the same two users, reported_at 2 hours apart", "still no wrong account assigned (the worker events fit both windows); never more than one case takes an account", tmp =>
        {
            var w = TwoUserWorld(tmp, false);
            var a = w.NewAuditor();
            Seen s1 = SeenFrom(a.AuditCase("XC-01")), s2 = SeenFrom(a.AuditCase("XC-02"));
            var taken = s1.Ops.Concat(s2.Ops).Where(o => o.Rid.StartsWith("account_9", StringComparison.Ordinal)).Select(o => o.Rid).ToList();
            return OkIf(taken.Distinct().Count() == taken.Count && s1.Error.Length == 0 && s2.Error.Length == 0,
                $"XC-01: {Clip(s1.Text(), 60)} / XC-02: {Clip(s2.Text(), 60)}; accounts used {Q(taken.Distinct().OrderBy(x => x, StringComparer.Ordinal))}");
        });
    }

    // ---- 10. retained records with extra identifiers, legal holds
    private static Ev Coll(string name, List<Dictionary<string, string>> recs, string t = TE2, string fid = "evidence_x2") =>
        new() { FileId = fid, Kind = "collection", Collection = name, CapturedAt = t, Source = "sweep", Records = recs };

    private static World WithExtra(string tmp, Ev extra, List<Res> resources)
    {
        var w = new World(tmp, resources: resources);
        w.Case.Evidence.Add(extra);
        BuildDataset(w.Case, w.Root, w.RulesCsv);
        return w;
    }

    private static World Hold(string tmp, string? until, string fid = "evidence_x2", Dictionary<string, string>? extra = null)
    {
        var ev = LegalHoldFile("legal_hold_105", A.UserId, until ?? "", extra, fid);
        if (until is null) ev.Records![0].Remove("retained_until");
        return WithExtra(tmp, ev, Core(1));
    }

    private static void RetainScenarios()
    {
        Add("R01", "retained records with extra identifiers", "security audit record (RETAIN) that also contains the user's e-mail and phone", "retained as the rule says: never deleted or redacted, VERIFY_RECORD_PRESENT, status unaffected", tmp =>
        {
            var ev = Coll("security_audit_events", new() { new() { ["resource_id"] = "audit_record_102", ["event_type"] = "account_closure_recorded", ["subject_reference"] = A.UserId, ["email"] = A.Email, ["phone"] = A.Phone } });
            var s = WithExtra(tmp, ev, Core(1)).Audit();
            var bad = s.Ops.Where(o => o.Rid == "audit_record_102" && o.Op is "delete" or "redact" or "found" or "exposed").ToList();
            return OkIf(s.Has("v-present", "audit_record_102") && bad.Count == 0, s.Text());
        });
        Add("R02", "retained records with extra identifiers", "tombstone that still carries the e-mail (rule_15 REDACT)", "INCOMPLETE: exposed-PII finding, redact, verify-redacted for the tombstone", tmp =>
        {
            var ev = Coll("deleted_customer_references", new() { new() { ["resource_id"] = "tombstone_105", ["user_id"] = A.UserId, ["email"] = A.Email } });
            var s = WithExtra(tmp, ev, Core(1)).Audit();
            return OkIf(s.Status == "INCOMPLETE" && s.Has("exposed", "tombstone_105") && s.Has("redact", "tombstone_105") && s.Has("v-redacted", "tombstone_105"), s.Text());
        });
        Add("R03", "retained records with extra identifiers", "tombstone that carries only the stable user id", "COMPLETE for it: verify-redacted, no remediation", tmp =>
        {
            var ev = Coll("deleted_customer_references", new() { new() { ["resource_id"] = "tombstone_105", ["user_id"] = A.UserId } });
            var s = WithExtra(tmp, ev, Core(1)).Audit();
            return OkIf(s.Status == "COMPLETE" && s.Has("v-redacted", "tombstone_105"), s.Text());
        });
        Add("R04", "retained records with extra identifiers", "a retained legal-hold record that carries the e-mail address", "retained but exposed: INCOMPLETE, redact the identifier, keep the record (shape is an assumption, see XC-11)", tmp =>
        {
            var s = WithExtra(tmp, LegalHoldFile("legal_hold_105", A.UserId, "2033-12-31T23:59:59Z", new() { ["email"] = A.Email }), Core(1)).Audit();
            return OkIf(s.Has("exposed", "legal_hold_105") && s.Has("redact", "legal_hold_105") && !s.Has("delete", "legal_hold_105"), s.Text());
        });
        Add("R05", "expired legal holds", "hold ended before audit_at", "rule_13 does not apply: no step for the hold, COMPLETE", tmp =>
        {
            var s = Hold(tmp, "2026-08-05T00:00:00Z").Audit();
            return OkIf(s.Status == "COMPLETE" && !s.Ops.Any(o => o.Rid == "legal_hold_105"), s.Text());
        });
        Add("R06", "expired legal holds", "hold ends exactly at audit_at", "ended (retained_until <= audit_at): no step for the hold", tmp =>
        {
            var s = Hold(tmp, TAud).Audit();
            return OkIf(!s.Ops.Any(o => o.Rid == "legal_hold_105"), s.Text());
        });
        Add("R07", "expired legal holds", "hold ends one second after audit_at", "still active: VERIFY_RECORD_PRESENT for the hold", tmp =>
        {
            var s = Hold(tmp, "2026-08-20T09:00:01Z").Audit();
            return OkIf(s.Has("v-present", "legal_hold_105"), s.Text());
        });
        Add("R08", "expired legal holds", "hold record without any retained_until", "no end date supplied: treated as active (kept and verified present); never deleted", tmp =>
        {
            var s = Hold(tmp, null).Audit();
            return OkIf(s.Has("v-present", "legal_hold_105") && !s.Has("delete", "legal_hold_105") && s.Error.Length == 0, s.Text());
        });
        Add("R09", "expired legal holds", "retained_until is unparseable ('someday')",
            "an unreadable end date is the same as no end date (R08): the hold counts as active, the record is kept and verified present, never deleted", tmp =>
        {
            var s = Hold(tmp, "someday").Audit();
            return OkIf(s.Error.Length == 0 && s.Has("v-present", "legal_hold_105") && !s.Has("delete", "legal_hold_105"), s.Text());
        });
        Add("R10", "expired legal holds", "retained_until given as a plain date '2026-08-05'", "a date is read as midnight UTC: expired, no step", tmp =>
        {
            var s = Hold(tmp, "2026-08-05").Audit();
            return OkIf(!s.Ops.Any(o => o.Rid == "legal_hold_105") && s.Error.Length == 0, s.Text());
        });
        Add("R11", "expired legal holds", "the snapshot that shows the hold was captured AFTER audit_at, hold still active then", "later evidence cannot create a finding or a citation at audit_at", tmp =>
        {
            var s = Hold(tmp, "2033-12-31T23:59:59Z", "evidence_x3").Audit();
            return OkIf(s.Error.Length == 0, s.Text());
        });
    }
}
