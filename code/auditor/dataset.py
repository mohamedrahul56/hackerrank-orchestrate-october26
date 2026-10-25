"""Typed loaders for the participant-facing CSVs (only what Phase 1 needs; Phase 2 extends it).

Only files under dataset/ are read. Nothing here knows about labels.
"""
from __future__ import annotations

import csv
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path


def parse_ts(value: str) -> datetime:
    """Parse the dataset's ISO-8601 'Z' timestamps into aware UTC datetimes. A time without a zone is UTC too (the dataset's convention),
    never the machine's local time: the answer must not depend on where the program runs."""
    dt = datetime.fromisoformat(value.strip().replace("Z", "+00:00"))
    return (dt.replace(tzinfo=timezone.utc) if dt.tzinfo is None else dt).astimezone(timezone.utc)


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
    case_order: list[str] = field(default_factory=list)                # every answerable case_id in file order (malformed rows included)
    bad_rows: list = field(default_factory=list)                       # BadRow list from the lenient loader

    def citable_time(self, evidence_id: str) -> datetime | None:
        """Timestamp of a citable id (file_id / activity_id / audit_id) or None if unknown."""
        for table in (self.evidence_time, self.activity_time, self.audit_time):
            if evidence_id in table:
                return table[evidence_id]
        return None


class DatasetError(RuntimeError):
    """A file the run cannot do without is missing or has no usable header. Everything else degrades row by row."""


csv.field_size_limit(16 * 1024 * 1024)       # the csv module's default (128 KiB per field) would abort the whole run on one long request_text


def _rows(path: Path, required: tuple[str, ...] = ()) -> list[dict[str, str]]:
    """Rows of a CSV as dicts. Blank lines are skipped; short rows get empty strings; a missing column in the header is an error."""
    try:
        with path.open(newline="", encoding="utf-8-sig") as fh:
            reader = csv.DictReader(fh)
            missing = [c for c in required if c not in (reader.fieldnames or [])]
            if missing:
                raise DatasetError(f"{path.name}: missing column(s) {missing}")
            return [{k: (v if v is not None else "") for k, v in r.items() if k is not None}
                    for r in reader if any((v or "").strip() for v in r.values() if not isinstance(v, list))]
    except FileNotFoundError as e:
        raise DatasetError(f"required file not found: {path}") from e
    except UnicodeDecodeError as e:
        raise DatasetError(f"{path.name} is not valid UTF-8: {e}") from e
    except csv.Error as e:                                          # e.g. a field above the limit: a clear error, not a traceback
        raise DatasetError(f"{path.name} is not readable as CSV: {e}") from e


def try_ts(value: str | None) -> datetime | None:
    try:
        return parse_ts(value) if value and value.strip() else None
    except (ValueError, TypeError, AttributeError):
        return None


@dataclass(frozen=True)
class BadRow:
    case_id: str
    line: int
    reason: str


REQUEST_COLUMNS = ("case_id", "email", "requested_at", "reported_status", "reported_at", "audit_at", "request_text")


def load_requests_lenient(path: Path) -> tuple[list[Request], list[BadRow], list[str]]:
    """(good requests, malformed rows, case_ids in file order without duplicates).

    A row with an unparseable timestamp or an empty email cannot be audited but still needs its answer row, so it is reported as a
    BadRow and the caller writes a conservative row for it. A row without case_id cannot be answered at all (skipped, reported).
    A repeated case_id keeps the first row."""
    good: list[Request] = []
    bad: list[BadRow] = []
    order: list[str] = []
    seen: set[str] = set()
    for i, r in enumerate(_rows(path, REQUEST_COLUMNS), start=2):
        cid = (r.get("case_id") or "").strip()
        if not cid:
            bad.append(BadRow("", i, "empty case_id"))
            continue
        if cid in seen:
            bad.append(BadRow(cid, i, "duplicate case_id (first row kept)"))
            continue
        seen.add(cid)
        order.append(cid)
        ts = {k: try_ts(r.get(k)) for k in ("requested_at", "reported_at", "audit_at")}
        requested_at, reported_at, audit_at = ts["requested_at"], ts["reported_at"], ts["audit_at"]
        if requested_at is None or reported_at is None or audit_at is None:
            bad.append(BadRow(cid, i, f"unparseable {', '.join(k for k, v in ts.items() if v is None)}"))
        elif not (r.get("email") or "").strip():
            bad.append(BadRow(cid, i, "empty email"))
        else:
            good.append(Request(cid, r["email"], requested_at, r.get("reported_status", ""), reported_at, audit_at, r.get("request_text", "")))
    return good, bad, order


def load_requests(path: Path) -> list[Request]:
    """Strict variant for tools that expect a clean file: raises on the first malformed row."""
    good, bad, _ = load_requests_lenient(path)
    broken = [b for b in bad if not b.reason.startswith("duplicate")]
    if broken:
        raise DatasetError(f"{path.name}: malformed row(s): " + "; ".join(f"line {b.line} {b.case_id or '?'}: {b.reason}" for b in broken[:5]))
    return good


def load_dataset(root: Path, requests_file: str = "deletion_requests.csv") -> Dataset:
    ds = Dataset(root=root)
    ds.requests, bad, ds.case_order = load_requests_lenient(root / requests_file)
    ds.bad_rows = bad
    for table, fname, id_col, time_col in ((ds.evidence_time, "evidence_files.csv", "file_id", "captured_at"),
                                           (ds.activity_time, "system_activity.csv", "activity_id", "event_time"),
                                           (ds.audit_time, "user_audit_log.csv", "audit_id", "event_time")):
        try:
            for r in _rows(root / fname):
                t = try_ts(r.get(time_col))
                if r.get(id_col) and t is not None:
                    table[r[id_col]] = t
        except DatasetError:
            pass                                                   # a missing optional table just means nothing can be cited from it
    return ds
