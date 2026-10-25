# Ghost Data Still Exists - deletion-audit agent (HackerRank Orchestrate, October 2026)

For every request in `dataset/deletion_requests.csv` the agent decides whether the person really asked for deletion, reconstructs the person's current and historical identifiers, finds
every copy of their data at `audit_at`, applies `dataset/deletion_rules.csv`, and writes `output.csv` (`case_id,deletion_status,steps_json`): `COMPLETE`, `INCOMPLETE` (findings,
an ordered remediation plan and verification checks) or `NOT_APPLICABLE` (`[]`). The challenge contract is in `problem_statement.md` / `AGENTS.md` (not repeated here).

**Principle: the model describes, deterministic code decides.** All statuses and steps come from code. The only model use is optional: turning the 11 PNG screenshots into schema-validated
JSON facts. (No model reads request text: ambiguous request wording is `NOT_APPLICABLE`.) **The submitted `output.csv` was produced without any model call**
(no API key was available when it was generated; see "Known weaknesses").

## Quick start

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) and nothing else (no Python, no API key, no network after the first `dotnet restore`).

```bash
git clone <this repository> && cd <it>
make setup                      # dotnet restore code/Auditor.sln
make build                      # library, command line, tools and tests; warnings are errors
make test                       # the whole xUnit suite; needs no key and no network (writes code/out/tests.trx)
make run                        # all 518 requests -> ./output.csv  (+ output.notes.md, output.usage.jsonl)
make score                      # the 32 labeled sample requests and the score report
make explain CASE=case_48       # how one case got its answer; read-only, writes nothing
make agent                      # no-change proofs and fake-client tests of the opt-in agent layer -> code/evaluation/agent_layer_report.md
make reports                    # regenerate the evaluation, baseline, extra-case and agent reports (runs the tests first)
make bench                      # performance report at 1x ... 40x the requests -> code/evaluation/performance_report.md
make mutation                   # break 5 rules one at a time in a scratch copy; the tests must fail for each
make package                    # check every submission rule and write ./code.zip
make check                      # build code.zip and run the final submission checklist
```

`make` alone lists the targets. Without `make` (for example on Windows) run the command each target wraps; they are the first lines of the `Makefile`:
`dotnet build code/Auditor.sln -c Release`, `dotnet test code/Auditor.sln -c Release`, `dotnet run -c Release --project code/src/Auditor.Cli --` (the command line, all options below),
`dotnet run -c Release --project code/tools/Auditor.Tools -- <command>` (the tools: `score`, `package`, `final-check`, `usage-report`, `evaluation-report`, `baselines`, `extra-cases`, `agent-proofs`, `performance-report`, `mutation-check`, `show-readings`).
Design, failure modes and measured performance: [`ARCHITECTURE.md`](ARCHITECTURE.md).

## Setup and run

```bash
dotnet run -c Release --project code/src/Auditor.Cli --      # reads dataset/, writes ./output.csv (518 rows, input order), output.notes.md and output.usage.jsonl
```

Everything is .NET 10 / C# (the solution is `code/Auditor.sln`; NuGet packages: Microsoft.Data.Sqlite for the SQL snapshots, PdfPig for PDFs, SkiaSharp for PNG decoding and crops, Tomlyn for the settings file, the official Anthropic SDK behind
an `IModelClient` interface, xUnit for the tests). The first version of this project was written in Python; the C# port reproduces its `output.csv` byte for byte (sha256 `4676156f...`), see `code/evaluation/CHANGELOG.md` row 53.
Reading is limited to `dataset/`; nothing else is read. The run is deterministic: two runs give byte-identical files. Useful options:

