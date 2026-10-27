using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using WSA.App.ViewModels;

namespace WSA.App.Views;

public sealed partial class PhonePage : Page
{
    private Point? _pointerDown;
    private long _pointerDownTicks;

    public PhonePage()
    {
        ViewModel = App.GetService<PhoneViewModel>();
        InitializeComponent();
    }

    public PhoneViewModel ViewModel { get; }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ConfirmAsync = (title, message, button) => Dialogs.ConfirmAsync(XamlRoot, title, message, button);
        ViewModel.StartWatching();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) => ViewModel.StopWatching();

    private async void InstallAdb_Click(object sender, RoutedEventArgs e) => await ViewModel.InstallAdbAsync();

    private void TextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            ViewModel.SendTextCommand.Execute(null);
        }
    }

    private async void InstallApk_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".apk");
        if (!TryInitialize(picker))
        {
            return;
        }

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await ViewModel.InstallApkAsync(file.Path);
        }
    }

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = $"phone-{DateTime.Now:yyyyMMdd-HHmmss}",
        };
        picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
        if (!TryInitialize(picker))
        {
            return;
        }

        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            await ViewModel.SaveScreenshotAsync(file.Path);
        }
    }

    private async void Wifi_Click(object sender, RoutedEventArgs e)
    {
        var connectBox = new TextBox
        {
            Header = "Connect address (IP:port)",
            PlaceholderText = "192.168.1.20:41234",
        };
        var pairBox = new TextBox
        {
            Header = "Pairing address (IP:port), first time only",
            PlaceholderText = "192.168.1.20:37215",
        };
        var codeBox = new TextBox { Header = "Pairing code (6 digits), first time only", MaxLength = 6 };

        var panel = new StackPanel { Spacing = 10, MinWidth = 400 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "On the phone (Android 11 or newer): Settings > Developer options > Wireless debugging > turn on. " +
                   "The phone and this PC must be on the same network. " +
                   "The connect address is shown on the Wireless debugging screen. " +
                   "For the first connection tap 'Pair device with pairing code' and enter the address and code it shows here as well.",
        });
        panel.Children.Add(connectBox);
        panel.Children.Add(pairBox);
        panel.Children.Add(codeBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Connect over Wi-Fi",
            Content = panel,
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.PairAndConnectAsync(connectBox.Text, pairBox.Text, codeBox.Text);
        }
    }

    private static bool TryInitialize(object picker)
    {
        // WinUI 3 desktop pickers must be tied to the window.
        if (App.MainWindow is not { } window)
        {
            return false;
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        return true;
    }

    private void Frame_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.IsDeviceReady || ViewModel.Frame is null)
        {
            return;
        }

        _pointerDown = e.GetCurrentPoint(PhoneImage).Position;
        _pointerDownTicks = Environment.TickCount64;
        PhoneImage.CapturePointer(e.Pointer);
    }

    private async void Frame_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        PhoneImage.ReleasePointerCapture(e.Pointer);
        if (_pointerDown is not { } start)
        {
            return;
        }

        _pointerDown = null;
        var end = e.GetCurrentPoint(PhoneImage).Position;
        if (!TryMap(start, out var sx, out var sy))
        {
            return;
        }

        TryMap(end, out var ex, out var ey, clamp: true);
        var moved = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2)) > 10;
        if (!moved)
        {
            await ViewModel.TapAsync(sx, sy);
        }
        else
        {
            var duration = (int)Math.Clamp(Environment.TickCount64 - _pointerDownTicks, 100, 1500);
            await ViewModel.SwipeAsync(sx, sy, ex, ey, duration);
        }
    }

    /// <summary>
    /// Converts a point on the displayed image to phone pixel coordinates. The image is centered and uniformly scaled,
    /// so its rendered size equals the picture and the scale is rendered size / pixel size.
    /// </summary>
    private bool TryMap(Point p, out int x, out int y, bool clamp = false)
    {
        x = y = 0;
        var pw = ViewModel.FramePixelWidth;
        var ph = ViewModel.FramePixelHeight;
        if (pw <= 0 || ph <= 0 || PhoneImage.ActualWidth <= 0 || PhoneImage.ActualHeight <= 0)
        {
            return false;
        }

        var fx = p.X / PhoneImage.ActualWidth;
        var fy = p.Y / PhoneImage.ActualHeight;
        if (!clamp && (fx < 0 || fx >= 1 || fy < 0 || fy >= 1))
        {
            return false;
        }

        x = (int)Math.Clamp(fx * pw, 0, pw - 1);
        y = (int)Math.Clamp(fy * ph, 0, ph - 1);
        return true;
    }
}
