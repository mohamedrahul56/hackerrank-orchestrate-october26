# Ghost Data Still Exists - deletion-audit agent (HackerRank Orchestrate, October 2026)

For every request in `dataset/deletion_requests.csv` the agent decides whether the person really asked for deletion, reconstructs the person's current and historical identifiers, finds
every copy of their data at `audit_at`, applies `dataset/deletion_rules.csv`, and writes `output.csv` (`case_id,deletion_status,steps_json`): `COMPLETE`, `INCOMPLETE` (findings,
an ordered remediation plan and verification checks) or `NOT_APPLICABLE` (`[]`). The challenge contract is in `problem_statement.md` / `AGENTS.md` (not repeated here).

**Principle: the model describes, deterministic code decides.** All statuses and steps come from code. The only model use is optional: turning the 11 PNG screenshots into schema-validated
JSON facts, and breaking a tie on a request whose wording the rule-based classifier calls ambiguous. **The submitted `output.csv` was produced without any model call**
(no API key was available when it was generated; see "Known weaknesses").

## Setup and run

```bash
python3 -m pip install -r code/requirements.txt     # pypdf (PDF evidence) and anthropic (only used if a key is set)
python3 code/main.py                                # reads dataset/, writes ./output.csv (518 rows, input order), output.notes.md and output.usage.jsonl
```

Tested on Python 3.13. Reading is limited to `dataset/`; nothing else is read. The run is deterministic: two runs give byte-identical files. Useful options:

| option | effect |
|---|---|
| `--requests sample_deletion_requests.csv --out code/out/pred_samples.csv` | run the 32 labeled samples |
| `--images auto` (default) / `on` / `off` | read the PNG evidence with a vision model; `auto` = only when `ANTHROPIC_API_KEY` is set |
| `--explain CASE_ID` | print how that one case got its answer (identity, evidence per resource with the one used, the rule, why each step exists or is left out) and exit; read-only: writes no `output.csv`, notes or usage log; screenshots are read only with `--images on` |
| `--no-timing-heuristics` | switch off the two second-of-minute heuristics (see "Known weaknesses") |
| `--no-discovery`, `--no-later-linking` | ablations |

Other commands (all from the repository root):

```bash
python3 code/evaluation/main.py                     # run the samples and print the score report
python3 -m unittest discover -s code/tests -t code  # the test suite (needs no key; dataset-dependent tests skip without dataset/)
python3 code/evaluation/evaluation_report.py        # regenerate code/evaluation/evaluation_report.md
python3 code/tools/determinism_check.py             # 27 runs under different hash seeds / directories / locales; output files must be byte-identical
python3 code/tools/package.py --dry-run             # check and list what code.zip would contain; without --dry-run it writes code.zip
```

**Using the vision model (optional).** Set the key in your own shell only (`export ANTHROPIC_API_KEY=...`); no script asks for it or writes it anywhere. Default model `claude-haiku-5-5`,
fallback `claude-sonnet-5-5` (`AUDITOR_VLM_MODEL`, `AUDITOR_VLM_FALLBACK_MODEL`). `python3 code/evaluation/show_image_readings.py` writes `code/out/image_readings.html` with each screenshot next to the
model's JSON and the observations the code derives from it. Readings are cached on disk (`AUDITOR_CACHE_DIR`, default `code/.cache/llm`). For cost reporting put per-model rates plus a source URL and a date
in `code/config/prices.json` (template: `code/config/prices.example.json`); without them the usage report says `cost not computed - prices not supplied`.
`python3 code/evaluation/usage_report.py --final` summarises the log written by the last `main.py` run.

## Architecture

```
request_text --> classify.py (clause-level, 8 languages; refusals, access, correction, opt-out -> NOT_APPLICABLE)
requested email --> identity.py (current + historical e-mails, hashes, phones; owner at request time)
evidence files --> evidence/*  one parser per format (SQL snapshots run in a sandboxed in-memory sqlite, JSON bundles/ledgers/exports, 7 log layouts, text handovers, PDF, PNG)
                   -> uniform observations (resource, category, state, identifiers, capture time)
link.py        --> resources tied to the user by exact identifiers (never names, never fuzzy)
discover.py    --> deleted accounts / anonymized analytics that carry no identifier, found through the deletion worker's activity timeline
state.py       --> state of each resource at audit_at (only evidence captured in [requested_at, audit_at]; latest file wins; the violating copy wins inside a snapshot)
policy.py      --> deletion_rules.csv (table driven: DELETE / REDACT / ANONYMIZE / RETAIN / retain+block restore) -> violation or compliant
audit.py       --> findings, remediation (flows and restores first), verification; evidence citation; provisional rows
validate.py    --> every row re-checked against the contract before and after writing (schema, order, pairing, evidence ids exist and are not later than audit_at)
llm.py         --> the only module that talks to a model: cache, retries, schema validation, usage log, no hard-coded prices
```

