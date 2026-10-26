using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using WSA.Core.Interfaces;
using WSA.Core.Models;
using WSA.Core.Services;

namespace WSA.Infrastructure.Emulator;

public sealed class AvdLifecycleService : IAvdLifecycleService
{
    private static readonly Regex NameRegex = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EarlyExitWindow = TimeSpan.FromSeconds(8);

    private readonly ISdkLocator _sdk;
    private readonly IToolCommandFactory _tools;
    private readonly IProcessRunner _runner;

    public AvdLifecycleService(ISdkLocator sdk, IToolCommandFactory tools, IProcessRunner runner)
    {
        _sdk = sdk;
        _tools = tools;
        _runner = runner;
    }

    public string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a name for the virtual device.";
        }

        if (name.Length > 64)
        {
            return "The name must be 64 characters or fewer.";
        }

        return NameRegex.IsMatch(name)
            ? null
            : "Use only letters, digits, '.', '_' and '-' (no spaces); this is a limit of avdmanager.";
    }

    public Task<IReadOnlyList<SystemImageInfo>> ListSystemImagesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<SystemImageInfo>>(() =>
        {
            var root = _sdk.Last?.SdkRoot;
            if (root is null)
            {
                return Array.Empty<SystemImageInfo>();
            }

            var images = new List<SystemImageInfo>();
            var baseDir = Path.Combine(root, "system-images");
            if (!Directory.Exists(baseDir))
            {
                return images;
            }

            // Layout: system-images/<android-NN>/<tag>/<abi>/ ; the folder is an image only if it has content.
            foreach (var apiDir in Directory.EnumerateDirectories(baseDir))
            {
                foreach (var tagDir in Directory.EnumerateDirectories(apiDir))
                {
                    foreach (var abiDir in Directory.EnumerateDirectories(tagDir))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!File.Exists(Path.Combine(abiDir, "source.properties")) &&
                            !File.Exists(Path.Combine(abiDir, "system.img")))
                        {
                            continue;
                        }

                        var api = Path.GetFileName(apiDir);
                        var tag = Path.GetFileName(tagDir);
                        var abi = Path.GetFileName(abiDir);
                        images.Add(new SystemImageInfo($"system-images;{api};{tag};{abi}", api, tag, abi));
                    }
                }
            }

            return images.OrderByDescending(i => i.ApiLevel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Tag, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken);

    public async Task<IReadOnlyList<DeviceProfile>> ListDeviceProfilesAsync(CancellationToken cancellationToken = default)
    {
        var request = _tools.TryCreate(SdkTool.AvdManager, new[] { "list", "device", "-c" }, out var error);
        if (request is null)
        {
            throw new ToolUnavailableException(error ?? "avdmanager is unavailable.");
        }

        var result = await _runner.RunAsync(request, null, ShortTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new ToolFailedException(
                $"avdmanager list device failed: {Reason(result)}");
        }

        return result.StandardOutput.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.Contains(' '))
            .Select(l => new DeviceProfile(l))
            .ToList();
    }

    public async Task<OperationResult> CreateAsync(
        CreateAvdRequest request, IProgress<OutputLine> output, CancellationToken cancellationToken = default)
    {
        var nameError = ValidateName(request.Name);
        if (nameError is not null)
        {
            return new OperationResult(false, nameError);
        }

        if (string.IsNullOrWhiteSpace(request.SystemImagePackage) || string.IsNullOrWhiteSpace(request.DeviceId))
        {
            return new OperationResult(false, "Choose a system image and a device profile.");
        }

        var cmd = _tools.TryCreate(
            SdkTool.AvdManager,
            new[] { "create", "avd", "-n", request.Name, "-k", request.SystemImagePackage, "-d", request.DeviceId },
            out var error);
        if (cmd is null)
        {
            return new OperationResult(false, error ?? "avdmanager is unavailable.");
        }

        // avdmanager asks "Do you wish to create a custom hardware profile? [no]"; answer "no" explicitly.
        cmd = cmd with { StandardInput = "no" + Environment.NewLine };

        var result = await _runner.RunAsync(cmd, output, CreateTimeout, cancellationToken).ConfigureAwait(false);
        if (result.WasCancelled)
        {
            return new OperationResult(false, result.StartError ?? "Cancelled. A partially created AVD may remain; refresh the list.");
        }

        return result.Succeeded
            ? new OperationResult(true, $"Created virtual device '{request.Name}'.")
            : new OperationResult(false, $"avdmanager could not create '{request.Name}': {Reason(result)}");
    }

    public async Task<OperationResult> StartAsync(string avdName, CancellationToken cancellationToken = default)
    {
        var request = _tools.TryCreate(SdkTool.Emulator, new[] { "-avd", avdName }, out var error);
        if (request is null)
        {
            return new OperationResult(false, error ?? "emulator is unavailable.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = request.Executable,
            UseShellExecute = false,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
        };
        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (request.Environment is not null)
        {
            foreach (var (k, v) in request.Environment)
            {
                psi.Environment[k] = v;
            }
        }

        // Output is deliberately NOT redirected: the emulator must stay independent of WSA (it keeps running if WSA
        // exits) and a closed pipe could disturb it. Early failures are detected by watching for a quick exit.
        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null)
            {
                return new OperationResult(false, "The emulator process did not start.");
            }

            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(EarlyExitWindow);
            try
            {
                await process.WaitForExitAsync(window.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Still running after the window: treat as successfully launched.
                return new OperationResult(true,
                    $"Emulator for '{avdName}' launched. It is booting in its own window; the state turns to Running once adb sees it.");
            }

            return new OperationResult(false,
                $"The emulator exited immediately with code {process.ExitCode}. In WSA Terminal run `emulator -avd {avdName}` " +
                "to see its error output, and check the acceleration diagnostics on Runtime & SDK (virtualization / Windows Hypervisor Platform).");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new OperationResult(false, $"Could not start the emulator: {ex.Message}");
        }
        finally
        {
            // Dispose releases our handle only; it does not stop the emulator.
            process?.Dispose();
        }
    }

    public async Task<OperationResult> StopAsync(
        string serial, IProgress<OutputLine> output, CancellationToken cancellationToken = default)
    {
        if (!serial.StartsWith("emulator-", StringComparison.Ordinal))
        {
            return new OperationResult(false, $"'{serial}' is not an emulator serial; refusing to stop it.");
        }

        var request = _tools.TryCreate(SdkTool.Adb, new[] { "-s", serial, "emu", "kill" }, out var error);
        if (request is null)
        {
            return new OperationResult(false, error ?? "adb is unavailable.");
        }

        var result = await _runner.RunAsync(request, output, ShortTimeout, cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? new OperationResult(true, $"Stop signal sent to {serial}. The emulator window closes shortly.")
            : new OperationResult(false, $"adb could not stop {serial}: {Reason(result)}");
    }

    public async Task<OperationResult> DeleteAsync(
        string avdName, IProgress<OutputLine> output, CancellationToken cancellationToken = default)
    {
        var request = _tools.TryCreate(SdkTool.AvdManager, new[] { "delete", "avd", "-n", avdName }, out var error);
        if (request is null)
        {
            return new OperationResult(false, error ?? "avdmanager is unavailable.");
        }

        var result = await _runner.RunAsync(request, output, ShortTimeout, cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? new OperationResult(true, $"Deleted virtual device '{avdName}'.")
            : new OperationResult(false, $"avdmanager could not delete '{avdName}': {Reason(result)}");
    }

    private static string Reason(CommandResult result) =>
        result.StartError
        ?? ToolOutputParsers.FirstLine(result.StandardError)
        ?? ToolOutputParsers.FirstLine(result.StandardOutput)
        ?? $"exit code {result.ExitCode}";
}
