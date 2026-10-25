using System.Security.Cryptography;
using System.Text;
using Auditor.Evidence;
using SkiaSharp;

namespace Auditor.Agent;

/// <summary>The call was well-formed JSON but cannot be executed (out of bounds, too small, ...). The message goes back to the model as an error tool result.</summary>
public sealed class ToolException : Exception
{
    public ToolException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// The two tools of the screenshot reader, with precise descriptions and JSON schemas. Both are read-only and side-effect-free.
/// * <c>zoom_region(x, y, width, height)</c>: an enlarged crop of THE screenshot being read (the tool takes no file argument, so it cannot reach any other file). Needs the native image
///   library; when it cannot be loaded the tool is not offered and a warning is logged once (the loop then consists of <c>submit_reading</c> only).
/// * <c>submit_reading(screen, records, unknown)</c>: the terminal tool. Its schema is the screenshot-reading schema of <see cref="ImageObs"/> plus an <c>unknown</c> list.
/// </summary>
public static class AgentTools
{
    public static readonly string[] UnknownFields = { "resource_id", "emails", "user_ids", "phones", "status_words", "aliases", "shows_direct_identifiers", "selected" };
    /// <summary>a crop smaller than this shows nothing worth enlarging</summary>
    public const int MinSide = 32;
    /// <summary>longest string the model may write into any field (the screen title is cut to 80 by the gate)</summary>
    public const int MaxText = 200;
    /// <summary>bounds the work for one zoom (the crop's area in original pixels)</summary>
    public const int MaxPixels = 4_000_000;
    /// <summary>a screenshot with more pixels than this is not decoded at all (a decompression bomb)</summary>
    public const long MaxImagePixels = 89_478_485;

    private static Dictionary<string, object?> Clone(Dictionary<string, object?> d) => (Dictionary<string, object?>)Py.ParseJson(Py.DumpsCompact(d))!;

    /// <summary>A copy of <paramref name="schema"/> in which every property carries its description (a missing description is a programming error, caught at first use).</summary>
    private static Dictionary<string, object?> Document(Dictionary<string, object?> schema, IReadOnlyDictionary<string, string> docs)
    {
        var copy = Clone(schema);

        void Walk(Dictionary<string, object?> node)
        {
            if (node.TryGetValue("properties", out var props) && props is Dictionary<string, object?> pd)
                foreach (var (name, sub) in pd)
                {
                    var subNode = (Dictionary<string, object?>)sub!;
                    subNode["description"] = docs[name];
                    Walk(subNode);
                }
            if (node.TryGetValue("items", out var items) && items is Dictionary<string, object?> itemNode) Walk(itemNode);
        }

        Walk(copy);
        return copy;
    }

    /// <summary>Size limits on the submission (the shared reading schema has none): lists and strings the model writes cannot be arbitrarily long. The limits equal what the sanitiser keeps.</summary>
    private static void Bound(Dictionary<string, object?> node)
    {
        var type = node.TryGetValue("type", out var t) ? t as string : null;
        if (type == "array")
        {
            bool ofObjects = node["items"] is Dictionary<string, object?> it && it.ContainsKey("properties");
            node["maxItems"] = (long)(ofObjects ? ImageObs.MaxRecords : ImageObs.MaxItems);
        }
        if (type == "string" && !node.ContainsKey("enum")) node["maxLength"] = (long)MaxText;
        if (node.TryGetValue("properties", out var props) && props is Dictionary<string, object?> pd)
            foreach (var sub in pd.Values) Bound((Dictionary<string, object?>)sub!);
        if (node.TryGetValue("items", out var items) && items is Dictionary<string, object?> itemNode) Bound(itemNode);
    }

    private static Dictionary<string, object?> Obj(params (string Key, object? Value)[] kv)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    private static Dictionary<string, object?> BuildSubmitSchema()
    {
        var baseSchema = Clone(ImageObs.Schema);
        baseSchema["required"] = new List<object?> { "screen", "records" };
        var props = (Dictionary<string, object?>)baseSchema["properties"]!;
        props["unknown"] = Obj(("type", "array"), ("items", Obj(
            ("type", "object"), ("additionalProperties", false), ("required", new List<object?> { "record_index", "field", "reason" }),
            ("properties", Obj(("record_index", Obj(("type", "integer"))), ("field", Obj(("type", "string"), ("enum", UnknownFields.Cast<object?>().ToList()))), ("reason", Obj(("type", "string"))))))));
        Bound((Dictionary<string, object?>)props["records"]!);           // not `screen`: a long title is cut to 80 characters by the gate (G7), it does not cost the whole reading
        Bound((Dictionary<string, object?>)props["unknown"]!);
        return Document(baseSchema, AgentToolTexts.FieldDocs);
    }

    private static Dictionary<string, object?> BuildZoomSchema()
    {
        var number = () => Obj(("type", "integer"));
        var schema = Obj(("type", "object"), ("additionalProperties", false), ("required", new List<object?> { "x", "y", "width", "height" }),
                         ("properties", Obj(("x", number()), ("y", number()), ("width", number()), ("height", number()))));
        return Document(schema, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x"] = AgentToolTexts.ZoomX, ["y"] = AgentToolTexts.ZoomY, ["width"] = AgentToolTexts.ZoomWidth, ["height"] = AgentToolTexts.ZoomHeight,
        });
    }

