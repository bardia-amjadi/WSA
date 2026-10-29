using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.App.ViewModels;

/// <summary>Information about the connected phone, shortcuts into Android's own Settings, and the Live View preferences.</summary>
public sealed partial class PhoneSettingsViewModel : PhoneAppViewModel
{
    private static readonly int[] FpsValues = { 15, 30, 60 };
    private static readonly int[] SizeValues = { 720, 1024, 1280, 1600 };

    private readonly IPhoneService _phone;
    private readonly IPhoneToolsService _tools;
    private readonly ISettingsService _settings;
    private bool _loading;

    public PhoneSettingsViewModel(IPhoneService phone, IPhoneToolsService tools, ISettingsService settings)
    {
        _phone = phone;
        _tools = tools;
        _settings = settings;
    }

    public ObservableCollection<InfoRow> Info { get; } = new();

    [ObservableProperty]
    private int _modeIndex;

    [ObservableProperty]
    private int _fpsIndex = 1;

    [ObservableProperty]
    private int _sizeIndex = 2;

    [ObservableProperty]
    private double _bitrate = 8;

    public override void Initialize(string serial)
    {
        base.Initialize(serial);
        _loading = true;
        var s = _settings.Current;
        ModeIndex = s.LiveViewMode == "screenshots" ? 1 : 0;
        FpsIndex = Math.Max(0, Array.IndexOf(FpsValues, s.LiveViewFps));
        SizeIndex = Math.Max(0, Array.IndexOf(SizeValues, s.LiveViewMaxSize));
        Bitrate = Math.Clamp(s.LiveViewBitrateMbps, 1, 40);
        _loading = false;
        _ = RefreshCommand.ExecuteAsync(null);
    }

    partial void OnModeIndexChanged(int value) => SavePreferences();

    partial void OnFpsIndexChanged(int value) => SavePreferences();

    partial void OnSizeIndexChanged(int value) => SavePreferences();

    partial void OnBitrateChanged(double value) => SavePreferences();

    private async void SavePreferences()
    {
        if (_loading)
        {
            return;
        }

        var s = _settings.Current;
        s.LiveViewMode = ModeIndex == 1 ? "screenshots" : "stream";
        s.LiveViewFps = FpsValues[Math.Clamp(FpsIndex, 0, FpsValues.Length - 1)];
        s.LiveViewMaxSize = SizeValues[Math.Clamp(SizeIndex, 0, SizeValues.Length - 1)];
        s.LiveViewBitrateMbps = (int)Math.Clamp(Bitrate, 1, 40);
        try
        {
            await _settings.SaveAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Could not save: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task RefreshAsync() =>
        RunAsync("Reading phone information...", async (_, ct) =>
        {
            var info = await _phone.GetInfoAsync(Serial, ct);
            var status = await _tools.GetStatusAsync(Serial, ct);
            Info.Clear();
            Info.Add(new InfoRow("Model", $"{info.Manufacturer} {info.Model}".Trim()));
            Info.Add(new InfoRow("Android", $"{info.AndroidVersion} (API {info.ApiLevel})"));
            if (info.Abi is not null) Info.Add(new InfoRow("CPU", info.Abi));
            if (info.Resolution is not null) Info.Add(new InfoRow("Screen", info.Resolution));
            Info.Add(new InfoRow("Battery", status.BatteryPercent is null ? "unknown" : $"{status.BatteryPercent}%{(status.Charging == true ? " (charging)" : string.Empty)}"));
            if (status.StorageFree is not null) Info.Add(new InfoRow("Storage", $"{status.StorageFree} free of {status.StorageTotal}"));
            if (status.WifiAddress is not null) Info.Add(new InfoRow("Wi-Fi", status.WifiAddress));
            Info.Add(new InfoRow("Serial", Serial));
            return null;
        });

    [RelayCommand]
    private Task OpenPageAsync(string page) =>
        RunAsync("Opening on the phone...", async (_, ct) =>
        {
            await _tools.OpenAndroidSettingsAsync(Serial, page, ct);
            return "Opened on the phone. Look at its screen (or the Live View).";
        });
}
