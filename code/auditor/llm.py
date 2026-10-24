"""The only module that talks to a model. Everything else gets validated JSON or an exception.

What it does for every call:
  * disk cache keyed by sha256(file bytes) + prompt version + sha256(prompt text) + model + schema + pipeline version + rules-file digest
    (see versions.py)  -> a rerun costs nothing and is deterministic, and any of those changing is a miss, never a stale hit
  * retries with exponential backoff on transport errors; ONE re-ask when the answer is not valid JSON for the schema
  * code validation of the answer against the JSON schema (jsonschema-style subset implemented here: type/required/enum/items)
  * usage log (JSONL): provider, model, input/output tokens, cache hit or live call, latency, cost if prices were supplied
Prices are NEVER hard-coded: they come from the user's price file (see prices.py: per-model rates plus source_url and date); without
a complete entry the cost is reported as "unpriced" ("cost not computed - prices not supplied").
The model's text is data: it can only influence the run through fields that code then validates.
"""
from __future__ import annotations

import base64
import hashlib
import json
import os
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable

from . import prices, versions

DEFAULT_MODEL = "claude-haiku-5-5"            # cost-efficient, vision-capable
DEFAULT_FALLBACK_MODEL = "claude-sonnet-5-5"  # used only when the first model fails schema validation twice
DEFAULT_TIMEOUT_S = 60.0                      # per request
MAX_BACKOFF_S = 60.0                          # cap for one backoff wait, also for a long Retry-After
CALL_ATTEMPTS = 4                             # tries per model call for transport errors / 429 / 5xx (waits 1, 2, 4 s unless Retry-After says more)


class LLMUnavailable(RuntimeError):
    """No credentials / SDK / network: the caller must fall back to its rule-based result."""


class SchemaError(ValueError):
    pass


def validate_schema(value: Any, schema: dict, path: str = "$") -> None:
    """Small JSON-schema subset (type, required, properties, items, enum, additionalProperties=false). Raises SchemaError."""
    t = schema.get("type")
    types = {"object": dict, "array": list, "string": str, "boolean": bool, "integer": int, "number": (int, float)}
    if t and not isinstance(value, types[t]) or (t == "integer" and isinstance(value, bool)):
        raise SchemaError(f"{path}: expected {t}, got {type(value).__name__}")
    if "enum" in schema and value not in schema["enum"]:
        raise SchemaError(f"{path}: {value!r} not in {schema['enum']}")
    if t == "object":
        for k in schema.get("required", []):
            if k not in value:
                raise SchemaError(f"{path}: missing required key {k!r}")
        props = schema.get("properties", {})
        if schema.get("additionalProperties") is False:
            extra = set(value) - set(props)
            if extra:
                raise SchemaError(f"{path}: unexpected keys {sorted(extra)}")
        for k, sub in props.items():
            if k in value:
                validate_schema(value[k], sub, f"{path}.{k}")
    if t == "array":
        for i, item in enumerate(value):
            validate_schema(item, schema["items"], f"{path}[{i}]")


@dataclass
class UsageLog:
    path: Path | None = None
    rows: list[dict] = field(default_factory=list)

    _lock: threading.Lock = field(default_factory=threading.Lock, repr=False, compare=False)

    def add(self, row: dict) -> None:
        with self._lock:                                          # image reads run in several threads: one whole line per row
            self.rows.append(row)
            if self.path is not None:
                self.path.parent.mkdir(parents=True, exist_ok=True)
                with self.path.open("a", encoding="utf-8") as fh:
                    fh.write(json.dumps(row, ensure_ascii=False, sort_keys=True) + "\n")


