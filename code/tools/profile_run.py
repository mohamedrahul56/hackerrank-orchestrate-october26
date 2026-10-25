#!/usr/bin/env python3
"""Profile the whole run: wall time per stage, peak memory, and the functions that dominate.

    python3 code/tools/profile_run.py                        # the real 518 requests (+ the 32 samples, which the auditor always loads)
    python3 code/tools/profile_run.py --repeat 20            # every request row repeated 20 times under new case ids
    python3 code/tools/profile_run.py --repeat 1 2 5 10 20 --table    # scaling table, one line per scale (fresh process per scale)
    python3 code/tools/profile_run.py --cprofile 12          # also print the 12 most expensive functions (adds profiler overhead)

Stages are the Auditor's own start-up stages plus the per-case loop and the write+validate step of main.py. Peak memory is the process's peak resident set size
(ru_maxrss) measured in a fresh process; `--tracemalloc` adds the peak of Python allocations (slower). Scaled inputs are built in a temporary folder: every file of
dataset/ is linked unchanged except deletion_requests.csv, whose rows are repeated K times with case ids `<id>__rK`. The repeated rows are NOT independent cases (same
person, same evidence), so their ANSWERS differ from the real run (they compete for the same free accounts); only the cost is meaningful.
"""
from __future__ import annotations

import argparse
import csv
import json
import resource
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "code"))


