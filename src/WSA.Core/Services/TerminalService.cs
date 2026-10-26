using System.Diagnostics;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.Core.Services;

/// <summary>What happened when a line was submitted to WSA Terminal.</summary>
public sealed record TerminalOutcome(
    bool ClearRequested,
    int? ExitCode,
    bool Cancelled,
    bool Failed,
    TimeSpan Duration);

/// <summary>
/// Executes WSA Terminal input. Built-ins read real SDK state; everything else launches an SDK executable directly
/// through <see cref="IProcessRunner"/>. Output is streamed line by line so long-running commands such as
/// <c>adb logcat</c> stay responsive and can be cancelled.
/// </summary>
public sealed class TerminalService
{
    private readonly ISdkLocator _sdk;
    private readonly IToolCommandFactory _tools;
    private readonly IProcessRunner _runner;
    private readonly IAvdService _avds;

    public TerminalService(ISdkLocator sdk, IToolCommandFactory tools, IProcessRunner runner, IAvdService avds)
    {
        _sdk = sdk;
        _tools = tools;
        _runner = runner;
        _avds = avds;
    }

    public async Task<TerminalOutcome> ExecuteAsync(
        string input, IProgress<OutputLine> output, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var action = TerminalCommandParser.Parse(input);

        TerminalOutcome Done(int? code = null, bool failed = false, bool clear = false, bool cancelled = false) =>
            new(clear, code, cancelled, failed, sw.Elapsed);

        void Error(string text) => output.Report(new OutputLine(OutputKind.StandardError, text));

        try
        {
            switch (action)
            {
                case EmptyAction:
                    return Done();

                case InvalidAction invalid:
                    Error(invalid.Message);
                    return Done(failed: true);

                case BuiltInAction builtIn:
                    return await RunBuiltInAsync(builtIn, output, sw, cancellationToken).ConfigureAwait(false);

                case ToolAction tool:
                {
                    var request = _tools.TryCreate(tool.Tool, tool.Args, out var error);
                    if (request is null)
                    {
                        Error(error ?? "Tool unavailable.");
                        return Done(failed: true);
                    }

                    return await RunRequestAsync(request, output, sw, cancellationToken).ConfigureAwait(false);
                }

                case SdkRelativeAction rel:
                {
                    var request = _tools.TryCreateRelative(rel.RelativePath, rel.Args, out var error);
                    if (request is null)
                    {
                        Error(error ?? "Path rejected.");
                        return Done(failed: true);
                    }

                    return await RunRequestAsync(request, output, sw, cancellationToken).ConfigureAwait(false);
                }

                default:
                    Error("Unsupported command.");
                    return Done(failed: true);
            }
        }
        catch (OperationCanceledException)
        {
            return Done(cancelled: true);
        }
        catch (Exception ex) when (ex is ToolUnavailableException or ToolFailedException)
        {
            Error(ex.Message);
            return Done(failed: true);
        }
    }

    private async Task<TerminalOutcome> RunRequestAsync(
        CommandRequest request, IProgress<OutputLine> output, Stopwatch sw, CancellationToken ct)
    {
        var result = await _runner.RunAsync(request, output, null, ct).ConfigureAwait(false);
        if (result.StartError is not null)
        {
            output.Report(new OutputLine(OutputKind.StandardError, result.StartError));
        }

        return new TerminalOutcome(false, result.ExitCode, result.WasCancelled, result.StartError is not null, sw.Elapsed);
    }

