"""Assemble every Obs from every evidence file + the activity stream's catalog rows."""
from __future__ import annotations

import os
import re
from datetime import datetime
from pathlib import Path
from typing import Any

from ..dataset import DatasetError, _rows, parse_ts, try_ts
from ..schema import valid_resource_id
from . import loaders, logobs, router
from .records import EvidenceBase, idents_from


def manifest_rows(root: Path) -> list[dict]:
    """evidence_files.csv rows with parsed capture time (a malformed row is skipped, never fatal; a missing manifest means no evidence)."""
    try:
        rows = _rows(root / "evidence_files.csv", ("file_id", "file_path", "captured_at"))
    except DatasetError:
        return []
    return [{**r, "t": t} for r in rows if (t := try_ts(r["captured_at"])) is not None and r["file_id"].strip()]


def inside_root(root: Path, rel: str) -> bool:
    """True when `rel`, joined to `root` and normalised WITHOUT following symlinks, stays inside `root` (no '..' escape, no absolute path).
    Only dataset/ may be read."""
    base = os.path.normpath(os.path.abspath(root))
    target = os.path.normpath(os.path.join(base, rel))
    return target == base or target.startswith(base + os.sep)


def _run_route(base: EvidenceBase, route: router.Route, t: datetime, root: Path, image_extractor: Any) -> None:
    """Execute one route with the reader it names (the decision itself is `router.route_file`)."""
    fid, path = route.file_id, route.path
    if route.handler == "sql":
        loaders.load_sql(base, fid, path, t)
    elif route.handler == "json":
        loaders.load_json(base, fid, path, t)
    elif route.handler == "text":
        loaders.load_text(base, fid, path, t)
    elif route.handler == "log":
        logobs.load_log(base, fid, path, t)
    elif route.handler == "pdf":
        from . import pdfobs
        pdfobs.load_pdf(base, fid, path, t, catalog=catalog_rows(root))
    elif route.handler == "image" and route.reader == "image_reader":
        from . import imageobs
        imageobs.load_image(base, fid, path, t, image_extractor, catalog=catalog_rows(root))
    else:
        base.warnings.append(f"{fid}: {route.reason}")


def load_evidence(root: Path, include_images: bool = True, image_extractor: Any = None, image_workers: int = 1) -> EvidenceBase:
    """Never raises: an unreadable file becomes a warning and the rest of the evidence is still used. Every manifest file is routed first (`router.route_file`); the route and what
    it produced are kept in `base.routes`."""
    base = EvidenceBase()
    manifest = manifest_rows(root)
    if not manifest:
        base.warnings.append("evidence manifest missing, empty or unreadable: no evidence files were used")
    safe = []
    for r in manifest:
        if inside_root(root, r["file_path"]):
            safe.append(r)
        else:
            base.warnings.append(f"{r['file_id']}: manifest path {r['file_path'][:60]!r} points outside the dataset folder; not read")
    reader = include_images and image_extractor is not None
    routes = [(router.route_file(r["file_id"], root / r["file_path"], reader), r["t"]) for r in safe]
    if reader and image_workers > 1:
        from . import imagejobs, imageobs
        pngs = [rt.path for rt, _ in routes if rt.reader == "image_reader"]
        image_extractor = imagejobs.prefetch(image_extractor, pngs, imageobs.SYSTEM, imageobs.INSTRUCTION, imageobs.SCHEMA, imageobs.PROMPT_VERSION, image_workers)
    for route, t in routes:
        n0, w0 = len(base.obs), len(base.warnings)
        try:
            _run_route(base, route, t, root, image_extractor)
            failed = False
        except Exception as e:  # noqa: BLE001 - a corrupt file must never stop the run
            base.warnings.append(f"{route.file_id}: could not read {route.path.name}: {type(e).__name__}: {e}")
            failed = True
        n = len(base.obs) - n0
        outcome = "error" if failed else "parsed" if n else "not read" if len(base.warnings) > w0 or route.reader == "none" else "no records"
        base.routes.append(router.Routed(route, outcome, n))
    odd = [o for o in base.obs if not valid_resource_id(o.resource)]
    if odd:                                                       # text that is not a resource id (a path, a sentence, SQL) is not a resource: drop it, say so
        base.obs = [o for o in base.obs if valid_resource_id(o.resource)]
        base.warnings.append(f"{len(odd)} observation(s) dropped: resource id is not of the form <name>_<number> (first: {odd[0].file_id} {odd[0].resource[:40]!r})")
    return base


_CATALOG_CACHE: dict[tuple, list[dict]] = {}


def catalog_rows(root: Path) -> list[dict]:
    """LINK_RESOURCE_IDENTITY activity rows: explicit (resource, alias, email, user_id) links published by the data catalog."""
    f = root / "system_activity.csv"
    stamp = (root, f.stat().st_mtime_ns, f.stat().st_size) if f.exists() else (root, 0, 0)
    if stamp in _CATALOG_CACHE:
        return _CATALOG_CACHE[stamp]
    rows = []
    try:
        activity = _rows(root / "system_activity.csv", ("activity_id", "event_time", "event_type"))
    except DatasetError:
        activity = []
    for r in activity:
        if r["event_type"] != "LINK_RESOURCE_IDENTITY" or try_ts(r["event_time"]) is None:
            continue
        d = r.get("details", "")
        m = re.search(r"(?:linked|confirmed)\s+(\S+)\s+\(([^)]*)\)", d)
        alias = m.group(2) if m else ""
        rows.append({"activity_id": r["activity_id"], "t": parse_ts(r["event_time"]), "resource_id": r.get("resource_id", ""),
                     "system": r.get("target_system", ""), "alias": alias, "idents": idents_from([("d", d)]),
                     "only_subject": "belongs only to" in d})
    _CATALOG_CACHE[stamp] = rows
    return rows
