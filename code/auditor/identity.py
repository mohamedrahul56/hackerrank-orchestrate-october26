"""Identity resolver: request email -> user(s) and every identifier that user has ever had.

Facts measured on the data (NOTES.md): email_hash = sha256(lower(strip(email))) for all current rows and all
audit-log pairs; every request email resolves to exactly one user; audit log only changes email/email_hash/phone.
Still handled generally: case/whitespace differences, an email reassigned to another user later (resolved by who
owned it at `requested_at`), and emails that match nobody or several users.
"""
from __future__ import annotations

import hashlib
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

from .dataset import _rows, parse_ts, try_ts
from .evidence.records import norm_phone


def norm_email(e: str) -> str:
    return e.strip().lower()


def email_hash(e: str) -> str:
    return hashlib.sha256(norm_email(e).encode("utf-8")).hexdigest()


@dataclass
class Person:
    user_id: str
    name: str
    current_email: str
    # (email, valid_from or None, valid_until or None) - None = unbounded
    email_spans: list[tuple[str, datetime | None, datetime | None]] = field(default_factory=list)
    hashes: set[str] = field(default_factory=set)
    phones: set[str] = field(default_factory=set)
    updated_at: datetime | None = None

    @property
    def emails(self) -> set[str]:
        return {e for e, _, _ in self.email_spans}

    def tokens(self) -> frozenset[str]:
        """Every identifier string that may identify this person inside evidence (emails, user_id, hashes, phones)."""
        return frozenset(self.emails | self.hashes | self.phones | {self.user_id})


class IdentityResolver:
    def __init__(self, dataset_root: Path):
        self.people: dict[str, Person] = {}
        self._by_email: dict[str, list[Person]] = defaultdict(list)
        self._token_users: dict[str, set[str]] = defaultdict(set)
        self._load(dataset_root)

    def _load(self, root: Path) -> None:
        self.warnings: list[str] = []
        for r in _rows(root / "user_identities.csv", ("user_id", "email")):
            if not r["user_id"].strip() or not r["email"].strip():
                self.warnings.append(f"identity row skipped (empty user_id or email): {r.get('user_id', '')!r}")
                continue
            p = Person(r["user_id"].strip(), r.get("name", ""), norm_email(r["email"]))
            p.updated_at = try_ts(r.get("updated_at"))
            p.hashes |= {email_hash(r["email"])} | ({r["email_hash"].strip().lower()} if r.get("email_hash", "").strip() else set())
            if norm_phone(r.get("phone", "")):
                p.phones.add(norm_phone(r["phone"]))
            self.people[p.user_id] = p
        changes: dict[str, list[dict]] = defaultdict(list)
        try:
            log_rows = _rows(root / "user_audit_log.csv", ("user_id", "event_time", "field_name"))
        except Exception as e:  # noqa: BLE001 - history is optional: without it only current identifiers resolve
            self.warnings.append(f"user_audit_log.csv not usable ({e}); only current identifiers will resolve")
            log_rows = []
        for r in log_rows:
            if try_ts(r["event_time"]) is None:
                self.warnings.append(f"audit-log row {r.get('audit_id', '?')} skipped (bad event_time)")
                continue
            changes[r["user_id"]].append(r)
        for uid, rows in changes.items():
            person = self.people.get(uid)
            if person is None:
                continue
            rows.sort(key=lambda r: r["event_time"])
            email_rows = [r for r in rows if r["field_name"] == "email"]
            for r in rows:
                if r["field_name"] == "email_hash":
                    person.hashes |= {r["old_value"].lower(), r["new_value"].lower()}
                elif r["field_name"] == "phone":
                    person.phones |= {x for x in (norm_phone(r["old_value"]), norm_phone(r["new_value"])) if x}
            prev_until: datetime | None = None
            spans: list[tuple[str, datetime | None, datetime | None]] = []
            for r in email_rows:                                    # old email valid until the change time
                when = parse_ts(r["event_time"])
                spans.append((norm_email(r.get("old_value", "")), prev_until, when))
                person.hashes.add(email_hash(r["old_value"])); person.hashes.add(email_hash(r["new_value"]))
                prev_until = when
            spans.append((person.current_email, prev_until, None))
            person.email_spans = spans
        for p in self.people.values():
            if not p.email_spans:
                # never changed its email: the identity snapshot's updated_at is the earliest time we can vouch for it
                p.email_spans = [(p.current_email, p.updated_at, None)]
            for address in p.emails:
                self._by_email[address].append(p)
            for t in p.tokens():
                self._token_users[t].add(p.user_id)

    # ------------------------------------------------------------------ public API
    def resolve(self, email: str, at: datetime | None = None) -> list[Person]:
        """People who own `email` (current or historical). If several, those who owned it at `at` win."""
        cands = self._by_email.get(norm_email(email), [])
        if len(cands) <= 1 or at is None:
            return list(cands)
        owners = [p for p in cands if any(e == norm_email(email) and (a is None or a <= at) and (b is None or at < b)
                                           for e, a, b in p.email_spans)]
        return owners or list(cands)

    def users_for_token(self, token: str) -> set[str]:
        return self._token_users.get(token, set())
