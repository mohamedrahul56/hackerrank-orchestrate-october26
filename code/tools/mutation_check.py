#!/usr/bin/env python3
"""Mutation check: break one rule at a time in a COPY of the code and show that the tests notice.

    python3 code/tools/mutation_check.py            # run every mutation, print the table (takes about a minute per mutation)
    python3 code/tools/mutation_check.py --write    # also write code/evaluation/mutation_check.md

For each mutation: copy `code/` to a scratch folder (the repository is never touched), apply one textual edit that breaks one rule, run the invariant,
golden-category, synthetic-case and sample-regression tests, and list the tests that fail. A mutation that no test notices ("SURVIVED") makes the exit code 1.
A control run on the unmodified copy must be green, otherwise failures could not be attributed to the mutation.
"""
from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TEST_MODULES = ["tests.test_invariants", "tests.test_golden_categories", "tests.test_extra_cases", "tests.test_pipeline", "tests.test_validate"]


@dataclass(frozen=True)
class Mutation:
    key: str
    rule: str              # the rule that is deliberately broken
    file: str              # relative to the repository root
    old: str               # exact text, must occur exactly once
    new: str
    primary: bool = True   # the three requested mutations; the others are extra


MUTATIONS = [
    Mutation("M1 canonical order", "inside REMEDIATION, flows must be disabled and restores blocked BEFORE records are deleted (spec 6.5; canonical operation order of problem_statement.md): "
             "DELETE_RECORD is moved ahead of DISABLE_DATA_FLOW in the shared operation table, so the pipeline and its own validator still agree with each other",
             "code/auditor/schema.py",
             '    ("DISABLE_DATA_FLOW", "REMEDIATION", "DISABLED"),\n    ("BLOCK_RESTORE", "REMEDIATION", "BLOCKED"),\n    ("DELETE_RECORD", "REMEDIATION", "ABSENT"),\n',
             '    ("DELETE_RECORD", "REMEDIATION", "ABSENT"),\n    ("DISABLE_DATA_FLOW", "REMEDIATION", "DISABLED"),\n    ("BLOCK_RESTORE", "REMEDIATION", "BLOCKED"),\n'),
    Mutation("M2 COMPLETE rows cite evidence", "every step of a COMPLETE row must carry an evidence id: verification steps of compliant records stop citing their file",
             "code/auditor/audit.py",
             'st.resource, st.file_id if cite_ok else ""),', 'st.resource, ""),'),
    Mutation("M3 NOT_APPLICABLE equals []", "a request that does not authorize deletion must produce steps_json == []: the non-deletion early return now emits a step",
             "code/auditor/audit.py",
             'return CaseResult(case_id, "NOT_APPLICABLE", [], [f"not a deletion request:',
             'return CaseResult(case_id, "NOT_APPLICABLE", [make_step("VERIFICATION", "VERIFY_RECORD_ABSENT", "account_service", "account_0", "")], [f"not a deletion request:'),
    Mutation("M4 policy table: REDACT", "REDACT categories (support message, delivery log, tombstone) must be fixed by REDACT_RECORD: the table now says ANONYMIZE_RECORD",
             "code/auditor/policy.py",
             '"REDACT": Treatment("REPORT_EXPOSED_PII", "REDACT_RECORD",', '"REDACT": Treatment("REPORT_EXPOSED_PII", "ANONYMIZE_RECORD",', primary=False),
    Mutation("M5 legal hold expiry", "a legal hold that ended before audit_at is no longer a retain requirement: the expiry check is removed",
             "code/auditor/audit.py",
             "if st.until is not None and st.until <= audit_at:", "if False:", primary=False),
]


def copy_code(dst: Path) -> None:
    shutil.copytree(ROOT / "code", dst / "code", ignore=shutil.ignore_patterns("__pycache__", "out", ".cache", "*.pyc"))
    (dst / "dataset").symlink_to(ROOT / "dataset")
    for name in ("README.md", "output.csv"):
        if (ROOT / name).exists():
            shutil.copy(ROOT / name, dst / name)


