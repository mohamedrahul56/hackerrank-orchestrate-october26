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

import heapq
from functools import lru_cache
import math
from bisect import bisect_left, bisect_right
from collections import Counter, defaultdict
from dataclasses import dataclass
from datetime import datetime, timedelta
from pathlib import Path
from typing import Callable

from .dataset import DatasetError, _rows, try_ts
from .settings import Tunables

# The numbers below are the defaults of settings.Tunables (config/settings.toml); every function takes `tune` and uses that instead.
DEFAULT_TUNE = Tunables()
ACCOUNT_WINDOW = DEFAULT_TUNE.account_window                          # relative to reported_at
ANALYTICS_WINDOW = DEFAULT_TUNE.analytics_window


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
    """Activity rows; a row with an unparseable time or without a resource is skipped, a missing file means an empty stream."""
    try:
        rows = _rows(root / "system_activity.csv", ("activity_id", "event_time", "event_type"))
    except DatasetError:
        return []
    out = []
    for r in rows:
        t = try_ts(r["event_time"])
        if t is not None:
            out.append(Event(r["activity_id"], t, r["event_type"], r.get("source_system", ""), r.get("target_system", ""), r.get("resource_id", ""), r.get("reported_result", "")))
    return out


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
    a candidate is accepted when it beats the runner-up by a clear margin (tune.phase_margin)."""
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



def known_accounts(linked: set[str], observed: set[str], events_by_resource: dict[str, list[Event]], R: datetime,
                   tune: Tunables = DEFAULT_TUNE) -> set[str]:
    """Accounts that already belong to the case: linked to its users AND either observed inside the case's evidence window or
    deleted by the worker inside the account window. The second kind matters: an account whose only observation is a later
    snapshot still has an owner, so it must not be offered to another case as a leftover candidate (regression: a case whose
    own account was linked late took a neighbour's account by elimination)."""
    own = {x for x in linked if x.startswith("account_")}
    return {x for x in own if x in observed or any(e.event_type == "DELETE_RECORD" and e.source == "deletion_worker"
                                                      and e.target == "account_service" and _in_window(e, R, tune.account_window)
                                                      for e in events_by_resource.get(x, []))}


def assign_accounts(cases: list[CaseKey], events: list[Event], explicit: dict[str, set[str]],
                    owners: dict[str, set[str]], cohort: dict[str, set[str]] | None = None,
                    own_events: dict[str, list[Event]] | None = None, tune: Tunables = DEFAULT_TUNE) -> dict[str, Assignment]:
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
    index = _TimeIndex((e.t, e.resource) for e in deletes)          # one sort instead of a scan of every account for every case
    for c in cases:
        if c.case_id in out:
            continue
        cand[c.case_id] = {a for a in index.between(c.reported_at + tune.account_window[0], c.reported_at + tune.account_window[1]) if a not in taken}
    keys = {c.case_id: c for c in cases}
    full = {cid: set(cs) for cid, cs in cand.items()}               # before any narrowing
    # stage 1: plain elimination; stage 2: keep accounts seen in the case's cohort snapshot; stage 3: keep accounts whose delete
    # event result matches the case's reported status. Narrowing is soft: it never empties a candidate set.
    pool = _Pool(cand, out, taken, by_account)
    for stage in (1, 2, 3):
        if stage == 2 and cohort:
            for cid in list(cand):
                narrowed = cand[cid] & cohort.get(cid, set())
                if narrowed:
                    cand[cid] = narrowed
        if stage == 3:
            for cid in list(cand):
                cand[cid] = _by_result(cand[cid], keys[cid], by_account, tune)
        pool.reindex()
        pool.eliminate()
    # Narrowing is only a preference. When the preferred account went to another case, fall back to the case's full candidate
    # set (minus what is taken), narrowed by the delete result again, and let elimination continue.
    for cid in sorted(cand):
        if not cand[cid]:
            cand[cid] = _by_result(full[cid] - taken, keys[cid], by_account, tune)
    pool.reindex()
    pool.eliminate()
    if own_events is not None:
        _phase_resolve(cases, pool, by_account, explicit, own_events, tune)
    for cid, cs in cand.items():
        out[cid] = Assignment(None, "ambiguous" if cs else "none", tuple(sorted(cs)))
    return out


def _delete_event(evs: list[Event], R: datetime, tune: Tunables = DEFAULT_TUNE) -> Event:
    return next(e for e in evs if _in_window(e, R, tune.account_window))


def _phase_resolve(cases: list[CaseKey], pool: "_Pool", by_account: dict[str, list[Event]], explicit: dict[str, set[str]],
                   own_events: dict[str, list[Event]], tune: Tunables = DEFAULT_TUNE) -> None:
    """Greedy: repeatedly accept the single (case, account) whose phase score leads the runner-up by tune.phase_margin the most,
    then let plain elimination propagate. Deterministic: ties broken by case_id, then account id (plain string order).
    Cost: every (case, account) score is computed once and a case is re-ranked only when its own candidate set changed (a heap with lazy
    invalidation replaces re-ranking every unresolved case after every acceptance)."""
    cand = pool.cand
    keys = {c.case_id: c for c in cases}
    model = PhaseModel()
    for c in cases:                                                 # learn only from explicitly linked single accounts
        ex = explicit.get(c.case_id)
        if ex and len(ex) == 1:
            evs = [e for e in by_account.get(next(iter(ex)), []) if _in_window(e, c.reported_at, tune.account_window)]
            if len(evs) == 1:
                model.learn(evs[0].t, own_events.get(c.case_id, []))
    scores: dict[tuple[str, str], float] = {}

    def score(cid: str, a: str) -> float:
        k = (cid, a)
        if k not in scores:
            scores[k] = model.score(own_events.get(cid, []), _delete_event(by_account[a], keys[cid].reported_at, tune).t)[0]
        return scores[k]

    version: dict[str, int] = {}
    heap: list[tuple[float, str, str, int]] = []                    # (-lead, case_id, account, version at push time)

    def push(cid: str) -> None:
        version[cid] = version.get(cid, 0) + 1
        cs = cand.get(cid)
        if cs is None or len(cs) < 2:
            return
        ranked = sorted(((score(cid, a), a) for a in sorted(cs)), key=lambda x: (-x[0], x[1]))
        lead = ranked[0][0] - ranked[1][0]
        if lead >= tune.phase_margin:
            heapq.heappush(heap, (-lead, cid, ranked[0][1], version[cid]))

    for cid in sorted(cand):
        push(cid)
    while heap:
        neg_lead, cid, a, ver = heapq.heappop(heap)
        if version.get(cid) != ver or cid not in cand:
            continue                                                # stale entry: the case's candidates changed or it was resolved
        pool.assign(cid, a, "phase", _delete_event(by_account[a], keys[cid].reported_at, tune))
        pool.eliminate()
        for other in pool.drain_touched():
            push(other)


class _TimeIndex:
    """Values sorted by time: `between(lo, hi)` returns the values whose time is in [lo, hi] without scanning the rest."""

    def __init__(self, pairs):
        ordered = sorted(pairs, key=lambda p: p[0])
        self.times = [p[0] for p in ordered]
        self.values = [p[1] for p in ordered]

    def between(self, lo: datetime, hi: datetime) -> list:
        return self.values[bisect_left(self.times, lo): bisect_right(self.times, hi)]


class _Pool:
    """The unresolved cases and their candidate accounts, with an index account -> cases that still list it.

    `eliminate()` reproduces the original multi-pass rule exactly (a case whose candidate set has one account takes it; cases are taken in the order of
    the original dict; a case that becomes single behind the cursor waits for the next pass) but touches only the cases that list the account just taken,
    instead of re-scanning every case after every acceptance."""

    def __init__(self, cand: dict[str, set[str]], out: dict[str, "Assignment"], taken: set[str], by_account: dict[str, list[Event]]):
        self.cand, self.out, self.taken, self.by_account = cand, out, taken, by_account
        self.order = {cid: i for i, cid in enumerate(cand)}
        self.names = {i: cid for cid, i in self.order.items()}
        self.claimants: dict[str, set[str]] = {}
        self.touched: set[str] = set()
        self.sole: set[str] = set()                                  # cases whose candidate set has exactly one account (checked again when used)

    def reindex(self) -> None:
        self.claimants = {}
        self.sole = set()
        for cid, cs in self.cand.items():
            for a in cs:
                self.claimants.setdefault(a, set()).add(cid)
            if len(cs) == 1:
                self.sole.add(cid)

    def assign(self, cid: str, a: str, how: str, event: Event) -> None:
        self.out[cid] = Assignment(a, how, (a,), event)
        for x in self.cand.pop(cid):
            self.claimants.get(x, set()).discard(cid)
        self.taken.add(a)
        for other in self.claimants.pop(a, set()):
            self.cand[other].discard(a)
            self.touched.add(other)
            if len(self.cand[other]) == 1:
                self.sole.add(other)

    def drain_touched(self) -> list[str]:
        t, self.touched = sorted(self.touched), set()
        return [c for c in t if c in self.cand]

    def eliminate(self) -> None:
        while True:
            heap = [self.order[cid] for cid in self.sole if cid in self.cand and len(self.cand[cid]) == 1]
            self.sole = {cid for cid in self.sole if cid in self.cand and len(self.cand[cid]) == 1}
            heapq.heapify(heap)
            progressed = False
            while heap:
                i = heapq.heappop(heap)
                cid = self.names[i]
                cs = self.cand.get(cid)
                if cs is None or len(cs) != 1:
                    continue
                a = next(iter(cs))
                before = {c: len(self.cand[c]) for c in self.claimants.get(a, ()) if c != cid}
                self.assign(cid, a, "eliminated", self.by_account[a][0])
                progressed = True
                for c in before:
                    if c in self.cand and len(self.cand[c]) == 1 and self.order[c] > i:
                        heapq.heappush(heap, self.order[c])           # ahead of the cursor: still taken in this pass
            if not progressed:
                return


def _by_result(accts: set[str], case: CaseKey, by_account: dict[str, list[Event]], tune: Tunables = DEFAULT_TUNE) -> set[str]:
    """Keep the accounts whose delete event result matches the case's reported status (soft: never empties the set)."""
    want = RESULT_OF_REPORTED_STATUS.get(case.reported_status)
    narrowed = {a for a in accts if any(e.result == want and _in_window(e, case.reported_at, tune.account_window) for e in by_account[a])}
    return narrowed or set(accts)



@lru_cache(maxsize=None)
def _chain_sums(k: int, gaps: tuple) -> frozenset:
    sums = {0}
    for _ in range(k):
        sums = {x + g for x in sums for g in gaps}
    return frozenset(sums)


def chain_ok(delta_seconds: float, k: int, tune: Tunables = DEFAULT_TUNE) -> bool:
    """delta between a candidate ANONYMIZE event and the k-th later event of the same case's chain (support=1, flow=2, backup=3)."""
    return any(abs(delta_seconds - x) <= tune.chain_tolerance_seconds for x in _chain_sums(k, tune.chain_gaps_seconds))



def assign_anonymized_analytics(cases: list[CaseKey], events: list[Event], anonymized: set[str],
                                chain: dict[str, list[tuple[datetime, int]]],
                                foreign_chain: Callable[[CaseKey, Event], bool] | None = None,
                                account_time: dict[str, datetime] | None = None, tune: Tunables = DEFAULT_TUNE) -> dict[str, Assignment]:
    """chain: case_id -> [(event time, position k)] of the case's OWN explicitly linked policy actions
    (REDACT support k=1, DISABLE flow k=2, BLOCK backup k=3).
    1) a candidate whose time fits the case's chain wins; 1b) seconds-phase: learned from the chain-resolved pairs, an anonymization
    event sits at a fixed second-offset from its case's account-delete event (`account_time`), so among a case's candidates the one
    whose offset fits wins when no other scored claimant of that candidate fits; 2) otherwise elimination: a candidate counts only when exactly
    one still-unassigned case has it in its window. `foreign_chain(case, event)` says that the candidate sits inside
    another user's completed chain (a decoy user whose deletion happened in the same hour): such a candidate is dropped."""
    anon_events = [e for e in events if e.event_type == "ANONYMIZE_RECORD" and e.source == "deletion_worker"
                   and e.target == "analytics_platform" and e.result == "SUCCESS" and e.resource in anonymized]
    # candidates per case: one sort by time and a bisect per case instead of a scan of every anonymization event for every case. The events
    # of a window are taken back in their original order, so a resource that was anonymized twice still keeps the LAST event, as before.
    win_lo, win_hi = tune.analytics_window
    times = _TimeIndex((e.t, i) for i, e in enumerate(anon_events))
    cand: dict[str, dict[str, Event]] = {}
    for c in cases:
        picked = sorted(times.between(c.reported_at + win_lo, c.reported_at + win_hi))
        cand[c.case_id] = {anon_events[i].resource: anon_events[i] for i in picked if not (foreign_chain and foreign_chain(c, anon_events[i]))}
    out: dict[str, Assignment] = {}
    taken: set[str] = set()
    for c in cases:                                              # pass 1: chain timing
        fits = {r for r, e in cand[c.case_id].items()
                if any(chain_ok((t - e.t).total_seconds(), k, tune) for t, k in chain.get(c.case_id, []))}
        if len(fits) == 1:
            r = next(iter(fits))
            out[c.case_id] = Assignment(r, "chain", (r,), cand[c.case_id][r])
            taken.add(r)
    if account_time:
        _analytics_phase(cases, cand, out, taken, account_time, tune)
    pending = {c.case_id for c in cases if c.case_id not in out}           # frozen: ambiguous answers must not shrink the claimant sets
    claimant_count: Counter = Counter(r for cid in pending for r in cand[cid])        # how many pending cases have each candidate in their window
    for c in cases:                                              # pass 2: elimination
        if c.case_id in out:
            continue
        rest = {r for r in cand[c.case_id] if r not in taken}
        if len(rest) == 1 and claimant_count[next(iter(rest))] == 1:
            r = next(iter(rest))
            out[c.case_id] = Assignment(r, "eliminated", (r,), cand[c.case_id][r])
        else:
            out[c.case_id] = Assignment(None, "ambiguous" if rest else "none", tuple(sorted(rest)))
    return out


def _analytics_phase(cases: list[CaseKey], cand: dict[str, dict[str, Event]], out: dict[str, Assignment], taken: set[str],
                     account_time: dict[str, datetime], tune: Tunables = DEFAULT_TUNE) -> None:
    model = PhaseModel(with_result=True)
    for cid, a in out.items():                                       # learn only from the chain-resolved pairs
        if a.how == "chain" and cid in account_time and a.event is not None:
            model.learn(account_time[cid], [a.event])
    if not model.tab:
        return

    memo: dict[tuple[str, str], bool] = {}

    def fits(cid: str, ev: Event) -> bool:
        k = (cid, ev.resource)
        if k not in memo:
            memo[k] = model.score([ev], account_time[cid])[0] >= tune.analytics_min_llr
        return memo[k]

    scored = [c.case_id for c in cases if c.case_id not in out and c.case_id in account_time]
    claimed_by: dict[str, list[str]] = defaultdict(list)          # candidate resource -> scored cases that have it in their window (static)
    for o in scored:
        for r in cand[o]:
            claimed_by[r].append(o)
    for cid in sorted(scored):
        good = sorted(r for r, ev in cand[cid].items() if r not in taken and fits(cid, ev))
        if len(good) != 1:
            continue
        r = good[0]
        if any(o != cid and o not in out and fits(o, cand[o][r]) for o in claimed_by[r]):          # a rival scored case fits it as well
            continue
        out[cid] = Assignment(r, "phase", (r,), cand[cid][r])
        taken.add(r)
