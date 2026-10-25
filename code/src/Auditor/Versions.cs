using System.Security.Cryptography;
using System.Text;
using Auditor.Agent;
using Auditor.Evidence;

namespace Auditor;

public sealed record PromptStamp(string Version, string Digest);

/// <summary>What produced an answer, stamped into output.notes.md, the LLM cache key and the resume key.</summary>
public sealed record VersionStamp(string Pipeline, string Rules, IReadOnlyDictionary<string, PromptStamp> Prompts, string Code, string Settings, string Readings = "");

/// <summary>
/// What produced an answer: the versions that are stamped into output.notes.md, the LLM cache key and the resume key.
/// * <see cref="PipelineVersion"/> - written by hand. Bump it whenever a change can alter an answer or a model reading (new rule, parser fix, changed prompt wording). It is part of the LLM cache
///   key, so cached screenshot readings made by another version are never reused.
/// * rules digest     - sha256 of dataset/deletion_rules.csv as it is on disk: the policy is data, so another file is another policy.
/// * prompt versions  - the label of each model prompt plus a digest of its full text and output schema, so editing a prompt without touching its label still invalidates the cache.
/// * code digest      - sha256 of the compiled auditor library. Used by the resume key only (any change to the code invalidates a checkpoint) and shown in the run stamp; it is NOT in the LLM
///   cache key, because a code edit does not change what a model says about an image.
/// </summary>
public static class Versions
{
    public const string PipelineVersion = "2026.10.6";
    /// <summary>layout of the cache key itself; bump if the key's fields change</summary>
    public const int LlmCacheFormat = 2;

    public static string Sha256Bytes(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    public static string DigestText(params string[] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var p in parts)
        {
            h.AppendData(Encoding.UTF8.GetBytes(p));
            h.AppendData(new byte[] { 0 });
        }
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Digest of deletion_rules.csv; 'missing' when the file is absent (the run then fails later with its own error).</summary>
    public static string RulesDigest(string datasetDir)
    {
        var p = Path.Combine(datasetDir, "deletion_rules.csv");
        return File.Exists(p) ? Sha256Bytes(File.ReadAllBytes(p)) : "missing";
    }

    /// <summary>Every prompt a model sees: label and digest of its text + schema.</summary>
    public static Dictionary<string, PromptStamp> Prompts()
    {
        var output = new Dictionary<string, PromptStamp>(StringComparer.Ordinal)
        {
            ["image_reading"] = new(ImageObs.PromptVersion, DigestText(ImageObs.System, ImageObs.Instruction, SortedJson.Dumps(ImageObs.Schema))),
        };
        try                                                   // the agent layer's prompt files and tool definitions (code/prompts/, Agent/AgentTools.cs)
        {
            var sys = PromptFile.Load("image_reader.system.md");
            var task = PromptFile.Load("image_reader.task.md");
            output["agent_image_reader"] = new($"{sys.Version}/{task.Version}", DigestText(sys.Text, task.Text, AgentTools.ToolsDigest()));
        }
        catch (Exception e)                                   // a missing prompt file must not break the run stamp of a run that does not use the agent
        {
            output["agent_image_reader"] = new("unavailable", e.GetType().Name);
        }
        return output;
    }

    public static string CodeDigest()
    {
        var location = typeof(Versions).Assembly.Location;
        return location.Length > 0 && File.Exists(location) ? Sha256Bytes(File.ReadAllBytes(location)) : "unknown";
    }

    /// <summary>The full version stamp of a run.</summary>
    public static VersionStamp Stamp(string datasetDir, string settingsFingerprint = "") =>
        new(PipelineVersion, RulesDigest(datasetDir), Prompts(), CodeDigest(), settingsFingerprint);

    public static VersionStamp DefaultStamp() => new(PipelineVersion, "", new Dictionary<string, PromptStamp>(), "", "");

    /// <summary>The part of a stamp that belongs in the LLM cache key (no code digest, no settings).</summary>
    public static VersionStamp CacheStamp(VersionStamp st) => new(st.Pipeline, st.Rules, new Dictionary<string, PromptStamp>(), "", "");

    /// <summary>Markdown lines for output.notes.md.</summary>
    public static List<string> StampLines(VersionStamp st)
    {
        var lines = new List<string> { $"* pipeline version: `{st.Pipeline}`", $"* rules file (deletion_rules.csv) sha256: `{st.Rules}`" };
        lines.AddRange(st.Prompts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"* prompt `{p.Key}`: version `{p.Value.Version}`, text+schema sha256 `{p.Value.Digest}`"));
        lines.Add($"* code sha256 (Auditor.dll): `{st.Code}`");
        lines.Add($"* settings fingerprint: `{st.Settings}`");
        if (st.Readings.Length > 0) lines.Add($"* screenshots read from a READINGS FILE, not by a model: {st.Readings}");
        return lines;
    }
}
