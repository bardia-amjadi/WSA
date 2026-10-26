using WSA.Core.Interfaces;
using WSA.Core.Models;
using WSA.Core.Services;

namespace WSA.Infrastructure.Adb;

public sealed class AdbService : IAdbService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly IToolCommandFactory _tools;
    private readonly IProcessRunner _runner;

    public AdbService(IToolCommandFactory tools, IProcessRunner runner)
    {
        _tools = tools;
        _runner = runner;
    }

    public async Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAdbAsync(new[] { "devices", "-l" }, cancellationToken).ConfigureAwait(false);
        return ToolOutputParsers.ParseAdbDevices(result.StandardOutput);
    }

    public async Task<string?> GetEmulatorAvdNameAsync(string serial, CancellationToken cancellationToken = default)
    {
        // Query failures for a single device (offline, unauthorized) mean "unknown", never a guessed name.
        var request = _tools.TryCreate(SdkTool.Adb, new[] { "-s", serial, "emu", "avd", "name" }, out var error);
        if (request is null)
        {
            throw new ToolUnavailableException(error ?? "adb is unavailable.");
        }

        var result = await _runner.RunAsync(request, null, Timeout, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? ToolOutputParsers.ParseEmuAvdName(result.StandardOutput) : null;
    }

    private async Task<CommandResult> RunAdbAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var request = _tools.TryCreate(SdkTool.Adb, args, out var error);
        if (request is null)
        {
            throw new ToolUnavailableException(error ?? "adb is unavailable.");
        }

        var result = await _runner.RunAsync(request, null, Timeout, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var reason = result.StartError
                         ?? ToolOutputParsers.FirstLine(result.StandardError)
                         ?? $"exit code {result.ExitCode}";
            throw new ToolFailedException($"adb {string.Join(' ', args)} failed: {reason}");
        }

        return result;
    }
}
