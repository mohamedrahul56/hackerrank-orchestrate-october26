using System.Collections.Generic;
using System.Diagnostics;
using Auditor.Evidence;

namespace Auditor;

public sealed record CaseResult(string CaseId, string Status, List<Step> Steps, List<string> Notes, List<(string Resource, string FileId, string Time)> LaterLinks, bool Provisional = false)
{
    public CaseResult(string caseId, string status, List<Step> steps, List<string> notes) : this(caseId, status, steps, notes, new(), false) { }
}

/// <summary>The switches and numbers of one audit run (built from <see cref="Settings"/>).</summary>
public sealed class AuditConfig
{
    /// <summary>D3: later evidence may link a resource to a user (never state, never cited)</summary>
    public bool AllowLaterLinking { get; init; } = true;
    /// <summary>activity-timeline discovery of id-less accounts / anonymized analytics</summary>
    public bool UseDiscovery { get; init; } = true;
    /// <summary>deletion request whose only state evidence is an unread screenshot -> provisional INCOMPLETE row instead of NOT_APPLICABLE</summary>
    public bool ProvisionalRows { get; init; } = true;
    /// <summary>measured harmful by hold-out (precision 86% vs 100%): kept only as an ablation switch</summary>
    public bool UseCohortFilter { get; init; }
    /// <summary>the two second-of-minute heuristics (accounts: PhaseModel offsets; analytics: the 40/41 s offset). Dataset-specific, see README/NOTES 12</summary>
    public bool UseTimingHeuristics { get; init; } = true;
    public Tunables Tune { get; init; } = new();
    /// <summary>concurrent model calls when reading screenshots (output order is unaffected)</summary>
    public int ImageWorkers { get; init; } = 1;
    public IImageReader? ImageReader { get; init; }

    public static AuditConfig FromSettings(Settings s, IImageReader? imageReader = null) => new()
    {
        AllowLaterLinking = s.LaterLinking, UseDiscovery = s.Discovery, ProvisionalRows = s.ProvisionalRows, UseCohortFilter = s.CohortFilter,
        UseTimingHeuristics = s.TimingHeuristics, Tune = s.Tune, ImageWorkers = s.MaxConcurrency, ImageReader = imageReader,
    };
}

/// <summary>
/// The case auditor: request -> (deletion_status, steps).
/// Flow for one case (every decision here is code; the only model call is the optional image reader):
///   1 classify request_text                      (Classifier)    NOT_APPLICABLE -> []
///   2 resolve current + historical identity      (IdentityResolver)
///   3 find every resource tied to that identity  (Linker + Discover)
///   4 state of each resource at audit_at         (StateResolver)       only evidence captured in [requested_at, audit_at]
///   5 policy verdict per resource                (PolicyEngine)      driven by deletion_rules.csv
///   6 findings / remediation / verification      (this class)      canonical order via Schema.Canonicalize
///   7 validate                                   (Validator)    a bad row never crashes the run
/// </summary>
public sealed class CaseAuditor
{
    private static readonly Dictionary<string, int> ChainPosition = new(StringComparer.Ordinal) { ["REDACT_RECORD"] = 1, ["DISABLE_DATA_FLOW"] = 2, ["BLOCK_RESTORE"] = 3 };

