#!/usr/bin/env python3
"""Coverage of the deletion_rules.csv categories: how much of each category exists in the data, how many
main-set cases explicitly involve it, and how many labeled samples exercise it. Writes coverage_report.md.

IMPORTANT LIMITS (also printed in the report):
  * "main-set cases" counts only cases whose user is tied to a resource of that category by an EXPLICIT identifier
    (current/old email, user_id, email hash, phone) found in the same record/log trace/catalog row. Deleted accounts and
    fully anonymized records carry no identifier, so this is a LOWER BOUND, not the true involvement.
  * No time filter is applied (a resource counts even if its only evidence is later than the case's audit_at).
This is an estimate for planning synthetic tests; the Phase 2 linker will replace it.
"""
from __future__ import annotations

import csv
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
D = ROOT / "dataset"

# resource-id prefix -> category name used in deletion_rules.csv (support_ is split below)
PREFIX_CATEGORY = {
    "account": "USER_PROFILE", "search": "SEARCH_INDEX_ENTRY", "cache": "CACHE_ENTRY", "analytics": "ANALYTICS_PROFILE",
    "export": "USER_EXPORT", "marketing": "MARKETING_PROFILE", "email_delivery": "EMAIL_DELIVERY_LOG",
    "audit_record": "SECURITY_AUDIT_LOG", "legal_hold": "LEGAL_HOLD_RECORD", "backup": "BACKUP_ARCHIVE",
    "tombstone": "ACCOUNT_TOMBSTONE", "object": "SUPPORT_ATTACHMENT", "data_flow": "(DATA_FLOW: no rule row)",
}
RES = re.compile(r"\b(account|search|support|analytics|export|data_flow|backup|audit_record|tombstone|legal_hold|marketing|cache|email_delivery|object)_(\d+)\b")


def rd(name: str) -> list[dict[str, str]]:
    with (D / name).open(newline="", encoding="utf-8") as fh:
        return list(csv.DictReader(fh))


def category_of(res: str, blob: str) -> str:
    prefix = res.rsplit("_", 1)[0]
    if prefix == "support":
        return "SUPPORT_ATTACHMENT" if "attachment" in blob.lower() else "SUPPORT_MESSAGE"
    return PREFIX_CATEGORY[prefix]


def blobs() -> list[tuple[str, bool]]:
    """(text, from_evidence_file) record-sized chunks from every text-bearing evidence file + system_activity rows."""
    out: list[tuple[str, bool]] = []
    for p in sorted((D / "evidence").glob("*/*")):
        if p.suffix in (".png", ".pdf"):
            continue
        text = p.read_text(encoding="utf-8", errors="replace")
        if p.suffix == ".json":
            def walk(o, ctx=""):
                if isinstance(o, dict):
                    ctx = o.get("collection", ctx) if isinstance(o.get("collection"), str) else ctx
                    if "resource_id" in o:
                        out.append((ctx + " " + json.dumps(o, ensure_ascii=False), True))
                    for v in o.values():
                        walk(v, ctx)
                elif isinstance(o, list):
                    for v in o:
                        walk(v, ctx)
            walk(json.loads(text))
        else:
            lines = text.splitlines()
            groups: dict[str, list[str]] = defaultdict(list)
            for i, line in enumerate(lines):
                m = re.search(r"(?:trace_id|requestId)[=\":]+\s*\"?([\w-]+)", line)
                groups[m.group(1) if m else f"line{i}"].append(line)   # started/completed lines share a trace id
            out += [("\n".join(v), True) for v in groups.values()]
    out += [(r["details"] + " " + r["resource_id"], False) for r in rd("system_activity.csv")]
    return out


