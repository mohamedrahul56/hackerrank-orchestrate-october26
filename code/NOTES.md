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
* Challenge/submission URL: **not trusted** (decision after Phase 0). `AGENTS.md` §4.1 contains a URL with four `w`s (`wwww.hackerrank.com`) that looks like a typo;
  the user will take the real link from their registration email. I will not present the AGENTS.md URL as authoritative.
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

## 8. Open questions for the user (ANSWERED after the Phase 0 checkpoint - see §9)

1. Provider/model for the VLM step (11 PNGs) — recommend Anthropic `claude-haiku-5-5`; need `ANTHROPIC_API_KEY` in env at run time and per-token prices for the usage report.
2. `case_id` output order: numeric (recommended) or plain string?
3. May later evidence (captured after `audit_at`) be used **only to link resource ids to identities** (never state/citations)? Needed for a few samples (case_26).
4. OK to default resource_id ties to plain string order?
5. Agent-trace zip: you must export it yourself at the end (I can prepare the checklist).

## 9. Decisions after the Phase 0 checkpoint (user answers) and Phase 1 measurements

### 9.1 Decisions
* **D1 Provider / models / prices.** Anthropic. Default `claude-haiku-5-5` for PNGs only; `claude-sonnet-5-5` fallback, both configurable by env var
  (`AUDITOR_VLM_MODEL`, `AUDITOR_VLM_FALLBACK_MODEL`); key only from `ANTHROPIC_API_KEY`. **OCR first, VLM only if OCR fails - decided by measurement.**
  No OCR binary is installed here (`tesseract` missing; `pytesseract` absent; only `pypdf`/`pdftotext` for PDFs). Phase 2 will try pip-installable OCR (e.g. `rapidocr-onnxruntime`),
  score OCR vs the 11 PNGs on the fields we need (resource ids, emails, user ids, state words), and use the VLM only for images where OCR misses a required field.
  OCR must also work on the grader's machine, so any OCR dependency has to be pinned in `requirements.txt` - measured trade-off, not assumed.
  **No prices in any report until the user pastes them;** `usage_report.md` will record the price source + date, or print "unpriced" with token counts only.
* **D2 Row order.** `dataset/output.csv` is header-only (1 line, verified) so it lists no case_ids -> **use the order of `deletion_requests.csv`** (numeric: case_33 ... case_550).
  Plain string sort would start `case_100, case_101...`; the validator enforces input order. Assumption A5.
* **D3 Later evidence** (captured after `audit_at`) **may only link a resource_id to a user**; never for state, never cited (the validator rejects citations after `audit_at`).
  Every use is logged below; Phase 2 will report scores with and without it (the pipeline will have a `--no-later-linking` switch).
* **D4 Ties** on (system_id) are broken by **plain string ascending resource_id**; unit-tested in `code/tests/test_schema.py::test_resource_id_ties_use_plain_string_order`.
* **D5 Agent trace.** The user exports the zip themselves; checklist in `code/SUBMISSION_CHECKLIST.md`.
* **D6 Policy engine is table-driven** from `deletion_rules.csv` (action per category) plus a small mapping table resource-id-prefix/collection -> category; no per-category if/else on case data.
* **D7 NOT_APPLICABLE comes from the meaning of `request_text`** (never case_id ranges; `CLASSIFY_REQUEST` activity rows may only corroborate).
* **D8 Reports, extra cases and fixtures are Markdown** (the code judge ignores `.csv/.tsv/.json/.jsonl/.sql/.log`). Extra evaluation cases will live in `code/evaluation/extra_cases/*.md`,
  each embedding its mini-dataset and expected steps as fenced blocks that a loader materialises into a temp dataset; expected output is also shown as a readable table.
* **D9 Final `usage_report.md` must come from a cold-cache full run of all 518 cases** (cache dir deleted first; the run log is the source).
* **D10 Unlinkable resources:** prefer explicit links; elimination/cohort heuristics are used **only if measured precision on the labeled samples is clearly > 50%**, otherwise the resource is omitted and documented.

