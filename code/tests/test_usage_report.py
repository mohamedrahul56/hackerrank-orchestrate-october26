"""usage_report.summarise: numbers come from the log, prices only from a complete user-supplied entry."""
import unittest

from tests._common import *  # noqa: F401,F403
import usage_report as ur
from auditor import prices

LIVE = lambda m, i, o, kind="vision": {"provider": "anthropic", "model": m, "kind": kind, "cache_hit": False, "calls": 1, "input_tokens": i, "output_tokens": o}  # noqa: E731


class UsageReportTests(unittest.TestCase):
    def test_without_prices_the_cost_line_says_so_and_tokens_are_exact(self):
        rows = [LIVE("claude-haiku-5-5", 1000, 100), LIVE("claude-haiku-5-5", 3000, 300), LIVE("claude-sonnet-5-5", 500, 50)]
        text = ur.summarise(rows, 10, {}, ["no price file at x"])
        self.assertIn("cost not computed - prices not supplied", text)
        self.assertIn("| anthropic | claude-haiku-5-5 | 2 | 2 | 4000 | 400 | 4400 | cost not computed - prices not supplied |", text)
        self.assertIn("total tokens: **4950**; average per request: **495.0**", text)
        self.assertNotIn("$", text)

    def test_with_complete_prices_cost_is_computed_and_source_is_cited(self):
        p, _ = prices.parse({"claude-haiku-5-5": {"input_per_mtok": 1, "output_per_mtok": 5, "source_url": "https://example.test/p", "date": "2026-10-25"}})
        text = ur.summarise([LIVE("claude-haiku-5-5", 1_000_000, 200_000)], 4, p, [])
        self.assertIn("$2.0000", text)
        self.assertIn("https://example.test/p", text)
        self.assertIn("$0.500000", text)

    def test_cache_hits_are_flagged_as_not_cold(self):
        text = ur.summarise([LIVE("claude-haiku-5-5", 1, 1), {"provider": "anthropic", "model": "claude-haiku-5-5", "cache_hit": True}], 2, {}, [])
        self.assertIn("Not a cold-cache run", text)

    def test_empty_log_reports_zero_calls(self):
        text = ur.summarise([], 518, {}, [])
        self.assertIn("No model calls were made", text)
        self.assertIn("average per request: **0.0**", text)


if __name__ == "__main__":
    unittest.main()
