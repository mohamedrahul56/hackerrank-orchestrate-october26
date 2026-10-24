"""Per-case results written as they are finished, so a killed run resumes instead of starting over.

File: `<out>.partial.jsonl`. Line 1 is a header holding the RUN KEY; every further line is one finished case. Each line carries the sha256 of its own
payload, so a line cut short by a kill (or damaged on disk) is detected and dropped, together with everything after it.

The run key is a hash of everything the answer depends on:
  * the input: every file under dataset/ (names and sha256), plus which requests file is answered;
  * the versions: pipeline version, rules-file digest, prompt digests, a digest of the auditor's own source (versions.py);
  * the settings that can change an answer (settings.fingerprint());
  * whether screenshots were read by a model, and a digest of the observations that reading produced.
A checkpoint whose key differs from the current run's is discarded (and says so), never mixed in.

What a resume saves, plainly: the per-case audit. Start-up work that needs every case at once (parsing the evidence, the activity-timeline discovery that
assigns id-less accounts across cases, classification) is recomputed on every start - it is deterministic, so the recomputed values are identical to the
ones that produced the checkpointed cases, which is exactly what the key guarantees. Model calls are not repeated either way: readings live in the LLM
cache (llm.py). A resumed run writes the same output.csv and output.notes.md, byte for byte, as an uninterrupted one.
"""
from __future__ import annotations

import hashlib
import json
import os
import sys
import time
from pathlib import Path

from .audit import Auditor, CaseResult

FORMAT = 1
FSYNC_EVERY = 25            # flushed to the OS after every case (survives a killed process); fsync'd to disk every this many cases (survives a power cut)


def dataset_digest(root: Path) -> str:
    h = hashlib.sha256()
    for p in sorted(Path(root).rglob("*")):
        if p.is_file():
            h.update(p.relative_to(root).as_posix().encode())
            h.update(b"\x00")
            h.update(hashlib.sha256(p.read_bytes()).digest())
    return h.hexdigest()


def image_observation_digest(auditor: Auditor) -> str:
    """Digest of what the screenshots were read as (empty when no model read them)."""
    rows = sorted(repr((o.file_id, o.resource, o.category, o.state, sorted(o.idents), o.source)) for o in auditor.base.obs if str(o.source).startswith("image:"))
    return hashlib.sha256("\n".join(rows).encode()).hexdigest() if rows else ""


def run_key(dataset_root: Path, requests_file: str, stamp: dict, images_on: bool, image_obs: str) -> str:
    material = {"format": FORMAT, "dataset": dataset_digest(dataset_root), "requests_file": requests_file, "pipeline": stamp["pipeline"], "rules": stamp["rules"],
                "prompts": stamp["prompts"], "code": stamp["code"], "settings": stamp["settings"], "images_on": images_on, "image_observations": image_obs}
    return hashlib.sha256(json.dumps(material, sort_keys=True).encode()).hexdigest()


def _line(payload: dict) -> str:
    # key order is kept as it is (no sort_keys): steps_json is written to output.csv in this order, and a resumed file must be byte-identical
    body = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    return json.dumps({"sha": hashlib.sha256(body.encode()).hexdigest(), "d": payload}, ensure_ascii=False, separators=(",", ":")) + "\n"


def _parse_line(text: str) -> dict | None:
    try:
        obj = json.loads(text)
        body = json.dumps(obj["d"], ensure_ascii=False, separators=(",", ":"))
        return obj["d"] if hashlib.sha256(body.encode()).hexdigest() == obj["sha"] else None
    except (ValueError, KeyError, TypeError):
        return None


def to_payload(r: CaseResult) -> dict:
    return {"kind": "case", "case_id": r.case_id, "status": r.status, "steps": r.steps, "notes": r.notes,
            "later_links": [list(x) for x in r.later_links], "provisional": r.provisional}


