using System.Text.RegularExpressions;
using WSA.Core.Interfaces;
using WSA.Core.Models;
using WSA.Core.Services;

namespace WSA.Infrastructure.Adb;

public sealed class PhoneService : IPhoneService
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Install = TimeSpan.FromMinutes(10);

    private static readonly Regex SerialRegex = new(@"^[A-Za-z0-9._:\-\[\]]{1,80}$", RegexOptions.Compiled);
    private static readonly Regex HostPortRegex = new(@"^[A-Za-z0-9.\-]{1,100}:\d{1,5}$", RegexOptions.Compiled);
    private static readonly Regex CodeRegex = new(@"^\d{6}$", RegexOptions.Compiled);
    private static readonly Regex PropRegex = new(@"^\[(?<k>[^\]]+)\]:\s*\[(?<v>.*)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex TextRegex = new(@"^[A-Za-z0-9 .,_@\-]{1,200}$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BACK"] = "KEYCODE_BACK",
        ["HOME"] = "KEYCODE_HOME",
        ["APP_SWITCH"] = "KEYCODE_APP_SWITCH",
        ["POWER"] = "KEYCODE_POWER",
        ["VOLUME_UP"] = "KEYCODE_VOLUME_UP",
        ["VOLUME_DOWN"] = "KEYCODE_VOLUME_DOWN",
        ["ENTER"] = "KEYCODE_ENTER",
        ["DEL"] = "KEYCODE_DEL",
    };

    private readonly IToolCommandFactory _tools;
    private readonly IProcessRunner _runner;

    public PhoneService(IToolCommandFactory tools, IProcessRunner runner)
    {
        _tools = tools;
        _runner = runner;
    }

    public async Task<PhoneInfo> GetInfoAsync(string serial, CancellationToken cancellationToken = default)
    {
        var props = AdbAsync(serial, new[] { "shell", "getprop" }, null, Short, cancellationToken);
        var battery = AdbAsync(serial, new[] { "shell", "dumpsys", "battery" }, null, Short, cancellationToken);
        var wm = AdbAsync(serial, new[] { "shell", "wm", "size" }, null, Short, cancellationToken);
        await Task.WhenAll(props, battery, wm).ConfigureAwait(false);

        var propResult = props.Result;
        if (!propResult.Succeeded)
        {
            throw new ToolFailedException(Friendly(propResult));
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in propResult.StandardOutput.Split('\n'))
        {
            var m = PropRegex.Match(line.Trim());
            if (m.Success)
            {
                map[m.Groups["k"].Value] = m.Groups["v"].Value;
            }
        }

        string Get(string key) => map.TryGetValue(key, out var v) && v.Length > 0 ? v : "unknown";

        string? level = null;
        if (battery.Result.Succeeded)
        {
            var m = Regex.Match(battery.Result.StandardOutput, @"^\s*level:\s*(\d+)", RegexOptions.Multiline);
            level = m.Success ? m.Groups[1].Value + "%" : null;
        }

        string? resolution = null;
        if (wm.Result.Succeeded)
        {
            var m = Regex.Match(wm.Result.StandardOutput, @"Physical size:\s*(\d+x\d+)");
            resolution = m.Success ? m.Groups[1].Value : null;
        }

        return new PhoneInfo(
            Get("ro.product.model"), Get("ro.product.manufacturer"),
            Get("ro.build.version.release"), Get("ro.build.version.sdk"),
            map.TryGetValue("ro.product.cpu.abi", out var abi) ? abi : null,
            level, resolution);
    }

    public async Task<byte[]> CaptureScreenAsync(string serial, CancellationToken cancellationToken = default)
    {
        var request = Create(serial, new[] { "exec-out", "screencap", "-p" });
        var result = await _runner.RunBinaryAsync(request, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        if (result.WasCancelled && result.StartError is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (!result.Succeeded)
        {
            throw new ToolFailedException(
                result.StartError ?? FriendlyText(result.StandardError, $"adb exited with code {result.ExitCode}"));
        }

        var png = result.Output;
        var isPng = png.Length > 8 && png[0] == 0x89 && png[1] == (byte)'P' && png[2] == (byte)'N' && png[3] == (byte)'G';
        if (!isPng)
        {
            throw new ToolFailedException(
                "The phone did not return a screenshot. The foreground app may block screen capture, or the device is asleep.");
        }

        return png;
    }

    public async Task TapAsync(string serial, int x, int y, CancellationToken cancellationToken = default) =>
        await InputAsync(serial, new[] { "input", "tap", Math.Max(0, x).ToString(), Math.Max(0, y).ToString() }, cancellationToken)
            .ConfigureAwait(false);

    public async Task SwipeAsync(string serial, int x1, int y1, int x2, int y2, int durationMs, CancellationToken cancellationToken = default) =>
        await InputAsync(serial, new[]
        {
            "input", "swipe",
            Math.Max(0, x1).ToString(), Math.Max(0, y1).ToString(),
            Math.Max(0, x2).ToString(), Math.Max(0, y2).ToString(),
            Math.Clamp(durationMs, 50, 3000).ToString(),
        }, cancellationToken).ConfigureAwait(false);

    public async Task KeyAsync(string serial, string keyName, CancellationToken cancellationToken = default)
    {
        if (!Keys.TryGetValue(keyName, out var code))
        {
            throw new ToolFailedException($"Key '{keyName}' is not supported.");
        }

        await InputAsync(serial, new[] { "input", "keyevent", code }, cancellationToken).ConfigureAwait(false);
    }

    public async Task TypeTextAsync(string serial, string text, CancellationToken cancellationToken = default)
    {
        if (!TextRegex.IsMatch(text))
        {
            throw new ToolFailedException(
                "Only plain ASCII text can be typed (letters, digits, space and . , _ @ -), up to 200 characters. " +
                "Other characters cannot be sent through adb reliably.");
        }

        // adb joins arguments into one remote shell line, and 'input text' expects %s for a space.
        await InputAsync(serial, new[] { "input", "text", text.Replace(" ", "%s") }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> InstallApkAsync(
        string serial, string apkPath, IProgress<OutputLine> output, CancellationToken cancellationToken = default)
    {
        if (!apkPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) || !File.Exists(apkPath))
        {
            return new OperationResult(false, $"Not an existing .apk file: {apkPath}");
        }

        var result = await AdbAsync(serial, new[] { "install", "-r", apkPath }, output, Install, cancellationToken)
            .ConfigureAwait(false);
        var success = result.Succeeded && result.StandardOutput.Contains("Success", StringComparison.Ordinal);
        if (success)
        {
            return new OperationResult(true, $"Installed {Path.GetFileName(apkPath)}.");
        }

        // adb prints "Performing Streamed Install" first; the useful line is the Failure/error one.
        var detail = (result.StandardError + "\n" + result.StandardOutput)
            .Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Contains("Failure", StringComparison.OrdinalIgnoreCase)
                                 || l.Contains("INSTALL_", StringComparison.Ordinal)
                                 || l.StartsWith("error", StringComparison.OrdinalIgnoreCase));
        return new OperationResult(false, $"Install failed: {detail ?? Friendly(result)}");
    }

    public async Task<OperationResult> PairAsync(string hostPort, string code, CancellationToken cancellationToken = default)
    {
        if (!HostPortRegex.IsMatch(hostPort))
        {
            return new OperationResult(false, "Enter the pairing address as IP:port, for example 192.168.1.20:37215.");
        }

        if (!CodeRegex.IsMatch(code))
        {
            return new OperationResult(false, "The pairing code is exactly 6 digits.");
        }

        var result = await AdbAsync(null, new[] { "pair", hostPort, code }, null, Short, cancellationToken).ConfigureAwait(false);
        var text = result.StandardOutput + result.StandardError;
        return result.Succeeded && text.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase)
            ? new OperationResult(true, "Paired with the phone.")
            : new OperationResult(false, $"Pairing failed: {Friendly(result)}");
    }

    public async Task<OperationResult> ConnectAsync(string hostPort, CancellationToken cancellationToken = default)
    {
        if (!HostPortRegex.IsMatch(hostPort))
        {
            return new OperationResult(false, "Enter the connect address as IP:port, for example 192.168.1.20:41234.");
        }

        var result = await AdbAsync(null, new[] { "connect", hostPort }, null, Short, cancellationToken).ConfigureAwait(false);
        var text = (result.StandardOutput + result.StandardError).Trim();

        // adb connect exits 0 even when it failed, so the printed text decides.
        var ok = result.Succeeded
                 && text.Contains("connected to", StringComparison.OrdinalIgnoreCase)
                 && !text.Contains("failed", StringComparison.OrdinalIgnoreCase)
                 && !text.Contains("cannot", StringComparison.OrdinalIgnoreCase);
        return ok
            ? new OperationResult(true, text)
            : new OperationResult(false, $"Could not connect to {hostPort}: {(text.Length > 0 ? text : Friendly(result))}");
    }

    public async Task<OperationResult> DisconnectAsync(string serial, CancellationToken cancellationToken = default)
    {
        if (!serial.Contains(':'))
        {
            return new OperationResult(false, "Only wireless (IP:port) connections can be disconnected here; unplug USB devices instead.");
        }

        if (!HostPortRegex.IsMatch(serial))
        {
            return new OperationResult(false, $"'{serial}' is not a valid wireless address.");
        }

        var result = await AdbAsync(null, new[] { "disconnect", serial }, null, Short, cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? new OperationResult(true, $"Disconnected {serial}.")
            : new OperationResult(false, $"Could not disconnect: {Friendly(result)}");
    }

    private async Task InputAsync(string serial, IReadOnlyList<string> inputArgs, CancellationToken ct)
    {
        var args = new List<string> { "shell" };
        args.AddRange(inputArgs);
        var result = await AdbAsync(serial, args, null, Short, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new ToolFailedException(Friendly(result));
        }
    }

    private CommandRequest Create(string? serial, IReadOnlyList<string> args)
    {
        var full = new List<string>();
        if (serial is not null)
        {
            if (!SerialRegex.IsMatch(serial))
            {
                throw new ToolFailedException($"'{serial}' is not a valid device serial.");
            }

            full.Add("-s");
            full.Add(serial);
        }

        full.AddRange(args);
        return _tools.TryCreate(SdkTool.Adb, full, out var error)
               ?? throw new ToolUnavailableException(error ?? "adb is unavailable. Install it from the Phone page.");
    }

    private Task<CommandResult> AdbAsync(
        string? serial, IReadOnlyList<string> args, IProgress<OutputLine>? output, TimeSpan timeout, CancellationToken ct) =>
        _runner.RunAsync(Create(serial, args), output, timeout, ct);

    private static string Friendly(CommandResult r) =>
        r.StartError ?? FriendlyText(r.StandardError + "\n" + r.StandardOutput, $"adb exited with code {r.ExitCode}");

    private static string FriendlyText(string text, string fallback)
    {
        if (text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "The phone has not authorized this computer. Unlock the phone and tap Allow on the USB debugging prompt.";
        }

        if (text.Contains("offline", StringComparison.OrdinalIgnoreCase))
        {
            return "The device is offline. Reconnect the cable, or turn USB debugging off and on again.";
        }

        if (text.Contains("no devices", StringComparison.OrdinalIgnoreCase) || text.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return "The device is not connected any more.";
        }

        return ToolOutputParsers.FirstLine(text) ?? fallback;
    }
}
