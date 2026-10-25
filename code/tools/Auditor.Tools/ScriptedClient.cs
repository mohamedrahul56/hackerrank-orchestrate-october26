using System.Security.Cryptography;

namespace Auditor.Tools;

/// <summary>
/// A fake model for the agent loop: it finds the screenshot's bytes in the first message, looks the reading up by sha256 and answers with one <c>zoom_region</c> call and then
/// <c>submit_reading</c>. Used by the tests and by the agent proofs; no key, no network, no real model.
/// </summary>
public sealed class ScriptedClient : IModelClient
{
    private readonly Dictionary<string, object?> readings;
    private readonly bool zoomFirst;
    private readonly object gate = new();
    private readonly Dictionary<string, int> turns = new(StringComparer.Ordinal);
    private int calls;

    /// <param name="readings">sha256 of the screenshot → { "file": name, "reading": the reading object }</param>
    public ScriptedClient(Dictionary<string, object?> readings, bool zoomFirst = true) { this.readings = readings; this.zoomFirst = zoomFirst; }

    public int Calls { get { lock (gate) return calls; } }

    public ModelResponse Create(ModelRequest request, TimeSpan timeout)
    {
        var data = Convert.FromBase64String(request.Messages[0].Content.OfType<ImageBlock>().First().Base64Data);
        var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        int n;
        lock (gate) { calls++; n = turns[sha] = turns.GetValueOrDefault(sha) + 1; }
        ContentBlock block;
        if (zoomFirst && n == 1)
            block = new ToolUseBlock($"z{sha[..6]}", "zoom_region", new Dictionary<string, object?> { ["x"] = 100L, ["y"] = 100L, ["width"] = 400L, ["height"] = 200L });
        else
        {
            var reading = new Dictionary<string, object?>(Py.AsDict(Py.AsDict(readings[sha])["reading"])) { ["unknown"] = new List<object?>() };
            block = new ToolUseBlock($"s{sha[..6]}", "submit_reading", reading);
        }
        return new ModelResponse("tool_use", new[] { block }, new ModelUsage(1000, 100, 0));
    }
}