def from_payload(d: dict) -> CaseResult:
    return CaseResult(d["case_id"], d["status"], d["steps"], list(d["notes"]), [tuple(x) for x in d["later_links"]], bool(d["provisional"]))


class Checkpoint:
    """Opened with the current run key. `done` holds the finished cases of a matching checkpoint; `how` says what happened to an existing file."""

    def __init__(self, path: Path, key: str, restart: bool = False):
        self.path, self.key = Path(path), key
        self.done: dict[str, CaseResult] = {}
        self.how = "fresh"
        self._fh = None
        self._since_sync = 0
        if self.path.exists() and restart:
            self.how = "restart requested: the old checkpoint was ignored"
        elif self.path.exists():
            self._load()                                           # sets how to "resumed ..." or "discarded: ..."
        if self.how.startswith("resumed"):
            self._fh = self.path.open("a", encoding="utf-8")
        else:
            self._start_new()

    # ---------------------------------------------------------------------------------------- reading
    def _load(self) -> None:
        text = self.path.read_bytes().decode("utf-8", errors="replace")
        lines = text.split("\n")
        header = _parse_line(lines[0]) if lines and lines[0] else None
        if not header or header.get("kind") != "header" or header.get("format") != FORMAT:
            self.how = "discarded: header unreadable"
            return
        if header.get("key") != self.key:
            self.how = "discarded: the checkpoint was made for other inputs, versions or settings"
            return
        valid = [lines[0]]
        for ln in lines[1:]:
            if not ln:
                continue
            d = _parse_line(ln)
            if d is None or d.get("kind") != "case":
                break                                             # a torn or damaged line: everything from here on is unfinished work
            try:
                self.done[d["case_id"]] = from_payload(d)
            except (KeyError, TypeError):
                break
            valid.append(ln)
        clean = "\n".join(valid) + "\n"
        if clean != text:                                         # drop the torn tail so new lines start on a clean boundary
            tmp = self.path.with_name(self.path.name + ".tmp")
            tmp.write_text(clean, encoding="utf-8")
            os.replace(tmp, self.path)
            self.how = f"resumed ({len(self.done)} cases kept, damaged tail dropped)"
        else:
            self.how = f"resumed ({len(self.done)} cases kept)"

    def _start_new(self) -> None:
        self.done.clear()
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._fh = self.path.open("w", encoding="utf-8")
        self._fh.write(_line({"kind": "header", "format": FORMAT, "key": self.key}))
        self._sync()

    # ---------------------------------------------------------------------------------------- writing
    def add(self, r: CaseResult) -> None:
        self.done[r.case_id] = r
        self._fh.write(_line(to_payload(r)))
        self._fh.flush()
        self._since_sync += 1
        if self._since_sync >= FSYNC_EVERY:
            self._sync()

    def _sync(self) -> None:
        self._fh.flush()
        os.fsync(self._fh.fileno())
        self._since_sync = 0

    def close(self, remove: bool = False) -> None:
        if self._fh is not None:
            self._sync()
            self._fh.close()
            self._fh = None
        if remove:
            self.path.unlink(missing_ok=True)


def audit_all(auditor: Auditor, case_order: list[str], ckpt: Checkpoint) -> tuple[list[CaseResult], int]:
    """Results in `case_order`; cases already in the checkpoint are not audited again. Returns (results, number reused)."""
    pause_after = int(os.environ.get("AUDITOR_TEST_PAUSE_AFTER", "0") or 0)        # test hook only: lets a test kill a genuinely running process at a known point
    reused = 0
    results = []
    for cid in case_order:
        r = ckpt.done.get(cid)
        if r is not None:
            reused += 1
        else:
            r = auditor.audit(cid)
            ckpt.add(r)
            if pause_after and len(ckpt.done) >= pause_after:
                ckpt._sync()
                print(f"CHECKPOINT-REACHED {len(ckpt.done)}", file=sys.stderr, flush=True)
                time.sleep(3600)
        results.append(r)
    return results, reused
