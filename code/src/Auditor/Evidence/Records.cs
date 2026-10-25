using System.Text.RegularExpressions;

namespace Auditor.Evidence;

/// <summary>
/// One uniform shape for everything any evidence file says about one resource at one moment.
/// Every parser (SQL, JSON, logs, text, PDF, image) produces <see cref="Obs"/> objects; everything downstream (linking, state at audit_at, policy, steps) only ever sees <see cref="Obs"/>.
/// That is what lets one policy engine handle fifteen evidence formats.
/// </summary>
/// <remarks>
/// State vocabulary (the <c>State</c> field), by data category:
///   DELETE-type categories (USER_PROFILE, SEARCH_INDEX_ENTRY, SUPPORT_ATTACHMENT, USER_EXPORT, SECONDARY_IDENTIFIER, MARKETING_PROFILE): PRESENT | ABSENT
///   SUPPORT_MESSAGE, ACCOUNT_TOMBSTONE: EXPOSED | REDACTED;  ANALYTICS_PROFILE: EXPOSED | ANONYMIZED;  DATA_FLOW: ACTIVE | DISABLED;  BACKUP_ARCHIVE: RESTORABLE | BLOCKED
///   SECURITY_AUDIT_LOG: PRESENT;  LEGAL_HOLD_RECORD: PRESENT | EXPOSED (carries a direct identifier);  CACHE_ENTRY: PRESENT | ABSENT
///   EMAIL_DELIVERY_LOG: EXPOSED | REDACTED;  PRODUCT_TELEMETRY: EXPOSED | ANONYMIZED
/// </remarks>
public sealed record Obs(
    string FileId,                 // manifest file_id this observation came from ("" for activity-derived)
    DateTime FileT,                // manifest captured_at (the file as a whole)
    DateTime T,                    // when the record was observed (record/event time; defaults to FileT)
    string System,
    string Resource,
    string Category,               // a deletion_rules.csv data_category (or DATA_FLOW)
    string State,                  // "" = link-only observation (no state claim)
    IReadOnlySet<string> Idents,
    string Source = "",            // e.g. "sql:customer_data_read_replica", "log:profile_lookup_completed"
    string Detail = "",            // short human-readable reason for the state
    DateTime? Until = null);       // retention / hold end carried by the record itself (retained_until); null = not stated

/// <summary>A file (or snapshot inside it) that lists ALL rows of a system's table/collection, so a missing row means absent.</summary>
public sealed record Coverage(string FileId, DateTime FileT, DateTime T, string System, string Source = "");

public sealed class EvidenceBase
{
    public List<Obs> Obs { get; set; } = new();
    public List<Coverage> Coverage { get; } = new();
    public List<string> Warnings { get; } = new();          // unreadable/corrupt files etc. (never raises)
    public List<Routed> Routes { get; } = new();            // how each manifest file was routed and what it produced
}

public static class Records
{
    public static readonly IReadOnlySet<string> NoIdents = new HashSet<string>(StringComparer.Ordinal);

