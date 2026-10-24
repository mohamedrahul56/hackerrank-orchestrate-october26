"""Optional record of WHY a case got its answer. `Auditor._audit(case_id, trace)` fills it at the exact points where it decides;
`explain.py` renders it. Passing no trace (the normal run) costs nothing and cannot change a result."""
from __future__ import annotations

from dataclasses import dataclass, field


@dataclass
class Trace:
    resources: dict[str, str] = field(default_factory=dict)                  # resource -> how it was tied to the case (explicit / eliminated / phase / chain ...)
    later: list[tuple[str, str, str]] = field(default_factory=list)          # (resource, linking file, time) links made by evidence after audit_at
    verdicts: dict[str, str] = field(default_factory=dict)                   # resource -> policy verdict sentence
    omitted: list[tuple[str, str]] = field(default_factory=list)             # (resource or topic, why no step)
    step_why: dict[tuple[str, str, str, str], str] = field(default_factory=dict)   # (phase, operation, system, resource) -> why this step exists
    states: dict = field(default_factory=dict)                               # resource -> ResourceState actually used
    status_reason: str = ""

    def why(self, step: dict, text: str) -> dict:
        self.step_why[(step["phase"], step["operation"], step["system_id"], step["resource_id"])] = text
        return step
