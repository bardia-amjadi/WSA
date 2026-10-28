using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.App.ViewModels;

/// <summary>One entry in the wizard's step rail.</summary>
public sealed record WizardStepRow(int Number, string Title, string Marker, string Status);

/// <summary>
/// The setup wizard. The first page asks what WSA is for; the steps that follow are only the ones that goal needs
/// (a phone needs just adb; virtual devices need Java, the command-line tools, the emulator and acceleration).
/// Every step can be done automatically (WSA downloads from the vendor) or manually (the user points WSA at what they
/// already have, or runs the commands themselves).
/// </summary>
public partial class SetupViewModel : OperationViewModelBase
{
    private const string KGoal = "goal", KJava = "java", KCmdline = "cmdline", KPackages = "packages",
        KAdb = "adb", KAccel = "accel", KFinish = "finish";

    private static readonly string[] PhoneSteps = { KGoal, KAdb, KFinish };
    private static readonly string[] FullSteps = { KGoal, KJava, KCmdline, KPackages, KAccel, KFinish };

    private readonly ISdkSetupService _setup;
    private readonly ISdkLocator _sdk;
    private readonly ISettingsService _settings;
    private readonly IAppLog _log;
    private string[] _keys = FullSteps;

    public SetupViewModel(ISdkSetupService setup, ISdkLocator sdk, ISettingsService settings, IAppLog log)
    {
        _setup = setup;
        _sdk = sdk;
        _settings = settings;
        _log = log;
        GoalIndex = _settings.Current.SetupGoal == SetupGoals.Phone ? 0 : 1;
        RebuildKeys();
    }

    /// <summary>Set by the page: shows the license text and returns true only if the user clicked Accept.</summary>
    public Func<string, Task<bool>>? ConfirmLicensesAsync { get; set; }

    /// <summary>Set by the page: generic confirmation dialog (title, message, button text).</summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Set by the page: folder picker. Returns the chosen folder or null.</summary>
    public Func<Task<string?>>? PickFolderAsync { get; set; }

    public ObservableCollection<WizardStepRow> Rail { get; } = new();

    [ObservableProperty]
    private int _stepIndex;

    [ObservableProperty]
    private int _goalIndex = 1;

    [ObservableProperty]
    private int _modeIndex;

    [ObservableProperty]
    private string _sdkRoot = "-";

    [ObservableProperty]
    private string _javaDetail = "Checking...";

