#!/usr/bin/env python3
"""Evaluation entry point: run the agent on the 32 labeled sample requests and print the score report (Markdown).

    python3 code/evaluation/main.py                 # samples -> code/out/pred_samples.csv, then the scorer
    python3 code/evaluation/main.py --split all     # extra arguments are passed to the scorer (see score.py --help)
    python3 code/evaluation/main.py --images file   # the same, with the screenshots read from the readings file instead of left unread

The full evaluation write-up (accuracy, step/field/sequence metrics, extra cases, failures and fixes) is in
`code/evaluation/evaluation_report.md`; the 23 extra cases are in `code/evaluation/extra_cases.md`.
"""
from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "code"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

import score  # noqa: E402

_spec = importlib.util.spec_from_file_location("agent_main", ROOT / "code" / "main.py")      # this file is also called main.py: load the agent by path
agent = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(agent)


def run(argv: list[str]) -> int:
    images = "off"                                                  # `--images file` scores the samples with the readings file (code/out/agent_readings.json) instead
    if "--images" in argv:
        i = argv.index("--images")
        images, argv = argv[i + 1], argv[:i] + argv[i + 2:]
    pred = ROOT / "code" / "out" / ("pred_samples.csv" if images == "off" else f"pred_samples_{images}.csv")
    code = agent.main(["--requests", "sample_deletion_requests.csv", "--out", str(pred), "--images", images])
    if code != 0:
        return code
    return score.main(["--pred", str(pred), *(argv or ["--split", "both"])])


if __name__ == "__main__":
    raise SystemExit(run(sys.argv[1:]))
