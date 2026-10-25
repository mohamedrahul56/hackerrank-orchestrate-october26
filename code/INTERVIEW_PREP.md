# Interview preparation - Ghost Data Still Exists deletion-audit agent

Every number below is from this repository's own reports (`code/evaluation/evaluation_report.md`, `phase3_results.md`, `phase4_results.md`, `CHANGELOG.md`) or from `output.csv`;
where something was not measured, this document says so. Test count at the time of writing: **379 tests, all passing**.

## 1. The two-minute architecture explanation

The dataset models a platform where a deletion workflow can say "Complete" while personal data still sits in replicas, logs, support attachments, exports, search indexes, analytics
records, archives or backups. For each of 518 requests I do five things, and **only the last of them ever involves a model**:

1. **Decide whether deletion was requested** (`classify.py`): a clause-level, eight-language classifier. Refusals, access, correction, export and marketing opt-outs are `NOT_APPLICABLE`; an explicit
   un-negated deletion wins in a mixed request. It reproduces my audit of all 112 distinct request sentences with 0 mismatches.
2. **Resolve identity** (`identity.py`): the request e-mail may be historical, so I rebuild every e-mail, hash and phone the user ever had from the snapshot plus the audit log, and pick the owner at request time.
3. **Turn every evidence file into the same observation record** (`evidence/*`): SQL snapshots (executed in a sandboxed in-memory sqlite), JSON bundles/ledgers/service exports, seven log layouts, text handovers,
   PDFs, and PNG screenshots. Each observation is "this resource, in this category, in this state, carrying these identifiers, seen in this file at this time".
4. **Find the state at `audit_at`** (`link.py`, `discover.py`, `state.py`, `policy.py`): link resources by exact identifiers; use only files captured between `requested_at` and `audit_at`; let the violating copy win inside
   a snapshot (replication drift); evaluate each resource against `deletion_rules.csv` through a table (DELETE / REDACT / ANONYMIZE / RETAIN / retain-and-block-restore). Deleted accounts and fully anonymized analytics
   records carry no identifier anywhere, so they are found through the deletion worker's activity timeline, by elimination plus two timing signals.
5. **Write the steps** (`audit.py`): findings with evidence ids, a remediation plan (disable flows and block restores first, then delete / redact / anonymize), verification steps, then validate every row against the contract.

The only model use is turning 11 screenshots into schema-validated JSON (no model reads request text: an ambiguous wording is NOT_APPLICABLE). **The submitted `output.csv` was produced with no model call at all**, because no API key was
available; the vision path is tested only with a fake client. That is the biggest gap in what I can claim.

## 2. Design decisions and rejected alternatives

| decision | rejected | reason (with evidence) |
|---|---|---|
| Deterministic code decides, model only describes | a model judging each case from raw evidence | reproducible byte for byte (two runs identical), cheap (0 tokens in the final run), injection-resistant (no model reads request or evidence text) |
| Table-driven policy from `deletion_rules.csv` | `if category == ...` branches | a rule row changes behaviour without code; tested by changing rule_04 in `test_extra_cases` |
| Later evidence may only **link** a resource to a user | using any file for state | evidence after `audit_at` cannot change the answer at `audit_at`; every use is logged (224 uses, 153 cases); a test proves no status, finding or remediation depends on it |
| Omit a step when the evidence cannot separate candidates | guess among 2 | a guess with p=1/2 is break-even for F1 (+0.02 pts for 72 rows), p=1/3 loses (-0.12 pts for 24 rows) |
| Hold-out measurement of the discovery | trusting the sample score | 32 labels are too few; the hold-out found the stolen-account defect and the harm of cohort narrowing |
| Cohort-snapshot narrowing OFF | keeping it | hold-out precision 85.1% (283 wrong + 49 stolen of 2250) vs 100% |
| VLM for screenshots | OCR | OCR read 64/64 tokens but needs a 391 MB dependency set and a parser per console layout |
| `user_N -> account_0NN` convention | using it for id-less accounts | wrong for a labeled sample; rejected, never merged |
| Provisional INCOMPLETE rows for screenshot-only requests | NOT_APPLICABLE placeholder | the classifier calls them deletion requests |
| One flag for the two timing heuristics | baking them in | they are dataset-specific (section 5) |

