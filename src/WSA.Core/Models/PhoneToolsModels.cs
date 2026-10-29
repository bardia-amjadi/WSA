namespace WSA.Core.Models;

/// <summary>One entry of a directory on the phone (from <c>ls -lA</c>).</summary>
public sealed record DeviceFile(string Name, bool IsDirectory, bool IsLink, long Size, string Modified, string Permissions)
{
    public string SizeText => IsDirectory ? string.Empty : Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{Size / (1024.0 * 1024):F1} MB",
        _ => $"{Size / (1024.0 * 1024 * 1024):F2} GB",
    };

    public string Kind => IsDirectory ? "Folder" : IsLink ? "Link" : "File";
}

/// <summary>An app installed on the phone.</summary>
public sealed record InstalledApp(string Package, bool IsSystem)
{
    public string Label => IsSystem ? "system" : string.Empty;
}

/// <summary>Live numbers shown on the desktop menu bar and in Settings.</summary>
public sealed record DeviceStatus(
    string? BatteryPercent, bool? Charging, string? WifiAddress, string? StorageFree, string? StorageTotal);

/// <summary>Settings for the H.264 screen stream.</summary>
public sealed record ScreenStreamOptions(int Fps, int MaxSize, int BitrateMbps);
