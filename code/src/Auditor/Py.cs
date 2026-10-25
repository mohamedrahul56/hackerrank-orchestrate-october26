using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Auditor;

/// <summary>A malformed value in the data (the .NET counterpart of the ValueError / KeyError / TypeError / AttributeError the loaders recover from).</summary>
public sealed class DataException : Exception
{
    public DataException(string message) : base(message) { }
}

/// <summary>A file the run cannot do without is missing or has no usable header. Everything else degrades row by row.</summary>
public sealed class DatasetException : Exception
{
    public DatasetException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Small helpers that give .NET values the semantics the audit relies on: ordinal string order, UTC timestamps parsed like ISO-8601 with an optional zone,
/// text read with universal newlines, and the textual form of JSON values (so that "True" and "1" mean what they meant when the rules were written).
/// </summary>
public static class Py
{
    public static readonly StringComparer Ord = StringComparer.Ordinal;
    public const string TsFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    // ------------------------------------------------------------------ time
    private static readonly Regex IsoRegex = new(
        @"^(?<y>\d{4})(?<dash>-?)(?<mo>\d{2})\k<dash>(?<d>\d{2})" +
        @"(?:.(?<h>\d{2})(?:(?<c1>:?)(?<mi>\d{2})(?:\k<c1>(?<s>\d{2})(?:[.,](?<f>\d+))?)?)?" +
        @"(?<tz>[+-]\d{2}(?::?\d{2}(?::?\d{2}(?:\.\d+)?)?)?)?)?$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>Parse the dataset's ISO-8601 timestamps ('Z' or an offset) into UTC. A time without a zone is UTC too, never the machine's local time.</summary>
    public static DateTime ParseTs(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.Trim().Replace("Z", "+00:00", StringComparison.Ordinal);
        var m = IsoRegex.Match(text);
        if (!m.Success) throw new DataException($"Invalid isoformat string: '{value}'");
        int year = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture), month = int.Parse(m.Groups["mo"].Value, CultureInfo.InvariantCulture), day = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
        int hour = m.Groups["h"].Success ? int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        int minute = m.Groups["mi"].Success ? int.Parse(m.Groups["mi"].Value, CultureInfo.InvariantCulture) : 0;
        int second = m.Groups["s"].Success ? int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
            throw new DataException($"Invalid isoformat string: '{value}'");
        long ticks = 0;
        if (m.Groups["f"].Success)
        {
            var frac = m.Groups["f"].Value;
            frac = (frac.Length > 6 ? frac[..6] : frac).PadRight(6, '0');
            ticks = long.Parse(frac, CultureInfo.InvariantCulture) * 10;               // microseconds -> 100 ns ticks
        }
        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).AddTicks(ticks);
        if (!m.Groups["tz"].Success) return local;
        var tz = m.Groups["tz"].Value;
        int sign = tz[0] == '-' ? -1 : 1;
        var digits = tz[1..].Replace(":", "", StringComparison.Ordinal);
        int oh = int.Parse(digits[..2], CultureInfo.InvariantCulture);
        int om = digits.Length >= 4 ? int.Parse(digits.Substring(2, 2), CultureInfo.InvariantCulture) : 0;
        int os = digits.Length >= 6 ? int.Parse(digits.Substring(4, 2), CultureInfo.InvariantCulture) : 0;
        if (oh > 23 || om > 59 || os > 59) throw new DataException($"Invalid isoformat string: '{value}'");
        return local - sign * new TimeSpan(oh, om, os);
    }

