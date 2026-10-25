#!/usr/bin/env python3
"""Run the whole pipeline under a matrix of environments that must not matter, and compare the bytes it writes.

    python3 code/tools/determinism_check.py                       # 3 hash seeds x 3 working directories x 3 locales (27 runs)
    python3 code/tools/determinism_check.py --code-root /path     # check another checkout (e.g. an older commit) against the same dataset

Varied: PYTHONHASHSEED (string hash order), the current working directory, LC_ALL / LANG / PYTHONUTF8 (locale and default encoding), TZ.
Compared: sha256 of output.csv, output.notes.md (all of it, including the file-name line: every run writes a file with the same name in a different folder)
and output.usage.jsonl. Exit code 1 and a per-file diff summary if any run differs from the first one.
"""
from __future__ import annotations

import argparse
import hashlib
import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

SEEDS = ("1", "2", "12345")
LOCALES = (  # (LC_ALL, PYTHONUTF8, TZ): C with the UTF-8 mode off is the harshest for default encodings
    ("C", "0", "UTC"),
    ("C.utf8", "1", "America/Los_Angeles"),
    ("POSIX", "0", "Asia/Kolkata"),
)
FILES = ("output.csv", "output.notes.md", "output.usage.jsonl")


def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest() if p.exists() else "(missing)"


def run_matrix(code_root: Path, dataset: Path, work: Path, seeds=SEEDS, locales=LOCALES, full: bool = True) -> list[dict]:
    """One run per (seed, cwd, locale) combination (full) or per seed with cwd i paired with locale i (quick); every run writes <work>/<n>/output.csv."""
    cwds = [code_root, work, Path("/")]
    runs = []
    n = 0
    for seed in seeds:
        for ci, cwd in enumerate(cwds):
            for li, (lc, utf8, tz) in enumerate(locales):
                if not full and ci != li:
                    continue
                n += 1
                out_dir = work / f"run{n:02d}"
                out_dir.mkdir(parents=True)
                env = {k: v for k, v in os.environ.items() if k not in ("LANG", "LC_ALL", "LC_CTYPE", "PYTHONUTF8", "PYTHONHASHSEED", "TZ", "AUDITOR_USAGE_LOG", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN")}
                env.update({"PYTHONHASHSEED": seed, "LC_ALL": lc, "LANG": lc, "PYTHONUTF8": utf8, "TZ": tz})
                p = subprocess.run([sys.executable, str(code_root / "code" / "main.py"), "--dataset", str(dataset), "--out", str(out_dir / "output.csv"), "--images", "off"],
                                   cwd=str(cwd), env=env, capture_output=True, text=True)
                runs.append({"n": n, "seed": seed, "cwd": str(cwd), "locale": lc, "utf8": utf8, "tz": tz, "exit": p.returncode,
                             "hashes": {f: sha(out_dir / f) for f in FILES}, "dir": out_dir})
    return runs


def first_difference(a: Path, b: Path) -> str:
    la, lb = a.read_text(encoding="utf-8").splitlines(), b.read_text(encoding="utf-8").splitlines()
    for i, (x, y) in enumerate(zip(la, lb), 1):
        if x != y:
            return f"line {i}: {x[:90]!r} vs {y[:90]!r}"
    return f"length {len(la)} vs {len(lb)}" if len(la) != len(lb) else "identical text"


def summarise(runs: list[dict]) -> tuple[list[str], bool]:
    lines = ["| file | distinct versions across %d runs | sha256 of the first run |" % len(runs), "|---|---|---|"]
    ok = all(r["exit"] == 0 for r in runs)
    for f in FILES:
        versions = {r["hashes"][f] for r in runs}
        ok &= len(versions) == 1
        lines.append(f"| {f} | {len(versions)} | `{runs[0]['hashes'][f][:16]}...` |")
    bad = [r for r in runs if r["exit"] != 0]
    lines += ["", f"runs with a non-zero exit code: {len(bad)}"]
    for f in FILES:
        base = runs[0]
        diff = [r for r in runs[1:] if r["hashes"][f] != base["hashes"][f]]
        if diff:
            lines.append(f"* {f} differs from run 1 in {len(diff)} of {len(runs) - 1} runs; first: seed {diff[0]['seed']}, cwd {diff[0]['cwd']}, locale {diff[0]['locale']}: "
                         + first_difference(base["dir"] / f, diff[0]["dir"] / f))
    return lines, ok


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--code-root", type=Path, default=ROOT, help="checkout whose code/main.py is run (default: this repository)")
    ap.add_argument("--dataset", type=Path, default=ROOT / "dataset")
    ap.add_argument("--quick", action="store_true", help="9 runs instead of 27 (each seed with one cwd/locale pair)")
    a = ap.parse_args(argv)
    with tempfile.TemporaryDirectory() as d:
        runs = run_matrix(a.code_root.resolve(), a.dataset.resolve(), Path(d), full=not a.quick)
        lines, ok = summarise(runs)
        print(f"{len(runs)} runs: PYTHONHASHSEED {list(SEEDS)} x cwd [code root, scratch dir, /] x locale/encoding/timezone {[l[0] + '/' + l[2] for l in LOCALES]}\n")
        print("\n".join(lines))
        print("\nRESULT:", "all outputs byte-identical" if ok else "OUTPUTS DIFFER")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
