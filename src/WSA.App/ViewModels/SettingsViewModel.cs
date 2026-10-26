using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WSA.Core.Interfaces;

namespace WSA.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ISdkLocator _sdk;

    public SettingsViewModel(ISettingsService settings, ISdkLocator sdk)
    {
        _settings = settings;
        _sdk = sdk;
        Reload();
    }

    [ObservableProperty]
    private string _sdkRootText = string.Empty;

    [ObservableProperty]
    private double _terminalMaxLines = 5000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageSeverity))]
    private bool _messageIsError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    public bool IsNotBusy => !IsBusy;

    public Microsoft.UI.Xaml.Controls.InfoBarSeverity MessageSeverity =>
        MessageIsError
            ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error
            : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public string SettingsFilePath => _settings.SettingsFilePath;

    /// <summary>Re-reads values from the loaded settings (called when the page is shown).</summary>
    public void Reload()
    {
        SdkRootText = _settings.Current.SdkRootOverride ?? string.Empty;
        TerminalMaxLines = _settings.Current.TerminalMaxLines;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var root = SdkRootText.Trim().Trim('"');
        if (root.Length > 0 && !Directory.Exists(root))
        {
            Message = $"The folder '{root}' does not exist. Choose an existing Android SDK folder or clear the field to use auto-detection.";
            MessageIsError = true;
            return;
        }

        if (double.IsNaN(TerminalMaxLines) || TerminalMaxLines < 200 || TerminalMaxLines > 100000)
        {
            Message = "Terminal buffer must be between 200 and 100000 lines.";
            MessageIsError = true;
            return;
        }

        IsBusy = true;
        try
        {
            _settings.Current.SdkRootOverride = root.Length == 0 ? null : root;
            _settings.Current.TerminalMaxLines = (int)TerminalMaxLines;
            await _settings.SaveAsync();

            var status = await _sdk.DetectAsync();
            Message = status.IsComplete
                ? "Saved. All Android SDK tools were found."
                : $"Saved. SDK detection result: {(status.SdkRoot is null ? "no SDK found" : "SDK found but incomplete")} - see Runtime & SDK.";
            MessageIsError = false;
        }
        catch (Exception ex)
        {
            Message = $"Could not save settings: {ex.Message}";
            MessageIsError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearSdkRoot()
    {
        SdkRootText = string.Empty;
    }
}
