"""Tunable settings in one place: defaults here, documented overrides in config/settings.toml, command-line overrides on top.

Precedence (lowest to highest): built-in defaults  <  settings file  <  AUDITOR_* environment (model names, cache dir only)  <  command line.
Unknown keys, wrong types and out-of-range values are errors: a typo in a settings file must not silently run with the default.

`fingerprint()` is a hash of every setting that can change the answer (not paths); the run stamp in output.notes.md and the resume key
(auditor/checkpoint.py) use it, so a run with other settings never reuses another run's results.
"""
from __future__ import annotations

import hashlib
import json
import os
from dataclasses import dataclass, field, fields, replace
from datetime import timedelta
from pathlib import Path
from typing import Any

try:
    import tomllib
except ModuleNotFoundError:                                  # Python < 3.11
    tomllib = None                                           # type: ignore[assignment]

DEFAULT_FILE = Path(__file__).resolve().parents[1] / "config" / "settings.toml"


class SettingsError(ValueError):
    pass


@dataclass(frozen=True)
class Tunables:
    """Numbers the activity-timeline discovery and the late-link rule depend on. Measured on this dataset (see NOTES.md 12, CHANGELOG)."""
    account_window_minutes: tuple[float, float] = (-90.0, 15.0)      # a deleted account's delete event relative to the case's reported_at
    analytics_window_minutes: tuple[float, float] = (-45.0, -5.0)    # an analytics anonymization event relative to reported_at
    phase_margin: float = 2.0                                        # log-likelihood lead over the runner-up an account must have (about 7:1)
    analytics_min_llr: float = 2.0                                   # log-likelihood an analytics candidate's offset must reach
    chain_gaps_seconds: tuple[int, int] = (390, 449)                 # seconds between consecutive deletion-worker policy actions
    chain_tolerance_seconds: float = 2.0
    later_link_max_days: int = 7                                     # evidence captured later than audit_at + this may not link a resource

    @property
    def account_window(self) -> tuple[timedelta, timedelta]:
        return timedelta(minutes=self.account_window_minutes[0]), timedelta(minutes=self.account_window_minutes[1])

    @property
    def analytics_window(self) -> tuple[timedelta, timedelta]:
        return timedelta(minutes=self.analytics_window_minutes[0]), timedelta(minutes=self.analytics_window_minutes[1])


@dataclass(frozen=True)
class Settings:
    # [pipeline]
    later_linking: bool = True            # evidence captured after audit_at may tie a resource to the user (never gives state, never cited)
    discovery: bool = True                # activity-timeline discovery of accounts / analytics that carry no identifier
    timing_heuristics: bool = True        # second-of-minute heuristics (dataset specific; see README "Known weaknesses")
    cohort_filter: bool = False           # ablation only (measured harmful)
    provisional_rows: bool = True         # screenshot-only deletion requests get a provisional INCOMPLETE row without a vision key
    # [discovery] / [audit]
    tune: Tunables = field(default_factory=Tunables)
    # [llm]
    images: str = "auto"                  # auto | on | off
    model: str = "claude-haiku-5-5"
    fallback_model: str = "claude-sonnet-5-5"
    cache_dir: str = ""                   # "" = code/.cache/llm
    max_concurrency: int = 4              # screenshots read at the same time (1 = sequential); does not change the answer
    timeout_seconds: float = 60.0         # per model request; does not change the answer

    def fingerprint(self) -> str:
        """Hash of everything that can change the answer. cache_dir is excluded (a path); the model names count only when images are read."""
        d = _to_plain(self)
        for k in ("cache_dir", "max_concurrency", "timeout_seconds"):     # how the model is called, not what the answer is
            d.pop(k, None)
        if self.images == "off":
            d.pop("model", None), d.pop("fallback_model", None)
        return hashlib.sha256(json.dumps(d, sort_keys=True).encode()).hexdigest()


def _to_plain(s: Any) -> dict:
    out = {}
    for f in fields(s):
        v = getattr(s, f.name)
        out[f.name] = _to_plain(v) if hasattr(v, "__dataclass_fields__") else (list(v) if isinstance(v, tuple) else v)
    return out


# ---------------------------------------------------------------------------------------------- file / overrides
_TOP = {f.name for f in fields(Settings)} - {"tune"}
_TUNE = {f.name for f in fields(Tunables)}
_SECTIONS = {"pipeline": {"later_linking", "discovery", "timing_heuristics", "cohort_filter", "provisional_rows"},
             "llm": {"images", "model", "fallback_model", "cache_dir", "max_concurrency", "timeout_seconds"},
             "discovery": {"account_window_minutes", "analytics_window_minutes", "phase_margin", "analytics_min_llr", "chain_gaps_seconds", "chain_tolerance_seconds"},
             "audit": {"later_link_max_days"}}


