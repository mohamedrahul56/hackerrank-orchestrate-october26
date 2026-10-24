# NOTES — Phase 0 exploration (no pipeline code yet)

Everything below was measured on the files in `dataset/` only. "Sample" = `sample_*.csv`
(case_01..case_32), "main" = `deletion_requests.csv` (case_33..case_550, 518 rows).
Throw-away exploration scripts live outside the repo (scratchpad) and are not part of the solution.

## 1. Repo / logging instructions (what they say)

* `AGENTS.md` (also pulled in by `CLAUDE.md`) is the contract. Agents must append to **`log.txt` at the repo root**
  (next to AGENTS.md): a `SESSION START` entry (§5.1) and, after every user turn, a per-turn entry (§5.2) containing
  verbatim user prompt (secrets redacted), 2-5 line response summary, actions, and a `Context:` block with
  `tool=` and `model=` lines that must be exact (never `unknown`/generic). Append-only. `log.txt` is uploaded as `chat_transcript`.
* The October end time is **not configured** anywhere, so no countdown is shown.
* `README.md`: run starter with `python3 code/main.py`; write `output.csv` at repo root; `code.zip` =
  `zip -r code.zip README.md code -x "*/__pycache__/*" "*/.venv/*" ...` — **no `dataset/`, no nested archives**;
  reports must be Markdown inside `code/evaluation/` (raw `.csv/.json/.sql/.log` are ignored by the code judge).
* `agent_trace_instructions.md`: a **separate fifth deliverable** — `agent-trace.zip` with the raw Claude Code session JSONL
  (`~/.claude/projects/...`) plus `/export`. Not mentioned in the original task list; flagging it.
* Challenge/submission URL (AGENTS.md §4.1, must be given verbatim when asked):
  https://wwww.hackerrank.com/contests/hackerrank-orchestrate-october26/challenges/ghost-data-still-exists
  (note: AGENTS.md literally has four `w`s; I reproduce it exactly as instructed.)
* Starter code: `code/main.py` and `code/evaluation/main.py` exist (both empty).

## 2. Dataset facts

| file | rows | notes |
|---|---|---|
| deletion_requests.csv | 518 | case_33..case_550, **file order is numeric** (case_33, case_34, ... case_100 ...) |
| sample_deletion_requests / sample_output | 32 | case_01..case_32; 22 INCOMPLETE, 10 COMPLETE, **0 NOT_APPLICABLE** |
| user_identities.csv | 850 | user_1.. ; sha256(lowercase email) == email_hash for 850/850 |
| user_audit_log.csv | 306 | only fields `email`, `email_hash`, `phone` change (no name changes); 107 email changes, each with a paired hash row |
| system_activity.csv | 4011 | 17 event types, results SUCCESS 2914 / FAILED 721 / PARTIAL 376 |
| deletion_rules.csv | 15 | categories listed in SPEC.md |
| evidence_files.csv | 94 | 24 SQL, 26 logs, 29 JSON/TXT, 11 PNG, 4 PDF (manifest has no case linkage) |

* **No overlap** between sample and main rows (case ids and content). Two *users* appear in both
  (user_07 sample case_07 / main case_61; user_17 sample case_17 / main case_46) but with different `audit_at`
  (time-travel pairs). Main set also has one user with two requests (user_18: case_33 and case_52, different audit_at).
  No main row carries expected output.
* **Identity resolution is clean**: every request email (lower/strip) resolves to exactly one user, 418 via current email,
  100 via a historical email (3/32 in samples). No email maps to 0 or >1 users; no historical email equals a current one.
  20 request emails are upper-case (e.g. `CHOI.JIHOON94@GMAIL.COM`) and the same upper-case form appears in evidence → always
  compare case-insensitively. Hash algorithm = sha256 of lowercased/stripped email (verified on all current + all 107 audit pairs).
* `reported_status` is nearly uninformative (sample crosstab, reported → true):
  COMPLETE → 6 COMPLETE / 11 INCOMPLETE; PARTIALLY_COMPLETE → 1 / 4; NOT_STARTED → 3 / 7.
* **NOT_APPLICABLE**: main has exactly 10 non-deletion requests, case_533..case_542 (phone update, stop promo email,
  data download, login reset, surname fix, marketing opt-out, "what do you hold", invoice copy, change email, copy of data).
  All explicit and in English; `system_activity` independently has 10 `CLASSIFY_REQUEST ... as non-deletion` rows for them
  (accounts also visible in `security_audit_ledger_20261001.json` with `result=FOUND`). Beware: requests that merely *ask to verify*
  a deletion ("Can you verify my account was deleted?", samples 21,25-28) are **deletion audits**, not NOT_APPLICABLE.
