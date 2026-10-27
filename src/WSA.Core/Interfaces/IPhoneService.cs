using WSA.Core.Models;

namespace WSA.Core.Interfaces;

public sealed record PhoneInfo(
    string Model,
    string Manufacturer,
    string AndroidVersion,
    string ApiLevel,
    string? Abi,
    string? Battery,
    string? Resolution);

/// <summary>
/// Talks to a real phone (or emulator) through adb only. All inputs are validated before they reach adb, and
/// methods throw <see cref="ToolFailedException"/> / <see cref="ToolUnavailableException"/> with user-facing messages.
/// </summary>
public interface IPhoneService
{
    Task<PhoneInfo> GetInfoAsync(string serial, CancellationToken cancellationToken = default);

    /// <summary>Returns a PNG of the current screen (<c>adb exec-out screencap -p</c>).</summary>
    Task<byte[]> CaptureScreenAsync(string serial, CancellationToken cancellationToken = default);

    Task TapAsync(string serial, int x, int y, CancellationToken cancellationToken = default);

    Task SwipeAsync(string serial, int x1, int y1, int x2, int y2, int durationMs, CancellationToken cancellationToken = default);

    /// <summary>Sends one key from a fixed allow-list (BACK, HOME, APP_SWITCH, POWER, VOLUME_UP, VOLUME_DOWN, ENTER, DEL).</summary>
    Task KeyAsync(string serial, string keyName, CancellationToken cancellationToken = default);

    /// <summary>Types ASCII text (letters, digits, space and . , _ @ -). Other characters are rejected, not silently dropped.</summary>
    Task TypeTextAsync(string serial, string text, CancellationToken cancellationToken = default);

    Task<OperationResult> InstallApkAsync(string serial, string apkPath, IProgress<OutputLine> output, CancellationToken cancellationToken = default);

    /// <summary>Android 11+ wireless debugging: <c>adb pair host:port code</c>.</summary>
    Task<OperationResult> PairAsync(string hostPort, string code, CancellationToken cancellationToken = default);

    /// <summary><c>adb connect host:port</c>. Checks the printed text because adb exits 0 even when it fails.</summary>
    Task<OperationResult> ConnectAsync(string hostPort, CancellationToken cancellationToken = default);

    Task<OperationResult> DisconnectAsync(string serial, CancellationToken cancellationToken = default);
}
