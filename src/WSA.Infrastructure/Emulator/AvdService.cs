using WSA.Core.Interfaces;
using WSA.Core.Models;
using WSA.Core.Services;

namespace WSA.Infrastructure.Emulator;

/// <summary>
/// Lists AVDs from <c>avdmanager list avd</c> (rich details) or, if avdmanager is unusable, from
/// <c>emulator -list-avds</c> (names only). Running state is derived from real adb emulator queries.
/// </summary>
public sealed class AvdService : IAvdService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly IToolCommandFactory _tools;
    private readonly IProcessRunner _runner;
    private readonly IAdbService _adb;
    private readonly ISdkLocator _locator;

    public AvdService(IToolCommandFactory tools, IProcessRunner runner, IAdbService adb, ISdkLocator locator)
    {
        _tools = tools;
        _runner = runner;
        _adb = adb;
        _locator = locator;
    }

    public async Task<IReadOnlyList<AvdInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var avds = await ListDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        var running = await FindRunningAsync(cancellationToken).ConfigureAwait(false);

        return avds
            .Select(a => running.TryGetValue(a.Name, out var serial) ? a with { IsRunning = true, RunningSerial = serial } : a)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<AvdInfo>> ListDefinitionsAsync(CancellationToken ct)
    {
        var avdManagerOk = _locator.Last?.Get(SdkTool.AvdManager).IsAvailable == true;
        if (avdManagerOk)
        {
            var request = _tools.TryCreate(SdkTool.AvdManager, new[] { "list", "avd" }, out _);
            if (request is not null)
            {
                var result = await _runner.RunAsync(request, null, Timeout, ct).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    return ToolOutputParsers.ParseAvdManagerList(result.StandardOutput);
                }

                if (_locator.Last?.Get(SdkTool.Emulator).IsAvailable != true)
                {
                    throw new ToolFailedException(
                        $"avdmanager list avd failed: {result.StartError ?? ToolOutputParsers.FirstLine(result.StandardError) ?? $"exit code {result.ExitCode}"}");
                }
            }
        }

        var emu = _tools.TryCreate(SdkTool.Emulator, new[] { "-list-avds" }, out var error);
        if (emu is null)
        {
            throw new ToolUnavailableException(
                "Neither avdmanager nor emulator is usable, so virtual devices cannot be listed. " + error);
        }

        var emuResult = await _runner.RunAsync(emu, null, Timeout, ct).ConfigureAwait(false);
        if (!emuResult.Succeeded)
        {
            throw new ToolFailedException(
                $"emulator -list-avds failed: {emuResult.StartError ?? ToolOutputParsers.FirstLine(emuResult.StandardError) ?? $"exit code {emuResult.ExitCode}"}");
        }

        return ToolOutputParsers.ParseListAvds(emuResult.StandardOutput)
            .Select(n => new AvdInfo(n, null, null, null, null, null, false, null))
            .ToList();
    }

    private async Task<Dictionary<string, string>> FindRunningAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_locator.Last?.Get(SdkTool.Adb).IsAvailable != true)
        {
            return map;
        }

        var devices = await _adb.ListDevicesAsync(ct).ConfigureAwait(false);
        foreach (var device in devices.Where(d =>
                     d.Serial.StartsWith("emulator-", StringComparison.Ordinal) &&
                     d.State.Equals("device", StringComparison.OrdinalIgnoreCase)))
        {
            var name = await _adb.GetEmulatorAvdNameAsync(device.Serial, ct).ConfigureAwait(false);
            if (name is not null)
            {
                map[name] = device.Serial;
            }
        }

        return map;
    }
}
