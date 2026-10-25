using System.Security.Cryptography;
using System.Text;
using Auditor.Evidence;

namespace Auditor;

/// <summary>An e-mail address valid between two moments (null = unbounded).</summary>
public sealed record EmailSpan(string Email, DateTime? From, DateTime? Until);

public sealed class Person
{
    public Person(string userId, string name, string currentEmail) { UserId = userId; Name = name; CurrentEmail = currentEmail; }

    public string UserId { get; }
    public string Name { get; }
    public string CurrentEmail { get; }
    public List<EmailSpan> EmailSpans { get; set; } = new();
    public HashSet<string> Hashes { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Phones { get; } = new(StringComparer.Ordinal);
    public DateTime? UpdatedAt { get; set; }

    public HashSet<string> Emails => EmailSpans.Select(s => s.Email).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every identifier string that may identify this person inside evidence (emails, user_id, hashes, phones).</summary>
    public HashSet<string> Tokens()
    {
        var t = Emails;
        t.UnionWith(Hashes); t.UnionWith(Phones); t.Add(UserId);
        return t;
    }
}

/// <summary>
/// Identity resolver: request email -> user(s) and every identifier that user has ever had.
/// Facts measured on the data (NOTES.md): email_hash = sha256(lower(strip(email))) for all current rows and all audit-log pairs; every request email resolves to exactly one user;
/// the audit log only changes email/email_hash/phone. Still handled generally: case/whitespace differences, an email reassigned to another user later (resolved by who owned it
/// at <c>requested_at</c>), and emails that match nobody or several users.
/// </summary>
public sealed class IdentityResolver
{
    private readonly Dictionary<string, List<Person>> byEmail = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> tokenUsers = new(StringComparer.Ordinal);
    private static readonly HashSet<string> NoUsers = new(StringComparer.Ordinal);

    public IdentityResolver(string datasetRoot)
    {
        Load(datasetRoot);
    }

    public Dictionary<string, Person> People { get; } = new(StringComparer.Ordinal);
    public List<string> Warnings { get; } = new();

    public static string NormEmail(string e) => e.Trim().ToLowerInvariant();

    public static string EmailHash(string e) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormEmail(e)))).ToLowerInvariant();

    private void Load(string root)
    {
        foreach (var r in Csv.ReadRows(Path.Combine(root, "user_identities.csv"), "user_id", "email"))
        {
            if (r["user_id"].Trim().Length == 0 || r["email"].Trim().Length == 0)
            {
                Warnings.Add($"identity row skipped (empty user_id or email): {Py.StrRepr(r.G("user_id"))}");
                continue;
            }
            var p = new Person(r["user_id"].Trim(), r.G("name"), NormEmail(r["email"])) { UpdatedAt = Py.TryTs(r.G("updated_at")) };
            p.Hashes.Add(EmailHash(r["email"]));
            if (r.G("email_hash").Trim().Length > 0) p.Hashes.Add(r["email_hash"].Trim().ToLowerInvariant());
            var phone = Records.NormPhone(r.G("phone"));
            if (phone.Length > 0) p.Phones.Add(phone);
            People[p.UserId] = p;
        }
        var changes = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.Ordinal);
        List<Dictionary<string, string>> logRows;
        try { logRows = Csv.ReadRows(Path.Combine(root, "user_audit_log.csv"), "user_id", "event_time", "field_name"); }
        catch (DatasetException e)                                                 // history is optional: without it only current identifiers resolve
        {
            Warnings.Add($"user_audit_log.csv not usable ({e.Message}); only current identifiers will resolve");
            logRows = new();
        }
        foreach (var r in logRows)
        {
            if (Py.TryTs(r["event_time"]) is null)
            {
                Warnings.Add($"audit-log row {(r.TryGetValue("audit_id", out var auditId) ? auditId : "?")} skipped (bad event_time)");
                continue;
            }
            if (!changes.TryGetValue(r["user_id"], out var list)) changes[r["user_id"]] = list = new();
            list.Add(r);
        }
        foreach (var (uid, rows0) in changes)
        {
            if (!People.TryGetValue(uid, out var person)) continue;
            var rows = rows0.OrderBy(r => r["event_time"], StringComparer.Ordinal).ToList();               // stable, by the raw time text
            var emailRows = rows.Where(r => r["field_name"] == "email").ToList();
            foreach (var r in rows)
            {
                if (r["field_name"] == "email_hash") { person.Hashes.Add(r.G("old_value").ToLowerInvariant()); person.Hashes.Add(r.G("new_value").ToLowerInvariant()); }
                else if (r["field_name"] == "phone")
                    foreach (var x in new[] { Records.NormPhone(r.G("old_value")), Records.NormPhone(r.G("new_value")) }.Where(x => x.Length > 0)) person.Phones.Add(x);
            }
            DateTime? prevUntil = null;
            var spans = new List<EmailSpan>();
            foreach (var r in emailRows)                                              // old email valid until the change time
            {
                var when = Py.ParseTs(r["event_time"]);
                spans.Add(new EmailSpan(NormEmail(r.G("old_value")), prevUntil, when));
                person.Hashes.Add(EmailHash(r.G("old_value"))); person.Hashes.Add(EmailHash(r.G("new_value")));
                prevUntil = when;
            }
            spans.Add(new EmailSpan(person.CurrentEmail, prevUntil, null));
            person.EmailSpans = spans;
        }
        foreach (var p in People.Values)
        {
            if (p.EmailSpans.Count == 0)
                p.EmailSpans = new List<EmailSpan> { new(p.CurrentEmail, p.UpdatedAt, null) };           // the snapshot's updated_at is the earliest time we can vouch for it
            foreach (var address in p.Emails)
            {
                if (!byEmail.TryGetValue(address, out var list)) byEmail[address] = list = new();
                list.Add(p);
            }
            foreach (var t in p.Tokens())
            {
                if (!tokenUsers.TryGetValue(t, out var set)) tokenUsers[t] = set = new(StringComparer.Ordinal);
                set.Add(p.UserId);
            }
        }
    }

    /// <summary>People who own <paramref name="email"/> (current or historical). If several, those who owned it at <paramref name="at"/> win.</summary>
    public List<Person> Resolve(string email, DateTime? at = null)
    {
        var norm = NormEmail(email);
        if (!byEmail.TryGetValue(norm, out var cands)) return new();
        if (cands.Count <= 1 || at is null) return new(cands);
        var owners = cands.Where(p => p.EmailSpans.Any(s => s.Email == norm && (s.From is null || s.From <= at) && (s.Until is null || at < s.Until))).ToList();
        return owners.Count > 0 ? owners : new(cands);
    }

    public IReadOnlySet<string> UsersForToken(string token) => tokenUsers.TryGetValue(token, out var s) ? s : NoUsers;
}
