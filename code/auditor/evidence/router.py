"""Per-file routing of the evidence manifest: which reader handles each file is decided here, by code, from what the file IS.

A route has two parts. `handler` names the parser for the file's format (sql, json, text, log, pdf, image) and `reader` says who does the reading:

  parser         a deterministic parser in this package (every format except images)
  image_reader   the screenshot reader (the single-shot model, a readings file, or the agent loop of `auditor/agent`): used only for images, and only when one is configured
  none           the file is not read; `reason` says why, and the run notes repeat it

The model never chooses a route and never sees anything but an image: a route is a function of the file name, the file's first bytes and whether a reader is configured. The agent loop
is therefore an escalation for the one format code cannot read, not a second opinion on formats code can. When no reader is configured, or the reader fails, the file is "not read" and
the deterministic evidence stands: exactly the behaviour before the router existed (`evaluation/router_equivalence.py` replays the old suffix dispatch and compares).

Content sniffing is limited to two unambiguous signatures (PNG, PDF): a text format is never guessed from its content, so a `.json` file is read as JSON or reported, never reinterpreted.
A file whose first bytes are a PNG or a PDF is routed by its bytes even if the name says otherwise. A `.png` without the signature still goes to the reader, which refuses it itself
(the agent loop cannot read the image header and reports 'not read'); the route only records the doubt.
"""
from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
from pathlib import Path

SUFFIX_HANDLER = {".sql": "sql", ".json": "json", ".txt": "text", ".log": "log", ".pdf": "pdf", ".png": "image"}
SIGNATURES = ((b"\x89PNG", "png"), (b"%PDF-", "pdf"))
HEAD_BYTES = 8


@dataclass(frozen=True)
class Route:
    file_id: str
    path: Path
    handler: str                 # sql | json | text | log | pdf | image | none
    reader: str                  # parser | image_reader | none
    reason: str


@dataclass(frozen=True)
class Routed:
    """A route and what running it produced (kept on `EvidenceBase.routes`, summarised in the run notes)."""
    route: Route
    outcome: str                 # parsed | no records | not read | error
    observations: int = 0


def sniff(head: bytes) -> str:
    """The kind named by an unambiguous file signature, else ''."""
    return next((kind for magic, kind in SIGNATURES if head.startswith(magic)), "")


def read_head(path: Path) -> bytes:
    try:
        with open(path, "rb") as f:
            return f.read(HEAD_BYTES)
    except OSError:
        return b""                                            # the handler will report the unreadable file with its own message


def route_file(file_id: str, path: Path, image_reader: bool) -> Route:
    """Decide the route of one manifest file. `image_reader` says whether a screenshot reader is configured for this run."""
    suffix = path.suffix.lower()
    handler = SUFFIX_HANDLER.get(suffix, "none")
    reason = f"{suffix or 'no extension'} file" if handler != "none" else f"unsupported file type {suffix}"
    kind = sniff(read_head(path))
    if kind in ("png", "pdf") and handler != ("image" if kind == "png" else "pdf"):
        handler, reason = ("image" if kind == "png" else "pdf"), f"content is a {kind.upper()}, the name says {suffix or 'no extension'}"
    elif handler == "image" and kind != "png" and path.exists():
        reason = "name says PNG but the PNG signature is missing"
    if handler == "none":
        return Route(file_id, path, "none", "none", reason)
    if handler == "image":
        if not image_reader:
            return Route(file_id, path, "image", "none", "image not read (no image extractor configured)")
        return Route(file_id, path, "image", "image_reader", reason)
    return Route(file_id, path, handler, "parser", reason)


def summary_lines(routed: list[Routed]) -> list[str]:
    """Markdown for the run notes: how many files went to which reader, and every file that was not read or read nothing."""
    by = Counter((r.route.handler, r.route.reader, r.outcome) for r in routed)
    out = ["| handler | reader | outcome | files |", "|---|---|---|---|"]
    out += [f"| {h} | {rd} | {oc} | {n} |" for (h, rd, oc), n in sorted(by.items())]
    odd = [r for r in routed if r.outcome in ("not read", "error", "no records")]
    if odd:
        out += ["", "Files that were not read or gave no record:"]
        out += [f"* {r.route.file_id} ({r.route.handler}, {r.outcome}): {r.route.reason}" for r in sorted(odd, key=lambda r: r.route.file_id)]
    return out
