using System.Security.Cryptography;
using System.Text;
using Auditor.Evidence;

namespace Auditor;

/// <summary>The readings file itself is unusable (missing, not JSON, wrong shape).</summary>
public sealed class ReadingsException : Exception
{
    public ReadingsException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The single-shot screenshot reader: one model request per image, through <see cref="Llm.ReadImageJson"/>.</summary>
public sealed class SingleShotImageReader : IImageReader
{
    private readonly Llm llm;

    public SingleShotImageReader(Llm llm) { this.llm = llm; }

    public string ObsSource => ImageObs.VlmSource;

    public Dictionary<string, object?> Read(string path, string system, string instruction, Dictionary<string, object?> schema, string promptVersion) =>
        llm.ReadImageJson("image", path, system, instruction, schema, promptVersion);
}

/// <summary>
/// A file of readings that stands in for the vision model: the same call as <see cref="Llm.ReadImageJson"/>, answered from a file instead of a model.
/// The file is JSON, keyed by the sha256 of each image's BYTES (not its name or path):
///   {"format": 1, "prompt_version": "img-v1", "source": "&lt;who made the readings and how&gt;", "readings": {"&lt;sha256 of the png&gt;": {"file": "&lt;name, for humans&gt;", "reading": {"screen": "...", "records": [...]}}}}
/// The reading of an image goes through exactly what a model's answer goes through: it must satisfy the same JSON schema, and the observations are then built by
/// <see cref="ImageObs.ObservationsFromReading"/>, which sanitises every field (capped lists, label-like status words, valid e-mails and ids only) and ignores free text. A file therefore
/// cannot do anything a model answer could not. An image whose hash is not in the file, or a file made for another prompt version, is "not read" (a warning), exactly like a missing API key.
/// Provenance is recorded: the usage log marks every reading as <c>provider: file</c>, and the run notes name the file's source and sha256.
/// </summary>
public sealed class FileReadings : IImageReader
{
    private readonly UsageLog? usage;

    public FileReadings(string path, UsageLog? usage = null)
    {
        Path = path; this.usage = usage;
        byte[] raw;
        object? doc;
        try
        {
            raw = File.ReadAllBytes(path);
            doc = Py.ParseJson(new UTF8Encoding(false, true).GetString(raw));
        }
        catch (FileNotFoundException e) { throw new ReadingsException($"readings file not found: {path}", e); }
        catch (Exception e) when (e is IOException or DecoderFallbackException or DataException) { throw new ReadingsException($"readings file {System.IO.Path.GetFileName(path)} is not valid JSON: {e.Message}", e); }
        if (doc is not Dictionary<string, object?> d || !Py.EqualsNumber(Py.Get(d, "format"), 1) || Py.Get(d, "readings") is not Dictionary<string, object?> readings || Py.Get(d, "prompt_version") is not string version)
            throw new ReadingsException($"readings file {System.IO.Path.GetFileName(path)}: expected an object with format 1, prompt_version and readings");
        FileSha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        PromptVersion = version;
        var src = Py.Str(d.TryGetValue("source", out var s) ? s : "");
        Source = src.Length > 300 ? src[..300] : src;
        Readings = readings;
    }

    public string Path { get; }
    public string FileSha256 { get; }
    public string PromptVersion { get; }
    public string Source { get; }
    public Dictionary<string, object?> Readings { get; }
    public string ObsSource => ImageObs.VlmSource;

    /// <summary>Same contract as <see cref="Llm.ReadImageJson"/>: the reading as a dictionary, or <see cref="LlmUnavailableException"/> (no reading) / <see cref="SchemaException"/> (reading does not fit the schema).</summary>
    public Dictionary<string, object?> ReadImageJson(string kind, string imagePath, string system, string instruction, Dictionary<string, object?> schema, string promptVersion)
    {
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(imagePath))).ToLowerInvariant();
        if (!Readings.TryGetValue(sha, out var entry) || entry is not Dictionary<string, object?> e || !e.ContainsKey("reading"))
            throw new LlmUnavailableException($"no reading for sha256 {sha[..12]} in {System.IO.Path.GetFileName(Path)}");
        if (PromptVersion != promptVersion)
            throw new LlmUnavailableException($"{System.IO.Path.GetFileName(Path)} was made for prompt version {Py.StrRepr(PromptVersion)}, this run asks for {Py.StrRepr(promptVersion)}");
        var reading = e["reading"];
        JsonSchemaCheck.Validate(reading, schema);                       // raises SchemaException, like a model answer that does not fit
        usage?.Add(new()
        {
            ["ts"] = Llm.Now(), ["provider"] = "file", ["model"] = "file-readings", ["kind"] = kind, ["file"] = System.IO.Path.GetFileName(imagePath), ["file_sha256"] = sha,
            ["cache_hit"] = false, ["calls"] = 0L, ["input_tokens"] = 0L, ["output_tokens"] = 0L, ["source"] = Source.Length > 80 ? Source[..80] : Source,
        });
        return (Dictionary<string, object?>)reading!;
    }

    public Dictionary<string, object?> Read(string path, string system, string instruction, Dictionary<string, object?> schema, string promptVersion) =>
        ReadImageJson("image", path, system, instruction, schema, promptVersion);

    public string Describe() => $"{System.IO.Path.GetFileName(Path)} sha256 {FileSha256}, {Readings.Count} readings, prompt {PromptVersion}, source: {Source}";
}
