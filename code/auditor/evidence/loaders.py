"""Format loaders: SQL snapshots, JSON bundles/exports/ledgers, text handovers -> Obs (logs are in logobs.py)."""
from __future__ import annotations

import json
import re
import sqlite3
from datetime import datetime
from pathlib import Path
from typing import Any

from ..dataset import parse_ts
from .records import (COLLECTION_RULES, LOOKUP_RULES, Coverage, EvidenceBase, Obs, flatten, idents_from,
                      system_for)

# ----------------------------------------------------------------------------- SQL
_ALLOWED = {sqlite3.SQLITE_CREATE_TABLE, sqlite3.SQLITE_CREATE_INDEX, sqlite3.SQLITE_INSERT, sqlite3.SQLITE_TRANSACTION,
            sqlite3.SQLITE_SELECT, sqlite3.SQLITE_READ, sqlite3.SQLITE_FUNCTION, sqlite3.SQLITE_UPDATE,
            sqlite3.SQLITE_REINDEX}


def _authorizer(action, arg1, arg2, dbname, source):
    """Evidence .sql files are untrusted text: allow only schema creation, INSERT and reads. No ATTACH, no PRAGMA writes."""
    if action in _ALLOWED:
        return sqlite3.SQLITE_OK
    if action == sqlite3.SQLITE_PRAGMA and arg1 == "foreign_keys":
        return sqlite3.SQLITE_OK
    return sqlite3.SQLITE_DENY


def read_sql_tables(path: Path) -> dict[str, list[dict[str, Any]]]:
    con = sqlite3.connect(":memory:")
    con.enable_load_extension(False) if hasattr(con, "enable_load_extension") else None
    con.set_authorizer(_authorizer)
    try:
        con.executescript(path.read_text(encoding="utf-8"))
        con.set_authorizer(None)
        tables: dict[str, list[dict[str, Any]]] = {}
        for (name,) in con.execute("SELECT name FROM sqlite_master WHERE type='table'").fetchall():
            cur = con.execute(f'SELECT * FROM "{name}"')
            cols = [c[0] for c in cur.description]
            tables[name] = [dict(zip(cols, row)) for row in cur.fetchall()]
        return tables
    finally:
        con.close()


def _record_obs(base: EvidenceBase, file_id: str, file_t: datetime, t: datetime, collection: str, rec: dict,
                source: str, system: str | None = None) -> None:
    try:
        rule = COLLECTION_RULES.get(collection)
        rid = rec.get("resource_id")
        if rule is None or not rid:
            return
        category, state_fn = rule
        until = None
        if rec.get("retained_until"):
            try:
                until = parse_ts(str(rec["retained_until"]))
            except ValueError:                                   # an unreadable end date is "no end date stated" (the hold stays active), not a lost record
                base.warnings.append(f"{file_id}: {rid}: retained_until {str(rec['retained_until'])[:30]!r} is not a time; treated as no end date")
        base.obs.append(Obs(file_id, file_t, t, system or system_for(rid), rid, category, state_fn(rec),
                            idents_from(flatten(rec)), source, until=until))
    except (ValueError, KeyError, TypeError, AttributeError) as e:      # one malformed record must not cost the rest of the file
        base.warnings.append(f"{file_id}: a record in {collection} skipped ({type(e).__name__}: {e})")


def load_sql(base: EvidenceBase, file_id: str, path: Path, file_t: datetime) -> None:
    tables = read_sql_tables(path)
    text = path.read_text(encoding="utf-8")
    for name, rows in tables.items():
        if name == "snapshot_scope":                               # inventory: which systems this snapshot covers
            for r in rows:
                try:
                    base.coverage.append(Coverage(file_id, file_t, parse_ts(r["captured_at"]), r["origin_system"], "sql:snapshot_scope"))
                except (ValueError, KeyError, TypeError, AttributeError) as e:
                    base.warnings.append(f"{file_id}: a snapshot_scope row skipped ({type(e).__name__}: {e})")
            continue
        for r in rows:
            try:
                t = min(parse_ts(r["updated_at"]), file_t) if r.get("updated_at") else file_t
                if "record_json" in r:                              # replica/primary tables: JSON payload + collection name
                    rec = json.loads(r["record_json"])
                    _record_obs(base, file_id, file_t, t, r["collection_name"], rec, f"sql:{name}", r.get("origin_system"))
                else:                                               # typed inventory tables / hand-made account tables
                    _record_obs(base, file_id, file_t, t, name, _normalise_typed_row(name, r), f"sql:{name}")
            except (ValueError, KeyError, TypeError, AttributeError) as e:
                base.warnings.append(f"{file_id}: a row of {name} skipped ({type(e).__name__}: {e})")
    # full-table snapshot of the account system for the hand-made failover file
    if any(n.startswith("customer_profiles_") for n in tables):
        base.coverage.append(Coverage(file_id, file_t, file_t, "account_service", "sql:customer_profiles_*"))


