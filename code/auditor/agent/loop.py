"""The bounded tool-calling loop that reads ONE screenshot (`AgentImageReader`, opt-in: `--images agent`).

Shape of one reading: the model gets the screenshot and the task, may call `zoom_region` a few times to read small text, and ends with exactly one `submit_reading`. Code, not the model,
runs the loop and enforces the limits:

  max_iterations   model turns per screenshot            max_tool_calls   tool calls per screenshot (a submit counts)
  max_zooms        zoom_region calls per screenshot      wall_seconds     wall-clock budget per screenshot (each request also has llm.timeout_seconds)
  repeated call    the same tool with the same input a second time is answered from memory once; a third time ends the loop
  invalid submit   one re-ask (a tool error that says what was wrong), then the loop ends

Transport problems (429, timeouts, 5xx) are retried with back-off inside `LLM._send`; refusals, an invalid or missing submission, a cap, a repeated call or an unexpected tool all end in a
`AgentFailure`, which is a `SchemaError`: the screenshot is then "not read", exactly like a missing key, and the deterministic result stands. A valid submission is cached (key: image bytes,
both prompt files, tool definitions, model, pipeline and rules versions) BEFORE the gate, so a change in a gate rule never needs a new model call. No API key, no network and no real
model are involved in any test of this module: they use a fake client.
"""
from __future__ import annotations

import json
import logging
import os
import threading
import time
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable

from ..llm import LLM, LLMUnavailable, SchemaError, validate_schema
from . import gate as gate_mod
from . import prompts, tools

log = logging.getLogger(__name__)
SOURCE = "image:agent"


class AgentFailure(SchemaError):
    """The loop ended without a usable submission (reason in the message)."""


@dataclass(frozen=True)
class AgentLimits:
    max_iterations: int = 5
    max_tool_calls: int = 6
    max_zooms: int = 4
    wall_seconds: float = 120.0
    zoom_max_px: int = 1200
    max_resubmits: int = 1


