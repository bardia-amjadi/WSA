using WSA.Core.Models;

namespace WSA.Core.Services;

public abstract record TerminalAction;

/// <summary>A command handled inside WSA (help, sdk, avds, ...).</summary>
public sealed record BuiltInAction(string Name, IReadOnlyList<string> Args) : TerminalAction;

/// <summary>One of the four well-known Android SDK tools, launched directly.</summary>
public sealed record ToolAction(SdkTool Tool, IReadOnlyList<string> Args) : TerminalAction;

/// <summary>
/// Advanced mode: <c>sdk-exec &lt;path-relative-to-SDK-root&gt; [args]</c> launches another executable that lives
/// inside the configured SDK root (for example <c>platform-tools\fastboot.exe</c>). Paths that escape the SDK root are rejected by the executor.
/// </summary>
public sealed record SdkRelativeAction(string RelativePath, IReadOnlyList<string> Args) : TerminalAction;

public sealed record EmptyAction : TerminalAction;

public sealed record InvalidAction(string Message) : TerminalAction;

/// <summary>Turns terminal input into a structured action. Never produces a shell invocation.</summary>
public static class TerminalCommandParser
{
    public static readonly IReadOnlyList<string> BuiltIns =
        new[] { "help", "clear", "sdk", "avds", "devices", "versions", "refresh" };

    public static TerminalAction Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new EmptyAction();
        }

        if (!CommandLineTokenizer.TryTokenize(input, out var tokens, out var error))
        {
            return new InvalidAction(error ?? "Could not parse command.");
        }

        if (tokens.Count == 0)
        {
            return new EmptyAction();
        }

        var name = tokens[0].ToLowerInvariant();
        var rest = tokens.Skip(1).ToList();

        if (BuiltIns.Contains(name))
        {
            return new BuiltInAction(name, rest);
        }

        switch (name)
        {
            case "adb":
                if (rest.Count == 1 && rest[0] == "shell")
                {
                    return new InvalidAction(
                        "Interactive 'adb shell' is not supported in WSA Terminal. " +
                        "Run one-shot commands instead, e.g.:  adb shell getprop ro.build.version.release");
                }
                return new ToolAction(SdkTool.Adb, rest);
            case "emulator":
                return new ToolAction(SdkTool.Emulator, rest);
            case "avdmanager":
                return new ToolAction(SdkTool.AvdManager, rest);
            case "sdkmanager":
                return new ToolAction(SdkTool.SdkManager, rest);
            case "sdk-exec":
                if (rest.Count == 0)
                {
                    return new InvalidAction("Usage: sdk-exec <path-relative-to-SDK-root> [arguments...]");
                }
                return new SdkRelativeAction(rest[0], rest.Skip(1).ToList());
            default:
                return new InvalidAction(
                    $"Unknown command '{tokens[0]}'. WSA Terminal only launches Android SDK tools directly " +
                    "(adb, emulator, avdmanager, sdkmanager, sdk-exec) and its own built-ins. Type 'help'.");
        }
    }
}
