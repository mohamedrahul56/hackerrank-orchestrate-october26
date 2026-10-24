"""Bounded concurrent image reads: success, 429, timeout, invalid JSON, with a fake client (no network, no key)."""
import base64
import json
import tempfile
import threading
import time
import unittest
from pathlib import Path
from types import SimpleNamespace

from tests._common import *  # noqa: F401,F403
from auditor.evidence import imagejobs
from auditor.evidence.load import load_evidence
from auditor.llm import LLM
from auditor import settings as S


class RateLimitError(Exception):
    def __init__(self, msg="429", retry_after=None):
        super().__init__(msg)
        self.response = SimpleNamespace(headers={"retry-after": str(retry_after)} if retry_after is not None else {})


class APITimeoutError(Exception):
    pass


def reading_for(k):
    return {"screen": f"console {k}", "records": [
        {"resource_id": f"data_flow_{1000 + k}", "selected": True, "emails": [], "user_ids": [], "phones": [],
         "status_words": ["Healthy", "Continuous"], "shows_direct_identifiers": False, "aliases": []}]}


def ok(obj, inp=10, out=5):
    text = obj if isinstance(obj, str) else json.dumps(obj)
    return SimpleNamespace(content=[SimpleNamespace(type="text", text=text)], stop_reason="end_turn",
                           usage=SimpleNamespace(input_tokens=inp, output_tokens=out, cache_read_input_tokens=0))


class ImageClient:
    """A thread-safe fake. The image's bytes are b'IMG<k>'; `script[k]` is a list of outcomes for the calls about that image (exception instances are
    raised, anything else is the reply); once the list is used up the image gets its normal reading. Records the maximum number of calls in flight."""

    def __init__(self, script=None, delay=None, barrier=None):
        self.script, self.delay, self.barrier = script or {}, delay or {}, barrier
        self.lock = threading.Lock()
        self.in_flight = self.max_in_flight = 0
        self.calls = []                                           # (image number, model, timeout) in call order
        self.messages = SimpleNamespace(create=self._create)
        self._first_wave = 0

    def _create(self, **kw):
        data = base64.b64decode(kw["messages"][0]["content"][0]["source"]["data"])
        k = int(data.decode().removeprefix("IMG"))
        with self.lock:
            self.in_flight += 1
            self.max_in_flight = max(self.max_in_flight, self.in_flight)
            self.calls.append((k, kw["model"], kw.get("timeout")))
            self._first_wave += 1
            wait_here = self.barrier is not None and self._first_wave <= self.barrier.parties
            queue = self.script.get(k)
            outcome = queue.pop(0) if queue else None
        try:
            if wait_here:
                self.barrier.wait(timeout=10)                     # passes only when `parties` calls are in flight together
            time.sleep(self.delay.get(k, 0))
            if isinstance(outcome, Exception):
                raise outcome
            return ok(reading_for(k) if outcome is None else outcome)
        finally:
            with self.lock:
                self.in_flight -= 1


def make_root(d, n):
    root = Path(d)
    (root / "evidence").mkdir()
    lines = ["file_id,file_path,source_origin,captured_at"]
    for k in range(n):
        (root / "evidence" / f"shot_{k}.png").write_bytes(f"IMG{k}".encode())
        lines.append(f"evidence_{k:02d},evidence/shot_{k}.png,screen,2026-09-01T10:{k:02d}:00Z")
    (root / "evidence_files.csv").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return root


