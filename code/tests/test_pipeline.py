"""Unit tests for the pipeline pieces, on synthetic data (plus one regression guard on the labeled samples)."""
import csv
import tempfile
import unittest
from datetime import timedelta
from pathlib import Path

from tests._common import DATASET, ROOT, needs_dataset
from auditor import discover as dsc
from auditor.dataset import parse_ts
from auditor.evidence.logparse import parse_line
from auditor.evidence.records import Obs
from auditor.identity import IdentityResolver, email_hash
from auditor.policy import PolicyEngine
from auditor.state import resolve

T = lambda s: parse_ts(s)  # noqa: E731


class LogParseTests(unittest.TestCase):
    def test_seven_wrappers_give_the_same_event(self):
        lines = [
            '2026-09-15T11:56:47Z level=INFO service=account_service trace_id=tr-1 resource_id=account_9 event=profile_lookup_completed rows=0 http_status=404',
            '2026-09-15T11:56:47Z INFO service=downstream_delivery_service event=record_lookup seq=1 system="account_service" resource_id="account_9" record_type="USER_PROFILE" result="NOT_FOUND" subject_reference="a@b.com"',
            '2026-09-15T11:56:47Z [INFO] [account_service] [tr-1] profile_lookup_completed: resource_id="account_9" rows="0" http_status="404"',
            '2026-09-15T11:56:47Z | INFO | account_service | tr-1 | profile_lookup_completed | resource_id="account_9" rows="0" http_status="404"',
            '2026-09-15T11:56:47Z requestId=tr-1 level=INFO function=profile_service message="resource_id=\\"account_9\\" event=\\"profile_lookup_completed\\" rows=\\"0\\" http_status=\\"404\\""',
            '2026-09-15T11:56:47Z {"severity":"INFO","service":"account_service","trace_id":"tr-1","resource_id":"account_9","event":"profile_lookup_completed","rows":"0","http_status":"404"}',
            '2026-09-15T11:56:47Z <134>1 node-3 account_service tr-1 - - level=INFO resource_id="account_9" event="profile_lookup_completed" rows="0" http_status="404"',
        ]
        for l in lines:
            e = parse_line(l)
            self.assertIsNotNone(e, l)
            self.assertEqual(e.fields.get("resource_id"), "account_9", l)
        completed = [parse_line(l) for l in lines if "profile_lookup_completed" in l]
        self.assertTrue(all(e.event == "profile_lookup_completed" and e.fields["rows"] == "0" for e in completed))
        self.assertEqual([e.trace for e in completed if e.trace], ["tr-1"] * len([e for e in completed if e.trace]))

    def test_literal_null_is_a_missing_value(self):
        e = parse_line('2026-09-15T10:00:00Z level=INFO service=analytics_platform trace_id=t event=analytics_profile_read_completed resource_id=analytics_1 email=null phone=None customer_reference=null')
        self.assertEqual((e.fields["email"], e.fields["phone"], e.fields["customer_reference"]), ("", "", ""))

    def test_garbage_line_is_skipped_not_fatal(self):
        self.assertIsNone(parse_line("this is not a log line"))
        self.assertIsNone(parse_line("2026-09-15T10:00:00Z {not json"))


def obs(file_t, state, source="sql:t", t=None, fid="f", res="account_1", cat="USER_PROFILE"):
    ft = T(file_t)
    return Obs(fid, ft, T(t) if t else ft, "account_service", res, cat, state, frozenset(), source)


