"""The synthetic cases in evaluation/extra_cases.md: the pipeline, the independent oracle and the hand-written expectations must agree,
and the Markdown must be what the generator produces (so reviewers read exactly what is tested)."""
import csv
import unittest

from tests._common import *  # noqa: F401,F403
import extra_cases as xc


class ExtraCases(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cases, cls.rules = xc.parse()

    def test_markdown_is_what_the_generator_writes(self):
        self.assertEqual(xc.MD.read_text(encoding="utf-8"), xc.render(xc.definitions()))

    def test_every_case_passes(self):
        results, _ = xc.run_all(self.cases, self.rules)
        self.assertGreaterEqual(len(results), 20)
        self.assertEqual([r["id"] for r in results if not r["ok"]], [])

    def test_hand_written_expectations_match_the_oracle(self):
        self.assertEqual([p for c in self.cases for p in xc.check_definition(c)], [])

    def test_all_uncovered_categories_are_exercised(self):
        cats = {r["category"] for c in self.cases for r in c["resources"]}
        for need in ("CACHE_ENTRY", "EMAIL_DELIVERY_LOG", "PRODUCT_TELEMETRY", "MARKETING_PROFILE", "LEGAL_HOLD_RECORD", "SECONDARY_IDENTIFIER", "SUPPORT_ATTACHMENT"):
            self.assertIn(need, cats)

    def test_at_least_three_non_deletion_or_edge_cases_with_expected_outputs(self):
        self.assertGreaterEqual(sum(1 for c in self.cases if not c["deletion_request"]), 3)

    @needs_dataset
    def test_embedded_rules_are_the_dataset_rules(self):
        with (DATASET / "deletion_rules.csv").open(newline="", encoding="utf-8") as fh:
            real = list(csv.DictReader(fh))
        self.assertEqual(list(csv.DictReader(self.rules.splitlines())), real)

    def test_a_broken_expectation_is_detected(self):
        case = next(c for c in self.cases if c["id"] == "XC-01")
        broken = {**case, "expected": {"status": "COMPLETE", "steps": case["expected"]["steps"]}}
        results, _ = xc.run_all([broken], self.rules)
        self.assertFalse(results[0]["ok"])

    def test_pipeline_is_driven_by_the_rule_table(self):
        """Change rule_04 from DELETE to RETAIN: the cache copy is now a required retained record, for oracle and pipeline alike."""
        case = next(c for c in self.cases if c["id"] == "XC-01")
        rules = self.rules.replace("rule_04,CACHE_ENTRY,Always,DELETE", "rule_04,CACHE_ENTRY,Always,RETAIN")
        status, steps = xc.run_pipeline(case, rules)
        o_status, o_steps = xc.oracle(case, rules)
        self.assertEqual((status, xc.compact(steps)), (o_status, o_steps))
        self.assertEqual(status, "COMPLETE")
        self.assertIn(["VERIFICATION", "VERIFY_RECORD_PRESENT", "cache_service", "cache_101", "evidence_x1"], o_steps)


if __name__ == "__main__":
    unittest.main()
