"""Model prices, supplied by the user - never hard-coded, never guessed.

File format (JSON), default `code/config/prices.json`, override with `AUDITOR_PRICES_FILE`:

  {"claude-haiku-5-5": {"input_per_mtok": 0.0, "output_per_mtok": 0.0, "cache_read_per_mtok": 0.0,
                        "source_url": "https://...", "date": "YYYY-MM-DD"}}

A model entry counts as *supplied* only when it has numeric input_per_mtok and output_per_mtok (>= 0), an http(s) `source_url` and an ISO `date`.
Anything else (missing file, null values, no source or date) is "prices not supplied", and reports then say exactly:
"cost not computed - prices not supplied".
"""
from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass
from pathlib import Path

NOT_SUPPLIED = "cost not computed - prices not supplied"
DEFAULT_PATH = Path(__file__).resolve().parents[1] / "config" / "prices.json"


@dataclass(frozen=True)
class Price:
    input_per_mtok: float
    output_per_mtok: float
    cache_read_per_mtok: float | None
    source_url: str
    date: str

    def cost(self, input_tokens: int, output_tokens: int, cache_read_tokens: int = 0) -> float:
        cache_rate = self.cache_read_per_mtok if self.cache_read_per_mtok is not None else self.input_per_mtok
        fresh = max(input_tokens - cache_read_tokens, 0)
        return (fresh * self.input_per_mtok + cache_read_tokens * cache_rate + output_tokens * self.output_per_mtok) / 1e6


def _num(v) -> bool:
    return isinstance(v, (int, float)) and not isinstance(v, bool) and v >= 0


def parse(doc: dict) -> tuple[dict[str, Price], list[str]]:
    """(supplied prices by model, problems). A model with any problem is simply absent from the first dict."""
    prices: dict[str, Price] = {}
    problems: list[str] = []
    for model, e in sorted(doc.items()):
        if model.startswith("_") or not isinstance(e, dict):
            continue
        bad = [k for k in ("input_per_mtok", "output_per_mtok") if not _num(e.get(k))]
        if e.get("cache_read_per_mtok") is not None and not _num(e["cache_read_per_mtok"]):
            bad.append("cache_read_per_mtok")
        if not re.match(r"https?://\S+$", str(e.get("source_url") or "")):
            bad.append("source_url")
        if not re.fullmatch(r"\d{4}-\d{2}-\d{2}", str(e.get("date") or "")):
            bad.append("date")
        if bad:
            problems.append(f"{model}: missing or invalid {', '.join(bad)}")
            continue
        prices[model] = Price(float(e["input_per_mtok"]), float(e["output_per_mtok"]),
                              None if e.get("cache_read_per_mtok") is None else float(e["cache_read_per_mtok"]), e["source_url"], e["date"])
    return prices, problems


def load(path: Path | None = None) -> tuple[dict[str, Price], list[str]]:
    p = path or Path(os.environ.get("AUDITOR_PRICES_FILE") or DEFAULT_PATH)
    if not p.exists():
        return {}, [f"no price file at {p}"]
    try:
        return parse(json.loads(p.read_text(encoding="utf-8")))
    except (OSError, json.JSONDecodeError) as e:
        return {}, [f"unreadable price file {p}: {e}"]
