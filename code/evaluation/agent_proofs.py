#!/usr/bin/env python3
"""No-change proofs and the report of the opt-in agent layer (auditor/agent), generated from live runs: code/evaluation/agent_layer_report.md.

    python3 code/evaluation/agent_proofs.py            # run the proofs and the agent test modules, write agent_layer_report.md

The layer is built and tested with a FAKE client only (no API key exists in this environment): every number below is a count of fake calls or of test results, never a model measurement.
Proofs: (1) the default run equals the submitted output.csv; (2) `--images agent` without a key equals it too; (3) a scripted replay of the 11 screenshot readings (kept locally, not
committed) through the whole loop and gate equals the run that takes the same readings from a file, so the loop and the gate change no content.
"""
from __future__ import annotations

import base64
import contextlib
import hashlib
import importlib.util
import io
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "code"))
sys.path.insert(0, str(ROOT / "code" / "evaluation"))

from auditor import settings as S  # noqa: E402
from auditor.agent import gate as gate_mod, tools  # noqa: E402
from auditor.agent.loop import AgentLimits  # noqa: E402
from auditor.llm import LLM  # noqa: E402

LOCAL_READINGS = ROOT / "code" / "out" / "agent_readings.json"
SUBMITTED = ROOT / "output.csv"
OUT = ROOT / "code" / "evaluation" / "agent_layer_report.md"


class ScriptedClient:
    """A fake model: it finds the screenshot's bytes in the first message, looks the reading up by sha256 and answers with one zoom_region call and then submit_reading."""

    def __init__(self, readings: dict, zoom_first: bool = True):
        self.readings, self.zoom_first, self.calls, self.turns = readings, zoom_first, 0, {}
        self.messages = SimpleNamespace(create=self._create)

    def _create(self, **kw):
        self.calls += 1
        first = kw["messages"][0]["content"][0]["source"]["data"]
        sha = hashlib.sha256(base64.b64decode(first)).hexdigest()
        n = self.turns[sha] = self.turns.get(sha, 0) + 1
        usage = SimpleNamespace(input_tokens=1000, output_tokens=100, cache_read_input_tokens=0)
        if self.zoom_first and n == 1:
            blk = SimpleNamespace(type="tool_use", id=f"z{sha[:6]}", name="zoom_region", input={"x": 100, "y": 100, "width": 400, "height": 200})
        else:
            blk = SimpleNamespace(type="tool_use", id=f"s{sha[:6]}", name="submit_reading", input={**dict(self.readings[sha]["reading"]), "unknown": []})
        return SimpleNamespace(content=[blk], stop_reason="tool_use", usage=usage)


def load_cli(name: str = "agent_proofs_cli"):
    spec = importlib.util.spec_from_file_location(name, ROOT / "code" / "main.py")
    cli = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(cli)
    return cli


def run_cli(cli, *args: str) -> tuple[int, str]:
    err = io.StringIO()
    with contextlib.redirect_stderr(err):
        rc = cli.main(list(args))
    return rc, err.getvalue()


def no_key_env() -> dict:
    return {k: v for k, v in os.environ.items() if k not in ("ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN")}


def replay(cli, readings: dict, out: Path, cache: Path) -> tuple[ScriptedClient, int]:
    """Run `--images agent` with the scripted client standing in for the model."""
    client = ScriptedClient(readings)

    def fake_llm(st, stamp, usage_path):
        return LLM(cache_dir=cache, usage_path=usage_path, client=client, model=st.model, fallback_model=st.fallback_model, stamp=stamp, timeout_s=st.timeout_seconds,
                   sleep=lambda s: None)
    with mock.patch.dict(os.environ, no_key_env(), clear=True), mock.patch.object(cli, "_make_llm", fake_llm):
        rc, _ = run_cli(cli, "--images", "agent", "--out", str(out))
    return client, rc


