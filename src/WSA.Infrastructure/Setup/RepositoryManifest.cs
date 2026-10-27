using System.Xml.Linq;
using WSA.Core.Models;

namespace WSA.Infrastructure.Setup;

public sealed record RemoteArchive(Uri Url, string Sha1, long? Size);

/// <summary>Reads Google's SDK repository index (repository2-3.xml). Element names are matched without namespaces.</summary>
public static class RepositoryManifest
{
    public const string IndexUrl = "https://dl.google.com/android/repository/repository2-3.xml";
    private const string DownloadBase = "https://dl.google.com/android/repository/";

    public static RemoteArchive FindWindowsCommandLineTools(string xml) => FindWindowsArchive(xml, "cmdline-tools;latest");

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
