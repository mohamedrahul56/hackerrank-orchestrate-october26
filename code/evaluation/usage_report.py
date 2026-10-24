#!/usr/bin/env python3
"""Model usage report from the usage log written by auditor/llm.py.

  python3 code/evaluation/usage_report.py                       # preview -> code/out/usage_report_preview.md (git-ignored)
  python3 code/evaluation/usage_report.py --final               # -> code/evaluation/usage_report.md (the submission file)

The submission file must come from the final full run with a COLD cache (delete AUDITOR_CACHE_DIR, point AUDITOR_USAGE_LOG at a fresh
file, run `python3 code/main.py`, then this script with --final). The report says so, and flags a log that contains cache hits.
Costs are computed only from the user's price file (auditor/prices.py: rates + source URL + date); otherwise the report states
"cost not computed - prices not supplied". Nothing here invents a price.
"""
from __future__ import annotations

import argparse
import collections
import csv
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "code"))
from auditor import prices as price_mod  # noqa: E402

DEFAULT_LOG = ROOT / "code" / "out" / "usage_log.jsonl"
FINAL = ROOT / "code" / "evaluation" / "usage_report.md"
PREVIEW = ROOT / "code" / "out" / "usage_report_preview.md"


def read_log(path: Path) -> list[dict]:
    if not path.exists():
        return []
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.strip():
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError:
                pass                                              # a torn last line must not break the report
    return rows


def summarise(rows: list[dict], n_requests: int, prices: dict, price_problems: list[str], log_path: str = "") -> str:
    live = [r for r in rows if not r.get("cache_hit") and not r.get("error")]
    hits = [r for r in rows if r.get("cache_hit")]
    failed = [r for r in rows if r.get("error")]
    L = ["# Usage report", "",
         f"Source: the model-usage log of the final full run (`{log_path or 'AUDITOR_USAGE_LOG'}`), {n_requests} requests in `dataset/deletion_requests.csv`.",
         "Everything below is computed from that log; no figure is typed in by hand.", ""]
    if hits:
        L += [f"> **Not a cold-cache run**: the log contains {len(hits)} cache hits. Delete the cache directory and rerun before submitting this report.", ""]
    if not rows:
        L += ["**No model calls were made in this run.** Every decision was made by deterministic code; images were not read by a model "
              "(or the usage log is missing). Total tokens: 0.", ""]
    by_model = collections.defaultdict(lambda: collections.Counter())
    by_kind = collections.defaultdict(lambda: collections.Counter())
    for r in live:
        for tbl, key in ((by_model, (r["provider"], r["model"])), (by_kind, (r["provider"], r["model"], r.get("kind", "?")))):
            c = tbl[key]
            c["requests"] += 1
            c["calls"] += int(r.get("calls", 1))
            c["input"] += int(r.get("input_tokens", 0))
            c["output"] += int(r.get("output_tokens", 0))
            c["cache_read"] += int(r.get("cache_read_input_tokens", 0))
    L += ["## Providers and models", "",
          "| provider | model | files processed | model calls (incl. re-asks) | input tokens | output tokens | total tokens | cost |", "|---|---|---|---|---|---|---|---|"]
    tot = collections.Counter()
    total_cost, priced_all = 0.0, True
    for (prov, model), c in sorted(by_model.items()):
        p = prices.get(model)
        cost = p.cost(c["input"], c["output"], c["cache_read"]) if p else None
        priced_all &= p is not None
        total_cost += cost or 0.0
        L.append(f"| {prov} | {model} | {c['requests']} | {c['calls']} | {c['input']} | {c['output']} | {c['input'] + c['output']} | "
                 f"{('$%.4f' % cost) if cost is not None else price_mod.NOT_SUPPLIED} |")
        tot.update(c)
    L.append(f"| **all models** | | {tot['requests']} | {tot['calls']} | {tot['input']} | {tot['output']} | {tot['input'] + tot['output']} | "
             f"{('$%.4f' % total_cost) if (by_model and priced_all) else price_mod.NOT_SUPPLIED} |")
    L += ["", "## By purpose", "", "| provider | model | purpose | files | calls | input tokens | output tokens |", "|---|---|---|---|---|---|---|"]
    for (prov, model, kind), c in sorted(by_kind.items()):
        L.append(f"| {prov} | {model} | {kind} | {c['requests']} | {c['calls']} | {c['input']} | {c['output']} |")
    if not by_kind:
        L.append("| - | - | - | 0 | 0 | 0 | 0 |")
    tokens = tot["input"] + tot["output"]
    L += ["", "## Per request", "",
          f"* total tokens: **{tokens}**; average per request: **{tokens / n_requests:.1f}** ({n_requests} requests; a model is called only to read a screenshot, or for a request whose wording the rule-based classifier finds ambiguous)",
          f"* model calls per request: {tot['calls'] / n_requests:.3f}",
          "* estimated total cost: " + (f"**${total_cost:.4f}**" if (by_model and priced_all) else f"**{price_mod.NOT_SUPPLIED}**"),
          "* estimated cost per request: " + (f"**${total_cost / n_requests:.6f}**" if (by_model and priced_all) else f"**{price_mod.NOT_SUPPLIED}**"), ""]
    L += ["## Prices", ""]
    if prices:
        L += [f"* {m}: input {p.input_per_mtok}/Mtok, output {p.output_per_mtok}/Mtok" + (f", cached input {p.cache_read_per_mtok}/Mtok" if p.cache_read_per_mtok is not None else "")
              + f" - source {p.source_url}, dated {p.date} (supplied by the participant)" for m, p in sorted(prices.items())]
    else:
        L += [f"* {price_mod.NOT_SUPPLIED}." + ("  " + "; ".join(price_problems).replace(str(ROOT) + "/", "") if price_problems else "")]
    if by_model and not priced_all and prices:
        L += [f"* models without a complete price entry: {sorted(set(m for m, _ in by_model) - set(prices))}"]
    L += ["", "## Cache and failures", "",
          f"* live calls: {len(live)}; cache hits: {len(hits)}; calls that failed schema validation twice: {len(failed)} (those images fall back to the deterministic result and are listed in the run notes)"]
    return "\n".join(L) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", type=Path, default=Path(os.environ.get("AUDITOR_USAGE_LOG", DEFAULT_LOG)))
    ap.add_argument("--requests", default="deletion_requests.csv")
    ap.add_argument("--final", action="store_true", help="write the submission file code/evaluation/usage_report.md")
    args = ap.parse_args()
    with (ROOT / "dataset" / args.requests).open(newline="", encoding="utf-8") as fh:
        n = sum(1 for _ in csv.DictReader(fh))
    prices, problems = price_mod.load()
    text = summarise(read_log(args.log), n, prices, problems, str(args.log.relative_to(ROOT)) if args.log.is_relative_to(ROOT) else args.log.name)
    out = FINAL if args.final else PREVIEW
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(text, encoding="utf-8")
    print(text)
    print(f"wrote {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
