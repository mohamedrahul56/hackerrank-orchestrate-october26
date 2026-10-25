using Auditor.Evidence;

namespace Auditor;

/// <summary>
/// <c>auditor --explain CASE_ID</c>: a read-only, step-by-step account of how one case got its answer.
/// It runs the real case audit with a <see cref="Trace"/>, so every sentence below describes a decision the code actually took, then adds the evidence behind it: identity resolution, every
/// observation of every resource with the one that was used marked, the rule from deletion_rules.csv, and for each emitted step (and each omitted one) the reason. Nothing is written
/// anywhere; the printed answer is the same row output.csv holds for that case.
/// </summary>
public static class Explainer
{
    private static string T(DateTime? dt) => dt is null ? "-" : Compat.FormatTs(dt.Value);

    private static List<string> Table(string[] header, List<string[]> rows, int indent = 2)
    {
        var pad = new string(' ', indent);
        if (rows.Count == 0) return new() { pad + "(none)" };
        var widths = Enumerable.Range(0, header.Length).Select(i => rows.Prepend(header).Max(r => r[i].Length)).ToArray();
        string Line(IEnumerable<string> cells) => (pad + string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i])))).TrimEnd();
        var lines = new List<string> { Line(header), Line(widths.Select(w => new string('-', w))) };
        lines.AddRange(rows.Select(r => Line(r)));
        return lines;
    }

    /// <summary>Plain-text explanation of one case. Throws <see cref="KeyNotFoundException"/> for an unknown case id.</summary>
    public static string Explain(CaseAuditor a, string caseId)
    {
        if (!a.Requests.ContainsKey(caseId) && !a.BadRequests.ContainsKey(caseId)) throw new KeyNotFoundException(caseId);
        var L = new List<string> { $"EXPLAIN {caseId}", new string('=', 8 + caseId.Length), "" };
        if (!a.Requests.ContainsKey(caseId))
        {
            var malformed = a.AuditCase(caseId);
            L.Add($"The request row is malformed ({a.BadRequests[caseId]}); it cannot be audited.");
            L.Add($"Answer: {malformed.Status}, steps_json = []");
            L.Add($"Why: {string.Join("; ", malformed.Notes)}");
            return string.Join("\n", L) + "\n";
        }
        var r = a.Requests[caseId];
        L.AddRange(new[]
        {
            "0. THE REQUEST",
            $"  email (as supplied) : {r.Email}",
            $"  requested_at        : {T(r.RequestedAt)}      only evidence captured from here on counts as state",
            $"  audit_at            : {T(r.AuditAt)}      the cutoff: evidence captured later never changes the answer or gets cited",
            $"  reported_status     : {r.ReportedStatus}  (a claim of the deletion workflow, reported {T(r.ReportedAt)}; never used as proof)",
            $"  request_text        : {r.RequestText}", "",
        });

        // ------------------------------------------------------------------ 1. request meaning
        var cls = a.Classification[caseId];
        L.Add("1. WHAT THE PERSON ASKED FOR (Classifier)");
        L.Add($"  decision : {cls.Label}  (method: {cls.Method})");
        if (cls.Reasons.Count > 0) L.AddRange(cls.Reasons.Select(x => $"  because  : {x}"));
        else L.Add("  because  : (no reasons recorded)");
        if (cls.Label != Classifier.Deletion)
        {
            var notApplicable = a.AuditCase(caseId);
            L.Add("");
            L.Add("RESULT");
            L.Add($"  deletion_status = {notApplicable.Status}, steps_json = []");
            L.Add("  A request that does not authorize deletion is NOT_APPLICABLE whatever the evidence says; no identity or evidence is examined.");
            return string.Join("\n", L) + "\n";
        }

        // ------------------------------------------------------------------ 2. identity
        L.Add("");
        L.Add("2. WHO THE PERSON IS (IdentityResolver)");
        var people = a.Res.Resolve(r.Email, r.RequestedAt);
        if (people.Count == 0) L.Add("  the e-mail matches no known identity -> conservative NOT_APPLICABLE (fallback)");
        foreach (var p in people)
        {
            var mail = r.Email.Trim().ToLowerInvariant();
            var span = p.EmailSpans.FirstOrDefault(s => s.Email == mail);
            var kind = mail == p.CurrentEmail ? "the CURRENT e-mail" : "a HISTORICAL e-mail";
            var valid = span is null ? "" : $" (valid {(span.From is null ? "since the beginning" : T(span.From))} .. {(span.Until is null ? "now" : T(span.Until))})";
            L.Add($"  {r.Email} -> {p.UserId} ({p.Name}): {kind}{valid}");
            L.Add($"    identifiers used to match evidence: {p.Emails.Count} e-mail(s) {Compat.ListRepr(p.Emails.OrderBy(e => e, StringComparer.Ordinal))}, {p.Hashes.Count} hash(es), {p.Phones.Count} phone(s), and the user id {p.UserId}");
            L.Add("    matching is by exact identifier only (e-mail, user id, sha256 of the e-mail, phone); names and look-alike addresses never match");
        }
        if (people.Count > 1) L.Add($"  {people.Count} people own this e-mail; the one who owned it at requested_at wins");

        // ------------------------------------------------------------------ run the real audit with a trace
        var trace = new Trace();
        CaseResult res;
        try { res = a.AuditCase(caseId, trace); }
        catch (Exception e) { res = new CaseResult(caseId, "NOT_APPLICABLE", new(), new() { $"FALLBACK: {e.GetType().Name}: {e.Message}" }); }     // explain what the production path would do: it falls back
        var users = a.Users[caseId];

        // ------------------------------------------------------------------ 3. resources
        L.Add("");
        L.Add("3. EVERYTHING TIED TO THAT PERSON (Linker, Discover)");
        var rows = new List<string[]>();
        foreach (var (rid, how) in trace.Resources.OrderBy(kv => kv.Key, StringComparer.Ordinal).ThenBy(kv => kv.Value, StringComparer.Ordinal))
        {
            var first = a.Link.FirstLinkTime(rid, users);
            var link = first is null ? "-" : $"{first.Value.Id} captured {T(first.Value.T)}";
            var later = trace.Later.Any(x => x.Resource == rid) ? " (AFTER audit_at: used only to link, logged)" : "";
            var meaning = how.Replace("analytics-", "", StringComparison.Ordinal) switch
            {
                "explicit" => "identifier in evidence", "eliminated" => "timeline + elimination", "phase" => "timeline + second-of-minute offset", "chain" => "timeline + policy-action chain", _ => how,
            };
            string system = a.Link.CatalogSystems.TryGetValue(rid, out var cs) && cs.Length > 0 ? cs : a.Link.Observations(rid).Select(o => o.System).FirstOrDefault() is { Length: > 0 } os ? os : Records.SystemFor(rid);
            rows.Add(new[] { rid, system, how, meaning, link + later });
        }
        L.AddRange(Table(new[] { "resource", "system", "found by", "meaning", "first link evidence" }, rows));
        a.AccountAsg.TryGetValue(caseId, out var asg);
        a.AnalyticsAsg.TryGetValue(caseId, out var ana);
        if (asg is not null)
        {
            L.Add($"  account discovery   : {asg.How}" + (asg.Resource is not null ? $" -> {asg.Resource}" : "") + (asg.CandidateList.Count > 0 ? $"; candidates {Compat.ListRepr(asg.CandidateList)}" : "")
                  + (asg.Event is not null ? $"; worker delete event {asg.Event.ActivityId} at {T(asg.Event.T)} ({asg.Event.Result})" : ""));
            string? howText = asg.How switch
            {
                "explicit" => "the account carries one of the person's identifiers (or is tied to them and deleted by the worker inside the window)",
                "eliminated" => "among the free accounts the worker deleted in this case's window, all but one belong to other cases, so exactly one remains",
                "phase" => "several free accounts fit; the one whose delete time fits the case's other events by second-of-minute offset wins (a dataset-specific timing heuristic; `--no-timing-heuristics` switches it off)",
                _ => null,
            };
            if (howText is not null) L.Add($"      -> {howText}");
            if (asg.How == "ambiguous")
                L.Add("      -> several free accounts fit this case equally well and nothing separates them: NO account step is emitted (a guess would be right about 1 time in " +
                      $"{(asg.CandidateList.Count > 0 ? asg.CandidateList.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "k")}; omitting is safer for precision)");
            if (asg.How == "none" && asg.Resource is null)
                L.Add("      -> no worker delete event for a free account falls in this case's window: no account is known, so no account step");
        }
        if (ana is not null && (ana.Resource is not null || ana.How == "ambiguous"))
            L.Add($"  analytics discovery : {ana.How}" + (ana.Resource is not null ? $" -> {ana.Resource}" : $"; candidates {Compat.ListRepr(ana.CandidateList)} (not separable: no step)"));
        if (trace.Resources.Count == 0) L.Add("  (no resource is tied to this person)");

        // ------------------------------------------------------------------ 4. state per resource
        L.Add("");
        L.Add($"4. STATE AT audit_at ({T(r.AuditAt)}) AND THE RULE APPLIED (StateResolver, PolicyEngine)");
        L.Add($"  window: evidence files captured in [{T(r.RequestedAt)}, {T(r.AuditAt)}]; latest file wins; inside one snapshot the violating copy wins; in a log the last event wins");
        foreach (var rid in trace.Resources.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var obs = a.Link.Observations(rid).OrderBy(o => o.FileT).ThenBy(o => o.T).ThenBy(o => o.Source, StringComparer.Ordinal).ToList();
            trace.States.TryGetValue(rid, out var st);
            L.Add("");
            L.Add($"  {rid}");
            if (obs.Count > 0)
            {
                var orows = new List<string[]>();
                foreach (var o in obs)
                {
                    bool inWindow = StateResolver.Usable(o, r.RequestedAt, r.AuditAt);
                    bool used = st is not null && inWindow && o.FileId == st.FileId && o.State == st.State;
                    var why = used ? "USED" : inWindow ? "inside window, superseded or not the violating copy" : o.FileT > r.AuditAt ? "captured AFTER audit_at: ignored for state" : "captured BEFORE the request: describes the world before it";
                    orows.Add(new[] { o.FileId, T(o.FileT), o.Source, o.Category, o.State, why });
                }
                L.AddRange(Table(new[] { "file", "captured", "source", "category", "state", "role" }, orows, 4));
            }
            else L.Add("    no evidence file observes this resource");
            if (st is not null) L.Add($"    -> state used: {st.State} ({st.Category}), from {st.FileId} captured {T(st.FileT)}");
            else L.Add("    -> no usable observation inside the window");
            if (trace.Verdicts.TryGetValue(rid, out var verdict) && verdict.Length > 0) L.Add($"    rule: {verdict}");
            foreach (var (what, why) in trace.Omitted)
                if (what == rid) L.Add($"    no step: {why}");
        }

        // ------------------------------------------------------------------ 5. steps
        L.Add("");
        L.Add("5. THE STEPS AND WHY EACH EXISTS (CaseAuditor)");
        if (res.Steps.Count == 0) L.Add("  (none)");
        foreach (var s in res.Steps)
        {
            L.Add($"  #{s.StepNo,-2} {s.Phase,-12} {s.Operation,-26} {s.ResourceId,-20} state={s.State,-10} evidence_id={(s.EvidenceId.Length > 0 ? s.EvidenceId : "(empty)")}");
            L.Add($"        why: {(trace.StepWhy.TryGetValue((s.Phase, s.Operation, s.SystemId, s.ResourceId), out var w) ? w : "(no reason recorded)")}");
        }
        if (res.Steps.Count > 0)
            L.Add("  order: phases FINDING, REMEDIATION, VERIFICATION; inside a phase the operation table order puts DISABLE_DATA_FLOW and BLOCK_RESTORE before deletes, redactions and anonymizations; ties by system_id then resource_id (plain string order)");

        // ------------------------------------------------------------------ 6. omissions and notes
        var extra = trace.Omitted.Where(x => !trace.Resources.ContainsKey(x.Resource)).ToList();
        L.Add("");
        L.Add("6. WHAT WAS LEFT OUT, AND WHY");
        var leftovers = trace.Omitted.Where(x => trace.Resources.ContainsKey(x.Resource)).Concat(extra).ToList();
        if (asg is not null && asg.How is "ambiguous" or "none" && asg.Resource is null) leftovers.Add(("account", $"no account step ({asg.How}), see section 3"));
        if (ana is not null && ana.How == "ambiguous") leftovers.Add(("anonymized analytics", "several candidates fit and nothing separates them"));
        if (leftovers.Count > 0) L.AddRange(leftovers.Select(x => $"  {x.Resource}: {x.Why}"));
        else L.Add("  (nothing: every resource tied to the person produced the steps above or needed none)");
        if (trace.Later.Count > 0)
        {
            L.Add("  later-evidence links (evidence captured after audit_at, used ONLY to tie the resource to this person; never for state or citations):");
            L.AddRange(trace.Later.Select(x => $"    {x.Resource} via {x.FileId} captured {x.Time}"));
        }

        // ------------------------------------------------------------------ 7. result
        var whyStatus = res.Status switch
        {
            "INCOMPLETE" => "at least one resource violates its rule at audit_at (see the VIOLATION lines), or the row is provisional",
            "COMPLETE" => "every relevant copy has the required treatment, every required retained record is present, and no flow or restore path can recreate prohibited data",
            _ => "this is a fallback (no evidence-backed resource in the window, unknown identity, or an error): see the notes",
        };
        L.Add("");
        L.Add("7. RESULT");
        L.Add($"  deletion_status = {res.Status}: {whyStatus}");
        bool disagree = (r.ReportedStatus == "COMPLETE" && res.Status == "INCOMPLETE") || (r.ReportedStatus is "NOT_STARTED" or "PARTIALLY_COMPLETE" && res.Status == "COMPLETE");
        if (disagree) L.Add($"  note: the workflow REPORTED {r.ReportedStatus}; that claim was not used as proof. The evidence at audit_at says {res.Status}.");
        if (res.Provisional) L.Add("  PROVISIONAL: built from structured data because the only state evidence is a screenshot that was not read; replaced by the normal computation once screenshots are read");
        L.AddRange(res.Notes.Select(n => $"  note: {n}"));
        L.Add("");
        L.Add("  This is exactly the row `auditor` writes to output.csv for this case; --explain wrote nothing.");
        return string.Join("\n", L) + "\n";
    }
}
