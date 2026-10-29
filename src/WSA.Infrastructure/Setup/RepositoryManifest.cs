using System.Xml.Linq;
using WSA.Core.Models;

namespace WSA.Infrastructure.Setup;

public sealed record RemoteArchive(Uri Url, string Sha1, long? Size);

/// <summary>Reads Google's SDK repository index (repository2-3.xml). Element names are matched without namespaces.</summary>
public static class RepositoryManifest
{
    public const string IndexUrl = "https://dl.google.com/android/repository/repository2-3.xml";
    private const string DownloadBase = "https://dl.google.com/android/repository/";

    /// <summary>
    /// Command-line tools 23.0 and newer print "sdkmanager is deprecated. Android CLI will be used instead" and no longer show
    /// or accept licenses the way WSA drives sdkmanager (<c>--licenses</c> with scripted input). Until WSA supports the new
    /// Android CLI, the newest classic release (major version 22 or lower) is used.
    /// </summary>
    public const int NewestClassicMajor = 22;

    public static RemoteArchive FindWindowsCommandLineTools(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new ToolFailedException($"Google's SDK repository index could not be parsed: {ex.Message}");
        }

        var versioned = doc.Descendants()
            .Where(e => e.Name.LocalName == "remotePackage")
            .Select(e => (string?)e.Attribute("path"))
            .Where(p => p is not null)
            .Select(p => (Path: p!, Match: System.Text.RegularExpressions.Regex.Match(p!, @"^cmdline-tools;(\d+)\.(\d+)$")))
            .Where(x => x.Match.Success)
            .Select(x => (x.Path, Major: int.Parse(x.Match.Groups[1].Value), Minor: int.Parse(x.Match.Groups[2].Value)))
            .Where(x => x.Major <= NewestClassicMajor)
            .OrderByDescending(x => x.Major).ThenByDescending(x => x.Minor)
            .ToList();

        foreach (var candidate in versioned)
        {
            try
            {
                return FindWindowsArchive(xml, candidate.Path);
            }
            catch (ToolFailedException)
            {
                // No usable Windows archive for this version; try the next older one.
            }
        }

        // No classic release is listed: fall back to whatever "latest" is, so setup still works (licenses may need the new tool).
        return FindWindowsArchive(xml, "cmdline-tools;latest");
    }

    public static RemoteArchive FindWindowsArchive(string xml, string packagePath)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new ToolFailedException($"Google's SDK repository index could not be parsed: {ex.Message}");
        }

        var packages = doc.Descendants()
            .Where(e => e.Name.LocalName == "remotePackage" && (string?)e.Attribute("path") == packagePath)
            .ToList();

        foreach (var package in packages)
        {
            foreach (var archive in package.Descendants().Where(e => e.Name.LocalName == "archive"))
            {
                var hostOs = archive.Elements().FirstOrDefault(e => e.Name.LocalName == "host-os")?.Value.Trim();
                if (!string.Equals(hostOs, "windows", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var complete = archive.Elements().FirstOrDefault(e => e.Name.LocalName == "complete");
                var url = complete?.Elements().FirstOrDefault(e => e.Name.LocalName == "url")?.Value.Trim();
                var checksum = complete?.Elements().FirstOrDefault(e => e.Name.LocalName == "checksum");
                var sha1 = checksum?.Value.Trim();
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(sha1))
                {
                    continue;
                }

                if (!string.Equals((string?)checksum!.Attribute("type"), "sha1", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long? size = long.TryParse(
                    complete!.Elements().FirstOrDefault(e => e.Name.LocalName == "size")?.Value, out var s) ? s : null;

                // Relative file names are resolved against Google's repository directory.
                var uri = Uri.TryCreate(url, UriKind.Absolute, out var absolute) ? absolute : new Uri(DownloadBase + url);
                return new RemoteArchive(uri, sha1, size);
            }
        }

        throw new ToolFailedException(
            $"Google's SDK repository index has no Windows '{packagePath}' archive with a SHA-1 checksum. " +
            "Install it manually (README, Android SDK setup) and press Re-check.");
    }
}
