"""A file of readings that stands in for the vision model: the same call as `LLM.read_image_json`, answered from a file instead of a model.

The file is JSON, keyed by the sha256 of each image's BYTES (not its name or path):

    {"format": 1, "prompt_version": "img-v1", "source": "<who made the readings and how>",
     "readings": {"<sha256 of the png>": {"file": "<name, for humans>", "reading": {"screen": "...", "records": [...]}}}}

The reading of an image goes through exactly what a model's answer goes through: it must satisfy the same JSON schema (`llm.validate_schema`), and the
observations are then built by `evidence.imageobs.observations_from_reading`, which sanitises every field (capped lists, label-like status words, valid
e-mails and ids only) and ignores free text. A file therefore cannot do anything a model answer could not. An image whose hash is not in the file, or a
file made for another prompt version, is "not read" (a warning), exactly like a missing API key.

Provenance is recorded: the usage log marks every reading as `provider: file`, and the run notes name the file's source and sha256.
"""
from __future__ import annotations

import hashlib
import json
import time
from pathlib import Path

from .llm import LLMUnavailable, UsageLog, validate_schema


class ReadingsError(ValueError):
    """The readings file itself is unusable (missing, not JSON, wrong shape)."""


class FileReadings:
    def __init__(self, path: Path, usage: UsageLog | None = None):
        self.path = Path(path)
        self.usage = usage
        try:
            raw = self.path.read_bytes()
            doc = json.loads(raw.decode("utf-8"))
        except FileNotFoundError as e:
            raise ReadingsError(f"readings file not found: {self.path}") from e
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as e:
            raise ReadingsError(f"readings file {self.path.name} is not valid JSON: {e}") from e
        if not isinstance(doc, dict) or doc.get("format") != 1 or not isinstance(doc.get("readings"), dict) or not isinstance(doc.get("prompt_version"), str):
            raise ReadingsError(f"readings file {self.path.name}: expected an object with format 1, prompt_version and readings")
        self.file_sha256 = hashlib.sha256(raw).hexdigest()
        self.prompt_version: str = doc["prompt_version"]
        self.source: str = str(doc.get("source", ""))[:300]
        self.readings: dict[str, dict] = doc["readings"]

    def read_image_json(self, kind: str, image_path: Path, system: str, instruction: str, schema: dict, prompt_version: str) -> dict:
        """Same contract as LLM.read_image_json: the reading as a dict, or LLMUnavailable (no reading) / SchemaError (reading does not fit the schema)."""
        sha = hashlib.sha256(Path(image_path).read_bytes()).hexdigest()
        entry = self.readings.get(sha)
        if not isinstance(entry, dict) or "reading" not in entry:
            raise LLMUnavailable(f"no reading for sha256 {sha[:12]} in {self.path.name}")
        if self.prompt_version != prompt_version:
            raise LLMUnavailable(f"{self.path.name} was made for prompt version {self.prompt_version!r}, this run asks for {prompt_version!r}")
        reading = entry["reading"]
        validate_schema(reading, schema)                       # raises SchemaError, like a model answer that does not fit
        if self.usage is not None:
            self.usage.add({"ts": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "provider": "file", "model": "file-readings", "kind": kind, "file": Path(image_path).name,
                            "file_sha256": sha, "cache_hit": False, "calls": 0, "input_tokens": 0, "output_tokens": 0, "source": self.source[:80]})
        return reading

    def describe(self) -> str:
        return f"{self.path.name} sha256 {self.file_sha256}, {len(self.readings)} readings, prompt {self.prompt_version}, source: {self.source}"
