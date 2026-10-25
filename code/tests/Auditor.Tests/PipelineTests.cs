
namespace Auditor.Tests;

/// <summary>Unit tests for the pipeline pieces, on synthetic data (plus regression guards on the labeled samples).</summary>
public class LogParseTests
{
    [Fact]
    public void SevenWrappersGiveTheSameEvent()
    {
        var lines = new[]
        {
            "2026-09-15T11:56:47Z level=INFO service=account_service trace_id=tr-1 resource_id=account_9 event=profile_lookup_completed rows=0 http_status=404",
            "2026-09-15T11:56:47Z INFO service=downstream_delivery_service event=record_lookup seq=1 system=\"account_service\" resource_id=\"account_9\" record_type=\"USER_PROFILE\" result=\"NOT_FOUND\" subject_reference=\"a@b.com\"",
            "2026-09-15T11:56:47Z [INFO] [account_service] [tr-1] profile_lookup_completed: resource_id=\"account_9\" rows=\"0\" http_status=\"404\"",
            "2026-09-15T11:56:47Z | INFO | account_service | tr-1 | profile_lookup_completed | resource_id=\"account_9\" rows=\"0\" http_status=\"404\"",
            "2026-09-15T11:56:47Z requestId=tr-1 level=INFO function=profile_service message=\"resource_id=\\\"account_9\\\" event=\\\"profile_lookup_completed\\\" rows=\\\"0\\\" http_status=\\\"404\\\"\"",
            "2026-09-15T11:56:47Z {\"severity\":\"INFO\",\"service\":\"account_service\",\"trace_id\":\"tr-1\",\"resource_id\":\"account_9\",\"event\":\"profile_lookup_completed\",\"rows\":\"0\",\"http_status\":\"404\"}",
            "2026-09-15T11:56:47Z <134>1 node-3 account_service tr-1 - - level=INFO resource_id=\"account_9\" event=\"profile_lookup_completed\" rows=\"0\" http_status=\"404\"",
        };
        foreach (var l in lines)
        {
            var e = LogParse.ParseLine(l);
            Assert.NotNull(e);
            Assert.Equal("account_9", e!.Fields.GetValueOrDefault("resource_id"));
        }
        var completed = lines.Where(l => l.Contains("profile_lookup_completed", StringComparison.Ordinal)).Select(l => LogParse.ParseLine(l)!).ToList();
        Assert.All(completed, e => { Assert.Equal("profile_lookup_completed", e.Event); Assert.Equal("0", e.Fields["rows"]); });
        var traces = completed.Where(e => e.Trace.Length > 0).Select(e => e.Trace).ToList();
        Assert.Equal(Enumerable.Repeat("tr-1", traces.Count), traces);
    }

    [Fact]
    public void LiteralNullIsAMissingValue()
    {
        var e = LogParse.ParseLine("2026-09-15T10:00:00Z level=INFO service=analytics_platform trace_id=t event=analytics_profile_read_completed resource_id=analytics_1 email=null phone=None customer_reference=null")!;
        Assert.Equal(("", "", ""), (e.Fields["email"], e.Fields["phone"], e.Fields["customer_reference"]));
    }

    [Fact]
    public void GarbageLineIsSkippedNotFatal()
    {
        Assert.Null(LogParse.ParseLine("this is not a log line"));
        Assert.Null(LogParse.ParseLine("2026-09-15T10:00:00Z {not json"));
    }
}

public class StateTests
{
    private static DateTime T(string s) => Compat.ParseTs(s);
    private static readonly DateTime T0 = T("2026-09-01T00:00:00Z"), A = T("2026-09-10T00:00:00Z");

    private static Obs Make(string fileT, string state, string source = "sql:t", string? t = null, string fid = "f", string res = "account_1", string cat = "USER_PROFILE")
    {
        var ft = T(fileT);
        return new Obs(fid, ft, t is null ? ft : T(t), "account_service", res, cat, state, new HashSet<string>(), source);
    }

    [Fact]
    public void WindowExcludesBeforeRequestAndAfterAudit()
    {
        var old = Make("2026-08-01T00:00:00Z", "PRESENT", fid: "old");
        var inside = Make("2026-09-05T00:00:00Z", "ABSENT", fid: "in");
        var later = Make("2026-09-12T00:00:00Z", "PRESENT", fid: "later");
        var st = StateResolver.Resolve(new[] { old, inside, later }, T0, A)!;
        Assert.Equal(("in", "ABSENT"), (st.FileId, st.State));
        Assert.Null(StateResolver.Resolve(new[] { old, later }, T0, A));
    }