class StateTests(unittest.TestCase):
    t0, a = T("2026-09-01T00:00:00Z"), T("2026-09-10T00:00:00Z")

    def test_window_excludes_before_request_and_after_audit(self):
        old, inside, later = obs("2026-08-01T00:00:00Z", "PRESENT", fid="old"), obs("2026-09-05T00:00:00Z", "ABSENT", fid="in"), obs("2026-09-12T00:00:00Z", "PRESENT", fid="later")
        st = resolve([old, inside, later], self.t0, self.a)
        self.assertEqual((st.file_id, st.state), ("in", "ABSENT"))
        self.assertIsNone(resolve([old, later], self.t0, self.a))

    def test_audit_boundary_is_inclusive(self):
        self.assertIsNotNone(resolve([obs("2026-09-10T00:00:00Z", "PRESENT")], self.t0, self.a))

    def test_latest_file_wins_and_drift_inside_one_file_counts(self):
        first = obs("2026-09-02T00:00:00Z", "PRESENT", fid="f1")
        second_primary = obs("2026-09-06T00:00:00Z", "ABSENT", fid="f2", source="sql:primary")
        second_replica = obs("2026-09-06T00:00:00Z", "PRESENT", fid="f2", source="sql:replica")   # replication drift
        self.assertEqual(resolve([first, second_primary], self.t0, self.a).state, "ABSENT")
        self.assertEqual(resolve([first, second_primary, second_replica], self.t0, self.a).state, "PRESENT")

    def test_in_a_log_the_last_event_wins(self):
        a = obs("2026-09-06T00:00:00Z", "PRESENT", source="log:x", t="2026-09-05T10:00:00Z")
        b = obs("2026-09-06T00:00:00Z", "ABSENT", source="log:x", t="2026-09-05T11:00:00Z")
        self.assertEqual(resolve([a, b], self.t0, self.a).state, "ABSENT")


