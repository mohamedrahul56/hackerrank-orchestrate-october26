"""Output validator. Runs on every row before it is written and again on the re-read file.

It checks the *contract* (schema, ordering, evidence rules, status/steps consistency); it never
decides what the right answer is. Hard problems are errors; things the 32 samples always do but
the spec does not strictly demand are warnings.
"""
from __future__ import annotations

import json
from collections import Counter
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any

from .dataset import Dataset
from .schema import (
    ALLOWED_TRIPLES, FINDING_TO_REMEDIATIONS, OP_RANK, OUTPUT_HEADER, PHASE_RANK, PHASES,
    REMEDIATION_TO_VERIFICATION, STATUSES, STEP_FIELDS, sort_key,
)
from .outio import read_output_raw


@dataclass(frozen=True)
class Issue:
    case_id: str
    code: str
    message: str
    severity: str = "error"  # "error" | "warning"

    def __str__(self) -> str:
        return f"[{self.severity}] {self.case_id}: {self.code}: {self.message}"


class ValidationError(Exception):
    """Raised by callers that must fail loudly (the final writer)."""


def _schema_issues(case_id: str, steps: Any) -> list[Issue]:
    if not isinstance(steps, list):
        return [Issue(case_id, "STEPS_NOT_LIST", f"steps_json must be a JSON array, got {type(steps).__name__}")]
    out: list[Issue] = []
    for i, s in enumerate(steps, start=1):
        if not isinstance(s, dict):
            out.append(Issue(case_id, "STEP_NOT_OBJECT", f"step #{i} is not an object"))
            continue
        if set(s) != set(STEP_FIELDS):
            out.append(Issue(case_id, "STEP_FIELDS", f"step #{i} fields {sorted(s)} != {sorted(STEP_FIELDS)}"))
            continue
        if not isinstance(s["step_no"], int) or isinstance(s["step_no"], bool):
            out.append(Issue(case_id, "STEP_NO_TYPE", f"step #{i} step_no must be an integer"))
        for f in STEP_FIELDS[1:]:
            if not isinstance(s[f], str):
                out.append(Issue(case_id, "FIELD_TYPE", f"step #{i} {f} must be a string (use \"\" not null)"))
        if isinstance(s["phase"], str) and s["phase"] not in PHASES:
            out.append(Issue(case_id, "BAD_PHASE", f"step #{i} phase {s['phase']!r}"))
        elif all(isinstance(s[f], str) for f in ("phase", "operation", "state")) and \
                (s["phase"], s["operation"], s["state"]) not in ALLOWED_TRIPLES:
            out.append(Issue(case_id, "BAD_TRIPLE", f"step #{i} ({s['phase']}, {s['operation']}, {s['state']}) not allowed"))
        if not s["system_id"] or not s["resource_id"]:
            out.append(Issue(case_id, "EMPTY_ID", f"step #{i} has empty system_id/resource_id"))
    return out


def validate_case(case_id: str, status: str, steps: Any, audit_at: datetime | None = None,
                  ds: Dataset | None = None) -> list[Issue]:
    """All contract checks for one row. `ds`/`audit_at` enable the evidence-id existence/time checks."""
    issues: list[Issue] = []
    if status not in STATUSES:
        return [Issue(case_id, "BAD_STATUS", f"deletion_status {status!r} not in {STATUSES}")]
    issues += _schema_issues(case_id, steps)
    if issues:
        return issues  # later checks assume well-formed steps

    if status == "NOT_APPLICABLE":
        if steps:
            issues.append(Issue(case_id, "NA_NOT_EMPTY", "NOT_APPLICABLE rows must have steps_json == []"))
        return issues
    if not steps:
        return [Issue(case_id, "EMPTY_STEPS", f"{status} rows must contain steps")]

    # step_no consecutive from 1
    if [s["step_no"] for s in steps] != list(range(1, len(steps) + 1)):
        issues.append(Issue(case_id, "STEP_NO", "step_no must be 1..n in array order"))
    # canonical order
    if [sort_key(s) for s in steps] != sorted(sort_key(s) for s in steps):
        issues.append(Issue(case_id, "ORDER", "steps are not in canonical order (phase, operation, system_id, resource_id)"))
    # duplicates
    dup = [k for k, n in Counter((s["phase"], s["operation"], s["system_id"], s["resource_id"]) for s in steps).items() if n > 1]
    if dup:
        issues.append(Issue(case_id, "DUPLICATE", f"duplicate steps: {dup[:3]}"))

    phases = Counter(s["phase"] for s in steps)
    if status == "COMPLETE":
        if set(phases) != {"VERIFICATION"}:
            issues.append(Issue(case_id, "COMPLETE_PHASES", f"COMPLETE rows may only contain VERIFICATION steps, got {dict(phases)}"))
    else:  # INCOMPLETE
        for ph in PHASES:
            if not phases[ph]:
                issues.append(Issue(case_id, "INCOMPLETE_MISSING_PHASE", f"INCOMPLETE rows need {ph} steps"))
        issues += _pairing_issues(case_id, steps)

    # evidence_id rules: cited only on FINDING (INCOMPLETE) or on every step (COMPLETE).
    for s in steps:
        cites = s["evidence_id"] != ""
        must_cite = s["phase"] == "FINDING" or status == "COMPLETE"
        if must_cite and not cites:
            issues.append(Issue(case_id, "EVIDENCE_MISSING", f"step {s['step_no']} ({s['operation']} {s['resource_id']}) must cite evidence"))
        if not must_cite and cites:
            issues.append(Issue(case_id, "EVIDENCE_ON_PROPOSAL", f"step {s['step_no']} ({s['operation']}) is future work and must have evidence_id \"\""))
        if cites and ds is not None:
            t = ds.citable_time(s["evidence_id"])
            if t is None:
                issues.append(Issue(case_id, "EVIDENCE_UNKNOWN", f"step {s['step_no']} cites unknown id {s['evidence_id']!r}"))
            elif audit_at is not None and t > audit_at:
                issues.append(Issue(case_id, "EVIDENCE_AFTER_AUDIT", f"step {s['step_no']} cites {s['evidence_id']} captured {t:%Y-%m-%dT%H:%M:%SZ} after audit_at"))

    # Warning only: every deletion audit in the samples verifies that the primary account is absent.
    if not any(s["operation"] == "VERIFY_RECORD_ABSENT" and s["system_id"] == "account_service" for s in steps):
        issues.append(Issue(case_id, "NO_ACCOUNT_ABSENT_CHECK", "no VERIFY_RECORD_ABSENT on account_service (all 32 samples have one)", "warning"))
    return issues