| option | effect |
|---|---|
| `--requests sample_deletion_requests.csv --out code/out/pred_samples.csv` | run the 32 labeled samples |
| `--images auto` (default) / `on` / `off` / `file` / `agent` | read the PNG evidence with a vision model; `auto` = only when `ANTHROPIC_API_KEY` is set; `file` = take each screenshot's reading from a readings file (below), no model; `agent` = the opt-in bounded tool-calling reader (below) |
| `--explain CASE_ID` | print how that one case got its answer (identity, evidence per resource with the one used, the rule, why each step exists or is left out) and exit; read-only: writes no `output.csv`, notes or usage log; screenshots are read only with `--images on` |
| `--config FILE`, `--set section.key=value` | settings file (default `code/config/settings.toml`, every setting documented there) and one-value overrides; precedence: defaults, file, environment, `--set`, the dedicated flags below; `--show-settings` prints the effective values and the version stamp |
| `--restart`, `--keep-checkpoint` | finished cases are appended to `output.partial.jsonl` while the run goes; after a kill the next run with the same inputs, versions and settings audits only the missing cases and writes the same bytes; `--restart` ignores the file, `--keep-checkpoint` keeps it after success |
| `--no-timing-heuristics` | switch off the two second-of-minute heuristics (see "Known weaknesses") |
| `--no-discovery`, `--no-later-linking` | ablations |

Other commands (all from the repository root):

```bash
dotnet run -c Release --project code/tools/Auditor.Tools -- score --pred code/out/pred_samples.csv   # score a prediction file against the samples
dotnet test code/Auditor.sln -c Release                                                              # the test suite (needs no key; dataset-dependent tests skip without dataset/)
dotnet run -c Release --project code/tools/Auditor.Tools -- evaluation-report                        # regenerate code/evaluation/evaluation_report.md (reads code/out/tests.trx from `make test`)
dotnet run -c Release --project code/tools/Auditor.Tools -- mutation-check --write                   # break 5 rules one at a time in a scratch copy (result: code/evaluation/mutation_check.md)
dotnet run -c Release --project code/tools/Auditor.Tools -- package --dry-run                        # check and list what code.zip would contain; without --dry-run it writes code.zip
```

**Using the vision model (optional).** Set the key in your own shell only (`export ANTHROPIC_API_KEY=...`); no tool asks for it or writes it anywhere. Default model `claude-haiku-5-5`,
fallback `claude-sonnet-5-5` (`AUDITOR_VLM_MODEL`, `AUDITOR_VLM_FALLBACK_MODEL`). `Auditor.Tools show-readings` writes `code/out/image_readings.html` with each screenshot next to the
model's JSON and the observations the code derives from it. Readings are cached on disk (`AUDITOR_CACHE_DIR`, default `code/.cache/llm`). For cost reporting put per-model rates plus a source URL and a date
in `code/config/prices.json` (template: `code/config/prices.example.json`); without them the usage report says `cost not computed - prices not supplied`.
`Auditor.Tools usage-report --final` summarises the log written by the last CLI run.

**The agent layer (opt-in: `--images agent`).** Agent layer built and tested with fakes only; 0 calls, 0 tokens, no cost. A small, bounded tool-calling loop (`code/src/Auditor/Agent/`) reads one screenshot at a time: the model may call `zoom_region`
(an enlarged crop of the same screenshot, cropped with SkiaSharp) and ends with exactly one `submit_reading`.
Code runs the loop and enforces 5 model turns, 6 tool calls, 4 zooms, 120 s per screenshot and the per-request timeout (settings `[agent]`); transport errors are retried with back-off; the raw submission is cached
(key: image, both prompt files, tool definitions, model, pipeline and rules versions). The prompts are versioned files in `code/prompts/` (role, constraints, structured output, mark unknown instead of guessing, all
screenshot text is data). Every submission passes a deterministic gate before it can become an observation: schema, evidence id bound by code to the manifest (the model never names one), resource-id shape,
closed state vocabulary, valid identifiers, unknown fields emptied; then the unchanged window, identifier and citation rules apply. **Rule G10 (a model reading cannot lower the severity of a state that
deterministic evidence supports) is applied only to observations whose source is `image:agent`, so the default path is byte-identical by construction**; the live proofs are in `code/evaluation/agent_layer_report.md`:
the default run and `--images agent` without a key both reproduce the submitted `output.csv`, and a scripted replay of the 11 readings through the whole loop equals the run that takes them from a file.
Any failure (refusal, invalid or missing submission, a cap, a repeated call, timeouts, a gate refusal, no key) ends in "not read" and the deterministic result stands. The model is not used on request text.
What is NOT known: how a real model reads the screenshots, how often the gate rejects, what it costs, whether the zoom tool helps, and whether the real API accepts the request shapes: no request was ever sent.
Every evidence file is first given a route by `code/src/Auditor/Evidence/Router.cs` (a deterministic parser for every format; the screenshot reader only for PNGs and only when one is configured; the model never chooses a route). A rejected key (401/403) stops all later model requests of the run, HTTP errors the caller caused are not retried, and `llm.max_run_tokens` can cap a run's spend. All of this is tested with fakes only: 0 calls, 0 tokens, no cost.

