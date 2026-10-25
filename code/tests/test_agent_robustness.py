"""Failure behaviour of the agent loop that the first test matrix did not cover: damaged images, a rejected key across several screenshots, the token budget, spend that is logged
even when the loop dies, size limits on a submission. Fake client only: no key, no network, 0 calls, 0 tokens."""
import unittest
from unittest import mock

from tests._common import *  # noqa: F401,F403
from tests.test_agent_loop import ZOOM, APITimeoutError, Base, Fake, msg, record, submit, use
from tests.test_agent_tools import make_png
from auditor.agent import tools
from auditor.agent.loop import AgentFailure, AgentImageReader, AgentLimits
from auditor.llm import LLM, LLMUnavailable
from tests.test_llm_policy import StatusError


class DamagedImages(Base):
    def test_a_truncated_png_is_a_tool_error_and_the_loop_goes_on(self):
        self.png.write_bytes(self.png.read_bytes()[:300])
        rd = self.reader([msg(use("zoom_region", ZOOM)), submit()])
        self.assertEqual(self.read(rd)["records"][0]["resource_id"], "account_5762")
        result = self.fake.calls[1]["messages"][2]["content"][0]
        self.assertTrue(result["is_error"])
        self.assertIn("cannot be cropped", result["content"])

    def test_a_decompression_bomb_is_a_tool_error_not_a_crash(self):
        import PIL.Image
        rd = self.reader([msg(use("zoom_region", ZOOM)), submit()])
        with mock.patch.object(PIL.Image, "MAX_IMAGE_PIXELS", 100):
            self.read(rd)
        result = self.fake.calls[1]["messages"][2]["content"][0]
        self.assertTrue(result["is_error"])
        self.assertIn("DecompressionBombError", result["content"])

    def test_a_file_that_is_not_a_png_ends_as_not_read(self):
        self.png.write_bytes(b"not a png at all, just some text")
        rd = self.reader([submit()])
        with self.assertRaises(AgentFailure):
            self.read(rd)
        self.assertEqual(self.fake.calls, [])                   # no request is wasted on a file the loop cannot even measure


class AFailedRunStopsCheaply(Base):
    def three_images(self, script, **kw):
        paths = [make_png(self.dir / f"s{i}.png", 300 + 10 * i, 200) for i in range(3)]
        self.fake = Fake(script)
        self.llm = LLM(cache_dir=self.dir / "cache", usage_path=self.dir / "usage.jsonl", client=self.fake, sleep=self.sleeps.append, clock=lambda: 100.0, **kw)
        return AgentImageReader(self.llm, {p: f"evidence_{i}" for i, p in enumerate(paths)}, AgentLimits(), clock=lambda: self.now[0]), paths

    def test_rejected_credentials_cost_one_request_for_the_whole_run(self):
        rd, paths = self.three_images([StatusError(401)])
        for p in paths:
            with self.assertRaises(LLMUnavailable):
                rd(p, "", "", {}, "")
        self.assertEqual(len(self.fake.calls), 1)

    def test_the_token_budget_leaves_the_remaining_screenshots_not_read(self):
        rd, paths = self.three_images([submit()], max_run_tokens=100)
        rd(paths[0], "", "", {}, "")                            # 120 tokens: over the budget of 100
        for p in paths[1:]:
            with self.assertRaisesRegex(LLMUnavailable, "token budget"):
                rd(p, "", "", {}, "")
        self.assertEqual(len(self.fake.calls), 1)
        self.assertEqual(sum(r.get("input_tokens", 0) for r in self.usage()), 100)

    def test_tokens_spent_before_the_model_became_unavailable_are_still_logged(self):
        rd = self.reader([msg(use("zoom_region", ZOOM)), *[APITimeoutError("t")] * 4])
        with self.assertRaises(LLMUnavailable):
            self.read(rd)
        row = [r for r in self.usage() if r.get("kind") == "agent_image"][0]
        self.assertEqual((row["calls"], row["input_tokens"], row["output_tokens"], row["zooms"]), (1, 100, 20, 1))
        self.assertIn("model unavailable during the loop", row["error"])

    def test_agent_failure_carries_the_trace_as_a_plain_attribute(self):
        self.assertEqual(AgentFailure("x", {"api_calls": 2}).trace, {"api_calls": 2})
        self.assertEqual(AgentFailure("x").trace, {})


class SizeLimits(Base):
    def bad_then_good(self, bad):
        rd = self.reader([submit(bad), submit()])
        self.read(rd)
        return self.fake.calls[1]["messages"][2]["content"][0]

    def test_a_list_longer_than_the_limit_is_sent_back_once(self):
        result = self.bad_then_good(record(emails=[f"a{i}@example.test" for i in range(21)]))
        self.assertTrue(result["is_error"])
        self.assertIn("more than 20 items", result["content"])

    def test_a_very_long_string_is_sent_back_once(self):
        result = self.bad_then_good(record(aliases=["x" * 500]))
        self.assertIn("longer than", result["content"])

    def test_a_long_screen_title_is_not_an_error_the_gate_cuts_it(self):
        d = {"screen": "t" * 600, "records": [record()]}
        rd = self.reader([msg(use("submit_reading", d))])
        self.read(rd)
        self.assertEqual(len(self.fake.calls), 1)

    def test_the_limits_are_in_the_schema_the_model_receives(self):
        records = tools.SUBMIT_SCHEMA["properties"]["records"]
        self.assertEqual(records["maxItems"], 100)
        self.assertEqual(records["items"]["properties"]["emails"]["maxItems"], 20)
        self.assertEqual(records["items"]["properties"]["aliases"]["items"]["maxLength"], tools.MAX_TEXT)
        self.assertNotIn("maxLength", tools.SUBMIT_SCHEMA["properties"]["screen"])


if __name__ == "__main__":
    unittest.main()
