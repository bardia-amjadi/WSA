using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WSA.App.Views;

namespace WSA.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "WSA - Windows Subsystem for Android";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        var page = tag switch
        {
            "overview" => typeof(OverviewPage),
            "devices" => typeof(DevicesPage),
            "terminal" => typeof(TerminalPage),
            "runtime" => typeof(RuntimePage),
            "settings" => typeof(SettingsPage),
            _ => null,
        };

        if (page is not null && ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page);
        }
    }
}