**Readings from a file instead of a model (`--images file`).** `Readings.cs` answers the same call as the model wrapper from a JSON file keyed by the sha256 of each PNG's bytes
(`llm.readings_file`, default `code/out/agent_readings.json`). The reading goes through the same JSON-schema validation and the same field sanitising as a model's answer, so a file cannot do
anything a model answer could not; an image whose hash is not in the file, or a file made for another prompt version, is "not read" like a missing key. The usage log marks these readings
`provider: file` (0 calls, 0 tokens) and the run notes say the screenshots came from a readings file. **The 11 readings that exist were made by the coding agent (Claude Code) looking at the PNGs
with its own vision in one session; they are not a model measurement and have been checked by nobody else yet.** `code/out/agent_readings_check.md` lists, for every file, what was read and what is
marked UNKNOWN, so a person can verify each by eye. Both files hold e-mail addresses of dataset people, so they live in the git-ignored `code/out/` and are never pushed or zipped; for the same reason the
submitted `output.csv` was produced WITHOUT them (it can be reproduced from `code.zip`). `dotnet run -c Release --project code/src/Auditor.Cli -- --images file --out code/out/run_readings.csv` reproduces the experiment.

## Architecture

```
request_text --> Classify.cs (clause-level, 8 languages; refusals, access, correction, opt-out -> NOT_APPLICABLE)
requested email --> Identity.cs (current + historical e-mails, hashes, phones; owner at request time)
evidence files --> Evidence/*  one parser per format (SQL snapshots run in a sandboxed in-memory SQLite, JSON bundles/ledgers/exports, 7 log layouts, text handovers, PDF, PNG)
                   -> uniform observations (resource, category, state, identifiers, capture time)
Link.cs        --> resources tied to the user by exact identifiers (never names, never fuzzy)
Discover.cs    --> deleted accounts / anonymized analytics that carry no identifier, found through the deletion worker's activity timeline
State.cs       --> state of each resource at audit_at (only evidence captured in [requested_at, audit_at]; latest file wins; the violating copy wins inside a snapshot)
Policy.cs      --> deletion_rules.csv (table driven: DELETE / REDACT / ANONYMIZE / RETAIN / retain+block restore) -> violation or compliant
Audit.cs       --> findings, remediation (flows and restores first), verification; evidence citation; provisional rows
Validate.cs    --> every row re-checked against the contract before and after writing (schema, order, pairing, evidence ids exist and are not later than audit_at)
Llm.cs         --> the only code that talks to a model (IModelClient): cache, retries, schema validation, usage log, no hard-coded prices
Readings.cs    --> optional: the same call answered from a readings file keyed by PNG hash (same validation and sanitising; provider "file")
```