def _coerce(name: str, current: Any, value: Any) -> Any:
    """Check `value` against the type of the default and return it in the field's canonical type."""
    if isinstance(current, bool):
        if isinstance(value, str) and value.lower() in ("true", "false"):
            value = value.lower() == "true"
        if not isinstance(value, bool):
            raise SettingsError(f"{name}: expected true/false, got {value!r}")
        return value
    if isinstance(current, tuple):
        if isinstance(value, str):
            try:
                value = json.loads(value)
            except json.JSONDecodeError as e:
                raise SettingsError(f"{name}: expected a list like [1, 2], got {value!r}") from e
        if not isinstance(value, (list, tuple)) or len(value) != len(current) or not all(isinstance(x, (int, float)) and not isinstance(x, bool) for x in value):
            raise SettingsError(f"{name}: expected a list of {len(current)} numbers, got {value!r}")
        return tuple(type(c)(x) for c, x in zip(current, value))
    if isinstance(current, (int, float)):
        try:
            num = float(value) if isinstance(current, float) else int(value)
        except (TypeError, ValueError) as e:
            raise SettingsError(f"{name}: expected a number, got {value!r}") from e
        if isinstance(value, bool):
            raise SettingsError(f"{name}: expected a number, got {value!r}")
        return num
    if not isinstance(value, str):
        raise SettingsError(f"{name}: expected text, got {value!r}")
    return value


def _validate(s: Settings) -> Settings:
    if s.images not in ("auto", "on", "off"):
        raise SettingsError(f"llm.images: must be auto, on or off, got {s.images!r}")
    t = s.tune
    for n in ("account_window_minutes", "analytics_window_minutes"):
        lo, hi = getattr(t, n)
        if lo >= hi:
            raise SettingsError(f"discovery.{n}: the start ({lo}) must be before the end ({hi})")
    if not 1 <= s.max_concurrency <= 16:
        raise SettingsError(f"llm.max_concurrency: must be between 1 and 16, got {s.max_concurrency}")
    if s.timeout_seconds <= 0:
        raise SettingsError("llm.timeout_seconds: must be positive")
    if t.phase_margin < 0 or t.analytics_min_llr < 0 or t.chain_tolerance_seconds < 0 or t.later_link_max_days < 0:
        raise SettingsError("discovery/audit numbers must not be negative")
    if min(t.chain_gaps_seconds) <= 0:
        raise SettingsError("discovery.chain_gaps_seconds: gaps must be positive")
    return s


def apply(s: Settings, dotted: str, value: Any) -> Settings:
    """Return a copy of `s` with one `section.key` replaced (the unit both the file loader and --set use)."""
    if "." not in dotted:
        raise SettingsError(f"{dotted!r}: use section.key, e.g. pipeline.discovery")
    section, key = dotted.split(".", 1)
    if section not in _SECTIONS or key not in _SECTIONS[section]:
        known = ", ".join(f"{sec}.{k}" for sec, ks in sorted(_SECTIONS.items()) for k in sorted(ks))
        raise SettingsError(f"unknown setting {dotted!r}; known settings: {known}")
    if section in ("discovery", "audit"):
        new = _coerce(dotted, getattr(s.tune, key), value)
        return _validate(replace(s, tune=replace(s.tune, **{key: new})))
    return _validate(replace(s, **{key: _coerce(dotted, getattr(s, key), value)}))


def from_file(path: Path | None = None, base: Settings | None = None) -> Settings:
    s = base or Settings()
    path = path or DEFAULT_FILE
    if not path.exists():
        if path != DEFAULT_FILE:
            raise SettingsError(f"settings file not found: {path}")
        return s                                              # the shipped file is optional; the built-in defaults are the same numbers
    if tomllib is None:
        raise SettingsError("reading a settings file needs Python 3.11+ (tomllib)")
    try:
        data = tomllib.loads(path.read_text(encoding="utf-8"))
    except (OSError, tomllib.TOMLDecodeError) as e:
        raise SettingsError(f"cannot read {path}: {e}") from e
    for section, body in data.items():
        if section not in _SECTIONS or not isinstance(body, dict):
            raise SettingsError(f"{path.name}: unknown section [{section}]")
        for key, value in body.items():
            s = apply(s, f"{section}.{key}", value)
    return s


def from_env(s: Settings, env: dict[str, str] | None = None) -> Settings:
    env = os.environ if env is None else env
    for var, dotted in (("AUDITOR_VLM_MODEL", "llm.model"), ("AUDITOR_VLM_FALLBACK_MODEL", "llm.fallback_model"), ("AUDITOR_CACHE_DIR", "llm.cache_dir")):
        if env.get(var):
            s = apply(s, dotted, env[var])
    return s


def load(path: Path | None = None, overrides: list[str] | None = None, env: dict[str, str] | None = None) -> Settings:
    """defaults < file < environment < `overrides` (a list of 'section.key=value' strings)."""
    s = from_env(from_file(path), env)
    for item in overrides or []:
        if "=" not in item:
            raise SettingsError(f"--set {item!r}: use section.key=value")
        k, v = item.split("=", 1)
        s = apply(s, k.strip(), v.strip())
    return s


def as_table(s: Settings) -> list[tuple[str, str]]:
    """(section.key, value) in file order, for the run stamp."""
    rows = []
    for sec, keys in _SECTIONS.items():
        for k in sorted(keys):
            v = getattr(s.tune, k) if sec in ("discovery", "audit") else getattr(s, k)
            rows.append((f"{sec}.{k}", json.dumps(list(v) if isinstance(v, tuple) else v)))
    return rows

