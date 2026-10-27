using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.App.ViewModels;

public sealed record SetupStepRow(string Title, string Detail, bool Done)
{
    public string Status => Done ? "Ready" : "Needed";
}

public partial class SetupViewModel : OperationViewModelBase
{
    private readonly ISdkSetupService _setup;
    private readonly ISdkLocator _sdk;

    public SetupViewModel(ISdkSetupService setup, ISdkLocator sdk)
    {
        _setup = setup;
        _sdk = sdk;
    }

    /// <summary>Set by the page: shows the license text and returns true only if the user clicked Accept.</summary>
    public Func<string, Task<bool>>? ConfirmLicensesAsync { get; set; }

    [ObservableProperty]
    private string _sdkRoot = "-";

    [ObservableProperty]
    private string _accelerationNote = "Not checked yet. It is checked once the emulator is installed.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsInstall))]
    private bool _isComplete;

    public bool NeedsInstall => !IsComplete;

    public ObservableCollection<SetupStepRow> Steps { get; } = new();

    [RelayCommand]
    private Task RefreshAsync() =>
        RunAsync("Checking installed components...", async (_, ct) =>
        {
            await _sdk.DetectAsync(ct);
            return null;
        });

    [RelayCommand]
    private Task InstallAllAsync() =>
        RunAsync("Starting setup...", async (progress, ct) =>
        {
            await _setup.EnsureSdkRootAsync(ct);
            var state = await _setup.GetStateAsync(ct);

            if (!state.JdkReady)
            {
                await _setup.InstallJdkAsync(progress, ct);
                state = await _setup.GetStateAsync(ct);
            }

            if (!state.CmdlineToolsReady)
            {
                await _setup.InstallCommandLineToolsAsync(progress, ct);
                state = await _setup.GetStateAsync(ct);
            }

            var license = await _setup.PreviewLicensesAsync(ct);
            if (!license.AllAccepted)
            {
                var accepted = ConfirmLicensesAsync is { } ask && await ask(license.Text);
                if (!accepted)
                {
                    return "Setup paused: the Android SDK licenses must be accepted to download the emulator and tools. " +
                           "Press the install button again when you are ready.";
                }

                await _setup.AcceptLicensesAsync(progress, ct);
            }

            var packages = new List<string>();
            if (!state.PlatformToolsReady) packages.Add("platform-tools");
            if (!state.EmulatorReady) packages.Add("emulator");
            if (packages.Count > 0)
            {
                await _setup.InstallPackagesAsync(packages, progress, ct);
            }

            return "Setup finished. You can now install an Android version from the Android Store.";
        });

    [RelayCommand]
    private void OpenWindowsFeatures()
    {
        try
        {
            // Fixed system program; the user confirms any elevation prompt themselves.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("optionalfeatures.exe") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ErrorMessage = $"Could not open Windows Features: {ex.Message}. Open it from Control Panel -> Programs -> Turn Windows features on or off.";
        }
    }

    protected override async Task OnOperationFinishedAsync()
    {
        var state = await _setup.GetStateAsync();
        SdkRoot = state.SdkRoot;
        IsComplete = state.IsComplete;
        AccelerationNote = state.AccelerationNote
                           ?? (state.EmulatorReady ? "No result available." : "Not checked yet. It is checked once the emulator is installed.");

        Steps.Clear();
        Steps.Add(new SetupStepRow("Java runtime (JDK)", state.JdkDetail, state.JdkReady));
        Steps.Add(new SetupStepRow("Android command-line tools", "sdkmanager and avdmanager", state.CmdlineToolsReady));
        Steps.Add(new SetupStepRow("Platform tools", "adb", state.PlatformToolsReady));
        Steps.Add(new SetupStepRow("Android Emulator", "The official emulator from Google", state.EmulatorReady));
    }

    /// <summary>Called when the page is shown.</summary>
    public Task LoadAsync() => RefreshCommand.ExecuteAsync(null);
}
