using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using WSA.Core.Models;

namespace WSA.Infrastructure.Installer;

/// <summary>Installs, verifies, repairs and removes the WSA application files, its Start menu shortcut and its Windows uninstall entry.</summary>
public sealed class AppInstaller
{
    private sealed record ManifestEntry(string Path, string Sha256, long Size);

    private sealed record InstallManifest(string Version, List<ManifestEntry> Files);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private static bool IsExcluded(string relative) =>
        relative.Equals(InstallLayout.SetupMarkerFileName, StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(InstallLayout.PackagesFolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies every application file from <paramref name="sourceDir"/> into the install folder.</summary>
    public async Task InstallAsync(string sourceDir, string version, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(sourceDir, f))
            .Where(r => !IsExcluded(r))
            .ToList();
        if (!files.Contains(InstallLayout.ExeName, StringComparer.OrdinalIgnoreCase))
        {
            throw new ToolFailedException($"{InstallLayout.ExeName} was not found in '{sourceDir}'. Run the installer from the extracted setup package.");
        }

        Directory.CreateDirectory(InstallLayout.InstallDir);
        progress.Report(new SetupProgress($"Copying WSA to {InstallLayout.InstallDir}...", 0, true));

        var entries = new List<ManifestEntry>();
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            entries.Add(await CopyOneAsync(sourceDir, files[i], ct).ConfigureAwait(false));
            progress.Report(new SetupProgress($"Copying files ({i + 1} of {files.Count})", (i + 1) * 100.0 / files.Count));
        }

        await WriteManifestAsync(version, entries, ct).ConfigureAwait(false);
        Register(version);
        progress.Report(new SetupProgress("WSA is installed and registered with Windows.", null));
    }

    /// <summary>Lists files that are missing or changed compared with the manifest written at install time.</summary>
    public async Task<IReadOnlyList<string>> VerifyAsync(IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        var manifest = await ReadManifestAsync(ct).ConfigureAwait(false);
        if (manifest is null)
        {
            return new[] { "install-manifest.json is missing or unreadable" };
        }

        var problems = new List<string>();
        for (var i = 0; i < manifest.Files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = manifest.Files[i];
            var path = Path.Combine(InstallLayout.InstallDir, entry.Path);
            if (!File.Exists(path))
            {
                problems.Add(entry.Path);
            }
            else if (!string.Equals(await HashAsync(path, ct).ConfigureAwait(false), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(entry.Path);
            }

            progress?.Report(new SetupProgress($"Verifying files ({i + 1} of {manifest.Files.Count})", (i + 1) * 100.0 / manifest.Files.Count));
        }

        return problems;
    }

    /// <summary>
    /// With a setup package (<paramref name="sourceDir"/>) all files are copied again (this also updates). Without one the installed
    /// files are verified and any damage is reported. The shortcut and uninstall entry are always recreated.
    /// </summary>
    public async Task RepairAsync(string? sourceDir, string version, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        if (sourceDir is not null)
        {
            // Running from a setup package: copy every file again, which also updates an older installation.
            await InstallAsync(sourceDir, version, progress, ct).ConfigureAwait(false);
            return;
        }

        progress.Report(new SetupProgress("Verifying installed files...", 0, true));
        var problems = await VerifyAsync(progress, ct).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            throw new ToolFailedException(
                $"{problems.Count} installed file(s) are damaged or missing (first: {problems[0]}). " +
                "Run Setup from the original WSA setup package to restore them.");
        }

        Register(version);
        progress.Report(new SetupProgress("All files are intact. Shortcut and Windows entry refreshed.", null));
    }

    /// <summary>Removes files, the shortcut and the uninstall entry. Files that are locked are reported, not ignored.</summary>
    public Task UninstallAsync(IProgress<SetupProgress> progress, CancellationToken ct) => Task.Run(() =>
    {
        progress.Report(new SetupProgress("Removing WSA...", null, true));
        Unregister();

        var locked = new List<string>();
        if (Directory.Exists(InstallLayout.InstallDir))
        {
            foreach (var file in Directory.EnumerateFiles(InstallLayout.InstallDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    locked.Add(Path.GetRelativePath(InstallLayout.InstallDir, file));
                }
            }

            if (locked.Count == 0)
            {
                foreach (var dir in Directory.EnumerateDirectories(InstallLayout.InstallDir, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length))
                {
                    TryDeleteDir(dir);
                }

                TryDeleteDir(InstallLayout.InstallDir);
            }
        }

        if (locked.Count > 0)
        {
            throw new ToolFailedException(
                $"{locked.Count} file(s) are in use and could not be removed (first: {locked[0]}). Close WSA and run Uninstall again.");
        }
    }, ct);

    private async Task<ManifestEntry> CopyOneAsync(string sourceDir, string relative, CancellationToken ct)
    {
        var source = Path.Combine(sourceDir, relative);
        var target = Path.Combine(InstallLayout.InstallDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        using var sha = SHA256.Create();
        await using var input = File.OpenRead(source);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return new ManifestEntry(relative, Convert.ToHexString(sha.Hash!), input.Length);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static async Task WriteManifestAsync(string version, List<ManifestEntry> entries, CancellationToken ct)
    {
        await using var stream = File.Create(Path.Combine(InstallLayout.InstallDir, InstallLayout.ManifestFileName));
        await JsonSerializer.SerializeAsync(stream, new InstallManifest(version, entries), Json, ct).ConfigureAwait(false);
    }

    private static async Task<InstallManifest?> ReadManifestAsync(CancellationToken ct)
    {
        var path = Path.Combine(InstallLayout.InstallDir, InstallLayout.ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<InstallManifest>(stream, Json, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<string?> InstalledVersionAsync(CancellationToken ct) => (await ReadManifestAsync(ct).ConfigureAwait(false))?.Version;

    private static void Register(string version)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using (var key = Registry.CurrentUser.CreateSubKey(InstallLayout.UninstallKey))
        {
            var exe = InstallLayout.InstalledExe;
            key.SetValue("DisplayName", "WSA - Windows Subsystem for Android");
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "WSA");
            key.SetValue("InstallLocation", InstallLayout.InstallDir);
            key.SetValue("DisplayIcon", exe);
            key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            key.SetValue("ModifyPath", $"\"{exe}\" --maintenance");
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        CreateShortcut();
    }

    private static void Unregister()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(InstallLayout.UninstallKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The entry stays; Windows will show it until it is removed manually.
        }

        try
        {
            File.Delete(InstallLayout.ShortcutPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover shortcut is harmless.
        }
    }

    private static void CreateShortcut()
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null)
            {
                return;
            }

            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(InstallLayout.ShortcutPath);
            link.TargetPath = InstallLayout.InstalledExe;
            link.WorkingDirectory = InstallLayout.InstallDir;
            link.Description = "WSA - Windows Subsystem for Android";
            link.Save();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // The app works without a Start menu entry.
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not empty or in use; reported through the locked-file list when relevant.
        }
    }
}
