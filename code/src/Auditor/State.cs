using Auditor.Evidence;

namespace Auditor;

/// <summary>State of one resource at audit_at.</summary>
public sealed record ResourceState(string Resource, string System, string Category, string State, string FileId, DateTime FileT, DateTime T, string Source, DateTime? Until = null);

/// <summary>
/// State of one resource at audit_at, from the evidence captured inside the request window.
/// Window rule: only observations whose FILE was captured in [requested_at, audit_at] count as state evidence.
///  * after audit_at: unknowable at audit time (never used for state, never cited);
///  * before requested_at: describes the world before the deletion was even requested; the same person can appear in several scenarios (users with two requests),
///    and old observations would resurrect resources of another scenario.
/// Later evidence may still be used to LINK a resource to a user (see <see cref="Linker"/>), and each such use is logged.
/// </summary>
public static class StateResolver
{
    /// <summary>when several observations of one resource sit in the same file (e.g. primary + replica tables), the violating one wins</summary>
    public static readonly IReadOnlyDictionary<string, int> Severity = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["PRESENT"] = 1, ["ABSENT"] = 0, ["EXPOSED"] = 1, ["REDACTED"] = 0, ["ANONYMIZED"] = 0, ["ACTIVE"] = 1, ["DISABLED"] = 0, ["RESTORABLE"] = 1, ["BLOCKED"] = 0,
    };

    /// <summary>observations written by the model-driven screenshot reader (auditor/agent); no other source starts with this</summary>
    public const string AgentSource = "image:agent";

    private static int SeverityOf(string state) => Severity.GetValueOrDefault(state, 0);

    public static bool Usable(Obs o, DateTime t0, DateTime auditAt) => o.State.Length > 0 && t0 <= o.FileT && o.FileT <= auditAt && o.T <= auditAt;

    private static Obs Pick(IReadOnlyList<Obs> obs)
    {
        var latestFileT = obs.Max(o => o.FileT);
        var inFile = obs.Where(o => o.FileT == latestFileT).ToList();
        if (inFile.All(o => o.Source.StartsWith("log:", StringComparison.Ordinal)))          // a log is a timeline: the last event wins
        {
            var lastT = inFile.Max(o => o.T);
            inFile = inFile.Where(o => o.T == lastT).ToList();
        }
        // tables of one snapshot: any copy that violates counts (the first of equal severity wins)
        return inFile.Aggregate((best, o) => SeverityOf(o.State) > SeverityOf(best.State) ? o : best);
    }

    public static ResourceState? Resolve(IEnumerable<Obs> observations, DateTime t0, DateTime auditAt, List<string>? log = null)
    {
        var obs = observations.Where(o => Usable(o, t0, auditAt)).ToList();
        if (obs.Count == 0) return null;
        var chosen = Pick(obs);
        if (chosen.Source == AgentSource)
        {
            // Gate rule G10: a model reading may add a finding or confirm a state, but it cannot lower the severity of the state that deterministic evidence supports in the window.
            // Entered only when a model-sourced observation was chosen, so a run without the agent layer never reaches this branch (byte-identical by construction).
            var det = obs.Where(o => !o.Source.StartsWith(AgentSource, StringComparison.Ordinal)).ToList();
            if (det.Count > 0)
            {
                var baseObs = Pick(det);
                if (SeverityOf(baseObs.State) > SeverityOf(chosen.State))
                {
                    log?.Add($"G10 {chosen.Resource}: the model reading in {chosen.FileId} says {chosen.State}, but {baseObs.FileId} supports {baseObs.State}: the supported state stays");
                    chosen = baseObs;
                }
            }
        }
        return new ResourceState(chosen.Resource, chosen.System, chosen.Category, chosen.State, chosen.FileId, chosen.FileT, chosen.T, chosen.Source, chosen.Until);
    }
}