    public static DateTime? TryTs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return ParseTs(value); }
        catch (DataException) { return null; }
    }

    /// <summary>Like ParseTs for a JSON value: anything that is not text is a malformed record (TypeError / AttributeError before).</summary>
    public static DateTime ParseTsValue(object? value) =>
        value is string s ? ParseTs(s) : throw new DataException($"timestamp must be text, got {TypeName(value)}");

    public static string FormatTs(DateTime t) => t.ToString(TsFormat, CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ text
    public static string TypeName(object? v) => v switch
    {
        null => "NoneType", bool => "bool", string => "str", long or BigInteger => "int", double => "float",
        List<object?> => "list", Dictionary<string, object?> => "dict", _ => v.GetType().Name,
    };

    /// <summary>The characters str.splitlines() breaks on.</summary>
    private static readonly char[] LineBreaks = { '\n', '\r', '\v', '\f', '\x1c', '\x1d', '\x1e', '\x85', '\u2028', '\u2029' };

    public static List<string> SplitLines(string s)
    {
        var lines = new List<string>();
        int start = 0, i = 0;
        while (i < s.Length)
        {
            if (Array.IndexOf(LineBreaks, s[i]) >= 0)
            {
                lines.Add(s[start..i]);
                if (s[i] == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                start = i + 1;
            }
            i++;
        }
        if (start < s.Length) lines.Add(s[start..]);
        return lines;
    }

    /// <summary>Whitespace-separated words (str.split() without arguments).</summary>
    public static string[] Words(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    public static bool IsAscii(string s) => s.All(c => c < 128);

    /// <summary>Read a text file like open(path).read(): strict UTF-8 (or replacement characters) and universal newlines.</summary>
    public static string ReadText(string path, bool replaceInvalid = false)
    {
        var bytes = File.ReadAllBytes(path);
        var enc = replaceInvalid ? new UTF8Encoding(false, false) : new UTF8Encoding(false, true);
        string text;
        try { text = enc.GetString(bytes); }
        catch (DecoderFallbackException e) { throw new DataException($"{Path.GetFileName(path)} is not valid UTF-8: {e.Message}"); }
        return text.Contains('\r', StringComparison.Ordinal) ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : text;
    }

    // ------------------------------------------------------------------ the textual form of JSON values
    /// <summary>str(value) for a decoded JSON value.</summary>
    public static string Str(object? v) => v switch
    {
        null => "None",
        string s => s,
        _ => Repr(v),
    };

    /// <summary>repr(value) for a decoded JSON value.</summary>
    public static string Repr(object? v)
    {
        switch (v)
        {
            case null: return "None";
            case bool b: return b ? "True" : "False";
            case long l: return l.ToString(CultureInfo.InvariantCulture);
            case BigInteger bi: return bi.ToString(CultureInfo.InvariantCulture);
            case double d: return FloatRepr(d);
            case string s: return StrRepr(s);
            case List<object?> list: return "[" + string.Join(", ", list.Select(Repr)) + "]";
            case Dictionary<string, object?> dict: return "{" + string.Join(", ", dict.Select(kv => StrRepr(kv.Key) + ": " + Repr(kv.Value))) + "}";
            default: return v.ToString() ?? "";
        }
    }

    public static string FloatRepr(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
        if (d == 0) return double.IsNegative(d) ? "-0.0" : "0.0";
        var r = d.ToString("R", CultureInfo.InvariantCulture);                       // shortest round-trip digits
        string sign = "";
        if (r[0] == '-') { sign = "-"; r = r[1..]; }
        string mant = r; int exp = 0;
        int e = r.IndexOfAny(new[] { 'E', 'e' });
        if (e >= 0) { mant = r[..e]; exp = int.Parse(r[(e + 1)..], CultureInfo.InvariantCulture); }
        string digits = mant.Replace(".", "", StringComparison.Ordinal);
        int pointPos = (mant.Contains('.', StringComparison.Ordinal) ? mant.IndexOf('.', StringComparison.Ordinal) : mant.Length) + exp;
        int lead = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0'); pointPos -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";
        int decExp = pointPos - 1;                                                       // scientific exponent
        string body;
        if (decExp < -4 || decExp >= 16)
        {
            body = digits[0] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" + (decExp < 0 ? "-" : "+") + Math.Abs(decExp).ToString("00", CultureInfo.InvariantCulture);
        }
        else if (pointPos <= 0) body = "0." + new string('0', -pointPos) + digits;
        else if (pointPos >= digits.Length) body = digits + new string('0', pointPos - digits.Length) + ".0";
        else body = digits[..pointPos] + "." + digits[pointPos..];
        return sign + body;
    }

    public static string StrRepr(string s)
    {
        char quote = s.Contains('\'', StringComparison.Ordinal) && !s.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        var sb = new StringBuilder().Append(quote);
        foreach (char c in s)
        {
            if (c == quote || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 0x20 || c == 0x7f) sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.Append(quote).ToString();
    }

    /// <summary>repr(list_of_strings): ['a', 'b']</summary>
    public static string ListRepr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(StrRepr)) + "]";

    /// <summary>bool(value) for a decoded JSON value.</summary>
    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        long l => l != 0,
        BigInteger bi => !bi.IsZero,
        double d => d != 0,
        string s => s.Length > 0,
        List<object?> l => l.Count > 0,
        Dictionary<string, object?> d => d.Count > 0,
        _ => true,
    };

    // ------------------------------------------------------------------ decoded JSON
    /// <summary>json.loads: objects become ordered dictionaries, arrays lists, integers long/BigInteger, other numbers double.</summary>
    public static object? ParseJson(string text)
    {
        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(text, new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = System.Text.Json.JsonCommentHandling.Disallow, MaxDepth = 512 });
        }
        catch (System.Text.Json.JsonException e) { throw new DataException("invalid JSON: " + e.Message); }
        using (doc) return Convert(doc.RootElement);
    }

    private static object? Convert(System.Text.Json.JsonElement e)
    {
        switch (e.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject()) d[p.Name] = Convert(p.Value);
                return d;
            case System.Text.Json.JsonValueKind.Array:
                return e.EnumerateArray().Select(Convert).ToList();
            case System.Text.Json.JsonValueKind.String: return e.GetString();
            case System.Text.Json.JsonValueKind.True: return true;
            case System.Text.Json.JsonValueKind.False: return false;
            case System.Text.Json.JsonValueKind.Number:
                var raw = e.GetRawText();
                if (raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0)
                    return long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) ? (object)l : BigInteger.Parse(raw, CultureInfo.InvariantCulture);
                return double.Parse(raw, CultureInfo.InvariantCulture);
            default: return null;
        }
    }

    /// <summary>json.dumps(value, separators=(",", ":"), ensure_ascii=False) for strings, numbers, bools, null, lists and string-keyed dictionaries.</summary>
    public static string DumpsCompact(object? v)
    {
        var sb = new StringBuilder();
        Dump(sb, v);
        return sb.ToString();
    }

    private static void Dump(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case long or int or BigInteger: sb.Append(System.Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            case double d: sb.Append(FloatRepr(d)); break;
            case string s: DumpString(sb, s); break;
            case System.Collections.IDictionary dict:
                sb.Append('{'); bool first = true;
                foreach (System.Collections.DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(','); first = false;
                    DumpString(sb, (string)kv.Key); sb.Append(':'); Dump(sb, kv.Value);
                }
                sb.Append('}'); break;
            case System.Collections.IEnumerable list:
                sb.Append('['); bool f2 = true;
                foreach (var item in list) { if (!f2) sb.Append(','); f2 = false; Dump(sb, item); }
                sb.Append(']'); break;
            default: throw new DataException($"cannot serialise {v.GetType().Name}");
        }
    }

    public static void DumpString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    // ------------------------------------------------------------------ small container helpers
    /// <summary>The value for a key of a decoded JSON object (null when absent); a non-object is a malformed record.</summary>
    public static object? Get(object? obj, string key) =>
        obj is Dictionary<string, object?> d ? (d.TryGetValue(key, out var v) ? v : null) : throw new DataException($"'{TypeName(obj)}' object has no attribute 'get'");

    public static bool Has(object? obj, string key) =>
        obj is Dictionary<string, object?> d ? d.ContainsKey(key) : throw new DataException($"argument of type '{TypeName(obj)}' is not a mapping");

    /// <summary>obj[key]: a missing key is a malformed record.</summary>
    public static object? Req(object? obj, string key) =>
        obj is Dictionary<string, object?> d ? (d.TryGetValue(key, out var v) ? v : throw new DataException($"missing key '{key}'")) : throw new DataException($"{TypeName(obj)} indices must be integers");

    public static Dictionary<string, object?> AsDict(object? obj) =>
        obj as Dictionary<string, object?> ?? throw new DataException($"expected an object, got {TypeName(obj)}");

    public static List<object?> AsList(object? obj) =>
        obj as List<object?> ?? throw new DataException($"expected an array, got {TypeName(obj)}");

    /// <summary>Iterate like `for x in value`: arrays give their items, objects their keys, anything else is a TypeError.</summary>
    public static IEnumerable<object?> Iterate(object? value) => value switch
    {
        List<object?> l => l,
        Dictionary<string, object?> d => d.Keys,
        string s => s.Select(c => (object?)c.ToString()),
        _ => throw new DataException($"'{TypeName(value)}' object is not iterable"),
    };

    /// <summary>value == n, for a decoded JSON value and a whole number (True == 1 as before; text never equals a number).</summary>
    public static bool EqualsNumber(object? v, long n) => v switch
    {
        long l => l == n, double d => d == n, BigInteger bi => bi == n, bool b => (b ? 1 : 0) == n, _ => false,
    };

    /// <summary>value &gt; 0; anything that is not a number is a malformed record (TypeError before).</summary>
    public static bool GreaterThanZero(object? v) => v switch
    {
        long l => l > 0, double d => d > 0, BigInteger bi => bi > 0, bool b => b, _ => throw new DataException($"'>' not supported between instances of '{TypeName(v)}' and 'int'"),
    };

    public static int Len(object? v) => v switch
    {
        string s => s.EnumerateRunes().Count(), List<object?> l => l.Count, Dictionary<string, object?> d => d.Count,
        _ => throw new DataException($"object of type '{TypeName(v)}' has no len()"),
    };

    /// <summary>(dict.get(key) or default) when the value is falsy.</summary>
    public static object? Or(object? value, object? fallback) => Truthy(value) ? value : fallback;

    // ------------------------------------------------------------------ comparison helpers
    public static int CompareTuple(DateTime a, string b, DateTime c, string d)
    {
        int r = a.CompareTo(c);
        return r != 0 ? r : string.CompareOrdinal(b, d);
    }

    public static int Mod(long value, int modulus) => (int)(((value % modulus) + modulus) % modulus);
}

public static class PyJsonSorted
{
    /// <summary>json.dumps(value, ensure_ascii=False, sort_keys=True) with the default separators (", " and ": ").</summary>
    public static string Dumps(object? v)
    {
        var sb = new System.Text.StringBuilder();
        Write(sb, v);
        return sb.ToString();
    }

    private static void Write(System.Text.StringBuilder sb, object? v)
    {
        switch (v)
        {
            case System.Collections.IDictionary dict:
                sb.Append('{'); bool first = true;
                foreach (var key in dict.Keys.Cast<string>().OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(", "); first = false;
                    Py.DumpString(sb, key); sb.Append(": "); Write(sb, dict[key]);
                }
                sb.Append('}'); break;
            case string or null or bool or long or int or double or System.Numerics.BigInteger:
                sb.Append(Py.DumpsCompact(v)); break;
            case System.Collections.IEnumerable list:
                sb.Append('['); bool f2 = true;
                foreach (var item in list) { if (!f2) sb.Append(", "); f2 = false; Write(sb, item); }
                sb.Append(']'); break;
            default: throw new DataException($"cannot serialise {v.GetType().Name}");
        }
    }
}