    [Fact]
    public void AuditBoundaryIsInclusive() => Assert.NotNull(StateResolver.Resolve(new[] { Make("2026-09-10T00:00:00Z", "PRESENT") }, T0, A));

    [Fact]
    public void LatestFileWinsAndDriftInsideOneFileCounts()
    {
        var first = Make("2026-09-02T00:00:00Z", "PRESENT", fid: "f1");
        var primary = Make("2026-09-06T00:00:00Z", "ABSENT", fid: "f2", source: "sql:primary");
        var replica = Make("2026-09-06T00:00:00Z", "PRESENT", fid: "f2", source: "sql:replica");     // replication drift
        Assert.Equal("ABSENT", StateResolver.Resolve(new[] { first, primary }, T0, A)!.State);
        Assert.Equal("PRESENT", StateResolver.Resolve(new[] { first, primary, replica }, T0, A)!.State);
    }

    [Fact]
    public void InALogTheLastEventWins()
    {
        var a = Make("2026-09-06T00:00:00Z", "PRESENT", source: "log:x", t: "2026-09-05T10:00:00Z");
        var b = Make("2026-09-06T00:00:00Z", "ABSENT", source: "log:x", t: "2026-09-05T11:00:00Z");
        Assert.Equal("ABSENT", StateResolver.Resolve(new[] { a, b }, T0, A)!.State);
    }
}

public class DiscoveryTests
{
    private static DateTime T(string s) => Compat.ParseTs(s);
    private static readonly DateTime R = T("2026-09-02T12:00:00Z");
    private static readonly Tunables Tune = new();

    private static Event Ev(int i, string res, double minutes, string kind = "DELETE_RECORD", string target = "account_service", string result = "SUCCESS") =>
        new($"a{i}", R.AddMinutes(minutes), kind, "deletion_worker", target, res, result);

    private static CaseKey Case(string cid, string status = "COMPLETE", DateTime? r = null)
    {
        var rr = r ?? R;
        return new CaseKey(cid, new HashSet<string> { "u_" + cid }, rr, rr.AddDays(-2), rr.AddDays(10), status);
    }

    private static Dictionary<string, HashSet<string>> None() => new();

    [Fact]
    public void ChainGapsAreSumsOf390And449()
    {
        Assert.True(Discover.ChainOk(449, 1, Tune) && Discover.ChainOk(390, 1, Tune) && Discover.ChainOk(839, 2, Tune) && Discover.ChainOk(1288, 3, Tune));
        Assert.False(Discover.ChainOk(600, 1, Tune));
    }

    [Fact]
    public void EliminationAssignsTheOnlyRemainingAccount()
    {
        var events = new[] { Ev(1, "account_A", -60), Ev(2, "account_B", -50) };
        var output = Discover.AssignAccounts(new[] { Case("c1"), Case("c2") }, events, new Dictionary<string, HashSet<string>> { ["c1"] = new() { "account_A" } }, new Dictionary<string, HashSet<string>> { ["account_A"] = new() { "u_c1" } }, null, null, Tune);
        Assert.Equal(("account_B", "eliminated"), (output["c2"].Resource, output["c2"].How));
    }

    [Fact]
    public void TwoUnknownCasesWithTwoCandidatesStayAmbiguous()
    {
        var events = new[] { Ev(1, "account_A", -60), Ev(2, "account_B", -50) };
        var output = Discover.AssignAccounts(new[] { Case("c1"), Case("c2") }, events, None(), None(), null, null, Tune);
        Assert.Equal(new string?[] { null, null }, new[] { output["c1"].Resource, output["c2"].Resource });
        Assert.All(new[] { "c1", "c2" }, c => Assert.Equal("ambiguous", output[c].How));
    }

    [Fact]
    public void ReportedStatusMatchesTheEventResultToBreakATie()
    {
        var events = new[] { Ev(1, "account_A", -60, result: "SUCCESS"), Ev(2, "account_B", -50, result: "FAILED") };
        var output = Discover.AssignAccounts(new[] { Case("c1", "COMPLETE"), Case("c2", "NOT_STARTED") }, events, None(), None(), null, null, Tune);
        Assert.Equal(("account_A", "account_B"), (output["c1"].Resource!, output["c2"].Resource!));
    }