    [ObservableProperty]
    private string _accelerationNote = "Not checked yet. It is checked once the emulator is installed.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JavaNeeded))]
    private bool _javaReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CmdlineNeeded))]
    private bool _cmdlineReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PackagesNeeded))]
    [NotifyPropertyChangedFor(nameof(AdbNeeded))]
    private bool _platformReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PackagesNeeded))]
    private bool _emulatorReady;

    [ObservableProperty]
    private string _manualJavaPath = string.Empty;

    [ObservableProperty]
    private string _manualSdkPath = string.Empty;

    public bool JavaNeeded => !JavaReady;

    public bool CmdlineNeeded => !CmdlineReady;

    public bool PackagesNeeded => !(PlatformReady && EmulatorReady);

    public bool AdbNeeded => !PlatformReady;

    public string CurrentKey => _keys[Math.Clamp(StepIndex, 0, _keys.Length - 1)];

    public bool IsGoalStep => CurrentKey == KGoal;
    public bool IsJavaStep => CurrentKey == KJava;
    public bool IsCmdlineStep => CurrentKey == KCmdline;
    public bool IsPackagesStep => CurrentKey == KPackages;
    public bool IsAdbStep => CurrentKey == KAdb;
    public bool IsAccelStep => CurrentKey == KAccel;
    public bool IsFinishStep => CurrentKey == KFinish;
    public bool NotFirst => StepIndex > 0;
    public bool NotLast => StepIndex < _keys.Length - 1;
    public bool IsLast => !NotLast;
    public bool IsAutoMode => ModeIndex == 0;
    public bool IsManualMode => ModeIndex == 1;

    public string StepTitle => CurrentKey switch
    {
        KGoal => "What do you want to use WSA for?",
        KJava => "Java runtime",
        KCmdline => "Android command-line tools",
        KPackages => "Platform tools and emulator",
        KAdb => "adb (Android Debug Bridge)",
        KAccel => "Hardware acceleration",
        _ => "All set",
    };

    public string ManualHelp => CurrentKey switch
    {
        KJava => "Choose the folder of a JDK 17 or newer that contains bin\\java.exe (for example C:\\Java\\jdk-27). WSA runs it to confirm the version before using it.",
        KCmdline => "Download \"Command line tools only\" from developer.android.com/studio, extract it, and place the contents in <SDK folder>\\cmdline-tools\\latest. Then point WSA at your SDK folder below.",
        KPackages => "In WSA Terminal (or any terminal) run:  sdkmanager \"platform-tools\" \"emulator\"   -   or point WSA at an SDK folder that already has them.",
        KAdb => "Point WSA at an SDK folder that already has platform-tools\\adb.exe (Android Studio's default is %LOCALAPPDATA%\\Android\\Sdk).",
        _ => string.Empty,
    };

    public bool HasModes => CurrentKey is KJava or KCmdline or KPackages or KAdb;

    public string StepDescription => CurrentKey switch
    {
        KJava => "The Android command-line tools (sdkmanager, avdmanager) and the emulator need Java 17 or newer. Automatic mode installs Microsoft Build of OpenJDK 21 into WSA's own folder; your existing Java is not touched.",
        KCmdline => "sdkmanager and avdmanager. Automatic mode downloads Google's command-line tools into your SDK folder and verifies the checksum from Google's repository index.",
        KPackages => "adb and the official Android Emulator, installed by Google's sdkmanager after you accept the Android SDK license.",
        KAdb => "adb talks to your phone. Only this small download is needed to connect a real device; no Java, emulator or Android image.",
        KAccel => "The emulator needs CPU virtualization turned on in your BIOS/UEFI and the Windows 'Windows Hypervisor Platform' feature. WSA cannot enable these itself: Windows requires administrator approval and a restart.",
        _ => string.Empty,
    };

    public string StepStatus => CurrentKey switch
    {
        KJava => (JavaReady ? "Ready: " : "Not ready: ") + JavaDetail,
        KCmdline => CmdlineReady ? "Installed" : "Not installed",
        KPackages => $"platform-tools (adb): {(PlatformReady ? "installed" : "missing")}   emulator: {(EmulatorReady ? "installed" : "missing")}",
        KAdb => PlatformReady ? "adb is installed" : "adb is not installed",
        KAccel => AccelerationNote,
        _ => string.Empty,
    };

    public bool StepNeeded => CurrentKey switch
    {
        KJava => JavaNeeded,
        KCmdline => CmdlineNeeded,
        KPackages => PackagesNeeded,
        KAdb => AdbNeeded,
        _ => false,
    };

    public bool IsStepDone => !StepNeeded;

    public string ManualCommand => CurrentKey == KPackages ? "sdkmanager \"platform-tools\" \"emulator\"" : string.Empty;

    public bool HasManualCommand => ManualCommand.Length > 0;

    public bool ManualUsesJava => CurrentKey == KJava;

    public bool ManualUsesSdk => CurrentKey is KCmdline or KPackages or KAdb;

    public string GoalSummary => GoalIndex == 0
        ? "Phone only: WSA will download just adb (about 10 MB). No Java, emulator or Android image."
        : "Everything: Java, Android command-line tools, platform tools and the emulator are installed so you can create and run virtual devices.";

    public string FinishSummary => _settings.Current.SetupGoal == SetupGoals.Phone
        ? (PlatformReady ? "adb is ready. Open the Phone page to connect your device." : "adb is not installed yet. Go back to the adb step, or finish and install it later from the Phone page.")
        : (JavaReady && CmdlineReady && PlatformReady && EmulatorReady
            ? "Everything WSA needs is installed. Open the Android Store to download an Android version."
            : "Some components are still missing. You can finish now and come back to Setup from Settings at any time.");

    partial void OnGoalIndexChanged(int value)
    {
        RebuildKeys();
        OnPropertyChanged(nameof(GoalSummary));
    }

    partial void OnModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsAutoMode));
        OnPropertyChanged(nameof(IsManualMode));
    }

    partial void OnStepIndexChanged(int value) => RaiseStepProperties();

    private void RebuildKeys()
    {
        _keys = GoalIndex == 0 ? PhoneSteps : FullSteps;
        if (StepIndex >= _keys.Length)
        {
            StepIndex = _keys.Length - 1;
        }

        RaiseStepProperties();
    }

    private void RaiseStepProperties()
    {
        foreach (var name in new[]
                 {
                     nameof(CurrentKey), nameof(IsGoalStep), nameof(IsJavaStep), nameof(IsCmdlineStep),
                     nameof(IsPackagesStep), nameof(IsAdbStep), nameof(IsAccelStep), nameof(IsFinishStep),
                     nameof(NotFirst), nameof(NotLast), nameof(IsLast), nameof(StepTitle), nameof(ManualHelp),
                     nameof(ManualCommand), nameof(HasManualCommand), nameof(ManualUsesJava), nameof(ManualUsesSdk),
                     nameof(FinishSummary), nameof(HasModes), nameof(StepDescription), nameof(StepStatus),
                     nameof(StepNeeded), nameof(IsStepDone),
                 })
        {
            OnPropertyChanged(name);
        }

        ModeIndex = 0;
        RebuildRail();
    }

    private void RebuildRail()
    {
        Rail.Clear();
        for (var i = 0; i < _keys.Length; i++)
        {
            var key = _keys[i];
            var done = key switch
            {
                KGoal => i < StepIndex || _settings.Current.SetupGoal is not null && i != StepIndex,
                KJava => JavaReady,
                KCmdline => CmdlineReady,
                KPackages => PlatformReady && EmulatorReady,
                KAdb => PlatformReady,
                _ => false,
            };
            var title = key switch
            {
                KGoal => "Goal",
                KJava => "Java",
                KCmdline => "Command-line tools",
                KPackages => "Platform tools + emulator",
                KAdb => "adb",
                KAccel => "Acceleration",
                _ => "Finish",
            };
            Rail.Add(new WizardStepRow(i + 1, title, i == StepIndex ? ">" : " ", done ? "ready" : string.Empty));
        }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (IsGoalStep)
        {
            _settings.Current.SetupGoal = GoalIndex == 0 ? SetupGoals.Phone : SetupGoals.Everything;
            await _settings.SaveAsync();
            _log.Info("wizard", $"goal = {_settings.Current.SetupGoal}");
        }

        if (StepIndex < _keys.Length - 1)
        {
            StepIndex++;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (StepIndex > 0)
        {
            StepIndex--;
        }
    }

    [RelayCommand]
    private async Task FinishAsync()
    {
        _settings.Current.WizardCompleted = true;
        await _settings.SaveAsync();
        _log.Info("wizard", "completed");
        StepIndex = 0;
        App.MainWindow?.NavigateTo(_settings.Current.SetupGoal == SetupGoals.Phone ? "phone" : "overview");
    }

    [RelayCommand]
    private Task RefreshAsync() =>
        RunAsync("Checking installed components...", async (_, ct) =>
        {
            await _sdk.DetectAsync(ct);
            return null;
        });

    /// <summary>Automatic mode: downloads and installs only what the current step needs.</summary>
    [RelayCommand]
    private async Task RunStepAsync()
    {
        switch (CurrentKey)
        {
            case KJava:
                await RunAsync("Installing Java...", async (progress, ct) =>
                {
                    await _setup.InstallJdkAsync(progress, ct);
                    return "Java is installed.";
                });
                break;

            case KCmdline:
                await RunAsync("Installing Android command-line tools...", async (progress, ct) =>
                {
                    await _setup.EnsureSdkRootAsync(ct);
                    await _setup.InstallCommandLineToolsAsync(progress, ct);
                    return "Command-line tools are installed.";
                });
                break;

            case KPackages:
                await RunAsync("Installing platform tools and emulator...", async (progress, ct) =>
                {
                    var license = await _setup.PreviewLicensesAsync(ct);
                    if (!license.AllAccepted)
                    {
                        var accepted = ConfirmLicensesAsync is { } ask && await ask(license.Text);
                        if (!accepted)
                        {
                            return "Paused: the Android SDK licenses must be accepted to download these components.";
                        }

                        await _setup.AcceptLicensesAsync(progress, ct);
                    }

                    var state = await _setup.GetStateAsync(ct);
                    var packages = new List<string>();
                    if (!state.PlatformToolsReady) packages.Add("platform-tools");
                    if (!state.EmulatorReady) packages.Add("emulator");
                    if (packages.Count > 0)
                    {
                        await _setup.InstallPackagesAsync(packages, progress, ct);
                    }

                    return "Platform tools and emulator are installed.";
                });
                break;

            case KAdb:
                var confirmed = ConfirmAsync is { } confirm && await confirm(
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
                    await _setup.EnsureSdkRootAsync(ct);
                    await _setup.InstallPlatformToolsDirectAsync(progress, ct);
                    return "adb is installed.";
                });
                break;
        }
    }

    /// <summary>Manual mode: use a JDK or SDK folder the user already has.</summary>
    [RelayCommand]
    private Task ApplyManualAsync() =>
        RunAsync("Checking the folder...", async (_, ct) =>
        {
            var result = ManualUsesJava
                ? await _setup.ApplyManualJavaHomeAsync(ManualJavaPath, ct)
                : await _setup.ApplyManualSdkRootAsync(ManualSdkPath, ct);
            if (!result.Success)
            {
                throw new ToolFailedException(result.Message);
            }

            return result.Message;
        });

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (PickFolderAsync is null)
        {
            return;
        }

        var folder = await PickFolderAsync();
        if (folder is null)
        {
            return;
        }

        if (ManualUsesJava)
        {
            ManualJavaPath = folder;
        }
        else
        {
            ManualSdkPath = folder;
        }
    }

    [RelayCommand]
    private void CopyManualCommand()
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(ManualCommand);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        SuccessMessage = "Command copied.";
    }

    [RelayCommand]
    private void OpenTerminal() => App.MainWindow?.NavigateTo("terminal");

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
        await RefreshStateAsync();
    }

    private async Task RefreshStateAsync()
    {
        var state = await _setup.GetStateAsync();
        SdkRoot = state.SdkRoot;
        JavaReady = state.JdkReady;
        JavaDetail = state.JdkDetail;
        CmdlineReady = state.CmdlineToolsReady;
        PlatformReady = state.PlatformToolsReady;
        EmulatorReady = state.EmulatorReady;
        AccelerationNote = state.AccelerationNote
                           ?? (state.EmulatorReady ? "No result available." : "Not checked yet. It is checked once the emulator is installed.");
        if (ManualSdkPath.Length == 0)
        {
            ManualSdkPath = state.SdkRoot;
        }

        foreach (var name in new[] { nameof(FinishSummary), nameof(StepStatus), nameof(StepNeeded), nameof(IsStepDone) })
        {
            OnPropertyChanged(name);
        }

        RebuildRail();
    }

    /// <summary>Called when the page is shown: re-detects, then starts at the first unfinished step if the wizard was never completed.</summary>
    public async Task LoadAsync()
    {
        await RefreshCommand.ExecuteAsync(null);
        GoalIndex = _settings.Current.SetupGoal == SetupGoals.Phone ? 0 : 1;
    }
}
