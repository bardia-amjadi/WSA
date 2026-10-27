using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.App.ViewModels;

public partial class RuntimeViewModel : ObservableObject
{
    private readonly ISdkLocator _sdk;
    private string? _root;

    public RuntimeViewModel(ISdkLocator sdk) => _sdk = sdk;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private string _sdkRoot = "-";

    [ObservableProperty]
    private string _sdkSource = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSdkFolderCommand))]
    private bool _hasSdkRoot;

    public bool IsNotBusy => !IsBusy;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public ObservableCollection<ToolRow> Tools { get; } = new();

    public ObservableCollection<string> Diagnostics { get; } = new();

    public string SetupGuide =>
        "Easiest: open the Setup page and let WSA install everything. The steps below are the manual alternative.\r\n\r\n" +
        "1. Install a JDK (17 or newer) - sdkmanager and avdmanager are Java programs.\r\n" +
        "2. Download \"Command line tools only\" from https://developer.android.com/studio#command-line-tools-only and extract it to <SDK root>\\cmdline-tools\\latest.\r\n" +
        "3. In WSA Terminal run:  sdkmanager --licenses   (read and accept the licenses yourself).\r\n" +
        "4. Run:  sdkmanager \"platform-tools\" \"emulator\" \"system-images;android-34;google_apis;x86_64\"\r\n" +
        "5. Create a device:  avdmanager create avd -n Pixel_34 -k \"system-images;android-34;google_apis;x86_64\" -d pixel_6\r\n" +
        "6. Press Re-detect. If your SDK lives elsewhere, set the SDK root in Settings.";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var status = await _sdk.DetectAsync();
            _root = status.SdkRoot;
            HasSdkRoot = status.SdkRoot is not null && Directory.Exists(status.SdkRoot);
            SdkRoot = status.SdkRoot ?? "No Android SDK found";
            SdkSource = status.SdkRootSource;

            Tools.Clear();
            foreach (var tool in status.Tools)
            {
                Tools.Add(ToolRow.From(tool));
            }

            Diagnostics.Clear();
            foreach (var line in status.Diagnostics)
            {
                Diagnostics.Add(line);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSdkRoot))]
    private async Task OpenSdkFolderAsync()
    {
        if (_root is null)
        {
            return;
        }

        try
        {
            var ok = await Windows.System.Launcher.LaunchFolderPathAsync(_root);
            if (!ok)
            {
                ErrorMessage = $"Windows could not open '{_root}'.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not open the SDK folder: {ex.Message}";
        }
    }
}
