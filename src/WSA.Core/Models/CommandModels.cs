namespace WSA.Core.Models;

/// <summary>A fully resolved request to launch one executable directly (never through a shell).</summary>
public sealed record CommandRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? StandardInput = null);

/// <summary>Which stream a line of process output came from.</summary>
public enum OutputKind
{
    StandardOutput,
    StandardError,
    Info,
}

public readonly record struct OutputLine(OutputKind Kind, string Text);

/// <summary>Result of a command whose standard output is binary (for example a PNG from <c>adb exec-out screencap -p</c>).</summary>
public sealed record BinaryResult(
    int? ExitCode,
    byte[] Output,
    string StandardError,
    string? StartError,
    bool WasCancelled)
{
    public bool Succeeded => StartError is null && !WasCancelled && ExitCode == 0;
}

/// <summary>Outcome of a finished (or failed-to-start) command.</summary>
public sealed record CommandResult(
    string Executable,
    IReadOnlyList<string> Arguments,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool WasCancelled,
    string? StartError)
{
    public bool Succeeded => StartError is null && !WasCancelled && ExitCode == 0;
}
