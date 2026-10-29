namespace WSA.Core.Models;

/// <summary>What the installer window was started for.</summary>
public enum InstallerMode
{
    /// <summary>Started from a setup package while WSA is not installed yet.</summary>
    Install,

    /// <summary>WSA is installed: repair it or remove parts of it.</summary>
    Maintenance,

    /// <summary>Started from Windows' "Uninstall" entry: same as maintenance with Uninstall preselected.</summary>
    Uninstall,
}

public enum InstallComponent
{
    App,
    Jdk,
    SdkTools,
    SystemImage,
}

public enum InstallAction
{
    Install,
    Repair,
    Remove,
}

/// <summary>Real state of one installable piece, from files on disk (and a real java run for the JDK).</summary>
public sealed record ComponentState(InstallComponent Component, string Title, bool Installed, string Detail);

public static class PackageIds
{
    /// <summary>The one system image that ships with WSA: Android 14, Google APIs, x86_64.</summary>
    public const string DefaultSystemImage = "system-images;android-34;google_apis;x86_64";
}

/// <summary>One file listed in <c>bundle.json</c> of an offline package set.</summary>
public sealed record BundleFile(string Name, string Sha256, long Bytes);

/// <summary>
/// Describes an offline package set (<c>packages\bundle.json</c> next to the installer). Produced by the
/// WSA.BundleTool in CI; every file is verified against its SHA-256 before it is extracted.
/// </summary>
public sealed record BundleManifest(string Version, string SystemImage, string JdkName, IReadOnlyList<BundleFile> Files);