    public static readonly Regex EmailRe = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant);
    public static readonly Regex UserRe = new(@"\buser_\d+\b", RegexOptions.CultureInvariant);
    public static readonly Regex HashRe = new(@"(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])", RegexOptions.CultureInvariant);
    public static readonly Regex ResourceRe = new(@"\b(account|search|support|analytics|export|data_flow|backup|audit_record|tombstone|legal_hold|marketing|cache|email_delivery|object)_\d+\b", RegexOptions.CultureInvariant);
    private static readonly Regex PhoneInText = new(@"\+\d[\d ()-]{6,}", RegexOptions.CultureInvariant);
    private static readonly Regex NonDigits = new(@"\D", RegexOptions.CultureInvariant);

    // fields that look like identifiers but are not (integrity seals are 64 hex chars, file names embed ids ...)
    private static readonly HashSet<string> NonIdentKeys = new(StringComparer.Ordinal) { "integrity_seal", "checksum", "archive_key", "object_key", "archive_path", "file_name", "file", "ticket_id" };

    /// <summary>resource-id prefix -> owning system (used when a source does not name the system)</summary>
    public static readonly IReadOnlyDictionary<string, string> SystemByPrefix = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["account"] = "account_service", ["search"] = "search_service", ["support"] = "support_platform",
        ["analytics"] = "analytics_platform", ["export"] = "export_service", ["data_flow"] = "integration_service",
        ["backup"] = "backup_service", ["audit_record"] = "security_service", ["tombstone"] = "identity_service",
        ["legal_hold"] = "legal_service", ["marketing"] = "marketing_platform", ["cache"] = "cache_service",
        ["email_delivery"] = "email_delivery_service", ["object"] = "object_storage",
    };

    public static string Prefix(string resourceId) => resourceId[..resourceId.LastIndexOf('_')];

    public static string SystemFor(string resourceId) =>
        SystemByPrefix.TryGetValue(Prefix(resourceId), out var s) ? s : throw new DataException($"no system for resource id '{resourceId}'");

    public static string NormPhone(string value)
    {
        var digits = NonDigits.Replace(value, "");
        return digits.Length >= 7 ? "p:" + digits : "";
    }

    /// <summary>(last-dict-key, scalar-as-string) pairs for every non-null, non-boolean scalar in a nested structure.</summary>
    public static IEnumerable<(string Key, string Value)> Flatten(object? obj, string key = "")
    {
        switch (obj)
        {
            case Dictionary<string, object?> d:
                foreach (var (k, v) in d) foreach (var pair in Flatten(v, k)) yield return pair;
                break;
            case List<object?> l:
                foreach (var v in l) foreach (var pair in Flatten(v, key)) yield return pair;
                break;
            case null:
            case bool:
                break;
            default:
                yield return (key, Py.Str(obj));
                break;
        }
    }

    /// <summary>Identifier strings (lower-case emails, user ids, sha256 hashes, 'p:'+digits phones) found in field VALUES. Exact tokens only - never names, never fuzzy matching.</summary>
    public static IReadOnlySet<string> IdentsFrom(IEnumerable<(string Key, string Value)> pairs)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, val) in pairs)
        {
            if (NonIdentKeys.Contains(key)) continue;
            foreach (Match m in EmailRe.Matches(val)) found.Add(m.Value.ToLowerInvariant());
            foreach (Match m in UserRe.Matches(val)) found.Add(m.Value);
            foreach (Match m in HashRe.Matches(val)) found.Add(m.Value.ToLowerInvariant());
            if (key.ToLowerInvariant().Contains("phone", StringComparison.Ordinal))
            {
                var p = NormPhone(val);
                if (p.Length > 0) found.Add(p);
            }
        }
        return found;
    }

    // ----------------------------------------------------------------------------- state rules per record shape
    public static string S(object? v) => v is null ? "" : Py.Str(v).Trim();

    private static object? Field(Dictionary<string, object?> rec, string key) => rec.TryGetValue(key, out var v) ? v : null;

    public static string ProfileState(Dictionary<string, object?> rec) => "PRESENT";

    public static string AnalyticsState(Dictionary<string, object?> rec) =>
        new[] { "user_id", "customer_reference", "email", "phone", "email_hash" }.Any(k => S(Field(rec, k)).Length > 0) ? "EXPOSED" : "ANONYMIZED";

    public static string TombstoneState(Dictionary<string, object?> rec) =>
        new[] { "name", "email", "phone", "email_hash", "full_name" }.Any(k => S(Field(rec, k)).Length > 0) ? "EXPOSED" : "REDACTED";

    /// <summary>Exposed iff a direct identifier (email/name/phone) is stored or quoted in the message text.</summary>
    public static string SupportMessageState(Dictionary<string, object?> rec)
    {
        foreach (var k in new[] { "requester_email", "requester_name", "requester_phone" })
            if (S(Field(rec, k)).Length > 0) return "EXPOSED";
        var text = S(Field(rec, "message_text")) + " " + S(Field(rec, "message_excerpt"));
        return EmailRe.IsMatch(text) || PhoneInText.IsMatch(text) ? "EXPOSED" : "REDACTED";
    }

    private static readonly string[] DirectIdentifierKeys = { "name", "full_name", "email", "phone", "email_hash", "customer_email", "owner_email", "requester_email" };

    /// <summary>A record kept on legal hold may carry the stable reference and order facts, nothing that identifies the person directly (rule_15's principle).</summary>
    public static string RetainedState(Dictionary<string, object?> rec) =>
        DirectIdentifierKeys.Any(k => S(Field(rec, k)).Length > 0) || EmailRe.IsMatch(S(Field(rec, "subject_reference"))) ? "EXPOSED" : "PRESENT";

    /// <summary>rule_11: a delivery log that contains an e-mail address must be redacted.</summary>
    public static string EmailLogState(Dictionary<string, object?> rec) =>
        Flatten(rec).Any(p => EmailRe.IsMatch(S(p.Value))) ? "EXPOSED" : "REDACTED";

    public static string ExportState(Dictionary<string, object?> rec)
    {
        object? avail = rec.TryGetValue("download_available", out var a) ? a : rec.TryGetValue("available", out var b) ? b : true;
        var text = Py.Str(avail).ToLowerInvariant();
        return text is not ("false" or "0" or "no") ? "PRESENT" : "ABSENT";
    }

    public static string FlowState(Dictionary<string, object?> rec) =>
        Py.Str(Field(rec, "enabled")).ToLowerInvariant() is "true" or "1" ? "ACTIVE" : "DISABLED";

    public static string BackupState(Dictionary<string, object?> rec) =>
        Py.Str(Field(rec, "restore_allowed")).ToLowerInvariant() is "true" or "1" ? "RESTORABLE" : "BLOCKED";

    public delegate string StateFn(Dictionary<string, object?> rec);

    /// <summary>collection / table name -> (category, state function)</summary>
    public static readonly IReadOnlyDictionary<string, (string Category, StateFn State)> CollectionRules = new Dictionary<string, (string, StateFn)>(StringComparer.Ordinal)
    {
        ["customer_profiles"] = ("USER_PROFILE", ProfileState),
        ["customer_profiles_primary"] = ("USER_PROFILE", ProfileState),
        ["customer_profiles_failover_replica"] = ("USER_PROFILE", ProfileState),
        ["customer_search_documents"] = ("SEARCH_INDEX_ENTRY", ProfileState),
        ["search_documents_primary"] = ("SEARCH_INDEX_ENTRY", ProfileState),
        ["customer_analytics_reports"] = ("ANALYTICS_PROFILE", AnalyticsState),
        ["customer_analytics_profiles"] = ("ANALYTICS_PROFILE", AnalyticsState),
        ["support_conversations"] = ("SUPPORT_MESSAGE", SupportMessageState),
        ["support_records"] = ("SUPPORT_MESSAGE", SupportMessageState),
        ["ticket_attachments"] = ("SUPPORT_ATTACHMENT", ProfileState),
        ["customer_export_jobs"] = ("USER_EXPORT", ExportState),
        ["customer_exports"] = ("USER_EXPORT", ProfileState),
        ["connector_configurations"] = ("DATA_FLOW", FlowState),
        ["connector_subscriptions"] = ("DATA_FLOW", FlowState),
        ["backup_catalog"] = ("BACKUP_ARCHIVE", BackupState),
        ["backup_restore_entries"] = ("BACKUP_ARCHIVE", BackupState),
        ["security_audit_events"] = ("SECURITY_AUDIT_LOG", ProfileState),
        ["security_audit_records"] = ("SECURITY_AUDIT_LOG", ProfileState),
        ["deleted_customer_references"] = ("ACCOUNT_TOMBSTONE", TombstoneState),
        ["retained_order_records"] = ("LEGAL_HOLD_RECORD", RetainedState),
        // categories that no labeled sample exercises; layouts follow the neighbouring collections (verified only by evaluation/extra_cases.md)
        ["marketing_profiles"] = ("MARKETING_PROFILE", ProfileState),
        ["cache_entries"] = ("CACHE_ENTRY", ProfileState),
        ["email_delivery_logs"] = ("EMAIL_DELIVERY_LOG", EmailLogState),
        ["product_telemetry_events"] = ("PRODUCT_TELEMETRY", AnalyticsState),
        ["secondary_identifiers"] = ("SECONDARY_IDENTIFIER", ProfileState),
        ["object_attachments"] = ("SUPPORT_ATTACHMENT", ProfileState),
    };

    /// <summary>"record lookup" vocabulary shared by handover text, security ledger, operational_record_history and record_lookup logs</summary>
    public static readonly IReadOnlyDictionary<(string RecordType, string Result), string> LookupRules = new Dictionary<(string, string), string>
    {
        [("USER_PROFILE", "FOUND")] = "PRESENT", [("USER_PROFILE", "NOT_FOUND")] = "ABSENT",
        [("SEARCH_INDEX_ENTRY", "FOUND")] = "PRESENT", [("SEARCH_INDEX_ENTRY", "NOT_FOUND")] = "ABSENT",
        [("SECURITY_AUDIT_LOG", "FOUND")] = "PRESENT",
        [("SUPPORT_MESSAGE", "IDENTIFIABLE")] = "EXPOSED", [("SUPPORT_MESSAGE", "REDACTED")] = "REDACTED",
        [("SUPPORT_ATTACHMENT", "FOUND")] = "PRESENT", [("SUPPORT_ATTACHMENT", "NOT_FOUND")] = "ABSENT",
        [("ANALYTICS_PROFILE", "IDENTIFIABLE")] = "EXPOSED", [("ANALYTICS_PROFILE", "ANONYMIZED")] = "ANONYMIZED",
        [("USER_EXPORT", "FOUND")] = "PRESENT", [("USER_EXPORT", "NOT_FOUND")] = "ABSENT",
        [("DATA_FLOW", "ACTIVE")] = "ACTIVE", [("DATA_FLOW", "DISABLED")] = "DISABLED",
        [("BACKUP_ARCHIVE", "ACTIVE")] = "RESTORABLE", [("BACKUP_ARCHIVE", "BLOCKED")] = "BLOCKED",
        [("ACCOUNT_TOMBSTONE", "IDENTIFIABLE")] = "EXPOSED", [("ACCOUNT_TOMBSTONE", "REDACTED")] = "REDACTED",
        [("LEGAL_HOLD_RECORD", "FOUND")] = "PRESENT", [("LEGAL_HOLD_RECORD", "IDENTIFIABLE")] = "EXPOSED",
        [("MARKETING_PROFILE", "FOUND")] = "PRESENT", [("MARKETING_PROFILE", "NOT_FOUND")] = "ABSENT",
        [("CACHE_ENTRY", "FOUND")] = "PRESENT", [("CACHE_ENTRY", "NOT_FOUND")] = "ABSENT",
        [("EMAIL_DELIVERY_LOG", "IDENTIFIABLE")] = "EXPOSED", [("EMAIL_DELIVERY_LOG", "REDACTED")] = "REDACTED",
        [("PRODUCT_TELEMETRY", "IDENTIFIABLE")] = "EXPOSED", [("PRODUCT_TELEMETRY", "ANONYMIZED")] = "ANONYMIZED",
        [("SECONDARY_IDENTIFIER", "FOUND")] = "PRESENT", [("SECONDARY_IDENTIFIER", "NOT_FOUND")] = "ABSENT",
    };
}
