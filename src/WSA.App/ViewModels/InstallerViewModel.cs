using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WSA.Core.Models;
using WSA.Infrastructure.Installer;

namespace WSA.App.ViewModels;

/// <summary>One selectable piece in the installer: the app, the JDK, the SDK tools or the system image.</summary>
public sealed partial class ComponentRow : ObservableObject
{
    public ComponentRow(ComponentState state)
    {
        Component = state.Component;
        Title = state.Title;
        Detail = state.Detail;
        Installed = state.Installed;
        IsSelected = true;
    }

    public InstallComponent Component { get; }

    public string Title { get; }

    public string Detail { get; }

    public bool Installed { get; }

    public string Status => Installed ? "Installed" : "Not installed";

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Drives the installer window for the three modes: install, repair and uninstall.</summary>
public sealed partial class InstallerViewModel : OperationViewModelBase
{
    private readonly InstallerService _installer;

    public InstallerViewModel(InstallerService installer)
    {
        _installer = installer;
        _installer.Version = typeof(InstallerViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>Set by the window: shows sdkmanager's license text and returns true only if the user accepts.</summary>
    public Func<string, Task<bool>>? ConfirmLicensesAsync { get; set; }

    public InstallerMode Mode { get; private set; } = InstallerMode.Install;

    public ObservableCollection<ComponentRow> Rows { get; } = new();

    [ObservableProperty]
    private int _actionIndex;

    [ObservableProperty]
    private bool _removeUserData;

    [ObservableProperty]
    private bool _acceptSdkLicense;

    [ObservableProperty]
    private bool _showChoose = true;

    [ObservableProperty]
    private bool _isDone;

    public bool IsInstallMode => Mode == InstallerMode.Install;

    public bool IsMaintenanceMode => Mode != InstallerMode.Install;

    public bool IsOffline => _installer.OfflineBundleDir is not null;

    public bool IsRemoveAction => IsMaintenanceMode && ActionIndex == 1;

    public bool ShowLicenseCheck => IsOffline && !IsRemoveAction;

    public string Heading => Mode == InstallerMode.Install ? "Install WSA"
        : IsRemoveAction ? "Uninstall WSA" : "Repair WSA";

    public string Subheading => IsOffline
        ? "Offline setup: Java, Android SDK tools and the Android 14 image come from this package and are verified before use."
        : "Online setup: Java, Android SDK tools and the Android 14 image are downloaded from their vendors and verified.";

    public string StartText => Mode == InstallerMode.Install ? "Install" : IsRemoveAction ? "Uninstall" : "Repair";

    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(CanLaunch));

    partial void OnActionIndexChanged(int value)
    {
        foreach (var name in new[] { nameof(IsRemoveAction), nameof(ShowLicenseCheck), nameof(Heading), nameof(StartText) })
        {
            OnPropertyChanged(name);
        }
    }

    public async Task InitializeAsync(InstallerMode mode)
    {
        Mode = mode;
        ActionIndex = mode == InstallerMode.Uninstall ? 1 : 0;
        foreach (var name in new[] { nameof(IsInstallMode), nameof(IsMaintenanceMode), nameof(Heading), nameof(StartText), nameof(IsRemoveAction), nameof(ShowLicenseCheck) })
        {
            OnPropertyChanged(name);
        }

        await RefreshRowsAsync();
    }

    private async Task RefreshRowsAsync()
    {
        try
        {
            var states = await _installer.GetStatesAsync(CancellationToken.None);
            Rows.Clear();
            foreach (var state in states)
            {
                Rows.Add(new ComponentRow(state));
            }
        }
        catch (Exception ex) when (ex is ToolFailedException or ToolUnavailableException or IOException)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        var action = Mode == InstallerMode.Install ? InstallAction.Install
            : IsRemoveAction ? InstallAction.Remove : InstallAction.Repair;
        var selected = Rows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ErrorMessage = "Select at least one item.";
            return;
        }

        if (action != InstallAction.Remove && IsOffline && !AcceptSdkLicense
            && selected.Any(r => r.Component is InstallComponent.SdkTools or InstallComponent.SystemImage))
        {
            ErrorMessage = "Accept the Android SDK License Agreement to install the Android SDK tools and system image.";
            return;
        }

        var touchesApp = selected.Any(r => r.Component == InstallComponent.App);
        if (touchesApp && action != InstallAction.Install && InstallerBootstrap.IsAppRunning())
        {
            ErrorMessage = "WSA is still running. Close it, then press the button again.";
            return;
        }

        var order = action == InstallAction.Remove
            ? new[] { InstallComponent.SystemImage, InstallComponent.SdkTools, InstallComponent.Jdk, InstallComponent.App }
            : new[] { InstallComponent.App, InstallComponent.Jdk, InstallComponent.SdkTools, InstallComponent.SystemImage };
        var plan = order.Select(c => selected.FirstOrDefault(r => r.Component == c)).Where(r => r is not null).Select(r => r!).ToList();
        var source = InstallLayout.IsSetupPackage ? InstallLayout.BaseDir : null;

        ErrorMessage = null;
        SuccessMessage = null;
        ShowChoose = false;

        await RunAsync(Heading + "...", async (progress, ct) =>
        {
            foreach (var row in plan)
            {
                ct.ThrowIfCancellationRequested();
                // Installing something that is already there is a repair: it only fetches what is missing.
                var step = action == InstallAction.Install && row.Installed ? InstallAction.Repair : action;
                progress.Report(new SetupProgress($"{row.Title}...", null, true));
                await _installer.RunAsync(row.Component, step, source, ConfirmLicensesAsync, progress, ct);
            }

            if (action == InstallAction.Remove && RemoveUserData)
            {
                InstallerService.RemoveUserData();
            }

            return action switch
            {
                InstallAction.Install => "WSA is installed. You can start it now.",
                InstallAction.Repair => "Repair finished.",
                _ => "WSA has been removed.",
            };
        });

        if (HasError)
        {
            ShowChoose = true;
            await RefreshRowsAsync();
        }
        else
        {
            IsDone = true;
        }
    }

    public bool CanLaunch => Mode == InstallerMode.Install || (!IsRemoveAction && InstallLayout.IsInstalled);

    [RelayCommand]
    private void LaunchApp()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(InstallLayout.InstalledExe) { UseShellExecute = false };
            System.Diagnostics.Process.Start(psi)?.Dispose();
            App.InstallerWindow?.Close();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ErrorMessage = $"Could not start WSA: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CloseWindow() => App.InstallerWindow?.Close();
}
