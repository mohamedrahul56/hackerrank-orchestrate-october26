"""The request policy of `LLM.send`, with a fake client: which HTTP statuses are retried, what stops a run, the token budget. No key, no network."""
import os
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

from tests._common import *  # noqa: F401,F403
from auditor.llm import CALL_ATTEMPTS, LLM, LLMUnavailable, SchemaError, validate_schema


class StatusError(Exception):
    def __init__(self, status, retry_after=None):
        super().__init__(f"HTTP {status}")
        self.status_code = status
        self.response = SimpleNamespace(headers={"retry-after": str(retry_after)} if retry_after else {})


def ok(inp=100, out=20):
    return SimpleNamespace(content=[], stop_reason="end_turn", usage=SimpleNamespace(input_tokens=inp, output_tokens=out, cache_read_input_tokens=0))


class Fake:
    def __init__(self, script):
        self.script, self.calls = list(script), 0
        self.messages = SimpleNamespace(create=self._create)

    def _create(self, **kw):
        self.calls += 1
        r = self.script.pop(0)
        if isinstance(r, Exception):
            raise r
        return r


class Policy(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)
        self.sleeps = []

    def tearDown(self):
        self.tmp.cleanup()

    def llm(self, script, **kw):
        self.fake = Fake(script)
        return LLM(cache_dir=self.dir / "c", usage_path=self.dir / "u.jsonl", client=self.fake, sleep=self.sleeps.append, clock=lambda: 100.0, **kw)

    def test_http_errors_that_the_caller_caused_are_not_retried(self):
        for status in (400, 401, 403, 404, 413, 422):
            with self.subTest(status=status):
                llm = self.llm([StatusError(status)])
                with self.assertRaises(LLMUnavailable) as cm:
                    llm.send(model="m", messages=[])
                self.assertEqual((self.fake.calls, self.sleeps, cm.exception.fatal), (1, [], status in (401, 403)))
                self.sleeps.clear()

    def test_timeouts_rate_limits_and_server_errors_are_retried_with_backoff(self):
        for status in (408, 409, 429, 500, 502, 503, 529):
            with self.subTest(status=status):
                llm = self.llm([StatusError(status)] * CALL_ATTEMPTS)
                with self.assertRaises(LLMUnavailable) as cm:
                    llm.send(model="m", messages=[])
                self.assertEqual((self.fake.calls, self.sleeps, cm.exception.fatal), (CALL_ATTEMPTS, [1.0, 2.0, 4.0], False))
                self.sleeps.clear()

    def test_errors_without_a_status_are_retried_only_when_they_are_transport_problems(self):
        class APIConnectionError(Exception): ...
        class SomethingElse(Exception): ...
        llm = self.llm([APIConnectionError("x"), ok()])
        llm.send(model="m", messages=[])
        self.assertEqual(self.fake.calls, 2)
        llm = self.llm([SomethingElse("x")])
        with self.assertRaises(LLMUnavailable):
            llm.send(model="m", messages=[])
        self.assertEqual(self.fake.calls, 1)

    def test_a_transient_error_then_success_returns_the_reply(self):
        llm = self.llm([StatusError(503), StatusError(500), ok()])
        resp, usage = llm.send(model="m", messages=[])
        self.assertEqual((self.fake.calls, self.sleeps, usage["input_tokens"]), (3, [1.0, 2.0], 100))

    def test_rejected_credentials_stop_every_later_request_without_touching_the_network(self):
        llm = self.llm([StatusError(401)])
        with self.assertRaises(LLMUnavailable):
            llm.send(model="m", messages=[])
        for _ in range(3):
            with self.assertRaises(LLMUnavailable) as cm:
                llm.send(model="m", messages=[])
            self.assertTrue(cm.exception.fatal)
            self.assertIn("no further model requests", str(cm.exception))
        self.assertEqual(self.fake.calls, 1)

    def test_giving_up_on_a_transient_error_does_not_stop_the_run(self):
        llm = self.llm([StatusError(503)] * CALL_ATTEMPTS + [ok()])
        with self.assertRaises(LLMUnavailable):
            llm.send(model="m", messages=[])
        llm.send(model="m", messages=[])
        self.assertEqual(self.fake.calls, CALL_ATTEMPTS + 1)

    def test_token_budget_stops_new_requests_once_it_is_reached(self):
        llm = self.llm([ok(), ok(), ok()], max_run_tokens=150)
        llm.send(model="m", messages=[])                       # 120 used: still under the budget
        llm.send(model="m", messages=[])                       # starts under the budget, ends over it (a request in flight completes)
        self.assertEqual(llm.tokens_used, 240)
        with self.assertRaises(LLMUnavailable) as cm:
            llm.send(model="m", messages=[])
        self.assertIn("token budget of 150", str(cm.exception))
        self.assertEqual(self.fake.calls, 2)

    def test_no_budget_means_no_limit(self):
        llm = self.llm([ok(10_000, 10_000)] * 3)
        for _ in range(3):
            llm.send(model="m", messages=[])
        self.assertEqual(llm.tokens_used, 60_000)

    def test_latency_is_measured_with_the_injected_monotonic_clock(self):
        ticks = iter(range(1000))
        self.fake = Fake([ok()])
        llm = LLM(cache_dir=self.dir / "c", usage_path=self.dir / "u.jsonl", client=self.fake, sleep=self.sleeps.append, clock=lambda: next(ticks) * 0.25)
        _, usage = llm.send(model="m", messages=[])
        self.assertEqual(usage["latency_s"], 0.25)

    def test_the_sdk_does_not_retry_on_its_own(self):
        try:
            import anthropic  # noqa: F401
        except ImportError:
            self.skipTest("anthropic is not installed")
        with mock.patch.dict(os.environ, {"ANTHROPIC_API_KEY": "placeholder-not-a-key"}), mock.patch("anthropic.Anthropic") as sdk:
            LLM(cache_dir=self.dir / "c", usage_path=self.dir / "u.jsonl").client()
        sdk.assert_called_once_with(max_retries=0)


class SchemaLimits(unittest.TestCase):
    def test_max_items_and_max_length(self):
        schema = {"type": "object", "properties": {"a": {"type": "array", "maxItems": 2, "items": {"type": "string", "maxLength": 3}}}}
        validate_schema({"a": ["abc", "d"]}, schema)
        with self.assertRaisesRegex(SchemaError, "more than 2 items"):
            validate_schema({"a": ["a", "b", "c"]}, schema)
        with self.assertRaisesRegex(SchemaError, "longer than 3"):
            validate_schema({"a": ["abcd"]}, schema)
        validate_schema({"a": ["x" * 1000] * 50}, {"type": "object", "properties": {"a": {"type": "array", "items": {"type": "string"}}}})      # no limit declared: none applied


if __name__ == "__main__":
    unittest.main()
