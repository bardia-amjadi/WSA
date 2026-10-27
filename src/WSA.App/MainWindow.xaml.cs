using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WSA.App.Views;
using WSA.Core.Interfaces;

namespace WSA.App;

public sealed partial class MainWindow : Window
{
    private object? _pendingParameter;

    public MainWindow()
    {
        InitializeComponent();
        Title = "WSA - Windows Subsystem for Android";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        Nav.SelectedItem = Nav.MenuItems[0];
        Nav.Loaded += Nav_Loaded;
    }

    /// <summary>Selects a navigation item by tag and shows its page, optionally passing a parameter to the page.</summary>
    public void NavigateTo(string tag, object? parameter = null)
    {
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string?)i.Tag == tag);
        if (item is null)
        {
            return;
        }

        var page = PageFor(tag);
        if (ReferenceEquals(Nav.SelectedItem, item))
        {
            // Already selected, so SelectionChanged will not fire; navigate directly (this also re-creates the page).
            ContentFrame.Navigate(page, parameter);
            return;
        }

        _pendingParameter = parameter;
        Nav.SelectedItem = item;
    }

    private async void Nav_Loaded(object sender, RoutedEventArgs e)
    {
        Nav.Loaded -= Nav_Loaded;

        // First run: if the SDK is not fully installed, start on Setup instead of an Overview full of "missing".
        try
        {
            var status = await App.GetService<ISdkLocator>().DetectAsync();
            if (!status.IsComplete)
            {
                NavigateTo("setup");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
        {
            // Detection problems are shown on the Overview and Runtime pages; stay where we are.
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        var page = PageFor(tag);
        var parameter = _pendingParameter;
        _pendingParameter = null;
        if (page is not null && (ContentFrame.CurrentSourcePageType != page || parameter is not null))
        {
            ContentFrame.Navigate(page, parameter);
        }
    }

    private static Type PageFor(string tag) => tag switch
    {
        "devices" => typeof(DevicesPage),
        "phone" => typeof(PhonePage),
        "store" => typeof(StorePage),
        "terminal" => typeof(TerminalPage),
        "setup" => typeof(SetupPage),
        "runtime" => typeof(RuntimePage),
        "settings" => typeof(SettingsPage),
        _ => typeof(OverviewPage),
    };
}
