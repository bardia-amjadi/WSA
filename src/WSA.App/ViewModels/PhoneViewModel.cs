using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.App.ViewModels;

public sealed record PhoneRow(string Serial, string State, string Kind, string Model)
{
    public bool IsReady => State == "device";

    public string Display => $"{(Model.Length > 0 ? Model : Serial)}  -  {Kind}, {State}";
}

public sealed record InfoRow(string Label, string Value);

public partial class PhoneViewModel : OperationViewModelBase
{
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(3);

    private readonly IAdbService _adb;
    private readonly IPhoneService _phone;
    private readonly ISdkSetupService _setup;
    private readonly ISdkLocator _sdk;

    private CancellationTokenSource? _liveCts;
    private CancellationTokenSource? _watchCts;
    private bool _rebuilding;
    private string? _loadedKey;

    public PhoneViewModel(IAdbService adb, IPhoneService phone, ISdkSetupService setup, ISdkLocator sdk)
    {
        _adb = adb;
        _phone = phone;
        _setup = setup;
        _sdk = sdk;
    }

    /// <summary>Set by the page: generic confirmation (title, message, primary button text) -> true if confirmed.</summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    public ObservableCollection<PhoneRow> Devices { get; } = new();

    public ObservableCollection<InfoRow> InfoRows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeviceReady))]
    [NotifyPropertyChangedFor(nameof(CanDisconnect))]
    private PhoneRow? _selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    private string _stateHint = string.Empty;

    [ObservableProperty]
    private bool _adbMissing;

    [ObservableProperty]
    private bool _isLive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private ImageSource? _frame;

    public bool ShowPlaceholder => Frame is null;

    [ObservableProperty]
    private string _liveStatus = "Live view is off.";

    [ObservableProperty]
    private string _textToSend = string.Empty;

    public int FramePixelWidth { get; private set; }

    public int FramePixelHeight { get; private set; }

    public bool IsDeviceReady => SelectedDevice?.IsReady == true;

    public bool CanDisconnect => SelectedDevice?.Serial.Contains(':') == true;

    public bool HasHint => !string.IsNullOrEmpty(StateHint);

    partial void OnSelectedDeviceChanged(PhoneRow? value)
    {
        if (_rebuilding)
        {
            return;
        }

        ApplySelection(value);
    }

    partial void OnIsLiveChanged(bool value)
    {
        if (value)
        {
            StartLive();
        }
        else
        {
            StopLiveLoop();
            LiveStatus = "Live view is off.";
        }
    }

    [RelayCommand]
    private Task RefreshAsync() =>
        RunAsync("Looking for devices...", async (_, ct) =>
        {
            await RefreshDevicesCoreAsync(ct);
            return null;
        });

    /// <summary>Starts polling adb for device changes (cable plugged in, authorization granted...). Call when the page is shown.</summary>
    public void StartWatching()
    {
        StopWatching();
        _watchCts = new CancellationTokenSource();
        _ = WatchLoopAsync(_watchCts.Token);
    }

    /// <summary>Stops polling and the live view. Call when the page is left.</summary>
    public void StopWatching()
    {
        _watchCts?.Cancel();
        _watchCts = null;
        IsLive = false;
        StopLiveLoop();
    }

    public async Task InstallAdbAsync()
    {
        var confirmed = ConfirmAsync is { } ask && await ask(
            "Install adb?",
            "Android SDK Platform-Tools (adb, about 10 MB) will be downloaded from Google and checked against Google's published checksum. " +
            "It is covered by the Android Software Development Kit License Agreement (https://developer.android.com/studio/terms). " +
            "No Java, emulator or Android image is needed to connect a phone.",
            "Accept and install");
        if (!confirmed)
        {
            return;
        }

        await RunAsync("Installing adb...", async (progress, ct) =>
        {
            await _setup.InstallPlatformToolsDirectAsync(progress, ct);
            return "adb is installed. Connect your phone with a USB cable and enable USB debugging.";
        });
    }

    public Task InstallApkAsync(string apkPath) =>
        RunAsync($"Installing {Path.GetFileName(apkPath)}...", async (progress, ct) =>
        {
            var serial = RequireReadySerial();
            var result = await _phone.InstallApkAsync(serial, apkPath, new OutputAdapter(progress), ct);
            return result.Success ? result.Message : throw new ToolFailedException(result.Message);
        });

    public Task SaveScreenshotAsync(string destinationPath) =>
        RunAsync("Capturing screenshot...", async (_, ct) =>
        {
            var png = await _phone.CaptureScreenAsync(RequireReadySerial(), ct);
            await File.WriteAllBytesAsync(destinationPath, png, ct);
            return $"Screenshot saved to {destinationPath}";
        });

    /// <summary>Pairs first if a pairing address is given (Android 11+), then connects over Wi-Fi.</summary>
    public Task PairAndConnectAsync(string connectAddress, string pairAddress, string pairCode) =>
        RunAsync("Connecting over Wi-Fi...", async (_, ct) =>
        {
            if (!string.IsNullOrWhiteSpace(pairAddress))
            {
                var pair = await _phone.PairAsync(pairAddress.Trim(), pairCode.Trim(), ct);
                if (!pair.Success)
                {
                    throw new ToolFailedException(pair.Message);
                }
            }

            var connect = await _phone.ConnectAsync(connectAddress.Trim(), ct);
            return connect.Success ? connect.Message : throw new ToolFailedException(connect.Message);
        });

    [RelayCommand]
    private Task DisconnectAsync() =>
        RunAsync("Disconnecting...", async (_, ct) =>
        {
            var serial = SelectedDevice?.Serial ?? throw new ToolFailedException("No device selected.");
            var result = await _phone.DisconnectAsync(serial, ct);
            return result.Success ? result.Message : throw new ToolFailedException(result.Message);
        });

    [RelayCommand]
    private Task SendKeyAsync(string? key) =>
        string.IsNullOrEmpty(key) ? Task.CompletedTask : GuardAsync(serial => _phone.KeyAsync(serial, key));

    [RelayCommand]
    private async Task SendTextAsync()
    {
        var text = TextToSend;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        await GuardAsync(serial => _phone.TypeTextAsync(serial, text));
        if (ErrorMessage is null)
        {
            TextToSend = string.Empty;
        }
    }

    public Task TapAsync(int x, int y) => GuardAsync(serial => _phone.TapAsync(serial, x, y));

    public Task SwipeAsync(int x1, int y1, int x2, int y2, int durationMs) =>
        GuardAsync(serial => _phone.SwipeAsync(serial, x1, y1, x2, y2, durationMs));

    protected override async Task OnOperationFinishedAsync()
    {
        try
        {
            await RefreshDevicesCoreAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is ToolFailedException or ToolUnavailableException)
        {
            StateHint = ex.Message;
        }
    }

    private async Task GuardAsync(Func<string, Task> action)
    {
        if (SelectedDevice is not { IsReady: true } device)
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            await action(device.Serial);
        }
        catch (Exception ex) when (ex is ToolFailedException or ToolUnavailableException)
        {
            ErrorMessage = ex.Message;
        }
    }

    private string RequireReadySerial() =>
        SelectedDevice is { IsReady: true } d
            ? d.Serial
            : throw new ToolFailedException("Select a connected, authorized device first.");

    private async Task WatchLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!IsBusy)
                {
                    try
                    {
                        await RefreshDevicesCoreAsync(ct);
                    }
                    catch (Exception ex) when (ex is ToolFailedException or ToolUnavailableException)
                    {
                        StateHint = ex.Message;
                    }
                }

                await Task.Delay(WatchInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Page left.
        }
    }

    private async Task RefreshDevicesCoreAsync(CancellationToken ct)
    {
        var status = _sdk.Last ?? await _sdk.DetectAsync(ct);
        AdbMissing = status.Get(SdkTool.Adb).Path is null;
        if (AdbMissing)
        {
            ReplaceDevices(Array.Empty<PhoneRow>());
            UpdateHint();
            return;
        }

        var devices = await _adb.ListDevicesAsync(ct);
        ReplaceDevices(devices.Select(ToRow).ToList());
        UpdateHint();
    }

    private static PhoneRow ToRow(AdbDevice d)
    {
        var kind = d.Serial.StartsWith("emulator-", StringComparison.Ordinal) ? "emulator"
            : d.Serial.Contains(':') ? "Wi-Fi" : "USB";

        // "adb devices -l" details look like: usb:1-1 product:x model:Pixel_7 device:y transport_id:1
        var model = string.Empty;
        foreach (var token in (d.Details ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("model:", StringComparison.Ordinal))
            {
                model = token["model:".Length..].Replace('_', ' ');
            }
        }

        return new PhoneRow(d.Serial, d.State, kind, model);
    }

    private void ReplaceDevices(IReadOnlyList<PhoneRow> rows)
    {
        if (rows.SequenceEqual(Devices))
        {
            return;
        }

        var keep = SelectedDevice?.Serial;
        _rebuilding = true;
        try
        {
            Devices.Clear();
            foreach (var row in rows)
            {
                Devices.Add(row);
            }

            SelectedDevice = rows.FirstOrDefault(r => r.Serial == keep)
                             ?? rows.FirstOrDefault(r => r.IsReady && r.Kind != "emulator")
                             ?? rows.FirstOrDefault(r => r.IsReady)
                             ?? rows.FirstOrDefault();
        }
        finally
        {
            _rebuilding = false;
        }

        ApplySelection(SelectedDevice);
    }

    private void ApplySelection(PhoneRow? value)
    {
        var key = value is null ? null : $"{value.Serial}|{value.State}";
        if (key == _loadedKey)
        {
            return;
        }

        _loadedKey = key;
        IsLive = false;
        InfoRows.Clear();
        Frame = null;
        UpdateHint();

        if (value is { IsReady: true })
        {
            _ = LoadInfoAsync(value.Serial, key!);
        }
    }

    private async Task LoadInfoAsync(string serial, string key)
    {
        try
        {
            var info = await _phone.GetInfoAsync(serial);
            if (_loadedKey != key)
            {
                return;
            }

            InfoRows.Clear();
            InfoRows.Add(new InfoRow("Model", $"{info.Manufacturer} {info.Model}"));
            InfoRows.Add(new InfoRow("Android", $"{info.AndroidVersion} (API {info.ApiLevel})"));
            if (info.Resolution is not null) InfoRows.Add(new InfoRow("Screen", info.Resolution));
            if (info.Battery is not null) InfoRows.Add(new InfoRow("Battery", info.Battery));
            if (info.Abi is not null) InfoRows.Add(new InfoRow("CPU ABI", info.Abi));
            InfoRows.Add(new InfoRow("Serial", serial));
        }
        catch (Exception ex) when (ex is ToolFailedException or ToolUnavailableException)
        {
            if (_loadedKey == key)
            {
                ErrorMessage = ex.Message;
            }
        }
    }

    private void UpdateHint()
    {
        if (AdbMissing)
        {
            StateHint = "adb is not installed yet. It is the only thing needed to connect a phone (about 10 MB).";
        }
        else if (Devices.Count == 0)
        {
            StateHint =
                "No device found. 1) Use a data-capable USB cable. 2) On the phone: Settings > About phone > tap Build number 7 times, " +
                "then Settings > Developer options > turn on USB debugging. 3) Unlock the phone and allow the computer. " +
                "Some brands need their USB driver installed on Windows. Or connect over Wi-Fi (button above).";
        }
        else if (SelectedDevice is { State: "unauthorized" })
        {
            StateHint = "The phone has not authorized this computer. Unlock it and tap Allow on the USB debugging prompt (tick Always allow).";
        }
        else if (SelectedDevice is { State: "offline" })
        {
            StateHint = "The device is offline. Reconnect the cable, or switch USB debugging off and on again.";
        }
        else if (SelectedDevice is { IsReady: false })
        {
            StateHint = $"The device reports state '{SelectedDevice.State}', so it cannot be controlled yet.";
        }
        else
        {
            StateHint = string.Empty;
        }
    }

    private void StartLive()
    {
        if (SelectedDevice is not { IsReady: true } device)
        {
            LiveStatus = "Select a connected, authorized device first.";
            IsLive = false;
            return;
        }

        StopLiveLoop();
        _liveCts = new CancellationTokenSource();
        _ = LiveLoopAsync(device.Serial, _liveCts.Token);
    }

    private void StopLiveLoop()
    {
        // Not disposed on purpose: the loop may still be observing the token.
        _liveCts?.Cancel();
        _liveCts = null;
    }

    private async Task LiveLoopAsync(string serial, CancellationToken ct)
    {
        LiveStatus = "Starting live view...";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                var png = await _phone.CaptureScreenAsync(serial, ct);
                await SetFrameAsync(png);
                LiveStatus = $"{FramePixelWidth} x {FramePixelHeight}, about {1000.0 / Math.Max(1, sw.ElapsedMilliseconds):0.0} frames/s";

                var rest = 150 - (int)sw.ElapsedMilliseconds;
                if (rest > 0)
                {
                    await Task.Delay(rest, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user or by leaving the page.
        }
        catch (Exception ex) when (ex is ToolFailedException or ToolUnavailableException)
        {
            ErrorMessage = $"Live view stopped: {ex.Message}";
            if (!ct.IsCancellationRequested)
            {
                IsLive = false;
            }
        }
    }

    private async Task SetFrameAsync(byte[] png)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        FramePixelWidth = bitmap.PixelWidth;
        FramePixelHeight = bitmap.PixelHeight;
        Frame = bitmap;
    }

    /// <summary>Adapts SetupProgress reporting to the OutputLine stream the phone service writes.</summary>
    private sealed class OutputAdapter : IProgress<OutputLine>
    {
        private readonly IProgress<SetupProgress> _inner;

        public OutputAdapter(IProgress<SetupProgress> inner) => _inner = inner;

        public void Report(OutputLine value) => _inner.Report(new SetupProgress(value.Text));
    }
}
