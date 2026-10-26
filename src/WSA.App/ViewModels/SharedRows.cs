using WSA.Core.Models;

namespace WSA.App.ViewModels;

/// <summary>Display row for one SDK tool, built only from real detection results.</summary>
public sealed record ToolRow(string Name, string State, string Location, string Version, string Problem)
{
    public bool HasProblem => Problem.Length > 0;

    public static ToolRow From(ToolStatus tool) => new(
        tool.DisplayName,
        tool.IsAvailable ? "Ready" : tool.Path is null ? "Missing" : "Problem",
        tool.Path ?? "Not found",
        tool.Version ?? "-",
        tool.Problem is not null && tool.Path is not null ? tool.Problem : string.Empty);
}

/// <summary>Display row for one AVD, built only from avdmanager/emulator/adb output.</summary>
public sealed record AvdRow(string Name, string Details, string State, bool IsRunning, string Path, string? Serial = null)
{
    public bool CanStart => !IsRunning;

    public bool CanStop => IsRunning;

    public static AvdRow From(AvdInfo avd)
    {
        var details = string.Join("  |  ", new[] { avd.Device, avd.BasedOn ?? avd.Target, avd.Abi }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        return new AvdRow(
            avd.Name,
            details.Length > 0 ? details : "Details unavailable (listed via emulator only)",
            avd.IsRunning ? $"Running ({avd.RunningSerial})" : "Stopped",
            avd.IsRunning,
            avd.Path ?? string.Empty,
            avd.RunningSerial);
    }
}

/// <summary>Display row for a device that adb reports (emulators and physical devices).</summary>
public sealed record AdbRow(string Serial, string State, string Details)
{
    public static AdbRow From(AdbDevice d) => new(d.Serial, d.State, d.Details ?? string.Empty);
}
