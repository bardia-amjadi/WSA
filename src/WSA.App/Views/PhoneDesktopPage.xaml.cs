using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;
using WSA.App.Controls;
using WSA.App.ViewModels;
using WSA.App.Views.PhoneApps;

namespace WSA.App.Views;

/// <summary>A macOS-style desktop for a connected phone: menu bar, movable windows and a dock with the phone apps.</summary>
public sealed partial class PhoneDesktopPage : Page
{
    private sealed record AppDef(string Key, string Title, string Glyph, string Color, double Width, double Height,
        Func<string, FrameworkElement> Create);

    private static readonly AppDef[] Apps =
    {
        new("live", "Live View", "", "#FF3B82F6", 760, 620, s => new LiveViewApp(s)),
        new("files", "Files", "", "#FF38A3F5", 640, 460, s => new FilesApp(s)),
        new("apps", "Apps", "", "#FF8B5CF6", 560, 460, s => new AppsApp(s)),
        new("terminal", "Terminal", "", "#FF2D2D30", 620, 400, s => new TerminalApp(s)),
        new("tools", "Tools", "", "#FFF59E0B", 520, 520, s => new ToolsApp(s)),
        new("settings", "Settings", "", "#FF6B7280", 520, 560, s => new SettingsApp(s)),
    };

    private readonly Dictionary<string, DesktopWindow> _windows = new();
    private readonly Dictionary<string, Ellipse> _dots = new();
    private string _serial = string.Empty;
    private int _z = 1;
    private int _cascade;

    public PhoneDesktopPage()
    {
        ViewModel = App.GetService<PhoneDesktopViewModel>();
        InitializeComponent();
        BuildDock();
    }

    /// <summary>Serial the user closed the desktop for; the Phone page does not reopen it automatically.</summary>
    public static string? DismissedSerial { get; set; }

    public PhoneDesktopViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _serial = e.Parameter as string ?? string.Empty;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_serial.Length == 0)
        {
            return;
        }

        ViewModel.Start(_serial);
        if (_windows.Count == 0)
        {
            Open("live");
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Stop();
        foreach (var w in _windows.Values.ToList())
        {
            Surface.Children.Remove(w);
        }

        _windows.Clear();
        foreach (var dot in _dots.Values)
        {
            dot.Visibility = Visibility.Collapsed;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        DismissedSerial = _serial;
        App.MainWindow?.NavigateTo("phone");
    }

    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep windows reachable when the main window gets smaller.
        foreach (var w in _windows.Values)
        {
            if (Canvas.GetLeft(w) > e.NewSize.Width - 80)
            {
                Canvas.SetLeft(w, Math.Max(0, e.NewSize.Width - w.Width));
            }

            if (Canvas.GetTop(w) > e.NewSize.Height - 60)
            {
                Canvas.SetTop(w, Math.Max(0, e.NewSize.Height - w.Height));
            }
        }
    }

    // ---- dock ----------------------------------------------------------------------------------------

    private void BuildDock()
    {
        foreach (var app in Apps)
        {
            var icon = new Border
            {
                Width = 48,
                Height = 48,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(ParseColor(app.Color)),
                Child = new FontIcon { Glyph = app.Glyph, FontSize = 24, Foreground = new SolidColorBrush(Colors.White) },
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 1),
                RenderTransform = new ScaleTransform { ScaleX = 1, ScaleY = 1 },
            };
            var dot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = new SolidColorBrush(Colors.White),
                Opacity = 0.9,
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            _dots[app.Key] = dot;

            var stack = new StackPanel { Spacing = 3, Background = new SolidColorBrush(Colors.Transparent) };
            stack.Children.Add(icon);
            stack.Children.Add(dot);
            ToolTipService.SetToolTip(stack, app.Title);
            stack.PointerEntered += (_, _) => Scale(icon, 1.22);
            stack.PointerExited += (_, _) => Scale(icon, 1.0);
            var key = app.Key;
            stack.Tapped += (_, _) => Open(key);
            DockPanel.Children.Add(stack);
        }
    }

    private static void Scale(Border icon, double factor)
    {
        if (icon.RenderTransform is ScaleTransform t)
        {
            t.ScaleX = factor;
            t.ScaleY = factor;
        }
    }

    private static Color ParseColor(string hex)
    {
        var v = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    // ---- windows -------------------------------------------------------------------------------------

    private void Open(string key)
    {
        if (_windows.TryGetValue(key, out var existing))
        {
            existing.Visibility = Visibility.Visible;
            BringToFront(existing);
            return;
        }

        var app = Apps.First(a => a.Key == key);
        var maxW = Math.Max(320, Surface.ActualWidth - 40);
        var maxH = Math.Max(240, Surface.ActualHeight - 110);
        var window = new DesktopWindow
        {
            Key = key,
            Title = app.Title,
            Width = Math.Min(app.Width, maxW),
            Height = Math.Min(app.Height, maxH),
            AppContent = app.Create(_serial),
        };

        var offset = 28 * (_cascade++ % 6);
        Canvas.SetLeft(window, Math.Min(40 + offset, Math.Max(0, Surface.ActualWidth - window.Width)));
        Canvas.SetTop(window, Math.Min(24 + offset, Math.Max(0, Surface.ActualHeight - window.Height - 90)));

        window.CloseRequested += (_, _) => Close(key);
        window.MinimizeRequested += (_, _) => window.Visibility = Visibility.Collapsed;
        window.Focused += (_, _) => BringToFront(window);

        _windows[key] = window;
        Surface.Children.Add(window);
        BringToFront(window);
        _dots[key].Visibility = Visibility.Visible;
    }

    private void Close(string key)
    {
        if (_windows.Remove(key, out var window))
        {
            Surface.Children.Remove(window);
        }

        _dots[key].Visibility = Visibility.Collapsed;
    }

    private void BringToFront(DesktopWindow window)
    {
        Canvas.SetZIndex(window, ++_z);
        FocusedTitle.Text = window.Title;
    }
}