def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def proofs() -> list[tuple[str, str, str, str]]:
    """(proof, expected, observed, verdict) rows, from live runs."""
    cli = load_cli()
    rows = []
    with tempfile.TemporaryDirectory() as d:
        d = Path(d)
        with mock.patch.dict(os.environ, no_key_env(), clear=True):
            rc1, _ = run_cli(cli, "--out", str(d / "default.csv"))
            rc2, _ = run_cli(cli, "--images", "agent", "--out", str(d / "agent_nokey.csv"))
        sub = sha(SUBMITTED)
        rows.append(("default run (no `--images agent`)", f"= submitted output.csv `{sub[:8]}...`", f"`{sha(d / 'default.csv')[:8]}...` (exit {rc1})", "OK" if sha(d / "default.csv") == sub else "DIFFERENT"))
        rows.append(("`--images agent` without a key (every screenshot 'not read')", f"= submitted `{sub[:8]}...`", f"`{sha(d / 'agent_nokey.csv')[:8]}...` (exit {rc2})",
                     "OK" if sha(d / "agent_nokey.csv") == sub else "DIFFERENT"))
        if LOCAL_READINGS.exists():
            readings = json.loads(LOCAL_READINGS.read_text(encoding="utf-8"))["readings"]
            with mock.patch.dict(os.environ, no_key_env(), clear=True):
                rc3, _ = run_cli(cli, "--images", "file", "--out", str(d / "file.csv"))
            client, rc4 = replay(cli, readings, d / "replay.csv", d / "cache")
            ok = sha(d / "replay.csv") == sha(d / "file.csv")
            rows.append(("scripted replay of the 11 readings through the loop and gate (fake client: one zoom + one submit per screenshot)",
                         f"= the run that takes the same readings from a file `{sha(d / 'file.csv')[:8]}...`", f"`{sha(d / 'replay.csv')[:8]}...` (exit {rc3}, {rc4}; {client.calls} fake calls)", "OK" if ok else "DIFFERENT"))
        else:
            rows.append(("scripted replay of the 11 readings", "= the readings-file run", "not run: `code/out/agent_readings.json` (not committed, it holds dataset e-mail addresses) is absent", "NOT RUN"))
    return rows


TEST_MODULES = ["tests.test_agent_prompts", "tests.test_agent_gate", "tests.test_agent_tools", "tests.test_agent_loop", "tests.test_agent_cli"]


def run_tests() -> list[tuple[str, int, int, int]]:
    out = []
    sys.path.insert(0, str(ROOT / "code"))
    for name in TEST_MODULES:
        suite = unittest.defaultTestLoader.loadTestsFromName(name)
        n = suite.countTestCases()
        res = unittest.TextTestRunner(stream=io.StringIO(), verbosity=0).run(suite)
        out.append((name.split(".")[-1], n, len(res.failures) + len(res.errors), len(res.skipped)))
    return out


