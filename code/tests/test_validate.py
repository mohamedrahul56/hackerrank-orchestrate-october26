import copy
import json
import unittest
from collections import Counter

from tests._common import *  # noqa: F401,F403
from auditor.dataset import load_dataset
from auditor.schema import canonicalize, make_step
from auditor.validate import validate_case, validate_output_file, validate_rows
from score import read_rows


def codes(issues, severity="error"):
    return {i.code for i in issues if i.severity == severity}


def incomplete_steps():
    return canonicalize([
        make_step("FINDING", "REPORT_REMAINING_RECORD", "search_service", "search_1", "evidence_01"),
        make_step("REMEDIATION", "DELETE_RECORD", "search_service", "search_1"),
        make_step("VERIFICATION", "VERIFY_RECORD_ABSENT", "account_service", "account_1"),
        make_step("VERIFICATION", "VERIFY_RECORD_ABSENT", "search_service", "search_1"),
    ])


class SyntheticRowTests(unittest.TestCase):
    def test_good_incomplete_row_is_clean(self):
        self.assertEqual(validate_case("c", "INCOMPLETE", incomplete_steps()), [])

    def test_na_must_be_empty(self):
        self.assertEqual(validate_case("c", "NOT_APPLICABLE", []), [])
        self.assertIn("NA_NOT_EMPTY", codes(validate_case("c", "NOT_APPLICABLE", incomplete_steps())))

    def test_bad_status_and_empty_steps(self):
        self.assertIn("BAD_STATUS", codes(validate_case("c", "DONE", [])))
        self.assertIn("EMPTY_STEPS", codes(validate_case("c", "COMPLETE", [])))

    def test_order_and_step_no(self):
        s = incomplete_steps()
        self.assertIn("ORDER", codes(validate_case("c", "INCOMPLETE", list(reversed(s)))))
        t = copy.deepcopy(s); t[2]["step_no"] = 9
        self.assertIn("STEP_NO", codes(validate_case("c", "INCOMPLETE", t)))

    def test_triple_types_and_nulls(self):
        s = incomplete_steps(); s[0]["state"] = "ABSENT"
        self.assertIn("BAD_TRIPLE", codes(validate_case("c", "INCOMPLETE", s)))
        s = incomplete_steps(); s[1]["evidence_id"] = None
        self.assertIn("FIELD_TYPE", codes(validate_case("c", "INCOMPLETE", s)))
        s = incomplete_steps(); del s[0]["state"]
        self.assertIn("STEP_FIELDS", codes(validate_case("c", "INCOMPLETE", s)))

    def test_duplicates(self):
        s = incomplete_steps(); s.append(dict(s[-1])); s[-1]["step_no"] = 5
        self.assertIn("DUPLICATE", codes(validate_case("c", "INCOMPLETE", s)))

    def test_complete_may_only_verify_and_must_cite(self):
        steps = canonicalize([make_step("VERIFICATION", "VERIFY_RECORD_ABSENT", "account_service", "account_1", "evidence_01")])
        self.assertEqual(validate_case("c", "COMPLETE", steps), [])
        steps[0]["evidence_id"] = ""
        self.assertIn("EVIDENCE_MISSING", codes(validate_case("c", "COMPLETE", steps)))
        self.assertIn("COMPLETE_PHASES", codes(validate_case("c", "COMPLETE", incomplete_steps())))

    def test_future_work_must_not_cite(self):
        s = incomplete_steps(); s[1]["evidence_id"] = "evidence_01"
        self.assertIn("EVIDENCE_ON_PROPOSAL", codes(validate_case("c", "INCOMPLETE", s)))
        s = incomplete_steps(); s[0]["evidence_id"] = ""
        self.assertIn("EVIDENCE_MISSING", codes(validate_case("c", "INCOMPLETE", s)))

    def test_pairing(self):
        s = [x for x in incomplete_steps() if x["phase"] != "REMEDIATION"]
        self.assertIn("FINDING_NO_REMEDIATION", codes(validate_case("c", "INCOMPLETE", canonicalize(s))))
        s = [x for x in incomplete_steps() if not (x["phase"] == "VERIFICATION" and x["system_id"] == "search_service")]
        self.assertIn("REMEDIATION_NO_VERIFICATION", codes(validate_case("c", "INCOMPLETE", canonicalize(s))))
        s = [x for x in incomplete_steps() if x["phase"] != "FINDING"]
        c = codes(validate_case("c", "INCOMPLETE", canonicalize(s)))
        self.assertIn("REMEDIATION_NO_FINDING", c)
        self.assertIn("INCOMPLETE_MISSING_PHASE", c)

    def test_missing_account_check_is_only_a_warning(self):
        s = canonicalize([x for x in incomplete_steps() if x["system_id"] != "account_service"])
        issues = validate_case("c", "INCOMPLETE", s)
        self.assertEqual(codes(issues), set())
        self.assertEqual(codes(issues, "warning"), {"NO_ACCOUNT_ABSENT_CHECK"})