class ConcurrentImageReads(unittest.TestCase):
    N = 8

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = make_root(self.tmp.name, self.N)
        self.sleeps = []
        self.cache = Path(self.tmp.name) / "cache"

    def tearDown(self):
        self.tmp.cleanup()

    def llm(self, client, **kw):
        kw.setdefault("sleep", self.sleeps.append)
        kw.setdefault("clock", lambda: 100.0)                      # a frozen clock: a pause that was set stays active
        return LLM(cache_dir=self.cache, usage_path=Path(self.tmp.name) / "usage.jsonl", client=client, **kw)

    def load(self, llm, workers):
        ex = lambda path, system, instruction, schema, version: llm.read_image_json("image", path, system, instruction, schema, version)  # noqa: E731
        return load_evidence(self.root, include_images=True, image_extractor=ex, image_workers=workers)

    @staticmethod
    def shape(base):
        return [(o.file_id, o.resource, o.state) for o in base.obs], list(base.warnings)

    # ---------------------------------------------------------------- success, bound, stable order
    def test_success_is_bounded_and_really_concurrent(self):
        client = ImageClient(barrier=threading.Barrier(3))
        base = self.load(self.llm(client), workers=3)
        self.assertEqual(len(base.obs), self.N)
        self.assertEqual(client.max_in_flight, 3, "three calls were in flight together (the barrier needs that) and never more than the bound")

    def test_bound_one_is_sequential(self):
        client = ImageClient()
        self.load(self.llm(client), workers=1)
        self.assertEqual((client.max_in_flight, [c[0] for c in client.calls]), (1, list(range(self.N))))

    def test_output_order_is_the_manifest_order_whatever_order_replies_arrive_in(self):
        sequential = self.shape(self.load(self.llm(ImageClient()), workers=1))
        self.cache = Path(self.tmp.name) / "cache2"               # a cold cache again: the replies must really arrive concurrently
        client = ImageClient(delay={k: (self.N - k) * 0.03 for k in range(self.N)})        # the LAST image answers first
        concurrent = self.shape(self.load(self.llm(client), workers=self.N))
        self.assertGreater(client.max_in_flight, 1)
        self.assertEqual(concurrent, sequential)
        self.assertEqual([o[0] for o in concurrent[0]], [f"evidence_{k:02d}" for k in range(self.N)])

    def test_usage_log_has_one_complete_row_per_image(self):
        self.load(self.llm(ImageClient(delay={k: 0.01 for k in range(self.N)})), workers=4)
        rows = [json.loads(l) for l in (Path(self.tmp.name) / "usage.jsonl").read_text().splitlines()]       # every line is whole JSON
        self.assertEqual(sorted(r["file"] for r in rows), sorted(f"shot_{k}.png" for k in range(self.N)))
        self.assertTrue(all(r["input_tokens"] == 10 and not r["cache_hit"] for r in rows))

    def test_the_same_image_twice_at_once_costs_one_call(self):
        (self.root / "evidence" / "shot_7.png").write_bytes(b"IMG3")                        # file 7 is a copy of file 3
        client = ImageClient(delay={3: 0.1})
        base = self.load(self.llm(client), workers=self.N)
        self.assertEqual(sum(1 for c in client.calls if c[0] == 3), 1)
        self.assertEqual(len(base.obs), self.N)

    # ---------------------------------------------------------------- 429
    def test_429_is_retried_with_backoff_and_the_image_is_still_read(self):
        client = ImageClient(script={2: [RateLimitError(), RateLimitError()]})
        base = self.load(self.llm(client), workers=1)
        self.assertEqual(len(base.obs), self.N)
        self.assertEqual(base.warnings, [])
        self.assertEqual(self.sleeps[:2], [1.0, 2.0], "backoff doubles")
        self.assertEqual(set(self.sleeps[2:]), {2.0}, "with a frozen clock the later images wait out the same shared pause, and nothing else")

    def test_retry_after_longer_than_the_backoff_wins_and_is_capped(self):
        client = ImageClient(script={0: [RateLimitError(retry_after=7)], 1: [RateLimitError(retry_after=9999)]})
        self.load(self.llm(client), workers=1)
        self.assertEqual(self.sleeps[0], 7.0)
        self.assertIn(60.0, self.sleeps)                           # MAX_BACKOFF_S

    def test_one_429_pauses_the_next_call_too(self):
        # the pause is shared: image 1's call, made after image 0 was rate limited, sits out the remaining pause first
        client = ImageClient(script={0: [RateLimitError(retry_after=5)]})
        self.load(self.llm(client), workers=1)
        self.assertEqual(self.sleeps[0], 5.0)
        self.assertEqual(len(self.sleeps), 1 + (self.N - 1) + 0, "the rate-limited call retries without a second wait; each of the other 7 images sits out the shared pause once")
        self.assertEqual(set(self.sleeps), {5.0})

    def test_429_that_never_clears_gives_up_for_that_image_only(self):
        client = ImageClient(script={4: [RateLimitError()] * 4})
        base = self.load(self.llm(client), workers=3)
        self.assertEqual(len(base.obs), self.N - 1)
        self.assertEqual(len(base.warnings), 1)
        self.assertIn("evidence_04", base.warnings[0])
        self.assertIn("LLMUnavailable", base.warnings[0])
        self.assertIn("RateLimitError", base.warnings[0])

    # ---------------------------------------------------------------- timeout
    def test_timeout_is_retried_and_the_request_carries_the_timeout(self):
        client = ImageClient(script={1: [APITimeoutError("timed out")]})
        base = self.load(self.llm(client, timeout_s=12.5), workers=2)
        self.assertEqual(len(base.obs), self.N)
        self.assertEqual({c[2] for c in client.calls}, {12.5})
        self.assertEqual(sum(1 for c in client.calls if c[0] == 1), 2)
        self.assertEqual(self.sleeps, [1.0], "a timeout backs off only the call that timed out")

    def test_timeout_every_time_is_a_warning_not_a_crash(self):
        client = ImageClient(script={6: [APITimeoutError("t")] * 4})
        base = self.load(self.llm(client), workers=4)
        self.assertEqual(len(base.obs), self.N - 1)
        self.assertTrue(any("evidence_06" in w and "APITimeoutError" in w for w in base.warnings))

    # ---------------------------------------------------------------- invalid JSON
    def test_invalid_json_gets_one_reask_and_succeeds(self):
        client = ImageClient(script={5: ["this is not json"]})
        base = self.load(self.llm(client), workers=4)
        self.assertEqual(len(base.obs), self.N)
        self.assertEqual(sum(1 for c in client.calls if c[0] == 5), 2)
        rows = [json.loads(l) for l in (Path(self.tmp.name) / "usage.jsonl").read_text().splitlines()]
        self.assertEqual(next(r for r in rows if r["file"] == "shot_5.png")["calls"], 2)

    def test_invalid_json_from_both_models_is_a_warning_and_others_are_unaffected(self):
        client = ImageClient(script={3: ["nope"] * 4})
        base = self.load(self.llm(client), workers=4)
        self.assertEqual([o[0] for o in self.shape(base)[0]], [f"evidence_{k:02d}" for k in range(self.N) if k != 3])
        self.assertTrue(any("evidence_03" in w and "SchemaError" in w for w in base.warnings))
        self.assertEqual([c[1] for c in client.calls if c[0] == 3], ["claude-haiku-5-5", "claude-haiku-5-5", "claude-sonnet-5-5", "claude-sonnet-5-5"])

    def test_a_mix_of_all_failures_gives_the_same_output_sequentially_and_concurrently(self):
        def script():
            return {0: [RateLimitError()], 2: [APITimeoutError("t")], 3: ["bad"], 5: [RateLimitError()] * 4, 6: ["x"] * 4}
        seq = self.shape(self.load(self.llm(ImageClient(script())), workers=1))
        self.cache = Path(self.tmp.name) / "cache2"
        con = self.shape(self.load(self.llm(ImageClient(script(), delay={k: 0.01 * (self.N - k) for k in range(self.N)})), workers=5))
        self.assertEqual(con, seq)
        self.assertEqual(len(seq[0]), self.N - 2)
        self.assertEqual(len(seq[1]), 2)


