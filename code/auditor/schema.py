"""The output contract (AGENTS.md section 6.3/6.4) as data, plus canonical ordering.

Everything that needs to know "which operations exist, in which order, with which state"
reads it from here, so the validator, the step generator and the scorer cannot disagree.
"""
from __future__ import annotations

from typing import Any

STATUSES = ("COMPLETE", "INCOMPLETE", "NOT_APPLICABLE")
PHASES = ("FINDING", "REMEDIATION", "VERIFICATION")
STEP_FIELDS = ("step_no", "phase", "system_id", "resource_id", "operation", "state", "evidence_id")
OUTPUT_HEADER = ("case_id", "deletion_status", "steps_json")

# (operation, phase, state) in the CANONICAL operation order from problem_statement.md.
# The position in this tuple is the sort rank used inside a phase.
OPERATIONS: tuple[tuple[str, str, str], ...] = (
    ("REPORT_REMAINING_RECORD", "FINDING", "PRESENT"),
    ("REPORT_EXPOSED_PII", "FINDING", "PRESENT"),
    ("REPORT_ACTIVE_DATA_FLOW", "FINDING", "ACTIVE"),
    ("REPORT_RESTORE_RISK", "FINDING", "ACTIVE"),
    ("DISABLE_DATA_FLOW", "REMEDIATION", "DISABLED"),
    ("BLOCK_RESTORE", "REMEDIATION", "BLOCKED"),
    ("DELETE_RECORD", "REMEDIATION", "ABSENT"),
    ("REDACT_RECORD", "REMEDIATION", "REDACTED"),
    ("ANONYMIZE_RECORD", "REMEDIATION", "ANONYMIZED"),
    ("VERIFY_RECORD_ABSENT", "VERIFICATION", "ABSENT"),
    ("VERIFY_RECORD_PRESENT", "VERIFICATION", "PRESENT"),
    ("VERIFY_RECORD_REDACTED", "VERIFICATION", "REDACTED"),
    ("VERIFY_RECORD_ANONYMIZED", "VERIFICATION", "ANONYMIZED"),
    ("VERIFY_DATA_FLOW_DISABLED", "VERIFICATION", "DISABLED"),
    ("VERIFY_RESTORE_BLOCKED", "VERIFICATION", "BLOCKED"),
)
OP_RANK = {op: i for i, (op, _, _) in enumerate(OPERATIONS)}
PHASE_RANK = {p: i for i, p in enumerate(PHASES)}
ALLOWED_TRIPLES = frozenset((phase, op, state) for op, phase, state in OPERATIONS)

# What each finding must be followed by, and what each remediation must be proven by.
# Derived from the problem statement's operation meanings and checked against all
# 22 INCOMPLETE sample answers in tests/test_validate.py.
FINDING_TO_REMEDIATIONS = {
    "REPORT_REMAINING_RECORD": {"DELETE_RECORD"},
    "REPORT_EXPOSED_PII": {"REDACT_RECORD", "ANONYMIZE_RECORD"},
    "REPORT_ACTIVE_DATA_FLOW": {"DISABLE_DATA_FLOW"},
    "REPORT_RESTORE_RISK": {"BLOCK_RESTORE"},
}
REMEDIATION_TO_VERIFICATION = {
    "DISABLE_DATA_FLOW": "VERIFY_DATA_FLOW_DISABLED",
    "BLOCK_RESTORE": "VERIFY_RESTORE_BLOCKED",
    "DELETE_RECORD": "VERIFY_RECORD_ABSENT",
    "REDACT_RECORD": "VERIFY_RECORD_REDACTED",
    "ANONYMIZE_RECORD": "VERIFY_RECORD_ANONYMIZED",
}


def sort_key(step: dict[str, Any]) -> tuple:
    """Canonical order: phase, operation (table order), then plain-string system_id, resource_id.

    Plain string order for resource_id is a decision recorded in NOTES.md (ids are NOT
    zero-padded, so e.g. 'audit_record_9528' sorts before 'audit_record_987').
    """
    return (PHASE_RANK[step["phase"]], OP_RANK[step["operation"]], step["system_id"], step["resource_id"])


def make_step(phase: str, operation: str, system_id: str, resource_id: str, evidence_id: str = "") -> dict[str, Any]:
    """Build a step; `state` is implied by the operation so callers cannot get it wrong."""
    state = next(s for op, _, s in OPERATIONS if op == operation)
    return {
        "step_no": 0,
        "phase": phase,
        "system_id": system_id,
        "resource_id": resource_id,
        "operation": operation,
        "state": state,
        "evidence_id": evidence_id,
    }


def canonicalize(steps: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Sort into canonical order and renumber step_no from 1 (returns new dicts)."""
    ordered = sorted(steps, key=sort_key)
    return [{**s, "step_no": i} for i, s in enumerate(ordered, start=1)]
