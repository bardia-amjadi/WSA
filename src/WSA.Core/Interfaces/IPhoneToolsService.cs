using WSA.Core.Models;

namespace WSA.Core.Interfaces;

/// <summary>
/// Everything the phone desktop apps do, built on adb. Paths that reach the phone's shell are validated and quoted, and
/// file changes are limited to /sdcard, /storage and /data/local/tmp. Methods throw
/// <see cref="ToolFailedException"/> / <see cref="ToolUnavailableException"/> with user-facing messages.
/// </summary>
public interface IPhoneToolsService
{
    Task<IReadOnlyList<DeviceFile>> ListDirectoryAsync(string serial, string path, CancellationToken ct = default);

    Task PullAsync(string serial, string remotePath, string localFolder, IProgress<OutputLine>? output, CancellationToken ct = default);

    Task PushAsync(string serial, string localPath, string remoteFolder, IProgress<OutputLine>? output, CancellationToken ct = default);

    Task DeleteAsync(string serial, string remotePath, CancellationToken ct = default);

    Task MakeFolderAsync(string serial, string remotePath, CancellationToken ct = default);

    Task RenameAsync(string serial, string remotePath, string newName, CancellationToken ct = default);

    /// <summary>Runs one command line on the phone's own shell (<c>adb shell ...</c>). Returns the combined output and the exit code.</summary>
    Task<(string Output, int? ExitCode)> RunShellAsync(string serial, IReadOnlyList<string> args, IProgress<OutputLine>? output, CancellationToken ct = default);

    Task<IReadOnlyList<InstalledApp>> ListAppsAsync(string serial, bool includeSystem, CancellationToken ct = default);

    Task LaunchAppAsync(string serial, string package, CancellationToken ct = default);

    Task ForceStopAsync(string serial, string package, CancellationToken ct = default);

    Task ClearDataAsync(string serial, string package, CancellationToken ct = default);

    Task UninstallAsync(string serial, string package, CancellationToken ct = default);

    Task<DeviceStatus> GetStatusAsync(string serial, CancellationToken ct = default);

    /// <summary>Quick switches. <paramref name="name"/> is one of wifi, data, stayawake, touches.</summary>
    Task SetToggleAsync(string serial, string name, bool on, CancellationToken ct = default);

    Task SetBrightnessAsync(string serial, int percent, CancellationToken ct = default);

    /// <summary>Records the screen for <paramref name="seconds"/> (1-180) on the phone, then copies the video into <paramref name="localFolder"/>. Returns the local file.</summary>
    Task<string> RecordScreenAsync(string serial, int seconds, string localFolder, CancellationToken ct = default);

    /// <summary>mode: "system" (normal), "recovery" or "bootloader".</summary>
    Task RebootAsync(string serial, string mode, CancellationToken ct = default);

    /// <summary>Opens Android's own Settings app (or one of its pages) on the phone.</summary>
    Task OpenAndroidSettingsAsync(string serial, string page, CancellationToken ct = default);

    /// <summary>Turns on adb over Wi-Fi (<c>adb tcpip 5555</c>) and returns the phone's Wi-Fi address if known.</summary>
    Task<string?> EnableWifiAdbAsync(string serial, CancellationToken ct = default);
}