def _normalise_typed_row(table: str, r: dict) -> dict:
    """Typed inventory rows use different column names than the JSON collections; map the few that matter."""
    r = dict(r)
    if table in ("customer_profiles", "customer_profiles_primary", "customer_profiles_failover_replica"):
        r.setdefault("customer_reference", r.get("user_id"))
    if table == "support_records":
        r["message_text"] = r.get("message_excerpt", "")
    if table == "customer_exports":
        r["download_available"] = True
    return r


# ----------------------------------------------------------------------------- JSON
def _walk_service_export(base: EvidenceBase, file_id: str, file_t: datetime, doc: dict) -> None:
    """service_resource_export_*: per-service lists of point-in-time lookups, each with its own observed_at."""
    for svc, lists in doc.get("service_exports", {}).items():
        for list_name, items in lists.items():
            for it in items:
                try:
                    _walk_item(base, file_id, file_t, svc, list_name, it)
                except (ValueError, KeyError, TypeError, AttributeError) as e:
                    base.warnings.append(f"{file_id}: an item of {svc}.{list_name} skipped ({type(e).__name__}: {e})")


def _walk_item(base: EvidenceBase, file_id: str, file_t: datetime, svc: str, list_name: str, it: dict) -> None:
    rid = it.get("resource_id")
    if not rid:
        return
    t = min(parse_ts(it["observed_at"]), file_t) if it.get("observed_at") else file_t
    src = f"json:{svc}.{list_name}"
    if list_name == "profile_lookups":
        prof = it.get("profile")
        present = it.get("status_code") == 200 and prof is not None
        base.obs.append(Obs(file_id, file_t, t, svc, rid, "USER_PROFILE", "PRESENT" if present else "ABSENT",
                            idents_from(flatten(prof or {})), src))
    elif list_name == "index_queries":
        docs = (it.get("hits") or {}).get("documents") or []
        present = (it.get("hits") or {}).get("total", len(docs)) > 0
        idents = idents_from(flatten(docs)) | idents_from([("query", str(it.get("query") or ""))])
        base.obs.append(Obs(file_id, file_t, t, svc, rid, "SEARCH_INDEX_ENTRY", "PRESENT" if present else "ABSENT", idents, src))
    elif list_name == "profiles":
        _record_obs(base, file_id, file_t, t, "customer_analytics_reports", it, src, svc)
    elif list_name == "tickets":
        _record_obs(base, file_id, file_t, t, "support_conversations", it, src, svc)
    elif list_name == "exports":
        base.obs.append(Obs(file_id, file_t, t, svc, rid, "USER_EXPORT", "PRESENT", idents_from(flatten(it)), src))
    elif list_name == "connectors":
        _record_obs(base, file_id, file_t, t, "connector_configurations", it, src, svc)
    elif list_name == "restore_entries":
        _record_obs(base, file_id, file_t, t, "backup_catalog", it, src, svc)
    elif list_name == "audit_records":
        _record_obs(base, file_id, file_t, t, "security_audit_events", it, src, svc)


def _lookup_obs(base: EvidenceBase, file_id: str, file_t: datetime, t: datetime, system: str, rid: str,
                record_type: str, result: str, who: str, source: str) -> None:
    state = LOOKUP_RULES.get((record_type, result))
    if state is None:
        base.warnings.append(f"{file_id}: unknown lookup ({record_type}, {result}) for {rid}")
        return
    base.obs.append(Obs(file_id, file_t, t, system, rid, record_type, state, idents_from([("subject", who)]), source))


