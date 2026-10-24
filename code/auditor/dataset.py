"""Typed loaders for the participant-facing CSVs (only what Phase 1 needs; Phase 2 extends it).

Only files under dataset/ are read. Nothing here knows about labels.
"""
from __future__ import annotations

import csv
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path


def parse_ts(value: str) -> datetime:
    """Parse the dataset's ISO-8601 'Z' timestamps into aware UTC datetimes."""
    return datetime.fromisoformat(value.strip().replace("Z", "+00:00")).astimezone(timezone.utc)


@dataclass(frozen=True)
class Request:
    case_id: str
    email: str
    requested_at: datetime
    reported_status: str
    reported_at: datetime
    audit_at: datetime
    request_text: str


@dataclass
class Dataset:
    root: Path
    requests: list[Request] = field(default_factory=list)
    # id -> timestamp of everything a step may cite as evidence_id
    evidence_time: dict[str, datetime] = field(default_factory=dict)   # file_id -> captured_at
    activity_time: dict[str, datetime] = field(default_factory=dict)   # activity_id -> event_time
    audit_time: dict[str, datetime] = field(default_factory=dict)      # audit_id -> event_time

    def citable_time(self, evidence_id: str) -> datetime | None:
        """Timestamp of a citable id (file_id / activity_id / audit_id) or None if unknown."""
        for table in (self.evidence_time, self.activity_time, self.audit_time):
            if evidence_id in table:
                return table[evidence_id]
        return None


def _rows(path: Path) -> list[dict[str, str]]:
    with path.open(newline="", encoding="utf-8") as fh:
        return list(csv.DictReader(fh))


def load_requests(path: Path) -> list[Request]:
    return [
        Request(
            case_id=r["case_id"],
            email=r["email"],
            requested_at=parse_ts(r["requested_at"]),
            reported_status=r["reported_status"],
            reported_at=parse_ts(r["reported_at"]),
            audit_at=parse_ts(r["audit_at"]),
            request_text=r["request_text"],
        )
        for r in _rows(path)
    ]


def load_dataset(root: Path, requests_file: str = "deletion_requests.csv") -> Dataset:
    ds = Dataset(root=root)
    ds.requests = load_requests(root / requests_file)
    ds.evidence_time = {r["file_id"]: parse_ts(r["captured_at"]) for r in _rows(root / "evidence_files.csv")}
    ds.activity_time = {r["activity_id"]: parse_ts(r["event_time"]) for r in _rows(root / "system_activity.csv")}
    ds.audit_time = {r["audit_id"]: parse_ts(r["event_time"]) for r in _rows(root / "user_audit_log.csv")}
    return ds