    private async Task<TerminalOutcome> RunBuiltInAsync(
        BuiltInAction action, IProgress<OutputLine> output, Stopwatch sw, CancellationToken ct)
    {
        void Info(string text) => output.Report(new OutputLine(OutputKind.Info, text));

        switch (action.Name)
        {
            case "help":
                foreach (var line in HelpText)
                {
                    Info(line);
                }

                return new TerminalOutcome(false, 0, false, false, sw.Elapsed);

            case "clear":
                return new TerminalOutcome(true, 0, false, false, sw.Elapsed);

            case "refresh":
                Info("Detecting Android SDK and tools...");
                await _sdk.DetectAsync(ct).ConfigureAwait(false);
                ReportSdk(output);
                return new TerminalOutcome(false, 0, false, false, sw.Elapsed);

            case "sdk":
            case "versions":
                if (_sdk.Last is null)
                {
                    await _sdk.DetectAsync(ct).ConfigureAwait(false);
                }

                ReportSdk(output, versionsOnly: action.Name == "versions");
                return new TerminalOutcome(false, 0, false, false, sw.Elapsed);

            case "devices":
            {
                var request = _tools.TryCreate(SdkTool.Adb, new[] { "devices", "-l" }, out var error);
                if (request is null)
                {
                    output.Report(new OutputLine(OutputKind.StandardError, error ?? "adb unavailable."));
                    return new TerminalOutcome(false, null, false, true, sw.Elapsed);
                }

                return await RunRequestAsync(request, output, sw, ct).ConfigureAwait(false);
            }

            case "avds":
            {
                var list = await _avds.ListAsync(ct).ConfigureAwait(false);
                if (list.Count == 0)
                {
                    Info("No virtual devices found. Create one with avdmanager (see 'help') or in Android Studio.");
                }

                foreach (var avd in list)
                {
                    var state = avd.IsRunning ? $"running ({avd.RunningSerial})" : "stopped";
                    var extra = string.Join(", ", new[] { avd.Device, avd.Abi }.Where(s => !string.IsNullOrEmpty(s)));
                    Info($"{avd.Name,-32} {state,-24} {extra}");
                }

                return new TerminalOutcome(false, 0, false, false, sw.Elapsed);
            }

            default:
                output.Report(new OutputLine(OutputKind.StandardError, $"Unhandled built-in '{action.Name}'."));
                return new TerminalOutcome(false, null, false, true, sw.Elapsed);
        }
    }

    private void ReportSdk(IProgress<OutputLine> output, bool versionsOnly = false)
    {
        void Info(string text) => output.Report(new OutputLine(OutputKind.Info, text));
        var status = _sdk.Last;
        if (status is null)
        {
            Info("SDK detection has not run.");
            return;
        }

        if (!versionsOnly)
        {
            Info($"SDK root : {status.SdkRoot ?? "(not found)"}   [{status.SdkRootSource}]");
        }

        foreach (var tool in status.Tools)
        {
            var state = tool.IsAvailable ? "ok" : tool.Path is null ? "missing" : "problem";
            Info($"{tool.DisplayName,-11} {state,-8} {(versionsOnly ? tool.Version ?? "-" : tool.Path ?? "-")}");
            if (!versionsOnly && tool.Problem is not null && tool.Path is not null)
            {
                Info($"            {tool.Problem}");
            }
        }

        if (!versionsOnly)
        {
            foreach (var d in status.Diagnostics)
            {
                Info($"! {d}");
            }
        }
    }

    public static readonly IReadOnlyList<string> HelpText = new[]
    {
        "WSA Terminal runs real Android SDK tools. Input is parsed into arguments and the tool is launched",
        "directly - there is no shell, so pipes, redirects and && do not work.",
        "",
        "Built-ins:",
        "  sdk | versions | refresh     show / re-detect SDK path, tools, versions and diagnostics",
        "  avds                         list virtual devices and whether they are running",
        "  devices                      adb devices -l",
        "  clear | help",
        "",
        "SDK tools (arguments are passed through unchanged):",
        "  adb devices -l | adb version | adb logcat | adb install <apk> | adb uninstall <package>",
        "  adb push <local> <device> | adb pull <device> <local>",
        "  adb shell <command ...>      ONE-SHOT remote command; bare 'adb shell' (interactive) is not supported",
        "  emulator -list-avds",
        "  avdmanager list avd | sdkmanager --list",
        "",
        "Advanced command mode:",
        "  sdk-exec <path-relative-to-SDK-root> [args]   e.g. sdk-exec platform-tools\\fastboot.exe devices",
        "  Only files inside the configured SDK root can be launched this way.",
        "",
        "Quote paths containing spaces: adb install \"C:\\My Apps\\app.apk\"",
        "Backslashes are literal; use \\\" for a quote inside double quotes.",
        "Long-running commands (adb logcat) keep running until you press Cancel (Esc).",
    };
}