    [Fact]
    public void CohortSnapshotNarrowsCandidates()
    {
        var events = new[] { Ev(1, "account_A", -60), Ev(2, "account_B", -50) };
        var output = Discover.AssignAccounts(new[] { Case("c1") }, events, None(), None(), new Dictionary<string, HashSet<string>> { ["c1"] = new() { "account_B" } }, null, Tune);
        Assert.Equal("account_B", output["c1"].Resource);
    }

    [Fact]
    public void LateLinkedOwnAccountIsKnownAndNotOfferedToANeighbour()
    {
        // Regression: c1's own account is linked only by a later snapshot (no observation in its window). It must still count as c1's,
        // otherwise elimination hands the only other candidate (c2's real account) to c1.
        var events = new[] { Ev(1, "account_A", -60, result: "FAILED"), Ev(2, "account_B", -50) };
        var c1 = Case("c1", "NOT_STARTED");
        var known = Discover.KnownAccounts(new[] { "account_A" }, new HashSet<string>(), new Dictionary<string, List<Event>> { ["account_A"] = new() { events[0] }, ["account_B"] = new() { events[1] } }, c1.ReportedAt, Tune);
        Assert.Equal(new[] { "account_A" }, known);
        var output = Discover.AssignAccounts(new[] { c1, Case("c2") }, events, new Dictionary<string, HashSet<string>> { ["c1"] = known }, new Dictionary<string, HashSet<string>> { ["account_A"] = new() { "u_c1" } }, null, null, Tune);
        Assert.Equal(("account_A", "account_B"), (output["c1"].Resource!, output["c2"].Resource!));
    }

    [Fact]
    public void LinkedAccountWithoutADeleteEventInTheWindowIsNotKnown()
    {
        var ev = Ev(1, "account_A", -300);                                // outside the 90 minute window
        Assert.Empty(Discover.KnownAccounts(new[] { "account_A" }, new HashSet<string>(), new Dictionary<string, List<Event>> { ["account_A"] = new() { ev } }, R, Tune));
        Assert.Equal(new[] { "account_A" }, Discover.KnownAccounts(new[] { "account_A" }, new HashSet<string> { "account_A" }, new Dictionary<string, List<Event>>(), R, Tune));    // observed in window
    }

    [Fact]
    public void PhaseModelPrefersTheCandidateWhoseSecondsPhaseFits()
    {
        // Learned from explicit pairs: a support redaction lands 10 s (+-1) after the account delete, whatever the minute.
        var m = new Discover.PhaseModel();
        for (int i = 0; i < 12; i++)
        {
            var d = R.AddMinutes(-50).AddSeconds(i * 7 % 60);
            m.Learn(d, new[] { new Event($"s{i}", d.AddMinutes(100 + i).AddSeconds(10), "REDACT_RECORD", "deletion_worker", "support_platform", "support_1", "SUCCESS") });
        }
        var own = new[] { new Event("x", R.AddMinutes(45).AddSeconds(5), "REDACT_RECORD", "deletion_worker", "support_platform", "support_9", "SUCCESS") };
        var fits = m.Score(own, R.AddMinutes(-50).AddSeconds(55)).Total;       // 5 - 55 = 10 mod 60
        var off = m.Score(own, R.AddMinutes(-50).AddSeconds(20)).Total;
        Assert.True(fits > 1.5);
        Assert.True(off < 0.0);
    }

    private static Event WithSecond(Event e, int s) => e with { T = new DateTime(e.T.Year, e.T.Month, e.T.Day, e.T.Hour, e.T.Minute, s, DateTimeKind.Utc) };

