"""One uniform shape for everything any evidence file says about one resource at one moment.

Every parser (SQL, JSON, logs, text, PDF, image) produces `Obs` objects; everything downstream
(linking, state at audit_at, policy, steps) only ever sees `Obs`. That is what lets one policy engine
handle fifteen evidence formats.

State vocabulary (the `state` field), by data category:
  DELETE-type categories (USER_PROFILE, SEARCH_INDEX_ENTRY, SUPPORT_ATTACHMENT, USER_EXPORT,
                          SECONDARY_IDENTIFIER, MARKETING_PROFILE) : PRESENT | ABSENT
  SUPPORT_MESSAGE, ACCOUNT_TOMBSTONE                               : EXPOSED | REDACTED
  ANALYTICS_PROFILE                                                : EXPOSED | ANONYMIZED
  DATA_FLOW                                                        : ACTIVE | DISABLED
  BACKUP_ARCHIVE                                                   : RESTORABLE | BLOCKED
  SECURITY_AUDIT_LOG, LEGAL_HOLD_RECORD                            : PRESENT
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from datetime import datetime
from typing import Any, Iterable

EMAIL_RE = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
USER_RE = re.compile(r"\buser_\d+\b")
HASH_RE = re.compile(r"(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])")
RESOURCE_RE = re.compile(r"\b(account|search|support|analytics|export|data_flow|backup|audit_record|tombstone|legal_hold|marketing|cache|email_delivery|object)_\d+\b")

# fields that look like identifiers but are not (integrity seals are 64 hex chars, file names embed ids ...)
NON_IDENT_KEYS = {"integrity_seal", "checksum", "archive_key", "object_key", "archive_path", "file_name", "file", "ticket_id"}

# resource-id prefix -> owning system (used when a source does not name the system)
SYSTEM_BY_PREFIX = {
    "account": "account_service", "search": "search_service", "support": "support_platform",
    "analytics": "analytics_platform", "export": "export_service", "data_flow": "integration_service",
    "backup": "backup_service", "audit_record": "security_service", "tombstone": "identity_service",
    "legal_hold": "legal_service", "marketing": "marketing_platform", "cache": "cache_service",
    "email_delivery": "email_delivery_service", "object": "object_storage",
}


def system_for(resource_id: str) -> str:
    return SYSTEM_BY_PREFIX[resource_id.rsplit("_", 1)[0].replace("audit_record", "audit_record")]


def norm_phone(value: str) -> str:
    digits = re.sub(r"\D", "", value)
    return "p:" + digits if len(digits) >= 7 else ""


def flatten(obj: Any, key: str = "") -> Iterable[tuple[str, str]]:
    """(last-dict-key, scalar-as-string) pairs for every non-null scalar in a nested structure."""
    if isinstance(obj, dict):
        for k, v in obj.items():
            yield from flatten(v, k)
    elif isinstance(obj, (list, tuple)):
        for v in obj:
            yield from flatten(v, key)
    elif obj is not None and not isinstance(obj, bool):
        yield key, str(obj)


def idents_from(pairs: Iterable[tuple[str, str]]) -> frozenset[str]:
    """Identifier strings (lower-case emails, user ids, sha256 hashes, 'p:'+digits phones) found in field VALUES.
    Exact tokens only - never names, never fuzzy matching."""
    found: set[str] = set()
    for key, val in pairs:
        if key in NON_IDENT_KEYS:
            continue
        for m in EMAIL_RE.finditer(val):
            found.add(m.group(0).lower())
        for m in USER_RE.finditer(val):
            found.add(m.group(0))
        for m in HASH_RE.finditer(val):
            found.add(m.group(0).lower())
        if "phone" in key.lower():
            p = norm_phone(val)
            if p:
                found.add(p)
    return frozenset(found)


@dataclass(frozen=True)
class Obs:
    file_id: str                 # manifest file_id this observation came from ("" for activity-derived)
    file_t: datetime             # manifest captured_at (the file as a whole)
    t: datetime                  # when the record was observed (record/event time; defaults to file_t)
    system: str
    resource: str
    category: str                # a deletion_rules.csv data_category (or DATA_FLOW)
    state: str                   # see module docstring; "" = link-only observation (no state claim)
    idents: frozenset[str] = frozenset()
    source: str = ""             # e.g. "sql:customer_data_read_replica", "log:profile_lookup_completed"
    detail: str = ""             # short human-readable reason for the state (used in notes/debugging)


@dataclass(frozen=True)
class Coverage:
    """A file (or snapshot inside it) that lists ALL rows of a system's table/collection, so a missing row means absent."""
    file_id: str
    file_t: datetime
    t: datetime
    system: str
    source: str = ""


@dataclass
class EvidenceBase:
    obs: list[Obs] = field(default_factory=list)
    coverage: list[Coverage] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)   # unreadable/corrupt files etc. (never raises)


# ----------------------------------------------------------------------------- state rules per record shape
def _s(v: Any) -> str:
    return "" if v is None else str(v).strip()


def profile_state(rec: dict) -> str:
    return "PRESENT"