def scaled_dataset(src: Path, dst: Path, repeat: int) -> Path:
    """dst/ links every file of src unchanged except deletion_requests.csv, which holds each row `repeat` times (case ids suffixed __rK, K >= 2)."""
    dst.mkdir(parents=True, exist_ok=True)
    for p in src.iterdir():
        if p.name != "deletion_requests.csv":
            (dst / p.name).symlink_to(p.resolve())
    with (src / "deletion_requests.csv").open(newline="", encoding="utf-8") as fh:
        reader = csv.DictReader(fh)
        rows = list(reader)
        header = reader.fieldnames
    with (dst / "deletion_requests.csv").open("w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=header, lineterminator="\n")
        w.writeheader()
        for k in range(1, repeat + 1):
            for r in rows:
                w.writerow({**r, "case_id": r["case_id"] if k == 1 else f"{r['case_id']}__r{k}"})
    return dst


def one_run(dataset: Path, tracemalloc_on: bool, cprofile_n: int) -> dict:
    """The work of `python3 code/main.py`, stage by stage, in THIS process."""
    import tracemalloc
    from auditor.audit import Auditor, Config
    from auditor.dataset import load_dataset
    from auditor.outio import write_output
    from auditor.validate import validate_output_file
    stages: dict[str, float] = {}
    if tracemalloc_on:
        tracemalloc.start()
    prof = None
    if cprofile_n:
        import cProfile
        prof = cProfile.Profile()
        prof.enable()
    t_all = time.perf_counter()
    t = time.perf_counter()
    aud = Auditor(dataset, Config())
    stages["auditor_start_up_total"] = time.perf_counter() - t
    peak_start = tracemalloc.get_traced_memory()[1] if tracemalloc_on else 0
    t = time.perf_counter()
    ds = load_dataset(dataset)
    stages["load_dataset_for_validation"] = time.perf_counter() - t
    t = time.perf_counter()
    results = [aud.audit(cid) for cid in ds.case_order]
    stages["audit_cases_loop"] = time.perf_counter() - t
    with tempfile.TemporaryDirectory() as d:
        out = Path(d) / "output.csv"
        t = time.perf_counter()
        write_output(out, [(x.case_id, x.status, x.steps) for x in results])
        stages["write_output"] = time.perf_counter() - t
        import hashlib
        out_sha = hashlib.sha256(out.read_bytes()).hexdigest()
        t = time.perf_counter()
        errors = [i for i in validate_output_file(out, ds) if i.severity == "error"]
        stages["validate_output"] = time.perf_counter() - t
    total = time.perf_counter() - t_all
    if prof:
        prof.disable()
    peak_alloc = tracemalloc.get_traced_memory()[1] if tracemalloc_on else None
    top = []
    if prof:
        import io
        import pstats
        s = io.StringIO()
        pstats.Stats(prof, stream=s).sort_stats("cumulative").print_stats(cprofile_n)
        top = [l for l in s.getvalue().splitlines() if l.strip()][-cprofile_n - 3:]
    from collections import Counter
    return {"cases": len(results), "total_s": total, "stages": {**{k: v for k, v in aud.timings.items()}, **stages},
            "peak_rss_mb": resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024, "peak_tracemalloc_mb": (peak_alloc / 1e6) if peak_alloc is not None else None,
            "status_counts": dict(Counter(x.status for x in results)), "output_sha256": out_sha, "validator_errors": len(errors), "profile_top": top, "peak_alloc_after_start_up_mb": peak_start / 1e6}


def fmt(r: dict) -> str:
    lines = [f"cases audited: {r['cases']}   total wall time: {r['total_s']:.2f} s   peak RSS: {r['peak_rss_mb']:.0f} MB"
             + (f"   peak Python allocations: {r['peak_tracemalloc_mb']:.0f} MB" if r["peak_tracemalloc_mb"] is not None else "")
             + f"   statuses: {r['status_counts']}   validator errors: {r['validator_errors']}", ""]
    top_level = ["identity", "evidence_parse", "link", "activity_index", "classify_requests", "resolve_identities", "discovery_total", "load_dataset_for_validation", "audit_cases_loop", "write_output", "validate_output"]
    lines.append(f"{'stage':<34}{'seconds':>10}{'share':>8}")
    for k in top_level:
        if k in r["stages"]:
            lines.append(f"{k:<34}{r['stages'][k]:>10.3f}{100 * r['stages'][k] / r['total_s']:>7.1f}%")
    for k in sorted(k for k in r["stages"] if k.startswith("discovery.")):
        lines.append(f"  {k:<32}{r['stages'][k]:>10.3f}{100 * r['stages'][k] / r['total_s']:>7.1f}%")
    if r["profile_top"]:
        lines += ["", "most expensive functions (cumulative):"] + ["  " + l for l in r["profile_top"]]
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--repeat", type=int, nargs="*", default=[1], help="repeat every request row K times (one or several scales)")
    ap.add_argument("--table", action="store_true", help="print one summary line per scale")
    ap.add_argument("--tracemalloc", action="store_true")
    ap.add_argument("--cprofile", type=int, default=0, metavar="N")
    ap.add_argument("--runs", type=int, default=1, help="fresh-process runs per scale; the run with the median total time is reported (all totals are kept in the JSON)")
    ap.add_argument("--save", type=Path, help="write all results as JSON to this file")
    ap.add_argument("--json", action="store_true", help="(internal) print the result of one in-process run as JSON")
    ap.add_argument("--dataset", type=Path, default=ROOT / "dataset")
    a = ap.parse_args(argv)
    if a.json:                                          # child mode: one scale, one fresh process
        with tempfile.TemporaryDirectory() as d:
            ds = scaled_dataset(a.dataset, Path(d) / "ds", a.repeat[0]) if a.repeat[0] > 1 else a.dataset
            print(json.dumps(one_run(ds, a.tracemalloc, a.cprofile)))
        return 0
    results = []
    for k in a.repeat:                                  # a fresh process per scale so that peak memory is that scale's own
        cmd = [sys.executable, __file__, "--json", "--repeat", str(k), "--dataset", str(a.dataset)] + (["--tracemalloc"] if a.tracemalloc else []) + (["--cprofile", str(a.cprofile)] if a.cprofile else [])
        runs = []
        for _ in range(max(1, a.runs)):
            p = subprocess.run(cmd, capture_output=True, text=True)
            if p.returncode != 0:
                print(p.stderr[-2000:])
                return 1
            runs.append(json.loads(p.stdout.strip().splitlines()[-1]))
        if len({x["output_sha256"] for x in runs}) != 1:
            print(f"error: x{k}: the runs produced different output files")
            return 1
        runs.sort(key=lambda x: x["total_s"])
        r = runs[len(runs) // 2]
        r["runs_total_s"] = [round(x["total_s"], 3) for x in runs]
        r["repeat"] = k
        results.append(r)
        if not a.table:
            print(f"=== repeat x{k}\n{fmt(r)}\n")
    if a.save:
        a.save.write_text(json.dumps(results, indent=1), encoding="utf-8")
    if a.table or len(results) > 1:
        print(summary_table(results))
    return 0


def summary_table(results: list[dict]) -> str:
    base = results[0]
    rows = ["| repeat | cases | total s | start-up s | discovery s | cases loop s | validate s | peak RSS MB | growth vs first row (time / cases) | output sha256 |", "|---|---|---|---|---|---|---|---|---|---|"]
    for r in results:
        st = r["stages"]
        rows.append(f"| x{r['repeat']} | {r['cases']} | {r['total_s']:.2f} | {st['auditor_start_up_total']:.2f} | {st.get('discovery_total', 0):.2f} | {st['audit_cases_loop']:.2f} | {st['validate_output']:.2f} | "
                    f"{r['peak_rss_mb']:.0f} | {r['total_s'] / base['total_s']:.1f}x / {r['cases'] / base['cases']:.1f}x | `{r['output_sha256'][:10]}` |")
    return "\n".join(rows)


if __name__ == "__main__":
    raise SystemExit(main())
