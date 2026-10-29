using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WSA.Core.Interfaces;

namespace WSA.App.ViewModels;

/// <summary>State and button commands of the Live View app. The video itself is handled by the view.</summary>
public sealed partial class LiveViewViewModel : PhoneAppViewModel
{
    private readonly IPhoneService _phone;

    public LiveViewViewModel(IPhoneService phone) => _phone = phone;

    [ObservableProperty]
    private string _statusText = "Starting...";

    [ObservableProperty]
    private string _fpsText = string.Empty;

    [ObservableProperty]
    private string _textToSend = string.Empty;

    /// <summary>Raised when the user asks to restart the stream (for example after changing quality).</summary>
    public event EventHandler? RestartRequested;

    [RelayCommand]
    private void Restart() => RestartRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task KeyAsync(string key)
    {
        try
        {
            await _phone.KeyAsync(Serial, key);
        }
        catch (Exception ex) when (ex is Core.Models.ToolFailedException or Core.Models.ToolUnavailableException)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SendTextAsync()
    {
        var text = TextToSend;
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            await _phone.TypeTextAsync(Serial, text);
            TextToSend = string.Empty;
        }
        catch (Exception ex) when (ex is Core.Models.ToolFailedException or Core.Models.ToolUnavailableException)
        {
            ErrorMessage = ex.Message;
        }
    }

    public Task TapAsync(int x, int y) => Guard(() => _phone.TapAsync(Serial, x, y));

    public Task SwipeAsync(int x1, int y1, int x2, int y2, int ms) => Guard(() => _phone.SwipeAsync(Serial, x1, y1, x2, y2, ms));

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is Core.Models.ToolFailedException or Core.Models.ToolUnavailableException)
        {
            ErrorMessage = ex.Message;
        }
    }
}