## 3. Score table: baseline to final (labeled samples; all numbers from `CHANGELOG.md` and the reports)

| stage | status | step precision | step recall | step F1 | what moved it |
|---|---|---|---|---|---|
| B0: everything INCOMPLETE, no steps | 22/32 | - | - | 0% | floor |
| B1: copy `reported_status`, no steps | 17/32 | - | - | 0% | floor (the workflow claim is wrong about half the time) |
| first end-to-end pipeline (dev 23 cases) | 23/23 | 96.8% | 88.9% | 92.7% | classifier, evidence parsers, linker, policy, steps |
| + literal `null` handling, claimant fix, later-only audit records (dev) | 23/23 | 99.2% | 91.9% | 95.4% | log parser bug; ambiguity must not leak between cases |
| + no analytics for a case that has its own (dev) | 23/23 | 100% | 92.6% | 96.2% | removed wrong extra steps |
| + match delete-event result to `reported_status` (dev) | 23/23 | 100% | 95.6% | 97.7% | resolves ambiguous account groups (189/189 explicit cases agree) |
| end of Phase 2, all 32 | 32/32 | 100% | 95.9% | 97.9% | 9 cases not tuned against in Phase 2-3: 98.2% |
| + late-linked own account, full-set fallback (Phase 3) | 32/32 | 100% | 97.4% | - | a labeled sample exposed a stolen account |
| + seconds-phase for anonymized analytics (Phase 3) | 32/32 | 100% | 98.4% | 99.2% | all 32; dev 98.9%; the 9 not-tuned cases 100% |
| Phase 4 (provisional rows, hardening) | 32/32 | 100% | 98.4% | 99.2% | main-set only: NOT_APPLICABLE 19 -> 10 |

**Reverted or rejected:** the first dev/hold-out split (only 4 cases); the zero-padded account convention; cohort-snapshot narrowing (kept as an ablation switch, off); the "NOT_APPLICABLE for screenshot-only requests"
placeholder. **Ablations:** no timeline discovery 92.5% F1 (status 29/32); no later-evidence linking 96.3%; no second-of-minute heuristics 97.9%.

## 4. Known weaknesses (say them before they are asked)

* Vision path never run for real: accuracy, variance and cost unmeasured. 9 provisional rows (`case_34 35 39 50 52 53 55 61 63`) are assumed-violating guesses.
* Only 32 labels, all read in Phase 0: nothing is an independent test; the 9 "not tuned against in Phase 2-3" cases are weaker than unseen data.
* 27 deletion rows lack an account step and 28 anonymized-analytics rows are ambiguous: omitted on purpose.
* Timing heuristics are dataset-specific (section 5, Q3).
* Categories with no labeled sample (cache, delivery log, telemetry, marketing, secondary identifier, attachments, legal-hold exposure) rely on synthetic cases I wrote from the rule text.
* Constants (time windows, chain gaps, margins) were set from the labeled data.

## 5. Twenty likely questions

**Q1. Why is the system mostly deterministic?**
Because the task is an audit: the same evidence must give the same answer, and the answer must be explainable step by step. The evidence is structured (SQL, JSON, logs) and `deletion_rules.csv` is a table,
so rules suffice. Two full runs give a byte-identical `output.csv`; a model would add variance, cost and an injection surface. The model is confined to 11 screenshots, where a rule cannot read pixels,
and even there its output is a validated JSON description that code turns into a state.