Key rules: evidence captured **after** `audit_at` may only tie a resource to a user (never state, never cited; every use is logged); workflow statuses and activity results are claims, never proof;
every text in a request or evidence file is data (no model sees it except the optional validated tie-breaker); a step is omitted rather than guessed when the evidence cannot separate candidates.

## Design decisions and rejected alternatives

| decision | rejected alternative | why |
|---|---|---|
| Deterministic rules decide; a model only reads screenshots | one LLM call per case with the evidence in the prompt | reproducible, auditable, injection-resistant, almost free; evidence is structured so rules suffice |
| Omit a step when candidates cannot be separated | guess the single most plausible account | a guess among 2 is break-even for F1 (p about 50%), among 3 or more it loses (`phase3_results.md` section 4) |
| Hold-out measurement of the discovery (hide explicit links, re-find them, count `stolen`) | judging it by the 32 labeled samples alone | too few labels; the hold-out found a real defect the sample score hid |
| Cohort-snapshot narrowing OFF | keep it (it looked right on the samples) | hold-out precision 85.1% vs 100% (`evaluation_report.md` section 5.3) |
| Later evidence only links, never gives state | use any file for state | evidence after `audit_at` cannot change the answer at `audit_at` |
| VLM for the 11 PNGs | OCR (RapidOCR read 64/64 tokens but needs a 391 MB dependency set and a parser per console layout) | a small validated JSON schema is lighter and more portable |
| Zero-padded account-number convention (`user_N` -> `account_0NN`) | use it for id-less accounts | right for hand-made users, wrong for a labeled sample (rejected, never merged) |
| Provisional `INCOMPLETE` rows for screenshot-only requests | `NOT_APPLICABLE` placeholder | these are deletion requests per the classifier; see below |

## Results (details: `code/evaluation/evaluation_report.md`)

Labeled samples (32): status 32/32, step precision 100%, recall 98.4%, step F1 99.2%; the 9 cases not tuned against in Phase 2-3: status 9/9, F1 100%. Main set (518, no labels):
COMPLETE 152, INCOMPLETE 356, NOT_APPLICABLE 10. 23 synthetic cases (`code/evaluation/extra_cases.md`) pass. Model usage in the final run: 0 calls (`code/evaluation/usage_report.md`).
The 32 labels were all read in Phase 0 to infer the rules, so none of these numbers is an independent test.

## Known weaknesses (honest list)

* **The vision path has not been run for real.** No API key was available, so it is tested only with a fake client; its accuracy, variance and cost are unmeasured.
* **Provisional rows.** Nine deletion requests (`case_34 35 39 50 52 53 55 61 63`) have a screenshot as their only state evidence. Without a key each is answered `INCOMPLETE` with three steps:
  a finding on the record the data catalog ties to the user inside the window, citing the in-window screenshot of the same system, then remediation and verification. The state is assumed, not read.
  They are listed in `output.notes.md`; with a key they are computed normally.
* **Two heuristics exploit the timing of this dataset's synthetic events and would not be reliable on production logs.** When a deleted account or a fully anonymized analytics record carries no identifier,
  the agent finds it through the activity timeline. Two signals there are regularities of how this data was generated: inside one case the second-of-minute of each worker event keeps a fixed offset from the
  account-delete event, and an anonymization event sits 40/41 s after it. Real services have unsynchronised clocks, retries and jitter, so these offsets would not exist. They are learned only from explicitly
  linked cases (never from labels), measured by hold-out (100% precision of what they assign), and switchable with `--no-timing-heuristics` (samples step F1 99.2% -> 97.9%; 27 -> 110 deletion rows without an
  account step). The older timeline rules (account delete 45-75 minutes before `reported_at`, analytics about 29 minutes before, 390/449 s between policy actions) are the same kind of regularity; `--no-discovery` removes them.
* 100% hold-out precision does not prove the same rate on the cases that really have no link, nor on another dataset instance.
* 27 deletion rows lack an account verification step, and 28 rows have an anonymized-analytics record whose owner cannot be separated from another case's; both are omitted on purpose (nothing separates the candidates).
* Policy categories without a labeled sample (cache, delivery log, telemetry, marketing, secondary identifier, attachments, legal-hold exposure) are verified only by the synthetic cases; the redaction shape for an
  exposed legal-hold record is an assumption.
* Classifier lexicons cover eight languages; other languages fall back to the (fake-tested) model tie-breaker or to `NOT_APPLICABLE`.

## Repository layout

`code/main.py` entry point - `code/auditor/` the library - `code/evaluation/` scorer, baselines, reports (`evaluation_report.md`, `usage_report.md`, `extra_cases.md`, `CHANGELOG.md` loop log) -
`code/tests/` - `code/tools/package.py` - `code/NOTES.md`, `code/SPEC.md` working notes - `code/INTERVIEW_PREP.md`. `dataset/` is read-only input and is never copied into `code.zip`; keys, `code/out/`, caches and
`code/config/prices.json` are never committed or zipped.
