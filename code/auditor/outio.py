"""Reading and writing output.csv exactly like the sample answers: LF endings, csv-module quoting,
compact JSON in steps_json (no spaces)."""
from __future__ import annotations

import csv
import json
from pathlib import Path
from typing import Any

from .schema import OUTPUT_HEADER

Row = tuple[str, str, list[dict[str, Any]]]  # (case_id, deletion_status, steps)


def dumps_steps(steps: list[dict[str, Any]]) -> str:
    return json.dumps(steps, separators=(",", ":"), ensure_ascii=False)


def write_output(path: Path, rows: list[Row]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh, lineterminator="\n")
        w.writerow(OUTPUT_HEADER)
        for case_id, status, steps in rows:
            w.writerow([case_id, status, dumps_steps(steps)])


def read_output_raw(path: Path) -> tuple[list[str], list[list[str]]]:
    """Header and raw string rows (no JSON parsing) so the validator can report bad JSON per case."""
    with path.open(newline="", encoding="utf-8") as fh:
        rows = list(csv.reader(fh))
    return (rows[0] if rows else []), rows[1:]