class DiscoveryTests(unittest.TestCase):
    R = T("2026-09-02T12:00:00Z")

    def ev(self, i, res, minutes, kind="DELETE_RECORD", target="account_service", result="SUCCESS"):
        return dsc.Event(f"a{i}", self.R + timedelta(minutes=minutes), kind, "deletion_worker", target, res, result)

    def case(self, cid, status="COMPLETE", r=None):
        r = r or self.R
        return dsc.CaseKey(cid, frozenset({"u_" + cid}), r, r - timedelta(days=2), r + timedelta(days=10), status)

    def test_chain_gaps_are_sums_of_390_and_449(self):
        self.assertTrue(dsc.chain_ok(449, 1) and dsc.chain_ok(390, 1) and dsc.chain_ok(839, 2) and dsc.chain_ok(1288, 3))
        self.assertFalse(dsc.chain_ok(600, 1))

    def test_elimination_assigns_the_only_remaining_account(self):
        events = [self.ev(1, "account_A", -60), self.ev(2, "account_B", -50)]
        out = dsc.assign_accounts([self.case("c1"), self.case("c2")], events, {"c1": {"account_A"}}, {"account_A": {"u_c1"}})
        self.assertEqual((out["c2"].resource, out["c2"].how), ("account_B", "eliminated"))

    def test_two_unknown_cases_with_two_candidates_stay_ambiguous(self):
        events = [self.ev(1, "account_A", -60), self.ev(2, "account_B", -50)]
        out = dsc.assign_accounts([self.case("c1"), self.case("c2")], events, {}, {})
        self.assertEqual([out[c].resource for c in ("c1", "c2")], [None, None])
        self.assertTrue(all(out[c].how == "ambiguous" for c in ("c1", "c2")))

    def test_reported_status_matches_the_event_result_to_break_a_tie(self):
        events = [self.ev(1, "account_A", -60, result="SUCCESS"), self.ev(2, "account_B", -50, result="FAILED")]
        out = dsc.assign_accounts([self.case("c1", "COMPLETE"), self.case("c2", "NOT_STARTED")], events, {}, {})
        self.assertEqual((out["c1"].resource, out["c2"].resource), ("account_A", "account_B"))

    def test_cohort_snapshot_narrows_candidates(self):
        events = [self.ev(1, "account_A", -60), self.ev(2, "account_B", -50)]
        out = dsc.assign_accounts([self.case("c1")], events, {}, {}, cohort={"c1": {"account_B"}})
        self.assertEqual(out["c1"].resource, "account_B")

    def test_late_linked_own_account_is_known_and_not_offered_to_a_neighbour(self):
        """Regression: c1's own account is linked only by a later snapshot (no observation in its window). It must still count as
        c1's, otherwise elimination hands the only other candidate (c2's real account) to c1."""
        events = [self.ev(1, "account_A", -60, result="FAILED"), self.ev(2, "account_B", -50)]
        c1 = self.case("c1", "NOT_STARTED")
        known = dsc.known_accounts({"account_A"}, set(), {"account_A": [events[0]], "account_B": [events[1]]}, c1.reported_at)
        self.assertEqual(known, {"account_A"})
        out = dsc.assign_accounts([c1, self.case("c2")], events, {"c1": known}, {"account_A": {"u_c1"}})
        self.assertEqual((out["c1"].resource, out["c2"].resource), ("account_A", "account_B"))

    def test_linked_account_without_a_delete_event_in_the_window_is_not_known(self):
        ev = self.ev(1, "account_A", -300)                                # outside the 90 minute window
        self.assertEqual(dsc.known_accounts({"account_A"}, set(), {"account_A": [ev]}, self.R), set())
        self.assertEqual(dsc.known_accounts({"account_A"}, {"account_A"}, {}, self.R), {"account_A"})   # observed in window

    def test_phase_model_prefers_the_candidate_whose_seconds_phase_fits(self):
        """Learned from explicit pairs: a support redaction lands 10 s (+-1) after the account delete, whatever the minute."""
        m = dsc.PhaseModel()
        for i in range(12):
            d = self.R + timedelta(minutes=-50, seconds=(i * 7) % 60)
            m.learn(d, [dsc.Event(f"s{i}", d + timedelta(minutes=100 + i, seconds=10), "REDACT_RECORD", "deletion_worker", "support_platform", "support_1", "SUCCESS")])
        own = [dsc.Event("x", self.R + timedelta(minutes=45, seconds=5), "REDACT_RECORD", "deletion_worker", "support_platform", "support_9", "SUCCESS")]
        fits = m.score(own, self.R + timedelta(minutes=-50, seconds=55))[0]       # 5 - 55 = 10 mod 60
        off = m.score(own, self.R + timedelta(minutes=-50, seconds=20))[0]
        self.assertGreater(fits, 1.5)
        self.assertLess(off, 0.0)

    def test_phase_stage_breaks_a_tie_that_elimination_cannot(self):
        events = [self.ev(1, "account_A", -60), self.ev(2, "account_B", -50)]
        events = [dsc.Event(e.activity_id, e.t.replace(second=s), e.event_type, e.source, e.target, e.resource, e.result) for e, s in zip(events, (10, 40))]
        known = [self.case(f"k{i}") for i in range(10)]
        train_events, explicit, own = [], {}, {}
        for i, k in enumerate(known):                     # ten solved cases: support redaction always 20 s after the account delete
            d = dsc.Event(f"d{i}", k.reported_at + timedelta(minutes=-55, seconds=i), "DELETE_RECORD", "deletion_worker", "account_service", f"account_k{i}", "SUCCESS")
            train_events.append(d)
            explicit[k.case_id] = {f"account_k{i}"}
            own[k.case_id] = [dsc.Event(f"r{i}", d.t + timedelta(minutes=90, seconds=20), "REDACT_RECORD", "deletion_worker", "support_platform", f"support_k{i}", "SUCCESS")]
        c1, c2 = self.case("c1"), self.case("c2")
        own["c1"] = [dsc.Event("rc1", events[0].t + timedelta(minutes=95, seconds=20), "REDACT_RECORD", "deletion_worker", "support_platform", "support_c1", "SUCCESS")]
        own["c2"] = [dsc.Event("rc2", events[1].t + timedelta(minutes=95, seconds=20), "REDACT_RECORD", "deletion_worker", "support_platform", "support_c2", "SUCCESS")]
        out = dsc.assign_accounts(known + [c1, c2], events + train_events, explicit, {}, None, own)
        self.assertEqual((out["c1"].resource, out["c1"].how), ("account_A", "phase"))
        self.assertEqual(out["c2"].resource, "account_B")
        out = dsc.assign_accounts(known + [c1, c2], events + train_events, explicit, {}, None, {**own, "c1": [], "c2": []})
        self.assertEqual([out[c].resource for c in ("c1", "c2")], [None, None])        # no events -> still ambiguous, never a coin flip

    def test_narrowing_to_a_neighbours_account_falls_back_to_the_full_candidate_set(self):
        """Regression: the cohort snapshot lists the neighbour's account; once the neighbour takes it, c2 must get its own, not nothing."""
        events = [self.ev(1, "account_A", -60), self.ev(2, "account_B", -50)]
        out = dsc.assign_accounts([self.case("c1"), self.case("c2")], events, {}, {}, cohort={"c1": {"account_A"}, "c2": {"account_A"}})
        self.assertIsNotNone(out["c2"].resource)                                               # not left empty-handed
        self.assertNotEqual(out["c1"].resource, out["c2"].resource)                            # and never two cases on one account

    def test_analytics_phase_picks_the_claimant_whose_offset_fits(self):
        """Chain-resolved pairs teach the offset (anonymization 41 s after the account delete); an ambiguous candidate then goes to
        the claimant that fits, provided no other scored claimant fits too."""
        cases, events, chain, atime = [], [], {}, {}
        for i in range(20):
            c = self.case(f"k{i}")
            cases.append(c)
            d = c.reported_at + timedelta(minutes=-55, seconds=i * 7)         # 7 s apart: no two chains alias
            atime[c.case_id] = d
            a = dsc.Event(f"an{i}", d + timedelta(minutes=26, seconds=41 - 60 * 0), "ANONYMIZE_RECORD", "deletion_worker", "analytics_platform", f"analytics_k{i}", "SUCCESS")
            events.append(a)
            chain[c.case_id] = [(a.t + timedelta(seconds=449), 1)]
        c1, c2 = self.case("c1"), self.case("c2")
        atime["c1"], atime["c2"] = self.R + timedelta(minutes=-55, seconds=3), self.R + timedelta(minutes=-52, seconds=33)
        shared = dsc.Event("sh", atime["c1"] + timedelta(minutes=25, seconds=41), "ANONYMIZE_RECORD", "deletion_worker", "analytics_platform", "analytics_S", "SUCCESS")
        out = dsc.assign_anonymized_analytics(cases + [c1, c2], events + [shared], {"analytics_S"} | {f"analytics_k{i}" for i in range(20)}, chain, None, atime)
        self.assertEqual((out["c1"].resource, out["c1"].how), ("analytics_S", "phase"))
        self.assertIsNone(out["c2"].resource)

    def test_account_linked_to_someone_else_is_never_guessed(self):
        events = [self.ev(1, "account_A", -60)]
        out = dsc.assign_accounts([self.case("c1")], events, {}, {"account_A": {"someone_else"}})
        self.assertIsNone(out["c1"].resource)

    def test_analytics_claimed_by_two_cases_is_not_assigned_and_ambiguity_does_not_leak(self):
        """Regression: an 'ambiguous' answer for one case must not shrink the claimant set of the next."""
        events = [self.ev(1, "analytics_X", -29, "ANONYMIZE_RECORD", "analytics_platform")]
        out = dsc.assign_anonymized_analytics([self.case("c1"), self.case("c2")], events, {"analytics_X"}, {})
        self.assertEqual([out[c].resource for c in ("c1", "c2")], [None, None])

    def test_analytics_unique_claimant_is_assigned(self):
        events = [self.ev(1, "analytics_X", -29, "ANONYMIZE_RECORD", "analytics_platform")]
        out = dsc.assign_anonymized_analytics([self.case("c1"), self.case("c2", r=self.R + timedelta(days=3))], events, {"analytics_X"}, {})
        self.assertEqual(out["c1"].resource, "analytics_X")

    def test_chain_timing_picks_the_candidate_that_fits_the_cases_own_support_event(self):
        events = [self.ev(1, "analytics_X", -29, "ANONYMIZE_RECORD", "analytics_platform"),
                  self.ev(2, "analytics_Y", -36, "ANONYMIZE_RECORD", "analytics_platform")]
        support_time = events[0].t + timedelta(seconds=449)
        out = dsc.assign_anonymized_analytics([self.case("c1"), self.case("c2")], events, {"analytics_X", "analytics_Y"},
                                              {"c1": [(support_time, 1)]})
        self.assertEqual((out["c1"].resource, out["c1"].how), ("analytics_X", "chain"))

    def test_decoy_users_completed_chain_excludes_the_candidate(self):
        events = [self.ev(1, "analytics_X", -29, "ANONYMIZE_RECORD", "analytics_platform")]
        foreign = lambda case, e: True                                   # another user's chain follows this event
        out = dsc.assign_anonymized_analytics([self.case("c1")], events, {"analytics_X"}, {}, foreign)
        self.assertIsNone(out["c1"].resource)