class AgentImageReader:
    """An extractor with the call signature of `LLM.read_image_json` wrapped as `reader(path, system, instruction, schema, prompt_version) -> reading dict`
    (the arguments are ignored: the reader uses its own versioned prompt files). Observations built from its readings are tagged `image:agent`."""

    obs_source = SOURCE

    def __init__(self, llm: LLM, file_ids_by_path: dict[str, str], limits: AgentLimits | None = None, clock: Callable[[], float] = time.monotonic,
                 prompt_dir: Path | None = None):
        self.llm, self.limits, self._clock = llm, limits or AgentLimits(), clock
        self.file_ids = {os.path.normpath(str(p)): fid for p, fid in file_ids_by_path.items()}
        self.system = prompts.load("image_reader.system.md", prompt_dir)
        self.task = prompts.load("image_reader.task.md", prompt_dir)
        self.notes: list[str] = []                           # gate rejections and failures, for the run notes
        self.stats: Counter = Counter()
        self._lock = threading.Lock()

    # ------------------------------------------------------------------ public
    def __call__(self, path: Path, system: str = "", instruction: str = "", schema: dict | None = None, prompt_version: str = "") -> dict:
        path = Path(path)
        file_id = self.file_ids.get(os.path.normpath(str(path)))
        if file_id is None:                                  # gate rule G2: only screenshots of the manifest are read
            raise LLMUnavailable(f"{path.name} is not an evidence file of the manifest")
        data = path.read_bytes()
        import hashlib
        fhash = hashlib.sha256(data).hexdigest()
        raw: dict | None = None
        last: Exception | None = None
        for model in (self.llm.model, self.llm.fallback_model):
            key = self._key(fhash, model)
            with self.llm._key_lock(key):                    # the same screenshot twice at once costs one reading
                cached = self.llm._cache_get(key)
                if cached is not None:
                    self.llm.usage.add({"ts": _now(), "provider": "anthropic", "model": model, "kind": "agent_image", "file": path.name, "file_sha256": fhash, "cache_hit": True,
                                        "input_tokens": 0, "output_tokens": 0, "calls": 0})
                    raw = cached
                    break
                try:
                    raw, trace = self._run(model, path, data)
                except AgentFailure as e:
                    last = e
                    self.llm.usage.add({**self._row(path, fhash, model, e.trace if hasattr(e, "trace") else {}), "error": f"agent loop ended without a reading: {e}"})
                    self._count("failed")
                    continue
                self.llm._cache_put(key, raw)
                self.llm.usage.add(self._row(path, fhash, model, trace))
                break
        if raw is None:
            self._note(f"{file_id}: not read ({last})")
            raise AgentFailure(f"no model produced a valid reading for {path.name}: {last}")
        try:
            result = gate_mod.gate_reading(raw, file_id, set(self.file_ids.values()))
        except (SchemaError, gate_mod.GateError) as e:       # a cached or fresh submission that the gate refuses is "not read"
            self._note(f"{file_id}: not read (gate: {e})")
            raise LLMUnavailable(f"gate refused the reading of {path.name}: {e}") from e
        for r in result.rejections:
            self._note(f"{file_id}: {r}")
        self._count("read")
        return result.reading

    # ------------------------------------------------------------------ the loop
    def _run(self, model: str, path: Path, data: bytes) -> tuple[dict, dict]:
        lim = self.limits
        trace: dict[str, Any] = {"api_calls": 0, "tool_calls": 0, "zooms": 0, "input_tokens": 0, "output_tokens": 0, "cache_read": 0, "latency_s": 0.0}
        try:
            width, height = tools.image_size(path)
        except tools.ToolError as e:
            raise _fail(f"cannot read the image header: {e}", trace)
        specs = tools.tool_specs()
        zoom_on = any(t["name"] == "zoom_region" for t in specs)
        task = self.task.fill(width=width, height=height, max_zooms=lim.max_zooms if zoom_on else 0)
        media = "image/png" if path.suffix.lower() == ".png" else "image/jpeg"
        messages: list[dict] = [{"role": "user", "content": [tools.image_block(data, media), {"type": "text", "text": task}]}]
        started = self._clock()
        seen: dict[str, list] = {}
        repeats = invalid = 0
        for _ in range(lim.max_iterations):
            if self._clock() - started > lim.wall_seconds:
                raise _fail(f"wall-clock budget of {lim.wall_seconds:g} s used up", trace)
            resp, u = self.llm._send(model=model, max_tokens=4000, system=self.system.text, tools=specs, output_config={"effort": "low"}, messages=messages)
            trace["api_calls"] += 1
            for k in ("input_tokens", "output_tokens", "cache_read"):
                trace[k] += int(u.get(k, 0) or 0)
            trace["latency_s"] = round(trace["latency_s"] + float(u.get("latency_s", 0) or 0), 3)
            if getattr(resp, "stop_reason", "") == "refusal":
                raise _fail("the model refused", trace)
            blocks = list(getattr(resp, "content", []) or [])
            uses = [b for b in blocks if getattr(b, "type", "") == "tool_use"]
            messages.append({"role": "assistant", "content": blocks})
            if not uses:
                if getattr(resp, "stop_reason", "") == "max_tokens":
                    raise _fail("the answer was cut off (max_tokens)", trace)
                messages.append({"role": "user", "content": [{"type": "text", "text": "Call submit_reading now with what you can read; list anything unreadable in `unknown`."}]})
                continue
            trace["tool_calls"] += len(uses)
            if trace["tool_calls"] > lim.max_tool_calls:
                raise _fail(f"tool-call cap of {lim.max_tool_calls} exceeded", trace)
            results: list[dict] = []
            for tu in uses:
                name, args = getattr(tu, "name", ""), getattr(tu, "input", None)
                if not isinstance(args, dict):
                    results.append(_err(tu, "the tool input must be a JSON object"))
                    continue
                if name == "submit_reading":
                    try:
                        validate_schema(args, tools.SUBMIT_SCHEMA)
                    except SchemaError as e:
                        invalid += 1
                        if invalid > lim.max_resubmits:
                            raise _fail(f"invalid submission twice ({str(e)[:120]})", trace)
                        results.append(_err(tu, f"submission rejected: {str(e)[:200]}. Call submit_reading again with a structure that fits the schema."))
                        continue
                    return args, trace
                sig = name + json.dumps(args, sort_keys=True, default=str)
                if sig in seen:
                    repeats += 1
                    if repeats >= 2:
                        raise _fail("the same tool call was repeated", trace)
                    results.append({"type": "tool_result", "tool_use_id": tu.id, "content": seen[sig]})
                    continue
                if name == "zoom_region" and zoom_on:
                    results.append(self._zoom(tu, args, path, trace, seen, sig))
                else:
                    results.append(_err(tu, f"unknown tool {name[:40]!r}; the tools are zoom_region and submit_reading"))
            messages.append({"role": "user", "content": results})
        raise _fail(f"iteration cap of {lim.max_iterations} reached without a submission", trace)

    def _zoom(self, tu: Any, args: dict, path: Path, trace: dict, seen: dict, sig: str) -> dict:
        try:
            validate_schema(args, tools.ZOOM_SCHEMA)
            if trace["zooms"] >= self.limits.max_zooms:
                raise tools.ToolError(f"the zoom limit of {self.limits.max_zooms} is used up; call submit_reading")
            trace["zooms"] += 1
            png, media = tools.zoom_region(path, args, self.limits.zoom_max_px)
        except (SchemaError, tools.ToolError) as e:
            return _err(tu, str(e)[:200])
        content = [tools.image_block(png, media)]
        seen[sig] = content
        return {"type": "tool_result", "tool_use_id": tu.id, "content": content}

    # ------------------------------------------------------------------ bookkeeping
    def _key(self, fhash: str, model: str) -> str:
        spec = {"submit": tools.SUBMIT_SCHEMA, "tools": tools.tools_digest(), "limits": [self.limits.max_iterations, self.limits.max_tool_calls, self.limits.max_zooms, self.limits.zoom_max_px]}
        return self.llm.cache_key(fhash, f"{self.system.version}/{self.task.version}", model, spec, self.system.text, self.task.text)

    def _row(self, path: Path, fhash: str, model: str, trace: dict) -> dict:
        u = {"input_tokens": trace.get("input_tokens", 0), "output_tokens": trace.get("output_tokens", 0), "cache_read": trace.get("cache_read", 0), "latency_s": trace.get("latency_s", 0.0)}
        row = self.llm._usage_row("agent_image", path, fhash, model, u, max(trace.get("api_calls", 1) - 1, 0))
        row.update({"calls": trace.get("api_calls", 0), "tool_calls": trace.get("tool_calls", 0), "zooms": trace.get("zooms", 0)})
        return row

    def _note(self, text: str) -> None:
        with self._lock:
            self.notes.append(text)

    def _count(self, what: str) -> None:
        with self._lock:
            self.stats[what] += 1


def _fail(reason: str, trace: dict) -> AgentFailure:
    e = AgentFailure(reason)
    e.trace = dict(trace)                                    # tokens already spent are still logged
    return e


def _err(tu: Any, text: str) -> dict:
    return {"type": "tool_result", "tool_use_id": getattr(tu, "id", ""), "content": text, "is_error": True}


def _now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
