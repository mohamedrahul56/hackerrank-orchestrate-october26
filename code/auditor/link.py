"""Link resources to users by exact identifiers found in the evidence.

A resource belongs to a user when any observation of it carries an identifier that belongs to that user
(current/old email, user_id, email hash, phone). Names and look-alike emails never match.
`first_link_time` records WHEN the earliest linking evidence was captured so the pipeline can tell whether a link
needed evidence captured after audit_at (allowed only for linking, and logged).
"""
from __future__ import annotations

from collections import defaultdict
from datetime import datetime

from .evidence.records import EvidenceBase, Obs
from .identity import IdentityResolver


class Linker:
    def __init__(self, base: EvidenceBase, resolver: IdentityResolver, catalog: list[dict]):
        self.by_resource: dict[str, list[Obs]] = defaultdict(list)
        # (resource, user) -> sorted list of (file_t, file_id) of observations that link them
        self.links: dict[tuple[str, str], list[tuple[datetime, str]]] = defaultdict(list)
        self.catalog_systems: dict[str, str] = {}
        for o in base.obs:
            self.by_resource[o.resource].append(o)
            for tok in o.idents:
                for uid in resolver.users_for_token(tok):
                    self.links[(o.resource, uid)].append((o.file_t, o.file_id))
        for row in catalog:                                  # catalog rows are explicit links too (no state)
            self.catalog_systems[row["resource_id"]] = row["system"]
            for tok in row["idents"]:
                for uid in resolver.users_for_token(tok):
                    self.links[(row["resource_id"], uid)].append((row["t"], row["activity_id"]))
        for v in self.links.values():
            v.sort()
        self.user_resources: dict[str, set[str]] = defaultdict(set)
        self.resource_users: dict[str, set[str]] = defaultdict(set)
        for (rid, uid) in self.links:
            self.user_resources[uid].add(rid)
            self.resource_users[rid].add(uid)

    def resources_of(self, users: set[str]) -> set[str]:
        out: set[str] = set()
        for u in users:
            out |= self.user_resources.get(u, set())
        return out

    def first_link_time(self, resource: str, users: set[str]) -> tuple[datetime, str] | None:
        cands = [self.links[(resource, u)][0] for u in users if (resource, u) in self.links]
        return min(cands) if cands else None

    def link_ids_before(self, resource: str, users: set[str], cutoff: datetime) -> bool:
        """True if some linking evidence for (resource, any user) was captured at/before `cutoff`."""
        t = self.first_link_time(resource, users)
        return t is not None and t[0] <= cutoff
