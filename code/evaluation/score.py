#!/usr/bin/env python3
"""Score a predictions CSV against a labeled CSV (default: the 32 public samples).

Reports (all as Markdown so the same text goes to the terminal and to evaluation reports):
  * case-level deletion_status accuracy + confusion matrix
  * step-level precision / recall / F1   (strict = all 6 content fields, relaxed = ignoring evidence_id)
  * resource-discovery P/R/F1            ((system_id, resource_id) pairs, i.e. "did we find the records")
  * field-level accuracy per field       (gold steps aligned to predicted steps, see `align`)
  * sequence accuracy                    (exact ordered match of the whole row, and mean LCS ratio)
  * per-case failure listing with a readable diff

Dev / not-tuned split: the 32 labeled samples are split deterministically (stratified by status and by
the set of systems involved, tie-broken by a hash of case_id; no case_id literals). Failure diffs are
only printed for dev cases; not-tuned cases only contribute to aggregate numbers unless --reveal-heldout.

Usage:  python3 code/evaluation/score.py --pred out/pred.csv [--split dev|heldout|all   (heldout = the 9 cases not tuned against in Phase 2-3)] [--markdown report.md]
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "code"))
from auditor.schema import STATUSES, STEP_FIELDS  # noqa: E402

CONTENT_FIELDS = ("phase", "system_id", "resource_id", "operation", "state", "evidence_id")
FIELDS = STEP_FIELDS  # step_no + the six content fields

Steps = list[dict[str, Any]]


@dataclass
class Row:
    status: str
    steps: Steps
    error: str = ""  # JSON / shape problem found while reading


def read_rows(path: Path) -> dict[str, Row]:
    """case_id -> Row. Unparseable steps become [] with `error` set (the scorer never crashes on bad input)."""
    out: dict[str, Row] = {}
    with path.open(newline="", encoding="utf-8") as fh:
        for r in csv.DictReader(fh):
            err, steps = "", []
            try:
                parsed = json.loads(r.get("steps_json", ""))
                if isinstance(parsed, list) and all(isinstance(s, dict) for s in parsed):
                    steps = parsed
                else:
                    err = "steps_json is not an array of objects"
            except json.JSONDecodeError as e:
                err = f"bad JSON: {e}"
            out[r["case_id"]] = Row(r.get("deletion_status", ""), steps, err)
    return out


# ----------------------------------------------------------------------------- split
def assign_splits(gold: dict[str, Row]) -> dict[str, str]:
    """Deterministic ~2/3 dev, ~1/3 not tuned against. Stratum = (status, size bucket of the answer) so both halves
    contain simple and complex cases of each status; inside a stratum cases are ordered by sha256(case_id)
    and every third one goes to the not-tuned group. (An earlier draft stratified by the exact set of systems; that made
    almost every stratum a singleton and left only 4 not-tuned cases - too few to say anything.)"""
    def bucket(n: int) -> str:
        return "small" if n <= 3 else "medium" if n <= 8 else "large"

    strata: dict[tuple, list[str]] = defaultdict(list)
    for cid, row in gold.items():
        strata[(row.status, bucket(len(row.steps)))].append(cid)
    split: dict[str, str] = {}
    for members in strata.values():
        members.sort(key=lambda c: hashlib.sha256(c.encode()).hexdigest())
        for i, cid in enumerate(members):
            split[cid] = "heldout" if i % 3 == 2 else "dev"
    return split


# ----------------------------------------------------------------------------- step helpers
def full(s: dict[str, Any]) -> tuple:
    return tuple(s.get(f, "") for f in CONTENT_FIELDS)


def relaxed(s: dict[str, Any]) -> tuple:
    return tuple(s.get(f, "") for f in CONTENT_FIELDS if f != "evidence_id")


def resource(s: dict[str, Any]) -> tuple:
    return (s.get("system_id", ""), s.get("resource_id", ""))


def lcs_len(a: list, b: list) -> int:
    prev = [0] * (len(b) + 1)
    for x in a:
        cur = [0]
        for j, y in enumerate(b, start=1):
            cur.append(prev[j - 1] + 1 if x == y else max(prev[j], cur[-1]))
        prev = cur
    return prev[-1]


def lcs_ratio(a: list, b: list) -> float:
    return 1.0 if not a and not b else 2 * lcs_len(a, b) / (len(a) + len(b))


def prf(tp: int, n_pred: int, n_gold: int) -> tuple[float, float, float]:
    p = tp / n_pred if n_pred else (1.0 if not n_gold else 0.0)
    r = tp / n_gold if n_gold else (1.0 if not n_pred else 0.0)
    return p, r, (2 * p * r / (p + r) if p + r else 0.0)


def multiset_tp(g: list[tuple], p: list[tuple]) -> int:
    return sum((Counter(g) & Counter(p)).values())


def align(gold: Steps, pred: Steps) -> list[dict[str, Any] | None]:
    """For each gold step find the predicted step it most plausibly corresponds to, so per-field
    accuracy is not destroyed by one missing step shifting every later index.
    Greedy tiers, each predicted step used once: exact content, then (phase, system, resource, op),
    then (phase, resource), then (resource), then (phase, operation)."""
    tiers = (
        lambda s: full(s),
        lambda s: (s.get("phase"), s.get("system_id"), s.get("resource_id"), s.get("operation")),
        lambda s: (s.get("phase"), s.get("resource_id")),
        lambda s: (s.get("resource_id"),),
        lambda s: (s.get("phase"), s.get("operation")),
    )
    match: list[dict[str, Any] | None] = [None] * len(gold)
    used: set[int] = set()
    for key in tiers:
        for gi, g in enumerate(gold):
            if match[gi] is not None:
                continue
            for pi, p in enumerate(pred):
                if pi not in used and key(g) == key(p):
                    match[gi] = p
                    used.add(pi)
                    break
    return match


# ----------------------------------------------------------------------------- metrics
@dataclass
class CaseResult:
    case_id: str
    gold_status: str
    pred_status: str
    exact: bool
    lcs_full: float
    lcs_relaxed: float
    f1_full: float
    missing: Steps = field(default_factory=list)   # in gold, not predicted (strict)
    extra: Steps = field(default_factory=list)     # predicted, not in gold (strict)
    order_only: bool = False                       # same multiset of steps, different order
    error: str = ""
    present: bool = True


@dataclass
class Metrics:
    n_cases: int
    status_correct: int
    confusion: dict[tuple[str, str], int]
    step_strict: tuple[float, float, float]
    step_relaxed: tuple[float, float, float]
    discovery: tuple[float, float, float]
    field_acc: dict[str, float]
    n_gold_steps: int
    exact_cases: int
    mean_lcs_full: float
    mean_lcs_relaxed: float
    cases: list[CaseResult]
    missing_cases: int = 0
    bad_rows: int = 0


def score(gold: dict[str, Row], pred: dict[str, Row], case_ids: list[str]) -> Metrics:
    conf: Counter = Counter()
    tp_f = tp_r = tp_d = np_f = ng_f = np_d = ng_d = 0
    field_ok: Counter = Counter()
    n_gold_steps = 0
    results: list[CaseResult] = []
    missing_cases = bad_rows = 0
    for cid in case_ids:
        g = gold[cid]
        p = pred.get(cid)
        present = p is not None
        if p is None:
            p = Row("", [])
            missing_cases += 1
        if p.error:
            bad_rows += 1
        conf[(g.status, p.status or "(missing)")] += 1
        gf, pf = [full(s) for s in g.steps], [full(s) for s in p.steps]
        gr, pr = [relaxed(s) for s in g.steps], [relaxed(s) for s in p.steps]
        gd, pd = {resource(s) for s in g.steps}, {resource(s) for s in p.steps}
        tp_f += multiset_tp(gf, pf); np_f += len(pf); ng_f += len(gf)
        tp_r += multiset_tp(gr, pr)
        tp_d += len(gd & pd); np_d += len(pd); ng_d += len(gd)
        for gs, ps in zip(g.steps, align(g.steps, p.steps)):
            n_gold_steps += 1
            for f in FIELDS:
                if ps is not None and ps.get(f) == gs.get(f):
                    field_ok[f] += 1
        exact = g.status == p.status and g.steps == p.steps
        c_missing = list((Counter(gf) - Counter(pf)).elements())
        c_extra = list((Counter(pf) - Counter(gf)).elements())
        results.append(CaseResult(
            cid, g.status, p.status, exact, lcs_ratio(gf, pf), lcs_ratio(gr, pr),
            prf(multiset_tp(gf, pf), len(pf), len(gf))[2],
            missing=[dict(zip(CONTENT_FIELDS, t)) for t in c_missing],
            extra=[dict(zip(CONTENT_FIELDS, t)) for t in c_extra],
            order_only=(not c_missing and not c_extra and gf != pf),
            error=p.error, present=present))
    n = len(case_ids)
    return Metrics(
        n_cases=n,
        status_correct=sum(conf[(s, s)] for s in STATUSES),
        confusion=dict(conf),
        step_strict=prf(tp_f, np_f, ng_f), step_relaxed=prf(tp_r, np_f, ng_f), discovery=prf(tp_d, np_d, ng_d),
        field_acc={f: (field_ok[f] / n_gold_steps if n_gold_steps else 1.0) for f in FIELDS},
        n_gold_steps=n_gold_steps,
        exact_cases=sum(r.exact for r in results),
        mean_lcs_full=sum(r.lcs_full for r in results) / n if n else 1.0,
        mean_lcs_relaxed=sum(r.lcs_relaxed for r in results) / n if n else 1.0,
        cases=results, missing_cases=missing_cases, bad_rows=bad_rows)


# ----------------------------------------------------------------------------- report
def pct(x: float) -> str:
    return f"{100 * x:.1f}%"


def _step_str(s: dict[str, Any]) -> str:
    return f"{s['phase'][:4]} {s['operation']} {s['system_id']}/{s['resource_id']}" + (f" @{s['evidence_id']}" if s["evidence_id"] else "")


def summary_lines(m: Metrics, title: str) -> list[str]:
    L = [f"### {title}", "", f"* cases: **{m.n_cases}**, gold steps: **{m.n_gold_steps}**"
         + (f", missing prediction rows: {m.missing_cases}" if m.missing_cases else "")
         + (f", unparseable steps_json: {m.bad_rows}" if m.bad_rows else "")]
    L.append(f"* case-level status accuracy: **{m.status_correct}/{m.n_cases} = {pct(m.status_correct / m.n_cases if m.n_cases else 1)}**")
    p, r, f1 = m.step_strict
    L.append(f"* step-level strict (all 6 content fields): P {pct(p)} / R {pct(r)} / **F1 {pct(f1)}**")
    p, r, f1 = m.step_relaxed
    L.append(f"* step-level relaxed (ignoring evidence_id): P {pct(p)} / R {pct(r)} / F1 {pct(f1)}")
    p, r, f1 = m.discovery
    L.append(f"* resource discovery ((system_id, resource_id) pairs): P {pct(p)} / R {pct(r)} / F1 {pct(f1)}")
    L.append(f"* sequence: exact ordered match **{m.exact_cases}/{m.n_cases} = {pct(m.exact_cases / m.n_cases if m.n_cases else 1)}**; "
             f"mean LCS ratio strict {pct(m.mean_lcs_full)}, relaxed {pct(m.mean_lcs_relaxed)}")
    L += ["", "| field | accuracy over gold steps |", "|---|---|"]
    L += [f"| {f} | {pct(a)} |" for f, a in m.field_acc.items()]
    labels = list(STATUSES) + ["(missing)"]
    L += ["", "Confusion matrix (rows = gold, columns = predicted):", "",
          "| gold \\ pred | " + " | ".join(labels) + " |", "|---|" + "---|" * len(labels)]
    for g in STATUSES:
        L.append(f"| {g} | " + " | ".join(str(m.confusion.get((g, p), 0)) for p in labels) + " |")
    return L


def failure_lines(m: Metrics, limit: int, only: set[str] | None = None) -> list[str]:
    L = ["", "#### Per-case failures", ""]
    shown = 0
    for r in m.cases:
        if r.exact or (only is not None and r.case_id not in only):
            continue
        shown += 1
        if shown > limit:
            L.append(f"... {sum(1 for x in m.cases if not x.exact) - limit} more not shown (use --failures N)")
            break
        head = f"* **{r.case_id}** gold={r.gold_status} pred={r.pred_status or '(missing)'} step-F1={pct(r.f1_full)} LCS={pct(r.lcs_full)}"
        if r.error:
            head += f" ERROR: {r.error}"
        L.append(head)
        if r.order_only:
            L.append("  * same steps, wrong ORDER")
        for s in r.missing:
            L.append(f"  * - missing: {_step_str(s)}")
        for s in r.extra:
            L.append(f"  * + extra:   {_step_str(s)}")
    if shown == 0:
        L.append("(none)")
    return L


def build_report(gold, pred, split_arg: str, failures: int, reveal_heldout: bool, label: str) -> tuple[str, dict[str, Metrics]]:
    splits = assign_splits(gold)
    dev = sorted(c for c in gold if splits[c] == "dev")
    held = sorted(c for c in gold if splits[c] == "heldout")
    allc = sorted(gold)
    out = [f"## Score: {label}", "", f"labeled cases {len(allc)} = dev {len(dev)} + not-tuned-against-in-Phase-2-3 {len(held)} "
           "(split is deterministic and stratified; see assign_splits)", ""]
    ms: dict[str, Metrics] = {}
    wanted = {"all": [("all", allc)], "dev": [("dev", dev)], "heldout": [("not-tuned", held)],
              "both": [("dev", dev), ("not-tuned", held), ("all", allc)]}[split_arg]
    for name, ids in wanted:
        m = score(gold, pred, ids)
        ms[name] = m
        out += summary_lines(m, f"{name} ({len(ids)} cases)")
        if name == "dev" or (name == "not-tuned" and reveal_heldout) or (name == "all" and split_arg == "all"):
            only = set(dev) if name == "all" and not reveal_heldout else None
            out += failure_lines(m, failures, only)
        elif name == "not-tuned":
            out += ["", "#### Per-case failures", "", "(hidden on purpose: the not-tuned cases are only used for the aggregate number)"]
        out.append("")
    return "\n".join(out), ms


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--pred", type=Path, required=True)
    ap.add_argument("--gold", type=Path, default=ROOT / "dataset" / "sample_output.csv")
    ap.add_argument("--split", choices=["dev", "heldout", "all", "both"], default="both")
    ap.add_argument("--failures", type=int, default=20, help="max failing cases to list")
    ap.add_argument("--reveal-heldout", action="store_true", help="also list failures of the not-tuned cases (do not use while tuning)")
    ap.add_argument("--markdown", type=Path, help="also write the report here")
    ap.add_argument("--label", default="")
    a = ap.parse_args(argv)
    gold, pred = read_rows(a.gold), read_rows(a.pred)
    text, _ = build_report(gold, pred, a.split, a.failures, a.reveal_heldout, a.label or a.pred.name)
    print(text)
    if a.markdown:
        a.markdown.parent.mkdir(parents=True, exist_ok=True)
        a.markdown.write_text(text + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