* No prompt-injection strings found in request_text or evidence (grepped for ignore/instruction/system prompt/etc.). Defenses
  still required because evaluation data may differ from what I can see; treat as data.

## 3. How the evidence is organised (the part that drives everything)

Evidence files are **cohort snapshots**: each file covers a batch of ~10 consecutive users and shows the *current state of
every resource of those users* in one of several formats. The same facts reappear in different formats:

| format | files | how to read | what it exposes |
|---|---|---|---|
| SQL "inventory" (`customer_data_inventory_*`) | 11 | sqlite3; 8 typed tables + `snapshot_scope` | per-system rows, `customer_reference`/email/hash columns |
| SQL "replica" (`read/regional/reporting/failover_replica_*`) | 11 | sqlite3; `customer_data_primary` + replica table(s), JSON in `record_json` | **replication drift**: record present in replica table but absent in primary |
| SQL tiny hand-made (54, 57) | 2 | sqlite3 | per-case drift (e.g. failover replica still holds `account_005`) |
| JSON bundle (`customer_data_snapshot_bundle_*`) | 12 | json; list of dated `snapshots[]` (each own `captured_at`) | includes later snapshots than file date (e.g. bundle 09-17 contains an ACTIVE account snapshot dated 09-09) |
| JSON service export (`service_resource_export_*`) | 11 (+6 other small JSON records) | json; `service_exports.<svc>.<list>` with `observed_at`, 404 lookups | accounts appear only as `profile:null, status_code:404` — **no identifier** |
| logs (readback traces, syslog, structured, k8s-style, cloud runtime) | 26 | regex per format; key=value lines | `lookup_key=<email|user_id>`, `correlation_key=<sha256>`, `rows=0`, `marker_found=false` ... |
| TXT handover | 1 | line format `ts | system | resource | TYPE | RESULT | key` | `USER_PROFILE NOT_FOUND`, `ANALYTICS_PROFILE IDENTIFIABLE/ANONYMIZED`, `DATA_FLOW ACTIVE/DISABLED` ... |
| PDF | 4 | `pdftotext`/pypdf works (text layer) | statement, support attachment CS-4827/support_001, legal retention register (`ORD…|user_08|…|Retain until`), compliance journal (audit_record_003) |
| PNG | 11 | **no OCR installed**; needs a VLM | admin consoles: marketing profile, deleted-account reference `user_17` ("Retained reference"), backup/restore console, object storage browser, ... |

Probably only ~12 main cases (the ones whose users are the low-numbered hand-authored ids 3,7,9,11,13,14,16,17,18,20,22 — to be confirmed by tracing in Phase 2) depend on PNG/PDF content.
Everything else is machine-parseable, so **an LLM is not needed for the bulk of the data**; the only unavoidable VLM use is the 11 PNGs
(and maybe a fallback for odd log lines).

### 3.1 Resource ↔ user linking (the central difficulty)

A resource belongs to a user when evidence ties it to one of the user's identifiers: current/old **email** (case-insens.),
`user_id` (as `customer_reference`, `subject_reference`, `subject_selector`, `user_id`), **email hash** (e.g. `correlation_key`, `email_hash`;
the hash of an *old* email links records that carry only a hash), **phone** (current/old), or catalog `LINK_RESOURCE_IDENTITY` rows.
Exact-identifier match only — decoys such as `jonas.berglund@…` / `joana.berg@…` next to `jonas.berg@…` must not match
(sample 28 literally says "a search result with a similar email belongs to someone else").

But in the expected outputs many resources have **no identifier in any evidence**:

* deleted accounts (profile `null`, 404) — e.g. case_25 `account_3136`: the only place tying it to the user is the activity stream timeline;
* fully anonymized analytics records (all fields null) — e.g. `analytics_1895/4566/4574/6077/5335` for cases 25-28/21;
* some records hidden behind a *later* snapshot (case_26 `account_5684` is linked to `user_26` only by a bundle captured after audit_at).

Measured on the 32 samples: of 111 expected (case, resource) pairs, a crude identifier-window matcher links 74; the rest are
(a) accounts/analytics that are unlinkable by identifier (≈20) and (b) things my crude matcher missed (multi-line log pairs).

