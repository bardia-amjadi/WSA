namespace WSA.Core.Models;

public static class JavaRequirements
{
    /// <summary>Minimum Java major version required by current Android command-line tools.</summary>
    public const int MinimumMajor = 17;
}

/// <summary>
/// Which Java WSA gives to the SDK tools it launches, and why. <paramref name="Notes"/> records what WSA saw in the
/// environment (for example a JAVA_HOME that exists in the registry but not in WSA's own process).
/// </summary>
public sealed record JavaResolution(
    string? JavaHome,
    string Source,
    string? JavaExe,
    string? Problem,
    IReadOnlyList<string> Notes);

/// <summary>Result of actually running the resolved java.exe, so the version shown is the one the child processes get.</summary>
public sealed record JavaReport(
    JavaResolution Resolution,
    string? JavaHomeReportedByJvm,
    string? VersionLine,
    string? SpecificationVersion,
    string? Vendor,
    int? Major,
    bool MeetsMinimum,
    IReadOnlyList<string> Lines);