class LLM:
    def __init__(self, cache_dir: Path | None = None, usage_path: Path | None = None, client: Any = None,
                 model: str | None = None, fallback_model: str | None = None, sleep: Callable[[float], None] = time.sleep,
                 stamp: dict | None = None, timeout_s: float = DEFAULT_TIMEOUT_S, clock: Callable[[], float] = time.monotonic):
        self.model = model or os.environ.get("AUDITOR_VLM_MODEL", DEFAULT_MODEL)
        self.fallback_model = fallback_model or os.environ.get("AUDITOR_VLM_FALLBACK_MODEL", DEFAULT_FALLBACK_MODEL)
        self.cache_dir = cache_dir or Path(os.environ.get("AUDITOR_CACHE_DIR", Path(__file__).resolve().parents[1] / ".cache" / "llm"))
        self.usage = UsageLog(usage_path or Path(os.environ.get("AUDITOR_USAGE_LOG", Path(__file__).resolve().parents[1] / "out" / "usage_log.jsonl")))
        self.stamp = versions.cache_stamp(stamp or {"pipeline": versions.PIPELINE_VERSION})     # main.py passes the run's stamp (rules digest included)
        self._client = client
        self._sleep = sleep
        self._clock = clock
        self.timeout_s = timeout_s
        self._pause_until = 0.0                              # rate-limit backoff shared by every thread: one 429 slows all workers, not just one
        self._state = threading.Lock()                       # guards _pause_until, _key_locks and the lazily built client
        self._key_locks: dict[str, threading.Lock] = {}
        self.prices, self.price_problems = prices.load()

    # ------------------------------------------------------------------ client
    def client(self) -> Any:
        with self._state:
            return self._client_locked()

    def _client_locked(self) -> Any:
        if self._client is None:
            try:
                import anthropic
            except ImportError as e:
                raise LLMUnavailable("the 'anthropic' package is not installed (pip install -r code/requirements.txt)") from e
            if not (os.environ.get("ANTHROPIC_API_KEY") or os.environ.get("ANTHROPIC_AUTH_TOKEN")):
                raise LLMUnavailable("ANTHROPIC_API_KEY is not set")
            self._client = anthropic.Anthropic(max_retries=2)
        return self._client

    # ------------------------------------------------------------------ cache
    def cache_key(self, file_hash: str, prompt_version: str, model: str, schema: dict, system: str = "", instruction: str = "") -> str:
        """Every input that can change the model's reading: the image, the prompt label AND text, the model, the schema, the pipeline
        version and the rules-file digest (versions.py). A change in any of them is a miss."""
        blob = json.dumps({"fmt": versions.LLM_CACHE_FORMAT, "f": file_hash, "p": prompt_version, "pt": versions.digest_text(system, instruction),
                           "m": model, "s": schema, "pipeline": self.stamp.get("pipeline", ""), "rules": self.stamp.get("rules", "")}, sort_keys=True)
        return hashlib.sha256(blob.encode()).hexdigest()

    def _cache_get(self, key: str) -> dict | None:
        p = self.cache_dir / f"{key}.json"
        try:
            return json.loads(p.read_text(encoding="utf-8")) if p.exists() else None
        except (OSError, json.JSONDecodeError):
            return None                                           # a corrupt cache entry is just a miss

    def _cache_put(self, key: str, value: dict) -> None:
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        tmp = self.cache_dir / f".{key}.{threading.get_ident()}.tmp"           # write then rename: a reader never sees half an entry
        tmp.write_text(json.dumps(value, ensure_ascii=False, sort_keys=True), encoding="utf-8")
        os.replace(tmp, self.cache_dir / f"{key}.json")

    # ------------------------------------------------------------------ public
    def read_image_json(self, kind: str, image_path: Path, system: str, instruction: str, schema: dict, prompt_version: str) -> dict:
        """Describe one image as JSON matching `schema`. Raises LLMUnavailable (no key) or SchemaError (model never complied)."""
        data = image_path.read_bytes()
        fhash = hashlib.sha256(data).hexdigest()
        media = "image/png" if image_path.suffix.lower() == ".png" else "image/jpeg"
        last: Exception | None = None
        for model in (self.model, self.fallback_model):
            key = self.cache_key(fhash, prompt_version, model, schema, system, instruction)
            with self._key_lock(key):                             # the same image twice at once costs one call: the second waits and hits the cache
                cached = self._cache_get(key)
                if cached is not None:
                    self.usage.add({"ts": _now(), "provider": "anthropic", "model": model, "kind": kind, "file": image_path.name,
                                    "file_sha256": fhash, "cache_hit": True, "input_tokens": 0, "output_tokens": 0, "calls": 0})
                    return cached
                for attempt in range(2):                          # one re-ask for invalid JSON
                    try:
                        raw, usage = self._call(model, system, instruction, data, media, schema)
                        value = _parse_json(raw)
                        validate_schema(value, schema)
                        self._cache_put(key, value)
                        self.usage.add(self._usage_row(kind, image_path, fhash, model, usage, attempt))
                        return value
                    except (SchemaError, json.JSONDecodeError) as e:
                        last = e
                        instruction = instruction + "\n\nYour previous answer was not valid JSON for the schema. Answer with the JSON object only."
            self.usage.add({"ts": _now(), "provider": "anthropic", "model": model, "kind": kind, "file": image_path.name,
                            "file_sha256": fhash, "cache_hit": False, "input_tokens": 0, "output_tokens": 0, "calls": 0,
                            "error": f"schema validation failed twice: {last}"})
        raise SchemaError(f"no model produced valid JSON for {image_path.name}: {last}")

    # ------------------------------------------------------------------ internals
    def _call(self, model: str, system: str, instruction: str, data: bytes, media: str, schema: dict) -> tuple[str, dict]:
        resp, usage = self._send(
            model=model, max_tokens=4000, system=system,
            output_config={"effort": "low", "format": {"type": "json_schema", "schema": schema}},
            messages=[{"role": "user", "content": [
                {"type": "image", "source": {"type": "base64", "media_type": media, "data": base64.standard_b64encode(data).decode()}},
                {"type": "text", "text": instruction}]}])
        if getattr(resp, "stop_reason", "") == "refusal":
            raise SchemaError("model refused")
        text = "".join(getattr(b, "text", "") for b in resp.content if getattr(b, "type", "") == "text")
        return text, usage

    def _send(self, **create_kwargs: Any) -> tuple[Any, dict]:
        """One Messages API request with the shared policy: the per-request timeout, back-off on transport errors / 429 / 5xx (Retry-After honoured, capped), a pause shared by
        every worker after a 429. Returns (response, usage); raises LLMUnavailable when the call cannot be completed. Used by the single-shot reader and by the agent loop."""
        client = self.client()
        delay = 1.0
        waited_until = 0.0                                   # the pause this call already sat out itself
        for attempt in range(CALL_ATTEMPTS):
            self._wait_for_pause(waited_until)
            try:
                t0 = time.time()
                resp = client.messages.create(timeout=self.timeout_s, **create_kwargs)
                u = resp.usage
                return resp, {"input_tokens": getattr(u, "input_tokens", 0), "output_tokens": getattr(u, "output_tokens", 0),
                              "cache_read": getattr(u, "cache_read_input_tokens", 0) or 0, "latency_s": round(time.time() - t0, 3)}
            except (SchemaError, LLMUnavailable):
                raise
            except Exception as e:  # noqa: BLE001 - transport / rate limit / 5xx: back off and retry, then give up
                name = type(e).__name__
                retryable = any(s in name for s in ("RateLimit", "Timeout", "Connection", "InternalServer", "Overloaded", "APIStatus"))
                if not retryable or attempt == CALL_ATTEMPTS - 1:
                    raise LLMUnavailable(f"{name}: {e}") from e
                wait = min(max(delay, _retry_after(e)), MAX_BACKOFF_S)           # the server's Retry-After wins when it asks for longer
                if "RateLimit" in name or "Overloaded" in name:
                    waited_until = self._pause_all(wait)                          # every worker backs off, not only this one
                self._sleep(wait)
                delay *= 2
        raise LLMUnavailable("unreachable")

    def _pause_all(self, seconds: float) -> float:
        with self._state:
            self._pause_until = max(self._pause_until, self._clock() + seconds)
            return self._pause_until

    def _wait_for_pause(self, already_waited_until: float = 0.0) -> None:
        with self._state:
            until = self._pause_until
            remaining = until - self._clock()
        if remaining > 0 and until > already_waited_until:
            self._sleep(remaining)

    def _key_lock(self, key: str) -> threading.Lock:
        with self._state:
            return self._key_locks.setdefault(key, threading.Lock())

    def _usage_row(self, kind: str, path: Path, fhash: str, model: str, u: dict, attempt: int) -> dict:
        price = self.prices.get(model)
        cost = price.cost(u["input_tokens"], u["output_tokens"], u["cache_read"]) if price else None
        return {"ts": _now(), "provider": "anthropic", "model": model, "kind": kind, "file": path.name, "file_sha256": fhash,
                "cache_hit": False, "calls": 1 + attempt, "input_tokens": u["input_tokens"], "output_tokens": u["output_tokens"],
                "cache_read_input_tokens": u["cache_read"], "latency_s": u["latency_s"], "cost_usd": cost,
                "price_source": price.source_url if price else "unpriced", "price_date": price.date if price else ""}


def _retry_after(e: Exception) -> float:
    """Seconds from a Retry-After header of the failed response (0 when absent or not a number)."""
    headers = getattr(getattr(e, "response", None), "headers", None)
    try:
        return max(0.0, float(headers.get("retry-after"))) if headers is not None and headers.get("retry-after") else 0.0
    except (TypeError, ValueError, AttributeError):
        return 0.0


def _parse_json(raw: str) -> Any:
    s = raw.strip()
    if s.startswith("```"):
        s = s.strip("`")
        s = s[s.index("\n") + 1:] if "\n" in s else s
    return json.loads(s)


def _now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
