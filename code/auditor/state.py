"""State of one resource at audit_at, from the evidence captured inside the request window.

Window rule: only observations whose FILE was captured in [requested_at, audit_at] count as state evidence.
 * after audit_at: unknowable at audit time (never used for state, never cited);
 * before requested_at: describes the world before the deletion was even requested; the same person can appear in
   several scenarios (users with two requests), and old observations would resurrect resources of another scenario.
Later evidence may still be used to LINK a resource to a user (see link.py), and each such use is logged.
"""
from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime

from .evidence.records import Obs

# when several observations of one resource sit in the same file (e.g. primary + replica tables), the violating one wins
SEVERITY = {"PRESENT": 1, "ABSENT": 0, "EXPOSED": 1, "REDACTED": 0, "ANONYMIZED": 0, "ACTIVE": 1, "DISABLED": 0,
            "RESTORABLE": 1, "BLOCKED": 0}


@dataclass(frozen=True)
class ResourceState:
    resource: str
    system: str
    category: str
    state: str
    file_id: str
    file_t: datetime
    t: datetime
    source: str
    until: datetime | None = None      # retention / hold end stated by the record (None = not stated)


def usable(o: Obs, t0: datetime, audit_at: datetime) -> bool:
    return bool(o.state) and t0 <= o.file_t <= audit_at and o.t <= audit_at


AGENT_SOURCE = "image:agent"        # observations written by the model-driven screenshot reader (auditor/agent); no other source starts with this


def _pick(obs: list[Obs]) -> Obs:
    latest_file_t = max(o.file_t for o in obs)
    in_file = [o for o in obs if o.file_t == latest_file_t]
    if all(o.source.startswith("log:") for o in in_file):          # a log is a timeline: the last event wins
        last_t = max(o.t for o in in_file)
        in_file = [o for o in in_file if o.t == last_t]
    return max(in_file, key=lambda o: SEVERITY.get(o.state, 0))    # tables of one snapshot: any copy that violates counts


def resolve(observations: list[Obs], t0: datetime, audit_at: datetime, log: list[str] | None = None) -> ResourceState | None:
    obs = [o for o in observations if usable(o, t0, audit_at)]
    if not obs:
        return None
    chosen = _pick(obs)
    if chosen.source == AGENT_SOURCE:
        # Gate rule G10: a model reading may add a finding or confirm a state, but it cannot lower the severity of the state that deterministic evidence supports in the window.
        # Entered only when a model-sourced observation was chosen, so a run without the agent layer never reaches this branch (byte-identical by construction).
        det = [o for o in obs if not o.source.startswith("image:agent")]
        if det:
            base = _pick(det)
            if SEVERITY.get(base.state, 0) > SEVERITY.get(chosen.state, 0):
                if log is not None:
                    log.append(f"G10 {chosen.resource}: the model reading in {chosen.file_id} says {chosen.state}, but {base.file_id} supports {base.state}: the supported state stays")
                chosen = base
    return ResourceState(chosen.resource, chosen.system, chosen.category, chosen.state, chosen.file_id, chosen.file_t, chosen.t, chosen.source, chosen.until)
