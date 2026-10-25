namespace Auditor;

/// <summary>An event of the activity stream (system_activity.csv).</summary>
public sealed record Event(string ActivityId, DateTime T, string EventType, string Source, string Target, string Resource, string Result);

/// <summary>The part of a request that discovery needs.</summary>
public sealed record CaseKey(string CaseId, IReadOnlySet<string> Users, DateTime ReportedAt, DateTime RequestedAt, DateTime AuditAt, string ReportedStatus = "");

/// <summary>Who got the resource and how: explicit | eliminated | phase | chain | ambiguous | none.</summary>
public sealed record Assignment(string? Resource = null, string How = "none", IReadOnlyList<string>? Candidates = null, Event? Event = null)
{
    public IReadOnlyList<string> CandidateList => Candidates ?? Array.Empty<string>();
}

/// <summary>
/// Find the resources that carry NO identifier in any evidence: deleted accounts (a 404 with no owner) and fully anonymized analytics records (every field null).
/// The only link left is the deletion worker's activity timeline. For every deletion case the worker writes a burst of events around the case's <c>reported_at</c> (R): the account
/// delete 45-75 minutes before R, the analytics anonymization about 29 minutes before R (offsets measured on the data, see NOTES.md). Cases that share the same R produce interleaved
/// bursts, so a candidate is accepted only by ELIMINATION: remove accounts that are explicitly linked to someone else or already assigned, and accept a case's account only when exactly
/// one candidate remains. Ambiguous groups stay unresolved - a guess with probability 1/k of being right is worse than omitting the step.
/// </summary>
public static class Discover
{
    /// <summary>The deletion worker's delete event carries the same result the workflow reports for the case (measured: 189 of 189 cases with an explicit account link). Used only to MATCH an event to its case.</summary>
    private static readonly Dictionary<string, string> ResultOfReportedStatus = new(StringComparer.Ordinal) { ["COMPLETE"] = "SUCCESS", ["PARTIALLY_COMPLETE"] = "PARTIAL", ["NOT_STARTED"] = "FAILED" };

    /// <summary>Activity rows; a row with an unparseable time is skipped, a missing file means an empty stream.</summary>
    public static List<Event> LoadEvents(string root)
    {
        List<Dictionary<string, string>> rows;
        try { rows = Csv.ReadRows(Path.Combine(root, "system_activity.csv"), "activity_id", "event_time", "event_type"); }
        catch (DatasetException) { return new(); }
        var output = new List<Event>();
        foreach (var r in rows)
        {
            var t = Py.TryTs(r["event_time"]);
            if (t is not null) output.Add(new Event(r["activity_id"], t.Value, r["event_type"], r.G("source_system"), r.G("target_system"), r.G("resource_id"), r.G("reported_result")));
        }
        return output;
    }

    private static bool InWindow(Event e, DateTime r, (TimeSpan, TimeSpan) win) => r + win.Item1 <= e.T && e.T <= r + win.Item2;

    /// <summary>
    /// Seconds-phase regularity inside one case. Measured on the accounts that ARE linked explicitly (188 cases): for a given event kind (event_type, target) the second-of-minute of a case's
    /// event minus the second-of-minute of that case's account-delete event falls in a small set of values whatever the minute. Two cases that share a reported_at therefore have
    /// account-delete events that look alike by minute but not by seconds-phase. The table is learned from the participant data itself (explicit links only) and scores how well a candidate
    /// account-delete time fits the case's other events: sum of log-likelihood ratios against a uniform phase. It is a ranking signal only.
    /// </summary>
    public sealed class PhaseModel
    {
        private const double Alpha = 0.5;
        private readonly bool withResult;
        private readonly Dictionary<(string, string, string), (int[] Counts, int Total)> tab = new();

        public PhaseModel(bool withResult = false) { this.withResult = withResult; }

        public bool HasData => tab.Count > 0;

        private (string, string, string) KeyOf(Event e) => withResult ? (e.EventType, e.Target, e.Result) : (e.EventType, e.Target, "");

