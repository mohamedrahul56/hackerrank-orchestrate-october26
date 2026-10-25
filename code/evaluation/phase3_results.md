# Phase 3 results

> **Frozen record.** Measured on the original Python implementation (last Python commit `277dfe4`) by `code/evaluation/phase3_report.py`, which was retired in the port to C# / .NET 10 (CHANGELOG row 53) together with the other Python-only measurement scripts. The hold-out measurement was ported (`Auditor.Tools/Holdout.cs`, reported in `evaluation_report.md`). The numbers below are not re-measured on the C# code and are kept as history; `see `evaluation_report.md` for the C# measurement`.


Every number is from this run, except the block marked *Phase 2 snapshot* (measured on commit 8d4d389 before Phase 3 changed anything).
Failures of the not-tuned sample cases are never listed (aggregate only).

## 1. Labeled samples (32): scores and ablations

| variant / split | cases | status | step P | step R | step F1 | resource-discovery F1 | exact rows | mean LCS |
|---|---|---|---|---|---|---|---|---|
| full pipeline (Phase 3) / dev | 23 | 23/23 | 100.0% | 97.8% | **98.9%** | 98.0% | 20/23 | 98.1% |
| full pipeline (Phase 3) / not-tuned | 9 | 9/9 | 100.0% | 100.0% | **100.0%** | 100.0% | 9/9 | 100.0% |
| full pipeline (Phase 3) / all | 32 | 32/32 | 100.0% | 98.4% | **99.2%** | 98.6% | 29/32 | 98.6% |
| no seconds-phase stages / dev | 23 | 23/23 | 100.0% | 95.6% | **97.7%** | 95.9% | 17/23 | 97.3% |
| no seconds-phase stages / not-tuned | 9 | 9/9 | 100.0% | 96.6% | **98.2%** | 97.0% | 7/9 | 95.6% |
| no seconds-phase stages / all | 32 | 32/32 | 100.0% | 95.9% | **97.9%** | 96.3% | 24/32 | 96.8% |
| with cohort-snapshot narrowing (Phase 2 default) / dev | 23 | 23/23 | 100.0% | 97.8% | **98.9%** | 98.0% | 20/23 | 98.1% |
| with cohort-snapshot narrowing (Phase 2 default) / not-tuned | 9 | 9/9 | 100.0% | 100.0% | **100.0%** | 100.0% | 9/9 | 100.0% |
| with cohort-snapshot narrowing (Phase 2 default) / all | 32 | 32/32 | 100.0% | 98.4% | **99.2%** | 98.6% | 29/32 | 98.6% |
| no later-evidence linking (D3 off) / dev | 23 | 23/23 | 99.2% | 92.6% | **95.8%** | 92.4% | 14/23 | 95.2% |
| no later-evidence linking (D3 off) / not-tuned | 9 | 9/9 | 100.0% | 94.8% | **97.3%** | 95.4% | 6/9 | 97.7% |
| no later-evidence linking (D3 off) / all | 32 | 32/32 | 99.4% | 93.3% | **96.3%** | 93.3% | 20/32 | 95.9% |
| no timeline discovery / dev | 23 | 20/23 | 100.0% | 84.4% | **91.6%** | 84.2% | 6/23 | 79.3% |
| no timeline discovery / not-tuned | 9 | 9/9 | 100.0% | 89.7% | **94.5%** | 90.3% | 5/9 | 87.5% |
| no timeline discovery / all | 32 | 29/32 | 100.0% | 86.0% | **92.5%** | 86.2% | 11/32 | 81.6% |

Phase 2 reference (same split): dev F1 97.7% (P 100%, R 95.6%), not-tuned F1 98.2%, all 32 status 32/32.

## 2. The questions asked before iterating

### (a) Could a missing account step change `deletion_status`?

