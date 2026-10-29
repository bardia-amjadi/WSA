using WSA.Core.Interfaces;
using WSA.Core.Models;
using WSA.Infrastructure.AndroidSdk;
using WSA.Infrastructure.Configuration;

namespace WSA.Infrastructure.Installer;

/// <summary>
/// Installs, repairs and removes WSA and its packages (JDK, Android SDK tools, one system image). Packages come either from
/// an offline package set next to the installer (verified by SHA-256) or are downloaded from their vendors by the same
/// code the in-app Setup uses.
/// </summary>
public sealed class InstallerService
{
    private readonly ISdkSetupService _setup;
    private readonly ISdkLocator _sdk;
    private readonly IProcessRunner _runner;
    private readonly ISettingsService _settings;
    private readonly AppInstaller _app;

    public InstallerService(ISdkSetupService setup, ISdkLocator sdk, IProcessRunner runner, ISettingsService settings, AppInstaller app)
    {
        _setup = setup;
        _sdk = sdk;
        _runner = runner;
        _settings = settings;
        _app = app;
    }

    /// <summary>The folder with offline packages, or null when only online installation is possible.</summary>
    public string? OfflineBundleDir => InstallLayout.OfflineBundleDir;

    public string Version { get; set; } = typeof(InstallerService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Package set to use: from the offline folder when present, otherwise the built-in default image id.</summary>
    public async Task<string> SystemImageIdAsync(CancellationToken ct)
    {
        var dir = OfflineBundleDir;
        var manifest = dir is null ? null : await BundleReader.ReadAsync(dir, ct).ConfigureAwait(false);
        return manifest?.SystemImage ?? PackageIds.DefaultSystemImage;
    }

    public async Task<IReadOnlyList<ComponentState>> GetStatesAsync(CancellationToken ct)
    {
        var state = await _setup.GetStateAsync(ct).ConfigureAwait(false);
        var imageId = await SystemImageIdAsync(ct).ConfigureAwait(false);
        var imageDir = SystemImageDir(state.SdkRoot, imageId);
        var imageInstalled = File.Exists(Path.Combine(imageDir, "source.properties")) || File.Exists(Path.Combine(imageDir, "package.xml"));

        var version = await _app.InstalledVersionAsync(ct).ConfigureAwait(false);
        return new[]
        {
            new ComponentState(InstallComponent.App, "WSA application", InstallLayout.IsInstalled,
                InstallLayout.IsInstalled ? $"Version {version ?? "unknown"} in {InstallLayout.InstallDir}" : $"Will be installed to {InstallLayout.InstallDir}"),
            new ComponentState(InstallComponent.Jdk, "Java (JDK 21)", File.Exists(ManagedPaths.JavaExe),
                File.Exists(ManagedPaths.JavaExe) ? $"WSA-managed JDK in {ManagedPaths.JdkDir}" : "Microsoft Build of OpenJDK 21, kept in WSA's own folder"),
            new ComponentState(InstallComponent.SdkTools, "Android SDK tools", state.CmdlineToolsReady && state.PlatformToolsReady && state.EmulatorReady,
                $"Command-line tools, platform-tools (adb) and emulator in {state.SdkRoot}"),
            new ComponentState(InstallComponent.SystemImage, "Android 14 system image", imageInstalled,
                $"{imageId} (x86_64, Google APIs)"),
        };
    }

    /// <summary>Runs one action on one component. Packages must be processed in order: JDK, SDK tools, system image.</summary>
    public async Task RunAsync(
        InstallComponent component, InstallAction action, string? appSourceDir,
        Func<string, Task<bool>>? confirmLicenses, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        switch (component)
        {
            case InstallComponent.App:
                await RunAppAsync(action, appSourceDir, progress, ct).ConfigureAwait(false);
                break;
            case InstallComponent.Jdk:
                await RunJdkAsync(action, progress, ct).ConfigureAwait(false);
                break;
            case InstallComponent.SdkTools:
                await RunSdkToolsAsync(action, confirmLicenses, progress, ct).ConfigureAwait(false);
                break;
            default:
                await RunImageAsync(action, confirmLicenses, progress, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task RunAppAsync(InstallAction action, string? sourceDir, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        switch (action)
        {
            case InstallAction.Remove:
                await _app.UninstallAsync(progress, ct).ConfigureAwait(false);
                break;
            case InstallAction.Repair:
                await _app.RepairAsync(sourceDir, Version, progress, ct).ConfigureAwait(false);
                break;
            default:
                if (sourceDir is null)
                {
                    throw new ToolFailedException("The WSA files were not found next to the installer. Run it from the extracted setup package.");
                }

                await _app.InstallAsync(sourceDir, Version, progress, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task RunJdkAsync(InstallAction action, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        if (action == InstallAction.Remove)
        {
            progress.Report(new SetupProgress("Removing WSA's JDK...", null, true));
            DeleteDirectory(ManagedPaths.JdkDir);
            await _sdk.DetectAsync(ct).ConfigureAwait(false);
            return;
        }

        if (action == InstallAction.Repair && await ManagedJdkRunsAsync(ct).ConfigureAwait(false))
        {
            progress.Report(new SetupProgress("The JDK runs correctly; nothing to repair.", null, true));
            return;
        }

        if (OfflineBundleDir is { } dir && await BundleReader.ReadAsync(dir, ct).ConfigureAwait(false) is { } manifest)
        {
            DeleteDirectory(ManagedPaths.JdkDir);
            await BundleReader.ExtractAsync(dir, manifest, manifest.JdkName, ManagedPaths.JdkDir, "Java (JDK 21)", progress, ct)
                .ConfigureAwait(false);
            if (!await ManagedJdkRunsAsync(ct).ConfigureAwait(false))
            {
                throw new ToolFailedException($"The JDK was extracted to {ManagedPaths.JdkDir} but java.exe does not run.");
            }

            await _sdk.DetectAsync(ct).ConfigureAwait(false);
            return;
        }

        await _setup.InstallJdkAsync(progress, ct).ConfigureAwait(false);
    }

    private async Task RunSdkToolsAsync(
        InstallAction action, Func<string, Task<bool>>? confirmLicenses, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        if (action == InstallAction.Remove)
        {
            var root = await _setup.EnsureSdkRootAsync(ct).ConfigureAwait(false);
            if (File.Exists(Path.Combine(root, InstallLayout.ManagedSdkMarker)))
            {
                progress.Report(new SetupProgress($"Removing the SDK WSA installed ({root})...", null, true));
                DeleteDirectory(root);
                if (string.Equals(_settings.Current.SdkRootOverride, root, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.Current.SdkRootOverride = null;
                    await _settings.SaveAsync(ct).ConfigureAwait(false);
                }
            }
            else
            {
                progress.Report(new SetupProgress($"'{root}' was not created by WSA, so it was left untouched.", null, true));
            }

            await _sdk.DetectAsync(ct).ConfigureAwait(false);
            return;
        }

        var current = await _setup.GetStateAsync(ct).ConfigureAwait(false);
        if (action == InstallAction.Repair && current.CmdlineToolsReady && current.PlatformToolsReady && current.EmulatorReady)
        {
            progress.Report(new SetupProgress("Android SDK tools are present; nothing to repair.", null, true));
            return;
        }

        var sdkRoot = await _setup.EnsureSdkRootAsync(ct).ConfigureAwait(false);
        MarkManagedIfFresh(sdkRoot);

        if (OfflineBundleDir is { } dir && await BundleReader.ReadAsync(dir, ct).ConfigureAwait(false) is { } manifest)
        {
            await BundleReader.ExtractAsync(dir, manifest, "sdk.zip", sdkRoot, "Android SDK tools", progress, ct).ConfigureAwait(false);
            await _sdk.DetectAsync(ct).ConfigureAwait(false);
            return;
        }

        if (!current.CmdlineToolsReady)
        {
            await _setup.InstallCommandLineToolsAsync(progress, ct).ConfigureAwait(false);
        }

        await AcceptLicensesAsync(confirmLicenses, progress, ct).ConfigureAwait(false);
        var packages = new List<string>();
        if (!current.PlatformToolsReady) packages.Add("platform-tools");
        if (!current.EmulatorReady) packages.Add("emulator");
        await _setup.InstallPackagesAsync(packages, progress, ct).ConfigureAwait(false);
    }

    private async Task RunImageAsync(
        InstallAction action, Func<string, Task<bool>>? confirmLicenses, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        var imageId = await SystemImageIdAsync(ct).ConfigureAwait(false);
        var sdkRoot = await _setup.EnsureSdkRootAsync(ct).ConfigureAwait(false);
        var imageDir = SystemImageDir(sdkRoot, imageId);

        if (action == InstallAction.Remove)
        {
            progress.Report(new SetupProgress("Removing the Android 14 system image...", null, true));
            DeleteDirectory(imageDir);
            return;
        }

        var present = File.Exists(Path.Combine(imageDir, "source.properties")) || File.Exists(Path.Combine(imageDir, "package.xml"));
        if (action == InstallAction.Repair && present)
        {
            progress.Report(new SetupProgress("The system image is present; nothing to repair.", null, true));
            return;
        }

        if (OfflineBundleDir is { } dir && await BundleReader.ReadAsync(dir, ct).ConfigureAwait(false) is { } manifest)
        {
            DeleteDirectory(imageDir);
            await BundleReader.ExtractAsync(dir, manifest, "systemimage.zip", sdkRoot, "Android 14 system image", progress, ct)
                .ConfigureAwait(false);
            await _sdk.DetectAsync(ct).ConfigureAwait(false);
            return;
        }

        await AcceptLicensesAsync(confirmLicenses, progress, ct).ConfigureAwait(false);
        await _setup.InstallPackagesAsync(new[] { imageId }, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Shows sdkmanager's own license text and accepts only if the user agrees; nothing is accepted silently.</summary>
    private async Task AcceptLicensesAsync(Func<string, Task<bool>>? confirm, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        var license = await _setup.PreviewLicensesAsync(ct).ConfigureAwait(false);
        if (license.AllAccepted)
        {
            return;
        }

        if (confirm is null || !await confirm(license.Text).ConfigureAwait(false))
        {
            throw new ToolFailedException("The Android SDK licenses were not accepted, so the Android components cannot be downloaded.");
        }

        await _setup.AcceptLicensesAsync(progress, ct).ConfigureAwait(false);
    }

    /// <summary>Marks an SDK folder that WSA creates from scratch, so uninstall knows it may remove it.</summary>
    private static void MarkManagedIfFresh(string sdkRoot)
    {
        var marker = Path.Combine(sdkRoot, InstallLayout.ManagedSdkMarker);
        if (File.Exists(marker))
        {
            return;
        }

        if (!Directory.EnumerateFileSystemEntries(sdkRoot).Any())
        {
            File.WriteAllText(marker, "Created by the WSA installer. Removing WSA's Android SDK deletes this folder." + Environment.NewLine);
        }
    }

    private async Task<bool> ManagedJdkRunsAsync(CancellationToken ct)
    {
        if (!File.Exists(ManagedPaths.JavaExe))
        {
            return false;
        }

        var result = await _runner.RunAsync(
            new CommandRequest(ManagedPaths.JavaExe, new[] { "-version" }), null, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        return result.Succeeded;
    }

    private static string SystemImageDir(string sdkRoot, string imageId) =>
        Path.Combine(new[] { sdkRoot }.Concat(imageId.Split(';')).ToArray());

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    /// <summary>Optionally removes WSA's settings, logs and downloads.</summary>
    public static void RemoveUserData()
    {
        DeleteDirectory(ManagedPaths.AppDataDir);
    }
}
