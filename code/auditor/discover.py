"""Find the resources that carry NO identifier in any evidence: deleted accounts (a 404 with no owner) and fully
anonymized analytics records (every field null).

The only link left is the deletion worker's activity timeline. For every deletion case the worker writes a burst of
events around the case's `reported_at` (R): the account delete 45-75 minutes before R, the analytics anonymization
about 29 minutes before R (offsets measured on the data, see NOTES.md). Cases that share the same R produce
interleaved bursts, so a candidate is accepted only by ELIMINATION: remove accounts that are explicitly linked to
someone else or already assigned, and accept a case's account only when exactly one candidate remains. Ambiguous
groups stay unresolved - a guess with probability 1/k of being right is worse than omitting the step.
"""
from __future__ import annotations

import csv
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from pathlib import Path

from .dataset import parse_ts

ACCOUNT_WINDOW = (timedelta(minutes=-90), timedelta(minutes=15))      # relative to reported_at
ANALYTICS_WINDOW = (timedelta(minutes=-45), timedelta(minutes=-5))


@dataclass(frozen=True)
class Event:
    activity_id: str
    t: datetime
    event_type: str
    source: str
    target: str
    resource: str
    result: str


# The deletion worker's delete event carries the same result the workflow reports for the case (measured: 189 of 189 cases
# with an explicit account link). Used only to MATCH an event to its case, never as evidence about the data's state.
RESULT_OF_REPORTED_STATUS = {"COMPLETE": "SUCCESS", "PARTIALLY_COMPLETE": "PARTIAL", "NOT_STARTED": "FAILED"}


@dataclass
class CaseKey:
    case_id: str
    users: frozenset[str]
    reported_at: datetime
    requested_at: datetime
    audit_at: datetime
    reported_status: str = ""


def load_events(root: Path) -> list[Event]:
    with (root / "system_activity.csv").open(newline="", encoding="utf-8") as fh:
        return [Event(r["activity_id"], parse_ts(r["event_time"]), r["event_type"], r["source_system"], r["target_system"],
                      r["resource_id"], r["reported_result"]) for r in csv.DictReader(fh)]


@dataclass
class Assignment:
    resource: str | None = None
    how: str = "none"                  # explicit | eliminated | ambiguous | none
    candidates: tuple[str, ...] = ()
    event: Event | None = None


def _in_window(e: Event, R: datetime, win: tuple[timedelta, timedelta]) -> bool:
    return R + win[0] <= e.t <= R + win[1]


def assign_accounts(cases: list[CaseKey], events: list[Event], explicit: dict[str, set[str]],
                    owners: dict[str, set[str]], cohort: dict[str, set[str]] | None = None) -> dict[str, Assignment]:
    """explicit: case_id -> accounts already linked to the case's users inside its window.
    owners: account -> users it is explicitly linked to (any time).
    cohort: case_id -> accounts observed in an evidence file that also observes the case's other records (same snapshot
            = same cohort of users); used to narrow an ambiguous group."""
    deletes = [e for e in events if e.event_type == "DELETE_RECORD" and e.source == "deletion_worker" and e.target == "account_service"]
    by_account: dict[str, list[Event]] = {}
    for e in deletes:
        by_account.setdefault(e.resource, []).append(e)
    out: dict[str, Assignment] = {}
    taken: set[str] = set(owners)                                   # linked to *some* user: never a guess for another case
    for c in cases:
        if explicit.get(c.case_id):
            accts = sorted(explicit[c.case_id])
            ev = next((e for a in accts for e in by_account.get(a, [])), None)
            out[c.case_id] = Assignment(accts[0], "explicit", tuple(accts), ev)
            taken |= set(accts)
    cand: dict[str, set[str]] = {}
    for c in cases:
        if c.case_id in out:
            continue
        cand[c.case_id] = {a for a, evs in by_account.items() if a not in taken and any(_in_window(e, c.reported_at, ACCOUNT_WINDOW) for e in evs)}
    keys = {c.case_id: c for c in cases}
    # stage 1: plain elimination; stage 2: keep accounts seen in the case's cohort snapshot; stage 3: keep accounts whose delete
    # event result matches the case's reported status. Narrowing is soft: it never empties a candidate set.
    for stage in (1, 2, 3):
        if stage == 2 and cohort:
            for cid in list(cand):
                narrowed = cand[cid] & cohort.get(cid, set())
                if narrowed:
                    cand[cid] = narrowed
        if stage == 3:
            for cid in list(cand):
                want = RESULT_OF_REPORTED_STATUS.get(keys[cid].reported_status)
                narrowed = {a for a in cand[cid] if any(e.result == want and _in_window(e, keys[cid].reported_at, ACCOUNT_WINDOW) for e in by_account[a])}
                if narrowed:
                    cand[cid] = narrowed
        _eliminate(cand, out, taken, by_account)
    for cid, cs in cand.items():
        out[cid] = Assignment(None, "ambiguous" if cs else "none", tuple(sorted(cs)))
    return out


