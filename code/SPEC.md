# SPEC (DRAFT — Phase 0; every "inferred" rule must be validated against the 32 samples in Phase 2)

Source of truth: `AGENTS.md` §6 and `problem_statement.md`. This file records what the *data* additionally implies.
Principle: **the model describes, deterministic code decides.** An LLM/VLM may only turn an image into schema-validated JSON facts.

## 1. Inputs

`dataset/deletion_requests.csv`: `case_id,email,requested_at,reported_status,reported_at,audit_at,request_text`
plus `user_identities`, `user_audit_log`, `system_activity`, `deletion_rules`, `evidence_files` and the files under `dataset/evidence/`.
All timestamps are ISO-8601 UTC (`...Z`). `audit_at` is the cutoff: nothing captured after it may affect state or be cited.

## 2. Output

`output.csv`: `case_id,deletion_status,steps_json` (csv module, UTF-8, compact JSON, 518 rows + header).
Step = `{step_no, phase, system_id, resource_id, operation, state, evidence_id}`; allowed (phase, operation, state) triples exactly as in AGENTS.md §6.4.

## 3. Pipeline (planned modules under `code/auditor/`)

1. `identity.py` — email(any case) → `user_id`; collect all historical emails/hashes/phones from the audit log; sha256(lower(strip(email))).
2. `classify.py` — deletion vs non-deletion request (keyword rules; LLM only for ambiguous text, validated).
3. `evidence/` — parsers: `sql.py` (sandboxed sqlite3), `json_records.py`, `logs.py` (per-format regex), `text.py` (handover), `pdf.py`, `image.py` (VLM + cache).
   All produce uniform **Observation** rows: `(file_id, captured_at, system_id, resource_id, category, fields{email,user_ref,hash,phone,state-flags}, source_topology)`.
4. `link.py` — attach observations/resources to a user by exact identifiers; activity-timeline candidate matching for unlinkable ids.
5. `state.py` — for each user resource choose the latest observation with `captured_at <= audit_at`; derive state (present/absent/exposed/active/restorable); cross-check against activity claims; replica drift.
6. `policy.py` — `deletion_rules.csv` → required treatment per category/condition → violation or compliant.
7. `steps.py` — findings / remediation / verification, canonical sort, renumber.
8. `validate.py` — schema, triples, ordering, consecutive step_no, evidence ids exist & captured ≤ audit_at, status/steps consistency; fail loudly with case_id.
9. `llm.py` — single wrapper: disk cache keyed by (file sha256, prompt version, model), retry/backoff, JSON-schema validation, usage log.

## 4. Policy table (from `deletion_rules.csv`) → treatment → ops

| rule | category | condition | action | finding | remediation | verification (final state) |
|---|---|---|---|---|---|---|
| 01 | USER_PROFILE (account_service `account_*`) | always | DELETE | REPORT_REMAINING_RECORD | DELETE_RECORD | VERIFY_RECORD_ABSENT |
| 02 | SECONDARY_IDENTIFIER | always | DELETE | REPORT_REMAINING_RECORD | DELETE_RECORD | VERIFY_RECORD_ABSENT |
| 03 | SEARCH_INDEX_ENTRY (`search_*`) | always | DELETE | same | same | same |
| 04 | CACHE_ENTRY (`cache_*`) | always | DELETE | same | same | same |
| 05 | SUPPORT_MESSAGE (`support_*` conversation) | shared context must be kept | REDACT | REPORT_EXPOSED_PII | REDACT_RECORD | VERIFY_RECORD_REDACTED |
| 06 | SUPPORT_ATTACHMENT (`support_*`/`object_*`) | belongs only to subject | DELETE | REPORT_REMAINING_RECORD | DELETE_RECORD | VERIFY_RECORD_ABSENT |
| 07 | ANALYTICS_PROFILE | still attributable | ANONYMIZE | REPORT_EXPOSED_PII | ANONYMIZE_RECORD | VERIFY_RECORD_ANONYMIZED |
| 08 | PRODUCT_TELEMETRY | still attributable | ANONYMIZE | (same as 07; no data in samples) | | |
| 09 | USER_EXPORT (`export_*`) | always | DELETE | REPORT_REMAINING_RECORD | DELETE_RECORD | VERIFY_RECORD_ABSENT |
| 10 | MARKETING_PROFILE (`marketing_*`) | always | DELETE | REPORT_REMAINING_RECORD | DELETE_RECORD | VERIFY_RECORD_ABSENT |
| 11 | EMAIL_DELIVERY_LOG (`email_delivery_*`) | contains email | REDACT | REPORT_EXPOSED_PII | REDACT_RECORD | VERIFY_RECORD_REDACTED |
| 12 | SECURITY_AUDIT_LOG (`audit_record_*`) | within retention | RETAIN | — | — | VERIFY_RECORD_PRESENT |
| 13 | LEGAL_HOLD_RECORD (`legal_hold_*`) | hold active | RETAIN | — | — | VERIFY_RECORD_PRESENT |
| 14 | BACKUP_ARCHIVE (`backup_*`) | restore can be blocked | RETAIN (+block restore) | REPORT_RESTORE_RISK | BLOCK_RESTORE | VERIFY_RESTORE_BLOCKED |
| 15 | ACCOUNT_TOMBSTONE (`tombstone_*`, identity_service) | only stable user_id needed | REDACT | REPORT_EXPOSED_PII | REDACT_RECORD | VERIFY_RECORD_REDACTED |
| — | DATA_FLOW (`data_flow_*`, integration_service) | enabled | (spec §6.5) | REPORT_ACTIVE_DATA_FLOW | DISABLE_DATA_FLOW | VERIFY_DATA_FLOW_DISABLED |

