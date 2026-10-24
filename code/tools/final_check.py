#!/usr/bin/env python3
"""Final submission checklist: every item a program can verify is verified here; the rest is marked NOT VERIFIED.

    python3 code/tools/final_check.py [--build-zip] [--expect-log-turns N]

Prints a Markdown checklist (also written to code/out/final_checklist.md, git-ignored). Exit code 1 if a verifiable item fails.
"""
from __future__ import annotations

import argparse
import csv
import importlib.util
import io
import json
import re
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "code"))
sys.path.insert(0, str(ROOT / "code" / "evaluation"))


def load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def git(*args: str) -> str:
    return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True).stdout.strip()


def natural(c: str) -> int:
    return int(re.search(r"\d+", c).group())


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--build-zip", action="store_true", help="write ./code.zip with the packaging tool before checking it")
    ap.add_argument("--expect-log-turns", type=int, default=0, help="number of user turns the log must contain (0 = do not check)")
    a = ap.parse_args(argv)
    pkg = load("package_tool", ROOT / "code" / "tools" / "package.py")
    agent = load("agent_main", ROOT / "code" / "main.py")
    from auditor.dataset import load_dataset
    from auditor.validate import validate_output_file
    from tests.test_reports_markdown import problems

    items: list[tuple[str, bool | None, str]] = []          # (what, True verified / False failed / None cannot be verified here, detail)

    def add(what: str, ok: bool | None, detail: str = "") -> None:
        items.append((what, ok, detail))

    ds = load_dataset(ROOT / "dataset")
    out = ROOT / "output.csv"
    # ---------------------------------------------------------------- output.csv
    rows = list(csv.reader(out.open(newline="", encoding="utf-8"))) if out.exists() else []
    ids = [r[0] for r in rows[1:]]
    add("`output.csv` is at the repository root with header `case_id,deletion_status,steps_json`", bool(rows) and rows[0] == ["case_id", "deletion_status", "steps_json"])
    add(f"`output.csv` has exactly {len(ds.case_order)} data rows, one per `case_id` of `deletion_requests.csv`, no duplicates", len(ids) == len(ds.case_order) == len(set(ids)) and set(ids) == set(ds.case_order),
        f"{len(ids)} rows")
    add("rows are in the order of `deletion_requests.csv`, which is also the numeric order of the case ids", ids == ds.case_order and ids == sorted(ids, key=natural),
        "NOTE: a plain string sort would put a three-digit id before a two-digit one; the input file and the sample answers use numeric order, so numeric order is used")
    errs = [i for i in validate_output_file(out, ds) if i.severity == "error"] if out.exists() else ["missing"]
    add("every row passes the contract validator (JSON, step_no, phases, triples, order, pairing, evidence ids exist and are not later than `audit_at`)", not errs, f"{len(errs)} errors")
    statuses = {}
    for r in rows[1:]:
        statuses[r[1]] = statuses.get(r[1], 0) + 1
    na_ok = all(json.loads(r[2]) == [] for r in rows[1:] if r[1] == "NOT_APPLICABLE")
    add("NOT_APPLICABLE rows have `[]`; statuses are COMPLETE / INCOMPLETE / NOT_APPLICABLE only", na_ok and set(statuses) <= {"COMPLETE", "INCOMPLETE", "NOT_APPLICABLE"},
        ", ".join(f"{k} {v}" for k, v in sorted(statuses.items())))
    complete_empty = sum(1 for r in rows[1:] if r[1] == "COMPLETE" for s in json.loads(r[2]) if not s["evidence_id"])
    add("every step of every COMPLETE row cites an evidence id", complete_empty == 0, f"{complete_empty} steps without one")
    with tempfile.TemporaryDirectory() as d:
        fresh = Path(d) / "fresh.csv"
        import contextlib
        with contextlib.redirect_stderr(io.StringIO()):
            code = agent.main(["--out", str(fresh)])
        same = fresh.exists() and out.exists() and fresh.read_bytes() == out.read_bytes()
    add("`output.csv` is byte-identical to a fresh run of `python3 code/main.py` (determinism)", code == 0 and same)
    notes = ROOT / "output.notes.md"
    prov = re.findall(r"^\* (case_\d+):", notes.read_text(encoding="utf-8").split("## Provisional rows")[1].split("## Per-case notes")[0], flags=re.M) if notes.exists() else []
    add("provisional rows are flagged in `output.notes.md`", bool(notes.exists()), f"{len(prov)}: {', '.join(prov)}")

    # ---------------------------------------------------------------- reports
    ev = ROOT / "code" / "evaluation"
    for name in ("evaluation_report.md", "usage_report.md"):
        p = ev / name
        add(f"`code/evaluation/{name}` exists and renders as well-formed Markdown", p.exists() and not problems(p), "; ".join(problems(p)) if p.exists() else "missing")
    usage = (ev / "usage_report.md").read_text(encoding="utf-8") if (ev / "usage_report.md").exists() else ""
    info_line = re.search(r"sha256 `([0-9a-f]{16})", usage)
    add("`usage_report.md` describes the run that produced the submitted `output.csv` (sha256 matches)", bool(info_line) and out.exists() and
        __import__("hashlib").sha256(out.read_bytes()).hexdigest().startswith(info_line.group(1)), info_line.group(1) + "..." if info_line else "no run_info")
    add("`usage_report.md` states 0 model calls or real logged tokens, and the exact no-price line or a cost with source", ("No model calls were made" in usage or "model calls (incl. re-asks)" in usage) and
        ("cost not computed - prices not supplied" in usage or "supplied by the participant" in usage))
    add("README, INTERVIEW_PREP and the 23 synthetic cases are well-formed Markdown", all(not problems(p) for p in (ROOT / "README.md", ROOT / "code" / "INTERVIEW_PREP.md", ev / "extra_cases.md")))

    # ---------------------------------------------------------------- zip
    zpath = ROOT / "code.zip"
    files = pkg.collect(ROOT)
    bad, _ = pkg.check(files, ROOT, require_final=True)
    add("packaging rules pass with the final reports required (no dataset/, `.env`, `prices.json`, `code/out/`, traces, nested archives; no secrets; no dataset e-mails; no judge-ignored file types except the price template)", not bad, "; ".join(bad))
    if a.build_zip and not bad:
        pkg.build(files, zpath, ROOT)
    if zpath.exists():
        with zipfile.ZipFile(zpath) as z:
            names = z.namelist()
            tz = z.testzip()
            with tempfile.TemporaryDirectory() as d:
                z.extractall(d)
                with contextlib.redirect_stderr(io.StringIO()):
                    r = subprocess.run([sys.executable, str(Path(d) / "code" / "main.py"), "--dataset", str(ROOT / "dataset"), "--out", str(Path(d) / "from_zip.csv")], capture_output=True, text=True)
                same_zip = r.returncode == 0 and (Path(d) / "from_zip.csv").read_bytes() == out.read_bytes()
        add("`code.zip` exists, is a valid archive and its names equal the checked file list", tz is None and sorted(names) == sorted(pkg.rel(p, ROOT) for p in files), f"{len(names)} files, {zpath.stat().st_size // 1024} KiB")
        add("`code.zip` has no `dataset/`, `.env`, `prices.json`, `code/out/`, nested archive; has README.md, both reports, INTERVIEW_PREP.md, extra_cases.md",
            not any(n.startswith("dataset/") or n.split("/")[-1].startswith(".env") or n.endswith("/prices.json") or n.startswith("code/out/") or any(n.endswith(e) for e in pkg.ARCHIVE_EXT) for n in names)
            and all(n in names for n in ("README.md", "code/evaluation/evaluation_report.md", "code/evaluation/usage_report.md", "code/INTERVIEW_PREP.md", "code/evaluation/extra_cases.md")))
        add("the extracted `code.zip`, run on the dataset, reproduces `output.csv` byte for byte", same_zip)
    else:
        add("`code.zip` exists", False, "run with --build-zip")

    # ---------------------------------------------------------------- log.txt
    log = ROOT / "log.txt"
    text = log.read_text(encoding="utf-8") if log.exists() else ""
    heads = re.findall(r"^## \[[^\]]+\] ", text, flags=re.M)
    tool_lines = re.findall(r"^tool=(.+)$", text, flags=re.M)
    model_lines = re.findall(r"^model=(.+)$", text, flags=re.M)
    prompts = text.count("User Prompt (verbatim")
    add("`log.txt` is at the repository root (the path `AGENTS.md` requires), UTF-8, LF line endings", log.exists() and b"\r" not in log.read_bytes())
    add("every log entry has exactly one non-empty `tool=` and one `model=` line, no placeholders", len(heads) == len(tool_lines) == len(model_lines) and all(t.strip() and "<" not in t for t in tool_lines + model_lines)
        and set(tool_lines) == {"Claude Code"} and set(model_lines) == {"claude-sonnet-5-5"}, f"{len(heads)} entries (1 session start + {prompts} turns); tool {set(tool_lines)}; model {set(model_lines)}")
    if a.expect_log_turns:
        add(f"`log.txt` holds all {a.expect_log_turns} user turns", prompts == a.expect_log_turns, f"{prompts} found")
    add("`log.txt` contains no secret shape", not pkg.scan_text("log.txt", text))

    # ---------------------------------------------------------------- repository
    tracked = git("ls-files").split("\n")
    add("no `.env*`, `prices.json`, `code.zip` or `agent-trace*` is tracked by git", not [t for t in tracked if t.split("/")[-1].startswith(".env") or t.endswith("prices.json") or t == "code.zip" or t.startswith("agent-trace")])
    leaked = []
    emails = pkg.dataset_emails(ROOT)
    for t in tracked:
        if t.startswith("dataset/") or not t:
            continue
        txt = pkg.read_text(ROOT / t) if (ROOT / t).is_file() else None
        if txt and ({m.lower() for m in pkg.EMAIL_RE.findall(txt)} & emails):
            leaked.append(t)
    add("no tracked file outside `dataset/` contains an e-mail address of a dataset person", not leaked, ", ".join(leaked))
    secrets = [h for t in tracked if t and (ROOT / t).is_file() and not t.startswith("dataset/") for h in pkg.scan_text(t, pkg.read_text(ROOT / t) or "")]
    add("secret scan over every tracked file outside `dataset/`", not secrets, "; ".join(secrets))
    dirty = git("status", "--porcelain")
    branch = git("rev-parse", "--abbrev-ref", "HEAD")
    ahead = git("rev-list", "--count", f"origin/{branch}..HEAD") if git("rev-parse", "--verify", f"origin/{branch}") else "?"
    add("working tree is clean (everything that matters is committed)", not dirty, dirty.replace("\n", "; ")[:200])
    add(f"HEAD is pushed to `origin/{branch}`", ahead == "0", f"{ahead} commits not pushed (this reads the local copy of the remote ref)")

    # ---------------------------------------------------------------- cannot be verified by a program
    add("`agent-trace.zip` exported by you (steps in `code/SUBMISSION_CHECKLIST.md`)", None, "only you can export it")
    add("submission URL taken from your registration email (the URL in `AGENTS.md` is not trusted)", None, "only you can check it")
    add("AI-interview readiness (`code/INTERVIEW_PREP.md`)", None, "needs you")

    lines = ["# Final checklist (generated by `python3 code/tools/final_check.py`)", ""]
    for what, ok, detail in items:
        mark = "[x] VERIFIED" if ok else ("[ ] NOT VERIFIED" if ok is None else "[ ] **FAILED**")
        lines.append(f"- {mark} - {what}" + (f" ({detail})" if detail else ""))
    text_out = "\n".join(lines) + "\n"
    (ROOT / "code" / "out").mkdir(exist_ok=True)
    (ROOT / "code" / "out" / "final_checklist.md").write_text(text_out, encoding="utf-8")
    print(text_out)
    return 1 if any(ok is False for _, ok, _ in items) else 0


if __name__ == "__main__":
    raise SystemExit(main())
