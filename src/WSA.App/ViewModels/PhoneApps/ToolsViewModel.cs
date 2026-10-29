using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.App.ViewModels;

public sealed partial class ToolsViewModel : PhoneAppViewModel
{
    private readonly IPhoneToolsService _tools;
    private readonly IPhoneService _phone;

    public ToolsViewModel(IPhoneToolsService tools, IPhoneService phone)
    {
        _tools = tools;
        _phone = phone;
    }

    [ObservableProperty]
    private double _brightness = 60;

    [ObservableProperty]
    private string? _lastFile;

    private static string OutputFolder
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WSA");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    [RelayCommand]
    private Task ScreenshotAsync() =>
        RunAsync("Taking a screenshot...", async (_, ct) =>
        {
            var png = await _phone.CaptureScreenAsync(Serial, ct);
            var path = Path.Combine(OutputFolder, $"phone-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            await File.WriteAllBytesAsync(path, png, ct);
            LastFile = path;
            return $"Saved {path}";
        });

    [RelayCommand]
    private Task RecordAsync(string seconds) =>
        RunAsync($"Recording the screen for {seconds} s...", async (_, ct) =>
        {
            var path = await _tools.RecordScreenAsync(Serial, int.Parse(seconds), OutputFolder, ct);
            LastFile = path;
            return $"Saved {path}";
        });

    [RelayCommand]
    private void OpenOutputFolder()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe");
            psi.ArgumentList.Add(OutputFolder);
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>Parameter looks like "wifi:on" or "touches:off".</summary>
    [RelayCommand]
    private Task SwitchAsync(string spec)
    {
        var parts = spec.Split(':');
        return RunAsync("Changing setting...", async (_, ct) =>
        {
            await _tools.SetToggleAsync(Serial, parts[0], parts[1] == "on", ct);
            return $"{parts[0]} turned {parts[1]}.";
        });
    }

    [RelayCommand]
    private Task ApplyBrightnessAsync() =>
        RunAsync("Setting brightness...", async (_, ct) =>
        {
            await _tools.SetBrightnessAsync(Serial, (int)Brightness, ct);
            return $"Brightness set to {(int)Brightness}% (auto-brightness is now off).";
        });

    [RelayCommand]
    private async Task RebootAsync(string mode)
    {
        var what = mode == "system" ? "restart" : $"restart into {mode}";
        if (!await AskAsync("Restart the phone?", $"The phone will {what}. Unsaved work on it may be lost.", "Restart"))
        {
            return;
        }

        await RunAsync("Restarting...", async (_, ct) =>
        {
            await _tools.RebootAsync(Serial, mode, ct);
            return "The phone is restarting. The desktop reconnects when it is back.";
        });
    }

    [RelayCommand]
    private Task WifiAdbAsync() =>
        RunAsync("Switching adb to Wi-Fi...", async (_, ct) =>
        {
            var ip = await _tools.EnableWifiAdbAsync(Serial, ct);
            return ip is null
                ? "adb now listens on port 5555. Find the phone's Wi-Fi address in Android settings, then use 'Connect over Wi-Fi' on the Phone page."
                : $"adb now listens on {ip}:5555. Use 'Connect over Wi-Fi' on the Phone page with that address.";
        });
}
