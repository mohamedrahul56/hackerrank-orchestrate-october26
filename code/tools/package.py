#!/usr/bin/env python3
"""Build (or dry-run) code.zip and check every submission rule before anything is uploaded.

    python3 code/tools/package.py --dry-run           # list what would be zipped, run all checks, write nothing
    python3 code/tools/package.py                     # write ./code.zip (git-ignored) after the checks pass
    python3 code/tools/package.py --require-final     # also require evaluation_report.md and usage_report.md (Phase 5)

Rules enforced (AGENTS.md 6.9 and your Phase 0 corrections):
  * no dataset/, no .env, no virtualenv or caches, no code/out/, no agent traces, no nested archives, no code/config/prices.json (local prices file)
  * nothing that looks like a secret (API keys, tokens, private keys, key-like assignments) in anything zipped, in log.txt, or in prices.json
  * no content of the dataset leaks into the zip (no e-mail address of a dataset person appears in any zipped file)
  * the code judge ignores .csv .tsv .json .jsonl .sql .log: the only such file allowed is the price-file template; every table a reader needs is Markdown
  * deterministic: sorted entries, fixed timestamps and permissions, so two builds are byte-identical
"""
from __future__ import annotations

import argparse
import csv
import re
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

ARCHIVE_EXT = {".zip", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".7z", ".rar", ".jar", ".whl"}
JUDGE_IGNORED_EXT = {".csv", ".tsv", ".json", ".jsonl", ".sql", ".log"}
ALLOWED_IGNORED = {"code/config/prices.example.json"}
EXCLUDED_DIRS = {"__pycache__", ".git", ".venv", "venv", "node_modules", ".cache", ".pytest_cache", ".mypy_cache"}
EXCLUDED_PREFIXES = ("code/out/", "code/.cache/", "dataset/", "agent-trace", "output")
EXCLUDED_NAMES = {".env", "prices.json", "log.txt", ".DS_Store"}
REQUIRED = ["README.md", "code/main.py", "code/requirements.txt", "code/evaluation/extra_cases.md", "code/NOTES.md"]
FINAL_REQUIRED = ["code/evaluation/evaluation_report.md", "code/evaluation/usage_report.md"]
MAX_FILE_BYTES = 5_000_000

SECRET_PATTERNS = {
    "Anthropic key": re.compile(r"sk-ant-[A-Za-z0-9_\-]{10,}"),
    "OpenAI-style key": re.compile(r"\bsk-[A-Za-z0-9]{20,}"),
    "AWS access key": re.compile(r"\bAKIA[0-9A-Z]{16}\b"),
    "GitHub token": re.compile(r"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}\b|github_pat_[A-Za-z0-9_]{30,}"),
    "Slack token": re.compile(r"\bxox[abprs]-[A-Za-z0-9-]{10,}"),
    "private key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----"),
    "JWT": re.compile(r"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}"),
    "key-like assignment": re.compile(r"""(?i)\b(?:api[_-]?key|secret|passwd|password|auth[_-]?token|access[_-]?token)\b["']?\s*[:=]\s*["'][A-Za-z0-9_\-/+=]{20,}["']"""),
}
EMAIL_RE = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
TEXT_EXT = {".py", ".md", ".txt", ".csv", ".json", ".sql", ".log", ".toml", ".cfg", ".ini", ".yml", ".yaml", ".sh", ".jsonl", ""}


def rel(p: Path, root: Path) -> str:
    return p.relative_to(root).as_posix()


def collect(root: Path = ROOT) -> list[Path]:
    """README.md plus everything under code/ that is not excluded, sorted."""
    out: list[Path] = []
    for base in (root / "README.md", root / "code"):
        paths = [base] if base.is_file() else sorted(base.rglob("*")) if base.exists() else []
        for p in paths:
            if not p.is_file():
                continue
            r = rel(p, root)
            parts = set(r.split("/"))
            if parts & EXCLUDED_DIRS or p.name in EXCLUDED_NAMES or p.name.startswith(".env") or r.startswith(EXCLUDED_PREFIXES) or p.suffix == ".pyc":
                continue
            out.append(p)
    return sorted(out, key=lambda p: rel(p, root))