    [Fact]
    public void PhaseStageBreaksATieThatEliminationCannot()
    {
        var events = new[] { Ev(1, "account_A", -60), Ev(2, "account_B", -50) }.Zip(new[] { 10, 40 }, WithSecond).ToList();
        var known = Enumerable.Range(0, 10).Select(i => Case($"k{i}")).ToList();
        var train = new List<Event>();
        var explicitLinks = new Dictionary<string, HashSet<string>>();
        var own = new Dictionary<string, List<Event>>();
        for (int i = 0; i < known.Count; i++)                     // ten solved cases: support redaction always 20 s after the account delete
        {
            var d = new Event($"d{i}", known[i].ReportedAt.AddMinutes(-55).AddSeconds(i), "DELETE_RECORD", "deletion_worker", "account_service", $"account_k{i}", "SUCCESS");
            train.Add(d);
            explicitLinks[known[i].CaseId] = new() { $"account_k{i}" };
            own[known[i].CaseId] = new() { new Event($"r{i}", d.T.AddMinutes(90).AddSeconds(20), "REDACT_RECORD", "deletion_worker", "support_platform", $"support_k{i}", "SUCCESS") };
        }
        var c1 = Case("c1"); var c2 = Case("c2");
        own["c1"] = new() { new Event("rc1", events[0].T.AddMinutes(95).AddSeconds(20), "REDACT_RECORD", "deletion_worker", "support_platform", "support_c1", "SUCCESS") };
        own["c2"] = new() { new Event("rc2", events[1].T.AddMinutes(95).AddSeconds(20), "REDACT_RECORD", "deletion_worker", "support_platform", "support_c2", "SUCCESS") };
        var all = known.Concat(new[] { c1, c2 }).ToList();
        var output = Discover.AssignAccounts(all, events.Concat(train).ToList(), explicitLinks, None(), null, own, Tune);
        Assert.Equal(("account_A", "phase"), (output["c1"].Resource!, output["c1"].How));
        Assert.Equal("account_B", output["c2"].Resource);
        var noOwn = new Dictionary<string, List<Event>>(own) { ["c1"] = new(), ["c2"] = new() };
        output = Discover.AssignAccounts(all, events.Concat(train).ToList(), explicitLinks, None(), null, noOwn, Tune);
        Assert.Equal(new string?[] { null, null }, new[] { output["c1"].Resource, output["c2"].Resource });         // no events -> still ambiguous, never a coin flip
    }

    [Fact]
    public void NarrowingToANeighboursAccountFallsBackToTheFullCandidateSet()
    {
        // Regression: the cohort snapshot lists the neighbour's account; once the neighbour takes it, c2 must get its own, not nothing.
        var events = new[] { Ev(1, "account_A", -60), Ev(2, "account_B", -50) };
        var output = Discover.AssignAccounts(new[] { Case("c1"), Case("c2") }, events, None(), None(), new Dictionary<string, HashSet<string>> { ["c1"] = new() { "account_A" }, ["c2"] = new() { "account_A" } }, null, Tune);
        Assert.NotNull(output["c2"].Resource);                                                  // not left empty-handed
        Assert.NotEqual(output["c1"].Resource, output["c2"].Resource);                         // and never two cases on one account
    }

    [Fact]
    public void AnalyticsPhasePicksTheClaimantWhoseOffsetFits()
    {
        // Chain-resolved pairs teach the offset (anonymization 41 s after the account delete); an ambiguous candidate then goes to the claimant that fits,
        // provided no other scored claimant fits too.
        var cases = new List<CaseKey>(); var events = new List<Event>();
        var chain = new Dictionary<string, List<(DateTime T, int K)>>(); var atime = new Dictionary<string, DateTime>();
        for (int i = 0; i < 20; i++)
        {
            var c = Case($"k{i}");
            cases.Add(c);
            var d = c.ReportedAt.AddMinutes(-55).AddSeconds(i * 7);                // 7 s apart: no two chains alias
            atime[c.CaseId] = d;
            var a = new Event($"an{i}", d.AddMinutes(26).AddSeconds(41), "ANONYMIZE_RECORD", "deletion_worker", "analytics_platform", $"analytics_k{i}", "SUCCESS");
            events.Add(a);
            chain[c.CaseId] = new() { (a.T.AddSeconds(449), 1) };
        }
        var c1 = Case("c1"); var c2 = Case("c2");
        atime["c1"] = R.AddMinutes(-55).AddSeconds(3); atime["c2"] = R.AddMinutes(-52).AddSeconds(33);
        var shared = new Event("sh", atime["c1"].AddMinutes(25).AddSeconds(41), "ANONYMIZE_RECORD", "deletion_worker", "analytics_platform", "analytics_S", "SUCCESS");
        var anonymized = new HashSet<string> { "analytics_S" }; foreach (var i in Enumerable.Range(0, 20)) anonymized.Add($"analytics_k{i}");
        var output = Discover.AssignAnonymizedAnalytics(cases.Concat(new[] { c1, c2 }).ToList(), events.Concat(new[] { shared }).ToList(), anonymized, chain, null, atime, Tune);
        Assert.Equal(("analytics_S", "phase"), (output["c1"].Resource!, output["c1"].How));
        Assert.Null(output["c2"].Resource);
    }

