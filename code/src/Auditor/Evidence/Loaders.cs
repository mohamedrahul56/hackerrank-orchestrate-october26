using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Auditor.Evidence;

/// <summary>Format loaders: SQL snapshots, JSON bundles/exports/ledgers, text handovers -> Obs (logs are in <see cref="LogObs"/>).</summary>
public static class Loaders
{
    // ----------------------------------------------------------------------------- SQL
    private const int SqliteOk = 0, SqliteDeny = 1;
    private const int ActionCreateIndex = 1, ActionCreateTable = 2, ActionPragma = 19, ActionRead = 20, ActionSelect = 21, ActionTransaction = 22, ActionInsert = 18,
                      ActionUpdate = 23, ActionReindex = 27, ActionFunction = 31;
    private static readonly HashSet<int> AllowedActions = new() { ActionCreateTable, ActionCreateIndex, ActionInsert, ActionTransaction, ActionSelect, ActionRead, ActionFunction, ActionUpdate, ActionReindex };

    /// <summary>Evidence .sql files are untrusted text: allow only schema creation, INSERT and reads. No ATTACH, no PRAGMA writes.</summary>
    private static int Authorize(object user, int action, string arg1, string arg2, string db, string source)
    {
        if (AllowedActions.Contains(action)) return SqliteOk;
        if (action == ActionPragma && arg1 == "foreign_keys") return SqliteOk;
        return SqliteDeny;
    }

    private static readonly strdelegate_authorizer AuthorizerDelegate = Authorize;      // kept alive for the whole run: SQLite holds on to the callback

