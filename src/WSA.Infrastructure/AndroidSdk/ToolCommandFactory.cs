using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.Infrastructure.AndroidSdk;

public sealed class ToolCommandFactory : IToolCommandFactory
{
    private readonly ISdkLocator _locator;

    public ToolCommandFactory(ISdkLocator locator) => _locator = locator;

    public CommandRequest? TryCreate(SdkTool tool, IReadOnlyList<string> args, out string? error)
    {
        error = null;
        var status = _locator.Last;
        if (status is null)
        {
            error = "SDK detection has not completed yet. Wait a moment or run 'refresh'.";
            return null;
        }

        if (status.SdkRoot is null)
        {
            error = "No Android SDK is configured. Open 'Runtime & SDK' to see what is missing, or set the SDK root in Settings.";
            return null;
        }

        var toolStatus = status.Get(tool);
        if (toolStatus.Path is null)
        {
            error = $"{toolStatus.DisplayName} is not installed in '{status.SdkRoot}'. Run 'sdk' for details.";
            return null;
        }

        // A tool whose probe failed (e.g. no Java) is still launchable so the user can see its real error output.
        return new CommandRequest(toolStatus.Path, args, status.SdkRoot, status.ToolEnvironment);
    }

    public CommandRequest? TryCreateRelative(string relativePath, IReadOnlyList<string> args, out string? error)
    {
        error = null;
        var status = _locator.Last;
        if (status?.SdkRoot is null)
        {
            error = "No Android SDK is configured. Run 'sdk' for details.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            error = "sdk-exec needs a path relative to the SDK root, e.g. platform-tools\\fastboot.exe";
            return null;
        }

        var root = Path.GetFullPath(status.SdkRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var candidate in Candidates(Path.GetFullPath(Path.Combine(root, relativePath))))
        {
            if (!candidate.StartsWith(root, comparison))
            {
                error = "The path escapes the SDK root and was rejected.";
                return null;
            }

            if (File.Exists(candidate))
            {
                return new CommandRequest(candidate, args, status.SdkRoot, status.ToolEnvironment);
            }
        }

        error = $"No such file inside the SDK root: {relativePath}";
        return null;
    }

    private static IEnumerable<string> Candidates(string full)
    {
        yield return full;
        if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(Path.GetExtension(full)))
        {
            yield return full + ".exe";
            yield return full + ".bat";
        }
    }
}