def _pairing_issues(case_id: str, steps: list[dict[str, Any]]) -> list[Issue]:
    """INCOMPLETE rows: finding -> remediation -> verification must line up per resource."""
    issues: list[Issue] = []
    by = lambda ph: [s for s in steps if s["phase"] == ph]  # noqa: E731
    remediations = {(s["system_id"], s["resource_id"]): s["operation"] for s in by("REMEDIATION")}
    verifications = {(s["system_id"], s["resource_id"], s["operation"]) for s in by("VERIFICATION")}
    finding_keys = set()
    for f in by("FINDING"):
        key = (f["system_id"], f["resource_id"])
        finding_keys.add(key)
        if remediations.get(key) not in FINDING_TO_REMEDIATIONS[f["operation"]]:
            issues.append(Issue(case_id, "FINDING_NO_REMEDIATION",
                                f"{f['operation']} {f['resource_id']} needs one of {sorted(FINDING_TO_REMEDIATIONS[f['operation']])}"))
    for key, op in remediations.items():
        if key not in finding_keys:
            issues.append(Issue(case_id, "REMEDIATION_NO_FINDING", f"{op} {key[1]} has no matching finding"))
        if (key[0], key[1], REMEDIATION_TO_VERIFICATION[op]) not in verifications:
            issues.append(Issue(case_id, "REMEDIATION_NO_VERIFICATION", f"{op} {key[1]} needs {REMEDIATION_TO_VERIFICATION[op]}"))
    return issues


def validate_rows(rows: list[tuple[str, str, Any]], ds: Dataset) -> list[Issue]:
    """Row-level checks across a whole prediction set (one row per request, request order, no dupes)."""
    issues: list[Issue] = []
    expected = [r.case_id for r in ds.requests]
    got = [c for c, _, _ in rows]
    if len(set(got)) != len(got):
        issues.append(Issue("-", "DUPLICATE_CASE", f"duplicate case_ids: {[c for c, n in Counter(got).items() if n > 1][:5]}"))
    if set(got) != set(expected):
        issues.append(Issue("-", "CASE_SET", f"missing={sorted(set(expected) - set(got))[:5]} extra={sorted(set(got) - set(expected))[:5]}"))
    elif got != expected:
        issues.append(Issue("-", "CASE_ORDER", "rows are not in the order of the requests file"))
    audit = {r.case_id: r.audit_at for r in ds.requests}
    for case_id, status, steps in rows:
        issues += validate_case(case_id, status, steps, audit.get(case_id), ds)
    return issues


def validate_output_file(path: Path, ds: Dataset) -> list[Issue]:
    """Re-read the written CSV from disk, parse every steps_json, and re-validate everything."""
    header, raw = read_output_raw(path)
    issues: list[Issue] = []
    if tuple(header) != OUTPUT_HEADER:
        issues.append(Issue("-", "HEADER", f"header {header} != {list(OUTPUT_HEADER)}"))
    rows: list[tuple[str, str, Any]] = []
    for i, r in enumerate(raw, start=2):
        if len(r) != 3:
            issues.append(Issue(f"line{i}", "ROW_WIDTH", f"expected 3 columns, got {len(r)}"))
            continue
        try:
            steps = json.loads(r[2])
        except json.JSONDecodeError as e:
            issues.append(Issue(r[0], "BAD_JSON", str(e)))
            continue
        rows.append((r[0], r[1], steps))
    issues += validate_rows(rows, ds)
    return issues


def raise_on_errors(issues: list[Issue]) -> None:
    errors = [i for i in issues if i.severity == "error"]
    if errors:
        raise ValidationError("\n".join(str(i) for i in errors[:20]) + (f"\n... {len(errors)} errors total" if len(errors) > 20 else ""))