    public string Root { get; }
    public AuditConfig Cfg { get; }
    /// <summary>seconds per start-up stage (the profiling tool reads it; nothing else does)</summary>
    public Dictionary<string, double> Timings { get; } = new(StringComparer.Ordinal);
    public IdentityResolver Res { get; }
    public EvidenceBase Base { get; }
    public IReadOnlyList<CatalogRow> Catalog { get; private set; } = Array.Empty<CatalogRow>();
    public List<ManifestRow> Manifest { get; private set; } = new();
    public Linker Link { get; }
    public PolicyEngine Policy { get; }
    public List<Event> Events { get; }
    public Dictionary<string, List<Event>> EventsByResource { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Request> Requests { get; } = new(StringComparer.Ordinal);
    /// <summary>case_id -> why the row cannot be audited (answered with a fallback row)</summary>
    public Dictionary<string, string> BadRequests { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Classification> Classification { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> Users { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Assignment> AccountAsg { get; private set; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> KnownAccounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Assignment> AnalyticsAsg { get; private set; } = new(StringComparer.Ordinal);

    private DateTime[]? chainTimes;
    private readonly Dictionary<string, List<HashSet<string>>> chainHits = new(StringComparer.Ordinal);
    private List<(DateTime T, int K, HashSet<string> Owners)> chainEvents = new();

    public CaseAuditor(string root, AuditConfig? config = null, IReadOnlyList<string>? requestsFiles = null)
    {
        Root = root;
        Cfg = config ?? new AuditConfig();
        Res = Stage("identity", () => new IdentityResolver(root));
        Base = Stage("evidence_parse", () => Load.LoadEvidence(root, includeImages: Cfg.ImageReader is not null, Cfg.ImageReader, Cfg.ImageWorkers));
        (Link, _) = (Stage("link", () =>
        {
            Catalog = Load.CatalogRows(root);
            Manifest = Load.ManifestRows(root);
            return new Linker(Base, Res, Catalog);
        }), 0);
        Policy = new PolicyEngine(Path.Combine(root, "deletion_rules.csv"));
        Events = Stage("activity_index", () =>
        {
            var events = Discover.LoadEvents(root);
            foreach (var e in events)
            {
                if (!EventsByResource.TryGetValue(e.Resource, out var list)) EventsByResource[e.Resource] = list = new();
                list.Add(e);
            }
            return events;
        });
        foreach (var file in requestsFiles ?? new[] { "deletion_requests.csv", "sample_deletion_requests.csv" })
        {
            if (!File.Exists(Path.Combine(root, file))) continue;
            var (good, bad, _) = Dataset.LoadRequestsLenient(Path.Combine(root, file));
            foreach (var r in good) Requests.TryAdd(r.CaseId, r);
            foreach (var b in bad)
                if (b.CaseId.Length > 0 && !b.Reason.StartsWith("duplicate", StringComparison.Ordinal)) BadRequests.TryAdd(b.CaseId, b.Reason);
        }
        Stage("classify_requests", () =>
        {
            foreach (var (cid, r) in Requests) Classification[cid] = Classifier.Classify(r.RequestText);
            return 0;
        });
        Stage("resolve_identities", () =>
        {
            foreach (var (cid, r) in Requests) Users[cid] = Res.Resolve(r.Email, r.RequestedAt).Select(p => p.UserId).ToHashSet(StringComparer.Ordinal);
            return 0;
        });
        if (Cfg.UseDiscovery) Stage("discovery_total", () => { DiscoverAll(); return 0; });
    }

    private T Stage<T>(string name, Func<T> body)
    {
        var sw = Stopwatch.StartNew();
        try { return body(); }
        finally { Timings[name] = Timings.GetValueOrDefault(name) + sw.Elapsed.TotalSeconds; }
    }

    // ------------------------------------------------------------------ global discovery (needs every case)
    private HashSet<string> CaseFiles(string cid)
    {
        var r = Requests[cid];
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var res in Link.ResourcesOf(Users[cid]))
            foreach (var o in Link.Observations(res))
                if (StateResolver.Usable(o, r.RequestedAt, r.AuditAt)) files.Add(o.FileId);
        return files;
    }

    private void DiscoverAll()
    {
        var cases = new List<CaseKey>();
        var explicitLinks = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var cohort = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var chain = new Dictionary<string, List<(DateTime T, int K)>>(StringComparer.Ordinal);
        var profilesIn = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);                 // file_id -> USER_PROFILE resources it observes (one pass instead of one per case)
        foreach (var o in Base.Obs)
        {
            if (o.Category != "USER_PROFILE") continue;
            if (!profilesIn.TryGetValue(o.FileId, out var set)) profilesIn[o.FileId] = set = new(StringComparer.Ordinal);
            set.Add(o.Resource);
        }
        var sw = Stopwatch.StartNew();
        foreach (var (cid, r) in Requests)
        {
            if (Classification[cid].Label != Classifier.Deletion || Users[cid].Count == 0) continue;
            cases.Add(new CaseKey(cid, Users[cid].ToHashSet(StringComparer.Ordinal), r.ReportedAt, r.RequestedAt, r.AuditAt, r.ReportedStatus));
            var linked = Link.ResourcesOf(Users[cid]);
            if (!Cfg.AllowLaterLinking)                       // D3 off: a link made only by evidence captured after audit_at does not exist
                linked = linked.Where(x => Link.LinkIdsBefore(x, Users[cid], r.AuditAt)).ToHashSet(StringComparer.Ordinal);
            var observed = linked.Where(x => x.StartsWith("account_", StringComparison.Ordinal) && Link.Observations(x).Any(o => StateResolver.Usable(o, r.RequestedAt, r.AuditAt))).ToHashSet(StringComparer.Ordinal);
            explicitLinks[cid] = Discover.KnownAccounts(linked, observed, EventsByResource, r.ReportedAt, Cfg.Tune);
            KnownAccounts[cid] = explicitLinks[cid];
            var files = CaseFiles(cid);
            cohort[cid] = files.SelectMany(f => profilesIn.TryGetValue(f, out var p) ? p : Enumerable.Empty<string>()).ToHashSet(StringComparer.Ordinal);
            var ch = new List<(DateTime T, int K)>();
            foreach (var x in linked)
                foreach (var e in EventsByResource.TryGetValue(x, out var evs) ? evs : new List<Event>())
                    if (e.Source == "deletion_worker" && e.Result == "SUCCESS" && ChainPosition.ContainsKey(e.EventType) && Math.Abs((e.T - r.ReportedAt).TotalSeconds) < 3600)
                        ch.Add((e.T, ChainPosition[e.EventType]));
            chain[cid] = ch;
        }
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (rid, uid) in Link.Links.Keys)
        {
            if (!rid.StartsWith("account_", StringComparison.Ordinal)) continue;
            if (!owners.TryGetValue(rid, out var set)) owners[rid] = set = new(StringComparer.Ordinal);
            set.Add(uid);
        }
        Timings["discovery.case_inputs"] = sw.Elapsed.TotalSeconds;
        var own = Cfg.UseTimingHeuristics ? Stage("discovery.own_events", () => cases.ToDictionary(c => c.CaseId, OwnEvents, StringComparer.Ordinal)) : null;
        AccountAsg = Stage("discovery.assign_accounts", () => Discover.AssignAccounts(cases, Events, explicitLinks, owners, Cfg.UseCohortFilter ? cohort : null, own, Cfg.Tune));
        sw.Restart();
        var anonymized = Base.Obs.Where(o => o.Category == "ANALYTICS_PROFILE" && o.State == "ANONYMIZED" && o.Idents.Count == 0).Select(o => o.Resource).ToHashSet(StringComparer.Ordinal);
        chainTimes = null;
        chainHits.Clear();
        chainEvents = new();                                  // every completed policy action that belongs to some known user
        foreach (var (rid, evs) in EventsByResource)
        {
            var ridOwners = Link.UsersOf(rid).ToHashSet(StringComparer.Ordinal);
            foreach (var e in evs)
                if (e.Source == "deletion_worker" && e.Result == "SUCCESS" && ChainPosition.ContainsKey(e.EventType) && ridOwners.Count > 0) chainEvents.Add((e.T, ChainPosition[e.EventType], ridOwners));
        }
        var hasOwn = cases.Where(c => Link.ResourcesOf(c.Users).Any(x => x.StartsWith("analytics_", StringComparison.Ordinal))).Select(c => c.CaseId).ToHashSet(StringComparer.Ordinal);
        var accountTime = Cfg.UseTimingHeuristics
            ? AccountAsg.Where(kv => kv.Value.Resource is not null && kv.Value.Event is not null).ToDictionary(kv => kv.Key, kv => kv.Value.Event!.T, StringComparer.Ordinal)
            : null;
        AnalyticsAsg = Discover.AssignAnonymizedAnalytics(cases.Where(c => !hasOwn.Contains(c.CaseId)).ToList(), Events, anonymized, chain, ForeignChain, accountTime, Cfg.Tune);
        Timings["discovery.analytics"] = sw.Elapsed.TotalSeconds;
    }

    /// <summary>Activity events on the case's linked resources around its reported_at (timing only, never state).</summary>
    private List<Event> OwnEvents(CaseKey c)
    {
        var seen = new SortedDictionary<string, Event>(StringComparer.Ordinal);
        foreach (var rid in Link.ResourcesOf(c.Users))
            foreach (var e in EventsByResource.TryGetValue(rid, out var evs) ? evs : new List<Event>())
                if (Math.Abs((e.T - c.ReportedAt).TotalSeconds) < 4 * 3600) seen[e.ActivityId] = e;
        return seen.Values.ToList();
    }

    /// <summary>True when <paramref name="e"/> is followed by another user's completed policy actions at chain spacing (a decoy user's deletion).</summary>
    private bool ForeignChain(CaseKey c, Event e)
    {
        if (chainTimes is null)                               // sorted once; each event then looks only at the 25 minutes after it
        {
            chainEvents = chainEvents.OrderBy(x => x.T).ToList();
            chainTimes = chainEvents.Select(x => x.T).ToArray();
        }
        if (!chainHits.TryGetValue(e.ActivityId, out var hit))
        {
            int lo = UpperBound(chainTimes, e.T);                                                  // first chain event strictly after e
            int hi = LowerBound(chainTimes, e.T + TimeSpan.FromSeconds(1500));                     // first one 1500 s or more after e
            hit = new List<HashSet<string>>();
            for (int i = lo; i < hi; i++)
                if (Discover.ChainOk((chainEvents[i].T - e.T).TotalSeconds, chainEvents[i].K, Cfg.Tune)) hit.Add(chainEvents[i].Owners);
            chainHits[e.ActivityId] = hit;
        }
        return hit.Any(owners => !owners.Overlaps(c.Users));
    }

    private static int LowerBound(DateTime[] a, DateTime x) { int lo = 0, hi = a.Length; while (lo < hi) { int mid = (lo + hi) / 2; if (a[mid] < x) lo = mid + 1; else hi = mid; } return lo; }

    private static int UpperBound(DateTime[] a, DateTime x) { int lo = 0, hi = a.Length; while (lo < hi) { int mid = (lo + hi) / 2; if (x < a[mid]) hi = mid; else lo = mid + 1; } return lo; }

    // ------------------------------------------------------------------ one case
    public CaseResult AuditCase(string caseId)
    {
        if (BadRequests.TryGetValue(caseId, out var why) && !Requests.ContainsKey(caseId))
            return new CaseResult(caseId, "NOT_APPLICABLE", new(), new() { $"FALLBACK: malformed request row ({why})" });
        if (!Requests.ContainsKey(caseId)) return new CaseResult(caseId, "NOT_APPLICABLE", new(), new() { "FALLBACK: case_id not found in the requests files" });
        try { return AuditCase(caseId, null); }
        catch (Exception e)                                   // one bad case must never crash the full run
        {
            Console.Error.WriteLine($"case {caseId} failed, falling back to a conservative row: {e}");
            return new CaseResult(caseId, "NOT_APPLICABLE", new(), new() { $"FALLBACK: {e.GetType().Name}: {e.Message}" });
        }
    }

    public CaseResult AuditCase(string caseId, Trace? trace)
    {
        var tr = trace ?? new Trace();                        // a throw-away trace in normal runs: the same code path, nothing read from it
        var r = Requests[caseId];
        var cls = Classification[caseId];
        if (cls.Label != Classifier.Deletion)
            return new CaseResult(caseId, "NOT_APPLICABLE", new(), new() { $"not a deletion request: {string.Join("; ", cls.Reasons)} [{cls.Method}]" });
        var users = Users[caseId];
        var notes = new List<string>();
        if (users.Count == 0) return new CaseResult(caseId, "NOT_APPLICABLE", new(), new() { "FALLBACK: request email matches no known identity" });
        DateTime t0 = r.RequestedAt, auditAt = r.AuditAt;
        var later = new List<(string Resource, string FileId, string Time)>();

        // ---- resources of this case
        var resources = new OrderedDictionary<string, string>(StringComparer.Ordinal);                  // resource -> how it was found
        foreach (var rid in Link.ResourcesOf(users).OrderBy(x => x, StringComparer.Ordinal))             // sorted: set order must not matter
        {
            var first = Link.FirstLinkTime(rid, users);
            if (first is not null && first.Value.T > auditAt)
            {
                if (!Cfg.AllowLaterLinking) continue;
                later.Add((rid, first.Value.Id, Py.FormatTs(first.Value.T)));
            }
            resources[rid] = "explicit";
        }
        var laterRids = later.Select(x => x.Resource).ToHashSet(StringComparer.Ordinal);
        AccountAsg.TryGetValue(caseId, out var acct);
        var activityFor = new Dictionary<string, Event?>(StringComparer.Ordinal);
        if (acct?.Resource is { } accountResource && !resources.ContainsKey(accountResource))
        {
            resources[accountResource] = acct.How;
            activityFor[accountResource] = acct.Event;
            if (acct.How == "explicit")                       // linked by a snapshot captured after audit_at: a logged later-evidence use
            {
                var first = Link.FirstLinkTime(accountResource, users);
                if (first is not null && first.Value.T > auditAt) later.Add((accountResource, first.Value.Id, Py.FormatTs(first.Value.T)));
            }
        }
        AnalyticsAsg.TryGetValue(caseId, out var ana);
        if (ana?.Resource is { } analyticsResource && !resources.ContainsKey(analyticsResource))
        {
            resources[analyticsResource] = $"analytics-{ana.How}";
            activityFor[analyticsResource] = ana.Event;
        }
        later = later.OrderBy(x => x, Comparer<(string, string, string)>.Create((a, b) => { int c = string.CompareOrdinal(a.Item1, b.Item1); if (c != 0) return c; c = string.CompareOrdinal(a.Item2, b.Item2); return c != 0 ? c : string.CompareOrdinal(a.Item3, b.Item3); })).ToList();
        tr.Resources = resources.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        tr.Later = new List<(string, string, string)>(later);

        // ---- state at audit_at
        var states = new OrderedDictionary<string, ResourceState>(StringComparer.Ordinal);
        foreach (var rid in resources.Keys)
        {
            var gateLog = new List<string>();
            var st = StateResolver.Resolve(Link.Observations(rid), t0, auditAt, gateLog);
            notes.AddRange(gateLog);                          // empty unless the agent layer's gate rule G10 acted
            if (st is not null) states[rid] = st;
        }
        // an account known only from the activity stream has no observation: treat as absent (claim) and cite the activity id
        var steps = new List<Step>();
        var violations = new List<(ResourceState St, Verdict V)>();
        var compliant = new List<(ResourceState St, Verdict V)>();
        tr.States = states.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        foreach (var (rid, st) in states)
        {
            if (st.Until is { } until && until <= auditAt)
            {
                notes.Add($"{rid}: retention/hold ended {Py.FormatTs(until)} (before audit_at): no retain requirement, no step");
                tr.Omitted.Add((rid, $"its retention / legal hold ended {Py.FormatTs(until)}, before audit_at: the RETAIN rule no longer applies, so nothing is required of it"));
                continue;
            }
            var v = Policy.Judge(st.Category, st.State);
            if (v is null)
            {
                notes.Add($"{rid}: state {st.State} for {st.Category} says nothing about policy; ignored");
                tr.Omitted.Add((rid, $"state {st.State} says nothing about policy for {st.Category}"));
                continue;
            }
            tr.Verdicts[rid] = VerdictSentence(st, v);
            if (v.Violation && laterRids.Contains(rid))
            {
                // a finding must stand on evidence available at audit_at; a record tied to the user only by a later snapshot may support an end-state check, never a finding or a
                // remediation (decision D3). Measured: this never fires today.
                notes.Add($"{rid}: violation not reported - the record is linked to the user only by evidence captured after audit_at");
                tr.Omitted.Add((rid, "it violates policy but is tied to the user only by evidence captured after audit_at (decision D3): no finding or remediation"));
                continue;
            }
            (v.Violation ? violations : compliant).Add((st, v));
            if (!v.Violation && !v.EmitVerification)
                tr.Omitted.Add((rid, $"already compliant ({st.State}) and a compliant {st.Category} copy adds no step (only the account is always verified)"));
        }
        // accounts with no state evidence in the window: discovered through the activity stream, or linked explicitly by a later snapshot of this same scenario (first seen between the request
        // and a week after audit_at)
        var unobservedAccounts = new List<string>();
        var laterLimit = auditAt + TimeSpan.FromDays(Cfg.Tune.LaterLinkMaxDays);
        foreach (var (rid, how) in resources)
        {
            if (states.ContainsKey(rid) || !rid.StartsWith("account_", StringComparison.Ordinal)) continue;
            var firstSeen = Link.Observations(rid).Select(o => (DateTime?)o.FileT).Min();
            bool deletedInWindow = KnownAccounts.TryGetValue(caseId, out var known) && known.Contains(rid);        // linked to the user AND deleted by the worker in the window
            if (how != "explicit" || deletedInWindow || (firstSeen is not null && t0 <= firstSeen && firstSeen <= laterLimit)) unobservedAccounts.Add(rid);
        }
        // other linked resources with no state evidence in the window that first appear at/after the request (e.g. a retained audit record seen only in a later snapshot of the cohort):
        // their END state is still a required check, with no citation
        var unobservedLinked = new List<(string Rid, Obs O)>();
        foreach (var (rid, how) in resources)
        {
            if (states.ContainsKey(rid) || how != "explicit" || rid.StartsWith("account_", StringComparison.Ordinal)) continue;
            var seen = Link.Observations(rid);
            var firstLinked = seen.Select(o => (DateTime?)o.FileT).Min();
            if (firstLinked is not null && t0 <= firstLinked && firstLinked <= laterLimit) unobservedLinked.Add((rid, seen.Aggregate((best, o) => o.FileT > best.FileT ? o : best)));
        }

        var status = violations.Count > 0 ? "INCOMPLETE" : "COMPLETE";
        bool citeOk = status == "COMPLETE";
        foreach (var (st, v) in violations)
        {
            if (v.Treatment.Finding is null || v.Treatment.Fix is null) throw new InvalidOperationException("a violation always names its finding and fix (policy)");
            steps.Add(tr.Why(Schema.MakeStep("FINDING", v.Treatment.Finding, st.System, st.Resource, st.FileId),
                             $"{st.Resource} is {st.State} at audit_at ({tr.Verdicts[st.Resource]}); cites {st.FileId}, the latest in-window file that shows it"));
            steps.Add(tr.Why(Schema.MakeStep("REMEDIATION", v.Treatment.Fix, st.System, st.Resource), "proposed fix for the finding above (future work, so no evidence id)"));
            steps.Add(tr.Why(Schema.MakeStep("VERIFICATION", v.Treatment.Verify, st.System, st.Resource), "check that must pass after the fix (future work, so no evidence id)"));
        }
        foreach (var (st, v) in compliant)
        {
            if (!v.EmitVerification) continue;
            steps.Add(tr.Why(Schema.MakeStep("VERIFICATION", v.Treatment.Verify, st.System, st.Resource, citeOk ? st.FileId : ""),
                             $"{st.Resource} already has the required treatment ({st.State}; {tr.Verdicts[st.Resource]})"
                             + (citeOk ? $"; cites {st.FileId}, the evidence that proves it" : "; the row is INCOMPLETE, so verification steps are future work with no evidence id")));
        }
        if (!citeOk)
        {
            foreach (var (rid, o) in unobservedLinked)
            {
                var v = Policy.Judge(o.Category, Policy.TreatmentFor(o.Category).Ok.OrderBy(x => x, StringComparer.Ordinal).First());
                if (v is not null && v.EmitVerification)
                {
                    steps.Add(tr.Why(Schema.MakeStep("VERIFICATION", v.Treatment.Verify, o.System, rid, ""),
                                     $"{rid} is tied to the user and first appears in {o.FileId} (a later snapshot of the same scenario): its required end state is still a check, with no citation"));
                    notes.Add($"{rid}: no state evidence in the window; end-state check added from its later/linked observation ({o.FileId})");
                }
            }
        }
        else if (unobservedLinked.Count > 0)
        {
            foreach (var (rid, _) in unobservedLinked)
                tr.Omitted.Add((rid, "tied to the user but no evidence captured inside the window shows it, and in a COMPLETE row every step must cite evidence: no step"));
        }
        foreach (var rid in unobservedAccounts)               // account known only from the activity stream
        {
            activityFor.TryGetValue(rid, out var ev);
            string cite = "", how = "";
            if (citeOk)
            {
                (cite, how) = CiteUnobserved("account_service", r, states, ev);
                notes.Add($"{rid}: no observation in window; cited {Py.StrRepr(cite)} ({how})");
            }
            steps.Add(tr.Why(Schema.MakeStep("VERIFICATION", "VERIFY_RECORD_ABSENT", "account_service", rid, cite),
                             $"{rid} is the user's account ({(resources.TryGetValue(rid, out var res) ? res : "None")}); no file shows it inside the window, so it is treated as deleted (claim from the worker's delete event)"
                             + (citeOk ? $"; cites {cite} ({how})" : "; the row is INCOMPLETE, so no evidence id")));
        }
        bool provisional = false;
        if (steps.Count == 0)
        {
            var prov = Cfg.ProvisionalRows ? Provisional(r, users) : null;
            if (prov is null)
                return new CaseResult(caseId, "NOT_APPLICABLE", new(), new() { "FALLBACK: deletion request but no evidence-backed resource in the request window" }, later, false);
            steps = prov.Value.Steps; status = "INCOMPLETE"; provisional = true;
            notes.Add("PROVISIONAL: " + prov.Value.Why);
            foreach (var s in steps) tr.Why(s, "PROVISIONAL - " + prov.Value.Why);
        }
        steps = Schema.Canonicalize(steps);
        var issues = Validator.ValidateCase(caseId, status, steps).Where(i => i.Severity == "error").ToList();
        if (issues.Count > 0)
            return new CaseResult(caseId, "NOT_APPLICABLE", new(), new() { $"FALLBACK: validator rejected row: {issues[0]}" }, later, false);
        return new CaseResult(caseId, status, steps, notes, later, provisional);
    }

    private string VerdictSentence(ResourceState st, Verdict v)
    {
        var rid = Policy.RuleIds.GetValueOrDefault(st.Category, "");
        var (action, cond) = Policy.Rules.TryGetValue(st.Category, out var rule) ? rule : ("DISABLE_FLOW", "spec 6.5: an active data flow can recreate the data (not a row of deletion_rules.csv)");
        var text = rid.Length > 0 ? $"{rid} {st.Category} / {cond} / {action}".Trim() : $"{st.Category}: {cond}";
        var outcome = v.Violation ? "VIOLATION" : "compliant";
        return $"{text}; state {st.State} -> {outcome} (treatment {v.Action})";
    }

    // ------------------------------------------------------------------ provisional row for an unread screenshot
    /// <summary>
    /// A deletion request whose state evidence is a screenshot nobody could read (no model key, or the read failed) is not a non-deletion request, so NOT_APPLICABLE is almost certainly
    /// wrong. Build the most likely row from structured data only:
    ///   * a record the data catalog (or any identifier match) ties to the user inside [requested_at, audit_at], with no readable state in the window;
    ///   * a screenshot captured inside the window by an admin tool of the same system (matched on the system's leading word: account_admin_portal ~ account_service);
    ///   * the record is assumed to show the violating state of its category, i.e. the screenshot was taken because something remained.
    /// The row is flagged provisional (run notes, provisional list) and is replaced as soon as the screenshot is really read.
    /// </summary>
    private (List<Step> Steps, string Why)? Provisional(Request r, IReadOnlySet<string> users)
    {
        var steps = new List<Step>();
        var used = new List<string>();
        foreach (var rid in Link.ResourcesOf(users).OrderBy(x => x, StringComparer.Ordinal))
        {
            var times = users.SelectMany(u => Link.Links.TryGetValue((rid, u), out var l) ? l.Select(x => x.T) : Enumerable.Empty<DateTime>()).ToList();
            if (!times.Any(t => r.RequestedAt <= t && t <= r.AuditAt)) continue;
            if (StateResolver.Resolve(Link.Observations(rid), r.RequestedAt, r.AuditAt) is not null) continue;
            var prefix = Records.Prefix(rid);
            var judged = ImageObs.StateFromDescription(prefix, new ReadingRecord("", false, new[] { "x@example.test" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), true, Array.Empty<string>()));
            var v = judged is null ? null : Policy.Judge(judged.Value.Category, judged.Value.State);
            if (v is null || !v.Violation || v.Treatment.Finding is null || v.Treatment.Fix is null) continue;      // a violation always names its finding and fix
            var system = Link.CatalogSystems.TryGetValue(rid, out var cs) && cs.Length > 0 ? cs : Records.SystemFor(rid);
            var lead = system.Split('_')[0];
            var shots = Manifest.Where(m => m.FilePath.ToLowerInvariant().EndsWith(".png", StringComparison.Ordinal) && r.RequestedAt <= m.T && m.T <= r.AuditAt && m.Origin.Split('_')[0] == lead).ToList();
            if (shots.Count == 0) continue;
            var shot = shots.Aggregate((best, m) => Py.CompareTuple(m.T, m.FileId, best.T, best.FileId) > 0 ? m : best);
            steps.Add(Schema.MakeStep("FINDING", v.Treatment.Finding, system, rid, shot.FileId));
            steps.Add(Schema.MakeStep("REMEDIATION", v.Treatment.Fix, system, rid));
            steps.Add(Schema.MakeStep("VERIFICATION", v.Treatment.Verify, system, rid));
            used.Add($"{rid} (screenshot {shot.FileId})");
        }
        if (steps.Count == 0) return null;
        return (Schema.Canonicalize(steps), "no readable state evidence; assumed the violating state of " + string.Join(", ", used) + " from the unread screenshot(s)");
    }

    // ------------------------------------------------------------------ citation fallback for unobserved resources
    /// <summary>
    /// No evidence file observes the resource. Prefer a snapshot that covers the system AND observes the case's other records (same cohort); then any covering snapshot in the window;
    /// finally the activity id of the delete claim.
    /// </summary>
    private (string Cite, string How) CiteUnobserved(string system, Request r, IReadOnlyDictionary<string, ResourceState> states, Event? ev)
    {
        var caseFiles = states.Values.Select(s => s.FileId).ToHashSet(StringComparer.Ordinal);
        var cov = Base.Coverage.Where(c => c.System == system && r.RequestedAt <= c.FileT && c.FileT <= r.AuditAt).ToList();
        var same = cov.Where(c => caseFiles.Contains(c.FileId)).OrderBy(c => c.FileT).ToList();            // stable
        if (same.Count > 0) return (same[^1].FileId, "snapshot covering the system and observing the case's other records");
        if (cov.Count > 0) return (cov.Aggregate((best, c) => c.FileT > best.FileT ? c : best).FileId, "latest snapshot covering the system in the window");
        if (ev is not null) return (ev.ActivityId, "ASSUMPTION: activity_id used because no file observes the resource");
        return ("", "no citation available");
    }
}
