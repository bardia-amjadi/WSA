using WSA.Core.Models;

namespace WSA.Core.Interfaces;

/// <summary>A running H.264 screen stream from one phone. Dispose to stop it and remove the adb forward.</summary>
public interface IScreenStreamSession : IAsyncDisposable
{
    /// <summary>Raw H.264 (Annex B) video as produced by the phone.</summary>
    Stream Video { get; }

    string ServerVersion { get; }
}

/// <summary>
/// Streams the phone's screen as H.264 using the open-source scrcpy server (Apache-2.0), which WSA downloads once from the
/// official scrcpy GitHub release (SHA-256 verified) and pushes to the phone through adb.
/// </summary>
public interface IScreenStreamService
{
    Task<IScreenStreamSession> StartAsync(
        string serial, ScreenStreamOptions options, IProgress<string> status, CancellationToken ct = default);
}
