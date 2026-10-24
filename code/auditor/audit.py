"""The case auditor: request -> (deletion_status, steps).

Flow for one case (every decision here is code; the only model call is the optional image reader and the optional
tie-breaker for ambiguous request wording):

  1 classify request_text                      (classify.py)    NOT_APPLICABLE -> []
  2 resolve current + historical identity      (identity.py)
  3 find every resource tied to that identity  (link.py + discover.py)
  4 state of each resource at audit_at         (state.py)       only evidence captured in [requested_at, audit_at]
  5 policy verdict per resource                (policy.py)      driven by deletion_rules.csv
  6 findings / remediation / verification      (this file)      canonical order via schema.canonicalize
  7 validate                                   (validate.py)    a bad row never crashes the run
"""
from __future__ import annotations

import csv
import logging
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from pathlib import Path

from . import discover as dsc
from .classify import DELETION, classify_request
from .dataset import Request, load_requests_lenient, parse_ts
from .evidence.imageobs import state_from_description
from .evidence.load import catalog_rows, load_evidence, manifest_rows
from .evidence.records import EvidenceBase, Obs, system_for
from .identity import IdentityResolver
from .link import Linker
from .policy import PolicyEngine
from .schema import canonicalize, make_step
from .state import ResourceState, resolve, usable
from .trace import Trace
from .validate import validate_case

log = logging.getLogger("auditor")

CHAIN_POSITION = {"REDACT_RECORD": 1, "DISABLE_DATA_FLOW": 2, "BLOCK_RESTORE": 3}
# A resource first seen up to this long AFTER audit_at may still be linked to the case (a later snapshot of the same
# cohort: 2 days in every sample where it happens). Evidence from other scenarios of the same person is months away.
LATER_LINK_MAX_DAYS = 7


@dataclass
class CaseResult:
    case_id: str
    status: str
    steps: list[dict]
    notes: list[str] = field(default_factory=list)          # assumptions / fallbacks used (surfaced in the run log)
    later_links: list[tuple[str, str, str]] = field(default_factory=list)  # (resource, linking file/activity, its time)
    provisional: bool = False       # row built without reading a screenshot that the case depends on (see Auditor._provisional)


@dataclass
class Config:
    allow_later_linking: bool = True      # D3: later evidence may link a resource to a user (never state, never cited)
    use_discovery: bool = True            # activity-timeline discovery of id-less accounts / anonymized analytics
    provisional_rows: bool = True         # deletion request whose only state evidence is an unread screenshot -> provisional INCOMPLETE row instead of NOT_APPLICABLE
    use_cohort_filter: bool = False       # measured harmful by hold-out (precision 86% vs 100%): kept only as an ablation switch
    use_timing_heuristics: bool = True   # the two second-of-minute heuristics (accounts: PhaseModel offsets; analytics: the 40/41 s offset). Dataset-specific, see README/NOTES 12
    classifier_llm: object | None = None
    image_extractor: object | None = None


