using System.Text;
using Auditor.Evidence;

namespace Auditor.Agent;

/// <summary>The submission cannot be used at all (G2); the reader falls back to 'not read'.</summary>
public sealed class GateException : Exception
{
    public GateException(string message) : base(message) { }
}

/// <param name="Reading">conforms to the reading schema: what the unchanged observation builder expects</param>
/// <param name="Rejections">one line per dropped record or value, for the run notes</param>
public sealed record GateResult(Dictionary<string, object?> Reading, List<string> Rejections);

/// <summary>
/// The gate: deterministic code between what the model says about a screenshot and anything the audit uses.
/// The model describes; this class and the rest of the pipeline decide. A model reading becomes observations only through <see cref="GateReading"/>, and the observations then meet the same
/// rules as every other evidence. The rules, with where each one is enforced:
///   G1  schema        the submission must satisfy the submit_reading schema (<see cref="AgentTools.SubmitSchema"/>) - here.
///   G2  evidence id   the model never names an evidence id; code binds every observation to the manifest's file_id of the screenshot it read, and refuses a screenshot that is not in the
///                     manifest - here (<see cref="GateException"/>) and in <see cref="AgentImageReader"/>.
///   G3  id shape      a resource id must be &lt;name&gt;_&lt;number&gt;; a record with another id (a path, a sentence) is dropped; a record with no id needs at least one alias - here.
///   G4  vocabulary    a record whose id family has no state in the closed vocabulary (<see cref="ImageObs.StateFromDescription"/>) is dropped - here.
///   G5  identifiers   only syntactically valid e-mail addresses, user ids and phone numbers are kept - here.
///   G6  unknown       a field the model marked unknown is emptied; a record whose id was marked unknown is dropped unless it has an alias - here.
///   G7  free text     `screen` and any prose are never read by any decision (the screen title is truncated) - here and in <see cref="ImageObs.Sanitize"/>.
///   G8  window        an observation gives STATE only if its file was captured inside [requested_at, audit_at]; later it can only link, never be cited - <see cref="StateResolver.Usable"/> (unchanged).
///   G9  identifier    an observation attaches to a case only through an exact identifier of the resolved user or a data-catalog link - <see cref="Linker"/> (unchanged).
///   G10 no erasing    a model-sourced observation (source image:agent) can add a finding or confirm a state but cannot lower the severity of a state that deterministic evidence supports
///                     in the window - <see cref="StateResolver.Resolve"/>; it is entered only when a model-sourced observation exists, so every other run is untouched.
///   G11 evidence      every step cites an id that exists and is not later than audit_at; a COMPLETE row cites evidence for every step - <see cref="Validator"/> (unchanged).
/// (There is no G12: the model is not used on request text.)
/// </summary>
public static class Gate
{
    public const int ScreenMax = 80;
    private static readonly string[] ListFields = { "emails", "user_ids", "phones", "status_words", "aliases" };

    private static void Clear(Dictionary<string, object?> rec, string name) =>
        rec[name] = ListFields.Contains(name) ? new List<object?>() : name is "shows_direct_identifiers" or "selected" ? false : "";

    private static List<string> Strings(object? v) => ((List<object?>)v!).Cast<string>().ToList();

    private static string Truncate(string s, int runes)
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (var r in s.EnumerateRunes())
        {
            if (n++ >= runes) break;
            sb.Append(r.ToString());
        }
        return sb.ToString();
    }

    /// <summary>Apply G1-G7 to one <c>submit_reading</c> input. Throws <see cref="SchemaException"/> (G1) or <see cref="GateException"/> (G2); everything else is dropped with a rejection line.</summary>
    public static GateResult GateReading(Dictionary<string, object?> submission, string fileId, IReadOnlySet<string> knownFileIds)
    {
        JsonSchemaCheck.Validate(submission, AgentTools.SubmitSchema);                                            // G1
        if (!knownFileIds.Contains(fileId)) throw new GateException($"{Py.StrRepr(fileId)} is not an evidence file of the manifest");      // G2
        var rej = new List<string>();
        var records = ((List<object?>)Py.ParseJson(Py.DumpsCompact(submission["records"]))!).Cast<Dictionary<string, object?>>().ToList();      // a deep copy
        if (submission.TryGetValue("unknown", out var unknown) && unknown is List<object?> unknownList)           // G6
            foreach (var u in unknownList.Cast<Dictionary<string, object?>>())
            {
                long i = (long)u["record_index"]!;
                if (0 <= i && i < records.Count) Clear(records[(int)i], (string)u["field"]!);
                else rej.Add($"G6 unknown refers to record {i}, which does not exist");
            }
        var output = new List<object?>();
        for (int i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            var rid = ((string)rec["resource_id"]!).Trim();
            if (rid.Length > 0 && !Schema.ValidResourceId(rid))                                                    // G3
            {
                rej.Add($"G3 record {i}: resource id {Py.StrRepr(Truncate(rid, 30))} is not <name>_<number>: dropped");
                continue;
            }
            if (rid.Length == 0 && !Strings(rec["aliases"]).Any(a => a.Trim().Length > 0))
            {
                rej.Add($"G3 record {i}: no resource id and no alias: dropped");
                continue;
            }
            var emails = Strings(rec["emails"]); var users = Strings(rec["user_ids"]); var phones = Strings(rec["phones"]);
            if (rid.Length > 0 && ImageObs.StateFromDescription(Records.Prefix(rid), new ReadingRecord(rid, false, emails, users, phones, Strings(rec["status_words"]), (bool)rec["shows_direct_identifiers"]!, Strings(rec["aliases"]))) is null)       // G4
            {
                rej.Add($"G4 record {i}: {rid} has no state in the allowed vocabulary: dropped");
                continue;
            }
            var goodEmails = emails.Where(e => ImageObs.FullMatch(Records.EmailRe, e.Trim())).ToList();             // G5
            var goodUsers = users.Where(u => ImageObs.FullMatch(Records.UserRe, u.Trim())).ToList();
            var goodPhones = phones.Where(p => Records.NormPhone(p).Length > 0).ToList();
            foreach (var (kind, before, after) in new[] { ("e-mail", emails.Count, goodEmails.Count), ("user id", users.Count, goodUsers.Count), ("phone", phones.Count, goodPhones.Count) })
                if (after != before) rej.Add($"G5 record {i}: {before - after} {kind} value(s) not syntactically valid: removed");
            var cleaned = new Dictionary<string, object?>(rec, StringComparer.Ordinal)
            {
                ["resource_id"] = rid, ["emails"] = goodEmails.Cast<object?>().ToList(), ["user_ids"] = goodUsers.Cast<object?>().ToList(), ["phones"] = goodPhones.Cast<object?>().ToList(),
            };
            output.Add(cleaned);
        }
        var screen = submission.TryGetValue("screen", out var sv) ? Py.Str(sv) : "";
        var reading = new Dictionary<string, object?> { ["screen"] = Truncate(screen, ScreenMax), ["records"] = output };   // G7: unknown[] and prose do not travel further
        JsonSchemaCheck.Validate(reading, ImageObs.Schema);
        return new GateResult(reading, rej);
    }
}
