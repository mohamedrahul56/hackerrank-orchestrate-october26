"""Policy engine, driven by dataset/deletion_rules.csv.

The CSV says what must happen to each data category (DELETE / REDACT / ANONYMIZE / RETAIN). This module turns
(category, observed state) into a verdict. The mapping action -> operations is a small constant table (TREATMENTS);
nothing else in the code branches on a category name, so a new rule row with a known action works without code.

Two rows need a note:
  * BACKUP_ARCHIVE is RETAIN "when restore access can be blocked": the archive stays, but a restorable archive is a
    finding (REPORT_RESTORE_RISK -> BLOCK_RESTORE -> VERIFY_RESTORE_BLOCKED).
  * DATA_FLOW is not a data category in the CSV; the problem statement (section 6.5) requires disabling active
    flows, so it carries a fixed treatment below.
"""
from __future__ import annotations

import csv
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Treatment:
    finding: str | None          # operation reported when the record violates policy
    fix: str | None              # remediation operation
    verify: str                  # verification operation for the compliant end state
    bad: frozenset[str]          # observed states that violate the rule
    ok: frozenset[str]           # observed states that satisfy the rule
    verify_when_ok: bool = True  # compliant records get a verification step in the answer


TREATMENTS: dict[str, Treatment] = {
    "DELETE": Treatment("REPORT_REMAINING_RECORD", "DELETE_RECORD", "VERIFY_RECORD_ABSENT",
                        frozenset({"PRESENT"}), frozenset({"ABSENT"}), verify_when_ok=False),
    "REDACT": Treatment("REPORT_EXPOSED_PII", "REDACT_RECORD", "VERIFY_RECORD_REDACTED",
                        frozenset({"EXPOSED"}), frozenset({"REDACTED"})),
    "ANONYMIZE": Treatment("REPORT_EXPOSED_PII", "ANONYMIZE_RECORD", "VERIFY_RECORD_ANONYMIZED",
                           frozenset({"EXPOSED"}), frozenset({"ANONYMIZED"})),
    "RETAIN": Treatment(None, None, "VERIFY_RECORD_PRESENT", frozenset(), frozenset({"PRESENT"})),
    "RETAIN_BLOCK_RESTORE": Treatment("REPORT_RESTORE_RISK", "BLOCK_RESTORE", "VERIFY_RESTORE_BLOCKED",
                                      frozenset({"RESTORABLE"}), frozenset({"BLOCKED"})),
    "DISABLE_FLOW": Treatment("REPORT_ACTIVE_DATA_FLOW", "DISABLE_DATA_FLOW", "VERIFY_DATA_FLOW_DISABLED",
                              frozenset({"ACTIVE"}), frozenset({"DISABLED"})),
}
# The account is the subject of the request: its absence is always verified even though other absent DELETE-type
# records (search entries, exports, ...) are only reported when they still exist. (All 32 samples verify the account.)
ALWAYS_VERIFY_ABSENT = frozenset({"USER_PROFILE"})


@dataclass(frozen=True)
class Verdict:
    category: str
    state: str
    action: str
    treatment: Treatment
    violation: bool
    emit_verification: bool


class PolicyEngine:
    def __init__(self, rules_csv: Path):
        self.rules: dict[str, tuple[str, str]] = {}
        self.rule_ids: dict[str, str] = {}                       # category -> rule_id (for explanations)
        with rules_csv.open(newline="", encoding="utf-8") as fh:
            for r in csv.DictReader(fh):
                self.rules[r["data_category"]] = (r["required_action"].strip().upper(), r["condition"])
                self.rule_ids[r["data_category"]] = r.get("rule_id", "")

    def action_for(self, category: str) -> str:
        if category == "DATA_FLOW":
            return "DISABLE_FLOW"
        if category not in self.rules:
            raise KeyError(f"no rule for data category {category!r} in deletion_rules.csv")
        action, condition = self.rules[category]
        if action == "RETAIN" and "restore" in condition.lower():
            return "RETAIN_BLOCK_RESTORE"
        return action

    def treatment_for(self, category: str) -> Treatment:
        return TREATMENTS[self.action_for(category)]

    def judge(self, category: str, state: str) -> Verdict | None:
        """None when the state says nothing about policy (unknown state)."""
        action = self.action_for(category)
        t = TREATMENTS[action]
        if action == "RETAIN" and state == "EXPOSED":
            # a retained record keeps its place but may only carry the stable reference (rule_15's principle): redact the rest
            action, t = "REDACT", TREATMENTS["REDACT"]
        if state in t.bad:
            return Verdict(category, state, action, t, True, True)
        if state in t.ok:
            emit = t.verify_when_ok or (category in ALWAYS_VERIFY_ABSENT)
            return Verdict(category, state, action, t, False, emit)
        return None