    [Fact]
    public void AccountLinkedToSomeoneElseIsNeverGuessed()
    {
        var output = Discover.AssignAccounts(new[] { Case("c1") }, new[] { Ev(1, "account_A", -60) }, None(), new Dictionary<string, HashSet<string>> { ["account_A"] = new() { "someone_else" } }, null, null, Tune);
        Assert.Null(output["c1"].Resource);
    }

    private static readonly IReadOnlyDictionary<string, List<(DateTime T, int K)>> NoChain = new Dictionary<string, List<(DateTime, int)>>();

    [Fact]
    public void AnalyticsClaimedByTwoCasesIsNotAssignedAndAmbiguityDoesNotLeak()
    {
        // Regression: an 'ambiguous' answer for one case must not shrink the claimant set of the next.
        var events = new[] { Ev(1, "analytics_X", -29, "ANONYMIZE_RECORD", "analytics_platform") };
        var output = Discover.AssignAnonymizedAnalytics(new[] { Case("c1"), Case("c2") }, events, new HashSet<string> { "analytics_X" }, NoChain, null, null, Tune);
        Assert.Equal(new string?[] { null, null }, new[] { output["c1"].Resource, output["c2"].Resource });
    }

    [Fact]
    public void AnalyticsUniqueClaimantIsAssigned()
    {
        var events = new[] { Ev(1, "analytics_X", -29, "ANONYMIZE_RECORD", "analytics_platform") };
        var output = Discover.AssignAnonymizedAnalytics(new[] { Case("c1"), Case("c2", r: R.AddDays(3)) }, events, new HashSet<string> { "analytics_X" }, NoChain, null, null, Tune);
        Assert.Equal("analytics_X", output["c1"].Resource);
    }

    [Fact]
    public void ChainTimingPicksTheCandidateThatFitsTheCasesOwnSupportEvent()
    {
        var events = new[] { Ev(1, "analytics_X", -29, "ANONYMIZE_RECORD", "analytics_platform"), Ev(2, "analytics_Y", -36, "ANONYMIZE_RECORD", "analytics_platform") };
        var supportTime = events[0].T.AddSeconds(449);
        var output = Discover.AssignAnonymizedAnalytics(new[] { Case("c1"), Case("c2") }, events, new HashSet<string> { "analytics_X", "analytics_Y" },
            new Dictionary<string, List<(DateTime T, int K)>> { ["c1"] = new() { (supportTime, 1) } }, null, null, Tune);
        Assert.Equal(("analytics_X", "chain"), (output["c1"].Resource!, output["c1"].How));
    }

    [Fact]
    public void DecoyUsersCompletedChainExcludesTheCandidate()
    {
        var events = new[] { Ev(1, "analytics_X", -29, "ANONYMIZE_RECORD", "analytics_platform") };
        var output = Discover.AssignAnonymizedAnalytics(new[] { Case("c1") }, events, new HashSet<string> { "analytics_X" }, NoChain, (_, _) => true, null, Tune);     // another user's chain follows this event
        Assert.Null(output["c1"].Resource);
    }
}

public class PolicyTests
{
    private static PolicyEngine Engine(TempDir d) => new(d.Write("rules.csv",
        "rule_id,data_category,condition,required_action\n" +
        "r1,USER_PROFILE,Always,DELETE\nr2,SUPPORT_MESSAGE,shared,REDACT\nr3,ANALYTICS_PROFILE,attributable,ANONYMIZE\n" +
        "r4,SECURITY_AUDIT_LOG,within retention,RETAIN\nr5,BACKUP_ARCHIVE,Restore access can be blocked,RETAIN\n" +
        "r6,CACHE_ENTRY,Always,DELETE\n"));