@needs_dataset
class GoldAndEvidenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.ds = load_dataset(DATASET, "sample_deletion_requests.csv")
        cls.gold = read_rows(DATASET / "sample_output.csv")

    def test_gold_passes_validator_with_no_errors_or_warnings(self):
        self.assertEqual([str(i) for i in validate_output_file(DATASET / "sample_output.csv", self.ds)], [])

    def test_evidence_must_exist_and_precede_audit_at(self):
        row = self.gold["case_21"]
        steps = copy.deepcopy(row.steps)
        audit = {r.case_id: r.audit_at for r in self.ds.requests}["case_21"]
        steps[0]["evidence_id"] = "evidence_999"
        self.assertIn("EVIDENCE_UNKNOWN", codes(validate_case("case_21", "COMPLETE", steps, audit, self.ds)))
        steps[0]["evidence_id"] = "evidence_50"          # security ledger captured 2026-10-01, after case_21's audit_at
        self.assertIn("EVIDENCE_AFTER_AUDIT", codes(validate_case("case_21", "COMPLETE", steps, audit, self.ds)))
        early = min(self.ds.activity_time, key=self.ds.activity_time.get)   # earliest activity id (pre-dates audit_at)
        steps[0]["evidence_id"] = early                  # activity ids are legal citations when they pre-date audit_at
        self.assertEqual(codes(validate_case("case_21", "COMPLETE", steps, audit, self.ds)), set())

    def test_row_level_checks(self):
        rows = [(c, r.status, r.steps) for c, r in self.gold.items()]
        self.assertEqual(validate_rows(rows, self.ds), [])
        self.assertIn("CASE_SET", codes(validate_rows(rows[:-1], self.ds)))
        self.assertIn("DUPLICATE_CASE", codes(validate_rows(rows + rows[:1], self.ds)))
        self.assertIn("CASE_ORDER", codes(validate_rows(list(reversed(rows)), self.ds)))

    def test_sample_facts_about_evidence_ids(self):
        """Regression test for facts confirmed from the 32 labeled samples (see NOTES.md):
        INCOMPLETE rows: FINDING always cites evidence; REMEDIATION and *every* VERIFICATION step -
        including checks on already-compliant resources such as the account or retained audit record -
        has evidence_id == "". COMPLETE rows: every step cites evidence."""
        tally = Counter()
        for r in self.gold.values():
            for s in r.steps:
                tally[(r.status, s["phase"], bool(s["evidence_id"]))] += 1
        self.assertEqual({k for k in tally if k[0] == "INCOMPLETE" and k[1] == "FINDING" and not k[2]}, set())
        self.assertEqual({k for k in tally if k[0] == "INCOMPLETE" and k[1] != "FINDING" and k[2]}, set())
        self.assertEqual({k for k in tally if k[0] == "COMPLETE" and not k[2]}, set())
        self.assertGreater(tally[("INCOMPLETE", "VERIFICATION", False)], 0)

    def test_every_sample_has_account_absent_check(self):
        for cid, r in self.gold.items():
            self.assertTrue(any(s["operation"] == "VERIFY_RECORD_ABSENT" and s["system_id"] == "account_service" for s in r.steps), cid)


if __name__ == "__main__":
    unittest.main()