Key rules: evidence captured **after** `audit_at` may only tie a resource to a user (never state, never cited; every use is logged); workflow statuses and activity results are claims, never proof;
every text in a request or evidence file is data (no model reads request or evidence text; the optional vision model only describes screenshots); a step is omitted rather than guessed when the evidence cannot separate candidates.

## Where each claim lives in the code

Every row names a symbol in a file and the test file that checks the claim; `ClaimsMapTests` (in `code/tests/Auditor.Tests/`) fails if a file, a symbol or a test named here does not exist.
Nothing in the table was measured against a real model: the model path is tested with fakes only (0 calls, 0 tokens).

<!-- claims:start -->
| claim | code | test |
|---|---|---|
| A route is decided by code from the file name and two signatures; the model never chooses one and sees only images | `code/src/Auditor/Evidence/Router.cs::RouteFile` | `RouterTests` |
| The router changes no observation: the old suffix dispatch and the router give identical evidence for all 94 files | `code/tools/Auditor.Tools/RouterEquivalence.cs::Compare` | `RouterTests` |
| The agent loop is bounded: model turns, tool calls, zooms, wall clock, repeated calls, one re-ask | `code/src/Auditor/Agent/AgentImageReader.cs::AgentLimits` | `AgentLoopTests` |
| Two read-only tools, each property documented, with size limits | `code/src/Auditor/Agent/AgentToolTexts.cs::FieldDocs` | `AgentCraftTests` |
| Prompts are versioned files; the id vocabulary in them comes from the code | `code/src/Auditor/Agent/PromptFile.cs::PromptFile` | `AgentPromptsDocsTests` |
| A deterministic gate validates every model reading before any decision (G1 to G11) | `code/src/Auditor/Agent/Gate.cs::GateReading` | `AgentGateTests` |
| A model observation can never lower a state that deterministic evidence supports (G10); the default path never enters it | `code/src/Auditor/State.cs::AgentSource` | `AgentGateTests` |
| Any loop failure ends as "not read" and the deterministic result stands | `code/src/Auditor/Agent/AgentImageReader.cs::AgentFailure` | `AgentLoopTests` |
| The cache key holds image, prompts, tools, limits, model and versions; a change is a miss | `code/src/Auditor/Agent/AgentImageReader.cs::Key` | `AgentLoopTests` |
| Retry is decided by HTTP status; 401 and 403 stop the run; a token budget caps spend | `code/src/Auditor/Llm.cs::Send` | `LlmTests` |
| Damaged images and over-long submissions are handled without ending the run | `code/src/Auditor/Agent/AgentTools.cs::ZoomRegion` | `AgentRobustnessTests` |
| No model reads request text; an ambiguous request is NOT_APPLICABLE | `code/src/Auditor/Classify.cs::Classifier` | `ClassifyTests` |
| The policy table, not a model, decides delete, redact, anonymize or retain | `code/src/Auditor/Policy.cs::PolicyEngine` | `GoldenCategoriesTests` |
| Every output row passes a contract validator | `code/src/Auditor/Validate.cs::ValidateCase` | `ValidateTests` |
| A killed run resumes and writes the same bytes | `code/src/Auditor/Checkpoint.cs::Checkpoint` | `ResumeTests` |
| Settings have documented defaults and layered overrides | `code/src/Auditor/Settings.cs::Settings` | `VersionsSettingsTests` |
| With no key, `--images agent` reproduces the submitted `output.csv` byte for byte | `code/tools/Auditor.Tools/AgentProofs.cs::Proofs` | `AgentCliTests` |
<!-- claims:end -->

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
Edge-case sweep (`code/evaluation/edge_case_sweep.md`, a frozen record of the 118-scenario sweep run on the Python implementation: 116 OK and 2 documented decisions; its first run on the earlier code found 8 real defects, all fixed with regression tests that now live in `EdgeRegressionsTests`).
The 32 labels were all read in Phase 0 to infer the rules, so none of these numbers is an independent test.

## Known weaknesses (honest list)

