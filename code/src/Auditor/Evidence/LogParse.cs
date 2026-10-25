using System.Text.RegularExpressions;

namespace Auditor.Evidence;

/// <summary>
/// Generic parser for the seven log wrappers found in dataset/evidence/logs/. All wrappers carry the same thing: a timestamp, a service, a trace id, an event name and key=value fields.
/// We normalise the wrapper away and return one <see cref="LogEvent"/> per line, then group events that share a trace id into one record.
/// Wrappers seen:
///   1 `TS level=X service=Y trace_id=Z event=E k=v ...`         (readback traces, service_events, support_replica)
///   2 `TS LEVEL service=Y event=record_lookup seq=N system=.. resource_id=.. record_type=.. result=.. subject_reference=..`
///   3 `TS [LEVEL] [service] [trace] event: k="v" ...`            (application_trace)
///   4 `TS | LEVEL | service | trace | event | k="v" ...`         (operations_stream)
///   5 `TS requestId=T level=X function=S message="k=\"v\" ..."`  (cloud_runtime_export)
///   6 `TS {json}`                                                 (structured_service_events)
///   7 `TS &lt;pri&gt;1 node service trace - - level=X k="v" ...`        (syslog_archive)
/// </summary>
public static class LogParse
{
    private static readonly Regex Ts = new(@"^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ)\s+(.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Kv = new(@"([A-Za-z_][\w]*)=(""(?:[^""\\]|\\.)*""|\S+)", RegexOptions.CultureInvariant);
    private static readonly Regex Bracket = new(@"^\[(\w+)\]\s+\[([\w-]+)\]\s+\[([\w-]+)\]\s+(\w+):\s*(.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Syslog = new(@"^<\d+>\d+\s+(\S+)\s+(\S+)\s+(\S+)\s+-\s+-\s+(.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex LevelPrefix = new(@"^(DEBUG|INFO|WARN|ERROR)\s+", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Nulls = new(StringComparer.Ordinal) { "null", "none", "nil" };

    public sealed class LogEvent
    {
        public string Ts { get; init; } = "";
        public string Service { get; set; } = "";
        public string Trace { get; set; } = "";
        public string Event { get; set; } = "";
        public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);
        public int LineNo { get; init; }
    }

    private static string Unquote(string v)
    {
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1];
        return v.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    /// <summary>key=value pairs; the literal null/None some wrappers print for a missing value becomes an empty string.</summary>
    public static Dictionary<string, string> KvPairs(string text)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Kv.Matches(text))
        {
            var v = Unquote(m.Groups[2].Value);
            output[m.Groups[1].Value] = Nulls.Contains(v.ToLowerInvariant()) ? "" : v;
        }
        return output;
    }

    private static string Get(Dictionary<string, string> f, string key, string fallback = "") => f.TryGetValue(key, out var v) ? v : fallback;

    public static LogEvent? ParseLine(string line, int lineNo = 0)
    {
        var m = Ts.Match(line.Trim());
        if (!m.Success) return null;
        var ts = m.Groups[1].Value;
        var rest = m.Groups[2].Value;
        var ev = new LogEvent { Ts = ts, LineNo = lineNo };
        Dictionary<string, string> f;
        if (rest.StartsWith('{'))                                                    // 6 structured JSON
        {
            Dictionary<string, object?> raw;
            try { raw = Py.ParseJson(rest) as Dictionary<string, object?> ?? throw new DataException("not an object"); }
            catch (DataException) { return null; }
            f = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in raw) f[k] = v is null || Nulls.Contains(Py.Str(v).ToLowerInvariant()) ? "" : Py.Str(v);
            ev.Service = f.TryGetValue("service", out var svc) ? svc : Get(f, "logger");
            ev.Trace = Get(f, "trace_id");
        }
        else if (rest.StartsWith('['))                                               // 3 bracket style
        {
            var mm = Bracket.Match(rest);
            if (!mm.Success) return null;
            ev.Service = mm.Groups[2].Value; ev.Trace = mm.Groups[3].Value; ev.Event = mm.Groups[4].Value;
            f = KvPairs(mm.Groups[5].Value);
        }
        else if (rest.StartsWith('|'))                                               // 4 pipe style: "| LEVEL | service | trace | event | k=v ..."
        {
            var parts = rest.Trim('|', ' ').Split(" | ", 5).Select(p => p.Trim()).ToArray();
            if (parts.Length < 5) return null;
            ev.Service = parts[1]; ev.Trace = parts[2]; ev.Event = parts[3];
            f = KvPairs(parts[4]);
        }
        else if (rest.StartsWith('<'))                                               // 7 syslog
        {
            var mm = Syslog.Match(rest);
            if (!mm.Success) return null;
            ev.Service = mm.Groups[2].Value; ev.Trace = mm.Groups[3].Value;
            f = KvPairs(mm.Groups[4].Value);
            ev.Event = Get(f, "event");
        }
        else                                                                         // 1, 2, 5
        {
            rest = LevelPrefix.Replace(rest, "level=$1 ");
            f = KvPairs(rest);
            if (f.ContainsKey("message") && f.ContainsKey("function"))               // 5 cloud runtime: nested kv in message
            {
                var inner = KvPairs(f["message"]);
                f.Remove("message");
                ev.Service = Get(f, "function");
                ev.Trace = Get(f, "requestId");
                foreach (var (k, v) in inner) f[k] = v;
            }
            else
            {
                ev.Service = Get(f, "service");
                ev.Trace = Get(f, "trace_id");
            }
            ev.Event = Get(f, "event");
        }
        ev.Fields = f;
        if (ev.Event.Length == 0) ev.Event = Get(f, "event");
        return ev;
    }

    public static List<LogEvent> ParseLog(string text)
    {
        var output = new List<LogEvent>();
        int i = 0;
        foreach (var line in Py.SplitLines(text))
        {
            i++;
            var e = ParseLine(line, i);
            if (e is not null) output.Add(e);
        }
        return output;
    }
}