class PolicyTests(unittest.TestCase):
    def engine(self):
        d = tempfile.mkdtemp()
        p = Path(d) / "rules.csv"
        p.write_text("rule_id,data_category,condition,required_action\n"
                     "r1,USER_PROFILE,Always,DELETE\nr2,SUPPORT_MESSAGE,shared,REDACT\nr3,ANALYTICS_PROFILE,attributable,ANONYMIZE\n"
                     "r4,SECURITY_AUDIT_LOG,within retention,RETAIN\nr5,BACKUP_ARCHIVE,Restore access can be blocked,RETAIN\n"
                     "r6,CACHE_ENTRY,Always,DELETE\n", encoding="utf-8")
        return PolicyEngine(p)

    def test_each_action_maps_to_the_documented_operations(self):
        e = self.engine()
        v = e.judge("SUPPORT_MESSAGE", "EXPOSED")
        self.assertEqual((v.violation, v.treatment.finding, v.treatment.fix, v.treatment.verify), (True, "REPORT_EXPOSED_PII", "REDACT_RECORD", "VERIFY_RECORD_REDACTED"))
        v = e.judge("ANALYTICS_PROFILE", "EXPOSED")
        self.assertEqual((v.treatment.fix, v.treatment.verify), ("ANONYMIZE_RECORD", "VERIFY_RECORD_ANONYMIZED"))
        v = e.judge("BACKUP_ARCHIVE", "RESTORABLE")                      # RETAIN + "restore can be blocked"
        self.assertEqual((v.violation, v.treatment.finding, v.treatment.fix), (True, "REPORT_RESTORE_RISK", "BLOCK_RESTORE"))
        self.assertFalse(e.judge("BACKUP_ARCHIVE", "BLOCKED").violation)
        v = e.judge("DATA_FLOW", "ACTIVE")                               # not in the CSV: fixed by the problem statement
        self.assertEqual((v.violation, v.treatment.fix), (True, "DISABLE_DATA_FLOW"))

    def test_retained_records_never_violate_and_are_verified_present(self):
        v = self.engine().judge("SECURITY_AUDIT_LOG", "PRESENT")
        self.assertEqual((v.violation, v.emit_verification, v.treatment.verify), (False, True, "VERIFY_RECORD_PRESENT"))

    def test_only_the_account_is_verified_when_already_absent(self):
        e = self.engine()
        self.assertTrue(e.judge("USER_PROFILE", "ABSENT").emit_verification)
        self.assertFalse(e.judge("CACHE_ENTRY", "ABSENT").emit_verification)

    def test_a_rule_row_changes_behaviour_and_an_unknown_category_fails_loudly(self):
        e = self.engine()
        self.assertEqual(e.judge("CACHE_ENTRY", "PRESENT").treatment.fix, "DELETE_RECORD")     # table-driven: works for any DELETE row
        with self.assertRaises(KeyError):
            e.judge("UNKNOWN_CATEGORY", "PRESENT")

    def test_unknown_state_says_nothing(self):
        self.assertIsNone(self.engine().judge("USER_PROFILE", "SOMETHING_ELSE"))


