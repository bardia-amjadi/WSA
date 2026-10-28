using WSA.Core.Models;

namespace WSA.Core.Interfaces;

/// <summary>
/// In-app installation of the Android SDK pieces WSA needs. Everything is downloaded from official vendor servers,
/// integrity-checked, and installed per user (no administrator rights). Methods throw
/// <see cref="ToolFailedException"/> / <see cref="ToolUnavailableException"/> / <see cref="OperationCanceledException"/>.
/// </summary>
public interface ISdkSetupService
{
    Task<SetupState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Manual route: uses an SDK folder the user already has. Saves it to Settings and re-detects. Fails only if the folder does not exist.</summary>
    Task<OperationResult> ApplyManualSdkRootAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Manual route: uses a JDK the user already has (folder containing bin\java.exe). Verifies it runs, saves it and re-detects.</summary>
    Task<OperationResult> ApplyManualJavaHomeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Creates the SDK folder if needed and remembers it in Settings when no SDK was configured. Returns the root.</summary>
    Task<string> EnsureSdkRootAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads Microsoft Build of OpenJDK 21 (SHA-256 verified) into WSA's own folder.</summary>
    Task InstallJdkAsync(IProgress<SetupProgress> progress, CancellationToken cancellationToken = default);

    /// <summary>Downloads Google's command-line tools (checksum from Google's repository index) into the SDK root.</summary>
    Task InstallCommandLineToolsAsync(IProgress<SetupProgress> progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads only Google's platform-tools (adb, about 10 MB, SHA-1 verified) into the SDK root. No JDK, emulator or
    /// system image is needed, which is enough for connecting a real phone. The caller must have obtained the user's
    /// agreement to the Android SDK license first.
    /// </summary>
    Task InstallPlatformToolsDirectAsync(IProgress<SetupProgress> progress, CancellationToken cancellationToken = default);

    /// <summary>Returns the license text that sdkmanager would ask the user to accept. Nothing is accepted by this call.</summary>
    Task<LicensePreview> PreviewLicensesAsync(CancellationToken cancellationToken = default);

    /// <summary>Accepts all pending licenses. Call only after the user explicitly agreed.</summary>
    Task AcceptLicensesAsync(IProgress<SetupProgress> progress, CancellationToken cancellationToken = default);

    Task InstallPackagesAsync(IReadOnlyList<string> packages, IProgress<SetupProgress> progress, CancellationToken cancellationToken = default);

    Task UninstallPackageAsync(string package, IProgress<SetupProgress> progress, CancellationToken cancellationToken = default);

    /// <summary>Lists installable system images (x86_64; default, Google APIs, Google Play) from Google's catalog. Needs network.</summary>
    Task<IReadOnlyList<StoreImage>> ListStoreImagesAsync(CancellationToken cancellationToken = default);
}
