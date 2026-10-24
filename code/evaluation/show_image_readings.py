#!/usr/bin/env python3
"""Put every screenshot next to what the vision model read from it, so you can check the readings by eye.

  export ANTHROPIC_API_KEY=...      # in your shell only - this script never asks for it and never writes it anywhere
  python3 code/evaluation/show_image_readings.py                 # all PNGs -> code/out/image_readings.html (open it in a browser)
  python3 code/evaluation/show_image_readings.py --text          # same content as plain text in the terminal
  python3 code/evaluation/show_image_readings.py --only <file_id> [<file_id> ...]     # ids from evidence_files.csv

For each image the page shows: the picture, the raw JSON the model returned, and the observations the code derives from it
(resource, data category, state, identifiers) - exactly what the pipeline would use. Readings are cached (AUDITOR_CACHE_DIR), so the
pipeline run that follows costs nothing extra. Model usage of this preview goes to code/out/image_preview_usage.jsonl, NOT to the
usage log of the final run.
"""
from __future__ import annotations

import argparse
import base64
import csv
import html
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "code"))

from auditor.dataset import parse_ts  # noqa: E402
from auditor.evidence import imageobs  # noqa: E402
from auditor.evidence.load import catalog_rows  # noqa: E402
from auditor.evidence.records import EvidenceBase  # noqa: E402

OUT_HTML = ROOT / "code" / "out" / "image_readings.html"
NO_KEY = ("ANTHROPIC_API_KEY is not set, so no screenshot can be read.\n"
          "Set it in your own shell (it is never written to a file or asked for in chat):\n"
          "    export ANTHROPIC_API_KEY=...\n"
          "and run this script again. Nothing was sent anywhere.")


def png_rows(root: Path, only: list[str] | None = None) -> list[dict]:
    with (root / "evidence_files.csv").open(newline="", encoding="utf-8") as fh:
        rows = [r for r in csv.DictReader(fh) if r["file_path"].lower().endswith(".png")]
    return [r for r in rows if not only or r["file_id"] in only]


def read_all(root: Path, extractor, only: list[str] | None = None) -> list[dict]:
    """[{row, path, reading | None, error, observations}] - one entry per PNG, in manifest order."""
    catalog = catalog_rows(root)
    items = []
    for r in png_rows(root, only):
        path = root / r["file_path"]
        item = {"row": r, "path": path, "reading": None, "error": "", "observations": []}
        try:
            item["reading"] = extractor(path, imageobs.SYSTEM, imageobs.INSTRUCTION, imageobs.SCHEMA, imageobs.PROMPT_VERSION)
            base = EvidenceBase()
            imageobs.observations_from_reading(base, r["file_id"], parse_ts(r["captured_at"]), item["reading"], catalog)
            item["observations"] = [{"resource": o.resource, "system": o.system, "category": o.category, "state": o.state,
                                     "identifiers": sorted(o.idents), "detail": o.detail} for o in base.obs]
        except Exception as e:  # noqa: BLE001 - show the failure next to the picture instead of stopping
            item["error"] = f"{type(e).__name__}: {e}"
        items.append(item)
    return items


def render_text(items: list[dict]) -> str:
    out = []
    for it in items:
        r = it["row"]
        out += [f"=== {r['file_id']}  {r['file_path']}  (captured {r['captured_at']}, origin {r['origin']})"]
        if it["error"]:
            out.append(f"READING FAILED: {it['error']}")
            continue
        out += ["--- model reading:", json.dumps(it["reading"], indent=1, ensure_ascii=False), "--- derived observations:"]
        out += [f"  {o['resource']:<22} {o['category']:<20} {o['state']:<11} ids={o['identifiers']} ({o['detail']})" for o in it["observations"]] or ["  (none)"]
        out.append("")
    return "\n".join(out)


def render_html(items: list[dict]) -> str:
    cards = []
    for it in items:
        r = it["row"]
        img = base64.b64encode(it["path"].read_bytes()).decode()
        if it["error"]:
            right = f"<p class='bad'>READING FAILED: {html.escape(it['error'])}</p>"
        else:
            obs = "".join(f"<tr><td>{html.escape(o['resource'])}</td><td>{html.escape(o['category'])}</td><td><b>{html.escape(o['state'])}</b></td>"
                          f"<td>{html.escape(', '.join(o['identifiers']))}</td><td>{html.escape(o['detail'])}</td></tr>" for o in it["observations"])
            right = (f"<h4>Model reading (raw JSON)</h4><pre>{html.escape(json.dumps(it['reading'], indent=1, ensure_ascii=False))}</pre>"
                     f"<h4>Observations the code derives</h4><table><tr><th>resource</th><th>category</th><th>state</th><th>identifiers</th><th>shown as</th></tr>"
                     f"{obs or '<tr><td colspan=5>(none)</td></tr>'}</table>")
        cards.append(f"<section><h3>{html.escape(r['file_id'])} - {html.escape(r['file_path'])}</h3>"
                     f"<p>captured {html.escape(r['captured_at'])}, origin {html.escape(r['origin'])}</p>"
                     f"<div class='row'><div class='img'><img src='data:image/png;base64,{img}' alt='{html.escape(r['file_id'])}'></div><div class='txt'>{right}</div></div></section>")
    css = ("body{font-family:system-ui,sans-serif;margin:16px;background:#fafafa;color:#111}section{border:1px solid #ccc;background:#fff;margin:0 0 24px;padding:12px;border-radius:6px}"
           ".row{display:flex;gap:16px;flex-wrap:wrap}.img{flex:1 1 480px}.img img{max-width:100%;border:1px solid #ddd}.txt{flex:1 1 420px;min-width:0}"
           "pre{background:#f2f2f2;padding:8px;overflow:auto;max-height:420px;font-size:12px}table{border-collapse:collapse;font-size:13px}td,th{border:1px solid #ccc;padding:3px 6px;text-align:left}.bad{color:#b00020}")
    return f"<!doctype html><meta charset='utf-8'><title>Image readings</title><style>{css}</style><h1>Screenshots and what the model read ({len(items)})</h1>" + "".join(cards)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dataset", type=Path, default=ROOT / "dataset")
    ap.add_argument("--only", nargs="*", help="file ids to show (default: all PNGs)")
    ap.add_argument("--text", action="store_true", help="print plain text instead of writing the HTML page")
    ap.add_argument("--out", type=Path, default=OUT_HTML)
    a = ap.parse_args(argv)
    if not (os.environ.get("ANTHROPIC_API_KEY") or os.environ.get("ANTHROPIC_AUTH_TOKEN")):
        print(NO_KEY, file=sys.stderr)
        return 2
    from auditor.llm import LLM
    usage = ROOT / "code" / "out" / "image_preview_usage.jsonl"
    llm = LLM(usage_path=usage)
    items = read_all(a.dataset, lambda path, system, instruction, schema, version: llm.read_image_json("image", path, system, instruction, schema, version), a.only)
    if not items:
        print("no PNG files in the manifest" + (f" matching {a.only}" if a.only else ""), file=sys.stderr)
        return 1
    if a.text:
        print(render_text(items))
    else:
        a.out.parent.mkdir(parents=True, exist_ok=True)
        a.out.write_text(render_html(items), encoding="utf-8")
        print(f"wrote {a.out} ({len(items)} images, {sum(1 for i in items if i['error'])} failed) - open it in a browser")
    return 1 if any(i["error"] for i in items) else 0


if __name__ == "__main__":
    raise SystemExit(main())
