namespace WSA.Core.Models;

public enum SdkTool
{
    SdkManager,
    AvdManager,
    Emulator,
    Adb,
}

/// <summary>Result of looking for one SDK executable.</summary>
public sealed record ToolStatus(SdkTool Tool, string DisplayName, string? Path, string? Version, string? Problem)
{
    public bool IsAvailable => Path is not null && Problem is null;
}

/// <summary>Result of SDK discovery plus per-tool diagnostics.</summary>
public sealed record SdkStatus(
    string? SdkRoot,
    string SdkRootSource,
    IReadOnlyList<ToolStatus> Tools,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyDictionary<string, string> ToolEnvironment,
    JavaResolution Java)
{
    public ToolStatus Get(SdkTool tool) => Tools.First(t => t.Tool == tool);
    public bool IsComplete => SdkRoot is not null && Tools.All(t => t.IsAvailable);
}

public sealed record AvdInfo(
    string Name,
    string? Device,
    string? Path,
    string? Target,
    string? BasedOn,
    string? Abi,
    bool IsRunning,
    string? RunningSerial);

public sealed record AdbDevice(string Serial, string State, string? Details);