**Activity timeline anchor.** For generated cases every case has a burst of `deletion_worker` activity anchored on its `reported_at` (R):
`DELETE_RECORD account` at ≈ R-45..R-70 min, `ANONYMIZE` analytics ≈ R-29 min, `REDACT` support/tombstone ≈ R-22 min, `DISABLE_DATA_FLOW` ≈ R-15 min,
`BLOCK_RESTORE` ≈ R-8 min (these are the *claims of success* for compliant cases), or later bursts after R for
cases where the work was not done: `CHECK_DATA_FLOW` ≈ R+40-60 min, `DELETE search` ≈ R+1h, `REDACT support` ≈ R+1h20, `ANONYMIZE analytics` ≈ R+1h40,
usually FAILED (offsets measured on samples 22-30 only; roughly, with jitter).
539 `DELETE_RECORD deletion_worker→account_service` rows exist for 540 deletion cases (32+518-10) → almost every case has exactly one.
Problem: `reported_at` is **not unique** (238 distinct values for 518 main rows; up to 4-6 cases share an R), and the burst timestamps of
cases sharing an R interleave with ~1-min jitter. So the activity stream narrows candidates but cannot always assign an unlinkable account/analytics id to a case.
Candidate disambiguators I will test in Phase 2 (measured on samples, not assumed): exclusion of resources already linked to other users;
cohort count matching (k unlinked resources vs k unlinked cases in the same R-group); position within bundle JSON (generation order = user order).
**Expected loss is concentrated in step fields for unlinkable compliant resources, not in `deletion_status`.**

### 3.2 State reconstruction at `audit_at`

* Each evidence file has a capture time (manifest `captured_at`; SQL header `-- Captured at`; JSON `captured_at/exported_at`; per-record `observed_at`).
* Cited evidence in every sample is the **latest capture ≤ audit_at that observes the resource** (e.g. case_12 audit 09-15T12:00 cites evidence_01
  captured 09-15T11:59:39; case_22 audit 09-21T10:00:00 cites evidence_05 captured 09-21T10:00:00 — equal timestamps count as ≤).
  Account-absence steps in the hand-made cases cite the **account DB snapshot** (`evidence_54`) even though the account row is simply missing from it
  (absence proven by a snapshot that covers the account system).