    [Fact]
    public void EachActionMapsToTheDocumentedOperations()
    {
        using var d = new TempDir(); var e = Engine(d);
        var v = e.Judge("SUPPORT_MESSAGE", "EXPOSED")!;
        Assert.Equal((true, "REPORT_EXPOSED_PII", "REDACT_RECORD", "VERIFY_RECORD_REDACTED"), (v.Violation, v.Treatment.Finding, v.Treatment.Fix, v.Treatment.Verify));
        v = e.Judge("ANALYTICS_PROFILE", "EXPOSED")!;
        Assert.Equal(("ANONYMIZE_RECORD", "VERIFY_RECORD_ANONYMIZED"), (v.Treatment.Fix, v.Treatment.Verify));
        v = e.Judge("BACKUP_ARCHIVE", "RESTORABLE")!;                     // RETAIN + "restore can be blocked"
        Assert.Equal((true, "REPORT_RESTORE_RISK", "BLOCK_RESTORE"), (v.Violation, v.Treatment.Finding, v.Treatment.Fix));
        Assert.False(e.Judge("BACKUP_ARCHIVE", "BLOCKED")!.Violation);
        v = e.Judge("DATA_FLOW", "ACTIVE")!;                              // not in the CSV: fixed by the problem statement
        Assert.Equal((true, "DISABLE_DATA_FLOW"), (v.Violation, v.Treatment.Fix));
    }

    [Fact]
    public void RetainedRecordsNeverViolateAndAreVerifiedPresent()
    {
        using var d = new TempDir();
        var v = Engine(d).Judge("SECURITY_AUDIT_LOG", "PRESENT")!;
        Assert.Equal((false, true, "VERIFY_RECORD_PRESENT"), (v.Violation, v.EmitVerification, v.Treatment.Verify));
    }

    [Fact]
    public void OnlyTheAccountIsVerifiedWhenAlreadyAbsent()
    {
        using var d = new TempDir(); var e = Engine(d);
        Assert.True(e.Judge("USER_PROFILE", "ABSENT")!.EmitVerification);
        Assert.False(e.Judge("CACHE_ENTRY", "ABSENT")!.EmitVerification);
    }

    [Fact]
    public void ARuleRowChangesBehaviourAndAnUnknownCategoryFailsLoudly()
    {
        using var d = new TempDir(); var e = Engine(d);
        Assert.Equal("DELETE_RECORD", e.Judge("CACHE_ENTRY", "PRESENT")!.Treatment.Fix);          // table-driven: works for any DELETE row
        Assert.Throws<KeyNotFoundException>(() => e.Judge("UNKNOWN_CATEGORY", "PRESENT"));
    }

    [Fact]
    public void UnknownStateSaysNothing()
    {
        using var d = new TempDir();
        Assert.Null(Engine(d).Judge("USER_PROFILE", "SOMETHING_ELSE"));
    }

    [Fact]
    public void AnInventedRequiredActionStopsTheRun()
    {
        using var d = new TempDir();
        Assert.Throws<DatasetException>(() => new PolicyEngine(d.Write("bad.csv", "rule_id,data_category,condition,required_action\nr1,USER_PROFILE,Always,SHRED\n")));
    }
}

public class IdentityTests
{
    private static IdentityResolver Build(TempDir d, string[] identities, string[] audit)
    {
        d.Write("user_identities.csv", "user_id,name,email,phone,email_hash,updated_at\n" + string.Join("\n", identities) + "\n");
        d.Write("user_audit_log.csv", "audit_id,user_id,event_time,field_name,old_value,new_value\n" + string.Join("\n", audit) + "\n");
        return new IdentityResolver(d.Path);
    }

    [Fact]
    public void HistoricalEmailResolvesAndHashesAreSha256OfLowercase()
    {
        using var d = new TempDir();
        var r = Build(d, new[] { $"user_1,A,new@x.com,+1 555 0100 123,{IdentityResolver.EmailHash("new@x.com")},2026-01-01T00:00:00Z" }, new[] { "a1,user_1,2025-06-01T00:00:00Z,email,Old@X.com,new@x.com" });
        var p = Assert.Single(r.Resolve("  OLD@x.com "));
        Assert.Equal("user_1", p.UserId);
        Assert.Contains(IdentityResolver.EmailHash("old@x.com"), p.Hashes);
        Assert.Equal(IdentityResolver.EmailHash("old@x.com"), IdentityResolver.EmailHash(" Old@X.com "));
        Assert.Contains("user_1", r.UsersForToken("user_1"));
        Assert.Empty(r.Resolve("nobody@x.com"));
    }

