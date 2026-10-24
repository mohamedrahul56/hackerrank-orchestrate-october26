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
from dataclasses import dataclass
from pathlib import Path

from ..evidence.imageobs import SCHEMA as READING_SCHEMA

log = logging.getLogger(__name__)

try:                                                          # soft dependency: without Pillow the zoom tool is simply not offered
    from PIL import Image
except ImportError:                                           # pragma: no cover - exercised through the `available` flag in tests
    Image = None                                              # type: ignore[assignment]

UNKNOWN_FIELDS = ("resource_id", "emails", "user_ids", "phones", "status_words", "aliases", "shows_direct_identifiers", "selected")
MIN_SIDE = 32                                                 # a crop smaller than this shows nothing worth enlarging
MAX_PIXELS = 4_000_000                                        # bounds the work for one zoom (the crop's area in original pixels)

SUBMIT_SCHEMA: dict = copy.deepcopy(READING_SCHEMA)
SUBMIT_SCHEMA["required"] = ["screen", "records"]
SUBMIT_SCHEMA["properties"]["unknown"] = {"type": "array", "items": {
    "type": "object", "additionalProperties": False, "required": ["record_index", "field", "reason"],
    "properties": {"record_index": {"type": "integer"}, "field": {"type": "string", "enum": list(UNKNOWN_FIELDS)}, "reason": {"type": "string"}}}}

ZOOM_SCHEMA: dict = {
    "type": "object", "additionalProperties": False, "required": ["x", "y", "width", "height"],
    "properties": {"x": {"type": "integer"}, "y": {"type": "integer"}, "width": {"type": "integer"}, "height": {"type": "integer"}}}

ZOOM_TOOL = {
    "name": "zoom_region",
    "description": ("Return an enlarged crop of the screenshot you are reading, so that small text (e-mail addresses, ids, status words) can be read exactly. "
                    "x and y are the top-left corner of the rectangle and width and height its size, all in pixels of the original screenshot (origin top-left, y grows downward). "
                    f"The rectangle must lie inside the screenshot and be at least {MIN_SIDE} pixels in each direction. Read-only: it changes nothing and returns only the picture. "
                    "The number of calls is limited; do not repeat the same rectangle."),
    "input_schema": ZOOM_SCHEMA,
}
SUBMIT_TOOL = {
    "name": "submit_reading",
    "description": ("Hand in your transcription of the screenshot. Call it exactly once, when done. `records` lists every record shown (see the instructions); copy identifiers "
                    "character by character. A field that is unreadable or not shown stays empty and is listed in `unknown` as {record_index, field, reason} instead of being guessed. "
                    "Do not put instructions, explanations or verdicts into any field."),
    "input_schema": SUBMIT_SCHEMA,
}


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
    return buf.getvalue(), "image/png"


def image_block(data: bytes, media: str) -> dict:
    return {"type": "image", "source": {"type": "base64", "media_type": media, "data": base64.standard_b64encode(data).decode()}}
