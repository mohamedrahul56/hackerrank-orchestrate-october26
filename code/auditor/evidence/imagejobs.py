"""Read many screenshots concurrently, apply the readings in the original order.

The model calls are the only slow, I/O-bound part of reading evidence. `prefetch` runs them in a bounded thread pool and returns an extractor
with the same signature as the one it wraps; `load_evidence` then walks the manifest sequentially as before and every `load_image` call gets its
reading (or re-raises its exception) from the prefetched results. So observations, warnings and everything derived from them come out in manifest
order whatever order the replies arrived in: concurrency changes the wall time, never the output.
"""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Callable

MAX_WORKERS = 16


def prefetch(extractor: Callable, paths: list[Path], system: str, instruction: str, schema: dict, prompt_version: str, workers: int) -> Callable:
    """Run `extractor(path, system, instruction, schema, prompt_version)` for every path with at most `workers` calls in flight."""
    workers = max(1, min(int(workers), MAX_WORKERS, len(paths) or 1))
    if workers == 1 or len(paths) < 2:
        return extractor                                          # nothing to overlap: keep the plain sequential path
    unique = list(dict.fromkeys(paths))

    def one(path: Path):
        try:
            return ("ok", extractor(path, system, instruction, schema, prompt_version))
        except BaseException as e:  # noqa: BLE001 - re-raised in manifest order by the wrapper below
            return ("err", e)

    with ThreadPoolExecutor(max_workers=workers, thread_name_prefix="image-read") as pool:
        results = dict(zip(unique, pool.map(one, unique)))        # map yields in submission order; the pool bounds the calls in flight

    def replay(path: Path, *args, **kw):
        kind, value = results[path] if path in results else one(path)
        if kind == "err":
            raise value
        return value
    replay.obs_source = getattr(extractor, "obs_source", "image:vlm")          # the source tag of the observations travels with the extractor
    return replay