def _eliminate(cand: dict[str, set[str]], out: dict[str, "Assignment"], taken: set[str], by_account: dict[str, list[Event]]) -> None:
    changed = True
    while changed:
        changed = False
        for cid, cs in list(cand.items()):
            if len(cs) == 1:
                a = next(iter(cs))
                out[cid] = Assignment(a, "eliminated", (a,), by_account[a][0])
                del cand[cid]
                taken.add(a)
                for other in cand.values():
                    other.discard(a)
                changed = True


GAPS = (390, 449)          # seconds between consecutive deletion-worker policy actions (measured: 71 of 72 gaps)
TOL = 2


def chain_ok(delta_seconds: float, k: int) -> bool:
    """delta between a candidate ANONYMIZE event and the k-th later event of the same case's chain (support=1, flow=2, backup=3)."""
    sums = {0}
    for _ in range(k):
        sums = {x + g for x in sums for g in GAPS}
    return any(abs(delta_seconds - x) <= TOL for x in sums)


def assign_anonymized_analytics(cases: list[CaseKey], events: list[Event], anonymized: set[str],
                                chain: dict[str, list[tuple[datetime, int]]],
                                foreign_chain: "callable | None" = None) -> dict[str, Assignment]:
    """chain: case_id -> [(event time, position k)] of the case's OWN explicitly linked policy actions
    (REDACT support k=1, DISABLE flow k=2, BLOCK backup k=3).
    1) a candidate whose time fits the case's chain wins; 2) otherwise elimination: a candidate counts only when exactly
    one still-unassigned case has it in its window. `foreign_chain(case, event)` says that the candidate sits inside
    another user's completed chain (a decoy user whose deletion happened in the same hour): such a candidate is dropped."""
    anon_events = [e for e in events if e.event_type == "ANONYMIZE_RECORD" and e.source == "deletion_worker"
                   and e.target == "analytics_platform" and e.result == "SUCCESS" and e.resource in anonymized]
    cand: dict[str, dict[str, Event]] = {c.case_id: {e.resource: e for e in anon_events if _in_window(e, c.reported_at, ANALYTICS_WINDOW)
                                                      and not (foreign_chain and foreign_chain(c, e))} for c in cases}
    out: dict[str, Assignment] = {}
    taken: set[str] = set()
    for c in cases:                                              # pass 1: chain timing
        fits = {r for r, e in cand[c.case_id].items()
                if any(chain_ok((t - e.t).total_seconds(), k) for t, k in chain.get(c.case_id, []))}
        if len(fits) == 1:
            r = next(iter(fits))
            out[c.case_id] = Assignment(r, "chain", (r,), cand[c.case_id][r])
            taken.add(r)
    pending = {c.case_id for c in cases if c.case_id not in out}           # frozen: ambiguous answers must not shrink the claimant sets
    for c in cases:                                              # pass 2: elimination
        if c.case_id in out:
            continue
        rest = {r for r in cand[c.case_id] if r not in taken}
        claimants = {r: [cid for cid in pending if r in cand[cid]] for r in rest}
        if len(rest) == 1 and len(claimants[next(iter(rest))]) == 1:
            r = next(iter(rest))
            out[c.case_id] = Assignment(r, "eliminated", (r,), cand[c.case_id][r])
        else:
            out[c.case_id] = Assignment(None, "ambiguous" if rest else "none", tuple(sorted(rest)))
    return out