### 9.2 Repo hygiene (verified)
* Remote `origin` = `github.com/mohamedrahul56/hackerrank-orchestrate-october26`, **visibility: private** (checked through the GitHub API, `private: true`).
* `dataset/` is **already tracked** in git (103 files, added by the starter commit `1eaa37b "chore: add initial csv files"`). I do not modify or add anything under `dataset/`; `code.zip` will exclude it (checked in Phase 5).
  If you want it out of the private repo's history you must do that yourself (history rewrite) - I will not.
* `.env`, `code.zip`, `__pycache__/`, venvs, `code/out/`, `code/.cache/` are in `.gitignore`; `tests/test_repo_hygiene.py` fails if a `.env*` file is tracked or an `sk-ant-` literal appears in code.
* `log.txt` is committed (it is the chat transcript deliverable). It contains the prompts verbatim, no secrets.

### 9.3 Prompt-injection scan - result: **found 0**
* `request_text`: 550 requests (518 main + 32 samples) contain only 112 distinct sentences; 65 sentences occur once = the 10 non-deletion requests (case_533-542, 20 sentences) and the hand-written sample texts.
  Zero sentences address an AI/system/agent (flagged vocabulary: ignore, disregard, override, system, prompt, assistant, AI, model, agent, instruction, json, output, mark, set status, respond, reply with, api key, token, secret, ...).
  Main-set deletion requests are compositions of a fixed sentence pool (each ends with a courteous confirmation request) - **template matching must NOT be used for classification** (D7); the classifier works on meaning.
* Evidence text: 83 text-bearing files (SQL, JSON, logs, TXT, PDF text layers) + all 5 CSVs scanned with an injection-phrase regex: 8 hits, all the benign `service=secret_agent event=lease_extended` vault-lease log lines. No injection.
* Images: I opened **all 11 PNGs** (UI mock-ups: admin console, analytics, backup, connector, export, object storage, search, security audit, support ticket, marketing, deleted-account reference): no instruction-like text.
  The 4 PDFs were read in full via `pdftotext`: none.
* Hardening stays mandatory anyway (hidden/regenerated data may differ): prompts say content is data, outputs are schema-validated, free text never reaches steps, SQL runs in a sandboxed sqlite connection.

### 9.4 Facts confirmed from the labeled samples
* **Verification steps for already-compliant resources in INCOMPLETE rows have an empty `evidence_id`: confirmed** (all 22 INCOMPLETE gold rows: every REMEDIATION and every VERIFICATION step - including `VERIFY_RECORD_ABSENT account_*` and `VERIFY_RECORD_PRESENT audit_record_*` - has `""`;
  every FINDING cites evidence). In COMPLETE rows every step cites evidence. 118 steps with `""`, 75 with an `evidence_*` id; **no sample cites an `activity_id` or `audit_id`**. Locked in by `tests/test_validate.py::test_sample_facts_about_evidence_ids`.
* Gold answers pass the validator with 0 errors and 0 warnings (all 32), including "cited id exists and was captured at/before `audit_at`".
* Pairing invariants hold on all 22 INCOMPLETE gold rows: every finding has the matching remediation on the same resource; every remediation has its verification; no remediation without a finding.

### 9.5 resource_id zero-padding / sort-order report
* 4,744 distinct resource ids across evidence, activity and sample answers. **Not zero-padded in general**: digit widths 3-5 are mixed for every prefix (e.g. `account_994`, `account_9797`, `account_12345`).
  Only ~60 ids (the hand-authored `account_001..010`, `analytics_037`, ...) have leading zeros.
* Consequently plain string order != numeric order for every prefix with more than one width (thousands of inverted pairs, e.g. `audit_record_9528 < audit_record_987`).
  No labeled sample has two steps with the same (operation, system_id), so **the tie rule is untestable on the labels**; it only matters in main-set cases where one user has >= 2 resources of the same kind.
  Decision D4 (plain string) is therefore a spec-reading choice, flagged as a risk (if the grader sorts numerically, such rows lose sequence score but not set score).

