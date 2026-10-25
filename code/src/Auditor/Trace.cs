namespace Auditor;

/// <summary>
/// Optional record of WHY a case got its answer. <c>Auditor.AuditCase(caseId, trace)</c> fills it at the exact points where it decides; <c>Explain</c> renders it.
/// Passing no trace (the normal run) costs nothing and cannot change a result.
/// </summary>
public sealed class Trace
{
    /// <summary>resource -> how it was tied to the case (explicit / eliminated / phase / chain ...)</summary>
    public Dictionary<string, string> Resources { get; set; } = new(StringComparer.Ordinal);
    /// <summary>(resource, linking file, time) links made by evidence after audit_at</summary>
    public List<(string Resource, string FileId, string Time)> Later { get; set; } = new();
    /// <summary>resource -> policy verdict sentence</summary>
    public Dictionary<string, string> Verdicts { get; } = new(StringComparer.Ordinal);
    /// <summary>(resource or topic, why no step)</summary>
    public List<(string Resource, string Why)> Omitted { get; } = new();
    /// <summary>(phase, operation, system, resource) -> why this step exists</summary>
    public Dictionary<(string Phase, string Operation, string System, string Resource), string> StepWhy { get; } = new();
    /// <summary>resource -> ResourceState actually used</summary>
    public Dictionary<string, ResourceState> States { get; set; } = new(StringComparer.Ordinal);
    public string StatusReason { get; set; } = "";

    public Step Why(Step step, string text)
    {
        StepWhy[(step.Phase, step.Operation, step.SystemId, step.ResourceId)] = text;
        return step;
    }
}
