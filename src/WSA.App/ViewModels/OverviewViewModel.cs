using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WSA.Core.Interfaces;
using WSA.Core.Models;
using WSA.Core.Services;

namespace WSA.App.ViewModels;

public partial class OverviewViewModel : ObservableObject
{
    private readonly ISdkLocator _sdk;
    private readonly IAvdService _avds;
    private readonly ISettingsService _settings;

    public OverviewViewModel(ISdkLocator sdk, IAvdService avds, ISettingsService settings)
    {
        _sdk = sdk;
        _avds = avds;
        _settings = settings;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private string _runtimeHeadline = "Checking...";

    [ObservableProperty]
    private string _sdkRoot = "-";

    [ObservableProperty]
    private string _sdkSource = string.Empty;

    [ObservableProperty]
    private string _emulatorSummary = "-";

    [ObservableProperty]
    private string _avdSummary = "-";

    [ObservableProperty]
    private string _runningSummary = "-";

    [ObservableProperty]
    private bool _needsSetup;

    public bool IsNotBusy => !IsBusy;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string? StartupWarning => App.StartupWarning;

    public bool HasStartupWarning => !string.IsNullOrEmpty(App.StartupWarning);

    public ObservableCollection<string> Diagnostics { get; } = new();

    public ObservableCollection<ToolRow> Tools { get; } = new();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var status = await _sdk.DetectAsync();
            Apply(status);
            await LoadAvdsAsync(status);
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

    private void Apply(SdkStatus status)
    {
        SdkRoot = status.SdkRoot ?? "No Android SDK found";
        SdkSource = status.SdkRootSource;
        NeedsSetup = !SetupRequirements.IsSatisfied(_settings.Current, status);
        var phoneOnly = _settings.Current.SetupGoal == SetupGoals.Phone;

        var ready = status.Tools.Count(t => t.IsAvailable);
        RuntimeHeadline = phoneOnly
            ? (status.Get(SdkTool.Adb).IsAvailable ? "Ready to connect a phone (adb installed)" : "adb is not installed yet")
            : status.IsComplete
            ? "Android SDK ready"
            : status.SdkRoot is null
                ? "Android SDK not found"
                : $"Android SDK incomplete ({ready} of {status.Tools.Count} tools ready)";

        var emulator = status.Get(SdkTool.Emulator);
        EmulatorSummary = emulator.IsAvailable
            ? emulator.Version ?? "Emulator available"
            : emulator.Path is null ? "Emulator not installed" : $"Emulator problem: {emulator.Problem}";

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

    private async Task LoadAvdsAsync(SdkStatus status)
    {
        var canList = status.Get(SdkTool.AvdManager).IsAvailable || status.Get(SdkTool.Emulator).IsAvailable;
        if (!canList)
        {
            AvdSummary = "Cannot list virtual devices";
            RunningSummary = "Needs a usable avdmanager or emulator";
            return;
        }

        var list = await _avds.ListAsync();
        var running = list.Count(a => a.IsRunning);
        AvdSummary = list.Count == 1 ? "1 virtual device" : $"{list.Count} virtual devices";
        RunningSummary = status.Get(SdkTool.Adb).IsAvailable
            ? $"{running} running"
            : "Running state unknown (adb unavailable)";
    }
}
