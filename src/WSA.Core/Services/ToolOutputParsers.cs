using System.Text.RegularExpressions;
using WSA.Core.Models;

namespace WSA.Core.Services;

/// <summary>Parsers for the human-readable output of Android SDK tools. These never invent data: unparseable input yields empty results.</summary>
public static class ToolOutputParsers
{
    private static readonly Regex AbiRegex = new(@"Tag/ABI:\s*(\S+)", RegexOptions.Compiled);

    /// <summary>Parses <c>adb devices -l</c>.</summary>
    public static IReadOnlyList<AdbDevice> ParseAdbDevices(string output)
    {
        var devices = new List<AdbDevice>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0
                || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith('*'))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            devices.Add(new AdbDevice(parts[0], parts[1], parts.Length > 2 ? parts[2] : null));
        }

        return devices;
    }

    /// <summary>Parses the AVD name out of <c>adb -s &lt;serial&gt; emu avd name</c> output.</summary>
    public static string? ParseEmuAvdName(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("KO", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return line;
        }

        return null;
    }

    /// <summary>Parses <c>emulator -list-avds</c> (one AVD name per line).</summary>
    public static IReadOnlyList<string> ParseListAvds(string output) =>
        output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("INFO", StringComparison.OrdinalIgnoreCase)
                        && !l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase)
                        && !l.Contains(' '))
            .ToList();

    /// <summary>
    /// Parses <c>avdmanager list avd</c>. Entries are separated by dashed lines and consist of "Key: value" lines.
    /// Entries that avdmanager reports as failing to load (no Name key) are skipped.
    /// </summary>
    public static IReadOnlyList<AvdInfo> ParseAvdManagerList(string output)
    {
        var result = new List<AvdInfo>();
        var block = new List<string>();

        void Flush()
        {
            if (block.Count > 0)
            {
                var avd = ParseAvdBlock(block);
                if (avd is not null)
                {
                    result.Add(avd);
                }

                block.Clear();
            }
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("----", StringComparison.Ordinal))
            {
                Flush();
            }
            else
            {
                block.Add(line);
            }
        }

        Flush();
        return result;
    }

    private static AvdInfo? ParseAvdBlock(List<string> lines)
    {
        string? name = null, device = null, path = null, target = null, basedOn = null, abi = null;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var abiMatch = AbiRegex.Match(trimmed);
            if (abiMatch.Success)
            {
                abi = abiMatch.Groups[1].Value;
            }

            var idx = trimmed.IndexOf(':');
            if (idx <= 0)
            {
                continue;
            }

            var key = trimmed[..idx].Trim();
            var value = trimmed[(idx + 1)..].Trim();
            switch (key)
            {
                case "Name": name ??= value; break;
                case "Device": device ??= value; break;
                case "Path": path ??= value; break;
                case "Target": target ??= value; break;
                case "Based on":
                    basedOn ??= AbiRegex.Replace(value, string.Empty).Trim();
                    break;
            }
        }

        return name is null ? null : new AvdInfo(name, device, path, target, basedOn, abi, false, null);
    }

    /// <summary>
    /// Parses <c>sdkmanager --list</c>. Only the "Installed packages" and "Available Packages" tables are read;
    /// rows are "path | version | description [| location]". Progress lines and headers are ignored.
    /// </summary>
    public static IReadOnlyList<SdkListEntry> ParseSdkManagerList(string output)
    {
        var entries = new List<SdkListEntry>();
        var section = string.Empty;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Installed packages", StringComparison.OrdinalIgnoreCase)) { section = "installed"; continue; }
            if (line.StartsWith("Available Packages", StringComparison.OrdinalIgnoreCase)) { section = "available"; continue; }
            if (line.StartsWith("Available Updates", StringComparison.OrdinalIgnoreCase)) { section = "updates"; continue; }
            if (section is not ("installed" or "available"))
            {
                continue;
            }

            var parts = line.Split('|');
            if (parts.Length < 3)
            {
                continue;
            }

            var path = parts[0].Trim();
            if (path.Length == 0 || path == "Path" || path.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            entries.Add(new SdkListEntry(path, parts[1].Trim(), parts[2].Trim(), section == "installed"));
        }

        return entries;
    }

    /// <summary>Extracts a one-line version string from tool output (first non-empty line).</summary>
    public static string? FirstLine(string output) =>
        output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
}