def load_json(base: EvidenceBase, file_id: str, path: Path, file_t: datetime) -> None:
    doc = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(doc, dict):
        base.warnings.append(f"{file_id}: {path.name} is not a JSON object")
        return

    def guarded(label: str, fn) -> None:
        try:
            fn()
        except (ValueError, KeyError, TypeError, AttributeError) as e:     # one malformed snapshot / record never costs the rest of the file
            base.warnings.append(f"{file_id}: {label} skipped ({type(e).__name__}: {e})")

    if "service_exports" in doc:
        _walk_service_export(base, file_id, file_t, doc)
        return
    if "snapshots" in doc:                                           # bundle: several dated snapshots in one file
        for snap in doc["snapshots"]:
            guarded("a snapshot", lambda snap=snap: _load_snapshot(base, file_id, file_t, snap))
        return
    if "records" in doc and "collection" in doc:                     # hand-made single-collection files
        t = parse_ts(doc["captured_at"])
        for rec in doc["records"]:
            _record_obs(base, file_id, file_t, t, doc["collection"], rec, f"json:{doc.get('source', '')}.{doc['collection']}")
        return
    if "records" in doc:                                             # ledgers / manifests / connector configs
        t = parse_ts(doc["exported_at"])
        for rec in doc["records"]:
            guarded("a ledger record", lambda rec=rec: _load_ledger_record(base, file_id, file_t, t, doc, rec, path))
        return
    base.warnings.append(f"{file_id}: unrecognised JSON layout in {path.name}")


def _load_snapshot(base: EvidenceBase, file_id: str, file_t: datetime, snap: dict) -> None:
    t = min(parse_ts(snap["captured_at"]), file_t) if parse_ts(snap["captured_at"]) <= file_t else parse_ts(snap["captured_at"])
    coll, store = snap["collection"], snap["data_store"]
    if coll == "operational_record_history":                         # lookup vocabulary
        for r in snap["records"]:
            try:
                _lookup_obs(base, file_id, file_t, parse_ts(r["recorded_at"]), r["system"], r["resource_id"],
                            r["record_type"], r["result"], r.get("lookup_value", ""), f"json:{store}.{coll}")
            except (ValueError, KeyError, TypeError, AttributeError) as e:
                base.warnings.append(f"{file_id}: a lookup record skipped ({type(e).__name__}: {e})")
        return
    base.coverage.append(Coverage(file_id, file_t, t, store, f"json:{store}.{coll}"))
    for rec in snap["records"]:
        _record_obs(base, file_id, file_t, t, coll, rec, f"json:{store}.{coll}", store)


def _load_ledger_record(base: EvidenceBase, file_id: str, file_t: datetime, t: datetime, doc: dict, rec: dict, path: Path) -> None:
    if "record_type" in rec:                                          # security_audit_ledger: lookup vocabulary
        _lookup_obs(base, file_id, file_t, parse_ts(rec.get("recorded_at", doc["exported_at"])), rec["system"],
                    rec["resource_id"], rec["record_type"], rec["result"], rec.get("subject_reference", ""), "json:ledger")
    elif "download_available" in rec or "file_name" in rec or "owner_email" in rec:
        _record_obs(base, file_id, file_t, t, "customer_export_jobs", rec, "json:export_manifest")
    elif "connector_name" in rec:
        _record_obs(base, file_id, file_t, t, "connector_configurations", rec, "json:connector_configurations")
    else:
        base.warnings.append(f"{file_id}: unrecognised record in {path.name}: {sorted(rec)[:5]}")


# ----------------------------------------------------------------------------- text handover
HANDOVER = re.compile(r"^-\s*(\S+)\s*\|\s*(\w+)\s*\|\s*(\S+)\s*\|\s*(\w+)\s*\|\s*(\w+)\s*\|\s*(.*?)\s*$")


def load_text(base: EvidenceBase, file_id: str, path: Path, file_t: datetime) -> None:
    for line in path.read_text(encoding="utf-8").splitlines():
        m = HANDOVER.match(line)
        if m:
            ts, system, rid, rtype, result, who = m.groups()
            try:
                _lookup_obs(base, file_id, file_t, parse_ts(ts), system, rid, rtype, result, who, "txt:handover")
            except ValueError as e:
                base.warnings.append(f"{file_id}: a handover line skipped ({e})")
