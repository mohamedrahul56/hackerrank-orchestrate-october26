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

from auditor import settings as cfgmod  # noqa: E402
from auditor import versions  # noqa: E402
from auditor.audit import Auditor, Config  # noqa: E402
from auditor.checkpoint import Checkpoint, audit_all, image_observation_digest, run_key  # noqa: E402
from auditor.dataset import DatasetError, load_dataset  # noqa: E402
from auditor.outio import write_output  # noqa: E402
from auditor.validate import validate_output_file  # noqa: E402


def main(argv: list[str] | None = None) -> int:
    try:
        return _run(argv)
    except DatasetError as e:                       # a required input file is missing or has no usable header: nothing sensible can be written
        print(f"error: {e}", file=sys.stderr)
        return 2


def _make_llm(st, stamp, usage_path):
    from auditor.llm import LLM
    return LLM(cache_dir=Path(st.cache_dir) if st.cache_dir else None, usage_path=usage_path, model=st.model, fallback_model=st.fallback_model, stamp=stamp,
               timeout_s=st.timeout_seconds)


DEFAULT_READINGS = ROOT / "code" / "out" / "agent_readings.json"


def _file_readings(st, usage_path):
    """The readings-file source for images = file (auditor/readings.py). Raises ReadingsError when the file is unusable."""
    from auditor.llm import UsageLog
    from auditor.readings import FileReadings
    return FileReadings(Path(st.readings_file) if st.readings_file else DEFAULT_READINGS, UsageLog(usage_path))


def _explain(a, cfg, st, stamp) -> int:
    """--explain: build the auditor exactly as a normal run does, print the explanation, write nothing (no output.csv, notes or usage log)."""
    from auditor.explain import explain
    if st.images in ("on", "file"):
        import tempfile
        tmp_usage = Path(tempfile.mkdtemp()) / "explain_usage.jsonl"                              # the usage of an explanation is not part of any run's log
        source = _make_llm(st, stamp, tmp_usage) if st.images == "on" else _file_readings(st, tmp_usage)
        cfg.image_extractor = lambda path, system, instruction, schema, version: source.read_image_json("image", path, system, instruction, schema, version)
    auditor = Auditor(a.dataset, cfg)
    try:
        sys.stdout.write(explain(auditor, a.explain))
    except KeyError:
        known = sorted(auditor.requests, key=lambda c: (len(c), c))
        print(f"error: no case {a.explain!r} in the request files (e.g. {', '.join(known[:3])} ... {known[-1]})", file=sys.stderr)
        return 2
    return 0


