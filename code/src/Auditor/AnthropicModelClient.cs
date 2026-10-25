using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace Auditor;

/// <summary>
/// The adapter between <see cref="IModelClient"/> and the official Anthropic SDK (Messages API, tool use, image content, structured output). It only translates shapes and failures;
/// the retry policy, the budget and every decision about an answer live outside. The SDK's own retries are switched off (<c>MaxRetries = 0</c>) so the policy of <see cref="Llm.Send"/>
/// is applied exactly once. The shapes follow the SDK documentation; they have never been sent to a real endpoint from this repository (no API key was available), only to fakes in tests.
/// </summary>
public sealed class AnthropicModelClient : IModelClient
{
    private readonly AnthropicClient client = new() { MaxRetries = 0 };

    public ModelResponse Create(ModelRequest request, TimeSpan timeout)
    {
        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            System = request.System,
            Messages = request.Messages.Select(ToParam).ToList(),
        };
        if (request.Tools is { Count: > 0 }) parameters = parameters with { Tools = request.Tools.Select(t => new ToolUnion(ToTool(t))).ToList() };
        if (request.Effort is not null || request.JsonSchema is not null)
        {
            var output = new OutputConfig();
            if (request.Effort is not null) output = output with { Effort = request.Effort switch { "low" => Effort.Low, "medium" => Effort.Medium, "high" => Effort.High, _ => Effort.Low } };
            if (request.JsonSchema is not null)
                output = output with { Format = new JsonOutputFormat { Schema = request.JsonSchema.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)) } };
            parameters = parameters with { OutputConfig = output };
        }
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var message = client.Messages.Create(parameters, cts.Token).GetAwaiter().GetResult();
            return FromMessage(message);
        }
        catch (AnthropicApiException e) { throw Translate(e); }
        catch (OperationCanceledException e) { throw new ModelApiException("the request timed out", null, "Timeout", 0, e); }
        catch (HttpRequestException e) { throw new ModelApiException(e.Message, null, "Connection", 0, e); }
    }

    private static ModelApiException Translate(AnthropicApiException e) => new(e.Message, (int)e.StatusCode, "Other", 0, e);

    private static MessageParam ToParam(ChatMessage m)
    {
        var content = new MessageContentParam(m.Content.Select(ToBlock).ToList());
        return m.Role == "assistant" ? new AssistantMessageParam { Content = content } : new UserMessageParam { Content = content };
    }

    private static MediaType MediaTypeOf(string media) => media == "image/png" ? MediaType.ImagePng : MediaType.ImageJpeg;

    private static ImageBlockParam Image(ImageBlock i) => new() { Source = new Base64ImageSource { Data = i.Base64Data, MediaType = MediaTypeOf(i.MediaType) } };

    private static ContentBlockParam ToBlock(Auditor.ContentBlock b) => b switch
    {
        Auditor.TextBlock t => new TextBlockParam { Text = t.Text },
        ImageBlock i => Image(i),
        Auditor.ToolUseBlock u => new ToolUseBlockParam { ID = u.Id, Name = u.Name, Input = u.Input is Dictionary<string, object?> d ? d.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)) : new() },
        ToolResultBlock r => new ToolResultBlockParam
        {
            ToolUseID = r.ToolUseId,
            IsError = r.IsError,
            Content = new ToolResultBlockParamContent(r.Content.Select(c => c switch
            {
                Auditor.TextBlock t => new Block(new TextBlockParam { Text = t.Text }),
                ImageBlock i => new Block(Image(i)),
                _ => throw new InvalidOperationException("a tool result holds text or images only"),
            }).ToList()),
        },
        _ => throw new InvalidOperationException($"unsupported block {b.GetType().Name}"),
    };

    private static Tool ToTool(ToolSpec t)
    {
        var props = t.InputSchema.TryGetValue("properties", out var p) && p is Dictionary<string, object?> pd
            ? pd.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)) : new Dictionary<string, JsonElement>();
        var required = t.InputSchema.TryGetValue("required", out var r) && r is List<object?> rl ? rl.OfType<string>().ToList() : new List<string>();
        return new Tool { Name = t.Name, Description = t.Description, InputSchema = new InputSchema { Properties = props, Required = required } };
    }

    private static ModelResponse FromMessage(Message m)
    {
        var blocks = new List<Auditor.ContentBlock>();
        foreach (var block in m.Content)
        {
            if (block.TryPickText(out var text)) blocks.Add(new Auditor.TextBlock(text.Text));
            else if (block.TryPickToolUse(out var use))
                blocks.Add(new Auditor.ToolUseBlock(use.ID, use.Name, use.Input.ToDictionary(kv => kv.Key, kv => Py.ParseJson(kv.Value.GetRawText()), StringComparer.Ordinal)));
        }
        return new ModelResponse(m.StopReason?.Raw() ?? "", blocks, new ModelUsage(m.Usage.InputTokens, m.Usage.OutputTokens, m.Usage.CacheReadInputTokens ?? 0));
    }
}