        private static bool UsableEvent(Event e) => !(e.EventType == "DELETE_RECORD" && e.Target == "account_service");

        private static int Phase(Event e, DateTime tAccount) => Py.Mod((long)Math.Truncate((e.T - tAccount).TotalSeconds), 60);

        public void Learn(DateTime tAccount, IEnumerable<Event> events)
        {
            foreach (var e in events)
            {
                if (!UsableEvent(e)) continue;
                var key = KeyOf(e);
                if (!tab.TryGetValue(key, out var entry)) entry = (new int[60], 0);
                entry.Counts[Phase(e, tAccount)]++;
                tab[key] = (entry.Counts, entry.Total + 1);
            }
        }

        public (double Total, int Used) Score(IEnumerable<Event> events, DateTime tAccount)
        {
            double total = 0.0;
            int used = 0;
            foreach (var e in events)
            {
                if (!UsableEvent(e) || !tab.TryGetValue(KeyOf(e), out var cnt) || cnt.Total == 0) continue;
                int off = Phase(e, tAccount);
                int near = 0;
                foreach (int d in new[] { -1, 0, 1 }) near += cnt.Counts[Py.Mod(off + d, 60)];
                double density = (near + Alpha) / (cnt.Total + Alpha * 60) / 3.0;
                total += Math.Log(density * 60);
                used++;
            }
            return (total, used);
        }
    }

    /// <summary>
    /// Accounts that already belong to the case: linked to its users AND either observed inside the case's evidence window or deleted by the worker inside the account window. The second
    /// kind matters: an account whose only observation is a later snapshot still has an owner, so it must not be offered to another case as a leftover candidate.
    /// </summary>
    public static HashSet<string> KnownAccounts(IEnumerable<string> linked, IReadOnlySet<string> observed, IReadOnlyDictionary<string, List<Event>> eventsByResource, DateTime r, Tunables tune)
    {
        var window = tune.AccountWindow;
        var output = new HashSet<string>(StringComparer.Ordinal);
        foreach (var x in linked.Where(x => x.StartsWith("account_", StringComparison.Ordinal)))
        {
            if (observed.Contains(x)
                || (eventsByResource.TryGetValue(x, out var evs) && evs.Any(e => e.EventType == "DELETE_RECORD" && e.Source == "deletion_worker" && e.Target == "account_service" && InWindow(e, r, window))))
                output.Add(x);
        }
        return output;
    }

