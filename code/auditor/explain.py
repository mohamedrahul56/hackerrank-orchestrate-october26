"""`python3 code/main.py --explain CASE_ID`: a read-only, step-by-step account of how one case got its answer.

It runs the real case audit with a Trace (see trace.py), so every sentence below describes a decision the code actually took, then adds the
evidence behind it: identity resolution, every observation of every resource with the one that was used marked, the rule from
deletion_rules.csv, and for each emitted step (and each omitted one) the reason. Nothing is written anywhere; the printed answer is the
same row output.csv holds for that case.
"""
from __future__ import annotations

from .audit import Auditor, CaseResult
from .classify import DELETION
from .evidence.records import system_for
from .state import usable
from .trace import Trace

TS = "%Y-%m-%dT%H:%M:%SZ"


def _t(dt) -> str:
    return dt.strftime(TS) if dt else "-"


def _table(header: list[str], rows: list[list[str]], indent: int = 2) -> list[str]:
    if not rows:
        return [" " * indent + "(none)"]
    widths = [max(len(str(x)) for x in col) for col in zip(header, *rows)]
    line = lambda cells: " " * indent + "  ".join(str(c).ljust(w) for c, w in zip(cells, widths)).rstrip()  # noqa: E731
    return [line(header), line(["-" * w for w in widths])] + [line(r) for r in rows]


