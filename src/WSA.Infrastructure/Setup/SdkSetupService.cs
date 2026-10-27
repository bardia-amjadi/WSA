using System.IO.Compression;
using System.Text.RegularExpressions;
using WSA.Core.Interfaces;
using WSA.Core.Models;
using WSA.Core.Services;
using WSA.Infrastructure.Configuration;

namespace WSA.Infrastructure.Setup;

public sealed class SdkSetupService : ISdkSetupService
{
    // Verified against Microsoft's download page; the generic alias is tried if the pinned build is ever withdrawn.
    private static readonly string[] JdkUrls =
    {
        "https://aka.ms/download-jdk/microsoft-jdk-21.0.13.0.2-windows-x64.zip",
        "https://aka.ms/download-jdk/microsoft-jdk-21-windows-x64.zip",
    };

    private static readonly Regex Sha256Regex = new(@"\b[0-9a-fA-F]{64}\b", RegexOptions.Compiled);
    private static readonly Regex PercentRegex = new(@"(\d{1,3})%", RegexOptions.Compiled);
    private static readonly string[] StoreTags = { "default", "google_apis", "google_apis_playstore" };
    private static readonly TimeSpan LicenseTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(90);

    private const long SystemImageMinFreeBytes = 6L * 1024 * 1024 * 1024;
    private const long BaseMinFreeBytes = 1L * 1024 * 1024 * 1024;

    private readonly ISettingsService _settings;
    private readonly ISdkLocator _sdk;
    private readonly IToolCommandFactory _tools;
    private readonly IProcessRunner _runner;
    private readonly FileDownloader _downloader;

    public SdkSetupService(
        ISettingsService settings, ISdkLocator sdk, IToolCommandFactory tools,
        IProcessRunner runner, FileDownloader downloader)
    {
        _settings = settings;
        _sdk = sdk;
        _tools = tools;
        _runner = runner;
        _downloader = downloader;
    }

    public async Task<SetupState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var status = _sdk.Last ?? await _sdk.DetectAsync(cancellationToken).ConfigureAwait(false);
        var managedJdk = File.Exists(ManagedPaths.JavaExe);
        var sdkManager = status.Get(SdkTool.SdkManager);
        var jdkReady = managedJdk || sdkManager.IsAvailable;

        var jdkDetail = managedJdk
            ? $"Installed by WSA at {ManagedPaths.JdkDir}"
            : sdkManager.IsAvailable
                ? "A working Java runtime was found on this computer"
                : "Not installed";

        var accel = status.Diagnostics.FirstOrDefault(d => d.StartsWith("Hardware acceleration", StringComparison.Ordinal));