    [Fact]
    public void ReassignedEmailResolvesToTheOwnerAtRequestTime()
    {
        using var d = new TempDir();
        var r = Build(d, new[]
        {
            $"user_1,A,a2@x.com,+1 555 0100 123,{IdentityResolver.EmailHash("a2@x.com")},2026-01-01T00:00:00Z",
            $"user_2,B,shared@x.com,+1 555 0100 456,{IdentityResolver.EmailHash("shared@x.com")},2026-03-01T00:00:00Z",
        }, new[] { "a1,user_1,2026-02-01T00:00:00Z,email,shared@x.com,a2@x.com" });
        Assert.Equal(new[] { "user_1" }, r.Resolve("shared@x.com", Compat.ParseTs("2026-01-15T00:00:00Z")).Select(p => p.UserId));
        Assert.Equal(new[] { "user_2" }, r.Resolve("shared@x.com", Compat.ParseTs("2026-04-01T00:00:00Z")).Select(p => p.UserId));
        Assert.Equal(2, r.Resolve("shared@x.com").Count);                 // no timestamp: both candidates are returned
    }
}

/// <summary>Guard against silent regressions on the 32 labeled samples (thresholds sit just below the measured numbers).</summary>
public class SampleRegressionGuard
{
    [DatasetFact]
    public void Samples()
    {
        var gold = Scorer.ReadRows(Path.Combine(Repo.Dataset, "sample_output.csv"));
        var a = new CaseAuditor(Repo.Dataset);
        var pred = new Dictionary<string, Scorer.Row>();
        foreach (var cid in gold.Keys)
        {
            var r = a.AuditCase(cid);
            pred[cid] = new Scorer.Row(r.Status, r.Steps.Select(s => new Scorer.RawStep(s.ToJson())).ToList());
        }
        var m = Scorer.Score(gold, pred, gold.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
        Assert.Equal(32, m.StatusCorrect);
        Assert.Equal(1.0, m.StepStrict.P);                  // precision: never a wrong step on the labeled samples
        Assert.True(m.StepStrict.R >= 0.975);               // recall (measured 0.984)
        Assert.DoesNotContain(m.Cases, c => c.OrderOnly);
    }
}

/// <summary>Decision D3: evidence captured after audit_at may only tie a resource to a user. It must never create or change a status, a finding or a remediation - only end-state verification steps.</summary>
public class LaterEvidenceInvariant
{
    [DatasetFact]
    public void LaterLinkingOnlyAddsVerificationSteps()
    {
        var ids = Dataset.LoadRequests(Path.Combine(Repo.Dataset, "deletion_requests.csv")).Concat(Dataset.LoadRequests(Path.Combine(Repo.Dataset, "sample_deletion_requests.csv"))).Select(r => r.CaseId).ToList();
        var with = new CaseAuditor(Repo.Dataset, new AuditConfig());
        var without = new CaseAuditor(Repo.Dataset, new AuditConfig { AllowLaterLinking = false });
        static List<(string, string, string, string)> Keep(CaseResult r) => r.Steps.Where(s => s.Phase != "VERIFICATION").Select(s => (s.Phase, s.Operation, s.SystemId, s.ResourceId)).ToList();
        foreach (var cid in ids)
        {
            var a = with.AuditCase(cid); var b = without.AuditCase(cid);
            Assert.True(a.Status == b.Status, cid);
            Assert.True(Keep(a).SequenceEqual(Keep(b)), cid);
        }
    }
}

/// <summary>Regression: the CLI once enabled the cohort filter by default while the library config did not, so the file we would have submitted differed from the numbers we reported.
/// The CLI with no flags must give exactly what <c>new CaseAuditor(dataset)</c> gives.</summary>
public class CliMatchesLibrary
{
    [DatasetFact]
    public void DefaultCliEqualsDefaultLibrary()
    {
        using var d = new TempDir();
        var outPath = d.File("pred.csv");
        var (code, _, _) = Run.Cli("--requests", "deletion_requests.csv", "--out", outPath, "--images", "off");
        Assert.Equal(0, code);
        var got = Csv.ReadRows(outPath).ToDictionary(r => r.G("case_id"), r => (r.G("deletion_status"), r.G("steps_json")));
        var a = new CaseAuditor(Repo.Dataset);
        foreach (var r in Dataset.LoadRequests(Path.Combine(Repo.Dataset, "deletion_requests.csv")))
        {
            var res = a.AuditCase(r.CaseId);
            Assert.True(got[r.CaseId].Item1 == res.Status, r.CaseId);
            Assert.True(got[r.CaseId].Item2 == OutputIo.DumpsSteps(res.Steps), r.CaseId);
        }
    }
}
