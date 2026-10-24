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
import math
from collections import Counter, defaultdict
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


class PhaseModel:
    """Seconds-phase regularity inside one case.

    Measured on the accounts that ARE linked explicitly (188 cases): for a given event kind (event_type, target) the second-of-
    minute of a case's event minus the second-of-minute of that case's account-delete event falls in a small set of values
    (e.g. search deletes: 21-22 s or 51-52 s, support redactions: 10-11 s or 51-52 s) whatever the minute. Two cases that
    share a reported_at therefore have account-delete events that look alike by minute but not by seconds-phase.
    The table is learned from the participant data itself (explicit links only) and scores how well a candidate account-delete
    time fits the case's other events: sum of log-likelihood ratios against a uniform phase. It is a ranking signal only;
    a candidate is accepted when it beats the runner-up by a clear margin (PHASE_MARGIN)."""
    ALPHA = 0.5

    def __init__(self, with_result: bool = False) -> None:
        self.with_result = with_result            # key on the event's result too (anonymization events differ by result)
        self.tab: dict[tuple, Counter] = defaultdict(Counter)

    def key(self, e: Event) -> tuple:
        return (e.event_type, e.target, e.result) if self.with_result else (e.event_type, e.target)

    @staticmethod
    def usable(e: Event) -> bool:
        return not (e.event_type == "DELETE_RECORD" and e.target == "account_service")

    def learn(self, t_account: datetime, events: list[Event]) -> None:
        for e in events:
            if self.usable(e):
                self.tab[self.key(e)][int((e.t - t_account).total_seconds()) % 60] += 1

    def score(self, events: list[Event], t_account: datetime) -> tuple[float, int]:
        total, used = 0.0, 0
        for e in events:
            cnt = self.tab.get(self.key(e))
            if not self.usable(e) or not cnt:
                continue
            off = int((e.t - t_account).total_seconds()) % 60
            near = sum(cnt[(off + d) % 60] for d in (-1, 0, 1))
            density = (near + self.ALPHA) / (sum(cnt.values()) + self.ALPHA * 60) / 3.0
            total += math.log(density * 60)
            used += 1
        return total, used


PHASE_MARGIN = 2.0        # log-likelihood lead (about 7:1) over the runner-up; hold-out precision 100% at 1.0-3.0 (CHANGELOG row 14)


def known_accounts(linked: set[str], observed: set[str], events_by_resource: dict[str, list[Event]], R: datetime) -> set[str]:
    """Accounts that already belong to the case: linked to its users AND either observed inside the case's evidence window or
    deleted by the worker inside the account window. The second kind matters: an account whose only observation is a later
    snapshot still has an owner, so it must not be offered to another case as a leftover candidate (regression: a case whose
    own account was linked late took a neighbour's account by elimination)."""
    own = {x for x in linked if x.startswith("account_")}
    return {x for x in own if x in observed or any(e.event_type == "DELETE_RECORD" and e.source == "deletion_worker"
                                                      and e.target == "account_service" and _in_window(e, R, ACCOUNT_WINDOW)
                                                      for e in events_by_resource.get(x, []))}


def assign_accounts(cases: list[CaseKey], events: list[Event], explicit: dict[str, set[str]],
                    owners: dict[str, set[str]], cohort: dict[str, set[str]] | None = None,
                    own_events: dict[str, list[Event]] | None = None) -> dict[str, Assignment]:
    """explicit: case_id -> accounts already linked to the case's users inside its window.
    owners: account -> users it is explicitly linked to (any time).
    cohort: case_id -> accounts observed in an evidence file that also observes the case's other records (same snapshot
            = same cohort of users); used to narrow an ambiguous group.
    own_events: case_id -> activity events on the case's own linked resources; enables the seconds-phase stage (PhaseModel)."""
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
    full = {cid: set(cs) for cid, cs in cand.items()}               # before any narrowing
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
                cand[cid] = _by_result(cand[cid], keys[cid], by_account)
        _eliminate(cand, out, taken, by_account)
    # Narrowing is only a preference. When the preferred account went to another case, fall back to the case's full candidate
    # set (minus what is taken), narrowed by the delete result again, and let elimination continue.
    for cid in sorted(cand):
        if not cand[cid]:
            cand[cid] = _by_result(full[cid] - taken, keys[cid], by_account)
    _eliminate(cand, out, taken, by_account)
    if own_events is not None:
        _phase_resolve(cases, cand, out, taken, by_account, explicit, own_events)
    for cid, cs in cand.items():
        out[cid] = Assignment(None, "ambiguous" if cs else "none", tuple(sorted(cs)))
    return out