def main() -> int:
    ids = rd("user_identities.csv"); aud = rd("user_audit_log.csv")
    tok: dict[str, str] = {}
    for u in ids:
        for k in ("email", "email_hash", "phone"):
            tok[u[k].lower()] = u["user_id"]
        tok[u["user_id"]] = u["user_id"]
    for a in aud:
        if a["field_name"] in ("email", "email_hash", "phone"):
            tok[a["old_value"].lower()] = a["user_id"]
    # boundaries stop 'user_1' matching inside 'user_12' and 'a@b.com' inside 'xa@b.com'
    tokre = re.compile(r"(?<![A-Za-z0-9_.+-])(?:" + "|".join(sorted(map(re.escape, tok), key=len, reverse=True)) + r")(?![A-Za-z0-9_])", re.I)
    email_user = {u["email"].lower(): u["user_id"] for u in ids}
    email_user.update({a["old_value"].lower(): a["user_id"] for a in aud if a["field_name"] == "email"})

    all_res: dict[str, set[str]] = defaultdict(set)          # category -> resource ids
    linked: dict[str, set[str]] = defaultdict(set)           # category -> user ids
    in_evidence: dict[str, set[str]] = defaultdict(set)      # category -> ids seen in at least one evidence file
    for b, from_file in blobs():
        users = {tok[m.group(0).lower()] for m in tokre.finditer(b)}
        for m in RES.finditer(b):
            res = m.group(0)
            cat = category_of(res, b)
            all_res[cat].add(res)
            if from_file:
                in_evidence[cat].add(res)
            linked[cat] |= users

    main_cases = rd("deletion_requests.csv")
    case_user = {c["case_id"]: email_user[c["email"].strip().lower()] for c in main_cases}
    user_cases: dict[str, list[str]] = defaultdict(list)
    for c, u in case_user.items():
        user_cases[u].append(c)

    # labeled samples: category from the gold answer (support_ split by the treatment applied)
    gold = {r["case_id"]: json.loads(r["steps_json"]) for r in rd("sample_output.csv")}
    sample_cats: dict[str, set[str]] = defaultdict(set)
    for cid, steps in gold.items():
        for s in steps:
            res = s["resource_id"]; prefix = res.rsplit("_", 1)[0]
            if prefix == "support":
                treated = {x["operation"] for x in steps if x["resource_id"] == res}
                cat = "SUPPORT_ATTACHMENT" if treated & {"DELETE_RECORD", "VERIFY_RECORD_ABSENT"} else "SUPPORT_MESSAGE"
            else:
                cat = PREFIX_CATEGORY[prefix]
            sample_cats[cat].add(cid)

    rules = rd("deletion_rules.csv")
    order = [r["data_category"] for r in rules] + ["(DATA_FLOW: no rule row)"]
    action = {r["data_category"]: r["required_action"] for r in rules}
    L = ["# Coverage of deletion_rules.csv categories", "",
         "Generated by `python3 code/evaluation/coverage.py`. **Main-set counts are a lower bound** (explicit identifier links only; "
         "deleted accounts and anonymized records carry no identifier). No time filter applied.", "",
         "| category | rule action | distinct ids (evidence + activity) | of which in an evidence file | main-set cases explicitly linked (lower bound) | labeled samples covering | status |",
         "|---|---|---|---|---|---|---|"]
    uncovered = []
    for cat in order:
        n_res = len(all_res.get(cat, ()))
        n_main = sum(len(user_cases.get(u, ())) for u in linked.get(cat, ()))
        n_samp = len(sample_cats.get(cat, ()))
        status = "covered" if n_samp >= 2 else ("thin (1 sample)" if n_samp == 1 else ("**NO SAMPLE** -> synthetic cases needed" if n_res or cat in action else "no data"))
        if n_samp == 0:
            uncovered.append(cat)
        L.append(f"| {cat} | {action.get(cat, '-')} | {n_res} | {len(in_evidence.get(cat, ()))} | {n_main} | {n_samp} | {status} |")
    L += ["", "Categories with no labeled sample (need synthetic evaluation cases): " + ", ".join(uncovered), "",
          "Notes:", "* SECONDARY_IDENTIFIER and PRODUCT_TELEMETRY have a rule row but **no resource id prefix exists anywhere in the data** "
          "(0 resources), so they cannot occur in the main set as separate resources.",
          "* `support_*` ids are split into SUPPORT_MESSAGE / SUPPORT_ATTACHMENT by record context (collection `ticket_attachments`, attachment log events) "
          "in the evidence, and by the applied treatment (delete vs redact) in the sample answers.",
          "* Ids that occur **only in system_activity** (column 4 = 0 while column 3 > 0) are generic operational noise rows "
          "('Profile cache refresh processed cache_N', 'Transactional email delivery processed email_delivery_N'): no evidence file ever shows such a record's state or owner, so they cannot produce findings.",
          "* LEGAL_HOLD_RECORD exists only as `legal_hold_*` rows in SQL replicas / bundles; the legal retention register PDF names six users (user_08/12/17/21/24/29 = labeled samples case_08/12/17/21/24/29, **all six** of whose gold answers contain no legal-hold step), so that PDF is context, not a step source.",
          "* `object_*` (object storage) appears once (a catalog row about an attachment belonging only to one user).", ""]
    text = "\n".join(L)
    (ROOT / "code" / "evaluation" / "coverage_report.md").write_text(text, encoding="utf-8")
    print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