    /// <summary>Execute an evidence SQL snapshot in an in-memory SQLite database and return every table as a list of rows.</summary>
    public static List<(string Name, List<Dictionary<string, object?>> Rows)> ReadSqlTables(string path)
    {
        var script = Py.ReadText(path);
        using var con = new SqliteConnection("Data Source=:memory:");
        con.Open();
        raw.sqlite3_set_authorizer(con.Handle, AuthorizerDelegate, null);
        try
        {
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = script;
                cmd.ExecuteNonQuery();
            }
        }
        catch (SqliteException e) { throw new DataException($"OperationalError: {e.Message}"); }
        raw.sqlite3_set_authorizer(con.Handle, (strdelegate_authorizer?)null, null);
        var names = new List<string>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) names.Add(rd.GetString(0));
        }
        var tables = new List<(string, List<Dictionary<string, object?>>)>();
        foreach (var name in names)
        {
            var rows = new List<Dictionary<string, object?>>();
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{name}\"";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (int i = 0; i < rd.FieldCount; i++) row[rd.GetName(i)] = rd.IsDBNull(i) ? null : Normalise(rd.GetValue(i));
                rows.Add(row);
            }
            tables.Add((name, rows));
        }
        return tables;
    }

    private static object? Normalise(object value) => value switch
    {
        int i => (long)i,
        byte[] b => "b'" + string.Concat(b.Select(x => x is >= 32 and < 127 && x != '\'' && x != '\\' ? ((char)x).ToString() : $"\\x{x:x2}")) + "'",
        _ => value,
    };

    private static void RecordObs(EvidenceBase b, string fileId, DateTime fileT, DateTime t, object? collection, object? recObj, string source, string? system = null)
    {
        try
        {
            var rec = Py.AsDict(recObj);
            var rid = Py.Get(rec, "resource_id");
            if (collection is not string coll || !Records.CollectionRules.TryGetValue(coll, out var rule) || !Py.Truthy(rid)) return;
            if (rid is not string rids) throw new DataException($"resource_id must be text, got {Py.TypeName(rid)}");
            DateTime? until = null;
            if (Py.Truthy(Py.Get(rec, "retained_until")))
            {
                var rawUntil = Py.Str(rec["retained_until"]);
                try { until = Py.ParseTs(rawUntil); }
                catch (DataException)                                   // an unreadable end date is "no end date stated" (the hold stays active), not a lost record
                {
                    b.Warnings.Add($"{fileId}: {rids}: retained_until {Py.StrRepr(rawUntil.Length > 30 ? rawUntil[..30] : rawUntil)} is not a time; treated as no end date");
                }
            }
            b.Obs.Add(new Obs(fileId, fileT, t, !string.IsNullOrEmpty(system) ? system : Records.SystemFor(rids), rids, rule.Category, rule.State(rec),
                              Records.IdentsFrom(Records.Flatten(rec)), source, Until: until));
        }
        catch (DataException e)                                          // one malformed record must not cost the rest of the file
        {
            b.Warnings.Add($"{fileId}: a record in {Py.Str(collection)} skipped (DataError: {e.Message})");
        }
    }

    public static void LoadSql(EvidenceBase b, string fileId, string path, DateTime fileT)
    {
        var tables = ReadSqlTables(path);
        foreach (var (name, rows) in tables)
        {
            if (name == "snapshot_scope")                                   // inventory: which systems this snapshot covers
            {
                foreach (var r in rows)
                {
                    try { b.Coverage.Add(new Coverage(fileId, fileT, Py.ParseTsValue(Py.Req(r, "captured_at")), Py.Str(Py.Req(r, "origin_system")), "sql:snapshot_scope")); }
                    catch (DataException e) { b.Warnings.Add($"{fileId}: a snapshot_scope row skipped (DataError: {e.Message})"); }
                }
                continue;
            }
            foreach (var r in rows)
            {
                try
                {
                    var updated = Py.Get(r, "updated_at");
                    var t = Py.Truthy(updated) ? Min(Py.ParseTsValue(updated), fileT) : fileT;
                    if (r.ContainsKey("record_json"))                      // replica/primary tables: JSON payload + collection name
                    {
                        var rec = Py.ParseJson(Py.Req(r, "record_json") as string ?? throw new DataException("record_json must be text"));
                        var origin = Py.Get(r, "origin_system");
                        RecordObs(b, fileId, fileT, t, Py.Req(r, "collection_name"), rec, $"sql:{name}", Py.Truthy(origin) ? Py.Str(origin) : null);
                    }
                    else RecordObs(b, fileId, fileT, t, name, NormaliseTypedRow(name, r), $"sql:{name}");
                }
                catch (DataException e) { b.Warnings.Add($"{fileId}: a row of {name} skipped (DataError: {e.Message})"); }
            }
        }
        // full-table snapshot of the account system for the hand-made failover file
        if (tables.Any(t => t.Name.StartsWith("customer_profiles_", StringComparison.Ordinal)))
            b.Coverage.Add(new Coverage(fileId, fileT, fileT, "account_service", "sql:customer_profiles_*"));
    }

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;

    /// <summary>Typed inventory rows use different column names than the JSON collections; map the few that matter.</summary>
    private static Dictionary<string, object?> NormaliseTypedRow(string table, Dictionary<string, object?> row)
    {
        var r = new Dictionary<string, object?>(row, StringComparer.Ordinal);
        if (table is "customer_profiles" or "customer_profiles_primary" or "customer_profiles_failover_replica")
            r.TryAdd("customer_reference", Py.Get(r, "user_id"));
        if (table == "support_records") r["message_text"] = r.TryGetValue("message_excerpt", out var ex) ? ex : "";
        if (table == "customer_exports") r["download_available"] = true;
        return r;
    }

    // ----------------------------------------------------------------------------- JSON
    /// <summary>service_resource_export_*: per-service lists of point-in-time lookups, each with its own observed_at.</summary>
    private static void WalkServiceExport(EvidenceBase b, string fileId, DateTime fileT, Dictionary<string, object?> doc)
    {
        foreach (var (svc, lists) in Py.AsDict(doc["service_exports"]))
            foreach (var (listName, items) in Py.AsDict(lists))
                foreach (var it in Py.Iterate(items))
                {
                    try { WalkItem(b, fileId, fileT, svc, listName, it); }
                    catch (DataException e) { b.Warnings.Add($"{fileId}: an item of {svc}.{listName} skipped (DataError: {e.Message})"); }
                }
    }

    private static void WalkItem(EvidenceBase b, string fileId, DateTime fileT, string svc, string listName, object? it)
    {
        var rid = Py.Get(it, "resource_id");
        if (!Py.Truthy(rid)) return;
        if (rid is not string ridText) throw new DataException($"resource_id must be text, got {Py.TypeName(rid)}");
        var observed = Py.Get(it, "observed_at");
        var t = Py.Truthy(observed) ? Min(Py.ParseTsValue(observed), fileT) : fileT;
        var src = $"json:{svc}.{listName}";
        switch (listName)
        {
            case "profile_lookups":
                var prof = Py.Get(it, "profile");
                bool present = Py.EqualsNumber(Py.Get(it, "status_code"), 200) && prof is not null;
                b.Obs.Add(new Obs(fileId, fileT, t, svc, ridText, "USER_PROFILE", present ? "PRESENT" : "ABSENT", Records.IdentsFrom(Records.Flatten(Py.Truthy(prof) ? prof : new Dictionary<string, object?>())), src));
                break;
            case "index_queries":
                var hits = Py.Or(Py.Get(it, "hits"), new Dictionary<string, object?>());
                var docs = Py.Or(Py.Get(hits, "documents"), new List<object?>());
                var total = Py.Has(hits, "total") ? Py.Get(hits, "total") : (long)Py.Len(docs);
                bool found = Py.GreaterThanZero(total);
                var idents = new HashSet<string>(Records.IdentsFrom(Records.Flatten(docs)), StringComparer.Ordinal);
                idents.UnionWith(Records.IdentsFrom(new[] { ("query", Py.Str(Py.Or(Py.Get(it, "query"), ""))) }));
                b.Obs.Add(new Obs(fileId, fileT, t, svc, ridText, "SEARCH_INDEX_ENTRY", found ? "PRESENT" : "ABSENT", idents, src));
                break;
            case "profiles": RecordObs(b, fileId, fileT, t, "customer_analytics_reports", it, src, svc); break;
            case "tickets": RecordObs(b, fileId, fileT, t, "support_conversations", it, src, svc); break;
            case "exports": b.Obs.Add(new Obs(fileId, fileT, t, svc, ridText, "USER_EXPORT", "PRESENT", Records.IdentsFrom(Records.Flatten(it)), src)); break;
            case "connectors": RecordObs(b, fileId, fileT, t, "connector_configurations", it, src, svc); break;
            case "restore_entries": RecordObs(b, fileId, fileT, t, "backup_catalog", it, src, svc); break;
            case "audit_records": RecordObs(b, fileId, fileT, t, "security_audit_events", it, src, svc); break;
        }
    }

    private static void LookupObs(EvidenceBase b, string fileId, DateTime fileT, DateTime t, string system, string rid, string recordType, string result, object? who, string source)
    {
        if (!Records.LookupRules.TryGetValue((recordType, result), out var state))
        {
            b.Warnings.Add($"{fileId}: unknown lookup ({recordType}, {result}) for {rid}");
            return;
        }
        if (who is not string subject) throw new DataException($"subject must be text, got {Py.TypeName(who)}");
        b.Obs.Add(new Obs(fileId, fileT, t, system, rid, recordType, state, Records.IdentsFrom(new[] { ("subject", subject) }), source));
    }

    private static string Text(object? v, string what) => v as string ?? throw new DataException($"{what} must be text, got {Py.TypeName(v)}");

    public static void LoadJson(EvidenceBase b, string fileId, string path, DateTime fileT)
    {
        var parsed = Py.ParseJson(Py.ReadText(path));
        if (parsed is not Dictionary<string, object?> doc)
        {
            b.Warnings.Add($"{fileId}: {Path.GetFileName(path)} is not a JSON object");
            return;
        }

        void Guarded(string label, Action fn)
        {
            try { fn(); }
            catch (DataException e) { b.Warnings.Add($"{fileId}: {label} skipped (DataError: {e.Message})"); }          // one malformed snapshot / record never costs the rest of the file
        }

        if (doc.ContainsKey("service_exports")) { WalkServiceExport(b, fileId, fileT, doc); return; }
        if (doc.ContainsKey("snapshots"))                                     // bundle: several dated snapshots in one file
        {
            foreach (var snap in Py.Iterate(doc["snapshots"])) Guarded("a snapshot", () => LoadSnapshot(b, fileId, fileT, snap));
            return;
        }
        if (doc.ContainsKey("records") && doc.ContainsKey("collection"))      // hand-made single-collection files
        {
            var t = Py.ParseTsValue(Py.Req(doc, "captured_at"));
            foreach (var rec in Py.Iterate(doc["records"]))
                RecordObs(b, fileId, fileT, t, doc["collection"], rec, $"json:{Py.Str(doc.TryGetValue("source", out var so) ? so : "")}.{Py.Str(doc["collection"])}");
            return;
        }
        if (doc.ContainsKey("records"))                                       // ledgers / manifests / connector configs
        {
            var t = Py.ParseTsValue(Py.Req(doc, "exported_at"));
            foreach (var rec in Py.Iterate(doc["records"])) Guarded("a ledger record", () => LoadLedgerRecord(b, fileId, fileT, t, doc, rec, path));
            return;
        }
        b.Warnings.Add($"{fileId}: unrecognised JSON layout in {Path.GetFileName(path)}");
    }

    private static void LoadSnapshot(EvidenceBase b, string fileId, DateTime fileT, object? snap)
    {
        var t = Py.ParseTsValue(Py.Req(snap, "captured_at"));
        var coll = Py.Req(snap, "collection");
        var store = Py.Req(snap, "data_store");
        if (coll is "operational_record_history")                             // lookup vocabulary
        {
            foreach (var r in Py.Iterate(Py.Req(snap, "records")))
            {
                try
                {
                    LookupObs(b, fileId, fileT, Py.ParseTsValue(Py.Req(r, "recorded_at")), Text(Py.Req(r, "system"), "system"), Text(Py.Req(r, "resource_id"), "resource_id"),
                              Text(Py.Req(r, "record_type"), "record_type"), Text(Py.Req(r, "result"), "result"), Py.Has(r, "lookup_value") ? Py.Get(r, "lookup_value") : "", $"json:{Py.Str(store)}.{Py.Str(coll)}");
                }
                catch (DataException e) { b.Warnings.Add($"{fileId}: a lookup record skipped (DataError: {e.Message})"); }
            }
            return;
        }
        b.Coverage.Add(new Coverage(fileId, fileT, t, Py.Str(store), $"json:{Py.Str(store)}.{Py.Str(coll)}"));
        foreach (var rec in Py.Iterate(Py.Req(snap, "records")))
            RecordObs(b, fileId, fileT, t, coll, rec, $"json:{Py.Str(store)}.{Py.Str(coll)}", Py.Str(store));
    }

    private static void LoadLedgerRecord(EvidenceBase b, string fileId, DateTime fileT, DateTime t, Dictionary<string, object?> doc, object? recObj, string path)
    {
        var rec = Py.AsDict(recObj);
        if (rec.ContainsKey("record_type"))                                   // security_audit_ledger: lookup vocabulary
            LookupObs(b, fileId, fileT, Py.ParseTsValue(rec.TryGetValue("recorded_at", out var ra) ? ra : doc["exported_at"]), Text(Py.Req(rec, "system"), "system"),
                      Text(Py.Req(rec, "resource_id"), "resource_id"), Text(rec["record_type"], "record_type"), Text(Py.Req(rec, "result"), "result"),
                      rec.TryGetValue("subject_reference", out var sr) ? sr : "", "json:ledger");
        else if (rec.ContainsKey("download_available") || rec.ContainsKey("file_name") || rec.ContainsKey("owner_email"))
            RecordObs(b, fileId, fileT, t, "customer_export_jobs", rec, "json:export_manifest");
        else if (rec.ContainsKey("connector_name"))
            RecordObs(b, fileId, fileT, t, "connector_configurations", rec, "json:connector_configurations");
        else
            b.Warnings.Add($"{fileId}: unrecognised record in {Path.GetFileName(path)}: [{string.Join(", ", rec.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(5).Select(k => Py.StrRepr(k)))}]");
    }

    // ----------------------------------------------------------------------------- text handover
    private static readonly Regex Handover = new(@"^-\s*(\S+)\s*\|\s*(\w+)\s*\|\s*(\S+)\s*\|\s*(\w+)\s*\|\s*(\w+)\s*\|\s*(.*?)\s*$", RegexOptions.CultureInvariant);

    public static void LoadText(EvidenceBase b, string fileId, string path, DateTime fileT)
    {
        foreach (var line in Py.SplitLines(Py.ReadText(path)))
        {
            var m = Handover.Match(line);
            if (!m.Success) continue;
            try { LookupObs(b, fileId, fileT, Py.ParseTs(m.Groups[1].Value), m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, m.Groups[5].Value, m.Groups[6].Value, "txt:handover"); }
            catch (DataException e) { b.Warnings.Add($"{fileId}: a handover line skipped ({e.Message})"); }
        }
    }
}
