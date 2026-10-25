using Auditor.Evidence;

namespace Auditor;

/// <summary>
/// Link resources to users by exact identifiers found in the evidence. A resource belongs to a user when any observation of it carries an identifier that belongs to that user
/// (current/old email, user_id, email hash, phone). Names and look-alike emails never match. <c>FirstLinkTime</c> records WHEN the earliest linking evidence was captured so the
/// pipeline can tell whether a link needed evidence captured after audit_at (allowed only for linking, and logged).
/// </summary>
public sealed class Linker
{
    private static readonly IReadOnlySet<string> NoStrings = new HashSet<string>(StringComparer.Ordinal);
    private static readonly IReadOnlyList<Obs> NoObs = Array.Empty<Obs>();

    public Dictionary<string, List<Obs>> ByResource { get; } = new(StringComparer.Ordinal);
    /// <summary>(resource, user) -> sorted list of (file_t, file_id) of observations that link them</summary>
    public Dictionary<(string Resource, string User), List<(DateTime T, string FileId)>> Links { get; } = new();
    public Dictionary<string, string> CatalogSystems { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> UserResources { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> ResourceUsers { get; } = new(StringComparer.Ordinal);

    public Linker(EvidenceBase evidence, IdentityResolver resolver, IReadOnlyList<CatalogRow> catalog)
    {
        foreach (var o in evidence.Obs)
        {
            if (!ByResource.TryGetValue(o.Resource, out var list)) ByResource[o.Resource] = list = new();
            list.Add(o);
            foreach (var tok in o.Idents)
                foreach (var uid in resolver.UsersForToken(tok)) AddLink(o.Resource, uid, o.FileT, o.FileId);
        }
        foreach (var row in catalog)                                  // catalog rows are explicit links too (no state)
        {
            CatalogSystems[row.ResourceId] = row.System;
            foreach (var tok in row.Idents)
                foreach (var uid in resolver.UsersForToken(tok)) AddLink(row.ResourceId, uid, row.T, row.ActivityId);
        }
        var cmp = Comparer<(DateTime T, string FileId)>.Create((a, b) => { int c = a.T.CompareTo(b.T); return c != 0 ? c : string.CompareOrdinal(a.FileId, b.FileId); });
        foreach (var v in Links.Values) v.Sort(cmp);
        foreach (var (rid, uid) in Links.Keys)
        {
            if (!UserResources.TryGetValue(uid, out var rs)) UserResources[uid] = rs = new(StringComparer.Ordinal);
            rs.Add(rid);
            if (!ResourceUsers.TryGetValue(rid, out var us)) ResourceUsers[rid] = us = new(StringComparer.Ordinal);
            us.Add(uid);
        }
    }

    private void AddLink(string resource, string user, DateTime t, string id)
    {
        if (!Links.TryGetValue((resource, user), out var list)) Links[(resource, user)] = list = new();
        list.Add((t, id));
    }

    public IReadOnlyList<Obs> Observations(string resource) => ByResource.TryGetValue(resource, out var l) ? l : NoObs;

    public HashSet<string> ResourcesOf(IEnumerable<string> users)
    {
        var output = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in users)
            if (UserResources.TryGetValue(u, out var rs)) output.UnionWith(rs);
        return output;
    }

    public IReadOnlySet<string> UsersOf(string resource) => ResourceUsers.TryGetValue(resource, out var s) ? s : NoStrings;

    /// <summary>The earliest (time, linking evidence id) over the given users, or null when none of them is linked to the resource.</summary>
    public (DateTime T, string Id)? FirstLinkTime(string resource, IEnumerable<string> users)
    {
        (DateTime T, string Id)? best = null;
        foreach (var u in users)
        {
            if (!Links.TryGetValue((resource, u), out var l)) continue;
            var c = l[0];
            if (best is null || Py.CompareTuple(c.T, c.FileId, best.Value.T, best.Value.Id) < 0) best = (c.T, c.FileId);
        }
        return best;
    }

    /// <summary>True if some linking evidence for (resource, any user) was captured at/before <paramref name="cutoff"/>.</summary>
    public bool LinkIdsBefore(string resource, IEnumerable<string> users, DateTime cutoff)
    {
        var t = FirstLinkTime(resource, users);
        return t is not null && t.Value.T <= cutoff;
    }
}
