using System.Globalization;

namespace Auditor.Tools;

/// <summary>
/// Hold-out measurement of the timeline discovery (accounts and anonymized analytics), on the participant data only. The discovery finds resources that carry no identifier. Its precision
/// cannot be read off labeled answers for most cases, so it is measured the other way round: take cases whose resource IS linked explicitly (that link is the label), hide the link for a
/// random share of them, run the real discovery code, and check what it assigns. Every hidden case is scored as one of
///   correct   the discovery assigned exactly the hidden resource
///   wrong     it assigned another resource to the hidden case
///   stolen    it assigned nothing to the hidden case AND gave the hidden case's resource to a different case (the failure a per-case score would miss)
///   omitted   it assigned nothing (ambiguous or no candidate)
/// Precision of what is assigned = correct / (correct + wrong + stolen). The random draws use <see cref="Random"/> with the seed, so a run is reproducible.
/// </summary>
public static class Holdout
{
    public sealed class Result
    {
        public int N;
        public int Correct, Wrong, Stolen, Omitted;
        public double Precision()
        {
            int denom = Correct + Wrong + Stolen;
            return denom > 0 ? (double)Correct / denom : 1.0;
        }

        public string Line(string name)
        {
            double n = Math.Max(N, 1);
            return $"| {name} | {N} | {Correct} ({(100.0 * Correct / n).ToString("F1", CultureInfo.InvariantCulture)}%) | {Wrong} | {Stolen} | {Omitted} | **{(100 * Precision()).ToString("F1", CultureInfo.InvariantCulture)}%** |";
        }
    }

    public static readonly string[] Header =
    {
        "| setting | hidden cases | correct | wrong | stolen | omitted | precision of assignments |", "|---|---|---|---|---|---|---|",
    };

    /// <summary>k distinct items of <paramref name="items"/> (a partial Fisher-Yates shuffle with the given generator).</summary>
    private static List<T> Sample<T>(IReadOnlyList<T> items, int k, Random rnd)
    {
        var pool = items.ToList();
        for (int i = 0; i < Math.Min(k, pool.Count); i++)
        {
            int j = rnd.Next(i, pool.Count);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        return pool.Take(k).ToList();
    }

    /// <summary>case → its explicitly linked account, for cases with one linked account and a worker delete event in the window.</summary>
    private static Dictionary<string, string> AccountTruth(CaseAuditor aud)
    {
        var inputs = aud.DiscInputs ?? throw new InvalidOperationException("discovery did not run");
        var deletes = new Dictionary<string, List<Event>>(StringComparer.Ordinal);
        foreach (var e in aud.Events.Where(e => e.EventType == "DELETE_RECORD" && e.Source == "deletion_worker" && e.Target == "account_service"))
        {
            if (!deletes.TryGetValue(e.Resource, out var list)) deletes[e.Resource] = list = new();
            list.Add(e);
        }
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in inputs.Cases)
            if (inputs.Explicit.TryGetValue(c.CaseId, out var ex) && ex.Count == 1)
            {
                var account = ex.First();
                if (deletes.TryGetValue(account, out var evs) && evs.Any(e => Discover.InWindow(e, c.ReportedAt, aud.Cfg.Tune.AccountWindow))) output[c.CaseId] = account;
            }
        return output;
    }

    public static Result Accounts(CaseAuditor aud, double fraction, IEnumerable<int> seeds, bool useCohort = false, bool useTimingHeuristics = true)
    {
        var inputs = aud.DiscInputs ?? throw new InvalidOperationException("discovery did not run");
        var keys = inputs.Cases.ToDictionary(c => c.CaseId, StringComparer.Ordinal);
        var truthAll = AccountTruth(aud);
        var res = new Result();
        var truthKeys = truthAll.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        foreach (var seed in seeds)
        {
            var hold = Sample(truthKeys, (int)(fraction * truthKeys.Count), new Random(seed)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var holdSet = hold.ToHashSet(StringComparer.Ordinal);
            var ex2 = inputs.Explicit.ToDictionary(kv => kv.Key, kv => holdSet.Contains(kv.Key) ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(kv.Value, StringComparer.Ordinal), StringComparer.Ordinal);
            var ow2 = inputs.Owners.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (var h in hold)
            {
                var account = truthAll[h];
                var left = ow2.TryGetValue(account, out var owners) ? owners.Except(keys[h].Users).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
                if (left.Count == 0) ow2.Remove(account); else ow2[account] = left;
            }
            var output = Discover.AssignAccounts(inputs.Cases, aud.Events, ex2, ow2, useCohort ? inputs.Cohort : null, useTimingHeuristics ? inputs.OwnEvents : null, aud.Cfg.Tune);
            var takerOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (cid, a) in output)
                if (a.Resource is not null && a.How != "explicit") takerOf[a.Resource] = cid;
            foreach (var h in hold)
            {
                var a = output[h]; var truth = truthAll[h];
                res.N++;
                if (a.Resource == truth) res.Correct++;
                else if (a.Resource is not null) res.Wrong++;
                else if (takerOf.TryGetValue(truth, out var taker) && taker != h) res.Stolen++;
                else res.Omitted++;
            }
        }
        return res;
    }

    /// <summary>Hide the chain-resolved analytics assignments (the most reliable ones) and see whether the phase / elimination stages recover them.</summary>
    public static Result Analytics(CaseAuditor aud, double fraction, IEnumerable<int> seeds, bool useTimingHeuristics = true)
    {
        var inputs = aud.AnalyticsIn ?? throw new InvalidOperationException("discovery did not run");
        var truthAll = aud.AnalyticsAsg.Where(kv => kv.Value.How == "chain" && kv.Value.Resource is not null).ToDictionary(kv => kv.Key, kv => kv.Value.Resource!, StringComparer.Ordinal);
        var res = new Result();
        if (truthAll.Count == 0) return res;
        var truthKeys = truthAll.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        foreach (var seed in seeds)
        {
            var hold = Sample(truthKeys, Math.Max(1, (int)(fraction * truthKeys.Count)), new Random(seed)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var holdSet = hold.ToHashSet(StringComparer.Ordinal);
            var chain = inputs.Chain.ToDictionary(kv => kv.Key, kv => holdSet.Contains(kv.Key) ? new List<(DateTime T, int K)>() : kv.Value, StringComparer.Ordinal);
            var output = Discover.AssignAnonymizedAnalytics(inputs.Cases, inputs.Events, inputs.Anonymized, chain, inputs.Foreign, useTimingHeuristics ? inputs.AccountTime : null, inputs.Tune);
            var takerOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (cid, a) in output)
                if (a.Resource is not null) takerOf[a.Resource] = cid;
            foreach (var h in hold)
            {
                var a = output[h]; var truth = truthAll[h];
                res.N++;
                if (a.Resource == truth) res.Correct++;
                else if (a.Resource is not null) res.Wrong++;
                else if (takerOf.TryGetValue(truth, out var taker) && taker != h) res.Stolen++;
                else res.Omitted++;
            }
        }
        return res;
    }
}
