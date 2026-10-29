using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ISdkLocator _sdk;
    private readonly IJavaDiagnostics _java;

    public SettingsViewModel(ISettingsService settings, ISdkLocator sdk, IJavaDiagnostics java)
    {
        _settings = settings;
        _sdk = sdk;
        _java = java;
        Reload();
    }

    [ObservableProperty]
    private string _javaHomeText = string.Empty;

    [ObservableProperty]
    private int _goalIndex = 1;

    [ObservableProperty]
    private int _themeIndex = 2;

    [ObservableProperty]
    private bool _debugMode;

    public string AppVersion =>
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "dev";

    public bool IsInstalledCopy => WSA.Infrastructure.Installer.InstallLayout.RunsFromInstallDir;

    /// <summary>Starts the installer in maintenance mode (repair / uninstall) and closes the app so its files are not in use.</summary>
    [RelayCommand]
    private void OpenInstaller()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            psi.ArgumentList.Add("--maintenance");
            System.Diagnostics.Process.Start(psi)?.Dispose();
            Microsoft.UI.Xaml.Application.Current.Exit();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Message = $"Could not start the installer: {ex.Message}";
            MessageIsError = true;
        }
    }

    [RelayCommand]
    private void OpenWizard() => App.MainWindow?.NavigateTo("setup");

    [ObservableProperty]
    private string _javaReportText = string.Empty;

    [RelayCommand]
    private void ClearJavaHome() => JavaHomeText = string.Empty;

    /// <summary>Saves the JDK field and runs the real java.exe the SDK tools would get, showing exactly what it reported.</summary>
    [RelayCommand]
    private async Task TestJavaAsync()
    {
        IsBusy = true;
        try
        {
            var jdk = JavaHomeText.Trim().Trim('"').TrimEnd('\\', '/');
            _settings.Current.JavaHomeOverride = jdk.Length == 0 ? null : jdk;
            await _settings.SaveAsync();
            var report = await _java.ProbeAsync();
            JavaReportText = string.Join(Environment.NewLine, report.Lines);
            Message = report.MeetsMinimum
                ? $"Java {report.Major} will be used by sdkmanager, avdmanager and the emulator."
                : "This Java cannot be used - see the details below.";
            MessageIsError = !report.MeetsMinimum;
        }
        catch (Exception ex)
        {
            Message = $"Java test failed: {ex.Message}";
            MessageIsError = true;
        }
        finally
        {
            IsBusy = false;
        }
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
        JavaHomeText = _settings.Current.JavaHomeOverride ?? string.Empty;
        GoalIndex = _settings.Current.SetupGoal == SetupGoals.Phone ? 0 : 1;
        ThemeIndex = _settings.Current.Theme switch { "System" => 0, "Light" => 1, _ => 2 };
        DebugMode = _settings.Current.DebugMode;
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
            var jdk = JavaHomeText.Trim().Trim('"').TrimEnd('\\', '/');
            _settings.Current.JavaHomeOverride = jdk.Length == 0 ? null : jdk;
            _settings.Current.TerminalMaxLines = (int)TerminalMaxLines;
            _settings.Current.SetupGoal = GoalIndex == 0 ? SetupGoals.Phone : SetupGoals.Everything;
            _settings.Current.Theme = ThemeIndex switch { 0 => "System", 1 => "Light", _ => "Dark" };
            _settings.Current.DebugMode = DebugMode;
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
