"""Hold-out measurement of the timeline discovery (accounts and anonymized analytics), on the participant data only.

The discovery finds resources that carry no identifier. Its precision cannot be read off labeled answers for most cases,
so it is measured the other way round: take cases whose resource IS linked explicitly (that link is the label), hide the
link for a random share of them, run the real discovery code, and check what it assigns.

Every hidden case is scored as one of
  correct     the discovery assigned exactly the hidden resource
  wrong       it assigned another resource to the hidden case
  stolen      it assigned nothing to the hidden case AND gave the hidden case's resource to a different case
              (the failure a per-case score would miss: the taker is wrong, the owner is left without)
  omitted     it assigned nothing (ambiguous or no candidate)
Precision of what is assigned = correct / (correct + wrong + stolen-takers); `stolen` is reported separately.
"""
from __future__ import annotations

import random
from collections import Counter
from dataclasses import dataclass, field

from auditor import discover as dsc


@dataclass
class HoldoutResult:
    n: int = 0
    counts: Counter = field(default_factory=Counter)
    by_method: Counter = field(default_factory=Counter)

    @property
    def assigned(self) -> int:
        return self.counts["correct"] + self.counts["wrong"]

    def precision(self) -> float:
        taken_wrong = self.counts["wrong"] + self.counts["stolen"]
        denom = self.counts["correct"] + taken_wrong
        return self.counts["correct"] / denom if denom else 1.0

    def line(self, name: str) -> str:
        c = self.counts
        n = max(self.n, 1)
        return (f"| {name} | {self.n} | {c['correct']} ({100 * c['correct'] / n:.1f}%) | {c['wrong']} | {c['stolen']} | {c['omitted']} | "
                f"**{100 * self.precision():.1f}%** |")


HEADER = ["| setting | hidden cases | correct | wrong | stolen | omitted | precision of assignments |", "|---|---|---|---|---|---|---|"]


def _account_truth(aud) -> dict[str, str]:
    """case -> its explicitly linked account, for cases with one linked account and a worker delete event in the window."""
    cases, explicit, *_ = aud.disc_inputs
    dele: dict[str, list] = {}
    for e in aud.events:
        if e.event_type == "DELETE_RECORD" and e.source == "deletion_worker" and e.target == "account_service":
            dele.setdefault(e.resource, []).append(e)
    out = {}
    for c in cases:
        ex = explicit.get(c.case_id)
        if ex and len(ex) == 1:
            a = next(iter(ex))
            if any(dsc._in_window(e, c.reported_at, aud.cfg.tune.account_window) for e in dele.get(a, [])):
                out[c.case_id] = a
    return out


def account_holdout(aud, fraction: float, seeds: range, use_cohort: bool = False, use_timing_heuristics: bool = True) -> HoldoutResult:
    cases, explicit, owners, cohort, own = aud.disc_inputs
    keys = {c.case_id: c for c in cases}
    truth_all = _account_truth(aud)
    res = HoldoutResult()
    for seed in seeds:
        rnd = random.Random(seed)
        hold = sorted(rnd.sample(sorted(truth_all), int(fraction * len(truth_all))))
        ex2 = {k: (set() if k in hold else set(v)) for k, v in explicit.items()}
        ow2 = {k: set(v) for k, v in owners.items()}
        for h in hold:
            acct = truth_all[h]
            ow2[acct] = ow2.get(acct, set()) - set(keys[h].users)
            if not ow2[acct]:
                del ow2[acct]
        out = dsc.assign_accounts(cases, aud.events, ex2, ow2, cohort if use_cohort else None, own if use_timing_heuristics else None, aud.cfg.tune)
        taker_of = {a.resource: cid for cid, a in out.items() if a.resource and a.how != "explicit"}
        for h in hold:
            a, truth = out[h], truth_all[h]
            res.n += 1
            if a.resource == truth:
                res.counts["correct"] += 1
                res.by_method[a.how] += 1
            elif a.resource:
                res.counts["wrong"] += 1
            elif taker_of.get(truth) not in (None, h):
                res.counts["stolen"] += 1
            else:
                res.counts["omitted"] += 1
    return res


def analytics_holdout(aud, fraction: float, seeds: range, use_timing_heuristics: bool = True) -> HoldoutResult:
    """Hide the chain-resolved analytics assignments (the most reliable ones) and see whether the phase / elimination stages recover them."""
    cs, ev, anon, chain, foreign, acct_t, tune = aud.analytics_inputs
    truth_all = {cid: a.resource for cid, a in aud.analytics_asg.items() if a.how == "chain"}
    res = HoldoutResult()
    if not truth_all:
        return res
    for seed in seeds:
        rnd = random.Random(seed)
        hold = sorted(rnd.sample(sorted(truth_all), max(1, int(fraction * len(truth_all)))))
        ch = {k: ([] if k in hold else v) for k, v in chain.items()}
        out = dsc.assign_anonymized_analytics(cs, ev, anon, ch, foreign, acct_t if use_timing_heuristics else None, tune)
        taker_of = {a.resource: cid for cid, a in out.items() if a.resource}
        for h in hold:
            a, truth = out[h], truth_all[h]
            res.n += 1
            if a.resource == truth:
                res.counts["correct"] += 1
                res.by_method[a.how] += 1
            elif a.resource:
                res.counts["wrong"] += 1
            elif taker_of.get(truth) not in (None, h):
                res.counts["stolen"] += 1
            else:
                res.counts["omitted"] += 1
    return res


def f1_effect_of_guessing(pred_steps: int, tp: float, gold_steps: float, rows_by_k: dict[int, int]) -> list[tuple[str, float, float]]:
    """Expected micro step-F1 when an extra account step is guessed uniformly among k candidates (k rows each), versus omitting it.
    Adding one step with success probability p changes TP by p and the number of predicted steps by 1."""
    def f1(tp_, p_, g_):
        return 2 * tp_ / (p_ + g_) if (p_ + g_) else 0.0
    base = f1(tp, pred_steps, gold_steps)
    out = [("omit all", base, 0.0)]
    for k, n in sorted(rows_by_k.items()):
        out.append((f"guess the {n} rows with k={k} (p=1/{k})", f1(tp + n / k, pred_steps + n, gold_steps), f1(tp + n / k, pred_steps + n, gold_steps) - base))
    all_tp = tp + sum(n / k for k, n in rows_by_k.items())
    all_p = pred_steps + sum(rows_by_k.values())
    out.append(("guess every ambiguous row", f1(all_tp, all_p, gold_steps), f1(all_tp, all_p, gold_steps) - base))
    return out
