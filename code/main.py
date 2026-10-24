#!/usr/bin/env python3
"""Run the deletion-audit agent.

    python3 code/main.py                                   # all 518 requests -> ./output.csv
    python3 code/main.py --requests sample_deletion_requests.csv --out code/out/pred_samples.csv

Reads only files under dataset/. Writes the answer CSV, re-reads it from disk and validates every row.
Anything unusual (fallbacks, assumptions, later-evidence links) is written to <out>.notes.md and summarised on stderr.
"""
from __future__ import annotations

import argparse
import logging
import os
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))

from auditor.audit import Auditor, Config  # noqa: E402
from auditor.dataset import load_dataset  # noqa: E402
from auditor.outio import write_output  # noqa: E402
from auditor.validate import validate_output_file  # noqa: E402


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dataset", type=Path, default=ROOT / "dataset")
    ap.add_argument("--requests", default="deletion_requests.csv", help="file inside --dataset with the cases to answer")
    ap.add_argument("--out", type=Path, default=ROOT / "output.csv")
    ap.add_argument("--no-later-linking", action="store_true", help="forbid linking resources to users with evidence captured after audit_at")
    ap.add_argument("--no-discovery", action="store_true", help="disable activity-timeline discovery of id-less accounts / analytics")
    ap.add_argument("--cohort-filter", action="store_true", help="ablation only: narrow ambiguous accounts by cohort snapshot (measured harmful, off by default)")
    ap.add_argument("--images", choices=["auto", "on", "off"], default="auto",
                    help="read the PNG evidence with a vision model (needs ANTHROPIC_API_KEY); auto = only when a key is set")
    ap.add_argument("-v", "--verbose", action="store_true")
    a = ap.parse_args(argv)
    logging.basicConfig(level=logging.INFO if a.verbose else logging.WARNING, format="%(levelname)s %(message)s")

    cfg = Config(allow_later_linking=not a.no_later_linking, use_discovery=not a.no_discovery, use_cohort_filter=a.cohort_filter)
    llm = None
    if a.images == "on" or (a.images == "auto" and (os.environ.get("ANTHROPIC_API_KEY") or os.environ.get("ANTHROPIC_AUTH_TOKEN"))):
        from auditor.llm import LLM
        usage_path = Path(os.environ.get("AUDITOR_USAGE_LOG") or ROOT / "code" / "out" / "usage_log.jsonl")
        usage_path.parent.mkdir(parents=True, exist_ok=True)
        usage_path.write_text("", encoding="utf-8")            # one log per run: evaluation/usage_report.py summarises exactly this run
        llm = LLM(usage_path=usage_path)
        cfg.image_extractor = lambda path, system, instruction, schema, version: llm.read_image_json("image", path, system, instruction, schema, version)
    auditor = Auditor(a.dataset, cfg)
    ds = load_dataset(a.dataset, a.requests)
    results = [auditor.audit(r.case_id) for r in ds.requests]
    write_output(a.out, [(x.case_id, x.status, x.steps) for x in results])
    issues = validate_output_file(a.out, ds)
    errors = [i for i in issues if i.severity == "error"]

    notes = [f"# Run notes for {a.out.name}", "", f"cases: {len(results)}, status counts: {dict(Counter(x.status for x in results))}",
             f"evidence warnings: {len(auditor.base.warnings)}", ""]
    notes += [f"* {w}" for w in auditor.base.warnings[:50]]
    notes += ["", "## Per-case notes (assumptions, fallbacks)", ""]
    notes += [f"* {x.case_id}: {n}" for x in results for n in x.notes]
    notes += ["", "## Later-evidence links (D3: evidence captured after audit_at used ONLY to link a resource to the user)", ""]
    notes += [f"* {x.case_id}: {rid} linked via {fid} (captured {t})" for x in results for rid, fid, t in x.later_links]
    a.out.with_suffix(".notes.md").write_text("\n".join(notes) + "\n", encoding="utf-8")

    fallbacks = sum(1 for x in results for n in x.notes if n.startswith("FALLBACK"))
    print(f"wrote {a.out} ({len(results)} rows); status counts {dict(Counter(x.status for x in results))}; "
          f"fallbacks {fallbacks}; validator errors {len(errors)}", file=sys.stderr)
    for i in errors[:10]:
        print(i, file=sys.stderr)
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