class Prefetch(unittest.TestCase):
    def test_exceptions_are_replayed_in_order_and_workers_are_clamped(self):
        seen = []

        def extractor(path, *a):
            seen.append(path)
            if path.name == "b":
                raise ValueError("boom")
            return {"p": path.name}
        paths = [Path("a"), Path("b"), Path("c")]
        replay = imagejobs.prefetch(extractor, paths, "s", "i", {}, "v", workers=99)
        self.assertEqual(sorted(p.name for p in seen), ["a", "b", "c"])
        self.assertEqual(replay(Path("a"), "s", "i", {}, "v"), {"p": "a"})
        with self.assertRaises(ValueError):
            replay(Path("b"), "s", "i", {}, "v")
        self.assertEqual(len(seen), 3, "replaying does not call the model again")

    def test_one_worker_or_one_image_returns_the_plain_extractor(self):
        f = lambda *a: {}  # noqa: E731
        self.assertIs(imagejobs.prefetch(f, [Path("a"), Path("b")], "s", "i", {}, "v", 1), f)
        self.assertIs(imagejobs.prefetch(f, [Path("a")], "s", "i", {}, "v", 8), f)


class SettingsForConcurrency(unittest.TestCase):
    def test_bounds_and_that_they_do_not_change_the_fingerprint(self):
        base = S.Settings()
        self.assertEqual((base.max_concurrency, base.timeout_seconds), (4, 60.0))
        for bad in ("0", "17", "x"):
            with self.assertRaises(S.SettingsError):
                S.apply(base, "llm.max_concurrency", bad)
        with self.assertRaises(S.SettingsError):
            S.apply(base, "llm.timeout_seconds", "0")
        self.assertEqual(base.fingerprint(), S.apply(S.apply(base, "llm.max_concurrency", "9"), "llm.timeout_seconds", "5").fingerprint())

    def test_config_carries_the_worker_count(self):
        from auditor.audit import Config
        self.assertEqual(Config.from_settings(S.apply(S.Settings(), "llm.max_concurrency", "7")).image_workers, 7)


if __name__ == "__main__":
    unittest.main()