### 9.6 Other Phase 1 findings
* **Activity text templates** (37 total): besides generic noise, three templates are recreation/drift signals: `Active connector repopulated downstream resource <id>` (31 rows, integration_service -> search_service),
  `Restore job repopulated downstream resource <id>` (1 row, backup_service -> search_service) and `Replication worker reported its final checkpoint ... <account id>` (56 rows). These are claims; the state still comes from evidence.
* **Catalog rows** (12 `LINK_RESOURCE_IDENTITY` activity rows) name resource <-> email <-> user_id explicitly (data_flow, backup, export, marketing, analytics, tombstone, security audit, object storage for users 3,7,9,11,13,14,16,17,18,20,22). They are the grounded link for the image-only resources; the PNGs add *state* (e.g. data_flow selector/mode, export status).
* **Legal retention register PDF** names user_08/12/17/21/24/29 - all six are labeled samples whose gold answers contain **no legal-hold step**. Legal holds only appear as `legal_hold_*` rows (10 ids, 7 main cases linked, 0 samples). Treat the PDF as context.
* **`cache_*` and `email_delivery_*` ids exist only in `system_activity`** (284 and 368 ids), never in any evidence file -> no evidence of state or owner, so they cannot create findings unless a hidden variant adds evidence. Synthetic cases will still cover both categories (see `evaluation/coverage_report.md`).
* **Coverage** (`python3 code/evaluation/coverage.py` -> `code/evaluation/coverage_report.md`): categories with **no labeled sample**: SECONDARY_IDENTIFIER (0 resource ids in the data), PRODUCT_TELEMETRY (0), CACHE_ENTRY (activity-only), EMAIL_DELIVERY_LOG (activity-only), MARKETING_PROFILE (1 id, PNG only), LEGAL_HOLD_RECORD (10 ids / 7 main cases).
  Thin: USER_EXPORT 2 samples, ACCOUNT_TOMBSTONE 2 samples. Main-set involvement figures there are a lower bound (explicit identifier links only).
* **Split of the labeled samples** (`assign_splits`): 23 dev / 9 held-out (6 INCOMPLETE + 3 COMPLETE held-out), deterministic, stratified by status and answer size. 9 cases is a very small held-out set - its accuracy has a wide error bar; treat it as a sanity check, not a measurement.
* **Baselines** (see `evaluation/baseline_results.md`): all-INCOMPLETE status accuracy 22/32 (68.8%); copy `reported_status` 17/32 (53.1%); every step metric 0. Gold-status-with-no-steps also scores 0 on all step metrics, i.e. the score lives in the steps.

### 9.7 Later-evidence linking log (D3) - one line per use
| date | case | resource_id | linked via (file_id, captured_at) | audit_at | effect |
|---|---|---|---|---|---|
| see `evaluation/later_evidence_log.md` (generated; one row per use with the steps it added) | | | | | |
Known candidate from Phase 0: sample case_26 `account_5684` appears with its owner only in `evidence_02` (captured 2026-09-17, after audit_at 2026-09-15).

### 9.8 Open items for the next checkpoints
* Mixed requests (e.g. "stop marketing emails AND delete my account"; "delete only my marketing profile, keep the account"): working rule = explicit authorization to delete the account -> deletion audit even if other asks are present;
  deletion limited to a subset of categories or "keep the account" -> NOT_APPLICABLE. Assumption to confirm with synthetic cases in Phase 4.
* Behaviour when a case has no evidence at all captured <= `audit_at` (count in Phase 2).

## 10. Phase 2 - what was built, measured, assumed

All numbers: `python3 code/evaluation/phase2_report.py` (-> `evaluation/phase2_results.md`, `later_evidence_log.md`) and `evaluation/coverage.py`.

