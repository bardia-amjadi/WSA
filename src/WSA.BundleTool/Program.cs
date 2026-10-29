using System.IO.Compression;
using WSA.Core.Models;
using WSA.Infrastructure.AndroidSdk;
using WSA.Infrastructure.Configuration;
using WSA.Infrastructure.Installer;
using WSA.Infrastructure.ProcessExecution;
using WSA.Infrastructure.Setup;

namespace WSA.BundleTool;

/// <summary>
/// Builds the offline package set (jdk.zip, sdk.zip, systemimage.zip, bundle.json) on a CI runner by running the same
/// download and install code the app uses, so every download is checksum-verified. Android SDK components are licensed by
/// Google; running this tool requires --accept-android-sdk-licenses, which makes the person running the build accept them.
/// The resulting package set is meant for your own use and must not be published.
/// </summary>
internal static class Program
{
    private sealed class ConsoleProgress : IProgress<SetupProgress>
    {
        private string _last = string.Empty;

        public void Report(SetupProgress value)
        {
            // Skip the rapid percentage redraws; print each distinct message once.
            if (value.Message != _last && !string.IsNullOrWhiteSpace(value.Message)
                && (value.Percent is null || value.NewStep))
            {
                _last = value.Message;
                Console.WriteLine(value.Message);
            }
        }
    }

    private static async Task<int> Main(string[] args)
    {
        string? outDir = null;
        var version = "0.0.0";
        var accepted = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--version" when i + 1 < args.Length: version = args[++i]; break;
                case "--accept-android-sdk-licenses": accepted = true; break;
            }
        }

        if (outDir is null || !accepted)
        {
            Console.Error.WriteLine("Usage: WSA.BundleTool --out <folder> --version <x.y.z> --accept-android-sdk-licenses");
            Console.Error.WriteLine("The flag confirms that you accept the Android SDK License Agreement (https://developer.android.com/studio/terms).");
            return 2;
        }

        try
        {
            await BuildAsync(Path.GetFullPath(outDir), version);
            return 0;
        }
        catch (Exception ex) when (ex is ToolFailedException or ToolUnavailableException or IOException or HttpRequestException)
        {
            Console.Error.WriteLine($"BUNDLE FAILED: {ex.Message}");
            return 1;
        }
    }

    private static async Task BuildAsync(string outDir, string version)
    {
        var work = Path.Combine(outDir, "_work");
        var sdkRoot = Path.Combine(work, "Sdk");
        Directory.CreateDirectory(sdkRoot);

        var settings = new SettingsService(Path.Combine(work, "settings"));
        settings.Current.SdkRootOverride = sdkRoot;
        await settings.SaveAsync();

        var runner = new ProcessRunner();
        var locator = new SdkLocator(settings, runner);
        var setup = new SdkSetupService(settings, locator, new ToolCommandFactory(locator), runner, new FileDownloader());
        var progress = new ConsoleProgress();
        var ct = CancellationToken.None;

        Console.WriteLine("== JDK ==");
        await setup.InstallJdkAsync(progress, ct);

        Console.WriteLine("== Android command-line tools ==");
        await setup.InstallCommandLineToolsAsync(progress, ct);

        Console.WriteLine("== Licenses (accepted by the person running this build) ==");
        await setup.AcceptLicensesAsync(progress, ct);

        Console.WriteLine("== platform-tools and emulator ==");
        await setup.InstallPackagesAsync(new[] { "platform-tools", "emulator" }, progress, ct);

        var tools = await setup.GetStateAsync(ct);
        if (!tools.CmdlineToolsReady || !tools.PlatformToolsReady || !tools.EmulatorReady)
        {
            throw new ToolFailedException("The staged SDK is incomplete after installation; refusing to package it.");
        }

        var packages = Path.Combine(outDir, InstallLayout.PackagesFolderName);
        Directory.CreateDirectory(packages);
        var sdkZip = Path.Combine(packages, "sdk.zip");
        Console.WriteLine("== Packing sdk.zip ==");
        ZipFolder(sdkRoot, sdkZip, rel => !rel.StartsWith("system-images/", StringComparison.Ordinal)
                                         && !rel.StartsWith(".temp/", StringComparison.Ordinal)
                                         && !rel.StartsWith(".downloadIntermediates/", StringComparison.Ordinal)
                                         && rel != InstallLayout.ManagedSdkMarker);

        Console.WriteLine($"== System image {PackageIds.DefaultSystemImage} ==");
        await setup.InstallPackagesAsync(new[] { PackageIds.DefaultSystemImage }, progress, ct);
        var imageZip = Path.Combine(packages, "systemimage.zip");
        ZipFolder(sdkRoot, imageZip, rel => rel.StartsWith("system-images/", StringComparison.Ordinal));

        Console.WriteLine("== Packing jdk.zip ==");
        var jdkZip = Path.Combine(packages, "jdk.zip");
        ZipFolder(ManagedPaths.JdkDir, jdkZip, _ => true);

        var files = new List<BundleFile>();
        foreach (var zip in new[] { jdkZip, sdkZip, imageZip })
        {
            files.Add(new BundleFile(Path.GetFileName(zip), await BundleReader.Sha256Async(zip, ct), new FileInfo(zip).Length));
        }

        await BundleReader.WriteAsync(packages, new BundleManifest(version, PackageIds.DefaultSystemImage, "jdk.zip", files), ct);
        foreach (var f in files)
        {
            Console.WriteLine($"{f.Name}  {f.Bytes / (1024 * 1024)} MB  sha256 {f.Sha256}");
        }

        Directory.Delete(work, recursive: true);
        Console.WriteLine($"Offline package set written to {packages}");
    }

    private static void ZipFolder(string root, string zipPath, Func<string, bool> include)
    {
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var fullRoot = Path.GetFullPath(root);
        foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(fullRoot, file).Replace('\\', '/');
            if (include(relative))
            {
                archive.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
            }
        }
    }
}