def explain(a: Auditor, case_id: str) -> str:
    """Plain-text explanation of one case. Raises KeyError for an unknown case id."""
    if case_id not in a.requests and case_id not in a.bad_requests:
        raise KeyError(case_id)
    L: list[str] = [f"EXPLAIN {case_id}", "=" * (8 + len(case_id)), ""]
    if case_id not in a.requests:
        malformed = a.audit(case_id)
        L += [f"The request row is malformed ({a.bad_requests[case_id]}); it cannot be audited.", f"Answer: {malformed.status}, steps_json = []", f"Why: {'; '.join(malformed.notes)}"]
        return "\n".join(L) + "\n"
    r = a.requests[case_id]
    L += ["0. THE REQUEST",
          f"  email (as supplied) : {r.email}",
          f"  requested_at        : {_t(r.requested_at)}      only evidence captured from here on counts as state",
          f"  audit_at            : {_t(r.audit_at)}      the cutoff: evidence captured later never changes the answer or gets cited",
          f"  reported_status     : {r.reported_status}  (a claim of the deletion workflow, reported {_t(r.reported_at)}; never used as proof)",
          f"  request_text        : {r.request_text}", ""]

    # ------------------------------------------------------------------ 1. request meaning
    cls = a.classification[case_id]
    L += ["1. WHAT THE PERSON ASKED FOR (classify.py)", f"  decision : {cls.label}  (method: {cls.method})"]
    L += [f"  because  : {x}" for x in cls.reasons] or ["  because  : (no reasons recorded)"]
    if cls.label != DELETION:
        not_applicable = a.audit(case_id)
        L += ["", "RESULT", f"  deletion_status = {not_applicable.status}, steps_json = []",
              "  A request that does not authorize deletion is NOT_APPLICABLE whatever the evidence says; no identity or evidence is examined."]
        return "\n".join(L) + "\n"

    # ------------------------------------------------------------------ 2. identity
    L += ["", "2. WHO THE PERSON IS (identity.py)"]
    people = a.res.resolve(r.email, r.requested_at)
    if not people:
        L += ["  the e-mail matches no known identity -> conservative NOT_APPLICABLE (fallback)"]
    for p in people:
        mail = r.email.strip().lower()
        span = next((s for s in p.email_spans if s[0] == mail), None)
        kind = "the CURRENT e-mail" if mail == p.current_email else "a HISTORICAL e-mail"
        valid = "" if not span else f" (valid {_t(span[1]) if span[1] else 'since the beginning'} .. {_t(span[2]) if span[2] else 'now'})"
        L += [f"  {r.email} -> {p.user_id} ({p.name}): {kind}{valid}",
              f"    identifiers used to match evidence: {len(p.emails)} e-mail(s) {sorted(p.emails)}, {len(p.hashes)} hash(es), {len(p.phones)} phone(s), and the user id {p.user_id}",
              "    matching is by exact identifier only (e-mail, user id, sha256 of the e-mail, phone); names and look-alike addresses never match"]
    if len(people) > 1:
        L.append(f"  {len(people)} people own this e-mail; the one who owned it at requested_at wins")

    # ------------------------------------------------------------------ run the real audit with a trace
    trace = Trace()
    try:
        res: CaseResult = a._audit(case_id, trace)
    except Exception as e:  # noqa: BLE001 - explain what the production path would do: it falls back
        res = CaseResult(case_id, "NOT_APPLICABLE", [], [f"FALLBACK: {type(e).__name__}: {e}"])
    users = a.users[case_id]

    # ------------------------------------------------------------------ 3. resources
    L += ["", "3. EVERYTHING TIED TO THAT PERSON (link.py, discover.py)"]
    rows = []
    for rid, how in sorted(trace.resources.items()):
        first = a.link.first_link_time(rid, users)
        link = f"{first[1]} captured {_t(first[0])}" if first else "-"
        later = " (AFTER audit_at: used only to link, logged)" if any(x[0] == rid for x in trace.later) else ""
        meaning = {"explicit": "identifier in evidence", "eliminated": "timeline + elimination", "phase": "timeline + second-of-minute offset",
                   "chain": "timeline + policy-action chain"}.get(how.replace("analytics-", ""), how)
        rows.append([rid, a.link.catalog_systems.get(rid) or (next((o.system for o in a.link.by_resource.get(rid, [])), None)) or system_for(rid), how, meaning, link + later])
    L += _table(["resource", "system", "found by", "meaning", "first link evidence"], rows)
    asg = a.account_asg.get(case_id)
    ana = a.analytics_asg.get(case_id)
    if asg is not None:
        L.append(f"  account discovery   : {asg.how}" + (f" -> {asg.resource}" if asg.resource else "") + (f"; candidates {list(asg.candidates)}" if asg.candidates else "")
                 + (f"; worker delete event {asg.event.activity_id} at {_t(asg.event.t)} ({asg.event.result})" if asg.event else ""))
        how_text = {"explicit": "the account carries one of the person's identifiers (or is tied to them and deleted by the worker inside the window)",
                    "eliminated": "among the free accounts the worker deleted in this case's window, all but one belong to other cases, so exactly one remains",
                    "phase": "several free accounts fit; the one whose delete time fits the case's other events by second-of-minute offset wins "
                             "(a dataset-specific timing heuristic; `--no-timing-heuristics` switches it off)"}.get(asg.how)
        if how_text:
            L.append(f"      -> {how_text}")
        if asg.how == "ambiguous":
            L.append("      -> several free accounts fit this case equally well and nothing separates them: NO account step is emitted (a guess would be right about 1 time in "
                     f"{len(asg.candidates) or 'k'}; omitting is safer for precision)")
        if asg.how == "none" and not asg.resource:
            L.append("      -> no worker delete event for a free account falls in this case's window: no account is known, so no account step")
    if ana is not None and (ana.resource or ana.how == "ambiguous"):
        L.append(f"  analytics discovery : {ana.how}" + (f" -> {ana.resource}" if ana.resource else f"; candidates {list(ana.candidates)} (not separable: no step)"))
    if not trace.resources:
        L.append("  (no resource is tied to this person)")

    # ------------------------------------------------------------------ 4. state per resource
    L += ["", f"4. STATE AT audit_at ({_t(r.audit_at)}) AND THE RULE APPLIED (state.py, policy.py)",
          f"  window: evidence files captured in [{_t(r.requested_at)}, {_t(r.audit_at)}]; latest file wins; inside one snapshot the violating copy wins; in a log the last event wins"]
    for rid in sorted(trace.resources):
        obs = sorted(a.link.by_resource.get(rid, []), key=lambda o: (o.file_t, o.t, o.source))
        st = trace.states.get(rid)
        L += ["", f"  {rid}"]
        if obs:
            orows = []
            for o in obs:
                inwin = usable(o, r.requested_at, r.audit_at)
                used = st is not None and inwin and o.file_id == st.file_id and o.state == st.state
                why = "USED" if used else ("inside window, superseded or not the violating copy" if inwin else ("captured AFTER audit_at: ignored for state" if o.file_t > r.audit_at else "captured BEFORE the request: describes the world before it"))
                orows.append([o.file_id, _t(o.file_t), o.source, o.category, o.state, why])
            L += _table(["file", "captured", "source", "category", "state", "role"], orows, indent=4)
        else:
            L.append("    no evidence file observes this resource")
        if st is not None:
            L.append(f"    -> state used: {st.state} ({st.category}), from {st.file_id} captured {_t(st.file_t)}")
        else:
            L.append("    -> no usable observation inside the window")
        verdict = trace.verdicts.get(rid)
        if verdict:
            L.append(f"    rule: {verdict}")
        for what, why in trace.omitted:
            if what == rid:
                L.append(f"    no step: {why}")

    # ------------------------------------------------------------------ 5. steps
    L += ["", "5. THE STEPS AND WHY EACH EXISTS (audit.py)"]
    if not res.steps:
        L += ["  (none)"]
    for s in res.steps:
        key = (s["phase"], s["operation"], s["system_id"], s["resource_id"])
        L += [f"  #{s['step_no']:<2} {s['phase']:<12} {s['operation']:<26} {s['resource_id']:<20} state={s['state']:<10} evidence_id={s['evidence_id'] or '(empty)'}",
              f"        why: {trace.step_why.get(key, '(no reason recorded)')}"]
    order_note = ("  order: phases FINDING, REMEDIATION, VERIFICATION; inside a phase the operation table order puts DISABLE_DATA_FLOW and BLOCK_RESTORE before "
                  "deletes, redactions and anonymizations; ties by system_id then resource_id (plain string order)") if res.steps else ""
    if order_note:
        L.append(order_note)

    # ------------------------------------------------------------------ 6. omissions and notes
    extra = [(w, y) for w, y in trace.omitted if w not in trace.resources]
    L += ["", "6. WHAT WAS LEFT OUT, AND WHY"]
    leftovers = [(w, y) for w, y in trace.omitted if w in trace.resources] + extra
    if asg is not None and asg.how in ("ambiguous", "none") and not asg.resource:
        leftovers.append(("account", f"no account step ({asg.how}), see section 3"))
    if ana is not None and ana.how == "ambiguous":
        leftovers.append(("anonymized analytics", "several candidates fit and nothing separates them"))
    L += [f"  {w}: {y}" for w, y in leftovers] or ["  (nothing: every resource tied to the person produced the steps above or needed none)"]
    unlinked_later = [x for x in trace.later]
    if unlinked_later:
        L += ["  later-evidence links (evidence captured after audit_at, used ONLY to tie the resource to this person; never for state or citations):"]
        L += [f"    {rid} via {fid} captured {t}" for rid, fid, t in unlinked_later]

    # ------------------------------------------------------------------ 7. result
    why_status = {
        "INCOMPLETE": "at least one resource violates its rule at audit_at (see the VIOLATION lines), or the row is provisional",
        "COMPLETE": "every relevant copy has the required treatment, every required retained record is present, and no flow or restore path can recreate prohibited data",
        "NOT_APPLICABLE": "this is a fallback (no evidence-backed resource in the window, unknown identity, or an error): see the notes"}[res.status]
    L += ["", "7. RESULT", f"  deletion_status = {res.status}: {why_status}"]
    disagree = (r.reported_status == "COMPLETE" and res.status == "INCOMPLETE") or (r.reported_status in ("NOT_STARTED", "PARTIALLY_COMPLETE") and res.status == "COMPLETE")
    if disagree:
        L.append(f"  note: the workflow REPORTED {r.reported_status}; that claim was not used as proof. The evidence at audit_at says {res.status}.")
    if res.provisional:
        L.append("  PROVISIONAL: built from structured data because the only state evidence is a screenshot that was not read; replaced by the normal computation once screenshots are read")
    L += [f"  note: {n}" for n in res.notes]
    L += ["", "  This is exactly the row `python3 code/main.py` writes to output.csv for this case; --explain wrote nothing."]
    return "\n".join(L) + "\n"
