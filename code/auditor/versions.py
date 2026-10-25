"""What produced an answer: the versions that are stamped into output.notes.md, the LLM cache key and the resume key.

* PIPELINE_VERSION - written by hand. Bump it whenever a change can alter an answer or a model reading (new rule, parser fix, changed
  prompt wording). It is part of the LLM cache key, so cached screenshot readings made by another version are never reused.
* rules digest     - sha256 of dataset/deletion_rules.csv as it is on disk: the policy is data, so another file is another policy.
* prompt versions  - the label of each model prompt (imageobs.PROMPT_VERSION) plus a digest of its full text and output schema, so
  editing a prompt without touching its label still invalidates the cache.
* code digest      - sha256 of every auditor/*.py file. Used by the resume key only (any edit to the code invalidates a checkpoint) and
  shown in the run stamp; it is NOT in the LLM cache key, because a code edit does not change what a model says about an image.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

PIPELINE_VERSION = "2026.10.5"
LLM_CACHE_FORMAT = 2            # layout of the cache key itself; bump if the key's fields change

_PKG = Path(__file__).resolve().parent


def sha256_bytes(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def digest_text(*parts: str) -> str:
    h = hashlib.sha256()
    for p in parts:
        h.update(p.encode("utf-8"))
        h.update(b"\x00")
    return h.hexdigest()


def rules_digest(dataset_dir: Path) -> str:
    """Digest of deletion_rules.csv; 'missing' when the file is absent (the run then fails later with its own error)."""
    p = Path(dataset_dir) / "deletion_rules.csv"
    return sha256_bytes(p.read_bytes()) if p.exists() else "missing"


def prompts() -> dict[str, dict[str, str]]:
    """Every prompt a model sees: label and digest of its text + schema."""
    from .evidence import imageobs
    out = {"image_reading": {"version": imageobs.PROMPT_VERSION,
                             "digest": digest_text(imageobs.SYSTEM, imageobs.INSTRUCTION, json.dumps(imageobs.SCHEMA, sort_keys=True))}}
    try:                                                      # the agent layer's prompt files and tool definitions (code/prompts/, auditor/agent/tools.py)
        from .agent import prompts as agent_prompts, tools as agent_tools
        sysp, task = agent_prompts.load("image_reader.system.md"), agent_prompts.load("image_reader.task.md")
        out["agent_image_reader"] = {"version": f"{sysp.version}/{task.version}", "digest": digest_text(sysp.text, task.text, agent_tools.tools_digest())}
    except Exception as e:  # noqa: BLE001 - a missing prompt file must not break the run stamp of a run that does not use the agent
        out["agent_image_reader"] = {"version": "unavailable", "digest": f"{type(e).__name__}"}
    return out


def code_digest() -> str:
    h = hashlib.sha256()
    for p in sorted(_PKG.rglob("*.py")):
        h.update(p.relative_to(_PKG).as_posix().encode())
        h.update(b"\x00")
        h.update(p.read_bytes())
        h.update(b"\x00")
    return h.hexdigest()


def stamp(dataset_dir: Path, settings_fingerprint: str = "") -> dict:
    """The full version stamp of a run. Plain JSON-able dict with a stable key order."""
    return {"pipeline": PIPELINE_VERSION, "rules": rules_digest(dataset_dir), "prompts": prompts(),
            "code": code_digest(), "settings": settings_fingerprint}


def cache_stamp(st: dict) -> dict:
    """The part of a stamp that belongs in the LLM cache key (no code digest, no settings)."""
    return {"pipeline": st.get("pipeline", ""), "rules": st.get("rules", "")}


def stamp_lines(st: dict) -> list[str]:
    """Markdown lines for output.notes.md."""
    L = [f"* pipeline version: `{st['pipeline']}`", f"* rules file (deletion_rules.csv) sha256: `{st['rules']}`"]
    L += [f"* prompt `{name}`: version `{p['version']}`, text+schema sha256 `{p['digest']}`" for name, p in sorted(st["prompts"].items())]
    L += [f"* code sha256 (auditor/*.py): `{st['code']}`", f"* settings fingerprint: `{st['settings']}`"]
    if st.get("readings"):
        L.append(f"* screenshots read from a READINGS FILE, not by a model: {st['readings']}")
    return L
