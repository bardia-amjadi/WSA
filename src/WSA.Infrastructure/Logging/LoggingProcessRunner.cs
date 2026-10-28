using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.Infrastructure.Logging;

/// <summary>Wraps another runner and records each command line, its exit code and its output while Debug mode is on.</summary>
public sealed class LoggingProcessRunner : IProcessRunner
{
    private const int MaxLoggedChars = 4000;
    private readonly IProcessRunner _inner;
    private readonly IAppLog _log;

    public LoggingProcessRunner(IProcessRunner inner, IAppLog log)
    {
        _inner = inner;
        _log = log;
    }

    public async Task<CommandResult> RunAsync(
        CommandRequest request, IProgress<OutputLine>? output = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        Begin(request);
        var result = await _inner.RunAsync(request, output, timeout, cancellationToken).ConfigureAwait(false);
        if (result.StartError is not null)
        {
            _log.Error("process", $"{Path.GetFileName(request.Executable)} did not start: {result.StartError}");
        }
        else if (_log.IsDebugEnabled)
        {
            _log.Debug("process",
                $"exit {(result.ExitCode?.ToString() ?? "-")}{(result.WasCancelled ? " (cancelled)" : string.Empty)} after {result.Duration.TotalSeconds:F1}s");
            Dump("stdout", result.StandardOutput);
            Dump("stderr", result.StandardError);
        }

        return result;
    }

    public async Task<BinaryResult> RunBinaryAsync(
        CommandRequest request, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        Begin(request);
        var result = await _inner.RunBinaryAsync(request, timeout, cancellationToken).ConfigureAwait(false);
        if (result.StartError is not null)
        {
            _log.Error("process", $"{Path.GetFileName(request.Executable)} did not start: {result.StartError}");
        }
        else
        {
            _log.Debug("process", $"exit {(result.ExitCode?.ToString() ?? "-")}, {result.Output.Length} bytes of binary output");
        }

        return result;
    }

    private void Begin(CommandRequest request)
    {
        if (!_log.IsDebugEnabled)
        {
            return;
        }

        var args = string.Join(' ', request.Arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        _log.Debug("process", $"run {request.Executable} {args}");
        if (request.Environment is not null &&
            request.Environment.TryGetValue("JAVA_HOME", out var javaHome))
        {
            _log.Debug("process", $"  JAVA_HOME={javaHome}");
        }
    }

    private void Dump(string label, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var trimmed = text.Length > MaxLoggedChars ? text[..MaxLoggedChars] + " ...(truncated)" : text;
        _log.Debug("process", $"  {label}: {trimmed.Trim().Replace("\r", string.Empty).Replace("\n", " | ")}");
    }
}