def _run(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dataset", type=Path, default=ROOT / "dataset")
    ap.add_argument("--requests", default="deletion_requests.csv", help="file inside --dataset with the cases to answer")
    ap.add_argument("--out", type=Path, default=ROOT / "output.csv")
    ap.add_argument("--config", type=Path, default=None, metavar="FILE", help=f"settings file (TOML; default {cfgmod.DEFAULT_FILE.relative_to(ROOT)}, documented there)")
    ap.add_argument("--set", action="append", default=[], metavar="SECTION.KEY=VALUE", dest="overrides",
                    help="override one setting, e.g. --set discovery.phase_margin=3.0 (repeatable; wins over the file and the environment)")
    ap.add_argument("--show-settings", action="store_true", help="print the effective settings, their fingerprint and the version stamp, then exit")
    ap.add_argument("--no-later-linking", action="store_true", help="forbid linking resources to users with evidence captured after audit_at")
    ap.add_argument("--no-discovery", action="store_true", help="disable activity-timeline discovery of id-less accounts / analytics")
    ap.add_argument("--no-timing-heuristics", action="store_true",
                    help="disable the two second-of-minute heuristics (account offsets, analytics 40/41 s offset); they exploit this dataset's event timing")
    ap.add_argument("--cohort-filter", action="store_true", help="ablation only: narrow ambiguous accounts by cohort snapshot (measured harmful, off by default)")
    ap.add_argument("--images", choices=["auto", "on", "off", "file"], default=None,
                    help="read the PNG evidence with a vision model (needs ANTHROPIC_API_KEY); auto = only when a key is set; file = take the readings from a readings file "
                         "(settings llm.readings_file, default code/out/agent_readings.json; auditor/readings.py) instead of a model (default: settings llm.images)")
    ap.add_argument("--explain", metavar="CASE_ID", help="print how this one case got its answer (identity, evidence, rule, why each step exists or is omitted) and exit; "
                    "read-only: writes no output file, notes or usage log; screenshots are read only with --images on")
    ap.add_argument("--restart", action="store_true", help="ignore a checkpoint left by an interrupted run and audit every case again")
    ap.add_argument("--keep-checkpoint", action="store_true", help="keep <out>.partial.jsonl after a successful run (it is removed otherwise)")
    ap.add_argument("-v", "--verbose", action="store_true")
    a = ap.parse_args(argv)
    logging.basicConfig(level=logging.INFO if a.verbose else logging.WARNING, format="%(levelname)s %(message)s")

    flags = [("pipeline.later_linking=false", a.no_later_linking), ("pipeline.discovery=false", a.no_discovery), ("pipeline.cohort_filter=true", a.cohort_filter),
             ("pipeline.timing_heuristics=false", a.no_timing_heuristics), (f"llm.images={a.images}", a.images is not None)]
    try:                                                    # precedence: defaults < settings file < environment < --set < dedicated flags
        st = cfgmod.load(a.config, a.overrides)
        for item, on in flags:
            if on:
                st = cfgmod.apply(st, *item.split("=", 1))
    except cfgmod.SettingsError as e:
        print(f"error: settings: {e}", file=sys.stderr)
        return 2
    stamp = versions.stamp(a.dataset, st.fingerprint())
    if a.show_settings:
        print("\n".join(f"{k} = {v}" for k, v in cfgmod.as_table(st)))
        print("\n".join(["", *versions.stamp_lines(stamp)]))
        return 0
    cfg = Config.from_settings(st)
    if a.explain:
        return _explain(a, cfg, st, stamp)
    usage_path = Path(os.environ.get("AUDITOR_USAGE_LOG") or a.out.with_suffix(".usage.jsonl"))
    usage_path.parent.mkdir(parents=True, exist_ok=True)
    usage_path.write_text("", encoding="utf-8")                # one log per run: evaluation/usage_report.py summarises exactly this run
    key_present = bool(os.environ.get("ANTHROPIC_API_KEY") or os.environ.get("ANTHROPIC_AUTH_TOKEN"))      # presence only, never the value
    images_by_model = st.images == "on" or (st.images == "auto" and key_present)
    images_from_file = st.images == "file"
    images_on = images_by_model or images_from_file                  # some source reads the screenshots
    readings = None
    if images_by_model:
        llm = _make_llm(st, stamp, usage_path)
        cfg.image_extractor = lambda path, system, instruction, schema, version: llm.read_image_json("image", path, system, instruction, schema, version)
    elif images_from_file:
        from auditor.readings import ReadingsError
        try:
            readings = _file_readings(st, usage_path)
        except ReadingsError as e:
            print(f"error: {e}", file=sys.stderr)
            return 2
        stamp["readings"] = readings.describe()
        cfg.image_extractor = lambda path, system, instruction, schema, version: readings.read_image_json("image", path, system, instruction, schema, version)
    auditor = Auditor(a.dataset, cfg)
    ds = load_dataset(a.dataset, a.requests)
    # Finished cases are appended to <out>.partial.jsonl as they complete. If this run is killed, the next run with the same inputs, versions and settings
    # (the run key, see auditor/checkpoint.py) reads the file and audits only the cases that are missing; any other run discards it.
    key = run_key(a.dataset, a.requests, stamp, images_on, image_observation_digest(auditor))
    ckpt = Checkpoint(a.out.with_suffix(".partial.jsonl"), key, restart=a.restart)
    results, reused = audit_all(auditor, ds.case_order, ckpt)
    print(f"checkpoint {ckpt.path.name}: {ckpt.how}; {reused} of {len(results)} cases reused, {len(results) - reused} audited", file=sys.stderr)
    write_output(a.out, [(x.case_id, x.status, x.steps) for x in results])
    issues = validate_output_file(a.out, ds)
    errors = [i for i in issues if i.severity == "error"]

    import hashlib
    from auditor.llm import UsageLog
    UsageLog(usage_path).add({"kind": "run_info", "images_read_by_model": images_by_model, "images_read_from_file": images_from_file,
                              "readings_file": readings.describe() if readings else "", "api_key_present": key_present, "requests": len(results), "output_file": a.out.name,
                              "output_sha256": hashlib.sha256(a.out.read_bytes()).hexdigest(), "status_counts": dict(Counter(x.status for x in results)),
                              "provisional_rows": [x.case_id for x in results if x.provisional], "stamp": stamp,
                              "checkpoint": ckpt.how, "cases_reused_from_checkpoint": reused})
    notes = [f"# Run notes for {a.out.name}", "", "## Version stamp (what produced this file)", "", *versions.stamp_lines(stamp), "",
             "Effective settings: " + ", ".join(f"`{k}={v}`" for k, v in cfgmod.as_table(st)), "",
             f"cases: {len(results)}, status counts: {dict(Counter(x.status for x in results))}",
             f"evidence warnings: {len(auditor.base.warnings)}", ""]
    notes += [f"* {w}" for w in auditor.base.warnings[:50]]
    notes += [f"* {w}" for w in auditor.res.warnings[:20]]
    notes += [f"* request file line {b.line} ({b.case_id or 'no case_id'}): {b.reason}" for b in ds.bad_rows]
    provisional = [x for x in results if x.provisional]
    notes += ["", f"## Provisional rows ({len(provisional)}): built from structured data because the only state evidence is a screenshot that was not read", ""]
    notes += [f"* {x.case_id}: {x.status}, {len(x.steps)} steps - " + "; ".join(n for n in x.notes if n.startswith("PROVISIONAL")) for x in provisional]
    notes += ["", "## Per-case notes (assumptions, fallbacks)", ""]
    notes += [f"* {x.case_id}: {n}" for x in results for n in x.notes]
    notes += ["", "## Later-evidence links (D3: evidence captured after audit_at used ONLY to link a resource to the user)", ""]
    notes += [f"* {x.case_id}: {rid} linked via {fid} (captured {t})" for x in results for rid, fid, t in x.later_links]
    a.out.with_suffix(".notes.md").write_text("\n".join(notes) + "\n", encoding="utf-8")

    fallbacks = sum(1 for x in results for n in x.notes if n.startswith("FALLBACK"))
    print(f"wrote {a.out} ({len(results)} rows); status counts {dict(Counter(x.status for x in results))}; "
          f"fallbacks {fallbacks}; provisional {len(provisional)} {[x.case_id for x in provisional]}; validator errors {len(errors)}", file=sys.stderr)
    for i in errors[:10]:
        print(i, file=sys.stderr)
    ckpt.close(remove=not errors and not a.keep_checkpoint)          # on validator errors the file is kept for inspection
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