* Evidence captured after `audit_at` must be ignored for state and never cited (bundle 09-17, handover 10-01 for earlier audits, etc.).
  Using later evidence only to *link* a resource id to a user is a judgment call (needed for case_26's account) → decision to confirm.
* Replication drift: row in a replica table, absent in primary ⇒ stale copy ⇒ `REPORT_REMAINING_RECORD` (case_05 failover replica kept `account_005`
  although `DELETE_RECORD … SUCCESS` and a later `SYNC_RECORD → account_failover_replica SUCCESS`; case_16 `account_994` only in read_replica).
* Activity "claims" contradicted by evidence: `DELETE SUCCESS` but record still there (case_16 search_1007 SUCCESS yet present in snapshot),
  `DELETE FAILED` but record gone (case_25 account_3136 FAILED yet 404 → COMPLETE), `BLOCK_RESTORE SUCCESS` vs `restore_allowed`.
  The sample answers always follow the **evidence**.

## 4. Why the sample answers look the way they do (inferred, to be checked in Phase 2)

* **COMPLETE** = list one verification per relevant resource in its *correct final state*: account `VERIFY_RECORD_ABSENT`,
  security audit record `VERIFY_RECORD_PRESENT` (rule_12 RETAIN), tombstone `VERIFY_RECORD_REDACTED` (rule_15), support message `…_REDACTED`,
  analytics `…_ANONYMIZED`, data flow `…_DISABLED`, backup `VERIFY_RESTORE_BLOCKED` (rule_14: backup retained but restore blocked).
  Only resources that exist/existed for this user; all cite the same evidence file (the case's snapshot). e.g. case_26 (6 steps), case_21 (3).
* **INCOMPLETE** = FINDINGS only for violations (cite evidence) + REMEDIATION for the same resources + VERIFICATION for *every* relevant resource
  (violating ones **and** compliant ones: account absent, audit record present, even if nothing was wrong with them) with `evidence_id=""`.
  Finding↔remediation↔verification mapping (consistent in all 22 incomplete samples):

  | violation | finding | remediation | verification |
  |---|---|---|---|
  | account/search/support attachment/export/analytics-as-orphan still present (DELETE categories) | REPORT_REMAINING_RECORD | DELETE_RECORD | VERIFY_RECORD_ABSENT |
  | support message with PII (rule_05) / tombstone with PII (rule_15) | REPORT_EXPOSED_PII | REDACT_RECORD | VERIFY_RECORD_REDACTED |
  | analytics attributable (rule_07) | REPORT_EXPOSED_PII | ANONYMIZE_RECORD | VERIFY_RECORD_ANONYMIZED |
  | data flow enabled | REPORT_ACTIVE_DATA_FLOW | DISABLE_DATA_FLOW | VERIFY_DATA_FLOW_DISABLED |
  | backup restore allowed | REPORT_RESTORE_RISK | BLOCK_RESTORE | VERIFY_RESTORE_BLOCKED |
  | retained security audit | — | — | VERIFY_RECORD_PRESENT (always, when the user has one) |
* Same resource type → different treatment depending on **what the record is**: `analytics_9914` (case_12) is a bare-hash orphan (old-email hash, failed delete)
  → REMAINING/DELETE, whereas `analytics_4440/2034/037` with email/phone → EXPOSED_PII/ANONYMIZE. Rule to confirm in Phase 2.
* Findings cite an evidence id; **in INCOMPLETE cases COMPLIANT resources get no finding and no cited evidence** (their verification is future work → `""`).
* Account steps: `VERIFY_RECORD_ABSENT` appears in **every** deletion case (32/32 samples), even when no account evidence exists.
* Legal hold (rule_13), cache (rule_04), email delivery log (rule_11), marketing profile (rule_10), telemetry (rule_08), object-storage attachment (rule_06):
  appear in main-set evidence (`legal_hold_*`, `support_platform/ticket_attachments`, marketing PNG) but **never in the 32 samples** → expected mapping is
  by analogy (RETAIN→VERIFY_RECORD_PRESENT, DELETE→…ABSENT, REDACT→…REDACTED); untested.

## 5. Ordering (verified on samples)

* Phase order FINDING → REMEDIATION → VERIFICATION; within phase the operation table order; then `system_id`, then `resource_id`.
  Reproduced exactly on 32/32 samples (zero violations).
* **Unresolved**: no sample has two steps with the same (operation, system_id), so `resource_id` order (string vs natural: `audit_record_987` vs `audit_record_9528`)
  is untested. Spec says "ascending"; I'll default to plain string order (what a Python `sorted` tuple gives) unless you object.
* `case_id` order: main file is numeric order (case_33 … case_550) while AGENTS.md says "sorted by case_id". Plain string sort would put case_100 first.

## 6. Anomalies / places where spec and data disagree

1. `reported_status`/`reported_result` are unreliable by design (most COMPLETE claims are INCOMPLETE; failed deletes vs gone records).
2. AGENTS.md says outputs "sorted by case_id" but the input is numerically sorted (see §5).
3. README example `case_000,NOT_APPLICABLE,[]` — no such case id exists.
4. Evidence manifest `captured_at` for some files is later than file content dates (bundle 09-17 contains snapshots dated 09-09/09-15).
5. Same user audited at two times (user_18, case_33/52; user_07 sample/main) → the state must be time-sliced.
6. Some main cases may have *no* evidence captured ≤ audit_at (to count in Phase 2; then the conservative result must be defined).
7. 721 FAILED / 376 PARTIAL activity results are mostly decoys or contradicted by evidence.
8. Evidence SQL files are untrusted text → execute in an in-memory sqlite DB with an authorizer allowing only CREATE TABLE/INSERT/CREATE INDEX/PRAGMA(foreign_keys), never ATTACH/LOAD.

## 7. Assumptions recorded

* A1: exact-identifier matching (case-insensitive email, exact user_id, sha256 of lowercased email, normalized phone); never name/fuzzy.
* A2: NOT_APPLICABLE ⇔ request text explicitly does not authorize deletion (keyword rules + LLM only if ambiguous).
* A3: evidence captured after `audit_at` is never cited and never used for *state*.
* A4: resource_id ordering = plain string order (untested).
* A5: `case_id` output order = numeric order of the input file (matches the input; graders usually key by id).

## 8. Open questions for the user (also in the checkpoint message)

1. Provider/model for the VLM step (11 PNGs) — recommend Anthropic `claude-haiku-5-5`; need `ANTHROPIC_API_KEY` in env at run time and per-token prices for the usage report.
2. `case_id` output order: numeric (recommended) or plain string?
3. May later evidence (captured after `audit_at`) be used **only to link resource ids to identities** (never state/citations)? Needed for a few samples (case_26).
4. OK to default resource_id ties to plain string order?
5. Agent-trace zip: you must export it yourself at the end (I can prepare the checklist).
