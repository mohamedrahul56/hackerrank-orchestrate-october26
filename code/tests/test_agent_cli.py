"""The agent layer through the command line: opt-in, no change without a key, settings, and a scripted replay of the 11 screenshot readings through the full loop."""
import base64
import contextlib
import hashlib
import importlib.util
import io
import json
import os
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

from tests._common import *  # noqa: F401,F403
from auditor import settings as S
from auditor.llm import LLM
from agent_proofs import ScriptedClient

LOCAL = ROOT / "code" / "out" / "agent_readings.json"
SUBMITTED = ROOT / "output.csv"


def load_cli(name="auditor_cli_agent"):
    spec = importlib.util.spec_from_file_location(name, ROOT / "code" / "main.py")
    cli = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(cli)
    return cli


def run(cli, *args):
    err = io.StringIO()
    with contextlib.redirect_stderr(err):
        rc = cli.main(list(args))
    return rc, err.getvalue()


def no_key_env():
    return {k: v for k, v in os.environ.items() if k not in ("ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN")}


@unittest.skipUnless(DATASET.exists(), "dataset/ not present")
class OptInAndNoChange(unittest.TestCase):
    def test_default_is_unchanged_and_agent_is_not_the_default(self):
        self.assertEqual(S.Settings().images, "auto")
        self.assertNotEqual(S.load(env={}).images, "agent")

    def test_agent_mode_without_a_key_reproduces_the_submitted_output_byte_for_byte(self):
        cli = load_cli()
        with tempfile.TemporaryDirectory() as d, mock.patch.dict(os.environ, no_key_env(), clear=True):
            out = Path(d) / "o.csv"
            rc, err = run(cli, "--images", "agent", "--out", str(out))
            self.assertEqual(rc, 0, err)
            self.assertEqual(out.read_bytes(), SUBMITTED.read_bytes())
            notes = out.with_suffix(".notes.md").read_text(encoding="utf-8")
            self.assertIn("Agent layer", notes)
            self.assertIn("not read", notes)
            info = [json.loads(l) for l in out.with_suffix(".usage.jsonl").read_text().splitlines() if '"run_info"' in l][0]
            self.assertEqual((info["images_read_by_agent"], info["agent_screenshots_read"], info["agent_screenshots_failed"]), (True, 0, 0))
            rows = [json.loads(l) for l in out.with_suffix(".usage.jsonl").read_text().splitlines()]
            self.assertEqual([r for r in rows if r.get("kind") == "agent_image"], [], "no key: no call, no tokens, no usage row")

    def test_the_default_run_with_and_without_the_agent_package_importable_is_the_same_bytes(self):
        cli = load_cli("auditor_cli_default")
        with tempfile.TemporaryDirectory() as d, mock.patch.dict(os.environ, no_key_env(), clear=True):
            out = Path(d) / "o.csv"
            self.assertEqual(run(cli, "--out", str(out))[0], 0)
            self.assertEqual(out.read_bytes(), SUBMITTED.read_bytes())


class AgentSettings(unittest.TestCase):
    def test_images_agent_and_the_limits_are_valid_settings(self):
        s = S.apply(S.Settings(), "llm.images", "agent")
        self.assertEqual(s.images, "agent")
        s = S.apply(s, "agent.max_zooms", "2")
        self.assertEqual(s.agent_max_zooms, 2)
        for dotted, bad in (("agent.max_iterations", "0"), ("agent.max_iterations", "99"), ("agent.wall_seconds", "1"), ("agent.zoom_max_px", "10"), ("agent.max_zooms", "-1")):
            with self.assertRaises(S.SettingsError, msg=dotted):
                S.apply(S.Settings(), dotted, bad)

    def test_the_loop_limits_are_in_the_fingerprint_only_when_the_loop_runs(self):
        base = S.Settings()
        self.assertEqual(base.fingerprint(), S.apply(base, "agent.max_zooms", "1").fingerprint())
        a = S.apply(base, "llm.images", "agent")
        self.assertNotEqual(a.fingerprint(), S.apply(a, "agent.max_zooms", "1").fingerprint())

    def test_the_shipped_file_documents_every_agent_key(self):
        text = S.DEFAULT_FILE.read_text(encoding="utf-8")
        self.assertIn("[agent]", text)
        self.assertEqual(S.from_file(S.DEFAULT_FILE), S.Settings())


@unittest.skipUnless(LOCAL.exists() and DATASET.exists(), "the local readings file (code/out/agent_readings.json) or dataset/ is not present")
class ScriptedReplay(unittest.TestCase):
    """The loop and the gate must not change what a reading says: replaying the 11 readings through them equals the readings-file run."""

    def test_replay_through_the_loop_equals_the_readings_file_run(self):
        readings = json.loads(LOCAL.read_text(encoding="utf-8"))["readings"]
        cli = load_cli("auditor_cli_replay")
        client = ScriptedClient(readings)
        with tempfile.TemporaryDirectory() as d:
            d = Path(d)
            from_file = d / "file.csv"
            via_agent = d / "agent.csv"
            with mock.patch.dict(os.environ, no_key_env(), clear=True):
                self.assertEqual(run(cli, "--images", "file", "--out", str(from_file))[0], 0)
                real_llm = cli._make_llm

                def fake_llm(st, stamp, usage_path):
                    return LLM(cache_dir=d / "cache", usage_path=usage_path, client=client, model=st.model, fallback_model=st.fallback_model, stamp=stamp,
                               timeout_s=st.timeout_seconds, sleep=lambda s: None)
                with mock.patch.object(cli, "_make_llm", fake_llm):
                    rc, err = run(cli, "--images", "agent", "--out", str(via_agent))
            self.assertEqual(rc, 0, err)
            self.assertEqual(via_agent.read_bytes(), from_file.read_bytes(), "the agent loop and gate changed an answer")
            self.assertEqual(client.calls, 22, "11 screenshots x (one zoom + one submit)")
            rows = [json.loads(l) for l in via_agent.with_suffix(".usage.jsonl").read_text().splitlines()]
            agent_rows = [r for r in rows if r.get("kind") == "agent_image"]
            self.assertEqual((len(agent_rows), sum(r["zooms"] for r in agent_rows), sum(r["tool_calls"] for r in agent_rows)), (11, 11, 22))
            notes = via_agent.with_suffix(".notes.md").read_text(encoding="utf-8")
            self.assertIn("screenshots read: 11", notes)
            self.assertNotIn("G10", notes, "no model reading contradicted deterministic evidence on the real data")
            gate_lines = [l for l in notes.splitlines() if l.startswith("* evidence_") and " G" in l]
            self.assertEqual(len(gate_lines), 1, gate_lines)
            self.assertIn("evidence_42: G3 record 0: no resource id and no alias", gate_lines[0])       # the registry screenshot that shows no id: nothing to record, as for the file source


if __name__ == "__main__":
    unittest.main()
