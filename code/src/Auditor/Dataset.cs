namespace Auditor;

public sealed record Request(string CaseId, string Email, DateTime RequestedAt, string ReportedStatus, DateTime ReportedAt, DateTime AuditAt, string RequestText);

public sealed record BadRow(string CaseId, int Line, string Reason);

/// <summary>Typed loaders for the participant-facing CSVs. Only files under dataset/ are read. Nothing here knows about labels.</summary>
public sealed class Dataset
{
    public Dataset(string root) => Root = root;

    public string Root { get; }
    public List<Request> Requests { get; set; } = new();
    /// <summary>file_id -> captured_at</summary>
    public Dictionary<string, DateTime> EvidenceTime { get; } = new(StringComparer.Ordinal);
    /// <summary>activity_id -> event_time</summary>
    public Dictionary<string, DateTime> ActivityTime { get; } = new(StringComparer.Ordinal);
    /// <summary>audit_id -> event_time</summary>
    public Dictionary<string, DateTime> AuditTime { get; } = new(StringComparer.Ordinal);
    /// <summary>every answerable case_id in file order (malformed rows included)</summary>
    public List<string> CaseOrder { get; set; } = new();
    public List<BadRow> BadRows { get; set; } = new();

    /// <summary>Timestamp of a citable id (file_id / activity_id / audit_id) or null if unknown.</summary>
    public DateTime? CitableTime(string evidenceId)
    {
        foreach (var table in new[] { EvidenceTime, ActivityTime, AuditTime })
            if (table.TryGetValue(evidenceId, out var t)) return t;
        return null;
    }

    public static readonly string[] RequestColumns = { "case_id", "email", "requested_at", "reported_status", "reported_at", "audit_at", "request_text" };

    /// <summary>
    /// (good requests, malformed rows, case_ids in file order without duplicates).
    /// A row with an unparseable timestamp or an empty email cannot be audited but still needs its answer row, so it is reported as a BadRow and the caller writes a conservative row for it.
    /// A row without case_id cannot be answered at all (skipped, reported). A repeated case_id keeps the first row.
    /// </summary>
    public static (List<Request> Good, List<BadRow> Bad, List<string> Order) LoadRequestsLenient(string path)
    {
        var good = new List<Request>();
        var bad = new List<BadRow>();
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int line = 1;
        foreach (var r in Csv.ReadRows(path, RequestColumns))
        {
            line++;
            var cid = r.G("case_id").Trim();
            if (cid.Length == 0) { bad.Add(new BadRow("", line, "empty case_id")); continue; }
            if (!seen.Add(cid)) { bad.Add(new BadRow(cid, line, "duplicate case_id (first row kept)")); continue; }
            order.Add(cid);
            var requested = Py.TryTs(r.G("requested_at"));
            var reported = Py.TryTs(r.G("reported_at"));
            var audit = Py.TryTs(r.G("audit_at"));
            if (requested is null || reported is null || audit is null)
            {
                var broken = new[] { ("requested_at", requested), ("reported_at", reported), ("audit_at", audit) }.Where(x => x.Item2 is null).Select(x => x.Item1);
                bad.Add(new BadRow(cid, line, $"unparseable {string.Join(", ", broken)}"));
            }
            else if (r.G("email").Trim().Length == 0) bad.Add(new BadRow(cid, line, "empty email"));
            else good.Add(new Request(cid, r["email"], requested.Value, r.G("reported_status"), reported.Value, audit.Value, r.G("request_text")));
        }
        return (good, bad, order);
    }

    /// <summary>Strict variant for tools that expect a clean file: raises on the first malformed row.</summary>
    public static List<Request> LoadRequests(string path)
    {
        var (good, bad, _) = LoadRequestsLenient(path);
        var broken = bad.Where(b => !b.Reason.StartsWith("duplicate", StringComparison.Ordinal)).ToList();
        if (broken.Count > 0)
            throw new DatasetException($"{Path.GetFileName(path)}: malformed row(s): " + string.Join("; ", broken.Take(5).Select(b => $"line {b.Line} {(b.CaseId.Length > 0 ? b.CaseId : "?")}: {b.Reason}")));
        return good;
    }

    public static Dataset Load(string root, string requestsFile = "deletion_requests.csv")
    {
        var ds = new Dataset(root);
        var (good, bad, order) = LoadRequestsLenient(Path.Combine(root, requestsFile));
        ds.Requests = good; ds.BadRows = bad; ds.CaseOrder = order;
        foreach (var (table, file, idCol, timeCol) in new[]
        {
            (ds.EvidenceTime, "evidence_files.csv", "file_id", "captured_at"),
            (ds.ActivityTime, "system_activity.csv", "activity_id", "event_time"),
            (ds.AuditTime, "user_audit_log.csv", "audit_id", "event_time"),
        })
        {
            try
            {
                foreach (var r in Csv.ReadRows(Path.Combine(root, file)))
                {
                    var t = Py.TryTs(r.G(timeCol));
                    if (r.G(idCol).Length > 0 && t is not null) table[r[idCol]] = t.Value;
                }
            }
            catch (DatasetException) { /* a missing optional table just means nothing can be cited from it */ }
        }
        return ds;
    }
}