### 10.1 Request classifier (meaning-based; no case ids, no templates)
* `evaluation/request_sentences.md` lists all **112 distinct sentences** of the 550 request texts by the class I assign (DELETE 36, AUDIT 7, CONTEXT 16, ALLOW 4, SCOPE 16, COURTESY 13, NONDELETE 20).
  The main set is built from this fixed pool: only the 10 non-deletion requests and the hand-written samples have unique sentences.
* `auditor/classify.py` labels CLAUSES (DELETE / AUDIT / SUPPORT / NEGATED / NONDELETE / CONTEXT / NEUTRAL) with small multilingual lexicons (en, es, fr, pt, de, it, ja, ko) and decides at request level:
  an un-negated DELETE/AUDIT clause -> deletion audit, even if other asks are present (your rule); a refusal ("do not close", "keep my account", "not asking for deletion") -> NOT_APPLICABLE;
  removal limited to marketing ("remove me from marketing lists only") -> NOT_APPLICABLE; narrated deletion + a request to act on it ("I was told it was deleted ... please handle them") -> deletion audit.
  **Contradictory text and "mentions the deletion request but never states the wish" are AMBIGUOUS** -> optional LLM tie-breaker (validated enum, injection-safe) or, without one, conservatively NOT_APPLICABLE and logged.
* Results: sentence level **0 mismatches of 112**; request level **0 mismatches of 550** (540 deletion, 10 not applicable, 0 ambiguous); 8 classifier tests incl. 36 synthetic paraphrases (multilingual, mixed, partial-deletion, contradictory, injection-style).
* Borderline sentences (decisions): "Remove me from marketing lists only" -> NONDELETE (verb scoped to marketing); "correct it without removing any of my records" -> NEGATED; "Do not close the account" -> NEGATED;
  "Please keep the account as it is" / "leave my profile ... in place" -> NEGATED; "I still use it, so please don't delete or change anything" -> NEGATED; "not a request to erase it" -> NEGATED;
  "I would like it closed" / "close this account permanently" / "I'm deleting my account" -> DELETE (closure = deletion of the account); "I no longer need this account" -> CONTEXT (no verb of removal);
  "Can you tell me what personal information you hold" -> NONDELETE (access) but "tell me whether the account ... have already been removed" -> AUDIT; "You may retain backups ..." / "Security records can stay" -> ALLOW (does not cancel the deletion).
* A mixed request is judged by the clause that states the wish: "stop the promotional emails AND delete my account" -> deletion audit; "delete only the newsletter subscription, keep my account" -> not applicable.

### 10.2 Evidence extraction (code only, except PNG)
* One uniform `Obs` record (resource, category, state, identifiers, file, capture time) from: SQL (executed in an in-memory SQLite with an authorizer that allows only CREATE TABLE/INDEX, INSERT, reads; ATTACH, PRAGMA writes, DROP, DELETE are denied and tested),
  JSON (bundles, service exports, ledgers), seven log wrappers (one generic parser), the text handover, PDFs (pypdf), and PNGs (vision model, see 10.7). 2,895 observations; 0 unreadable files; unreadable files become warnings.
* The state vocabulary of every format lands in one table (`records.COLLECTION_RULES`, `records.LOOKUP_RULES`, `logobs.EVENT_RULES`); the policy engine (`policy.py`) turns (category, state) into a verdict using `deletion_rules.csv`.
* Meaning of log events decided from the data, e.g. `connector_heartbeat enabled=true scheduler_registered=true` = ACTIVE flow; `restore_candidate_evaluated eligible=true deletion_block=false` = restorable; `restore_authorization_completed decision=deny` = blocked;
  `contact_lookup_completed rows=1` found only through the hash of an old e-mail and `contact_query_completed`/`deletion_marker_check marker_found=false` = SECONDARY_IDENTIFIER (rule DELETE).
  A literal `null`/`None` in a log field is a missing value (found as a real bug: it made fully anonymized analytics look exposed).

