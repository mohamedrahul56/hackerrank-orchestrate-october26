"""Generic parser for the seven log wrappers found in dataset/evidence/logs/.

All wrappers carry the same thing: a timestamp, a service, a trace id, an event name and key=value
fields. We normalise the wrapper away and return one `LogEvent` per line, then group events that
share a trace id into one record (the "started" line carries the lookup key, the "completed" line
carries the resource id and the result).

Wrappers seen:
  1 `TS level=X service=Y trace_id=Z event=E k=v ...`         (readback traces, service_events, support_replica)
  2 `TS LEVEL service=Y event=record_lookup seq=N system=.. resource_id=.. record_type=.. result=.. subject_reference=..`
  3 `TS [LEVEL] [service] [trace] event: k="v" ...`            (application_trace)
  4 `TS | LEVEL | service | trace | event | k="v" ...`         (operations_stream)
  5 `TS requestId=T level=X function=S message="k=\"v\" ..."`  (cloud_runtime_export)
  6 `TS {json}`                                                 (structured_service_events)
  7 `TS <pri>1 node service trace - - level=X k="v" ...`        (syslog_archive)
"""
from __future__ import annotations

import json
import re
from dataclasses import dataclass, field

TS = re.compile(r"^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ)\s+(.*)$")
KV = re.compile(r'([A-Za-z_][\w]*)=("(?:[^"\\]|\\.)*"|\S+)')
LEVELS = ("DEBUG", "INFO", "WARN", "ERROR")


@dataclass
class LogEvent:
    ts: str
    service: str = ""
    trace: str = ""
    event: str = ""
    fields: dict[str, str] = field(default_factory=dict)
    line_no: int = 0


def _unquote(v: str) -> str:
    if len(v) >= 2 and v[0] == '"' and v[-1] == '"':
        v = v[1:-1]
    return v.replace('\\"', '"').replace("\\\\", "\\")


NULLS = {"null", "none", "nil"}


def kv_pairs(text: str) -> dict[str, str]:
    """key=value pairs; the literal null/None some wrappers print for a missing value becomes an empty string."""
    out = {}
    for m in KV.finditer(text):
        v = _unquote(m.group(2))
        out[m.group(1)] = "" if v.lower() in NULLS else v
    return out


def parse_line(line: str, line_no: int = 0) -> LogEvent | None:
    m = TS.match(line.strip())
    if not m:
        return None
    ts, rest = m.groups()
    ev = LogEvent(ts=ts, line_no=line_no)
    f: dict[str, str]
    if rest.startswith("{"):                                        # 6 structured JSON
        try:
            raw = json.loads(rest)
        except json.JSONDecodeError:
            return None
        f = {k: ("" if v is None or str(v).lower() in NULLS else str(v)) for k, v in raw.items()}
        ev.service = f.get("service", f.get("logger", ""))
        ev.trace = f.get("trace_id", "")
    elif rest.startswith("["):                                       # 3 bracket style
        mm = re.match(r"\[(\w+)\]\s+\[([\w-]+)\]\s+\[([\w-]+)\]\s+(\w+):\s*(.*)$", rest)
        if not mm:
            return None
        ev.service, ev.trace, ev.event = mm.group(2), mm.group(3), mm.group(4)
        f = kv_pairs(mm.group(5))
    elif rest.startswith("|"):                                       # 4 pipe style: "| LEVEL | service | trace | event | k=v ..."
        parts = [p.strip() for p in rest.strip("| ").split(" | ", 4)]
        if len(parts) < 5:
            return None
        ev.service, ev.trace, ev.event = parts[1], parts[2], parts[3]
        f = kv_pairs(parts[4])
    elif rest.startswith("<"):                                       # 7 syslog
        mm = re.match(r"<\d+>\d+\s+(\S+)\s+(\S+)\s+(\S+)\s+-\s+-\s+(.*)$", rest)
        if not mm:
            return None
        ev.service, ev.trace = mm.group(2), mm.group(3)
        f = kv_pairs(mm.group(4))
        ev.event = f.get("event", "")
    else:                                                            # 1, 2, 5
        rest = re.sub(r"^(DEBUG|INFO|WARN|ERROR)\s+", "level=\\1 ", rest)
        f = kv_pairs(rest)
        if "message" in f and "function" in f:                       # 5 cloud runtime: nested kv in message
            inner = kv_pairs(f.pop("message"))
            ev.service = f.get("function", "")
            ev.trace = f.get("requestId", "")
            f.update(inner)
        else:
            ev.service = f.get("service", "")
            ev.trace = f.get("trace_id", "")
        ev.event = f.get("event", "")
    ev.fields = f
    if not ev.event:
        ev.event = f.get("event", "")
    return ev


def parse_log(text: str) -> list[LogEvent]:
    out = []
    for i, line in enumerate(text.splitlines(), start=1):
        e = parse_line(line, i)
        if e is not None:
            out.append(e)
    return out