def scan_text(name: str, text: str) -> list[str]:
    return [f"{name}: looks like a {label}" for label, rx in SECRET_PATTERNS.items() if rx.search(text)]


def read_text(p: Path) -> str | None:
    if p.suffix.lower() not in TEXT_EXT:
        return None
    try:
        return p.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return None


def dataset_emails(root: Path) -> set[str]:
    found: set[str] = set()
    for name, cols in (("user_identities.csv", ("email",)), ("deletion_requests.csv", ("email",)), ("user_audit_log.csv", ("old_value", "new_value"))):
        f = root / "dataset" / name
        if not f.exists():
            continue
        with f.open(newline="", encoding="utf-8") as fh:
            for r in csv.DictReader(fh):
                for c in cols:
                    found |= {m.lower() for m in EMAIL_RE.findall(r.get(c) or "")}
    return found


def check(files: list[Path], root: Path = ROOT, require_final: bool = False) -> tuple[list[str], list[str]]:
    """(violations, notes). Violations make the build fail."""
    bad: list[str] = []
    notes: list[str] = []
    names = [rel(p, root) for p in files]
    for need in REQUIRED + (FINAL_REQUIRED if require_final else []):
        if need not in names:
            bad.append(f"missing required file {need}")
    if not require_final:
        notes += [f"not yet present (Phase 5): {n}" for n in FINAL_REQUIRED if n not in names]
    emails = dataset_emails(root)
    for p, n in zip(files, names):
        ext = p.suffix.lower()
        if n.startswith("dataset/") or "/dataset/" in n:
            bad.append(f"{n}: dataset content in the zip")
        if ext in ARCHIVE_EXT:
            bad.append(f"{n}: nested archive")
        if p.name.startswith(".env"):
            bad.append(f"{n}: environment file")
        if ext in JUDGE_IGNORED_EXT and n not in ALLOWED_IGNORED:
            bad.append(f"{n}: file type the code judge ignores (put the content in Markdown)")
        if p.stat().st_size > MAX_FILE_BYTES:
            bad.append(f"{n}: {p.stat().st_size} bytes is too large")
        text = read_text(p)
        if text is None:
            continue
        bad += scan_text(n, text)
        leaked = sorted({m.lower() for m in EMAIL_RE.findall(text)} & emails)
        if leaked:
            bad.append(f"{n}: contains {len(leaked)} e-mail address(es) of dataset people, e.g. {leaked[0]}")
    for extra in (root / "log.txt", root / "code" / "config" / "prices.json"):
        if extra.exists():
            t = read_text(extra) or ""
            bad += scan_text(rel(extra, root) + " (not zipped, scanned anyway)", t)
    return bad, notes


def build(files: list[Path], out: Path, root: Path = ROOT) -> None:
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for p in files:
            info = zipfile.ZipInfo(rel(p, root), date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = (0o755 if p.suffix in {".py", ".sh"} and p.stat().st_mode & 0o111 else 0o644) << 16
            z.writestr(info, p.read_bytes())


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dry-run", action="store_true", help="check and list, write nothing")
    ap.add_argument("--out", type=Path, default=ROOT / "code.zip")
    ap.add_argument("--require-final", action="store_true")
    a = ap.parse_args(argv)
    files = collect()
    bad, notes = check(files, ROOT, a.require_final)
    total = sum(p.stat().st_size for p in files)
    print(f"{len(files)} files, {total / 1024:.0f} KiB uncompressed")
    by_ext: dict[str, int] = {}
    for p in files:
        by_ext[p.suffix or "(none)"] = by_ext.get(p.suffix or "(none)", 0) + 1
    print("by extension:", dict(sorted(by_ext.items())))
    for p in files:
        print("  " + rel(p, ROOT))
    for n in notes:
        print("note:", n)
    if bad:
        print("\nVIOLATIONS:")
        for b in bad:
            print("  -", b)
        return 1
    print("\nall checks passed" + (" (dry run, nothing written)" if a.dry_run else ""))
    if not a.dry_run:
        build(files, a.out)
        print(f"wrote {a.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
