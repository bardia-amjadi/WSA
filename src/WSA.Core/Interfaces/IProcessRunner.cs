using WSA.Core.Models;

namespace WSA.Core.Interfaces;

/// <summary>
/// Launches executables directly (no shell) and captures their output asynchronously.
/// Implementations must never block the caller's thread.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs the process to completion. Output lines are reported to <paramref name="output"/> as they arrive.
    /// Cancellation or timeout terminates the process tree that this call started, and nothing else.
    /// A process that cannot start is reported through <see cref="CommandResult.StartError"/>, not thrown.
    /// </summary>
    Task<CommandResult> RunAsync(
        CommandRequest request,
        IProgress<OutputLine>? output = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>Like <see cref="RunAsync"/> but captures standard output as raw bytes (capped at 32 MB). Standard error is text.</summary>
    Task<BinaryResult> RunBinaryAsync(
        CommandRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
