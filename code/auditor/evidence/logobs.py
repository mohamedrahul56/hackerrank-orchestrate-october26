"""Log events -> Obs. The meaning of each event name lives in EVENT_RULES (one table, no scattered if/else)."""
from __future__ import annotations

from collections import defaultdict
from datetime import datetime
from pathlib import Path
from typing import Callable

from ..dataset import parse_ts
from .logparse import LogEvent, parse_log
from .records import (LOOKUP_RULES, EvidenceBase, Obs, analytics_state, backup_state, flatten, idents_from,
                      support_message_state, system_for)

Rule = tuple[str, Callable[[dict], "str | None"]]   # (category, fields -> state or None when the event says nothing)


def _truthy(v) -> bool:
    return str(v).lower() in ("true", "1", "yes")


def _flow_heartbeat(f: dict) -> str:
    """A connector that is enabled AND registered with the scheduler will still run (a recreation path)."""
    return "ACTIVE" if _truthy(f.get("enabled")) and _truthy(f.get("scheduler_registered", "true")) else "DISABLED"


def _flow_loaded(f: dict) -> str:
    return "ACTIVE" if _truthy(f.get("enabled")) or _truthy(f.get("scheduler_registered")) else "DISABLED"


def _restore_candidate(f: dict) -> str:
    return "RESTORABLE" if _truthy(f.get("eligible")) and not _truthy(f.get("deletion_block")) else "BLOCKED"


def _analytics_read(f: dict) -> str:
    return "EXPOSED" if str(f.get("direct_identifiers", "0")) not in ("0", "") else "ANONYMIZED"


EVENT_RULES: dict[str, Rule] = {
    "profile_lookup_completed": ("USER_PROFILE", lambda f: "PRESENT" if str(f.get("rows", "0")) != "0" and str(f.get("http_status", "200")) == "200" else "ABSENT"),
    "index_query_completed": ("SEARCH_INDEX_ENTRY", lambda f: "PRESENT" if f.get("result", "hit") == "hit" and str(f.get("total_hits", "1")) != "0" else "ABSENT"),
    "support_ticket_read_completed": ("SUPPORT_MESSAGE", support_message_state),
    "message_pii_scan": ("SUPPORT_MESSAGE", lambda f: "EXPOSED"),
    "message_read": ("SUPPORT_MESSAGE", support_message_state),
    "attachment_pii_scan": ("SUPPORT_ATTACHMENT", lambda f: "PRESENT"),
    "analytics_profile_read_completed": ("ANALYTICS_PROFILE", analytics_state),
    "analytics_profile_read": ("ANALYTICS_PROFILE", _analytics_read),
    "analytics_identity_scan": ("ANALYTICS_PROFILE", _analytics_read),
    # a record found only through the hash of an OLD email (opt-in contact row): a secondary identifier, rule_02 DELETE
    "contact_lookup_completed": ("SECONDARY_IDENTIFIER", lambda f: "PRESENT" if str(f.get("rows", "1")) != "0" else "ABSENT"),
    "contact_query_completed": ("SECONDARY_IDENTIFIER", lambda f: "PRESENT" if str(f.get("rows", "1")) != "0" else "ABSENT"),
    "deletion_marker_check": ("SECONDARY_IDENTIFIER", lambda f: "PRESENT" if not _truthy(f.get("marker_found")) else "ABSENT"),
    "connector_config_read_completed": ("DATA_FLOW", lambda f: "ACTIVE" if _truthy(f.get("enabled")) else "DISABLED"),
    "connector_heartbeat": ("DATA_FLOW", _flow_heartbeat),
    "connector_config_loaded": ("DATA_FLOW", _flow_loaded),
    "restore_policy_read_completed": ("BACKUP_ARCHIVE", backup_state),
    "restore_candidate_evaluated": ("BACKUP_ARCHIVE", _restore_candidate),
    "restore_authorization_completed": ("BACKUP_ARCHIVE", lambda f: "BLOCKED" if f.get("decision") == "deny" else "RESTORABLE"),
    "audit_record_read_completed": ("SECURITY_AUDIT_LOG", lambda f: "PRESENT"),
    "export_metadata_read_completed": ("USER_EXPORT", lambda f: "PRESENT" if _truthy(f.get("available")) else "ABSENT"),
    "export_download_check_completed": ("USER_EXPORT", lambda f: "PRESENT" if _truthy(f.get("available")) else "ABSENT"),
}


def load_log(base: EvidenceBase, file_id: str, path: Path, file_t: datetime) -> None:
    events = parse_log(path.read_text(encoding="utf-8", errors="replace"))
    groups: dict[str, list[LogEvent]] = defaultdict(list)
    for e in events:
        groups[e.trace if e.trace else f"line{e.line_no}"].append(e)
    for evs in groups.values():
        shared = idents_from(p for e in evs for p in flatten({k: v for k, v in e.fields.items()}))
        for e in evs:
            f = e.fields
            t = min(parse_ts(e.ts), file_t)
            if e.event == "record_lookup":
                state = LOOKUP_RULES.get((f.get("record_type", ""), f.get("result", "")))
                if state and f.get("resource_id"):
                    base.obs.append(Obs(file_id, file_t, t, f.get("system") or system_for(f["resource_id"]), f["resource_id"],
                                        f["record_type"], state, idents_from(flatten({"s": f.get("subject_reference", "")})),
                                        "log:record_lookup"))
                continue
            rule = EVENT_RULES.get(e.event)
            rid = f.get("resource_id")
            if rule is None or not rid:
                continue
            state = rule[1](f)
            if state:
                base.obs.append(Obs(file_id, file_t, t, system_for(rid), rid, rule[0], state, shared, f"log:{e.event}"))
