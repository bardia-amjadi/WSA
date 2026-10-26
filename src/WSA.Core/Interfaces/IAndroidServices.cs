using WSA.Core.Models;

namespace WSA.Core.Interfaces;

/// <summary>Finds the Android SDK and its command-line tools and reports real diagnostics.</summary>
public interface ISdkLocator
{
    /// <summary>The most recent detection result, or null if detection has not run yet.</summary>
    SdkStatus? Last { get; }

    event EventHandler<SdkStatus>? StatusChanged;

    Task<SdkStatus> DetectAsync(CancellationToken cancellationToken = default);
}

/// <summary>Builds launch requests for SDK executables from the last detected SDK status.</summary>
public interface IToolCommandFactory
{
    /// <summary>Creates a request for a well-known SDK tool. Returns null and an actionable <paramref name="error"/> if it is unavailable.</summary>
    CommandRequest? TryCreate(SdkTool tool, IReadOnlyList<string> args, out string? error);

    /// <summary>Creates a request for an executable inside the SDK root. Paths that escape the root are rejected.</summary>
    CommandRequest? TryCreateRelative(string relativePath, IReadOnlyList<string> args, out string? error);
}

/// <summary>ADB operations used by the UI and terminal. All results come from the real adb binary.</summary>
public interface IAdbService
{
    Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks a running emulator (via <c>adb -s serial emu avd name</c>) which AVD it is running.</summary>
    Task<string?> GetEmulatorAvdNameAsync(string serial, CancellationToken cancellationToken = default);
}

/// <summary>Create, start, stop and delete AVDs using the official SDK tools only.</summary>
public interface IAvdLifecycleService
{
    /// <summary>Scans the SDK's system-images folder. Returns an empty list if none are installed.</summary>
    Task<IReadOnlyList<SystemImageInfo>> ListSystemImagesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceProfile>> ListDeviceProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates <paramref name="name"/>; returns an error message, or null if valid.</summary>
    string? ValidateName(string name);

    Task<OperationResult> CreateAsync(CreateAvdRequest request, IProgress<OutputLine> output, CancellationToken cancellationToken = default);

    /// <summary>Launches <c>emulator -avd NAME</c> as an independent process and reports early failure. Does not wait for boot.</summary>
    Task<OperationResult> StartAsync(string avdName, CancellationToken cancellationToken = default);

    /// <summary>Stops a running emulator using <c>adb -s SERIAL emu kill</c>. Never kills processes by name.</summary>
    Task<OperationResult> StopAsync(string serial, IProgress<OutputLine> output, CancellationToken cancellationToken = default);

    Task<OperationResult> DeleteAsync(string avdName, IProgress<OutputLine> output, CancellationToken cancellationToken = default);
}

/// <summary>AVD enumeration backed by <c>avdmanager</c>, <c>emulator</c> and <c>adb</c>.</summary>
public interface IAvdService
{
    Task<IReadOnlyList<AvdInfo>> ListAsync(CancellationToken cancellationToken = default);
}
