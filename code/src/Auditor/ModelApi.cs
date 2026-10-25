namespace Auditor;

// A small, provider-neutral shape of one chat request and its answer. The audit talks to a model only through <see cref="IModelClient"/>; the Anthropic SDK sits behind one adapter
// (<see cref="AnthropicModelClient"/>) and every test uses a fake, so no key and no network are ever needed to test the model path.

/// <summary>One block of a message: text, an image, a tool call of the model, or the result of a tool call.</summary>
public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

public sealed record ImageBlock(string MediaType, string Base64Data) : ContentBlock;

/// <summary>A tool call of the model. <see cref="Input"/> is the decoded JSON the model wrote (an object when well-formed).</summary>
public sealed record ToolUseBlock(string Id, string Name, object? Input) : ContentBlock;

public sealed record ToolResultBlock(string ToolUseId, IReadOnlyList<ContentBlock> Content, bool IsError = false) : ContentBlock;

public sealed record ChatMessage(string Role, IReadOnlyList<ContentBlock> Content);

public sealed record ToolSpec(string Name, string Description, Dictionary<string, object?> InputSchema);

/// <param name="Effort">"low" | "medium" | "high" | null (the server default)</param>
/// <param name="JsonSchema">when set, the answer must be a JSON document of this schema (single-shot reader)</param>
public sealed record ModelRequest(string Model, int MaxTokens, string System, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ToolSpec>? Tools = null, string? Effort = null,
                                  Dictionary<string, object?>? JsonSchema = null);

public sealed record ModelUsage(long InputTokens, long OutputTokens, long CacheReadInputTokens);

/// <param name="StopReason">end_turn | tool_use | max_tokens | refusal | ...</param>
public sealed record ModelResponse(string StopReason, IReadOnlyList<ContentBlock> Content, ModelUsage Usage);

public interface IModelClient
{
    /// <summary>One request. Throws <see cref="ModelApiException"/> for a failed request.</summary>
    ModelResponse Create(ModelRequest request, TimeSpan timeout);
}

/// <summary>A failed request: an HTTP status when there was a response, otherwise the kind of transport problem.</summary>
public sealed class ModelApiException : Exception
{
    public ModelApiException(string message, int? statusCode = null, string kind = "Other", double retryAfterSeconds = 0, Exception? inner = null) : base(message, inner)
    {
        StatusCode = statusCode; Kind = kind; RetryAfterSeconds = retryAfterSeconds;
    }

    public int? StatusCode { get; }
    /// <summary>RateLimit | Timeout | Connection | InternalServer | Overloaded | Other (consulted only when there is no status code)</summary>
    public string Kind { get; }
    public double RetryAfterSeconds { get; }
}