    public static readonly Dictionary<string, object?> SubmitSchema = BuildSubmitSchema();
    public static readonly Dictionary<string, object?> ZoomSchema = BuildZoomSchema();
    public static readonly ToolSpec ZoomTool = new("zoom_region", AgentToolTexts.ZoomDescription, ZoomSchema);
    public static readonly ToolSpec SubmitTool = new("submit_reading", AgentToolTexts.SubmitDescription, SubmitSchema);

    private static bool? available;

    /// <summary>True when the image library can be loaded (zoom needs it; reading the image header does not).</summary>
    public static bool Available()
    {
        if (available is { } known) return known;
        try { using var probe = new SKBitmap(1, 1); available = true; }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or BadImageFormatException) { available = false; }
        return available.Value;
    }

    /// <summary>The tools offered to the model: zoom only when the image library is available.</summary>
    public static List<ToolSpec> ToolSpecs()
    {
        if (Available()) return new List<ToolSpec> { ZoomTool, SubmitTool };
        Console.Error.WriteLine("warning: the image library cannot be loaded: the zoom_region tool is disabled; the reader can only submit what it sees");
        return new List<ToolSpec> { SubmitTool };
    }

    /// <summary>Digest of both tool definitions (name, description, schema): part of the cache key and the run stamp, whether or not the image library is available.</summary>
    public static string ToolsDigest()
    {
        object?[] tools = { ToolDocument(ZoomTool), ToolDocument(SubmitTool) };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PyJsonSorted.Dumps(tools)))).ToLowerInvariant();
    }

    private static Dictionary<string, object?> ToolDocument(ToolSpec t) => Obj(("name", t.Name), ("description", t.Description), ("input_schema", t.InputSchema));

    /// <summary>The resource-id families the audit can judge, as 'account_N, search_N, ...': derived from the code that judges them, so the prompt cannot drift from it.</summary>
    public static string IdFamilies()
    {
        var empty = new ReadingRecord("", false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), false, Array.Empty<string>());
        return string.Join(", ", Records.SystemByPrefix.Keys.Where(p => ImageObs.StateFromDescription(p, empty) is not null).Select(p => $"{p}_N"));
    }

    private static readonly byte[] PngMagic = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>(width, height) of a PNG, from its header (no image library needed).</summary>
    public static (int Width, int Height) ImageSize(string path)
    {
        var head = new byte[32];
        int n;
        using (var f = File.OpenRead(path)) n = f.Read(head, 0, 32);
        if (n < 24 || !head.AsSpan(0, 8).SequenceEqual(PngMagic)) throw new ToolException("not a PNG file");
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16, 4)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20, 4)));
    }

    /// <summary>Crop <paramref name="args"/> out of the PNG at <paramref name="path"/>, enlarge it so that its longer side is <paramref name="maxPx"/> at most (a fixed filter, so the result is deterministic), return (png bytes, media type).</summary>
    public static (byte[] Png, string Media) ZoomRegion(string path, Dictionary<string, object?> args, int maxPx = 1200)
    {
        if (!Available()) throw new ToolException("zoom_region is not available (the image library cannot be loaded)");
        int x = (int)(long)args["x"]!, y = (int)(long)args["y"]!, w = (int)(long)args["width"]!, h = (int)(long)args["height"]!;
        try
        {
            var (W, H) = ImageSize(path);
            if (w < MinSide || h < MinSide) throw new ToolException($"the rectangle must be at least {MinSide} x {MinSide} pixels, got {w} x {h}");
            if (x < 0 || y < 0 || x + w > W || y + h > H) throw new ToolException($"the rectangle ({x}, {y}, {w} x {h}) is not inside the screenshot ({W} x {H} pixels)");
            if ((long)w * h > MaxPixels) throw new ToolException("the rectangle is too large; zoom into a smaller area");
            if ((long)W * H > MaxImagePixels) throw new ToolException($"DecompressionBombError: the screenshot has {(long)W * H} pixels");
            using var full = SKBitmap.Decode(path) ?? throw new ToolException("the screenshot could not be decoded");
            using var crop = new SKBitmap();
            if (!full.ExtractSubset(crop, new SKRectI(x, y, x + w, y + h))) throw new ToolException("the screenshot could not be cropped");
            double scale = Math.Min(maxPx / (double)Math.Max(w, h), 4.0);
            SKBitmap output = crop;
            SKBitmap? resized = null;
            if (scale > 1)
            {
                resized = crop.Resize(new SKImageInfo(Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale))), new SKSamplingOptions(SKCubicResampler.CatmullRom))
                          ?? throw new ToolException("the crop could not be enlarged");
                output = resized;
            }
            using (resized)
            using (var image = SKImage.FromBitmap(output))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                return (data.ToArray(), "image/png");
        }
        catch (ToolException) { throw; }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or OutOfMemoryException)       // a truncated or corrupt PNG: the model hears about it, the loop goes on
        {
            throw new ToolException($"the screenshot cannot be cropped ({e.GetType().Name}); read it as it is and call submit_reading", e);
        }
    }

    public static ImageBlock ImageBlock(byte[] data, string media) => new(media, Convert.ToBase64String(data));
}
