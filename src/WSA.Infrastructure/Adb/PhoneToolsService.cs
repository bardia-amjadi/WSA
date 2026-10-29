using System.Globalization;
using System.Text.RegularExpressions;
using WSA.Core.Interfaces;
using WSA.Core.Models;
using WSA.Core.Services;

namespace WSA.Infrastructure.Adb;

public sealed class PhoneToolsService : IPhoneToolsService
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan Transfer = TimeSpan.FromMinutes(30);

    private static readonly Regex SerialRegex = new(@"^[A-Za-z0-9._:\-\[\]]{1,80}$", RegexOptions.Compiled);
    private static readonly Regex PackageRegex = new(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$", RegexOptions.Compiled);
    private static readonly Regex LsRegex = new(
        @"^(?<perm>[\-dlcbps][rwxsStT\-]{9}[.+]?)\s+(?:\d+\s+)?(?<owner>\S+)\s+(?<group>\S+)\s+(?:(?<size>\d+)\s+)?(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2})\s+(?<name>.+)$",
        RegexOptions.Compiled);

    private static readonly string[] WritableRoots = { "/sdcard", "/storage", "/data/local/tmp" };

    private static readonly Dictionary<string, string> SettingsPages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["main"] = "android.settings.SETTINGS",
        ["wifi"] = "android.settings.WIFI_SETTINGS",
        ["bluetooth"] = "android.settings.BLUETOOTH_SETTINGS",
        ["display"] = "android.settings.DISPLAY_SETTINGS",
        ["sound"] = "android.settings.SOUND_SETTINGS",
        ["apps"] = "android.settings.APPLICATION_SETTINGS",
        ["battery"] = "android.settings.BATTERY_SAVER_SETTINGS",
        ["developer"] = "android.settings.APPLICATION_DEVELOPMENT_SETTINGS",
        ["about"] = "android.settings.DEVICE_INFO_SETTINGS",
    };

    private readonly IToolCommandFactory _tools;
    private readonly IProcessRunner _runner;

    public PhoneToolsService(IToolCommandFactory tools, IProcessRunner runner)
    {
        _tools = tools;
        _runner = runner;
    }

    // ---- Files ----------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<DeviceFile>> ListDirectoryAsync(string serial, string path, CancellationToken ct = default)
    {
        var dir = ValidatePath(path);
        var shellPath = Quote(dir.EndsWith('/') ? dir : dir + "/");
        var result = await AdbAsync(serial, new[] { "shell", "ls", "-lA", shellPath }, null, Short, ct).ConfigureAwait(false);
        if (!result.Succeeded && result.StandardOutput.Trim().Length == 0)
        {
            throw new ToolFailedException(FriendlyShell(result, $"Could not open {dir}"));
        }

        var list = new List<DeviceFile>();
        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var m = LsRegex.Match(raw.TrimEnd('\r').Trim());
            if (!m.Success)
            {
                continue;
            }

            var perm = m.Groups["perm"].Value;
            var name = m.Groups["name"].Value;
            var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow > 0)
            {
                name = name[..arrow];
            }

            var size = long.TryParse(m.Groups["size"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
            list.Add(new DeviceFile(name, perm[0] == 'd' || (perm[0] == 'l' && !name.Contains('.')), perm[0] == 'l', size,
                $"{m.Groups["date"].Value} {m.Groups["time"].Value}", perm));
        }

        return list.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task PullAsync(string serial, string remotePath, string localFolder, IProgress<OutputLine>? output, CancellationToken ct = default)
    {
        var remote = ValidatePath(remotePath);
        if (!Directory.Exists(localFolder))
        {
            throw new ToolFailedException($"The folder does not exist on this computer: {localFolder}");
        }

        var result = await AdbAsync(serial, new[] { "pull", remote, localFolder }, output, Transfer, ct).ConfigureAwait(false);
        Require(result, "Download failed");
    }

    public async Task PushAsync(string serial, string localPath, string remoteFolder, IProgress<OutputLine>? output, CancellationToken ct = default)
    {
        var remote = RequireWritable(ValidatePath(remoteFolder), allowRoot: true);
        if (!File.Exists(localPath) && !Directory.Exists(localPath))
        {
            throw new ToolFailedException($"Not found on this computer: {localPath}");
        }

        var result = await AdbAsync(serial, new[] { "push", localPath, remote.TrimEnd('/') + "/" }, output, Transfer, ct).ConfigureAwait(false);
        Require(result, "Upload failed");
    }

    public async Task DeleteAsync(string serial, string remotePath, CancellationToken ct = default)
    {
        var path = RequireWritable(ValidatePath(remotePath), allowRoot: false);
        var result = await AdbAsync(serial, new[] { "shell", "rm", "-rf", Quote(path) }, null, Short, ct).ConfigureAwait(false);
        Require(result, "Delete failed");
    }

    public async Task MakeFolderAsync(string serial, string remotePath, CancellationToken ct = default)
    {
        var path = RequireWritable(ValidatePath(remotePath), allowRoot: false);
        var result = await AdbAsync(serial, new[] { "shell", "mkdir", "-p", Quote(path) }, null, Short, ct).ConfigureAwait(false);
        Require(result, "Could not create the folder");
    }

    public async Task RenameAsync(string serial, string remotePath, string newName, CancellationToken ct = default)
    {
        var path = RequireWritable(ValidatePath(remotePath), allowRoot: false);
        if (newName.Contains('/') || newName == "." || newName == "..")
        {
            throw new ToolFailedException("A name cannot contain '/'.");
        }

        var parent = path[..path.LastIndexOf('/')];
        var target = ValidatePath(parent + "/" + newName);
        var result = await AdbAsync(serial, new[] { "shell", "mv", Quote(path), Quote(target) }, null, Short, ct).ConfigureAwait(false);
        Require(result, "Rename failed");
    }

    // ---- Terminal -------------------------------------------------------------------------------------------

    public async Task<(string Output, int? ExitCode)> RunShellAsync(
        string serial, IReadOnlyList<string> args, IProgress<OutputLine>? output, CancellationToken ct = default)
    {
        if (args.Count == 0)
        {
            throw new ToolFailedException("Type a command to run on the phone.");
        }

        var full = new List<string> { "shell" };
        full.AddRange(args);
        var result = await AdbAsync(serial, full, output, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        if (result.StartError is not null)
        {
            throw new ToolFailedException(result.StartError);
        }

        return ((result.StandardOutput + result.StandardError).TrimEnd(), result.ExitCode);
    }

    // ---- Apps -----------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<InstalledApp>> ListAppsAsync(string serial, bool includeSystem, CancellationToken ct = default)
    {
        var user = await AdbAsync(serial, new[] { "shell", "pm", "list", "packages", "-3" }, null, Short, ct).ConfigureAwait(false);
        Require(user, "Could not list apps");
        var apps = ParsePackages(user.StandardOutput, system: false);

        if (includeSystem)
        {
            var sys = await AdbAsync(serial, new[] { "shell", "pm", "list", "packages", "-s" }, null, Short, ct).ConfigureAwait(false);
            Require(sys, "Could not list system apps");
            apps = apps.Concat(ParsePackages(sys.StandardOutput, system: true)).ToList();
        }

        return apps.OrderBy(a => a.Package, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task LaunchAppAsync(string serial, string package, CancellationToken ct = default)
    {
        var result = await AdbAsync(serial,
            new[] { "shell", "monkey", "-p", RequirePackage(package), "-c", "android.intent.category.LAUNCHER", "1" }, null, Short, ct).ConfigureAwait(false);
        Require(result, "Could not open the app");
        if ((result.StandardOutput + result.StandardError).Contains("No activities found", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolFailedException("This app has no launcher screen to open.");
        }
    }

    public async Task ForceStopAsync(string serial, string package, CancellationToken ct = default) =>
        Require(await AdbAsync(serial, new[] { "shell", "am", "force-stop", RequirePackage(package) }, null, Short, ct).ConfigureAwait(false), "Could not stop the app");

    public async Task ClearDataAsync(string serial, string package, CancellationToken ct = default)
    {
        var result = await AdbAsync(serial, new[] { "shell", "pm", "clear", RequirePackage(package) }, null, Short, ct).ConfigureAwait(false);
        Require(result, "Could not clear the app's data");
        if (!result.StandardOutput.Contains("Success", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolFailedException($"The phone refused: {ToolOutputParsers.FirstLine(result.StandardOutput + result.StandardError)}");
        }
    }

    public async Task UninstallAsync(string serial, string package, CancellationToken ct = default)
    {
        var result = await AdbAsync(serial, new[] { "uninstall", RequirePackage(package) }, null, Short, ct).ConfigureAwait(false);
        Require(result, "Could not uninstall the app");
        if (!result.StandardOutput.Contains("Success", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolFailedException($"The phone refused: {ToolOutputParsers.FirstLine(result.StandardOutput + result.StandardError)}");
        }
    }

    // ---- Status and tools -----------------------------------------------------------------------------------

    public async Task<DeviceStatus> GetStatusAsync(string serial, CancellationToken ct = default)
    {
        var battery = AdbAsync(serial, new[] { "shell", "dumpsys", "battery" }, null, Short, ct);
        var df = AdbAsync(serial, new[] { "shell", "df", "/data" }, null, Short, ct);
        var ip = AdbAsync(serial, new[] { "shell", "ip", "-f", "inet", "addr", "show", "wlan0" }, null, Short, ct);
        await Task.WhenAll(battery, df, ip).ConfigureAwait(false);

        string? level = null;
        bool? charging = null;
        foreach (var line in battery.Result.StandardOutput.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith("level:", StringComparison.Ordinal))
            {
                level = line["level:".Length..].Trim();
            }
            else if (line.StartsWith("status:", StringComparison.Ordinal))
            {
                charging = line.EndsWith("2", StringComparison.Ordinal) || line.EndsWith("5", StringComparison.Ordinal);
            }
        }

        string? free = null, total = null;
        var dfLine = df.Result.StandardOutput.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Skip(1).FirstOrDefault();
        if (dfLine is not null)
        {
            var parts = dfLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4 && long.TryParse(parts[1], out var blocks) && long.TryParse(parts[3], out var avail))
            {
                total = FormatKb(blocks);
                free = FormatKb(avail);
            }
        }

        var address = Regex.Match(ip.Result.StandardOutput, @"inet\s+(\d{1,3}(?:\.\d{1,3}){3})/");
        return new DeviceStatus(level, charging, address.Success ? address.Groups[1].Value : null, free, total);
    }

    public async Task SetToggleAsync(string serial, string name, bool on, CancellationToken ct = default)
    {
        string[] args = name switch
        {
            "wifi" => new[] { "shell", "svc", "wifi", on ? "enable" : "disable" },
            "data" => new[] { "shell", "svc", "data", on ? "enable" : "disable" },
            "stayawake" => new[] { "shell", "svc", "power", "stayon", on ? "true" : "false" },
            "touches" => new[] { "shell", "settings", "put", "system", "show_touches", on ? "1" : "0" },
            _ => throw new ToolFailedException($"Unknown switch '{name}'."),
        };
        Require(await AdbAsync(serial, args, null, Short, ct).ConfigureAwait(false), "The phone did not accept the change");
    }

    public async Task SetBrightnessAsync(string serial, int percent, CancellationToken ct = default)
    {
        var value = Math.Clamp(percent, 1, 100) * 255 / 100;
        Require(await AdbAsync(serial, new[] { "shell", "settings", "put", "system", "screen_brightness_mode", "0" }, null, Short, ct).ConfigureAwait(false), "Could not turn off auto-brightness");
        Require(await AdbAsync(serial, new[] { "shell", "settings", "put", "system", "screen_brightness", value.ToString(CultureInfo.InvariantCulture) }, null, Short, ct).ConfigureAwait(false), "Could not set brightness");
    }

    public async Task<string> RecordScreenAsync(string serial, int seconds, string localFolder, CancellationToken ct = default)
    {
        seconds = Math.Clamp(seconds, 1, 180);
        if (!Directory.Exists(localFolder))
        {
            throw new ToolFailedException($"The folder does not exist on this computer: {localFolder}");
        }

        const string remote = "/sdcard/wsa-recording.mp4";
        var record = await AdbAsync(serial,
            new[] { "shell", "screenrecord", "--time-limit", seconds.ToString(CultureInfo.InvariantCulture), remote },
            null, TimeSpan.FromSeconds(seconds + 60), ct).ConfigureAwait(false);
        Require(record, "Screen recording failed");

        var local = Path.Combine(localFolder, $"phone-recording-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        var pull = await AdbAsync(serial, new[] { "pull", remote, local }, null, Transfer, ct).ConfigureAwait(false);
        Require(pull, "Could not copy the recording");
        await AdbAsync(serial, new[] { "shell", "rm", Quote(remote) }, null, Short, ct).ConfigureAwait(false);
        return local;
    }

    public async Task RebootAsync(string serial, string mode, CancellationToken ct = default)
    {
        var args = mode switch
        {
            "system" => new[] { "reboot" },
            "recovery" => new[] { "reboot", "recovery" },
            "bootloader" => new[] { "reboot", "bootloader" },
            _ => throw new ToolFailedException($"Unknown reboot mode '{mode}'."),
        };
        Require(await AdbAsync(serial, args, null, Short, ct).ConfigureAwait(false), "Reboot failed");
    }

    public async Task OpenAndroidSettingsAsync(string serial, string page, CancellationToken ct = default)
    {
        if (!SettingsPages.TryGetValue(page, out var action))
        {
            throw new ToolFailedException($"Unknown settings page '{page}'.");
        }

        Require(await AdbAsync(serial, new[] { "shell", "am", "start", "-a", action }, null, Short, ct).ConfigureAwait(false), "Could not open Settings on the phone");
    }

    public async Task<string?> EnableWifiAdbAsync(string serial, CancellationToken ct = default)
    {
        var result = await AdbAsync(serial, new[] { "tcpip", "5555" }, null, Short, ct).ConfigureAwait(false);
        Require(result, "Could not switch adb to Wi-Fi");
        return (await GetStatusAsync(serial, ct).ConfigureAwait(false)).WifiAddress;
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static List<InstalledApp> ParsePackages(string text, bool system) =>
        text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("package:", StringComparison.Ordinal))
            .Select(l => l["package:".Length..].Trim())
            .Where(p => PackageRegex.IsMatch(p))
            .Select(p => new InstalledApp(p, system))
            .ToList();

    private static string FormatKb(long kb) => kb switch
    {
        < 1024L * 1024 => $"{kb / 1024.0:F0} MB",
        _ => $"{kb / (1024.0 * 1024):F1} GB",
    };

    private static string RequirePackage(string package) =>
        PackageRegex.IsMatch(package) ? package : throw new ToolFailedException($"'{package}' is not a valid app id.");

    /// <summary>Accepts an absolute phone path made of letters, digits and a small set of punctuation. No quotes, no "..".</summary>
    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path[0] != '/' || path.Length > 500)
        {
            throw new ToolFailedException("A phone path must start with '/'.");
        }

        foreach (var c in path)
        {
            if (!(char.IsLetterOrDigit(c) || " ._-+@()[],~#&=/".Contains(c)))
            {
                throw new ToolFailedException($"The path contains a character that is not allowed here: '{c}'.");
            }
        }

        if (path.Split('/').Any(seg => seg == ".."))
        {
            throw new ToolFailedException("'..' is not allowed in a path.");
        }

        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static string Quote(string path) => "'" + path + "'";

    /// <summary>Changes are only allowed in the phone's shared storage and /data/local/tmp.</summary>
    private static string RequireWritable(string path, bool allowRoot)
    {
        foreach (var root in WritableRoots)
        {
            if (path.Equals(root, StringComparison.Ordinal))
            {
                return allowRoot ? path : throw new ToolFailedException($"Refusing to change {root} itself.");
            }

            if (path.StartsWith(root + "/", StringComparison.Ordinal))
            {
                return path;
            }
        }

        throw new ToolFailedException("For safety WSA only changes files inside /sdcard, /storage and /data/local/tmp.");
    }

    private CommandRequest Create(string serial, IReadOnlyList<string> args)
    {
        if (!SerialRegex.IsMatch(serial))
        {
            throw new ToolFailedException($"'{serial}' is not a valid device serial.");
        }

        var full = new List<string> { "-s", serial };
        full.AddRange(args);
        return _tools.TryCreate(SdkTool.Adb, full, out var error)
               ?? throw new ToolUnavailableException(error ?? "adb is unavailable. Install it from the Phone page.");
    }

    private Task<CommandResult> AdbAsync(
        string serial, IReadOnlyList<string> args, IProgress<OutputLine>? output, TimeSpan timeout, CancellationToken ct) =>
        _runner.RunAsync(Create(serial, args), output, timeout, ct);

    private static void Require(CommandResult result, string what)
    {
        if (!result.Succeeded)
        {
            throw new ToolFailedException(FriendlyShell(result, what));
        }
    }

    private static string FriendlyShell(CommandResult r, string what)
    {
        if (r.StartError is not null)
        {
            return r.StartError;
        }

        var text = r.StandardError + "\n" + r.StandardOutput;
        if (text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "The phone has not authorized this computer. Unlock it and tap Allow on the USB debugging prompt.";
        }

        if (text.Contains("no devices", StringComparison.OrdinalIgnoreCase) || text.Contains("not found", StringComparison.OrdinalIgnoreCase) && text.Contains("device", StringComparison.OrdinalIgnoreCase))
        {
            return "The phone is not connected any more.";
        }

        var line = ToolOutputParsers.FirstLine(text);
        return line is null ? $"{what} (adb exit code {r.ExitCode})." : $"{what}: {line}";
    }
}
