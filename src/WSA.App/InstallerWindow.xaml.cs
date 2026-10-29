using Microsoft.UI.Xaml;
using WSA.App.ViewModels;
using WSA.App.Views;
using WSA.Core.Models;
using WSA.Infrastructure.Installer;

namespace WSA.App;

/// <summary>The setup window: install from a setup package, repair, or uninstall WSA and its packages.</summary>
public sealed partial class InstallerWindow : Window
{
    private readonly InstallerMode _mode;

    public InstallerWindow(InstallerMode mode)
    {
        _mode = mode;
        ViewModel = App.GetService<InstallerViewModel>();
        InitializeComponent();
        Title = "WSA Setup";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 760));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        Closed += (_, _) => InstallerBootstrap.ScheduleTempCleanup();
    }

    public InstallerViewModel ViewModel { get; }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ConfirmLicensesAsync = text => Dialogs.ConfirmLicensesAsync(Root.XamlRoot, text);
        await ViewModel.InitializeAsync(_mode);
    }
}