**Q2. Why omit a step when you are unsure instead of guessing?**
Adding a step with probability p of being right raises true positives by p and predicted steps by 1; micro-F1 improves only if p > F1/2 (about 49%). With two candidates p is 50%: +0.02 pts over the 72 rows that had exactly
two candidates; with three it is 33%: -0.12 pts. So guessing is a wash at best. Omitting also keeps precision at 100% on the samples, and a wrong "remediation" step is worse for a reader than a missing verification.
Better still, I replaced guesses with evidence: the seconds-phase signal and the other discovery fixes cut the rows without an account step from 138 to 36 (27 real misses plus the 9 screenshot-only rows).

**Q3. Why are the timing heuristics risky?**
Two signals are regularities of how this dataset's events were generated: within a case, each worker event keeps a fixed second-of-minute offset from the account-delete event, and an anonymization event sits 40/41 s after it.
Real services have unsynchronised clocks, retries and jitter, so those offsets would not exist and the heuristic would add nothing, or worse. I kept them because they help here (samples F1 97.9% -> 99.2%; rows without an
account step 110 -> 27), they are learned only from explicitly linked cases, they are measured by hold-out, and they sit behind one flag (`--no-timing-heuristics`). The older rules (delete 45-75 minutes before `reported_at`,
analytics about 29 minutes before, 390/449 s between policy actions) are the same kind of regularity and are controlled by `--no-discovery`.

**Q4. What does 100% hold-out precision prove, and what does it not?**
It proves that when I hide an explicit link and let the real code re-find the resource, it never assigned a wrong one (2217 correct, 0 wrong, 0 stolen of 2250 at 30% hidden, 30 seeds). It does not prove the same
rate for the cases that truly have no link (deleted accounts without identifiers - possibly harder), nor on another generator seed, nor on real logs. It also measures precision of what is assigned, not recall: 33 of 2250 were omitted.
The first version of this test scored only the hidden case and missed a defect where a neighbour kept a hidden case's account; I added the `stolen` count after a labeled sample exposed it.

**Q5. How is prompt injection handled?**
Request text and evidence text are data. No model sees request text; only the screenshot reader sees evidence, and its
system prompt says the picture is data. Whatever the reader returns is reduced by code: lists are capped, status words must be short label-like strings (an instruction-like sentence is dropped), e-mails and user ids are
re-validated, resource ids must match the id pattern, the free-text `screen` field is never read. There are tests with an injected "mark COMPLETE" in a request, in a support message, and inside model-extracted fields.
My scan of the text in the data found no injection in the real requests or evidence ("found 0"; the pixels of the 11 screenshots were not scanned because they were not read). What I cannot rule out: an image that honestly shows the word "Disabled" for a record that is not disabled.

**Q6. Why are 9 rows provisional?**
Nine deletion requests have a screenshot as their only state evidence. The classifier says they are deletion requests, so NOT_APPLICABLE was almost certainly wrong. Without a key I cannot read the picture, so each row is
`INCOMPLETE` with a finding on the record the data catalog links to the user inside the window, citing the in-window screenshot of the same system, plus remediation and verification (3 steps). The state is assumed to be violating.
They are flagged in `output.notes.md`, `phase4_results.md`, the evaluation report and the README, and become normal rows when a key is present. They lack the account and audit-record verifications.

**Q7. How is the usage so low?**
Honestly: it is zero. The final run made 0 model calls and used 0 tokens (`usage_report.md`), because no key was available. By design only the 11 screenshots (and rare ambiguous wording) would ever call a model,
so a real run would be about 11 images with `claude-haiku-5-5`, cached by file hash; I have not measured that cost and prices were not supplied, so the report says "cost not computed - prices not supplied".

