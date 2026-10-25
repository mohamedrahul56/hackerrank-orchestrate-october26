# Coverage of deletion_rules.csv categories

> **Frozen record.** Measured on the original Python implementation (last Python commit `277dfe4`) by `code/evaluation/coverage.py`, which was retired in the port to C# / .NET 10 (CHANGELOG row 53) together with the other Python-only measurement scripts. The categories it lists are exercised per category by `GoldenCategoriesTests` and `ExtraCasesTests` on the C# code. The numbers below are not re-measured on the C# code and are kept as history; `the rule-coverage table itself was not regenerated`.


Categories are the pipeline's reading of the evidence (record collection / event type), not resource-id prefixes. `only in system_activity` = ids that exist solely as generic operational noise rows (no evidence file shows their state or owner).

| category | rule action | resources in evidence | only in system_activity | main-set cases with a step | labeled samples with a step | status |
|---|---|---|---|---|---|---|
| USER_PROFILE | DELETE | 597 | 0 | 473 | 32 | covered |
| SECONDARY_IDENTIFIER | DELETE | 4 | 0 | 0 | 2 | thin |
| SEARCH_INDEX_ENTRY | DELETE | 314 | 0 | 270 | 12 | covered |
| CACHE_ENTRY | DELETE | 0 | 284 | 0 | 0 | **no labeled sample** -> synthetic cases |
| SUPPORT_MESSAGE | REDACT | 170 | 0 | 161 | 8 | covered |
| SUPPORT_ATTACHMENT | DELETE | 23 | 0 | 20 | 2 | thin |
| ANALYTICS_PROFILE | ANONYMIZE | 205 | 0 | 174 | 12 | covered |
| PRODUCT_TELEMETRY | ANONYMIZE | 0 | 0 | 0 | 0 | **no labeled sample** -> synthetic cases |
| USER_EXPORT | DELETE | 34 | 0 | 31 | 2 | thin |
| MARKETING_PROFILE | DELETE | 1 | 0 | 1 | 0 | **no labeled sample** -> synthetic cases |
| EMAIL_DELIVERY_LOG | REDACT | 0 | 368 | 0 | 0 | **no labeled sample** -> synthetic cases |
| SECURITY_AUDIT_LOG | RETAIN | 457 | 0 | 403 | 24 | covered |
| LEGAL_HOLD_RECORD | RETAIN | 7 | 0 | 7 | 0 | **no labeled sample** -> synthetic cases |
| BACKUP_ARCHIVE | RETAIN | 65 | 0 | 59 | 6 | covered |
| ACCOUNT_TOMBSTONE | REDACT | 10 | 0 | 8 | 2 | thin |
| DATA_FLOW | (fixed by problem statement) | 156 | 0 | 146 | 9 | covered |

Categories with no labeled sample: CACHE_ENTRY, PRODUCT_TELEMETRY, MARKETING_PROFILE, EMAIL_DELIVERY_LOG, LEGAL_HOLD_RECORD

Reading the table:
* PRODUCT_TELEMETRY has no records anywhere in the data; CACHE_ENTRY and EMAIL_DELIVERY_LOG exist only as operational noise in system_activity, so "no link, no step" applies and they never produce steps (listed as known weaknesses).
* SECONDARY_IDENTIFIER appears as legacy contact-index rows and as opt-in contact rows found only through the hash of an old e-mail.
* MARKETING_PROFILE's single resource is visible only in a screenshot (read by the vision model when a key is available).
* LEGAL_HOLD_RECORD: the retention-register PDF names six users, all labeled samples whose gold answers have no legal-hold step; legal-hold rows are linked to other (main-set) users only. This is the top policy uncertainty.
