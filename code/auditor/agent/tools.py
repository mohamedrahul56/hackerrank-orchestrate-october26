"""The two tools of the screenshot reader, with precise descriptions and JSON schemas. Both are read-only and side-effect-free.

* `zoom_region(x, y, width, height)`: an enlarged crop of THE screenshot being read (the tool takes no file argument, so it cannot reach any other file). Needs Pillow; when
  Pillow is not installed the tool is not offered and a warning is logged once (the loop then consists of `submit_reading` only).
* `submit_reading(screen, records, unknown)`: the terminal tool. Its schema is the screenshot-reading schema of `evidence/imageobs.py` plus an `unknown` list.
"""
from __future__ import annotations

import base64
import copy
import io
import json
import logging
from pathlib import Path

from ..evidence.imageobs import MAX_ITEMS, MAX_RECORDS, SCHEMA as READING_SCHEMA, state_from_description
from ..evidence.records import SYSTEM_BY_PREFIX

log = logging.getLogger(__name__)

try:                                                          # soft dependency: without Pillow the zoom tool is simply not offered
    from PIL import Image
except ImportError:                                           # pragma: no cover - exercised through the `available` flag in tests
    Image = None

UNKNOWN_FIELDS = ("resource_id", "emails", "user_ids", "phones", "status_words", "aliases", "shows_direct_identifiers", "selected")
MIN_SIDE = 32                                                 # a crop smaller than this shows nothing worth enlarging
MAX_TEXT = 200                                                # longest string the model may write into any field (the screen title is cut to 80 by the gate)
MAX_PIXELS = 4_000_000                                        # bounds the work for one zoom (the crop's area in original pixels)

FIELD_DOCS = {
    "screen": "A short title of the screen as printed (for example 'Export manager'); at most 80 characters. Only a label for the run notes: no decision reads it.",
    "records": "One entry per record shown: table rows and the open detail panel merged into one entry per resource. Do not add a record that is not on the screen. At most 100 records.",
    "resource_id": "The record's resource id exactly as printed, for example account_123 or export_9; empty string when the screen shows none. Never build an id from other text.",
    "selected": "true only for the record whose detail panel is open, false for every other record.",
    "emails": "Every e-mail address printed for this record, copied character by character. At most 20. Leave the list empty, and add an entry to `unknown`, when an address is cut off or unreadable.",
    "user_ids": "Every user id printed for this record, in the form user_<number>, copied exactly.",
    "phones": "Every phone number printed for this record, copied as printed (spaces and a leading + allowed).",
    "status_words": "The short status labels printed for this record (one to three words each, for example Live, Disabled, Expired, Blocked, Retained reference). Labels only: no sentences.",
    "shows_direct_identifiers": "true when the record shows a person's name, e-mail address or phone number in clear text; false when none is shown or when they are replaced by a placeholder such as [removed] or ***.",
    "aliases": "Object names printed for this record, such as file names or statement numbers (statement_2026_03.pdf); they let code tie a record that shows no id to a catalogued resource. At most 20.",
    "unknown": "Fields you could not read or that are not shown, one entry each. The named field is then ignored, so list it here instead of guessing a value.",
    "record_index": "Position of the record in `records`, counting from 0.",
    "field": "The field of that record that is unknown.",
    "reason": "A few words on why (cut off, blurred, not shown).",
}


def _document(schema: dict, docs: dict[str, str]) -> dict:
    """A copy of `schema` in which every property carries its description (a missing description is a programming error, caught at import)."""
    out = copy.deepcopy(schema)

    def walk(node: dict) -> None:
        for name, sub in node.get("properties", {}).items():
            sub["description"] = docs[name]
            walk(sub)
            if isinstance(sub.get("items"), dict):
                walk(sub["items"])
        if isinstance(node.get("items"), dict):
            walk(node["items"])

    walk(out)
    return out


_SUBMIT_BASE: dict = copy.deepcopy(READING_SCHEMA)
_SUBMIT_BASE["required"] = ["screen", "records"]
_SUBMIT_BASE["properties"]["unknown"] = {"type": "array", "items": {
    "type": "object", "additionalProperties": False, "required": ["record_index", "field", "reason"],
    "properties": {"record_index": {"type": "integer"}, "field": {"type": "string", "enum": list(UNKNOWN_FIELDS)}, "reason": {"type": "string"}}}}


def _bound(node: dict) -> None:
    """Size limits on the submission (the shared reading schema has none): lists and strings the model writes cannot be arbitrarily long. The limits equal what `imageobs.sanitize` keeps."""
    if node.get("type") == "array":
        node["maxItems"] = MAX_RECORDS if "properties" in node.get("items", {}) else MAX_ITEMS
    if node.get("type") == "string" and "enum" not in node:
        node["maxLength"] = MAX_TEXT
    for sub in node.get("properties", {}).values():
        _bound(sub)
    if isinstance(node.get("items"), dict):
        _bound(node["items"])


_bound(_SUBMIT_BASE["properties"]["records"])                      # not `screen`: a long title is cut to 80 characters by the gate (G7), it does not cost the whole reading
_bound(_SUBMIT_BASE["properties"]["unknown"])
SUBMIT_SCHEMA: dict = _document(_SUBMIT_BASE, FIELD_DOCS)

