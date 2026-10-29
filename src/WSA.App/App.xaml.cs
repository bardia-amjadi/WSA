using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using WSA.App.ViewModels;
using WSA.Core.Interfaces;
using WSA.Core.Services;
using WSA.Infrastructure.AndroidSdk;
using WSA.Infrastructure.Adb;
using WSA.Infrastructure.Configuration;
using WSA.Infrastructure.Emulator;
using WSA.Infrastructure.Installer;
using WSA.Core.Models;
using WSA.Infrastructure.Logging;
using WSA.Infrastructure.ProcessExecution;
using WSA.Infrastructure.Setup;

namespace WSA.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public static MainWindow? MainWindow { get; private set; }

    public static InstallerWindow? InstallerWindow { get; private set; }

    /// <summary>Set when settings.json existed but could not be read; surfaced on the Overview page.</summary>
    public static string? StartupWarning { get; private set; }

    public static T GetService<T>() where T : notnull => Services.GetRequiredService<T>();

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Setup package, "Modify/Uninstall" from Windows, or Settings -> Repair: show the installer instead of the app.
        var launchArgs = InstallerBootstrap.Args;
        var installerMode = InstallerBootstrap.DetermineMode(launchArgs);
        if (installerMode is InstallerMode.Maintenance or InstallerMode.Uninstall && InstallerBootstrap.NeedsRelaunch(launchArgs))
        {
            // Maintenance must not delete the files it runs from, so continue from a temporary copy.
            InstallerBootstrap.RelaunchFromTemp(launchArgs);
            Exit();
            return;
        }

        Services = BuildServices();

        // Settings are a small local file; load them before the window so every page sees real configuration.
        StartupWarning = await GetService<ISettingsService>().LoadAsync();

        if (installerMode is { } mode)
        {
            InstallerWindow = new InstallerWindow(mode);
            InstallerWindow.Activate();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ISettingsService, SettingsService>(_ => new SettingsService());
        services.AddSingleton<IAppLog>(sp => new AppLog(sp.GetRequiredService<ISettingsService>()));
        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<IProcessRunner>(sp =>
            new LoggingProcessRunner(sp.GetRequiredService<ProcessRunner>(), sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<DebugViewModel>();
        services.AddSingleton<ISdkLocator, SdkLocator>();
        services.AddSingleton<IJavaDiagnostics, JavaDiagnosticsService>();
        services.AddSingleton<IToolCommandFactory, ToolCommandFactory>();
        services.AddSingleton<IAdbService, AdbService>();
        services.AddSingleton<IAvdService, AvdService>();
        services.AddSingleton<IAvdLifecycleService, AvdLifecycleService>();
        services.AddSingleton<IPhoneService, PhoneService>();
        services.AddSingleton<IPhoneToolsService, PhoneToolsService>();
        services.AddSingleton<IScreenStreamService, ScreenStreamService>();
        services.AddTransient<FilesViewModel>();
        services.AddTransient<TerminalAppViewModel>();
        services.AddTransient<ToolsViewModel>();
        services.AddTransient<AppsViewModel>();
        services.AddTransient<PhoneSettingsViewModel>();
        services.AddTransient<LiveViewViewModel>();
        services.AddTransient<PhoneDesktopViewModel>();
        services.AddSingleton<PhoneViewModel>();
        services.AddSingleton<TerminalService>();
        services.AddSingleton<FileDownloader>();
        services.AddSingleton<ISdkSetupService, SdkSetupService>();

        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<DevicesViewModel>();
        services.AddSingleton<TerminalViewModel>();
        services.AddSingleton<RuntimeViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<StoreViewModel>();
        services.AddSingleton<AppInstaller>();
        services.AddSingleton<InstallerService>();
        services.AddSingleton<InstallerViewModel>();

        return services.BuildServiceProvider();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Record the crash for bug reports; do not mark it handled, so failures are never hidden.
        try
        {
            Services?.GetService<IAppLog>()?.Error("crash", "Unhandled exception", e.Exception);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WSA");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:O}] {e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Nothing further can be done if the log location is unwritable.
        }
    }
}
