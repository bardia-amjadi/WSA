using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using WSA.Core.Interfaces;

namespace WSA.App.ViewModels;

/// <summary>Menu bar values of the phone desktop: device name, battery, Wi-Fi address and a clock.</summary>
public sealed partial class PhoneDesktopViewModel : ObservableObject
{
    private readonly IPhoneService _phone;
    private readonly IPhoneToolsService _tools;
    private DispatcherQueueTimer? _clock;
    private DispatcherQueueTimer? _status;

    public PhoneDesktopViewModel(IPhoneService phone, IPhoneToolsService tools)
    {
        _phone = phone;
        _tools = tools;
    }

    public string Serial { get; private set; } = string.Empty;

    [ObservableProperty]
    private string _deviceName = "Phone";

    [ObservableProperty]
    private string _batteryText = string.Empty;

    [ObservableProperty]
    private string _networkText = string.Empty;

    [ObservableProperty]
    private string _clockText = string.Empty;

    public void Start(string serial)
    {
        Serial = serial;
        DeviceName = serial;
        UpdateClock();

        var queue = DispatcherQueue.GetForCurrentThread();
        _clock = queue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(15);
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();

        _status = queue.CreateTimer();
        _status.Interval = TimeSpan.FromSeconds(30);
        _status.Tick += async (_, _) => await UpdateStatusAsync();
        _status.Start();

        _ = LoadNameAsync();
        _ = UpdateStatusAsync();
    }

    public void Stop()
    {
        _clock?.Stop();
        _status?.Stop();
    }

    private void UpdateClock() => ClockText = DateTime.Now.ToString("ddd HH:mm");

    private async Task LoadNameAsync()
    {
        try
        {
            var info = await _phone.GetInfoAsync(Serial);
            DeviceName = $"{info.Manufacturer} {info.Model}".Trim();
        }
        catch (Exception ex) when (ex is Core.Models.ToolFailedException or Core.Models.ToolUnavailableException)
        {
            // The name stays as the serial.
        }
    }

    private async Task UpdateStatusAsync()
    {
        try
        {
            var s = await _tools.GetStatusAsync(Serial);
            BatteryText = s.BatteryPercent is null ? string.Empty : $"{s.BatteryPercent}%{(s.Charging == true ? " +" : string.Empty)}";
            NetworkText = s.WifiAddress ?? string.Empty;
        }
        catch (Exception ex) when (ex is Core.Models.ToolFailedException or Core.Models.ToolUnavailableException)
        {
            // Keep the last values; the menu bar is informational.
        }
    }
}