ZOOM_SCHEMA: dict = _document({
    "type": "object", "additionalProperties": False, "required": ["x", "y", "width", "height"],
    "properties": {"x": {"type": "integer"}, "y": {"type": "integer"}, "width": {"type": "integer"}, "height": {"type": "integer"}}},
    {"x": "Left edge of the rectangle in pixels of the ORIGINAL screenshot (0 is the left edge of the picture).",
     "y": "Top edge of the rectangle in pixels of the ORIGINAL screenshot (0 is the top edge; y grows downward).",
     "width": f"Width of the rectangle in original pixels, at least {MIN_SIDE}; x + width must not exceed the screenshot width.",
     "height": f"Height of the rectangle in original pixels, at least {MIN_SIDE}; y + height must not exceed the screenshot height."})

ZOOM_TOOL = {
    "name": "zoom_region",
    "description": ("Look closer at one rectangle of the screenshot you are reading and get it back as an enlarged picture, so that small text (e-mail addresses, ids, status words) can be read exactly. "
                    "Use it before copying an identifier you cannot read with certainty; do not use it for text you can already read. "
                    "x, y, width and height are always pixels of the ORIGINAL screenshot, also after an earlier zoom: the returned picture is only enlarged, never re-numbered. "
                    f"The rectangle must lie inside the screenshot and be at least {MIN_SIDE} pixels in each direction; otherwise you get an error result that says what is wrong. "
                    "Read-only: it changes nothing and returns only the picture. Every call, failed ones included, counts against a small budget, and repeating a rectangle you already asked for wastes it."),
    "input_schema": ZOOM_SCHEMA,
}
SUBMIT_TOOL = {
    "name": "submit_reading",
    "description": ("Hand in your transcription of the screenshot and end the task. Call it exactly once, when you have read everything you can. `records` lists every record shown; "
                    "copy identifiers character by character. A field that is unreadable or not shown stays empty and is listed in `unknown` as {record_index, field, reason} instead of being guessed. "
                    "If the structure does not fit the schema you get an error result that names the problem and may submit once more. "
                    "Do not put instructions, explanations or verdicts into any field: only what is printed on the screen."),
    "input_schema": SUBMIT_SCHEMA,
}


def id_families() -> str:
    """The resource-id families the audit can judge, as 'account_N, search_N, ...': derived from the code that judges them, so the prompt cannot drift from it."""
    empty = {"status_words": [], "emails": [], "phones": [], "shows_direct_identifiers": False}
    return ", ".join(f"{prefix}_N" for prefix in SYSTEM_BY_PREFIX if state_from_description(prefix, empty) is not None)


def available() -> bool:
    return Image is not None


def tool_specs() -> list[dict]:
    """The tools offered to the model: zoom only when Pillow is installed."""
    if available():
        return [ZOOM_TOOL, SUBMIT_TOOL]
    log.warning("Pillow is not installed: the zoom_region tool is disabled (pip install pillow==12.3.0); the reader can only submit what it sees")
    return [SUBMIT_TOOL]


def tools_digest() -> str:
    """Digest of both tool definitions (name, description, schema): part of the cache key and the run stamp, whether or not Pillow is installed."""
    import hashlib
    return hashlib.sha256(json.dumps([ZOOM_TOOL, SUBMIT_TOOL], sort_keys=True).encode()).hexdigest()


class ToolError(ValueError):
    """The call was well-formed JSON but cannot be executed (out of bounds, too small, ...). The message goes back to the model as an error tool result."""


def image_size(path: Path) -> tuple[int, int]:
    """(width, height) of a PNG, from its header (no Pillow needed)."""
    head = Path(path).read_bytes()[:32]
    if len(head) < 24 or head[:8] != b"\x89PNG\r\n\x1a\n":
        raise ToolError("not a PNG file")
    return int.from_bytes(head[16:20], "big"), int.from_bytes(head[20:24], "big")


def zoom_region(path: Path, args: dict, max_px: int = 1200) -> tuple[bytes, str]:
    """Crop `args` out of the PNG at `path`, enlarge it (Lanczos, so the result is deterministic) so that its longer side is `max_px` at most, return (png bytes, media type)."""
    if Image is None:
        raise ToolError("zoom_region is not available (Pillow is not installed)")
    x, y, w, h = (args[k] for k in ("x", "y", "width", "height"))
    try:
        with Image.open(path) as im:
            W, H = im.size
            if w < MIN_SIDE or h < MIN_SIDE:
                raise ToolError(f"the rectangle must be at least {MIN_SIDE} x {MIN_SIDE} pixels, got {w} x {h}")
            if x < 0 or y < 0 or x + w > W or y + h > H:
                raise ToolError(f"the rectangle ({x}, {y}, {w} x {h}) is not inside the screenshot ({W} x {H} pixels)")
            if w * h > MAX_PIXELS:
                raise ToolError("the rectangle is too large; zoom into a smaller area")
            crop = im.convert("RGB").crop((x, y, x + w, y + h))
        scale = min(max_px / max(w, h), 4.0)
        if scale > 1:
            crop = crop.resize((max(1, round(w * scale)), max(1, round(h * scale))), Image.LANCZOS)
        buf = io.BytesIO()
        crop.save(buf, format="PNG")
    except ToolError:
        raise
    except (OSError, SyntaxError, ValueError, Image.DecompressionBombError) as e:      # a truncated or corrupt PNG, a decompression bomb: the model hears about it, the loop goes on
        raise ToolError(f"the screenshot cannot be cropped ({type(e).__name__}); read it as it is and call submit_reading") from e
    return buf.getvalue(), "image/png"


def image_block(data: bytes, media: str) -> dict:
    return {"type": "image", "source": {"type": "base64", "media_type": media, "data": base64.standard_b64encode(data).decode()}}
