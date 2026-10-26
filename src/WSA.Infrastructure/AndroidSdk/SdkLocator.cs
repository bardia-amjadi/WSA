using WSA.Core.Interfaces;
using WSA.Core.Models;
using WSA.Core.Services;

namespace WSA.Infrastructure.AndroidSdk;

/// <summary>
/// Locates the Android SDK root and the four command-line tools, then probes each tool by actually running it.
/// Nothing is assumed: every status field is derived from the file system or from real tool output.
/// </summary>
public sealed class SdkLocator : ISdkLocator
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly ISettingsService _settings;
    private readonly IProcessRunner _runner;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SdkLocator(ISettingsService settings, IProcessRunner runner)
    {
        _settings = settings;
        _runner = runner;
    }

    public SdkStatus? Last { get; private set; }

    public event EventHandler<SdkStatus>? StatusChanged;

    public async Task<SdkStatus> DetectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var diagnostics = new List<string>();
            var (root, source) = ResolveRoot(diagnostics);

            var tools = new List<ToolStatus>();
            if (root is null)
            {
                foreach (var tool in AllTools)
                {
                    tools.Add(new ToolStatus(tool, DisplayName(tool), null, null, "Android SDK root not found."));
                }

                diagnostics.Add(
                    "No Android SDK was found. Install the Android command-line tools (see README → Android SDK setup) " +
                    "or set the SDK root in Settings.");
            }
            else
            {
                var env = SdkEnvironment(root);
                var probes = AllTools.Select(t => ProbeToolAsync(root, t, env, cancellationToken)).ToArray();
                tools.AddRange(await Task.WhenAll(probes).ConfigureAwait(false));

                foreach (var missing in tools.Where(t => t.Path is null))
                {
                    diagnostics.Add($"{missing.DisplayName} not found under '{root}'. {InstallHint(missing.Tool)}");
                }

                foreach (var broken in tools.Where(t => t.Path is not null && t.Problem is not null))
                {
                    diagnostics.Add($"{broken.DisplayName} found but not usable: {broken.Problem}");
                }

                var emulator = tools.First(t => t.Tool == SdkTool.Emulator);
                if (emulator.IsAvailable)
                {
                    diagnostics.Add(await AccelCheckAsync(emulator.Path!, env, cancellationToken).ConfigureAwait(false));
                }
            }

            var status = new SdkStatus(root, source, tools, diagnostics);
            Last = status;
            StatusChanged?.Invoke(this, status);
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static readonly SdkTool[] AllTools = { SdkTool.SdkManager, SdkTool.AvdManager, SdkTool.Emulator, SdkTool.Adb };

    internal static IReadOnlyDictionary<string, string> SdkEnvironment(string root) =>
        new Dictionary<string, string> { ["ANDROID_SDK_ROOT"] = root, ["ANDROID_HOME"] = root };

    private (string? Root, string Source) ResolveRoot(List<string> diagnostics)
    {
        var configured = _settings.Current.SdkRootOverride;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Directory.Exists(configured))
            {
                return (Path.GetFullPath(configured), "Settings");
            }

            // An explicit setting that is wrong must be visible, not silently replaced by another SDK.
            diagnostics.Add($"The SDK root configured in Settings does not exist: '{configured}'.");
            return (null, "Settings (invalid)");
        }

        foreach (var variable in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (Directory.Exists(value))
            {
                return (Path.GetFullPath(value), $"{variable} environment variable");
            }

            diagnostics.Add($"{variable} is set to '{value}', but that directory does not exist.");
        }

        foreach (var candidate in DefaultLocations())
        {
            if (Directory.Exists(candidate))
            {
                return (candidate, "Default install location");
            }
        }

        return (null, "Not found");
    }

    private static IEnumerable<string> DefaultLocations()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
            {
                yield return Path.Combine(local, "Android", "Sdk");
            }
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, "Android", "Sdk");
                yield return Path.Combine(home, "Library", "Android", "sdk");
            }
        }
    }

    internal static string? FindTool(string root, SdkTool tool)
    {
        var win = OperatingSystem.IsWindows();
        foreach (var candidate in CandidatePaths(root, tool, win))
        {
            if (File.Exists(candidate) && IsExecutable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidatePaths(string root, SdkTool tool, bool win)
    {
        switch (tool)
        {
            case SdkTool.Adb:
                yield return Path.Combine(root, "platform-tools", win ? "adb.exe" : "adb");
                break;
            case SdkTool.Emulator:
                yield return Path.Combine(root, "emulator", win ? "emulator.exe" : "emulator");
                break;
            default:
                var name = tool == SdkTool.SdkManager ? "sdkmanager" : "avdmanager";
                var file = win ? name + ".bat" : name;
                var cmdline = Path.Combine(root, "cmdline-tools");
                yield return Path.Combine(cmdline, "latest", "bin", file);
                if (Directory.Exists(cmdline))
                {
                    foreach (var dir in Directory.EnumerateDirectories(cmdline)
                                 .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                    {
                        yield return Path.Combine(dir, "bin", file);
                    }
                }

                // Legacy layout (deprecated SDK Tools package).
                yield return Path.Combine(root, "tools", "bin", file);
                break;
        }
    }

    private static bool IsExecutable(string path) =>
        OperatingSystem.IsWindows()
        || (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    private async Task<ToolStatus> ProbeToolAsync(
        string root, SdkTool tool, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        var display = DisplayName(tool);
        var path = FindTool(root, tool);
        if (path is null)
        {
            return new ToolStatus(tool, display, null, null, "Not installed.");
        }

        if (tool == SdkTool.AvdManager)
        {
            // avdmanager has no cheap version flag; sdkmanager's probe covers the shared Java requirement.
            return new ToolStatus(tool, display, path, null, null);
        }

        var args = tool switch
        {
            SdkTool.Adb => new[] { "version" },
            SdkTool.Emulator => new[] { "-version" },
            _ => new[] { "--version" },
        };

        var result = await _runner.RunAsync(
            new CommandRequest(path, args, Environment: env), null, ProbeTimeout, ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            var reason = result.StartError
                         ?? ToolOutputParsers.FirstLine(result.StandardError)
                         ?? $"exited with code {result.ExitCode}";
            if (tool == SdkTool.SdkManager)
            {
                reason += " (sdkmanager and avdmanager need a Java runtime; install JDK 17 or newer and make sure JAVA_HOME or PATH points to it.)";
            }

            return new ToolStatus(tool, display, path, null, reason);
        }

        var lines = result.StandardOutput.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var version = tool == SdkTool.Emulator
            ? lines.FirstOrDefault(l => l.Contains("emulator version", StringComparison.OrdinalIgnoreCase)) ?? lines.FirstOrDefault()
            : lines.FirstOrDefault();

        return new ToolStatus(tool, display, path, version, null);
    }

    private async Task<string> AccelCheckAsync(string emulatorPath, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            new CommandRequest(emulatorPath, new[] { "-accel-check" }, Environment: env),
            null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);

        if (result.StartError is not null)
        {
            return $"Hardware acceleration check could not run: {result.StartError}";
        }

        var text = string.Join(" ",
            (result.StandardOutput + "\n" + result.StandardError)
            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(3));

        return result.ExitCode == 0
            ? $"Hardware acceleration check passed: {text}"
            : $"Hardware acceleration check FAILED (exit code {result.ExitCode}): {text} " +
              "Enable virtualization in firmware and the Windows Hypervisor Platform feature; see README → Troubleshooting.";
    }

    internal static string DisplayName(SdkTool tool) => tool switch
    {
        SdkTool.SdkManager => "sdkmanager",
        SdkTool.AvdManager => "avdmanager",
        SdkTool.Emulator => "emulator",
        _ => "adb",
    };

    private static string InstallHint(SdkTool tool) => tool switch
    {
        SdkTool.Adb => "Install the 'Platform-Tools' package: sdkmanager \"platform-tools\".",
        SdkTool.Emulator => "Install the 'Emulator' package: sdkmanager \"emulator\".",
        _ => "Install 'Android SDK Command-line Tools (latest)' from Android Studio's SDK Manager or from https://developer.android.com/studio#command-line-tools-only (extract to cmdline-tools\\latest).",
    };
}