class IdentityTests(unittest.TestCase):
    def build(self, identities, audit):
        d = Path(tempfile.mkdtemp())
        (d / "user_identities.csv").write_text("user_id,name,email,phone,email_hash,updated_at\n" + "\n".join(identities) + "\n", encoding="utf-8")
        (d / "user_audit_log.csv").write_text("audit_id,user_id,event_time,field_name,old_value,new_value\n" + "\n".join(audit) + "\n", encoding="utf-8")
        return IdentityResolver(d)

    def test_historical_email_resolves_and_hashes_are_sha256_of_lowercase(self):
        r = self.build([f"user_1,A,new@x.com,+1 555 0100 123,{email_hash('new@x.com')},2026-01-01T00:00:00Z"],
                       ["a1,user_1,2025-06-01T00:00:00Z,email,Old@X.com,new@x.com"])
        (p,) = r.resolve("  OLD@x.com ")
        self.assertEqual(p.user_id, "user_1")
        self.assertIn(email_hash("old@x.com"), p.hashes)
        self.assertEqual(email_hash(" Old@X.com "), email_hash("old@x.com"))
        self.assertIn("user_1", r.users_for_token("user_1"))
        self.assertEqual(r.resolve("nobody@x.com"), [])

    def test_reassigned_email_resolves_to_the_owner_at_request_time(self):
        r = self.build([f"user_1,A,a2@x.com,+1 555 0100 123,{email_hash('a2@x.com')},2026-01-01T00:00:00Z",
                        f"user_2,B,shared@x.com,+1 555 0100 456,{email_hash('shared@x.com')},2026-03-01T00:00:00Z"],
                       ["a1,user_1,2026-02-01T00:00:00Z,email,shared@x.com,a2@x.com"])
        self.assertEqual([p.user_id for p in r.resolve("shared@x.com", parse_ts("2026-01-15T00:00:00Z"))], ["user_1"])
        self.assertEqual([p.user_id for p in r.resolve("shared@x.com", parse_ts("2026-04-01T00:00:00Z"))], ["user_2"])
        self.assertEqual(len(r.resolve("shared@x.com")), 2)             # no timestamp: both candidates are returned