* USER_PROFILE observations without any identifier: ABSENT 37. **None is PRESENT**: a present account always carries an identifier, so it is linked by identity and its violation is already found. The accounts the timeline discovery looks for are therefore always ABSENT, and an ABSENT account is only ever a `VERIFY_RECORD_ABSENT` step (never a finding), so it cannot move a row from COMPLETE to INCOMPLETE.
* PRESENT accounts that belong to nobody in `user_identities`/audit log (their e-mails match no request either): ['account_7204', 'account_8741', 'account_9116'] - decoys, not requests.
* Rows with a deletion request but no account step now: **35** (8 of them provisional rows (screenshot-only requests; the ninth has an account step) waiting for the PNG reading; 27 real misses). Phase 2 snapshot: 138 rows.
* Status of the 27 real misses: COMPLETE 19, INCOMPLETE 8. Status flips possible from the account: **0**; what is lost is one `VERIFY_RECORD_ABSENT` step per row (see section 4 for the F1 cost).

### (b) Predicted status counts, main vs samples

| set | COMPLETE | INCOMPLETE | NOT_APPLICABLE | rows |
|---|---|---|---|---|
| main (518) | 152 (29.3%) | 356 (68.7%) | 10 (1.9%) | 518 |
| samples (32), predicted | 10 (31.2%) | 22 (68.8%) | 0 (0.0%) | 32 |
| samples (32), gold | 10 (31.2%) | 22 (68.8%) | 0 | 32 |

Predicted status by the workflow's `reported_status` (the claim; never used as evidence):

| set | reported_status | COMPLETE | INCOMPLETE | NOT_APPLICABLE |
|---|---|---|---|---|
| main | COMPLETE | 90 | 164 | 0 |
| main | PARTIALLY_COMPLETE | 36 | 116 | 0 |
| main | NOT_STARTED | 26 | 76 | 10 |
| samples | COMPLETE | 6 | 11 | 0 |
| samples | PARTIALLY_COMPLETE | 1 | 4 | 0 |
| samples | NOT_STARTED | 3 | 7 | 0 |

Of the 10 NOT_APPLICABLE rows in the main set, 10 are requests that do not authorize deletion and 9 are conservative fallbacks for deletion requests whose only evidence is a PNG (as of Phase 4 these are provisional INCOMPLETE rows, not NOT_APPLICABLE); the samples contain no non-deletion request.

### (c) Does any FINDING depend on later-evidence linking?

Steps that exist only because evidence captured after `audit_at` tied a resource to the user (main + samples): **VERIFICATION VERIFY_RECORD_ABSENT 85, VERIFICATION VERIFY_RECORD_ANONYMIZED 12, VERIFICATION VERIFY_RECORD_PRESENT 109**; status changes caused by it: **0**.
No FINDING or REMEDIATION step and no status depends on it: it only adds end-state `VERIFY_RECORD_*` steps, none of them cited. This is now enforced twice: `_audit` refuses to report a violation on a record linked only by later evidence, and `tests/test_pipeline.py::LaterEvidenceInvariant` re-runs all 550 cases with and without later linking and asserts identical statuses, findings and remediations.

### (d) Precision of the elimination heuristic and how many main rows depend on it

