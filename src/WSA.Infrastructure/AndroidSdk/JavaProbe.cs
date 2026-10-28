using System.Text.RegularExpressions;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.Infrastructure.AndroidSdk;

/// <summary>
/// Runs <c>java -XshowSettings:properties -version</c> with the same executable and environment the SDK tools get,
/// and turns the JVM's own answer into a report. This diagnoses; it never changes or bypasses any version check.
/// </summary>
internal static class JavaProbe
{
    private static readonly Regex PropertyRegex = new(@"^\s*(?<k>[\w.\-]+)\s=\s(?<v>.*)$", RegexOptions.Compiled);

    public static async Task<JavaReport> RunAsync(
        IProcessRunner runner, JavaResolution resolution, IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        var lines = new List<string>(resolution.Notes);
        lines.Add($"JDK folder chosen by WSA: {resolution.JavaHome ?? "(none)"}   [{resolution.Source}]");
        lines.Add($"java executable: {resolution.JavaExe ?? "(none found)"}");

        if (resolution.Problem is not null)
        {
            lines.Add($"PROBLEM: {resolution.Problem}");
        }

        if (resolution.JavaExe is null)
        {
            lines.Add("PROBLEM: No java.exe was found (no JAVA_HOME, nothing on PATH). Install a JDK 17+ or pick one in Settings.");
            return new JavaReport(resolution, null, null, null, null, null, false, lines);
        }

        var result = await runner.RunAsync(
            new CommandRequest(resolution.JavaExe, new[] { "-XshowSettings:properties", "-version" }, Environment: environment),
            null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);

        if (result.StartError is not null)
        {
            lines.Add($"PROBLEM: java could not be started: {result.StartError}");
            return new JavaReport(resolution, null, null, null, null, null, false, lines);
        }

        // The JVM prints its settings and version banner on stderr.
        var text = result.StandardError + "\n" + result.StandardOutput;
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        string? versionLine = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = PropertyRegex.Match(line);
            if (m.Success)
            {
                props.TryAdd(m.Groups["k"].Value, m.Groups["v"].Value.Trim());
            }
            else if (versionLine is null && line.Contains(" version \"", StringComparison.Ordinal))
            {
                versionLine = line.Trim();
            }
        }

        props.TryGetValue("java.specification.version", out var spec);
        props.TryGetValue("java.home", out var reportedHome);
        props.TryGetValue("java.vendor", out var vendor);
        var major = ParseMajor(spec);
        var meets = major is >= JavaRequirements.MinimumMajor;

        lines.Add($"Version banner: {versionLine ?? "(not printed)"}");
        lines.Add($"java.specification.version: {spec ?? "(unknown)"}   java.version: {(props.TryGetValue("java.version", out var v) ? v : "(unknown)")}");
        lines.Add($"java.home reported by the JVM: {reportedHome ?? "(unknown)"}");
        lines.Add($"Vendor: {vendor ?? "(unknown)"}");

        if (reportedHome is not null && resolution.JavaHome is not null &&
            !string.Equals(Path.GetFullPath(reportedHome).TrimEnd('\\', '/'), Path.GetFullPath(resolution.JavaHome).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"NOTE: the JVM runs from '{reportedHome}', not from the chosen folder (it may be a launcher stub that forwards to another JDK).");
        }

        lines.Add(major is null
            ? "PROBLEM: the Java version could not be read from the JVM's output."
            : meets
                ? $"OK: Java {major} meets the minimum (Java {JavaRequirements.MinimumMajor})."
                : $"PROBLEM: Java {major} is older than the required Java {JavaRequirements.MinimumMajor}.");

        return new JavaReport(resolution, reportedHome, versionLine, spec, vendor, major, meets, lines);
    }

    /// <summary>Turns "1.8" into 8 and "27" / "27.0.1" into 27.</summary>
    internal static int? ParseMajor(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        var parts = spec.Trim().Split('.');
        if (parts[0] == "1" && parts.Length > 1)
        {
            return int.TryParse(parts[1], out var legacy) ? legacy : null;
        }

        return int.TryParse(parts[0], out var major) ? major : null;
    }

    /// <summary>
    /// Builds an explanation for an sdkmanager failure that complains about Java, using what the JVM really reported.
    /// It states facts and the most likely reading; it never suggests skipping the tool's own version check.
    /// </summary>
    public static string ExplainJavaFailure(string toolMessage, JavaReport report)
    {
        var used = report.Resolution.JavaExe ?? "(no java found)";
        var version = report.Major is { } m ? $"Java {m}" : "unknown Java version";
        var head = $"{toolMessage} WSA started it with {used} ({version}, {report.Resolution.Source}).";

        if (report.Resolution.Problem is not null)
        {
            return $"{head} {report.Resolution.Problem}";
        }

        if (report.Major is null || !report.MeetsMinimum)
        {
            return $"{head} That Java is too old or unreadable. Choose a JDK {JavaRequirements.MinimumMajor}+ folder in Settings, " +
                   "or let Setup install one. If you installed a JDK recently, restart WSA so it sees the new JAVA_HOME.";
        }

        return $"{head} That Java satisfies the minimum (Java {JavaRequirements.MinimumMajor}), so the rejection most likely comes from " +
               "the sdkmanager launcher itself (a compatibility issue between this command-line tools release and this JDK build), " +
               "not from a missing JAVA_HOME. Setup can install a JDK 21 for WSA to use instead, or update the command-line tools. " +
               "WSA does not bypass the version check.";
    }
}

/// <summary>Service wrapper used by the UI and the terminal's <c>java</c> command.</summary>
public sealed class JavaDiagnosticsService : IJavaDiagnostics
{
    private readonly ISdkLocator _sdk;
    private readonly IProcessRunner _runner;

    public JavaDiagnosticsService(ISdkLocator sdk, IProcessRunner runner)
    {
        _sdk = sdk;
        _runner = runner;
    }

    public async Task<JavaReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var status = await _sdk.DetectAsync(cancellationToken).ConfigureAwait(false);
        return await JavaProbe.RunAsync(_runner, status.Java, status.ToolEnvironment, cancellationToken).ConfigureAwait(false);
    }
}
