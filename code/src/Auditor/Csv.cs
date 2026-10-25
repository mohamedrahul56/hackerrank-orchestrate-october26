using System.Text;

namespace Auditor;

/// <summary>
/// CSV reading and writing with the dialect the dataset and the output file use: comma, double quote doubled inside quotes, quoted fields may hold line breaks,
/// a stray quote after a closing quote is literal text (lenient), and a field is limited in size so that one huge cell is a clear error and not an out-of-memory crash.
/// </summary>
public static class Csv
{
    public const int FieldSizeLimit = 16 * 1024 * 1024;

    private enum State { StartRecord, StartField, InField, InQuotedField, QuoteInQuotedField, EatNewline }

    /// <summary>Every record of <paramref name="text"/> as a list of fields (an empty line is an empty record).</summary>
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var state = State.StartRecord;

        void Save()
        {
            fields.Add(field.ToString());
            field.Clear();
        }

        void Add(char c)
        {
            if (field.Length >= FieldSizeLimit) throw new DataException($"field larger than field limit ({FieldSizeLimit})");
            field.Append(c);
        }

        void EndLine()                                          // the end of one physical line (the reader's EOL marker)
        {
            switch (state)
            {
                case State.StartRecord: rows.Add(new List<string>()); break;      // an empty line
                case State.StartField: Save(); rows.Add(fields); fields = new List<string>(); state = State.StartRecord; break;
                case State.InField:
                case State.QuoteInQuotedField: Save(); rows.Add(fields); fields = new List<string>(); state = State.StartRecord; break;
                case State.EatNewline: rows.Add(fields); fields = new List<string>(); state = State.StartRecord; break;
                case State.InQuotedField: break;                                  // the record goes on in the next line
            }
        }

        foreach (var line in PhysicalLines(text))
        {
            foreach (char c in line)
            {
                switch (state)
                {
                    case State.StartRecord:
                        if (c is '\n' or '\r') { state = State.EatNewline; break; }
                        state = State.StartField;
                        goto case State.StartField;
                    case State.StartField:
                        if (c is '\n' or '\r') { Save(); state = State.EatNewline; }
                        else if (c == '"') state = State.InQuotedField;
                        else if (c == ',') Save();
                        else { Add(c); state = State.InField; }
                        break;
                    case State.InField:
                        if (c is '\n' or '\r') { Save(); state = State.EatNewline; }
                        else if (c == ',') { Save(); state = State.StartField; }
                        else Add(c);
                        break;
                    case State.InQuotedField:
                        if (c == '"') state = State.QuoteInQuotedField; else Add(c);
                        break;
                    case State.QuoteInQuotedField:
                        if (c == '"') { Add('"'); state = State.InQuotedField; }
                        else if (c == ',') { Save(); state = State.StartField; }
                        else if (c is '\n' or '\r') { Save(); state = State.EatNewline; }
                        else { Add(c); state = State.InField; }
                        break;
                    case State.EatNewline:
                        if (c is not ('\n' or '\r')) throw new DataException("new-line character seen in unquoted field - do you need to open the file in universal-newline mode?");
                        break;
                }
            }
            EndLine();
        }
        if (field.Length != 0 || state == State.InQuotedField)                    // end of input inside a field: the record ends there
        {
            Save();
            rows.Add(fields);
        }
        return rows;
    }

    /// <summary>The lines of a text file as a file iterator without newline translation gives them: each keeps its own terminator (\n, \r\n or \r).</summary>
    private static IEnumerable<string> PhysicalLines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') { yield return text[start..(i + 1)]; start = i + 1; }
            else if (text[i] == '\r')
            {
                int end = i + 1 < text.Length && text[i + 1] == '\n' ? i + 2 : i + 1;
                yield return text[start..end];
                start = end; i = end - 1;
            }
        }
        if (start < text.Length) yield return text[start..];
    }

    /// <summary>Rows of a CSV file as dictionaries keyed by the header. Blank rows are skipped; short rows get empty strings; a missing required column is an error.</summary>
    public static List<Dictionary<string, string>> ReadRows(string path, params string[] required)
    {
        string text;
        try
        {
            var bytes = File.ReadAllBytes(path);
            text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        }
        catch (FileNotFoundException e) { throw new DatasetException($"required file not found: {path}", e); }
        catch (DirectoryNotFoundException e) { throw new DatasetException($"required file not found: {path}", e); }
        catch (DecoderFallbackException e) { throw new DatasetException($"{Path.GetFileName(path)} is not valid UTF-8: {e.Message}", e); }

        List<List<string>> table;
        try { table = Parse(text); }
        catch (DataException e) { throw new DatasetException($"{Path.GetFileName(path)} is not readable as CSV: {e.Message}", e); }

        var header = table.Count > 0 ? table[0] : new List<string>();
        var missing = required.Where(c => !header.Contains(c, StringComparer.Ordinal)).ToList();
        if (missing.Count > 0)
            throw new DatasetException($"{Path.GetFileName(path)}: missing column(s) [{string.Join(", ", missing.Select(m => Compat.StrRepr(m)))}]");

        var rows = new List<Dictionary<string, string>>();
        foreach (var raw in table.Skip(1))
        {
            if (raw.Count == 0) continue;
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < header.Count; i++) row[header[i]] = i < raw.Count ? raw[i] : "";
            if (row.Values.Any(v => v.Trim().Length > 0)) rows.Add(row);
        }
        return rows;
    }

    /// <summary>One CSV record, quoting a field only when it holds a comma, a quote or a line break; lines end with LF.</summary>
    public static string FormatRow(IEnumerable<string> fields) =>
        string.Join(",", fields.Select(f => f.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + f.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : f)) + "\n";

    /// <summary>Row accessor with the semantics of dict.get(key, ""): an absent column reads as an empty string.</summary>
    public static string G(this Dictionary<string, string> row, string key) => row.TryGetValue(key, out var v) ? v : "";
}