@needs_dataset
class SampleRegressionGuard(unittest.TestCase):
    """Guard against silent regressions on the 32 labeled samples (thresholds sit just below the measured numbers)."""

    def test_samples(self):
        from auditor.audit import Auditor
        from score import read_rows, score
        gold = read_rows(DATASET / "sample_output.csv")
        a = Auditor(DATASET)
        from score import Row as SRow
        pred = {}
        for cid in gold:
            r = a.audit(cid)
            pred[cid] = SRow(r.status, r.steps)
        m = score(gold, pred, sorted(gold))
        self.assertEqual(m.status_correct, 32)
        self.assertEqual(m.step_strict[0], 1.0)             # precision: never a wrong step on the labeled samples
        self.assertGreaterEqual(m.step_strict[1], 0.975)    # recall (measured 0.984 after the Phase 3 discovery fixes)
        self.assertEqual([c.case_id for c in m.cases if c.order_only], [])


@needs_dataset
class LaterEvidenceInvariant(unittest.TestCase):
    """Decision D3: evidence captured after audit_at may only tie a resource to a user. It must never create or change a status,
    a finding or a remediation - only end-state verification steps."""

    def test_later_linking_only_adds_verification_steps(self):
        from auditor.audit import Auditor, Config
        from auditor.dataset import load_requests
        ids = [r.case_id for r in load_requests(DATASET / "deletion_requests.csv")] + [r.case_id for r in load_requests(DATASET / "sample_deletion_requests.csv")]
        with_, without = Auditor(DATASET, Config()), Auditor(DATASET, Config(allow_later_linking=False))
        for cid in ids:
            a, b = with_.audit(cid), without.audit(cid)
            self.assertEqual(a.status, b.status, cid)
            keep = lambda r: [(s["phase"], s["operation"], s["system_id"], s["resource_id"]) for s in r.steps if s["phase"] != "VERIFICATION"]  # noqa: E731
            self.assertEqual(keep(a), keep(b), cid)


@needs_dataset
class CliMatchesLibrary(unittest.TestCase):
    """Regression: main.py once enabled the cohort filter by default while Config() did not, so the file we would have submitted differed
    from the numbers we reported. The CLI with no flags must give exactly what Auditor(Config()) gives."""

    def test_default_cli_equals_default_library(self):
        import importlib.util
        from auditor.audit import Auditor
        from auditor.dataset import load_requests
        spec = importlib.util.spec_from_file_location("auditor_cli", ROOT / "code" / "main.py")
        cli = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cli)
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "pred.csv"
            self.assertEqual(cli.main(["--requests", "deletion_requests.csv", "--out", str(out), "--images", "off"]), 0)
            with out.open(newline="", encoding="utf-8") as fh:
                got = {r["case_id"]: (r["deletion_status"], r["steps_json"]) for r in csv.DictReader(fh)}
        a = Auditor(DATASET)
        import json
        for r in load_requests(DATASET / "deletion_requests.csv"):          # the main set: that is where the cohort filter changed 18 rows
            res = a.audit(r.case_id)
            self.assertEqual(got[r.case_id][0], res.status, r.case_id)
            self.assertEqual(json.loads(got[r.case_id][1]), res.steps, r.case_id)


if __name__ == "__main__":
    unittest.main()
