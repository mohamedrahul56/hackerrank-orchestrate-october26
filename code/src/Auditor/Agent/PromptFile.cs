using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Auditor.Agent;

public sealed class PromptException : Exception
{
    public PromptException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>A versioned prompt file under code/prompts/: <c>---\nversion: &lt;label&gt;\n---\n&lt;body&gt;</c>. The label and the sha256 of the text are part of the cache key and the run stamp.</summary>
public sealed record PromptFile(string Name, string Version, string Text, string Sha256)
{
    private static readonly Regex Front = new(@"\A---\nversion:\s*(?<v>[A-Za-z0-9._-]+)\s*\n---\n", RegexOptions.CultureInvariant);

    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "prompts");

    /// <summary>Replace <c>{key}</c> placeholders by value. Only the named keys are replaced; other braces stay as they are.</summary>
    public string Fill(IReadOnlyDictionary<string, object> values)
    {
        var output = Text;
        foreach (var (k, v) in values) output = output.Replace("{" + k + "}", Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return output;
    }

    public static PromptFile Load(string name, string? directory = null)
    {
        var path = Path.Combine(directory ?? DefaultDirectory, name);
        string raw;
        try { raw = Compat.ReadText(path); }
        catch (Exception e) when (e is IOException or DataException) { throw new PromptException($"prompt file not found: {name}", e); }
        var m = Front.Match(raw);
        if (!m.Success) throw new PromptException($"{name}: the file must start with a front matter block '---', 'version: <label>', '---'");
        var body = raw[(m.Index + m.Length)..].Trim();
        if (body.Length == 0) throw new PromptException($"{name}: empty prompt");
        return new PromptFile(name, m.Groups["v"].Value, body, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant());
    }
}