    /// <summary>
    /// explicit: case_id -> accounts already linked to the case's users inside its window. owners: account -> users it is explicitly linked to (any time). cohort: case_id -> accounts observed
    /// in an evidence file that also observes the case's other records (same snapshot = same cohort of users); used to narrow an ambiguous group. ownEvents: case_id -> activity events on the
    /// case's own linked resources; enables the seconds-phase stage (<see cref="PhaseModel"/>).
    /// </summary>
    public static Dictionary<string, Assignment> AssignAccounts(IReadOnlyList<CaseKey> cases, IReadOnlyList<Event> events, IReadOnlyDictionary<string, HashSet<string>> explicitLinks,
        IReadOnlyDictionary<string, HashSet<string>> owners, IReadOnlyDictionary<string, HashSet<string>>? cohort, IReadOnlyDictionary<string, List<Event>>? ownEvents, Tunables tune)
    {
        var deletes = events.Where(e => e.EventType == "DELETE_RECORD" && e.Source == "deletion_worker" && e.Target == "account_service").ToList();
        var byAccount = new Dictionary<string, List<Event>>(StringComparer.Ordinal);
        foreach (var e in deletes)
        {
            if (!byAccount.TryGetValue(e.Resource, out var list)) byAccount[e.Resource] = list = new();
            list.Add(e);
        }
        var output = new Dictionary<string, Assignment>(StringComparer.Ordinal);
        var taken = new HashSet<string>(owners.Keys, StringComparer.Ordinal);                  // linked to *some* user: never a guess for another case
        foreach (var c in cases)
        {
            if (explicitLinks.TryGetValue(c.CaseId, out var set) && set.Count > 0)
            {
                var accts = set.OrderBy(a => a, StringComparer.Ordinal).ToList();
                var ev = accts.SelectMany(a => byAccount.TryGetValue(a, out var l) ? l : new List<Event>()).FirstOrDefault();
                output[c.CaseId] = new Assignment(accts[0], "explicit", accts, ev);
                taken.UnionWith(accts);
            }
        }
        var cand = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var index = new TimeIndex<string>(deletes.Select(e => (e.T, e.Resource)));              // one sort instead of a scan of every account for every case
        var window = tune.AccountWindow;
        foreach (var c in cases)
        {
            if (output.ContainsKey(c.CaseId)) continue;
            cand[c.CaseId] = index.Between(c.ReportedAt + window.Item1, c.ReportedAt + window.Item2).Where(a => !taken.Contains(a)).ToHashSet(StringComparer.Ordinal);
        }
        var keys = cases.ToDictionary(c => c.CaseId, StringComparer.Ordinal);
        var full = cand.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.Ordinal), StringComparer.Ordinal);       // before any narrowing
        // stage 1: plain elimination; stage 2: keep accounts seen in the case's cohort snapshot; stage 3: keep accounts whose delete event result matches the case's reported status.
        // Narrowing is soft: it never empties a candidate set.
        var pool = new Pool(cand, output, taken, byAccount);
        foreach (int stage in new[] { 1, 2, 3 })
        {
            if (stage == 2 && cohort is { Count: > 0 })
            {
                foreach (var cid in cand.Keys.ToList())
                {
                    var narrowed = cand[cid].Where(a => cohort.TryGetValue(cid, out var co) && co.Contains(a)).ToHashSet(StringComparer.Ordinal);
                    if (narrowed.Count > 0) cand[cid] = narrowed;
                }
            }
            if (stage == 3)
                foreach (var cid in cand.Keys.ToList()) cand[cid] = ByResult(cand[cid], keys[cid], byAccount, tune);
            pool.Reindex();
            pool.Eliminate();
        }
        // Narrowing is only a preference. When the preferred account went to another case, fall back to the case's full candidate set (minus what is taken), narrowed by the delete result
        // again, and let elimination continue.
        foreach (var cid in cand.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
            if (cand[cid].Count == 0) cand[cid] = ByResult(full[cid].Where(a => !taken.Contains(a)).ToHashSet(StringComparer.Ordinal), keys[cid], byAccount, tune);
        pool.Reindex();
        pool.Eliminate();
        if (ownEvents is not null) PhaseResolve(cases, pool, byAccount, explicitLinks, ownEvents, tune);
        foreach (var (cid, cs) in cand)
            output[cid] = new Assignment(null, cs.Count > 0 ? "ambiguous" : "none", cs.OrderBy(a => a, StringComparer.Ordinal).ToList());
        return output;
    }

    private static Event DeleteEvent(IEnumerable<Event> evs, DateTime r, Tunables tune) => evs.First(e => InWindow(e, r, tune.AccountWindow));

    /// <summary>
    /// Greedy: repeatedly accept the single (case, account) whose phase score leads the runner-up by tune.PhaseMargin the most, then let plain elimination propagate. Deterministic: ties broken by
    /// case_id, then account id (plain string order). Every (case, account) score is computed once and a case is re-ranked only when its own candidate set changed (a heap with lazy invalidation).
    /// </summary>
    private static void PhaseResolve(IReadOnlyList<CaseKey> cases, Pool pool, Dictionary<string, List<Event>> byAccount, IReadOnlyDictionary<string, HashSet<string>> explicitLinks,
        IReadOnlyDictionary<string, List<Event>> ownEvents, Tunables tune)
    {
        var cand = pool.Cand;
        var keys = cases.ToDictionary(c => c.CaseId, StringComparer.Ordinal);
        var model = new PhaseModel();
        var noEvents = new List<Event>();
        foreach (var c in cases)                                           // learn only from explicitly linked single accounts
        {
            if (explicitLinks.TryGetValue(c.CaseId, out var ex) && ex.Count == 1)
            {
                var evs = (byAccount.TryGetValue(ex.First(), out var l) ? l : noEvents).Where(e => InWindow(e, c.ReportedAt, tune.AccountWindow)).ToList();
                if (evs.Count == 1) model.Learn(evs[0].T, ownEvents.GetValueOrDefault(c.CaseId) ?? noEvents);
            }
        }
        var scores = new Dictionary<(string, string), double>();

        double Score(string cid, string a)
        {
            if (!scores.TryGetValue((cid, a), out var v))
                scores[(cid, a)] = v = model.Score(ownEvents.GetValueOrDefault(cid) ?? noEvents, DeleteEvent(byAccount[a], keys[cid].ReportedAt, tune).T).Total;
            return v;
        }

        var version = new Dictionary<string, int>(StringComparer.Ordinal);
        var heap = new PriorityQueue<(double NegLead, string Cid, string Account, int Ver), (double, string, string, int)>(
            Comparer<(double, string, string, int)>.Create((a, b) =>
            {
                int c = a.Item1.CompareTo(b.Item1);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Item2, b.Item2);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Item3, b.Item3);
                return c != 0 ? c : a.Item4.CompareTo(b.Item4);
            }));

        void Push(string cid)
        {
            version[cid] = version.GetValueOrDefault(cid) + 1;
            if (!cand.TryGetValue(cid, out var cs) || cs.Count < 2) return;
            var ranked = cs.OrderBy(a => a, StringComparer.Ordinal).Select(a => (Score: Score(cid, a), Account: a))
                           .OrderBy(x => -x.Score).ThenBy(x => x.Account, StringComparer.Ordinal).ToList();           // stable: equal scores keep account order
            double lead = ranked[0].Score - ranked[1].Score;
            if (lead >= tune.PhaseMargin) heap.Enqueue((-lead, cid, ranked[0].Account, version[cid]), (-lead, cid, ranked[0].Account, version[cid]));
        }

        foreach (var cid in cand.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList()) Push(cid);
        while (heap.Count > 0)
        {
            var (_, cid, a, ver) = heap.Dequeue();
            if (version.GetValueOrDefault(cid) != ver || !cand.ContainsKey(cid)) continue;               // stale entry: the case's candidates changed or it was resolved
            pool.Assign(cid, a, "phase", DeleteEvent(byAccount[a], keys[cid].ReportedAt, tune));
            pool.Eliminate();
            foreach (var other in pool.DrainTouched()) Push(other);
        }
    }

    /// <summary>Values sorted by time: <see cref="Between"/> returns the values whose time is in [lo, hi] without scanning the rest.</summary>
    private sealed class TimeIndex<T>
    {
        private readonly DateTime[] times;
        private readonly T[] values;

        public TimeIndex(IEnumerable<(DateTime T, T Value)> pairs)
        {
            var ordered = pairs.OrderBy(p => p.T).ToArray();                  // stable
            times = ordered.Select(p => p.T).ToArray();
            values = ordered.Select(p => p.Value).ToArray();
        }

        public List<T> Between(DateTime lo, DateTime hi)
        {
            int start = LowerBound(lo), end = UpperBound(hi);
            return end > start ? values[start..end].ToList() : new List<T>();
        }

        private int LowerBound(DateTime x) { int lo = 0, hi = times.Length; while (lo < hi) { int mid = (lo + hi) / 2; if (times[mid] < x) lo = mid + 1; else hi = mid; } return lo; }

        private int UpperBound(DateTime x) { int lo = 0, hi = times.Length; while (lo < hi) { int mid = (lo + hi) / 2; if (x < times[mid]) hi = mid; else lo = mid + 1; } return lo; }
    }

    /// <summary>
    /// The unresolved cases and their candidate accounts, with an index account -> cases that still list it. <see cref="Eliminate"/> reproduces the original multi-pass rule exactly (a case whose
    /// candidate set has one account takes it; cases are taken in the order of the original dict; a case that becomes single behind the cursor waits for the next pass) but touches only the
    /// cases that list the account just taken, instead of re-scanning every case after every acceptance.
    /// </summary>
    private sealed class Pool
    {
        private readonly Dictionary<string, Assignment> output;
        private readonly HashSet<string> taken;
        private readonly Dictionary<string, List<Event>> byAccount;
        private readonly Dictionary<string, int> order;
        private readonly Dictionary<int, string> names;
        private Dictionary<string, HashSet<string>> claimants = new(StringComparer.Ordinal);
        private HashSet<string> touched = new(StringComparer.Ordinal);
        private HashSet<string> sole = new(StringComparer.Ordinal);                 // cases whose candidate set has exactly one account (checked again when used)

        public Pool(Dictionary<string, HashSet<string>> cand, Dictionary<string, Assignment> output, HashSet<string> taken, Dictionary<string, List<Event>> byAccount)
        {
            Cand = cand; this.output = output; this.taken = taken; this.byAccount = byAccount;
            order = cand.Keys.Select((cid, i) => (cid, i)).ToDictionary(x => x.cid, x => x.i, StringComparer.Ordinal);
            names = order.ToDictionary(kv => kv.Value, kv => kv.Key);
        }

        public Dictionary<string, HashSet<string>> Cand { get; }

        public void Reindex()
        {
            claimants = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            sole = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (cid, cs) in Cand)
            {
                foreach (var a in cs)
                {
                    if (!claimants.TryGetValue(a, out var set)) claimants[a] = set = new(StringComparer.Ordinal);
                    set.Add(cid);
                }
                if (cs.Count == 1) sole.Add(cid);
            }
        }

        public void Assign(string cid, string a, string how, Event ev)
        {
            output[cid] = new Assignment(a, how, new[] { a }, ev);
            var own = Cand[cid];
            Cand.Remove(cid);
            foreach (var x in own)
                if (claimants.TryGetValue(x, out var set)) set.Remove(cid);
            taken.Add(a);
            if (claimants.Remove(a, out var others))
                foreach (var other in others)
                {
                    Cand[other].Remove(a);
                    touched.Add(other);
                    if (Cand[other].Count == 1) sole.Add(other);
                }
        }

        public List<string> DrainTouched()
        {
            var t = touched.OrderBy(c => c, StringComparer.Ordinal).ToList();
            touched = new HashSet<string>(StringComparer.Ordinal);
            return t.Where(Cand.ContainsKey).ToList();
        }

        public void Eliminate()
        {
            while (true)
            {
                var heap = new PriorityQueue<int, int>();
                foreach (var cid in sole.Where(c => Cand.TryGetValue(c, out var s) && s.Count == 1)) heap.Enqueue(order[cid], order[cid]);
                sole = sole.Where(c => Cand.TryGetValue(c, out var s) && s.Count == 1).ToHashSet(StringComparer.Ordinal);
                bool progressed = false;
                while (heap.Count > 0)
                {
                    int i = heap.Dequeue();
                    var cid = names[i];
                    if (!Cand.TryGetValue(cid, out var cs) || cs.Count != 1) continue;
                    var a = cs.First();
                    var before = (claimants.TryGetValue(a, out var cl) ? cl : new HashSet<string>(StringComparer.Ordinal)).Where(c => c != cid).ToList();
                    Assign(cid, a, "eliminated", byAccount[a][0]);
                    progressed = true;
                    foreach (var c in before)
                        if (Cand.TryGetValue(c, out var s2) && s2.Count == 1 && order[c] > i) heap.Enqueue(order[c], order[c]);       // ahead of the cursor: still taken in this pass
                }
                if (!progressed) return;
            }
        }
    }

    /// <summary>Keep the accounts whose delete event result matches the case's reported status (soft: never empties the set).</summary>
    private static HashSet<string> ByResult(HashSet<string> accts, CaseKey c, Dictionary<string, List<Event>> byAccount, Tunables tune)
    {
        ResultOfReportedStatus.TryGetValue(c.ReportedStatus, out var want);
        var narrowed = accts.Where(a => byAccount[a].Any(e => e.Result == want && InWindow(e, c.ReportedAt, tune.AccountWindow))).ToHashSet(StringComparer.Ordinal);
        return narrowed.Count > 0 ? narrowed : new HashSet<string>(accts, StringComparer.Ordinal);
    }

    private static readonly Dictionary<(int, int, int), HashSet<int>> ChainSumCache = new();

    private static HashSet<int> ChainSums(int k, (int, int) gaps)
    {
        lock (ChainSumCache)
        {
            if (ChainSumCache.TryGetValue((k, gaps.Item1, gaps.Item2), out var cached)) return cached;
            var sums = new HashSet<int> { 0 };
            for (int i = 0; i < k; i++) sums = sums.SelectMany(x => new[] { x + gaps.Item1, x + gaps.Item2 }).ToHashSet();
            ChainSumCache[(k, gaps.Item1, gaps.Item2)] = sums;
            return sums;
        }
    }

    /// <summary>delta between a candidate ANONYMIZE event and the k-th later event of the same case's chain (support=1, flow=2, backup=3).</summary>
    public static bool ChainOk(double deltaSeconds, int k, Tunables tune) => ChainSums(k, tune.ChainGapsSeconds).Any(x => Math.Abs(deltaSeconds - x) <= tune.ChainToleranceSeconds);

    /// <summary>
    /// chain: case_id -> [(event time, position k)] of the case's OWN explicitly linked policy actions (REDACT support k=1, DISABLE flow k=2, BLOCK backup k=3).
    /// 1) a candidate whose time fits the case's chain wins; 1b) seconds-phase: learned from the chain-resolved pairs, an anonymization event sits at a fixed second-offset from its case's
    /// account-delete event (<paramref name="accountTime"/>), so among a case's candidates the one whose offset fits wins when no other scored claimant of that candidate fits; 2) otherwise
    /// elimination: a candidate counts only when exactly one still-unassigned case has it in its window. <paramref name="foreignChain"/>(case, event) says that the candidate sits inside
    /// another user's completed chain (a decoy user whose deletion happened in the same hour): such a candidate is dropped.
    /// </summary>
    public static Dictionary<string, Assignment> AssignAnonymizedAnalytics(IReadOnlyList<CaseKey> cases, IReadOnlyList<Event> events, IReadOnlySet<string> anonymized,
        IReadOnlyDictionary<string, List<(DateTime T, int K)>> chain, Func<CaseKey, Event, bool>? foreignChain, IReadOnlyDictionary<string, DateTime>? accountTime, Tunables tune)
    {
        var anonEvents = events.Where(e => e.EventType == "ANONYMIZE_RECORD" && e.Source == "deletion_worker" && e.Target == "analytics_platform" && e.Result == "SUCCESS" && anonymized.Contains(e.Resource)).ToList();
        // candidates per case: one sort by time and a bisect per case. The events of a window are taken back in their original order, so a resource that was anonymized twice still keeps the LAST event.
        var (winLo, winHi) = tune.AnalyticsWindow;
        var times = new TimeIndex<int>(anonEvents.Select((e, i) => (e.T, i)));
        var cand = new Dictionary<string, Dictionary<string, Event>>(StringComparer.Ordinal);
        foreach (var c in cases)
        {
            var picked = times.Between(c.ReportedAt + winLo, c.ReportedAt + winHi);
            picked.Sort();
            var d = new Dictionary<string, Event>(StringComparer.Ordinal);
            foreach (int i in picked)
                if (!(foreignChain is not null && foreignChain(c, anonEvents[i]))) d[anonEvents[i].Resource] = anonEvents[i];
            cand[c.CaseId] = d;
        }
        var output = new Dictionary<string, Assignment>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cases)                                             // pass 1: chain timing
        {
            var links = chain.TryGetValue(c.CaseId, out var l) ? l : new List<(DateTime T, int K)>();
            var fits = cand[c.CaseId].Where(kv => links.Any(x => ChainOk((x.T - kv.Value.T).TotalSeconds, x.K, tune))).Select(kv => kv.Key).ToList();
            if (fits.Count == 1)
            {
                var r = fits[0];
                output[c.CaseId] = new Assignment(r, "chain", new[] { r }, cand[c.CaseId][r]);
                taken.Add(r);
            }
        }
        if (accountTime is { Count: > 0 }) AnalyticsPhase(cases, cand, output, taken, accountTime, tune);
        var pending = cases.Where(c => !output.ContainsKey(c.CaseId)).Select(c => c.CaseId).ToHashSet(StringComparer.Ordinal);              // frozen: ambiguous answers must not shrink the claimant sets
        var claimantCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cid in pending) foreach (var r in cand[cid].Keys) claimantCount[r] = claimantCount.GetValueOrDefault(r) + 1;          // how many pending cases have each candidate in their window
        foreach (var c in cases)                                             // pass 2: elimination
        {
            if (output.ContainsKey(c.CaseId)) continue;
            var rest = cand[c.CaseId].Keys.Where(r => !taken.Contains(r)).ToList();
            if (rest.Count == 1 && claimantCount.GetValueOrDefault(rest[0]) == 1)
                output[c.CaseId] = new Assignment(rest[0], "eliminated", new[] { rest[0] }, cand[c.CaseId][rest[0]]);
            else
                output[c.CaseId] = new Assignment(null, rest.Count > 0 ? "ambiguous" : "none", rest.OrderBy(r => r, StringComparer.Ordinal).ToList());
        }
        return output;
    }

    private static void AnalyticsPhase(IReadOnlyList<CaseKey> cases, Dictionary<string, Dictionary<string, Event>> cand, Dictionary<string, Assignment> output, HashSet<string> taken,
        IReadOnlyDictionary<string, DateTime> accountTime, Tunables tune)
    {
        var model = new PhaseModel(withResult: true);
        foreach (var (cid, a) in output)                                       // learn only from the chain-resolved pairs
            if (a.How == "chain" && accountTime.TryGetValue(cid, out var at) && a.Event is not null) model.Learn(at, new[] { a.Event });
        if (!model.HasData) return;

        var memo = new Dictionary<(string, string), bool>();

        bool Fits(string cid, Event ev)
        {
            if (!memo.TryGetValue((cid, ev.Resource), out var v))
                memo[(cid, ev.Resource)] = v = model.Score(new[] { ev }, accountTime[cid]).Total >= tune.AnalyticsMinLlr;
            return v;
        }

        var scored = cases.Where(c => !output.ContainsKey(c.CaseId) && accountTime.ContainsKey(c.CaseId)).Select(c => c.CaseId).ToList();
        var claimedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);                  // candidate resource -> scored cases that have it in their window (static)
        foreach (var o in scored)
            foreach (var r in cand[o].Keys)
            {
                if (!claimedBy.TryGetValue(r, out var l)) claimedBy[r] = l = new();
                l.Add(o);
            }
        foreach (var cid in scored.OrderBy(c => c, StringComparer.Ordinal))
        {
            var good = cand[cid].Where(kv => !taken.Contains(kv.Key) && Fits(cid, kv.Value)).Select(kv => kv.Key).OrderBy(r => r, StringComparer.Ordinal).ToList();
            if (good.Count != 1) continue;
            var r = good[0];
            if (claimedBy[r].Any(o => o != cid && !output.ContainsKey(o) && Fits(o, cand[o][r]))) continue;          // a rival scored case fits it as well
            output[cid] = new Assignment(r, "phase", new[] { r }, cand[cid][r]);
            taken.Add(r);
        }
    }
}
