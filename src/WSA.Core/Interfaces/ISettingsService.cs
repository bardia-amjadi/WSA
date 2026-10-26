using WSA.Core.Models;

namespace WSA.Core.Interfaces;

public interface ISettingsService
{
    AppSettings Current { get; }
    string SettingsFilePath { get; }
    event EventHandler? Changed;

    /// <summary>Loads settings from disk. Returns a warning message if the file was unreadable (defaults are then used), otherwise null.</summary>
    Task<string?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(CancellationToken cancellationToken = default);
}
