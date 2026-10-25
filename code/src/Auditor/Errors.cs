namespace Auditor;

/// <summary>No credentials / SDK / network: the caller must fall back to its rule-based result.
/// <see cref="Fatal"/> marks a failure that no retry or other image can fix (rejected credentials, a spent token budget): the LLM then stops sending for the rest of the run.</summary>
public class LlmUnavailableException : Exception
{
    public LlmUnavailableException(string message, bool fatal = false, Exception? inner = null) : base(message, inner) { Fatal = fatal; }

    public bool Fatal { get; }
}

/// <summary>The model's answer does not fit the schema, or the run of a loop ended without a usable answer.</summary>
public class SchemaException : Exception
{
    public SchemaException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>A malformed value in the data (the .NET counterpart of the ValueError / KeyError / TypeError / AttributeError the loaders recover from).</summary>
public sealed class DataException : Exception
{
    public DataException(string message) : base(message) { }
}

/// <summary>A file the run cannot do without is missing or has no usable header. Everything else degrades row by row.</summary>
public sealed class DatasetException : Exception
{
    public DatasetException(string message, Exception? inner = null) : base(message, inner) { }
}
