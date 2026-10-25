namespace Auditor.Tests;

/// <summary>The two tools of the screenshot reader: schemas, bounds and determinism.</summary>
public class AgentToolSpecsTests
{
    [Fact]
    public void EachToolHasANameAPreciseDescriptionAndASchema()
    {
        foreach (var t in new[] { AgentTools.ZoomTool, AgentTools.SubmitTool })
        {
            Assert.True(t.Description.Length > 120);
            Assert.Equal("object", t.InputSchema["type"]);
            Assert.Equal(false, t.InputSchema["additionalProperties"]);
        }
        Assert.Equal("zoom_region", AgentTools.ZoomTool.Name);
        Assert.Equal("submit_reading", AgentTools.SubmitTool.Name);
        Assert.Contains(AgentTools.MinSide.ToString(), AgentTools.ZoomTool.Description, StringComparison.Ordinal);
        Assert.False(((Dictionary<string, object?>)AgentTools.ZoomSchema["properties"]!).ContainsKey("file"), "the zoom tool cannot name any file: it only ever sees the screenshot being read");
    }

    [Fact]
    public void SubmitSchemaIsTheReadingSchemaPlusUnknown()
    {
        var submit = ((Dictionary<string, object?>)AgentTools.SubmitSchema["properties"]!).Keys.ToHashSet();
        var reading = ((Dictionary<string, object?>)ImageObs.Schema["properties"]!).Keys.ToHashSet();
        Assert.Equal(new[] { "unknown" }, submit.Except(reading));
        Assert.Equal(new[] { "screen", "records" }, ((List<object?>)AgentTools.SubmitSchema["required"]!).Cast<string>());
        JsonSchemaCheck.Validate(Compat.ParseJson("{\"screen\": \"s\", \"records\": []}"), AgentTools.SubmitSchema);
        JsonSchemaCheck.Validate(Compat.ParseJson("{\"screen\": \"s\", \"records\": [], \"unknown\": [{\"record_index\": 0, \"field\": \"emails\", \"reason\": \"r\"}]}"), AgentTools.SubmitSchema);
        Assert.Throws<SchemaException>(() => JsonSchemaCheck.Validate(Compat.ParseJson("{\"screen\": \"s\", \"records\": [], \"unknown\": [{\"record_index\": \"0\", \"field\": \"emails\", \"reason\": \"r\"}]}"), AgentTools.SubmitSchema));
    }

    [Fact]
    public void TheDigestIsStable()
    {
        var a = AgentTools.ToolsDigest();
        Assert.Equal(a, AgentTools.ToolsDigest());
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    [Fact]
    public void BothToolsAreOffered() => Assert.Equal(new[] { "zoom_region", "submit_reading" }, AgentTools.ToolSpecs().Select(t => t.Name));

    [Fact]
    public void TheIdFamiliesAreDerivedFromTheJudgingCode()
    {
        var families = AgentTools.IdFamilies();
        foreach (var p in new[] { "account_N", "search_N", "data_flow_N", "backup_N" }) Assert.Contains(p, families, StringComparison.Ordinal);
    }
}

public class ZoomTests : IDisposable
{
    private readonly TempDir tmp = new();
    private readonly string png;

    public ZoomTests() => png = TestPng.Make(tmp.File("a.png"));
    public void Dispose() => tmp.Dispose();

    private static Dictionary<string, object?> Args(long x, long y, long w, long h) => new() { ["x"] = x, ["y"] = y, ["width"] = w, ["height"] = h };

    [Fact]
    public void ImageSizeComesFromTheHeader()
    {
        Assert.Equal((400, 300), AgentTools.ImageSize(png));
        var bad = tmp.Write("b.png", "not a png at all, just text");
        Assert.Throws<ToolException>(() => AgentTools.ImageSize(bad));
    }

    [Fact]
    public void ACropIsEnlargedWithinTheLimitAndIsAPng()
    {
        var (data, media) = AgentTools.ZoomRegion(png, Args(10, 20, 100, 50), maxPx: 600);
        Assert.Equal("image/png", media);
        Assert.Equal((400, 200), TestPng.Decode(data));                              // scale min(600/100, 4.0) = 4
        var (data2, _) = AgentTools.ZoomRegion(png, Args(0, 0, 400, 300), maxPx: 300);
        var (w, h) = TestPng.Decode(data2);
        Assert.True(Math.Max(w, h) <= 400);
    }

    [Fact]
    public void ZoomIsDeterministic()
    {
        var a = Args(5, 5, 120, 80);
        Assert.Equal(AgentTools.ZoomRegion(png, a).Png, AgentTools.ZoomRegion(png, a).Png);
    }

    [Theory]
    [InlineData(-1, 0, 50, 50)] [InlineData(0, 0, 31, 50)] [InlineData(380, 0, 50, 50)] [InlineData(0, 290, 50, 50)] [InlineData(0, 0, 4000, 4000)]
    public void Bounds(long x, long y, long w, long h) => Assert.Throws<ToolException>(() => AgentTools.ZoomRegion(png, Args(x, y, w, h)));

    [Fact]
    public void AHugeAreaIsRefusedBeforeAnyWork()
    {
        var big = TestPng.Make(tmp.File("big.png"), 3000, 2000);
        Assert.Throws<ToolException>(() => AgentTools.ZoomRegion(big, Args(0, 0, 3000, 2000)));
    }
}