Method (`evaluation/holdout.py`): cases whose account is linked explicitly (that link is the label) are hidden at random, the real discovery code runs, and each hidden case is `correct`, `wrong`, `stolen` (the hidden case's account was given to another case) or `omitted`. The Phase 2 measurement counted only the hidden case; it could not see a case that kept an account belonging to a labeled sample (found at the start of Phase 3 on a labeled sample: an account linked only by a later snapshot was handed to a neighbour). `stolen` closes that blind spot.

| setting | hidden cases | correct | wrong | stolen | omitted | precision of assignments |
|---|---|---|---|---|---|---|
| all eligible cases hidden at once (1 run) | 252 | 123 (48.8%) | 0 | 0 | 129 | **100.0%** |
| 30% hidden x 30 seeds, final pipeline | 2250 | 2217 (98.5%) | 0 | 0 | 33 | **100.0%** |
| 50% hidden x 30 seeds, final pipeline | 3780 | 3682 (97.4%) | 0 | 0 | 98 | **100.0%** |
| 30% x 30, elimination only (no seconds-phase) | 2250 | 1643 (73.0%) | 0 | 0 | 607 | **100.0%** |
| 30% x 30, with cohort-snapshot narrowing (Phase 2 default) | 2250 | 1901 (84.5%) | 283 | 49 | 17 | **85.1%** |

Phase 2 snapshot (per-case scoring only): single leave-one-out 142/142 correct (45 more had no delete event; 2 ambiguous); 30% hidden 1152/1152 correct (528 omitted); 50% hidden 1804/1804 (1016 omitted).

* Elimination is exact when the true account is among the candidates; what breaks it is a candidate set that was wrongly narrowed. Cohort-snapshot narrowing, the Phase 2 default, **fails this test** (precision in the row above) and is switched off by default; when narrowing leaves a case with nothing, the case now falls back to its full candidate set.
* Main rows by how their account was obtained: ambiguous 25, eliminated 189, explicit 239, none 11, phase 44 (of 508 deletion requests). **Rows that depend on elimination or the seconds-phase stage: 233** (189 eliminated, 44 phase); explicit links: 239.
* Anonymized analytics (main): ambiguous 28, chain 33, none 306, phase 68.

## 3. Discovery stages

### Accounts (id-less, found through the activity timeline)

* real misses left: 27 = 25 ambiguous (candidates per row: 2 19, 3 6) + 2 with no candidate (no worker delete event for any free account in the window).
* New general signals this phase (CHANGELOG rows 14-18): accounts linked to the user by a later snapshot count as the case's own when the worker deleted them in the window; a case whose preferred candidate was taken falls back to its full set; cohort narrowing off; **seconds-phase**: inside one case, the second-of-minute of every worker event sits at a fixed offset (per event kind) from the account-delete event, so the candidate whose offset fits the case's other events wins when it beats the runner-up by a clear margin (log-likelihood lead >= 2).

### Anonymized analytics (id-less)

* Pairs resolved by the 390/449 s policy-action chain teach the offset between the analytics anonymization and the account delete (41 s +-1 mod 60, 35 of 36 pairs); a candidate is accepted when its offset fits and no other scored claimant of that record fits.

| setting | hidden cases | correct | wrong | stolen | omitted | precision of assignments |
|---|---|---|---|---|---|---|
| each chain-resolved pair hidden alone (36 runs) | 36 | 36 (100.0%) | 0 | 0 | 0 | **100.0%** |
| 30% of chain pairs hidden x 30 seeds | 300 | 300 (100.0%) | 0 | 0 | 0 | **100.0%** |

## 4. Guessing among k candidates versus omitting (behaviour NOT changed)

Adding one step whose probability of being right is p raises predicted steps by 1 and true positives by p, so it improves micro-F1 only if p > F1/2 (about 49%). Assumptions, stated: step precision stays 1.0 as on the 32 samples, and each real-miss row lacks exactly one gold step (the account verification, present in all 32 labeled answers); other gold steps not reached by any record are ignored, which overstates F1 slightly.

Current run: 25 ambiguous rows, candidates per row 2 19, 3 6; predicted steps 3198.

| choice | expected F1 | change |
|---|---|---|
| omit all | 99.6% | +0.000 pts |
| guess the 19 rows with k=2 (p=1/2) | 99.6% | +0.001 pts |
| guess the 6 rows with k=3 (p=1/3) | 99.5% | -0.031 pts |
| guess every ambiguous row | 99.6% | -0.029 pts |

Phase 2 snapshot (the question as asked: 100 ambiguous rows, 100 rows by k {2: 72, 3: 24, 4: 1, 6: 3}; predicted steps 3020):

| choice | expected F1 | change |
|---|---|---|
| omit all | 97.9% | +0.000 pts |
| guess the 72 rows with k=2 (p=1/2) | 97.9% | +0.024 pts |
| guess the 24 rows with k=3 (p=1/3) | 97.8% | -0.121 pts |
| guess the 1 rows with k=4 (p=1/4) | 97.9% | -0.008 pts |
| guess the 3 rows with k=6 (p=1/6) | 97.9% | -0.031 pts |
| guess every ambiguous row | 97.8% | -0.134 pts |

Reading: a uniform guess among 2 candidates has p = 50%, essentially the break-even, so it neither helps nor hurts on average; k = 3+ hurts. The seconds-phase stage removed most of this choice by turning the guess into evidence.

## 5. Remaining failures (dev samples only)

wrong/extra steps produced: **0**

| missing gold steps | reason |
|---|---|
| 3 | resource id appears nowhere in the participant data (unreachable) |
