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


def usable(o: Obs, t0: datetime, audit_at: datetime) -> bool:
    return bool(o.state) and t0 <= o.file_t <= audit_at and o.t <= audit_at


def resolve(observations: list[Obs], t0: datetime, audit_at: datetime) -> ResourceState | None:
    obs = [o for o in observations if usable(o, t0, audit_at)]
    if not obs:
        return None
    latest_file_t = max(o.file_t for o in obs)
    in_file = [o for o in obs if o.file_t == latest_file_t]
    if all(o.source.startswith("log:") for o in in_file):          # a log is a timeline: the last event wins
        last_t = max(o.t for o in in_file)
        in_file = [o for o in in_file if o.t == last_t]
    chosen = max(in_file, key=lambda o: SEVERITY.get(o.state, 0))  # tables of one snapshot: any copy that violates counts
    return ResourceState(chosen.resource, chosen.system, chosen.category, chosen.state, chosen.file_id, chosen.file_t, chosen.t, chosen.source)
