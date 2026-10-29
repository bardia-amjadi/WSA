using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using WSA.Core.Models;

namespace WSA.Infrastructure.Installer;

/// <summary>Reads an offline package set, verifies each file against its SHA-256 and extracts it.</summary>
public static class BundleReader
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static async Task<BundleManifest?> ReadAsync(string bundleDir, CancellationToken ct)
    {
        var path = Path.Combine(bundleDir, InstallLayout.BundleFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<BundleManifest>(stream, Json, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task WriteAsync(string bundleDir, BundleManifest manifest, CancellationToken ct)
    {
        await using var stream = File.Create(Path.Combine(bundleDir, InstallLayout.BundleFileName));
        await JsonSerializer.SerializeAsync(stream, manifest, Json, ct).ConfigureAwait(false);
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    /// <summary>Verifies <paramref name="name"/> against the manifest, then extracts it into <paramref name="destination"/>.</summary>
    public static async Task ExtractAsync(
        string bundleDir, BundleManifest manifest, string name, string destination, string label,
        IProgress<SetupProgress> progress, CancellationToken ct)
    {
        var file = manifest.Files.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ToolFailedException($"The offline package set does not list {name}.");
        var zipPath = Path.Combine(bundleDir, file.Name);
        if (!File.Exists(zipPath))
        {
            throw new ToolFailedException($"{file.Name} is missing from the offline package folder '{bundleDir}'.");
        }

        progress.Report(new SetupProgress($"Verifying {label}...", null, true));
        var actual = await Sha256Async(zipPath, ct).ConfigureAwait(false);
        if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolFailedException($"{file.Name} is damaged (SHA-256 does not match the package list). Download the setup package again.");
        }

        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        progress.Report(new SetupProgress($"Extracting {label}...", 0, true));

        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var count = archive.Entries.Count;
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = archive.Entries[i];
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ToolFailedException($"{file.Name} contains an unsafe path ({entry.FullName}) and was rejected.");
                }

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                if (i % 200 == 0)
                {
                    progress.Report(new SetupProgress($"Extracting {label} ({i} of {count})", i * 100.0 / count));
                }
            }
        }, ct).ConfigureAwait(false);
    }
}
