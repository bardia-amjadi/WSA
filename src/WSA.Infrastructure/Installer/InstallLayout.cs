namespace WSA.Infrastructure.Installer;

/// <summary>Where WSA installs itself. Everything is per user, so no administrator rights are needed.</summary>
public static class InstallLayout
{
    public const string ExeName = "WSA.exe";
    public const string ManifestFileName = "install-manifest.json";
    public const string SetupMarkerFileName = "setup.json";
    public const string PackagesFolderName = "packages";
    public const string BundleFileName = "bundle.json";
    public const string ManagedSdkMarker = ".wsa-managed";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WSA";

    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WSA");

    public static string InstalledExe => Path.Combine(InstallDir, ExeName);

    public static string ShortcutPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "WSA.lnk");

    /// <summary>The folder this process runs from.</summary>
    public static string BaseDir => AppContext.BaseDirectory.TrimEnd('\\', '/');

    /// <summary>True when this process was started from a setup package (it has a setup.json next to it).</summary>
    public static bool IsSetupPackage => File.Exists(Path.Combine(BaseDir, SetupMarkerFileName));

    public static bool IsInstalled => File.Exists(InstalledExe);

    /// <summary>True when this process runs from the installed copy.</summary>
    public static bool RunsFromInstallDir =>
        BaseDir.Equals(InstallDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public static string? OfflineBundleDir
    {
        get
        {
            var dir = Path.Combine(BaseDir, PackagesFolderName);
            return File.Exists(Path.Combine(dir, BundleFileName)) ? dir : null;
        }
    }
}
