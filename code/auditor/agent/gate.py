"""The gate: deterministic code between what the model says about a screenshot and anything the audit uses.

The model describes; this module and the rest of the pipeline decide. A model reading becomes observations only through `gate_reading`, and the observations then meet the same rules as
every other evidence. The rules, with where each one is enforced:

  G1  schema        the submission must satisfy the submit_reading schema (`tools.SUBMIT_SCHEMA`) - here.
  G2  evidence id   the model never names an evidence id; code binds every observation to the manifest's `file_id` of the screenshot it read, and refuses a screenshot that is not in
                    the manifest - here (`gate_reading` raises `GateError`) and in `loop.AgentImageReader`.
  G3  id shape      a resource id must be `<name>_<number>`; a record with another id (a path, a sentence) is dropped; a record with no id needs at least one alias - here.
  G4  vocabulary    a record whose id family has no state in the closed vocabulary (`imageobs.state_from_description`) is dropped - here.
  G5  identifiers   only syntactically valid e-mail addresses, user ids and phone numbers are kept - here.
  G6  unknown       a field the model marked unknown is emptied; a record whose id was marked unknown is dropped unless it has an alias - here.
  G7  free text     `screen` and any prose are never read by any decision (the screen title is truncated) - here and in `imageobs.sanitize`.
  G8  window        an observation gives STATE only if its file was captured inside [requested_at, audit_at]; later it can only link, never be cited - `state.usable` (unchanged).
  G9  identifier    an observation attaches to a case only through an exact identifier of the resolved user or a data-catalog link - `link.py` (unchanged).
  G10 no erasing    a model-sourced observation (source `image:agent`) can add a finding or confirm a state but cannot lower the severity of a state that deterministic evidence supports
                    in the window - `state.resolve`; it is entered only when a model-sourced observation exists, so every other run is untouched.
  G11 evidence      every step cites an id that exists and is not later than audit_at; a COMPLETE row cites evidence for every step - `validate.py` (unchanged).
(There is no G12: the model is not used on request text.)
"""
from __future__ import annotations

import copy
from dataclasses import dataclass, field

from ..evidence.imageobs import SCHEMA as READING_SCHEMA, state_from_description
from ..evidence.records import EMAIL_RE, USER_RE, norm_phone
from ..llm import SchemaError, validate_schema
from ..schema import valid_resource_id
from .tools import SUBMIT_SCHEMA

SCREEN_MAX = 80
LIST_FIELDS = ("emails", "user_ids", "phones", "status_words", "aliases")


class GateError(ValueError):
    """The submission cannot be used at all (G2); the reader falls back to 'not read'."""


@dataclass
class GateResult:
    reading: dict                                                   # conforms to imageobs.SCHEMA: what the unchanged observation builder expects
    rejections: list[str] = field(default_factory=list)            # one line per dropped record or value, for the run notes


def _clear(rec: dict, name: str) -> None:
    rec[name] = [] if name in LIST_FIELDS else (False if name in ("shows_direct_identifiers", "selected") else "")


def gate_reading(submission: dict, file_id: str, known_file_ids: set[str]) -> GateResult:
    """Apply G1-G7 to one `submit_reading` input. Raises SchemaError (G1) or GateError (G2); everything else is dropped with a rejection line."""
    validate_schema(submission, SUBMIT_SCHEMA)                                                    # G1
    if file_id not in known_file_ids:                                                             # G2
        raise GateError(f"{file_id!r} is not an evidence file of the manifest")
    rej: list[str] = []
    records = copy.deepcopy(submission["records"])
    for u in submission.get("unknown", []):                                                       # G6
        i = u["record_index"]
        if 0 <= i < len(records):
            _clear(records[i], u["field"])
        else:
            rej.append(f"G6 unknown refers to record {i}, which does not exist")
    out: list[dict] = []
    for i, rec in enumerate(records):
        rid = rec["resource_id"].strip()
        if rid and not valid_resource_id(rid):                                                    # G3
            rej.append(f"G3 record {i}: resource id {rid[:30]!r} is not <name>_<number>: dropped")
            continue
        if not rid and not [a for a in rec["aliases"] if a.strip()]:
            rej.append(f"G3 record {i}: no resource id and no alias: dropped")
            continue
        if rid and state_from_description(rid.rsplit("_", 1)[0], {**rec, "status_words": list(rec["status_words"])}) is None:     # G4
            rej.append(f"G4 record {i}: {rid} has no state in the allowed vocabulary: dropped")
            continue
        emails = [e for e in rec["emails"] if EMAIL_RE.fullmatch(e.strip())]                        # G5
        users = [u for u in rec["user_ids"] if USER_RE.fullmatch(u.strip())]
        phones = [p for p in rec["phones"] if norm_phone(p)]
        for kind, before, after in (("e-mail", rec["emails"], emails), ("user id", rec["user_ids"], users), ("phone", rec["phones"], phones)):
            if len(after) != len(before):
                rej.append(f"G5 record {i}: {len(before) - len(after)} {kind} value(s) not syntactically valid: removed")
        out.append({**rec, "resource_id": rid, "emails": emails, "user_ids": users, "phones": phones})
    reading = {"screen": str(submission.get("screen", ""))[:SCREEN_MAX], "records": out}          # G7: unknown[] and prose do not travel further
    validate_schema(reading, READING_SCHEMA)
    return GateResult(reading, rej)
