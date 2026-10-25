"""Versioned prompt files under code/prompts/: `---\\nversion: <label>\\n---\\n<body>`. The label and the sha256 of the text are part of the cache key and the run stamp."""
from __future__ import annotations

import hashlib
import re
from dataclasses import dataclass
from pathlib import Path

PROMPT_DIR = Path(__file__).resolve().parents[2] / "prompts"
_FRONT = re.compile(r"\A---\nversion:\s*(?P<v>[A-Za-z0-9._-]+)\s*\n---\n", re.S)


class PromptError(ValueError):
    pass


@dataclass(frozen=True)
class Prompt:
    name: str
    version: str
    text: str
    sha256: str

    def fill(self, **values: object) -> str:
        """Replace `{key}` placeholders by value. Only the named keys are replaced; other braces stay as they are."""
        out = self.text
        for k, v in values.items():
            out = out.replace("{" + k + "}", str(v))
        return out


def load(name: str, directory: Path | None = None) -> Prompt:
    path = (directory or PROMPT_DIR) / name
    try:
        raw = path.read_text(encoding="utf-8")
    except OSError as e:
        raise PromptError(f"prompt file not found: {path.name}") from e
    m = _FRONT.match(raw)
    if not m:
        raise PromptError(f"{path.name}: the file must start with a front matter block '---', 'version: <label>', '---'")
    body = raw[m.end():].strip()
    if not body:
        raise PromptError(f"{path.name}: empty prompt")
    return Prompt(name, m["v"], body, hashlib.sha256(raw.encode("utf-8")).hexdigest())
