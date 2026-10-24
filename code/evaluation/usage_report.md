# Usage report

Source: the model-usage log of the final full run (`output.usage.jsonl`), 518 requests in `dataset/deletion_requests.csv`.
Everything below is computed from that log; no figure is typed in by hand.

* run that produced `output.csv` (sha256 `4676156fadff1e30...`): 518 requests, status counts COMPLETE 152, INCOMPLETE 356, NOT_APPLICABLE 10, images read by a vision model: **no**, API key present in the environment: **no** (presence only)
* provisional rows in that output (9): case_34, case_35, case_39, case_50, case_52, case_53, case_55, case_61, case_63

**No model calls were made in this run: 0 calls, 0 input tokens, 0 output tokens.** Every decision, including every status and every step, was made by deterministic code. The vision path (`claude-haiku-5-5`, fallback `claude-sonnet-5-5`, configured but not called) was **tested only with fakes** because no API key was available; the screenshots were not read, which is why the rows that depend only on a screenshot are provisional.

## Providers and models

| provider | model | files processed | model calls (incl. re-asks) | input tokens | output tokens | total tokens | cost |
|---|---|---|---|---|---|---|---|
| **all models** | | 0 | 0 | 0 | 0 | 0 | cost not computed - prices not supplied |

## By purpose

| provider | model | purpose | files | calls | input tokens | output tokens |
|---|---|---|---|---|---|---|
| - | - | - | 0 | 0 | 0 | 0 |

## Per request

* total tokens: **0**; average per request: **0.0** (518 requests; a model is called only to read a screenshot, or for a request whose wording the rule-based classifier finds ambiguous)
* model calls per request: 0.000
* estimated total cost: **cost not computed - prices not supplied**
* estimated cost per request: **cost not computed - prices not supplied**

## Prices

* cost not computed - prices not supplied.  no price file at code/config/prices.json

## Cache and failures

* live calls: 0; cache hits: 0; calls that failed schema validation twice: 0 (those images fall back to the deterministic result and are listed in the run notes)
