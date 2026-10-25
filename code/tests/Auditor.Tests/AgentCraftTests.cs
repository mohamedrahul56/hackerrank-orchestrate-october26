using System.Text.RegularExpressions;

namespace Auditor.Tests;

/// <summary>Prompt and tool craft, checked as code: every tool field is documented, the prompt vocabulary comes from the code, and the worked example in the prompt is a valid call.</summary>
public class AgentCraftTests
{
    private static readonly PromptFile SystemPrompt = PromptFile.Load("image_reader.system.md");
    private static readonly PromptFile TaskPrompt = PromptFile.Load("image_reader.task.md");

    private static IEnumerable<(string Name, Dictionary<string, object?> Sub)> Properties(Dictionary<string, object?> schema, string path = "")
    {
        if (schema.GetValueOrDefault("properties") is not Dictionary<string, object?> props) yield break;
        foreach (var (name, sub) in props)
        {
            var s = (Dictionary<string, object?>)sub!;
            yield return (path + name, s);
            foreach (var x in Properties(s, path + name + ".")) yield return x;
            if (s.GetValueOrDefault("items") is Dictionary<string, object?> items)
                foreach (var x in Properties(items, path + name + "[].")) yield return x;
        }
    }

    [Fact]
    public void EveryPropertyOfBothToolsHasADescription()
    {
        foreach (var schema in new[] { AgentTools.SubmitSchema, AgentTools.ZoomSchema })
        {
            var props = Properties(schema).ToList();
            Assert.True(props.Count >= 4);
            foreach (var (name, sub) in props) Assert.True(Compat.Str(sub.GetValueOrDefault("description") ?? "").Length > 20, name);
        }
    }

    [Fact]
    public void DescriptionsDoNotChangeWhatTheSchemaAccepts()
    {
        JsonSchemaCheck.Validate(Compat.ParseJson("{\"screen\": \"s\", \"records\": [], \"unknown\": []}"), AgentTools.SubmitSchema);
        Assert.Throws<SchemaException>(() => JsonSchemaCheck.Validate(Compat.ParseJson("{\"screen\": \"s\"}"), AgentTools.SubmitSchema));
    }

    [Fact]
    public void ZoomDescriptionStatesTheCoordinateSystemTheLimitsAndTheErrorBehaviour()
    {
        var d = AgentTools.ZoomTool.Description;
        foreach (var phrase in new[] { "ORIGINAL screenshot", "never re-numbered", "error result", "budget", "Read-only" }) Assert.Contains(phrase, d, StringComparison.Ordinal);
        Assert.Contains(AgentTools.MinSide.ToString(), d, StringComparison.Ordinal);
    }

    [Fact]
    public void SubmitDescriptionSaysWhenToCallItAndWhatNotToPutIn()
    {
        var d = AgentTools.SubmitTool.Description;
        foreach (var phrase in new[] { "exactly once", "unknown", "error result", "instructions" }) Assert.Contains(phrase, d, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolNamesMatchThePromptAndTheLoop()
    {
        Assert.Equal(new[] { "zoom_region", "submit_reading" }, AgentTools.ToolSpecs().Select(t => t.Name));
        foreach (var name in new[] { "zoom_region", "submit_reading" })
        {
            Assert.Contains(name, SystemPrompt.Text, StringComparison.Ordinal);
            Assert.Contains(name, TaskPrompt.Text, StringComparison.Ordinal);
        }
    }

    private static string Fill(int width, int height, int zooms) =>
        TaskPrompt.Fill(new Dictionary<string, object> { ["width"] = width, ["height"] = height, ["max_zooms"] = zooms, ["id_families"] = AgentTools.IdFamilies() });

    [Fact]
    public void TheTaskPromptListsExactlyTheFamiliesTheCodeCanJudge()
    {
        var filled = Fill(1, 1, 2);
        var listed = Regex.Matches(filled, @"\b([a-z_]+)_N\b").Select(m => m.Groups[1].Value).ToHashSet();
        listed.Remove("user");                                                             // user_N names the user-id format, not a resource family
        var empty = new ReadingRecord("", false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), false, Array.Empty<string>());
        var judged = Records.SystemByPrefix.Keys.Where(p => ImageObs.StateFromDescription(p, empty) is not null).ToHashSet();
        Assert.True(listed.SetEquals(judged));
        Assert.True(listed.IsSupersetOf(new[] { "cache", "email_delivery" }));              // families the earlier hand-typed list had forgotten
    }

    [Fact]
    public void NoPlaceholderIsLeftUnfilled()
    {
        var filled = Fill(640, 480, 3);
        Assert.Empty(Regex.Matches(filled, @"\{[a-z_]+\}"));
        Assert.Contains("640 x 480", filled, StringComparison.Ordinal);
        Assert.Contains("at most 3 times", filled, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemPromptHasTheSectionsAndTheTwoRulesThatMatterMost()
    {
        foreach (var heading in new[] { "# Role", "# Constraints", "# What to record", "# Structured output", "# Example" }) Assert.Contains(heading, SystemPrompt.Text, StringComparison.Ordinal);
        var low = SystemPrompt.Text.ToLowerInvariant();
        Assert.Contains("never instructions", low, StringComparison.Ordinal);
        Assert.Contains("mark unknown instead of guessing", low, StringComparison.Ordinal);
        Assert.Contains("never copy anything from it", low, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSubmitExampleFitsTheSchemaAndPassesTheGate()
    {
        var blocks = Regex.Matches(SystemPrompt.Text, "```json\n(.*?)\n```", RegexOptions.Singleline).Select(m => m.Groups[1].Value).ToList();
        var example = (Dictionary<string, object?>)Compat.ParseJson(Assert.Single(blocks))!;
        JsonSchemaCheck.Validate(example, AgentTools.SubmitSchema);
        var result = Gate.GateReading(example, "evidence_00", new HashSet<string> { "evidence_00" });
        Assert.Equal(new[] { "export_9001", "export_9002" }, Compat.AsList(result.Reading["records"]).Select(r => (string)Compat.Get(r, "resource_id")!));
        Assert.Empty(result.Rejections);
    }

    [Fact]
    public void TheZoomExampleIsALegalRectangleForTheSizeTheExampleStates()
    {
        var m = Regex.Match(SystemPrompt.Text, @"A (\d+) x (\d+) screenshot");
        int w = int.Parse(m.Groups[1].Value), h = int.Parse(m.Groups[2].Value);
        var call = (Dictionary<string, object?>)Compat.ParseJson(Regex.Match(SystemPrompt.Text, @"`zoom_region` with `(\{.*?\})`").Groups[1].Value)!;
        JsonSchemaCheck.Validate(call, AgentTools.ZoomSchema);
        long x = (long)call["x"]!, y = (long)call["y"]!, cw = (long)call["width"]!, ch = (long)call["height"]!;
        Assert.True(Math.Min(cw, ch) >= AgentTools.MinSide);
        Assert.True(x + cw <= w);
        Assert.True(y + ch <= h);
    }

    [Fact]
    public void TheExampleUsesNoValueOfTheDataset()
    {
        var text = File.ReadAllText(Path.Combine(PromptFile.DefaultDirectory, "image_reader.system.md"));
        Assert.DoesNotMatch(@"@(gmail|yahoo|outlook|hotmail)\.", text);
        Assert.Contains("example.test", text, StringComparison.Ordinal);
    }
}