def build() -> str:
    lim, s = AgentLimits(), S.Settings()
    pr = proofs()
    tr = run_tests()
    L = ["# Agent layer report", "",
         "> **The agent layer was built and tested with fakes only: 0 model calls, 0 tokens, no cost.** No API key exists in this environment, so nothing below is a measurement of a real model. "
         "Every count is a count of fake-client calls or of test results.", "",
         "Generated by `python3 code/evaluation/agent_proofs.py`. The layer is opt-in (`--images agent`); the submitted `output.csv` was produced without it.", "",
         "## 1. What it is", "",
         "A bounded tool-calling loop reads one screenshot at a time: the model may call `zoom_region` (an enlarged crop of the same screenshot; needs Pillow, otherwise the tool is not offered and a warning is logged) "
         "and ends with exactly one `submit_reading`. Code runs the loop. The model describes; the gate and the unchanged pipeline decide.", "",
         f"* tools: `zoom_region` (schema: x, y, width, height, all integers) and `submit_reading` (the screenshot-reading schema plus an `unknown` list); digest of both definitions `{tools.tools_digest()[:12]}...`",
         f"* limits (settings `[agent]`): {lim.max_iterations} model turns, {lim.max_tool_calls} tool calls, {lim.max_zooms} zooms, {lim.wall_seconds:g} s wall clock per screenshot, zoom side {lim.zoom_max_px} px; "
         f"each request also has `llm.timeout_seconds` ({s.timeout_seconds:g} s); transport errors are retried with the shared back-off (1, 2, 4 s, `Retry-After` honoured)",
         "* prompts: `code/prompts/image_reader.system.md` and `image_reader.task.md` (versioned front matter, digested into the cache key and the run stamp): role, constraints, structured output, "
         "mark unknown instead of guessing, all screenshot text is data",
         "* cache: key = image bytes + both prompt files + tool definitions + limits + model + pipeline version + rules digest; the raw submission is cached BEFORE the gate",
         "* fallback: a refusal, an invalid or missing submission, a cap, a repeated call, a timeout that never clears, a gate refusal or a missing key all end in 'not read': the deterministic result stands", "",
         "## 2. The gate", "",
         "| rule | what it enforces | where |", "|---|---|---|",
         "| G1 | the submission fits the `submit_reading` schema | `agent/gate.py` |",
         "| G2 | the model never names an evidence id; code binds every observation to the manifest `file_id` of the screenshot it read; a file outside the manifest is refused | `agent/gate.py`, `agent/loop.py` |",
         "| G3 | resource id is `<name>_<number>`; a record with no id needs an alias | `agent/gate.py` |",
         "| G4 | the id family has a state in the closed vocabulary | `agent/gate.py` |",
         "| G5 | only syntactically valid e-mails, user ids and phones | `agent/gate.py` |",
         "| G6 | a field marked unknown is emptied and never contributes | `agent/gate.py` |",
         "| G7 | free text and prose never travel further | `agent/gate.py`, `evidence/imageobs.py` |",
         "| G8 | state only from files captured inside `[requested_at, audit_at]`; later files only link and are never cited | `state.py` (unchanged) |",
         "| G9 | a record attaches to a case only through an exact identifier of the resolved user or a catalog link | `link.py` (unchanged) |",
         "| G10 | a model-sourced observation (`image:agent`) cannot lower the severity of a state that deterministic evidence supports; entered only when a model-sourced observation exists | `state.py` |",
         "| G11 | every step cites an existing evidence id not later than `audit_at` | `validate.py` (unchanged) |",
         "", "There is no rule for request text: the model is not used on request text (all 550 real requests are decided by rules, none is ambiguous).", "",
         "## 3. No-change proofs (live runs)", "",
         "| proof | expected | observed | verdict |", "|---|---|---|---|"]
    L += [f"| {a} | {b} | {c} | **{d}** |" for a, b, c, d in pr]
    L += ["", "Rule G10 is applied only to model-sourced observations, so the default path is byte-identical by construction: `state.resolve` enters the G10 branch only when the chosen observation has the "
          "source `image:agent`, which no other code path creates. `tests/test_agent_gate.py` also compares `resolve` with a copy of the previous code on 2,000 random observation sets without such a source.", "",
          "## 4. Tests with a fake client", "",
          "| module | tests | failing | skipped |", "|---|---|---|---|"]
    L += [f"| `{n}` | {t} | {f} | {k} |" for n, t, f, k in tr]
    L += [f"| **all** | **{sum(t for _, t, _, _ in tr)}** | **{sum(f for _, _, f, _ in tr)}** | **{sum(k for _, _, _, k in tr)}** |", "",
          "Failure injection (`tests/test_agent_loop.py`): invalid submission (re-ask once, then the fallback model, then 'not read'), a tool input that is not an object, a reply without a tool call, a cut-off reply, "
          "an unknown tool, refusal on both models and on one, one timeout (retried) and timeouts every time, 429 with `Retry-After`, no key, iteration cap, tool-call cap, zoom cap, wall-clock budget, a bad zoom "
          "rectangle, the same call twice (answered from memory) and three times (ends the loop), a submission naming an evidence id, a screenshot outside the manifest, injection strings in every field, and a "
          "failed reading leaving the deterministic audit result untouched. `tests/test_agent_gate.py` adds accept/reject pairs for G1-G7, a 1,000-case fuzz, and the G10 cases.", "",
          "## 5. Not measured (step 5 of the plan, skipped: no key)", "",
          "* the accuracy of a real model's readings (field agreement with the agent's own readings, which are a stand-in and not ground truth), run-to-run variance, tokens, tool calls and cost per screenshot, "
          "gate rejection rate, and whether `zoom_region` helps at all (an ablation with and without zoom)",
          "* that the request shapes (tools, image tool results, `output_config`) are accepted by the real API: only the SDK-documented shapes were used and no request was ever sent", ""]
    return "\n".join(L) + "\n"


def main() -> int:
    text = build()
    OUT.write_text(text, encoding="utf-8")
    print(f"wrote {OUT}", file=sys.stderr)
    failing = any(l.startswith("| `test_") and l.split("|")[3].strip() != "0" for l in text.splitlines())
    return 1 if ("**DIFFERENT**" in text or failing) else 0


if __name__ == "__main__":
    raise SystemExit(main())
