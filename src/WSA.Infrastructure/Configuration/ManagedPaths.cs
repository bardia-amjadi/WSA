namespace WSA.Infrastructure.Configuration;

/// <summary>Per-user locations WSA installs things into. No administrator rights are needed for any of them.</summary>
public static class ManagedPaths
{
    public static string AppDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WSA");

    /// <summary>WSA-managed JDK used for sdkmanager/avdmanager.</summary>
    public static string JdkDir => Path.Combine(AppDataDir, "jdk");

    public static string JavaExe => Path.Combine(JdkDir, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");

    public static string DownloadsDir => Path.Combine(AppDataDir, "downloads");

    /// <summary>Same default as Android Studio, so an SDK installed by WSA is usable there too.</summary>
    public static string DefaultSdkRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
}