def apply(m: Mutation, repo: Path) -> None:
    p = repo / m.file
    text = p.read_text(encoding="utf-8")
    if text.count(m.old) != 1:
        raise SystemExit(f"{m.key}: the text to mutate occurs {text.count(m.old)} times in {m.file} (must be exactly once); update the mutation definition")
    p.write_text(text.replace(m.old, m.new), encoding="utf-8")


def run_tests(repo: Path) -> tuple[int, int, list[str]]:
    """(tests run, exit code, failing test ids)"""
    p = subprocess.run([sys.executable, "-m", "unittest", "-v", *TEST_MODULES], cwd=repo / "code", capture_output=True, text=True)
    out = p.stderr + p.stdout
    failing = sorted({m.group(1) for m in re.finditer(r"^(?:FAIL|ERROR): (\S+) \(([^)]+)\)", out, flags=re.M) for m in [re.match(r"(.*)", f"{m.group(2)}")]})
    ran = re.search(r"Ran (\d+) tests", out)
    return (int(ran.group(1)) if ran else 0), p.returncode, failing


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--write", action="store_true", help="write code/evaluation/mutation_check.md")
    ap.add_argument("--only", nargs="*", help="mutation keys (prefix, e.g. M1) to run")
    a = ap.parse_args(argv)
    chosen = [m for m in MUTATIONS if not a.only or any(m.key.startswith(k) for k in a.only)]
    results = []
    with tempfile.TemporaryDirectory() as d:
        control_repo = Path(d) / "control"
        control_repo.mkdir()
        copy_code(control_repo)
        control_n, rc, failing = run_tests(control_repo)
        n = control_n
        print(f"control (no mutation): {n} tests, exit {rc}, failing {failing or 'none'}")
        if rc != 0:
            print("the control run must be green")
            return 2
        for m in chosen:
            repo = Path(d) / m.key.split()[0]
            repo.mkdir()
            copy_code(repo)
            apply(m, repo)
            n, rc, failing = run_tests(repo)
            results.append((m, n, rc, failing))
            print(f"{m.key}: {len(failing)} failing test(s) of {n} -> {'KILLED' if failing else 'SURVIVED'}")
            for f in failing[:8]:
                print("    ", f)
    lines = ["# Mutation check (generated by `python3 code/tools/mutation_check.py --write`)", "",
             "Each row breaks ONE rule in a scratch copy of the code (the repository is not touched) and runs the invariant tests, the per-category golden tests, the synthetic cases, "
             f"the sample regression guard and the validator tests ({', '.join(t.split('.')[-1] for t in TEST_MODULES)}). "
             f"Control: the unmodified copy passes all {control_n} of those tests, so every failure below is caused by the mutation. "
             "M1-M3 are the three requested mutations; M4-M5 are extra.", ""]
    lines += ["| mutation | broken rule | edited file | tests run | failing tests | verdict |", "|---|---|---|---|---|---|"]
    for m, n, _rc, failing in results:
        lines.append(f"| {m.key}{'' if m.primary else ' (extra)'} | {m.rule} | `{m.file}` | {n} | {len(failing)} | **{'KILLED' if failing else 'SURVIVED'}** |")
    for m, _n, _rc, failing in results:
        lines += ["", f"### {m.key}", "", f"Edit in `{m.file}`:", "", "```diff", "\n".join(f"- {l}" for l in m.old.rstrip("\n").splitlines()), "\n".join(f"+ {l}" for l in m.new.rstrip("\n").splitlines()), "```", "",
                  f"Failing tests ({len(failing)}):", ""]
        lines += [f"* `{f}`" for f in failing] or ["* (none: the mutation SURVIVED)"]
    text = "\n".join(lines) + "\n"
    if a.write:
        (ROOT / "code" / "evaluation" / "mutation_check.md").write_text(text, encoding="utf-8")
        print("wrote code/evaluation/mutation_check.md")
    return 0 if all(f for _, _, _, f in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
