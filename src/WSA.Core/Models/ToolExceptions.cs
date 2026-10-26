namespace WSA.Core.Models;

/// <summary>Thrown when a required SDK tool cannot be located or run. The message is user-facing and actionable.</summary>
public sealed class ToolUnavailableException : Exception
{
    public ToolUnavailableException(string message) : base(message) { }
}

/// <summary>Thrown when an SDK tool ran but reported failure. The message contains the tool's own error text.</summary>
public sealed class ToolFailedException : Exception
{
    public ToolFailedException(string message) : base(message) { }
}
