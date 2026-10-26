using System.Text.Json;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.Infrastructure.Configuration;

/// <summary>Stores <see cref="AppSettings"/> as JSON under %LOCALAPPDATA%\WSA (per user, no admin rights needed).</summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsService(string? directoryOverride = null)
    {
        var dir = directoryOverride
                  ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WSA");
        SettingsFilePath = Path.Combine(dir, "settings.json");
    }

    public AppSettings Current { get; private set; } = new();

    public string SettingsFilePath { get; }

    public event EventHandler? Changed;

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                Current = new AppSettings();
                return null;
            }

            try
            {
                await using var stream = File.OpenRead(SettingsFilePath);
                var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                Current = loaded ?? new AppSettings();
                return null;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Current = new AppSettings();
                return $"Settings file '{SettingsFilePath}' could not be read ({ex.Message}). Defaults are in use; saving will overwrite it.";
            }
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            var temp = SettingsFilePath + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, Current, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, SettingsFilePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
