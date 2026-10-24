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
