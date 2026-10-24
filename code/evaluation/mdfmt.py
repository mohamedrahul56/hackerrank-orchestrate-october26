"""Tiny helpers so generated reports never print raw Python reprs (they must render as Markdown)."""
from __future__ import annotations


def kv(items) -> str:
    """{'A': 1, 'B': 2} -> 'A 1, B 2' (sorted by key); accepts a dict, a Counter or an iterable of pairs."""
    d = dict(items)
    return ", ".join(f"{k} {v}" for k, v in sorted(d.items(), key=lambda kv_: str(kv_[0]))) or "none"