class Auditor:
    def __init__(self, root: Path, config: Config | None = None, requests_files: tuple[str, ...] = ("deletion_requests.csv", "sample_deletion_requests.csv")):
        self.root, self.cfg = root, config or Config()
        self.res = IdentityResolver(root)
        self.base: EvidenceBase = load_evidence(root, include_images=self.cfg.image_extractor is not None, image_extractor=self.cfg.image_extractor)
        self.catalog = catalog_rows(root)
        self.manifest = manifest_rows(root)
        self.link = Linker(self.base, self.res, self.catalog)
        self.policy = PolicyEngine(root / "deletion_rules.csv")
        self.events = dsc.load_events(root)
        self.events_by_resource: dict[str, list[dsc.Event]] = {}
        for e in self.events:
            self.events_by_resource.setdefault(e.resource, []).append(e)
        self.requests: dict[str, Request] = {}
        self.bad_requests: dict[str, str] = {}               # case_id -> why the row cannot be audited (answered with a fallback row)
        for f in requests_files:
            if (root / f).exists():
                good, bad, _ = load_requests_lenient(root / f)
                for r in good:
                    self.requests.setdefault(r.case_id, r)
                for b in bad:
                    if b.case_id and not b.reason.startswith("duplicate"):
                        self.bad_requests.setdefault(b.case_id, b.reason)
        self.classification = {cid: classify_request(r.request_text, self.cfg.classifier_llm) for cid, r in self.requests.items()}
        self.users: dict[str, set[str]] = {cid: {p.user_id for p in self.res.resolve(r.email, r.requested_at)} for cid, r in self.requests.items()}
        self.account_asg: dict[str, dsc.Assignment] = {}
        self.known_accounts: dict[str, set[str]] = {}
        self.analytics_asg: dict[str, dsc.Assignment] = {}
        if self.cfg.use_discovery:
            self._discover()

    # ------------------------------------------------------------------ global discovery (needs every case)
    def _case_files(self, cid: str) -> set[str]:
        r = self.requests[cid]
        files = set()
        for res in self.link.resources_of(self.users[cid]):
            for o in self.link.by_resource.get(res, []):
                if usable(o, r.requested_at, r.audit_at):
                    files.add(o.file_id)
        return files

    def _discover(self) -> None:
        cases, explicit, cohort, chain = [], {}, {}, {}
        for cid, r in self.requests.items():
            if self.classification[cid].label != DELETION or not self.users[cid]:
                continue
            cases.append(dsc.CaseKey(cid, frozenset(self.users[cid]), r.reported_at, r.requested_at, r.audit_at, r.reported_status))
            linked = self.link.resources_of(self.users[cid])
            if not self.cfg.allow_later_linking:                 # D3 off: a link made only by evidence captured after audit_at does not exist
                linked = {x for x in linked if self.link.link_ids_before(x, self.users[cid], r.audit_at)}
            observed = {x for x in linked if x.startswith("account_")
                        and any(usable(o, r.requested_at, r.audit_at) for o in self.link.by_resource.get(x, []))}
            explicit[cid] = dsc.known_accounts(linked, observed, self.events_by_resource, r.reported_at)
            self.known_accounts[cid] = explicit[cid]
            files = self._case_files(cid)
            cohort[cid] = {o.resource for o in self.base.obs if o.category == "USER_PROFILE" and o.file_id in files}
            ch = []
            for x in linked:
                for e in self.events_by_resource.get(x, []):
                    if e.source == "deletion_worker" and e.result == "SUCCESS" and e.event_type in CHAIN_POSITION \
                            and abs((e.t - r.reported_at).total_seconds()) < 3600:
                        ch.append((e.t, CHAIN_POSITION[e.event_type]))
            chain[cid] = ch
        owners: dict[str, set[str]] = {}
        for (rid, uid) in self.link.links:
            if rid.startswith("account_"):
                owners.setdefault(rid, set()).add(uid)
        own = {c.case_id: self._own_events(c) for c in cases} if self.cfg.use_timing_heuristics else None
        self.disc_inputs = (cases, explicit, owners, cohort, own)         # kept for the hold-out measurement (evaluation/holdout.py)
        self.account_asg = dsc.assign_accounts(cases, self.events, explicit, owners, cohort if self.cfg.use_cohort_filter else None, own)
        anonymized = {o.resource for o in self.base.obs if o.category == "ANALYTICS_PROFILE" and o.state == "ANONYMIZED" and not o.idents}
        self.chain_events = []                                   # every completed policy action that belongs to some known user
        for rid, evs in self.events_by_resource.items():
            owners = set(self.link.resource_users.get(rid, ()))
            for e in evs:
                if e.source == "deletion_worker" and e.result == "SUCCESS" and e.event_type in CHAIN_POSITION and owners:
                    self.chain_events.append((e.t, CHAIN_POSITION[e.event_type], owners))
        has_own = {c.case_id for c in cases if any(x.startswith("analytics_") for x in self.link.resources_of(set(c.users)))}
        account_time = {cid: a.event.t for cid, a in self.account_asg.items() if a.resource and a.event is not None} if self.cfg.use_timing_heuristics else None
        self.analytics_inputs = ([c for c in cases if c.case_id not in has_own], self.events, anonymized, chain, self._foreign_chain, account_time)
        self.analytics_asg = dsc.assign_anonymized_analytics(*self.analytics_inputs)

    def _own_events(self, c: "dsc.CaseKey") -> list["dsc.Event"]:
        """Activity events on the case's linked resources around its reported_at (timing only, never state)."""
        seen: dict[str, dsc.Event] = {}
        for rid in self.link.resources_of(set(c.users)):
            for e in self.events_by_resource.get(rid, []):
                if abs((e.t - c.reported_at).total_seconds()) < 4 * 3600:
                    seen[e.activity_id] = e
        return [seen[k] for k in sorted(seen)]

    def _foreign_chain(self, case: "dsc.CaseKey", e: "dsc.Event") -> bool:
        """True when e is followed by another user's completed policy actions at chain spacing (a decoy user's deletion)."""
        for t, k, owners in self.chain_events:
            d = (t - e.t).total_seconds()
            if 0 < d < 1500 and dsc.chain_ok(d, k) and not (owners & case.users):
                return True
        return False

    # ------------------------------------------------------------------ one case
    def audit(self, case_id: str) -> CaseResult:
        if case_id in self.bad_requests and case_id not in self.requests:
            return CaseResult(case_id, "NOT_APPLICABLE", [], [f"FALLBACK: malformed request row ({self.bad_requests[case_id]})"])
        if case_id not in self.requests:
            return CaseResult(case_id, "NOT_APPLICABLE", [], ["FALLBACK: case_id not found in the requests files"])
        try:
            return self._audit(case_id)
        except Exception as e:  # noqa: BLE001 - one bad case must never crash the full run
            log.exception("case %s failed, falling back to a conservative row", case_id)
            return CaseResult(case_id, "NOT_APPLICABLE", [], [f"FALLBACK: {type(e).__name__}: {e}"])

    def _audit(self, case_id: str, trace: Trace | None = None) -> CaseResult:
        tr = trace if trace is not None else Trace()           # a throw-away trace in normal runs: the same code path, nothing read from it
        r = self.requests[case_id]
        cls = self.classification[case_id]
        if cls.label != DELETION:
            return CaseResult(case_id, "NOT_APPLICABLE", [], [f"not a deletion request: {'; '.join(cls.reasons)} [{cls.method}]"])
        users = self.users[case_id]
        notes: list[str] = []
        if not users:
            return CaseResult(case_id, "NOT_APPLICABLE", [], ["FALLBACK: request email matches no known identity"])
        t0, audit_at = r.requested_at, r.audit_at
        later: list[tuple[str, str, str]] = []

        # ---- resources of this case
        resources: dict[str, str] = {}                           # resource -> how it was found
        for rid in sorted(self.link.resources_of(users)):                 # sorted: set order depends on the process hash seed
            first = self.link.first_link_time(rid, users)
            if first is not None and first[0] > audit_at:
                if not self.cfg.allow_later_linking:
                    continue
                later.append((rid, first[1], first[0].strftime("%Y-%m-%dT%H:%M:%SZ")))
            resources[rid] = "explicit"
        later_rids = {x[0] for x in later}
        acct = self.account_asg.get(case_id)
        activity_for: dict[str, dsc.Event | None] = {}
        if acct and acct.resource and acct.resource not in resources:
            resources[acct.resource] = acct.how
            activity_for[acct.resource] = acct.event
            if acct.how == "explicit":                           # linked by a snapshot captured after audit_at: a logged later-evidence use
                first = self.link.first_link_time(acct.resource, users)
                if first is not None and first[0] > audit_at:
                    later.append((acct.resource, first[1], first[0].strftime("%Y-%m-%dT%H:%M:%SZ")))
        ana = self.analytics_asg.get(case_id)
        if ana and ana.resource and ana.resource not in resources:
            resources[ana.resource] = f"analytics-{ana.how}"
            activity_for[ana.resource] = ana.event
        later.sort()                                              # explicit order for the later-evidence log, whatever order the links were found in
        tr.resources, tr.later = dict(resources), list(later)

        # ---- state at audit_at
        states: dict[str, ResourceState] = {}
        for rid in resources:
            st = resolve(self.link.by_resource.get(rid, []), t0, audit_at)
            if st is not None:
                states[rid] = st
        # an account known only from the activity stream has no observation: treat as absent (claim) and cite the activity id
        steps: list[dict] = []
        violations = []
        compliant = []
        tr.states = dict(states)
        for rid, st in states.items():
            if st.until is not None and st.until <= audit_at:
                notes.append(f"{rid}: retention/hold ended {st.until:%Y-%m-%dT%H:%M:%SZ} (before audit_at): no retain requirement, no step")
                tr.omitted.append((rid, f"its retention / legal hold ended {st.until:%Y-%m-%dT%H:%M:%SZ}, before audit_at: the RETAIN rule no longer applies, so nothing is required of it"))
                continue
            v = self.policy.judge(st.category, st.state)
            if v is None:
                notes.append(f"{rid}: state {st.state} for {st.category} says nothing about policy; ignored")
                tr.omitted.append((rid, f"state {st.state} says nothing about policy for {st.category}"))
                continue
            tr.verdicts[rid] = self._verdict_sentence(st, v)
            if v.violation and rid in later_rids:
                # a finding must stand on evidence available at audit_at; a record tied to the user only by a later snapshot may
                # support an end-state check, never a finding or a remediation (decision D3). Measured: this never fires today.
                notes.append(f"{rid}: violation not reported - the record is linked to the user only by evidence captured after audit_at")
                tr.omitted.append((rid, "it violates policy but is tied to the user only by evidence captured after audit_at (decision D3): no finding or remediation"))
                continue
            (violations if v.violation else compliant).append((st, v))
            if not v.violation and not v.emit_verification:
                tr.omitted.append((rid, f"already compliant ({st.state}) and a compliant {st.category} copy adds no step (only the account is always verified)"))
        # accounts with no state evidence in the window: discovered through the activity stream, or linked explicitly by a
        # later snapshot of this same scenario (first seen between the request and a week after audit_at)
        unobserved_accounts = []
        for rid, how in resources.items():
            if rid in states or not rid.startswith("account_"):
                continue
            first = min((o.file_t for o in self.link.by_resource.get(rid, [])), default=None)
            deleted_in_window = rid in self.known_accounts.get(case_id, ())     # linked to the user AND deleted by the worker in the window
            if how != "explicit" or deleted_in_window or (first is not None and t0 <= first <= audit_at + timedelta(days=LATER_LINK_MAX_DAYS)):
                unobserved_accounts.append(rid)
        # other linked resources with no state evidence in the window that first appear at/after the request (e.g. a retained
        # audit record seen only in a later snapshot of the cohort): their END state is still a required check, with no citation
        unobserved_linked = []
        for rid, how in resources.items():
            if rid in states or how != "explicit" or rid.startswith("account_"):
                continue
            seen = self.link.by_resource.get(rid, [])
            first = min((o.file_t for o in seen), default=None)
            if first is not None and t0 <= first <= audit_at + timedelta(days=LATER_LINK_MAX_DAYS):
                unobserved_linked.append((rid, max(seen, key=lambda o: o.file_t)))

        status = "INCOMPLETE" if violations else "COMPLETE"
        cite_ok = status == "COMPLETE"
        for st, v in violations:
            steps.append(tr.why(make_step("FINDING", v.treatment.finding, st.system, st.resource, st.file_id),
                                f"{st.resource} is {st.state} at audit_at ({tr.verdicts[st.resource]}); cites {st.file_id}, the latest in-window file that shows it"))
            steps.append(tr.why(make_step("REMEDIATION", v.treatment.fix, st.system, st.resource), "proposed fix for the finding above (future work, so no evidence id)"))
            steps.append(tr.why(make_step("VERIFICATION", v.treatment.verify, st.system, st.resource), "check that must pass after the fix (future work, so no evidence id)"))
        for st, v in compliant:
            if v.emit_verification:
                steps.append(tr.why(make_step("VERIFICATION", v.treatment.verify, st.system, st.resource, st.file_id if cite_ok else ""),
                                    f"{st.resource} already has the required treatment ({st.state}; {tr.verdicts[st.resource]})"
                                    + (f"; cites {st.file_id}, the evidence that proves it" if cite_ok else "; the row is INCOMPLETE, so verification steps are future work with no evidence id")))
        if not cite_ok:
            for rid, o in unobserved_linked:
                v = self.policy.judge(o.category, sorted(self.policy.treatment_for(o.category).ok)[0])
                if v is not None and v.emit_verification:
                    steps.append(tr.why(make_step("VERIFICATION", v.treatment.verify, o.system, rid, ""),
                                        f"{rid} is tied to the user and first appears in {o.file_id} (a later snapshot of the same scenario): its required end state is still a check, with no citation"))
                    notes.append(f"{rid}: no state evidence in the window; end-state check added from its later/linked observation ({o.file_id})")
        elif unobserved_linked:
            for rid, o in unobserved_linked:
                tr.omitted.append((rid, f"tied to the user but no evidence captured inside the window shows it, and in a COMPLETE row every step must cite evidence: no step"))
        for rid in unobserved_accounts:                          # account known only from the activity stream
            ev = activity_for.get(rid)
            cite = ""
            if cite_ok:
                cite, how = self._cite_unobserved("account_service", r, states, ev)
                notes.append(f"{rid}: no observation in window; cited {cite!r} ({how})")
            steps.append(tr.why(make_step("VERIFICATION", "VERIFY_RECORD_ABSENT", "account_service", rid, cite),
                                f"{rid} is the user's account ({resources.get(rid)}); no file shows it inside the window, so it is treated as deleted (claim from the worker's delete event)"
                                + (f"; cites {cite} ({how})" if cite_ok else "; the row is INCOMPLETE, so no evidence id")))
        provisional = False
        if not steps:
            prov = self._provisional(r, users) if self.cfg.provisional_rows else None
            if not prov:
                return CaseResult(case_id, "NOT_APPLICABLE", [], ["FALLBACK: deletion request but no evidence-backed resource in the request window"], later)
            steps, status, provisional = prov[0], "INCOMPLETE", True
            notes.append("PROVISIONAL: " + prov[1])
            for s_ in steps:
                tr.why(s_, "PROVISIONAL - " + prov[1])
        steps = canonicalize(steps)
        issues = [i for i in validate_case(case_id, status, steps) if i.severity == "error"]
        if issues:
            return CaseResult(case_id, "NOT_APPLICABLE", [], [f"FALLBACK: validator rejected row: {issues[0]}"], later)
        return CaseResult(case_id, status, steps, notes, later, provisional)

    def _verdict_sentence(self, st: ResourceState, v) -> str:
        rid = self.policy.rule_ids.get(st.category, "")
        action, cond = self.policy.rules.get(st.category, ("DISABLE_FLOW", "spec 6.5: an active data flow can recreate the data (not a row of deletion_rules.csv)"))
        rule = f"{rid} {st.category} / {cond} / {action}".strip() if rid else f"{st.category}: {cond}"
        outcome = "VIOLATION" if v.violation else "compliant"
        return f"{rule}; state {st.state} -> {outcome} (treatment {v.action})"

    # ------------------------------------------------------------------ provisional row for an unread screenshot
    def _provisional(self, r: Request, users: set[str]) -> tuple[list[dict], str] | None:
        """A deletion request whose state evidence is a screenshot nobody could read (no model key, or the read failed) is not a
        non-deletion request, so NOT_APPLICABLE is almost certainly wrong. Build the most likely row from structured data only:
          * a record the data catalog (or any identifier match) ties to the user inside [requested_at, audit_at], with no readable
            state in the window;
          * a screenshot captured inside the window by an admin tool of the same system (matched on the system's leading word:
            account_admin_portal ~ account_service);
          * the record is assumed to show the violating state of its category, i.e. the screenshot was taken because something remained.
        The row is flagged provisional (run notes, provisional list) and is replaced as soon as the screenshot is really read."""
        steps: list[dict] = []
        used: list[str] = []
        for rid in sorted(self.link.resources_of(users)):
            times = [t for u in users for (t, _) in self.link.links.get((rid, u), [])]
            if not any(r.requested_at <= t <= r.audit_at for t in times):
                continue
            if resolve(self.link.by_resource.get(rid, []), r.requested_at, r.audit_at) is not None:
                continue
            prefix = rid.rsplit("_", 1)[0]
            judged = state_from_description(prefix, {"status_words": [], "emails": ["x@example.test"], "phones": [], "shows_direct_identifiers": True})
            v = self.policy.judge(*judged) if judged else None
            if v is None or not v.violation:
                continue
            system = self.link.catalog_systems.get(rid) or system_for(rid)
            shots = [m for m in self.manifest if m["file_path"].lower().endswith(".png") and r.requested_at <= m["t"] <= r.audit_at
                     and m["origin"].split("_")[0] == system.split("_")[0]]
            if not shots:
                continue
            shot = max(shots, key=lambda m: (m["t"], m["file_id"]))
            steps += [make_step("FINDING", v.treatment.finding, system, rid, shot["file_id"]), make_step("REMEDIATION", v.treatment.fix, system, rid),
                      make_step("VERIFICATION", v.treatment.verify, system, rid)]
            used.append(f"{rid} (screenshot {shot['file_id']})")
        if not steps:
            return None
        return canonicalize(steps), ("no readable state evidence; assumed the violating state of " + ", ".join(used) + " from the unread screenshot(s)")

    # ------------------------------------------------------------------ citation fallback for unobserved resources
    def _cite_unobserved(self, system: str, r: Request, states: dict[str, ResourceState], ev: "dsc.Event | None") -> tuple[str, str]:
        """No evidence file observes the resource. Prefer a snapshot that covers the system AND observes the case's other
        records (same cohort); then any covering snapshot in the window; finally the activity id of the delete claim."""
        case_files = {st.file_id for st in states.values()}
        cov = [c for c in self.base.coverage if c.system == system and r.requested_at <= c.file_t <= r.audit_at]
        same = sorted((c for c in cov if c.file_id in case_files), key=lambda c: c.file_t)
        if same:
            return same[-1].file_id, "snapshot covering the system and observing the case's other records"
        if cov:
            return max(cov, key=lambda c: c.file_t).file_id, "latest snapshot covering the system in the window"
        if ev is not None:
            return ev.activity_id, "ASSUMPTION: activity_id used because no file observes the resource"
        return "", "no citation available"
