namespace Auditor.Tests;

/// <summary>Failure behaviour of the agent loop that the first test matrix did not cover: damaged images, a rejected key across several screenshots, the token budget, spend that is
/// logged even when the loop dies, size limits on a submission. Fake client only: no key, no network, 0 calls, 0 tokens.</summary>
public class AgentDamagedImageTests : AgentLoopBase
{
    [Fact]
    public void ATruncatedPngIsAToolErrorAndTheLoopGoesOn()
    {
        File.WriteAllBytes(Png, File.ReadAllBytes(Png).Take(300).ToArray());
        var rd = Reader(new object[] { Msg(Use("zoom_region", Zoom)), Submit() });
        Assert.Equal("account_5762", Py.Get(Py.AsList(Read(rd)["records"])[0], "resource_id"));
        var result = (ToolResultBlock)Fake.Calls[1].Messages[2].Content[0];
        Assert.True(result.IsError);
        Assert.Contains("cannot be cropped", ResultText(result), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotAPngEndsAsNotRead()
    {
        File.WriteAllText(Png, "not a png at all, just some text");
        var rd = Reader(new object[] { Submit() });
        Assert.Throws<AgentFailure>(() => Read(rd));
        Assert.Empty(Fake.Calls);                              // no request is wasted on a file the loop cannot even measure
    }
}

public class AFailedRunStopsCheaplyTests : AgentLoopBase
{
    private (AgentImageReader Reader, string[] Paths) ThreeImages(IEnumerable<object> script, long maxRunTokens = 0)
    {
        var paths = Enumerable.Range(0, 3).Select(i => TestPng.Make(Dir.File($"s{i}.png"), 300 + 10 * i, 200)).ToArray();
        Fake = new FakeModelClient(script);
        Llm = new Llm(cacheDir: Dir.File("cache"), usagePath: Dir.File("usage.jsonl"), client: Fake, sleep: Sleeps.Add, clock: () => 100.0, maxRunTokens: maxRunTokens);
        return (new AgentImageReader(Llm, paths.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => $"evidence_{x.i}"), new AgentLimits(), () => Now), paths);
    }

    [Fact]
    public void RejectedCredentialsCostOneRequestForTheWholeRun()
    {
        var (rd, paths) = ThreeImages(new object[] { FakeModelClient.Status(401) });
        foreach (var p in paths) Assert.Throws<LlmUnavailableException>(() => rd.Read(p, "", "", new(), ""));
        Assert.Single(Fake.Calls);
    }

    [Fact]
    public void TheTokenBudgetLeavesTheRemainingScreenshotsNotRead()
    {
        var (rd, paths) = ThreeImages(new object[] { Submit() }, maxRunTokens: 100);
        rd.Read(paths[0], "", "", new(), "");                              // 120 tokens: over the budget of 100
        foreach (var p in paths.Skip(1))
        {
            var e = Assert.Throws<LlmUnavailableException>(() => rd.Read(p, "", "", new(), ""));
            Assert.Contains("token budget", e.Message, StringComparison.Ordinal);
        }
        Assert.Single(Fake.Calls);
        Assert.Equal(100L, Usage().Sum(r => r.TryGetValue("input_tokens", out var v) && v is long l ? l : 0));
    }

    [Fact]
    public void TokensSpentBeforeTheModelBecameUnavailableAreStillLogged()
    {
        var rd = Reader(new object[] { Msg(Use("zoom_region", Zoom)) }.Concat(Enumerable.Repeat((object)new ModelApiException("t", null, "Timeout"), 4)));
        Assert.Throws<LlmUnavailableException>(() => Read(rd));
        var row = Usage().First(r => Py.Get(r, "kind") as string == "agent_image");
        Assert.Equal(new object[] { 1L, 100L, 20L, 1L }, new[] { row["calls"], row["input_tokens"], row["output_tokens"], row["zooms"] });
        Assert.Contains("model unavailable during the loop", (string)row["error"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentFailureCarriesTheTraceAsAPlainObject()
    {
        Assert.Equal(0, new AgentFailure("x").Trace.ApiCalls);
        Assert.Equal(2, new AgentFailure("x", new AgentTrace { ApiCalls = 2 }).Trace.ApiCalls);
    }
}

public class AgentSizeLimitsTests : AgentLoopBase
{
    private ToolResultBlock BadThenGood(Dictionary<string, object?> bad)
    {
        var rd = Reader(new object[] { Submit(1, bad), Submit(2) });
        Read(rd);
        return (ToolResultBlock)Fake.Calls[1].Messages[2].Content[0];
    }

    [Fact]
    public void AListLongerThanTheLimitIsSentBackOnce()
    {
        var result = BadThenGood(Record(over: ("emails", Enumerable.Range(0, 21).Select(i => (object?)$"a{i}@example.test").ToList())));
        Assert.True(result.IsError);
        Assert.Contains("more than 20 items", ResultText(result), StringComparison.Ordinal);
    }

    [Fact]
    public void AVeryLongStringIsSentBackOnce() =>
        Assert.Contains("longer than", ResultText(BadThenGood(Record(over: ("aliases", L(new string('x', 500)))))), StringComparison.Ordinal);

    [Fact]
    public void ALongScreenTitleIsNotAnErrorTheGateCutsIt()
    {
        var d = new Dictionary<string, object?> { ["screen"] = new string('t', 600), ["records"] = L(Record()) };
        var rd = Reader(new object[] { Msg(Use("submit_reading", d)) });
        Read(rd);
        Assert.Single(Fake.Calls);
    }

    [Fact]
    public void TheLimitsAreInTheSchemaTheModelReceives()
    {
        var records = (Dictionary<string, object?>)((Dictionary<string, object?>)AgentTools.SubmitSchema["properties"]!)["records"]!;
        Assert.Equal(100L, records["maxItems"]);
        var itemProps = (Dictionary<string, object?>)((Dictionary<string, object?>)records["items"]!)["properties"]!;
        Assert.Equal(20L, ((Dictionary<string, object?>)itemProps["emails"]!)["maxItems"]);
        Assert.Equal((long)AgentTools.MaxText, ((Dictionary<string, object?>)((Dictionary<string, object?>)itemProps["aliases"]!)["items"]!)["maxLength"]);
        Assert.False(((Dictionary<string, object?>)((Dictionary<string, object?>)AgentTools.SubmitSchema["properties"]!)["screen"]!).ContainsKey("maxLength"));
    }
}