def _delete_event(evs: list[Event], R: datetime) -> Event:
    return next(e for e in evs if _in_window(e, R, ACCOUNT_WINDOW))


def _phase_resolve(cases: list[CaseKey], cand: dict[str, set[str]], out: dict[str, "Assignment"], taken: set[str],
                   by_account: dict[str, list[Event]], explicit: dict[str, set[str]], own_events: dict[str, list[Event]]) -> None:
    """Greedy: repeatedly accept the single (case, account) whose phase score leads the runner-up by PHASE_MARGIN the most,
    then let plain elimination propagate. Deterministic: ties broken by case_id, then account id (plain string order)."""
    keys = {c.case_id: c for c in cases}
    model = PhaseModel()
    for c in cases:                                                 # learn only from explicitly linked single accounts
        ex = explicit.get(c.case_id)
        if ex and len(ex) == 1:
            evs = [e for e in by_account.get(next(iter(ex)), []) if _in_window(e, c.reported_at, ACCOUNT_WINDOW)]
            if len(evs) == 1:
                model.learn(evs[0].t, own_events.get(c.case_id, []))
    while True:
        best = None                                                 # (lead, case_id, account)
        for cid in sorted(cand):
            cs = cand[cid]
            if len(cs) < 2:
                continue
            ranked = sorted(((model.score(own_events.get(cid, []), _delete_event(by_account[a], keys[cid].reported_at).t)[0], a) for a in sorted(cs)),
                            key=lambda x: (-x[0], x[1]))
            lead = ranked[0][0] - ranked[1][0]
            if lead >= PHASE_MARGIN and (best is None or lead > best[0]):
                best = (lead, cid, ranked[0][1])
        if best is None:
            return
        _, cid, a = best
        out[cid] = Assignment(a, "phase", (a,), _delete_event(by_account[a], keys[cid].reported_at))
        del cand[cid]
        taken.add(a)
        for other in cand.values():
            other.discard(a)
        _eliminate(cand, out, taken, by_account)


def _by_result(accts: set[str], case: CaseKey, by_account: dict[str, list[Event]]) -> set[str]:
    """Keep the accounts whose delete event result matches the case's reported status (soft: never empties the set)."""
    want = RESULT_OF_REPORTED_STATUS.get(case.reported_status)
    narrowed = {a for a in accts if any(e.result == want and _in_window(e, case.reported_at, ACCOUNT_WINDOW) for e in by_account[a])}
    return narrowed or set(accts)


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


ANALYTICS_MIN_LLR = 2.0     # the candidate's phase offset must be clearly more likely than a uniform one (about 7:1)


def assign_anonymized_analytics(cases: list[CaseKey], events: list[Event], anonymized: set[str],
                                chain: dict[str, list[tuple[datetime, int]]],
                                foreign_chain: "callable | None" = None,
                                account_time: dict[str, datetime] | None = None) -> dict[str, Assignment]:
    """chain: case_id -> [(event time, position k)] of the case's OWN explicitly linked policy actions
    (REDACT support k=1, DISABLE flow k=2, BLOCK backup k=3).
    1) a candidate whose time fits the case's chain wins; 1b) seconds-phase: learned from the chain-resolved pairs, an anonymization
    event sits at a fixed second-offset from its case's account-delete event (`account_time`), so among a case's candidates the one
    whose offset fits wins when no other scored claimant of that candidate fits; 2) otherwise elimination: a candidate counts only when exactly
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
    if account_time:
        _analytics_phase(cases, cand, out, taken, account_time)
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


def _analytics_phase(cases: list[CaseKey], cand: dict[str, dict[str, Event]], out: dict[str, Assignment], taken: set[str],
                     account_time: dict[str, datetime]) -> None:
    model = PhaseModel(with_result=True)
    for cid, a in out.items():                                       # learn only from the chain-resolved pairs
        if a.how == "chain" and cid in account_time and a.event is not None:
            model.learn(account_time[cid], [a.event])
    if not model.tab:
        return

    def fits(cid: str, ev: Event) -> bool:
        return model.score([ev], account_time[cid])[0] >= ANALYTICS_MIN_LLR

    scored = [c.case_id for c in cases if c.case_id not in out and c.case_id in account_time]
    for cid in sorted(scored):
        good = sorted(r for r, ev in cand[cid].items() if r not in taken and fits(cid, ev))
        if len(good) != 1:
            continue
        r = good[0]
        rivals = [o for o in scored if o != cid and o not in out and r in cand[o] and fits(o, cand[o][r])]
        if rivals:
            continue
        out[cid] = Assignment(r, "phase", (r,), cand[cid][r])
        taken.add(r)