def analytics_state(rec: dict) -> str:
    keys = ("user_id", "customer_reference", "email", "phone", "email_hash")
    return "EXPOSED" if any(_s(rec.get(k)) for k in keys) else "ANONYMIZED"


def tombstone_state(rec: dict) -> str:
    return "EXPOSED" if any(_s(rec.get(k)) for k in ("name", "email", "phone", "email_hash", "full_name")) else "REDACTED"


def support_message_state(rec: dict) -> str:
    """Exposed iff a direct identifier (email/name/phone) is stored or quoted in the message text."""
    for k in ("requester_email", "requester_name", "requester_phone"):
        if _s(rec.get(k)):
            return "EXPOSED"
    text = _s(rec.get("message_text")) + " " + _s(rec.get("message_excerpt"))
    if EMAIL_RE.search(text) or re.search(r"\+\d[\d ()-]{6,}", text):
        return "EXPOSED"
    return "REDACTED"


def export_state(rec: dict) -> str:
    avail = rec.get("download_available", rec.get("available", True))
    return "PRESENT" if str(avail).lower() not in ("false", "0", "no") else "ABSENT"


def flow_state(rec: dict) -> str:
    return "ACTIVE" if str(rec.get("enabled")).lower() in ("true", "1") else "DISABLED"


def backup_state(rec: dict) -> str:
    return "RESTORABLE" if str(rec.get("restore_allowed")).lower() in ("true", "1") else "BLOCKED"


# collection / table name -> (category, state function)
COLLECTION_RULES: dict[str, tuple[str, Any]] = {
    "customer_profiles": ("USER_PROFILE", profile_state),
    "customer_profiles_primary": ("USER_PROFILE", profile_state),
    "customer_profiles_failover_replica": ("USER_PROFILE", profile_state),
    "customer_search_documents": ("SEARCH_INDEX_ENTRY", profile_state),
    "search_documents_primary": ("SEARCH_INDEX_ENTRY", profile_state),
    "customer_analytics_reports": ("ANALYTICS_PROFILE", analytics_state),
    "customer_analytics_profiles": ("ANALYTICS_PROFILE", analytics_state),
    "support_conversations": ("SUPPORT_MESSAGE", support_message_state),
    "support_records": ("SUPPORT_MESSAGE", support_message_state),
    "ticket_attachments": ("SUPPORT_ATTACHMENT", profile_state),
    "customer_export_jobs": ("USER_EXPORT", export_state),
    "customer_exports": ("USER_EXPORT", profile_state),
    "connector_configurations": ("DATA_FLOW", flow_state),
    "connector_subscriptions": ("DATA_FLOW", flow_state),
    "backup_catalog": ("BACKUP_ARCHIVE", backup_state),
    "backup_restore_entries": ("BACKUP_ARCHIVE", backup_state),
    "security_audit_events": ("SECURITY_AUDIT_LOG", profile_state),
    "security_audit_records": ("SECURITY_AUDIT_LOG", profile_state),
    "deleted_customer_references": ("ACCOUNT_TOMBSTONE", tombstone_state),
    "retained_order_records": ("LEGAL_HOLD_RECORD", profile_state),
}

# "record lookup" vocabulary shared by handover text, security ledger, operational_record_history and record_lookup logs
LOOKUP_RULES: dict[tuple[str, str], str] = {
    ("USER_PROFILE", "FOUND"): "PRESENT", ("USER_PROFILE", "NOT_FOUND"): "ABSENT",
    ("SEARCH_INDEX_ENTRY", "FOUND"): "PRESENT", ("SEARCH_INDEX_ENTRY", "NOT_FOUND"): "ABSENT",
    ("SECURITY_AUDIT_LOG", "FOUND"): "PRESENT",
    ("SUPPORT_MESSAGE", "IDENTIFIABLE"): "EXPOSED", ("SUPPORT_MESSAGE", "REDACTED"): "REDACTED",
    ("SUPPORT_ATTACHMENT", "FOUND"): "PRESENT", ("SUPPORT_ATTACHMENT", "NOT_FOUND"): "ABSENT",
    ("ANALYTICS_PROFILE", "IDENTIFIABLE"): "EXPOSED", ("ANALYTICS_PROFILE", "ANONYMIZED"): "ANONYMIZED",
    ("USER_EXPORT", "FOUND"): "PRESENT", ("USER_EXPORT", "NOT_FOUND"): "ABSENT",
    ("DATA_FLOW", "ACTIVE"): "ACTIVE", ("DATA_FLOW", "DISABLED"): "DISABLED",
    ("BACKUP_ARCHIVE", "ACTIVE"): "RESTORABLE", ("BACKUP_ARCHIVE", "BLOCKED"): "BLOCKED",
    ("ACCOUNT_TOMBSTONE", "IDENTIFIABLE"): "EXPOSED", ("ACCOUNT_TOMBSTONE", "REDACTED"): "REDACTED",
    ("LEGAL_HOLD_RECORD", "FOUND"): "PRESENT",
    ("MARKETING_PROFILE", "FOUND"): "PRESENT", ("MARKETING_PROFILE", "NOT_FOUND"): "ABSENT",
}
