namespace Auditor.Evidence;

/// <summary>Log events -> Obs. The meaning of each event name lives in <see cref="EventRules"/> (one table, no scattered if/else).</summary>
public static class LogObs
{
    private delegate string? StateRule(Dictionary<string, string> fields);

    private static string Get(Dictionary<string, string> f, string key, string? fallback = null) => f.TryGetValue(key, out var v) ? v : fallback ?? "None";

    private static bool Truthy(string? v) => (v ?? "None").ToLowerInvariant() is "true" or "1" or "yes";

    private static Dictionary<string, object?> Rec(Dictionary<string, string> f) => f.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);

    /// <summary>A connector that is enabled AND registered with the scheduler will still run (a recreation path).</summary>
    private static string FlowHeartbeat(Dictionary<string, string> f) => Truthy(Get(f, "enabled")) && Truthy(Get(f, "scheduler_registered", "true")) ? "ACTIVE" : "DISABLED";

    private static string FlowLoaded(Dictionary<string, string> f) => Truthy(Get(f, "enabled")) || Truthy(Get(f, "scheduler_registered")) ? "ACTIVE" : "DISABLED";

    private static string RestoreCandidate(Dictionary<string, string> f) => Truthy(Get(f, "eligible")) && !Truthy(Get(f, "deletion_block")) ? "RESTORABLE" : "BLOCKED";

    private static string AnalyticsRead(Dictionary<string, string> f) => Get(f, "direct_identifiers", "0") is not ("0" or "") ? "EXPOSED" : "ANONYMIZED";

    private static readonly Dictionary<string, (string Category, StateRule Rule)> EventRules = new(StringComparer.Ordinal)
    {
        ["profile_lookup_completed"] = ("USER_PROFILE", f => Get(f, "rows", "0") != "0" && Get(f, "http_status", "200") == "200" ? "PRESENT" : "ABSENT"),
        ["index_query_completed"] = ("SEARCH_INDEX_ENTRY", f => Get(f, "result", "hit") == "hit" && Get(f, "total_hits", "1") != "0" ? "PRESENT" : "ABSENT"),
        ["support_ticket_read_completed"] = ("SUPPORT_MESSAGE", f => Records.SupportMessageState(Rec(f))),
        ["message_pii_scan"] = ("SUPPORT_MESSAGE", _ => "EXPOSED"),
        ["message_read"] = ("SUPPORT_MESSAGE", f => Records.SupportMessageState(Rec(f))),
        ["attachment_pii_scan"] = ("SUPPORT_ATTACHMENT", _ => "PRESENT"),
        ["analytics_profile_read_completed"] = ("ANALYTICS_PROFILE", f => Records.AnalyticsState(Rec(f))),
        ["analytics_profile_read"] = ("ANALYTICS_PROFILE", AnalyticsRead),
        ["analytics_identity_scan"] = ("ANALYTICS_PROFILE", AnalyticsRead),
        // a record found only through the hash of an OLD email (opt-in contact row): a secondary identifier, rule_02 DELETE
        ["contact_lookup_completed"] = ("SECONDARY_IDENTIFIER", f => Get(f, "rows", "1") != "0" ? "PRESENT" : "ABSENT"),
        ["contact_query_completed"] = ("SECONDARY_IDENTIFIER", f => Get(f, "rows", "1") != "0" ? "PRESENT" : "ABSENT"),
        ["deletion_marker_check"] = ("SECONDARY_IDENTIFIER", f => !Truthy(Get(f, "marker_found")) ? "PRESENT" : "ABSENT"),
        ["connector_config_read_completed"] = ("DATA_FLOW", f => Truthy(Get(f, "enabled")) ? "ACTIVE" : "DISABLED"),
        ["connector_heartbeat"] = ("DATA_FLOW", FlowHeartbeat),
        ["connector_config_loaded"] = ("DATA_FLOW", FlowLoaded),
        ["restore_policy_read_completed"] = ("BACKUP_ARCHIVE", f => Records.BackupState(Rec(f))),
        ["restore_candidate_evaluated"] = ("BACKUP_ARCHIVE", RestoreCandidate),
        ["restore_authorization_completed"] = ("BACKUP_ARCHIVE", f => f.TryGetValue("decision", out var d) && d == "deny" ? "BLOCKED" : "RESTORABLE"),
        ["audit_record_read_completed"] = ("SECURITY_AUDIT_LOG", _ => "PRESENT"),
        ["export_metadata_read_completed"] = ("USER_EXPORT", f => Truthy(Get(f, "available")) ? "PRESENT" : "ABSENT"),
        ["export_download_check_completed"] = ("USER_EXPORT", f => Truthy(Get(f, "available")) ? "PRESENT" : "ABSENT"),
    };

    public static void LoadLog(EvidenceBase b, string fileId, string path, DateTime fileT)
    {
        var events = LogParse.ParseLog(Compat.ReadText(path, replaceInvalid: true));
        var groups = new Dictionary<string, List<LogParse.LogEvent>>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            var key = e.Trace.Length > 0 ? e.Trace : $"line{e.LineNo}";
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
            list.Add(e);
        }
        foreach (var evs in groups.Values)
        {
            var shared = Records.IdentsFrom(evs.SelectMany(e => Records.Flatten(e.Fields.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal))));
            foreach (var e in evs)
            {
                var f = e.Fields;
                var parsed = Compat.ParseTs(e.Ts);
                var t = parsed <= fileT ? parsed : fileT;
                if (e.Event == "record_lookup")
                {
                    f.TryGetValue("record_type", out var recordType);
                    f.TryGetValue("result", out var result);
                    string? state = Records.LookupRules.TryGetValue((recordType ?? "", result ?? ""), out var st) ? st : null;
                    if (!string.IsNullOrEmpty(state) && f.TryGetValue("resource_id", out var rid) && rid.Length > 0)
                    {
                        var system = f.TryGetValue("system", out var sys) && sys.Length > 0 ? sys : Records.SystemFor(rid);
                        b.Obs.Add(new Obs(fileId, fileT, t, system, rid, f["record_type"], state, Records.IdentsFrom(Records.Flatten(new Dictionary<string, object?> { ["s"] = f.TryGetValue("subject_reference", out var s) ? s : "" })), "log:record_lookup"));
                    }
                    continue;
                }
                if (!EventRules.TryGetValue(e.Event, out var rule) || !f.TryGetValue("resource_id", out var resourceId) || resourceId.Length == 0) continue;
                var ruleState = rule.Rule(f);
                if (!string.IsNullOrEmpty(ruleState))
                    b.Obs.Add(new Obs(fileId, fileT, t, Records.SystemFor(resourceId), resourceId, rule.Category, ruleState, shared, $"log:{e.Event}"));
            }
        }
    }
}