* **The vision path has not been run for real.** No API key was available, so it is tested only with a fake client; its accuracy, variance and cost are unmeasured.
* **Rows still provisional in the submitted `output.csv`: `case_34`, `case_35`, `case_39`, `case_50`, `case_52`, `case_53`, `case_55`, `case_61`, `case_63`** (9 of 518). Each is a deletion request whose only
  state evidence is a screenshot. Without a key or a readings file each is answered `INCOMPLETE` with three steps: a finding on the record the data catalog ties to the user inside the window, citing the
  in-window screenshot of the same system, then remediation and verification. The state is assumed, not read. They are listed in `output.notes.md`. **With the agent's readings file all nine are computed
  normally and come out identical** (the assumption held in 9 of 9), so they stay provisional only because the submitted run did not use the readings.
* **A screenshot can disagree with the rest of the data.** With the readings file exactly one other row changes, `case_45`: the security-audit screenshot lists six events (`audit_record_001` to `006`) for
  one user, so six retained-record verifications appear instead of one. Everywhere else in the data each user has exactly one audit record (457 of 457 users; 24 of 24 sample answers), so these are
  most likely false positives, and the readings run was **not** adopted into `output.csv`. Which of the two answers is right is a decision for the reader of the screenshot.
* **Two heuristics exploit the timing of this dataset's synthetic events and would not be reliable on production logs.** When a deleted account or a fully anonymized analytics record carries no identifier,
  the agent finds it through the activity timeline. Two signals there are regularities of how this data was generated: inside one case the second-of-minute of each worker event keeps a fixed offset from the
  account-delete event, and an anonymization event sits 40/41 s after it. Real services have unsynchronised clocks, retries and jitter, so these offsets would not exist. They are learned only from explicitly
  linked cases (never from labels), measured by hold-out (100% precision of what they assign), and switchable with `--no-timing-heuristics` (samples step F1 99.2% -> 97.9%; 27 -> 110 deletion rows without an
  account step). The older timeline rules (account delete 45-75 minutes before `reported_at`, analytics about 29 minutes before, 390/449 s between policy actions) are the same kind of regularity; `--no-discovery` removes them.
* 100% hold-out precision does not prove the same rate on the cases that really have no link, nor on another dataset instance.
* 27 deletion rows lack an account verification step, and 28 rows have an anonymized-analytics record whose owner cannot be separated from another case's; both are omitted on purpose (nothing separates the candidates).
* Policy categories without a labeled sample (cache, delivery log, telemetry, marketing, secondary identifier, attachments, legal-hold exposure) are verified only by the synthetic cases; the redaction shape for an
  exposed legal-hold record is an assumption.
* Classifier lexicons cover eight languages; other languages end as `NOT_APPLICABLE`.

## Repository layout

`code/Auditor.sln` - `code/src/Auditor/` the library - `code/src/Auditor.Cli/` the command line (entry point) - `code/tests/Auditor.Tests/` the xUnit suite - `code/tools/Auditor.Tools/` scorer, baselines, hold-out,
reports, performance profiling, mutation check, packaging and final checklist (one program with sub-commands) - `code/evaluation/` the generated reports (`evaluation_report.md`, `usage_report.md`, `extra_cases.md`,
`CHANGELOG.md` loop log) - `code/prompts/` and `code/config/` - `code/NOTES.md`, `code/SPEC.md` working notes - `code/INTERVIEW_PREP.md`. `dataset/` is read-only input and is never copied into `code.zip`; keys, `code/out/`,
caches and `code/config/prices.json` are never committed or zipped. No Python remains in the repository: reports that were produced by Python-only measurement scripts (determinism matrix, coverage, phase 2-4 ablations,
edge-case sweep) are kept as frozen records and say so at their top; the properties they measured are re-checked by xUnit tests (`DeterminismTests`, `EdgeRegressionsTests`, `InvariantsTests`).