        return new SetupState(
            TargetRoot(status),
            jdkReady,
            jdkDetail,
            status.Get(SdkTool.SdkManager).Path is not null,
            status.Get(SdkTool.Adb).Path is not null,
            status.Get(SdkTool.Emulator).Path is not null,
            accel);
    }

    public async Task<string> EnsureSdkRootAsync(CancellationToken cancellationToken = default)
    {
        var status = _sdk.Last ?? await _sdk.DetectAsync(cancellationToken).ConfigureAwait(false);
        var root = TargetRoot(status);
        Directory.CreateDirectory(root);

        if (status.SdkRoot is null)
        {
            _settings.Current.SdkRootOverride = root;
            await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
            await _sdk.DetectAsync(cancellationToken).ConfigureAwait(false);
        }

        return root;
    }

    public async Task InstallJdkAsync(IProgress<SetupProgress> progress, CancellationToken cancellationToken = default)
    {
        progress.Report(new SetupProgress("Preparing JDK download (Microsoft Build of OpenJDK 21)...", null, true));

        string? lastError = null;
        foreach (var zipUrl in JdkUrls)
        {
            var url = new Uri(zipUrl);
            try
            {
                var shaText = await _downloader.GetStringAsync(new Uri(zipUrl + ".sha256sum.txt"), cancellationToken)
                    .ConfigureAwait(false);
                var match = Sha256Regex.Match(shaText);
                if (!match.Success)
                {
                    throw new ToolFailedException("The published SHA-256 file did not contain a checksum.");
                }

                var zipPath = Path.Combine(ManagedPaths.DownloadsDir, "jdk-21-windows-x64.zip");
                progress.Report(new SetupProgress("Downloading JDK...", 0, true));
                await _downloader.DownloadAsync(url, zipPath, "SHA256", match.Value,
                    (done, total) => progress.Report(new SetupProgress(Describe("Downloading JDK", done, total), Percent(done, total))),
                    cancellationToken).ConfigureAwait(false);

                progress.Report(new SetupProgress("Extracting JDK...", null, true));
                await ExtractJdkAsync(zipPath, cancellationToken).ConfigureAwait(false);
                TryDelete(zipPath);

                progress.Report(new SetupProgress("Checking the installed JDK...", null, true));
                var check = await _runner.RunAsync(
                    new CommandRequest(ManagedPaths.JavaExe, new[] { "-version" }), null, TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false);
                if (!check.Succeeded)
                {
                    throw new ToolFailedException(
                        $"The JDK was extracted but does not run: {check.StartError ?? ToolOutputParsers.FirstLine(check.StandardError)}");
                }

                await _sdk.DetectAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (ToolFailedException ex)
            {
                lastError = ex.Message;
            }
        }

        throw new ToolFailedException($"Could not install the JDK. {lastError}");
    }

    public async Task InstallCommandLineToolsAsync(IProgress<SetupProgress> progress, CancellationToken cancellationToken = default)
    {
        var root = await EnsureSdkRootAsync(cancellationToken).ConfigureAwait(false);

        progress.Report(new SetupProgress("Reading Google's SDK repository index...", null, true));
        var xml = await _downloader.GetStringAsync(new Uri(RepositoryManifest.IndexUrl), cancellationToken).ConfigureAwait(false);
        var archive = RepositoryManifest.FindWindowsCommandLineTools(xml);

        var zipPath = Path.Combine(ManagedPaths.DownloadsDir, "commandlinetools-win.zip");
        progress.Report(new SetupProgress("Downloading Android command-line tools...", 0, true));
        await _downloader.DownloadAsync(archive.Url, zipPath, "SHA1", archive.Sha1,
            (done, total) => progress.Report(new SetupProgress(
                Describe("Downloading Android command-line tools", done, total), Percent(done, total))),
            cancellationToken).ConfigureAwait(false);

        progress.Report(new SetupProgress("Extracting command-line tools...", null, true));
        var cmdlineDir = Path.Combine(root, "cmdline-tools");
        var staging = Path.Combine(cmdlineDir, ".wsa-staging");
        var latest = Path.Combine(cmdlineDir, "latest");

        await Task.Run(() =>
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }

            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(zipPath, staging);

            // Google's archive contains a single top-level "cmdline-tools" folder that must become "latest".
            var inner = Path.Combine(staging, "cmdline-tools");
            if (!Directory.Exists(Path.Combine(inner, "bin")))
            {
                throw new ToolFailedException("The downloaded archive does not have the expected cmdline-tools layout.");
            }

            if (Directory.Exists(latest))
            {
                Directory.Delete(latest, true);
            }

            Directory.Move(inner, latest);
            Directory.Delete(staging, true);
        }, cancellationToken).ConfigureAwait(false);

        TryDelete(zipPath);
        var status = await _sdk.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (status.Get(SdkTool.SdkManager).Path is null)
        {
            throw new ToolFailedException($"Extraction finished but sdkmanager was not found under {latest}.");
        }
    }

    public async Task InstallPlatformToolsDirectAsync(IProgress<SetupProgress> progress, CancellationToken cancellationToken = default)
    {
        var root = await EnsureSdkRootAsync(cancellationToken).ConfigureAwait(false);

        progress.Report(new SetupProgress("Reading Google's SDK repository index...", null, true));
        var xml = await _downloader.GetStringAsync(new Uri(RepositoryManifest.IndexUrl), cancellationToken).ConfigureAwait(false);
        var archive = RepositoryManifest.FindWindowsArchive(xml, "platform-tools");

        var zipPath = Path.Combine(ManagedPaths.DownloadsDir, "platform-tools-win.zip");
        progress.Report(new SetupProgress("Downloading adb (Android platform-tools)...", 0, true));
        await _downloader.DownloadAsync(archive.Url, zipPath, "SHA1", archive.Sha1,
            (done, total) => progress.Report(new SetupProgress(
                Describe("Downloading adb", done, total), Percent(done, total))),
            cancellationToken).ConfigureAwait(false);

        progress.Report(new SetupProgress("Extracting adb...", null, true));
        var staging = Path.Combine(root, ".wsa-staging-platform-tools");
        var target = Path.Combine(root, "platform-tools");
        var adbName = OperatingSystem.IsWindows() ? "adb.exe" : "adb";

        await Task.Run(() =>
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }

            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(zipPath, staging);

            var inner = Path.Combine(staging, "platform-tools");
            if (!File.Exists(Path.Combine(inner, adbName)))
            {
                throw new ToolFailedException("The downloaded archive does not contain platform-tools\\" + adbName + ".");
            }

            if (Directory.Exists(target))
            {
                try
                {
                    Directory.Delete(target, true);
                }
                catch (IOException ex)
                {
                    throw new ToolFailedException(
                        $"The existing platform-tools folder is in use ({ex.Message}). Close other programs that use adb and try again.");
                }
            }

            Directory.Move(inner, target);
            Directory.Delete(staging, true);
        }, cancellationToken).ConfigureAwait(false);

        TryDelete(zipPath);
        var status = await _sdk.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (status.Get(SdkTool.Adb).Path is null)
        {
            throw new ToolFailedException($"Extraction finished but adb was not found under {target}.");
        }
    }

    public async Task<LicensePreview> PreviewLicensesAsync(CancellationToken cancellationToken = default)
    {
        // "y" opens the review; every later "n" declines the license so nothing is accepted by a preview.
        var stdin = "y\n" + string.Concat(Enumerable.Repeat("n\n", 40));
        var result = await RunSdkManagerAsync(new[] { "--licenses" }, stdin, null, LicenseTimeout, cancellationToken)
            .ConfigureAwait(false);

        var text = (result.StandardOutput + result.StandardError).Trim();
        var allAccepted = text.Contains("All SDK package licenses accepted", StringComparison.OrdinalIgnoreCase);
        return new LicensePreview(allAccepted, text);
    }

    public async Task AcceptLicensesAsync(IProgress<SetupProgress> progress, CancellationToken cancellationToken = default)
    {
        progress.Report(new SetupProgress("Accepting Android SDK licenses...", null, true));
        var stdin = string.Concat(Enumerable.Repeat("y\n", 60));
        var result = await RunSdkManagerAsync(new[] { "--licenses" }, stdin, null, LicenseTimeout, cancellationToken)
            .ConfigureAwait(false);

        var text = result.StandardOutput + result.StandardError;
        if (!text.Contains("All SDK package licenses accepted", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolFailedException(
                "sdkmanager did not confirm that the licenses were accepted: " +
                (ToolOutputParsers.FirstLine(result.StandardError) ?? $"exit code {result.ExitCode}"));
        }
    }

    public async Task InstallPackagesAsync(
        IReadOnlyList<string> packages, IProgress<SetupProgress> progress, CancellationToken cancellationToken = default)
    {
        if (packages.Count == 0)
        {
            return;
        }

        var root = TargetRoot(_sdk.Last);
        CheckFreeSpace(root, packages.Any(p => p.StartsWith("system-images;", StringComparison.Ordinal))
            ? SystemImageMinFreeBytes
            : BaseMinFreeBytes);

        progress.Report(new SetupProgress($"Installing {string.Join(", ", packages)}...", null, true));
        var args = new List<string> { "--install" };
        args.AddRange(packages);

        // No stdin on purpose: if a license is still pending, sdkmanager must fail rather than accept it silently.
        var result = await RunSdkManagerAsync(args, null, progress, InstallTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new ToolFailedException(
                $"sdkmanager could not install {string.Join(", ", packages)}: {FailureReason(result)} " +
                "(If this mentions licenses, accept them and try again.)");
        }

        await _sdk.DetectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UninstallPackageAsync(string package, IProgress<SetupProgress> progress, CancellationToken cancellationToken = default)
    {
        progress.Report(new SetupProgress($"Removing {package}...", null, true));
        var result = await RunSdkManagerAsync(new[] { "--uninstall", package }, null, progress, InstallTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new ToolFailedException($"sdkmanager could not remove {package}: {FailureReason(result)}");
        }
    }

    public async Task<IReadOnlyList<StoreImage>> ListStoreImagesAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunSdkManagerAsync(new[] { "--list" }, null, null, TimeSpan.FromMinutes(5), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new ToolFailedException(
                $"sdkmanager --list failed: {FailureReason(result)} (this needs an internet connection).");
        }

        var byPath = new Dictionary<string, SdkListEntry>(StringComparer.Ordinal);
        foreach (var entry in ToolOutputParsers.ParseSdkManagerList(result.StandardOutput))
        {
            // An installed entry wins over the same package listed as available.
            if (!byPath.TryGetValue(entry.Path, out var existing) || (entry.Installed && !existing.Installed))
            {
                byPath[entry.Path] = entry;
            }
        }

        var images = new List<StoreImage>();
        foreach (var entry in byPath.Values)
        {
            var parts = entry.Path.Split(';');
            if (parts.Length != 4 || parts[0] != "system-images" || parts[3] != "x86_64" || !StoreTags.Contains(parts[2]))
            {
                continue;
            }

            images.Add(new StoreImage(
                entry.Path, parts[1], AndroidVersionNames.Describe(parts[1]),
                AndroidVersionNames.VariantName(parts[2]), parts[3], entry.Description, entry.Installed));
        }

        return images
            .OrderByDescending(i => ApiSortKey(i.ApiName))
            .ThenBy(i => i.Variant, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<CommandResult> RunSdkManagerAsync(
        IReadOnlyList<string> args, string? stdin, IProgress<SetupProgress>? progress, TimeSpan timeout, CancellationToken ct)
    {
        var status = _sdk.Last ?? await _sdk.DetectAsync(ct).ConfigureAwait(false);
        var all = new List<string> { $"--sdk_root={TargetRoot(status)}" };
        all.AddRange(args);

        var request = _tools.TryCreate(SdkTool.SdkManager, all, out var error);
        if (request is null)
        {
            throw new ToolUnavailableException(error ?? "sdkmanager is not installed. Run Setup first.");
        }

        request = request with { StandardInput = stdin };
        var output = progress is null ? null : new InlineProgress<OutputLine>(line =>
        {
            var m = PercentRegex.Match(line.Text);
            double? percent = m.Success && int.TryParse(m.Groups[1].Value, out var p) && p is >= 0 and <= 100 ? p : null;
            progress.Report(new SetupProgress(line.Text.Trim(), percent));
        });

        return await _runner.RunAsync(request, output, timeout, ct).ConfigureAwait(false);
    }

    private string TargetRoot(SdkStatus? status)
    {
        if (status?.SdkRoot is { } root)
        {
            return root;
        }

        var configured = _settings.Current.SdkRootOverride;
        return string.IsNullOrWhiteSpace(configured) ? ManagedPaths.DefaultSdkRoot : Path.GetFullPath(configured);
    }

    private static async Task ExtractJdkAsync(string zipPath, CancellationToken ct)
    {
        var staging = Path.Combine(ManagedPaths.AppDataDir, "jdk-staging");
        await Task.Run(() =>
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }

            ZipFile.ExtractToDirectory(zipPath, staging);
            ct.ThrowIfCancellationRequested();

            // The zip holds one top-level folder (jdk-21.x) that contains bin\java.exe.
            var top = Directory.EnumerateDirectories(staging)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java")))
                ?? throw new ToolFailedException("The JDK archive did not contain bin\\java.exe.");

            if (Directory.Exists(ManagedPaths.JdkDir))
            {
                Directory.Delete(ManagedPaths.JdkDir, true);
            }

            Directory.Move(top, ManagedPaths.JdkDir);
            Directory.Delete(staging, true);
        }, ct).ConfigureAwait(false);
    }

    private static void CheckFreeSpace(string root, long required)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            if (drive.AvailableFreeSpace < required)
            {
                throw new ToolFailedException(
                    $"Not enough free disk space on {drive.Name}: {drive.AvailableFreeSpace / 1024 / 1024 / 1024} GB free, " +
                    $"about {required / 1024 / 1024 / 1024} GB needed. Free some space or choose another SDK folder in Settings.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            // Free space could not be determined (for example a network path); let sdkmanager report real failures.
        }
    }

    private static string FailureReason(CommandResult r) =>
        r.StartError
        ?? ToolOutputParsers.FirstLine(r.StandardError)
        ?? r.StandardOutput.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0)
        ?? $"exit code {r.ExitCode}";

    private static int ApiSortKey(string apiName)
    {
        var m = Regex.Match(apiName, @"^android-(\d+)(?:-ext(\d+))?$");
        return m.Success ? int.Parse(m.Groups[1].Value) * 100 + (m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0) : -1;
    }

    private static double? Percent(long done, long? total) =>
        total is > 0 ? Math.Min(100.0, done * 100.0 / total.Value) : null;

    private static string Describe(string what, long done, long? total) =>
        total is > 0
            ? $"{what}: {done / 1024.0 / 1024.0:0.0} of {total.Value / 1024.0 / 1024.0:0.0} MB"
            : $"{what}: {done / 1024.0 / 1024.0:0.0} MB";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Leftover downloads are harmless and are overwritten next time.
        }
    }

    /// <summary>Synchronous IProgress so callers (which already marshal to the UI) see ordered output.</summary>
    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public InlineProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
}