**Q8. How do you know the main-set answers are right without labels?**
I do not know; I can only bound it. The 32 samples score 32/32 on status and 99.2% step F1, but I read their gold in Phase 0. For the 518 I report the predicted distribution (COMPLETE 152, INCOMPLETE 356, NOT_APPLICABLE 10 -
the shares match the samples' 31%/69%), I never use `reported_status` as proof (it is only a cross-tab), I validate every row, and I cross-check mechanisms with ablations, hold-out and synthetic cases.

**Q9. How do you decide `NOT_APPLICABLE`?**
From the meaning of `request_text` at clause level, never from case ids or from the activity log's `CLASSIFY_REQUEST` rows (those may only corroborate). Access, correction, export, marketing opt-out, refusals ("do not delete") and login problems
are non-deletion. In a mixed request an explicit un-negated deletion wins. Ten main requests are non-deletion; 0 deletion requests fall back to NOT_APPLICABLE now.

**Q10. Why may later evidence link but not give state?**
The answer is the state at `audit_at`; a file captured later describes a different moment. But an identifier first seen in a later snapshot still proves who a record belongs to. So later files can tie a resource to a user
(logged: 224 uses in 153 cases; the 206 steps that exist only because of it are end-state verifications, 162 uncited in INCOMPLETE rows by spec, 44 cited with in-window files in COMPLETE rows) but never create a finding, a status or a citation.

**Q11. How do you handle historical identifiers?**
`identity.py` builds spans from the snapshot and the audit log: each old e-mail is valid until its change time, hashes are sha256 of the lower-cased e-mail, phones are normalised. If an e-mail was reassigned, the owner at
`requested_at` wins. Evidence is then matched by e-mail, user id, hash or phone - never by name or fuzzy match. Synthetic case XC-21 requests deletion from the old e-mail.

**Q12. How do you treat replicas and replication drift?**
Inside one snapshot file the violating copy wins (a primary says absent, a replica says present: present). Across files the latest file captured inside the window wins. Within a log the last event wins.

**Q13. Why do the six legal-register users have no legal-hold step in gold, and is that consistent with your rule?**
The register PDF has no resource id and its order numbers match no `legal_hold_*` record; the only legal-hold records belong to seven main-set users. A retained-record step needs a `(system, resource)` pair, so gold cannot have one.
That is consistent with applying rule_13 only to a hold record linked to the subject. All 11 hold observations are active until 2033, so the expiry and exposed-identifier branches are exercised only by synthetic cases.

**Q14. What did you revert or reject, and why?**
The first dev/hold-out split (4 cases - meaningless); the zero-padded account convention (wrong on a labeled sample); cohort-snapshot narrowing (85.1% hold-out precision; it also made `main.py` differ from the library on 18 rows, which a test now guards);
the NOT_APPLICABLE placeholder for screenshot-only requests.

**Q15. What would you do first with an API key?**
Run `show_image_readings.py` and compare the 11 PNGs with the readings by eye; set `prices.json`; clear the cache and run `main.py` on all 518 with usage logging; compare the status counts and the 9 provisional rows with the
current output; run twice with a warm cache (must be identical) and once with a cleared cache to measure model nondeterminism at row level; then write the real usage report.

**Q16. What breaks on another dataset instance?**
Anything tuned to this generator: the timing signals, the windows around `reported_at`, the chain gaps, the reported-status/result mapping. The parsers are format-driven but know this dataset's file layouts. The policy table and the
validator are generic. I would first switch the timing heuristics off and read the hold-out table.

**Q17. Why a VLM and not OCR?**
In a time-boxed test OCR read all 64 expected tokens from the 11 PNGs, but needs a 391 MB dependency set that must also install on the grader's machine, and it returns raw text that still needs a parser per console layout (11 layouts).
A schema-constrained JSON description is smaller and portable, and code validates every field. The price is that accuracy is unmeasured until a key exists.

**Q18. How did you evaluate with so few labels?**
Four tools: the scorer (status, step, field, sequence and LCS metrics, dev vs not-tuned split), baselines/sanity checks (a perfect answer scores 100%, a shuffled one loses order metrics), ablations (each switch's cost on the samples
and the main set), and hold-out re-finding of explicit links. Plus 23 synthetic cases with an independent oracle for categories no sample covers. None of this is an independent test set.

**Q19. Tell me about the stolen-account bug.**
Elimination gave an account to the only case that listed it as a candidate. A labeled sample revealed that a neighbouring case owned it: its own account was linked only by a later snapshot, so it looked unresolved. My
hold-out only scored the hidden case, so it could not see this. Fix: an account linked to the user and deleted by the worker inside the window counts as the case's own (`known_accounts`); the hold-out now counts `stolen`;
three Phase 2 assignments were revoked. Sample recall rose from 95.9% to 97.4% with that and the fallback to the full candidate set.

**Q20. How do you guarantee determinism, and what about model nondeterminism?**
Sorted iteration, plain-string tie-breaks, compact JSON, fixed zip metadata; no randomness except seeded hold-out samples. Two runs of `main.py` give identical bytes (checked in `phase4_results.md` and the evaluation report).
For the model: responses are cached on disk by (file sha256, prompt version, model, schema), so a warm rerun is identical and costs nothing; a cleared-cache rerun would measure run-to-run variance. That last step is
tested with a fake client only; it is not measured.

## 6. Operations follow-ups (added after the Phase 5 checkpoint; numbers in `evaluation/performance_report.md`, design in `../ARCHITECTURE.md`)

* **What was slow and what did you do?** Profiling found one stage that grew faster than the input: the timeline discovery (16.95 s of a 25.92 s run at 40x the requests). I added time indexes, an inverted claimant index and a heap there only; the run fell to 9.21 s (discovery 0.78 s) and the output file is byte-identical at every scale. Stages that were already linear got nothing.
* **Is it scalable?** It is a batch auditor, not a service. I scaled only the number of requests (repeated rows, not independent data). Evidence, activity and identity tables were not scaled, the whole dataset lives in memory in one process, discovery needs all cases at once, and vision-model latency was not measured.
* **What happens if the run is killed?** Finished cases are on disk with checksums; the next run with the same inputs, versions and settings audits only the missing ones and writes the same bytes (tested by SIGKILL and comparing sha256). A checkpoint made for anything else is discarded.
* **How do you know a cached model reading is still valid?** The cache key contains the image, prompt label and text digest, model, schema, pipeline version and rules digest; changing any of them is a miss (tested).
* **What if the API rate-limits you?** Backoff 1, 2, 4 s or the server's `Retry-After` (capped at 60 s), shared by all workers; after four tries the image is reported as not read and the affected requests get provisional rows. Tested with a fake client only; no real model was called.

## 7. Edge cases and the screenshot readings (added after the operations follow-ups)

* **How did you look for edge-case bugs?** I wrote 118 scenarios (`evaluation/edge_sweep.py`), each with the expected behaviour in words BEFORE running it, on small synthetic datasets: empty fields, bad timestamps, duplicate ids, e-mail spelling, old e-mails, shared `reported_at`, corrupt files and PNGs, prompt injection. The first run on the previous code found 8 real defects (time-zone dependence of zone-less times, a lost legal-hold record, a manifest path that could leave the dataset folder, a 128 KiB request that ended the whole run, an evidence string copied into a step as a resource id, and a rules file that was a traceback or silently turned cases into NOT_APPLICABLE). Each got the smallest general fix and a regression test that fails on the old code; after each fix the tests and the sample score were checked.
* **What did you decide rather than fix?** Two identity rows with the same address (the owner at request time wins); an unknown state is never reported as a violation (evidence that exists only after `audit_at` creates no finding); a request in an uncovered language is NOT_APPLICABLE. All three are in the sweep's DECISION rows.
* **Who read the 11 screenshots?** The coding agent, with its own vision, in one session: not the program and not a model call it made. The readings are a JSON file keyed by the PNG's content hash, loaded through the same schema validation and sanitising as a model answer (`auditor/readings.py`). They are unverified by anyone else; `code/out/agent_readings_check.md` lets a person check each by eye, with unknown fields marked UNKNOWN.
* **Why is the submitted `output.csv` not the one with the readings?** Two reasons. The readings file and the check sheet contain dataset e-mail addresses, so they cannot be shipped in `code.zip`, and an output that cannot be reproduced from the zip is a worse submission. And the readings run changes exactly one row, `case_45`, adding five audit-record verifications from a screenshot that lists six events for one user, which contradicts the rest of the data (457 of 457 users have one audit record). The nine provisional rows came out identical when read, so the provisional assumption held in 9 of 9.
* **Which rows are still provisional?** `case_34 35 39 50 52 53 55 61 63`, because the submitted run used neither a model nor a readings file.

## 8. The agent layer (added last; opt-in, built and tested with fakes only)

**Say this first: the agent layer was built and tested with fakes only: 0 model calls, 0 tokens, no cost. I had no API key, so I cannot tell you how well a real model reads the screenshots or what it costs.**

* **What is it?** `--images agent` (code in `auditor/agent/`, prompts in `prompts/`). For each of the 11 PNG screenshots the model runs a bounded loop with two tools: `zoom_region` (crop and enlarge a part of the image) and `submit_reading` (a structured description of the records on screen). It does not decide anything: its reading becomes observations only through a deterministic gate, and the rest of the pipeline is unchanged.
* **Why only two tools?** Each tool must be defensible. Zoom answers a real limitation (small text in a dense table); submit is how the answer leaves the model. A "search the activity log" or "look up the user" tool would let the model do work that code does exactly, and would make the audit depend on the model.
* **What did you drop, and why?** (1) A model pass over request text: all 550 requests are decided by rules (540 deletion, 10 not applicable, 0 ambiguous), so it could only add errors; with it went gate rule G12. (2) A confidence field: a model's self-reported confidence cannot be checked, so nothing could rest on it. (3) Any use of the model's output as a label for tuning: that would be an oracle, and I have 9 cases I promised not to tune against.
* **How do you know it cannot change a graded answer?** Three facts. By construction: model observations carry the source `image:agent`, and the only changed code (`state.resolve`, rule G10) is entered only if the chosen observation has that source, so the default path executes the old code. By proof, run live (`make agent`): the default run and `--images agent` without a key both give `output.csv` sha256 `4676156f…`. By replay: a scripted fake model that returns the 11 readings from section 7 through the loop and the gate gives `f86e7d6f…`, identical to the readings-file run, so the loop adds nothing of its own.
* **What does the gate do?** G1 schema; G2 the model never names an evidence id, code binds each observation to the manifest `file_id` of the image read; G3 resource-id shape; G4 closed vocabulary of states; G5 identifier validity; G6 "unknown" fields are cleared; G7 prose is never read by any decision; G8 audit window; G9 exact-identifier linking; G10 a model observation cannot lower a state that deterministic evidence supports; G11 every cited id exists and is not later than `audit_at`. G8, G9 and G11 are the existing pipeline rules, unchanged.
* **What are the limits of the loop?** 5 iterations, 6 tool calls, 4 zooms, 120 s, zoom at most 1200 px, one re-ask, retries with backoff and a shared pause after a 429, a cache keyed by image, prompts, tools, limits, model and rule versions. Any failure (refusal, invalid JSON, timeout, a cap) ends as "not read" and the deterministic result stands.
* **How was it tested?** 69 tests with a fake client: invalid JSON, refusal, timeout, 429, iteration cap, repeated tool call, fake evidence id, injection string, and more; plus a fuzz of the gate. The full suite is **379 tests, all passing**.
* **What would you do with a key?** Run the 11 images, compare with my own 11 readings (the check sheet), count disagreements by field, record tokens and cost from the usage file, and only then decide whether to adopt it. Zoom may well not help; I would measure that, not assume it.
* **Honest risk:** the tool-call message shapes follow the documented Messages API but were never sent to a real endpoint. A first real run could fail on a shape error; the failure mode is "not read", not a wrong answer.
