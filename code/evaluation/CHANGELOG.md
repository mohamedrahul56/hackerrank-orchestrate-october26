# CHANGELOG (every change: motivation, score before/after, kept or reverted)

Scores are on the 32 labeled samples. "status" = case-level deletion_status accuracy; "step F1" = strict step-level F1 (all six content fields).
Phase 1 builds measurement tooling, so there is no pipeline score yet; the baselines below are the starting point.

| # | change | motivation | before | after | verdict |
|---|---|---|---|---|---|
| 0 | `score.py`, `auditor/validate.py`, `auditor/schema.py`, `auditor/outio.py`, `baseline.py` created | measure before building (workflow rule: scorer first) | n/a | B0 all-INCOMPLETE-empty: status 22/32 (68.8%), step F1 0%; B1 copy reported_status: status 17/32 (53.1%), step F1 0%; gold vs gold = 100% everywhere | kept |
| 1 | Dev/held-out split stratified by (status, set of systems) | keep a held-out slice for honest generalisation numbers | held-out = 4 cases | - | **reverted**: nearly every stratum was a singleton, leaving only 4 held-out cases (meaningless) |
| 2 | Split stratified by (status, answer-size bucket) instead | same goal with usable sizes | held-out = 4 cases | dev 23 / held-out 9 (6 INCOMPLETE + 3 COMPLETE), deterministic, order-independent | kept |
| 3 | Fix of a test, not of the pipeline: `activity_1` -> real id from the loaded data | test used a non-existent id | 1 failing test | 36/36 tests pass | kept |
| 4 | `coverage.py`: boundary-safe token regex (`user_1` must not match inside `user_12`) | avoid inflated links in the coverage estimate | counts unchanged after the fix (bug latent, not triggered) | counts identical | kept |
| 5 | First end-to-end pipeline (classifier, evidence extraction for SQL/JSON/logs/text/PDF, linker, state, policy engine, step generator) | Phase 2 core | baseline B0/B1: dev status 16/23 and 14/23, step F1 0% | dev status 23/23, step P 96.8% R 88.9% **F1 92.7%** | kept |
| 6 | Log parser: literal `null` -> missing value; elimination: ambiguous answers no longer shrink claimant sets; later-only linked audit records become end-state checks in INCOMPLETE rows (A7); latest covering snapshot for account citations | reproduce dev failures one by one (case_03 analytics, case_23 extra analytics, three missing audit records, case_01 citation) | dev F1 92.7% (P 96.8 / R 88.9) | dev F1 95.4% (P 99.2 / R 91.9) | kept |
| 7 | Do not assign an anonymized analytics record to a case that already has its own analytics; accounts linked explicitly but first seen in a later snapshot of the same scenario count; stale accounts from other scenarios do not | case_11 / case_17 extra steps | dev F1 95.4% (P 99.2) | dev F1 96.2% (**P 100.0%** / R 92.6) | kept |
| 8 | Match the account delete event to the case by `reported_status` (SUCCESS/FAILED/PARTIAL), as a soft narrowing stage | measured 189/189 explicit cases; resolves ambiguous groups | dev F1 96.2% (R 92.6) | dev F1 **97.7%** (R 95.6), held-out F1 98.2% | kept |
| 9 | Account-number convention `user_N -> account_0NN` | idea from the hand-authored cases | - | wrong for one labeled sample (user_50) | **rejected, not merged** |
| 10 | SUPPORT-only wording ("the deletion request ...") no longer authorizes deletion by itself | an injected sentence must not flip a case | request level 0 mismatches | request level 0 mismatches (53 temporary mismatches caught by the test and fixed by recognising "request for ... deletion") | kept |
| 11 | Identity: an email never changed is valid from the snapshot's `updated_at` | test of a reassigned email returned both owners | both owners | owner at request time | kept |
| 12 | Ablation: no timeline discovery | quantify its value | - | status 29/32, F1 92.5% (vs 32/32, 97.9%) | evidence that it is needed |
| 13 | Ablation: no later-evidence linking (D3 off) | your request | F1 97.9% | F1 96.2% (precision unchanged) | reported |
