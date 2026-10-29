using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WSA.App.Views;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.App;

public sealed partial class MainWindow : Window
{
    private object? _pendingParameter;

    public MainWindow()
    {
        InitializeComponent();
        Title = "WSA - Windows Subsystem for Android";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        Nav.SelectedItem = Nav.MenuItems[0];
        Nav.Loaded += Nav_Loaded;

        var settings = App.GetService<ISettingsService>();
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        settings.Changed += (_, _) => _dispatcher.TryEnqueue(ApplySettings);
        ApplySettings();
    }

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    /// <summary>Applies theme and shows only the pages the chosen goal and debug mode need.</summary>
    private void ApplySettings()
    {
        var s = App.GetService<ISettingsService>().Current;
        Root.RequestedTheme = s.Theme switch
        {
            "Light" => ElementTheme.Light,
            "System" => ElementTheme.Default,
            _ => ElementTheme.Dark,
        };

        var virtualDevices = s.SetupGoal != SetupGoals.Phone;
        DevicesItem.Visibility = virtualDevices ? Visibility.Visible : Visibility.Collapsed;
        StoreItem.Visibility = virtualDevices ? Visibility.Visible : Visibility.Collapsed;
        DebugItem.Visibility = s.DebugMode ? Visibility.Visible : Visibility.Collapsed;

        if (Nav.SelectedItem is NavigationViewItem { Visibility: Visibility.Collapsed })
        {
            NavigateTo("overview");
        }
    }

    /// <summary>Shows the macOS-style desktop of a connected phone inside the Phone section.</summary>
    public void OpenDesktop(string serial) => ContentFrame.Navigate(typeof(PhoneDesktopPage), serial);

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

        // First run: start in the setup wizard until it has been completed once.
        if (!App.GetService<ISettingsService>().Current.WizardCompleted)
        {
            NavigateTo("setup");
        }

        await Task.CompletedTask;
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
        "debug" => typeof(DebugPage),
        "settings" => typeof(SettingsPage),
        _ => typeof(OverviewPage),
    };
}