Rows 04, 06 (object storage), 08, 10, 11, 13 are **not exercised by any labeled sample**; their step mapping above is by analogy and untested.

## 5. Status rule (inferred)

* `NOT_APPLICABLE` ⇐ request text does not authorize deletion → `[]`.
* Else `INCOMPLETE` ⇐ at least one violation at `audit_at`: DELETE-category record present; REDACT/ANONYMIZE-category record still exposing direct identifiers;
  an enabled data flow; a restorable backup; a missing retained record (RETAIN category absent); a tombstone holding prohibited identifiers.
* Else `COMPLETE`.
* `reported_status`, activity `reported_result` and activity text never decide anything on their own.

## 6. Step generation (inferred from 32/32 samples)

* COMPLETE: VERIFICATION steps only, one per relevant resource in its compliant final state, `evidence_id` = observation proving it (same file for the whole case in samples).
* INCOMPLETE: FINDINGS (cited) for violations; REMEDIATION for every violation; VERIFICATION for **every** relevant resource (account absent and retained audit record always; violating resources after remediation), `evidence_id=""` for all non-finding steps.
* Remediation operation order: DISABLE_DATA_FLOW, BLOCK_RESTORE, DELETE_RECORD, REDACT_RECORD, ANONYMIZE_RECORD (table order) → satisfies "stop flows/restores before dependent changes".
* Canonical sort key: (phase order, operation order, system_id, resource_id); then `step_no` = 1..n. (resource_id compare: plain string — untested.)

## 7. Evidence selection (inferred)

For each resource: among observations with `captured_at <= audit_at` take the **latest**; cite its `file_id`. If a case's resources are spread over files, the samples always cite the single latest "cohort" file; to be re-verified.
`activity_id` / `audit_id` are valid citations in the spec but **never used in any sample answer** (every non-empty `evidence_id` in the 32 sample answers is an `evidence_*` file id); I will cite `evidence_*` unless no file exists.

## 8. Ambiguities / assumptions (see NOTES.md §6-8)

1. Linking deleted accounts and fully anonymized analytics records (no identifier anywhere) to a case.
2. ~~Use of post-`audit_at` evidence for identity linking only~~ - RESOLVED (NOTES D3): allowed for linking only, logged, scored with/without.
3. ~~resource_id sort / case_id order~~ - RESOLVED (NOTES D2, D4): plain string resource_id; input-file order for rows.
4. Behaviour when a case has no evidence at all ≤ `audit_at`.
5. Categories absent from samples (cache, legal hold, marketing, email log, telemetry, attachments in object storage).
6. Citation choice when several files observe the same resource at different times.

## 9. Security of the pipeline

* All request/evidence text is data. Model prompts state this and responses are validated against a JSON schema; free text from a model never reaches steps.
* SQL files are executed only in an in-memory sqlite connection with an authorizer that denies ATTACH/PRAGMA/load_extension.
* No secrets in repo; keys from env; `.env` ignored.