### 10.3 State at audit_at (decision D3 applied)
* **A resource's state comes only from files captured in [requested_at, audit_at]** (assumption A6). Later files are never used for state or cited (validator-enforced). Files from before the request describe another scenario of the same person
  (users with two requests) and would resurrect resources; linking alone attaches resources of another scenario to one sample user (a stale account and a stale tombstone); the window keeps them out.
* Later evidence is used only to LINK resources to users (D3). Main set: **224 uses in 153 cases, 153 of them add at least one step** (`evaluation/later_evidence_log.md`). Samples: 32/32 status either way.
  **Score with vs without later linking**: all 32 samples strict F1 **97.9% vs 96.2%** (dev 97.7 vs 96.2; held-out 98.2 vs 96.4; precision 100% both). The extra steps are retained records (audit records) and accounts seen only in a later snapshot of the same cohort;
  they appear in INCOMPLETE rows as end-state checks with an empty evidence_id (assumption A7: first seen between requested_at and audit_at + 7 days; every sample use was 2 days after audit_at).
* Within one file the violating copy wins (replication drift: a stale replica row counts); across files the latest capture wins; inside a log the last event wins.

### 10.4 Resources with no identifier (accounts, anonymized analytics): discovery and its measured precision
* Evidence ties a deleted account to nobody (a 404 with no owner) and a fully anonymized analytics record to nobody. The activity timeline is the only link:
  the account delete lands 45-75 min before `reported_at`, the analytics anonymization ~29 min before; per-case policy actions are spaced 390 or 449 s apart (71 of 72 measured gaps).
  Cases that share a `reported_at` interleave, so assignment is by **elimination** only (accounts linked to someone else or already assigned are removed; a case is accepted only when exactly one candidate remains), narrowed softly by
  (a) the cohort snapshot that also observes the case's other records and (b) **the delete event's result = the case's reported_status (SUCCESS/FAILED/PARTIAL = COMPLETE/NOT_STARTED/PARTIALLY_COMPLETE: 189 of 189 explicit cases)** - used only to match an event to a case, never as a claim about state.
  Anonymized analytics additionally use chain timing (the candidate must sit 390/449 s before the case's own linked support/flow/backup action) and are dropped when another user's completed chain follows them (decoy users) or the case already has its own analytics record.
* **Measured on the 32 samples: accounts - 27 assigned, 27 correct, 0 wrong, 5 omitted (explicit 13, eliminated 14); anonymized analytics - 5 assigned, 5 correct, 2 omitted.** Your threshold (clearly above 50%) is met (100% on what is assigned), so they are used; ambiguous groups stay unresolved.
* **Rejected by measurement**: the zero-padded-number convention (`user_N` -> `account_0NN`). It is right for hand-authored users 1-10 but wrong for user_50 (`account_050` exists, the gold account is another id). Not used.
* Main set: **370 of 508 deletion requests get an account step; 138 do not** (100 ambiguous timelines, 27 without any candidate, 11 hand-authored/image-only). That is the biggest expected loss.

### 10.5 Evidence citation (assumption log)
* A finding / COMPLETE verification cites the file whose observation produced the state (latest in window); an INCOMPLETE row's remediation and verification steps cite nothing (confirmed from the samples).
* A resource no file observes (an account known only from the timeline) is cited as: a snapshot that covers its system AND observes the case's other records; else the latest covering snapshot in the window; else the activity id of the delete claim.
  **Activity-id citations used in the main run: 1** (assumption log in the run notes). Samples: 0.
* Known mismatch kept: hand-authored sample case where the gold cites an earlier account snapshot than the one the rule picks (the account is not found there anyway, see 10.6).

### 10.6 Remaining differences on the dev samples (6 missing steps, 0 wrong steps)
* 3 steps unreachable: an audit record id that appears in no participant file (two cases) and an account with no activity at all. Held-out misses are not examined.
* 3 steps: an account whose timeline group is ambiguous.

### 10.7 OCR vs VLM (time-boxed test, decided by measurement)
* OCR: `rapidocr-onnxruntime` 1.2.3 (+ onnxruntime 1.31.0, opencv-python 5.0.0.93, numpy 2.5.4, Shapely 2.2.0, pyclipper, Pillow, PyYAML = a 391 MB venv). Read **all 64 of 64** expected tokens (resource ids, e-mails, user ids, state words) from the 11 PNGs, 1.3-2.2 s per image on CPU.
  Rejected anyway: heavy dependency set that must also install on the grader's machine, and raw text still needs a layout-specific parser per console (11 layouts).
* VLM path built (`llm.py`, `evidence/imageobs.py`): `claude-haiku-5-5` (fallback `claude-sonnet-5-5`), structured JSON schema, code validation of every field, disk cache by (file sha256, prompt version, model, schema), retries with backoff, usage log, no prices unless `AUDITOR_PRICES_FILE` is supplied by you (source + date recorded).
  **VLM accuracy is NOT measured: no ANTHROPIC_API_KEY exists in this session.** Plumbing is tested with a fake client and with fixture readings. Without a key the 9 main cases whose evidence is a PNG fall back to a conservative NOT_APPLICABLE row (logged); with a key they produce findings (checked with fixture readings).
* Ablation deterministic-only vs full: identical on all 32 samples (none depends on a PNG); the 9 image cases are the only difference.

### 10.8 Top uncertainties and known weaknesses (honest list)
1. **LEGAL_HOLD_RECORD** (rule 13: RETAIN while a legal hold is active): applied only where a `legal_hold_*` id is linked to the user -> **7 main cases** get `VERIFY_RECORD_PRESENT`; no labeled sample has a legal-hold step and the six users of the register PDF (all samples) have none in gold. Cannot be validated.
2. Missing account step in ~27% of deletion rows (ambiguous or absent timeline) and anonymized analytics when cases share a `reported_at`. Precision is protected on purpose (omit instead of guess).
3. Records the evidence never shows (e.g. two sample audit records) cannot be produced by any method.
4. **No link, no step**: PRODUCT_TELEMETRY has no records; CACHE_ENTRY (284 ids) and EMAIL_DELIVERY_LOG (368 ids) exist only as noise rows in system_activity; they never produce steps. SECONDARY_IDENTIFIER is thin (2 samples), MARKETING_PROFILE is image-only.
5. Image evidence needs the API key; VLM accuracy unmeasured. 9 rows are conservative fallbacks until then.
6. Plain-string resource ordering: 1 of 499 rows differs from numeric order (risk is small).
7. Several constants were set from the labeled data: windows (-90..+15 min accounts, -45..-5 analytics), chain gaps (390/449 s), the 7-day later-link bound. They are documented, unit-tested and measured, but tuned on 23 dev cases; the held-out set (9 cases) is the only independent check and is small.
   **Honesty note**: the rules were first inferred in Phase 0 by reading all 32 gold answers, so "held-out" means "not looked at while tuning in Phase 2", not "never seen".
8. Classifier lexicons cover 8 languages; other languages end in AMBIGUOUS -> tie-breaker or conservative NOT_APPLICABLE. The LLM tie-breaker path is tested with fakes only.
9. `export download_available=false` is read as "absent" (assumption A8); a support message is "exposed" when any direct identifier is stored or quoted (A9); a hash-only opt-in contact is a SECONDARY_IDENTIFIER (A10).

### 10.9 Questions for the user (Phase 2 checkpoint)
1. Provide `ANTHROPIC_API_KEY` (and optionally `AUDITOR_PRICES_FILE`) for a real image read, or accept the 9 conservative fallback rows for now?
2. Keep "omit rather than guess" for ambiguous accounts (current), or output the single most plausible account when only 2 candidates remain (expected precision 50%)?
3. Legal holds: keep applying them (7 cases) or drop them until a labeled example exists?
