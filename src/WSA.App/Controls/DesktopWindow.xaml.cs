using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace WSA.App.Controls;

/// <summary>A draggable, resizable window with macOS-style traffic lights, hosted on a <see cref="Canvas"/>.</summary>
public sealed partial class DesktopWindow : UserControl
{
    private bool _dragging;
    private bool _resizing;
    private Point _grab;
    private Rect _restore;
    private bool _zoomed;

    public DesktopWindow()
    {
        InitializeComponent();
        PointerPressed += (_, _) => Focused?.Invoke(this, EventArgs.Empty);
    }

    public string Key { get; set; } = string.Empty;

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public object? AppContent
    {
        get => Host.Content;
        set => Host.Content = value;
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? MinimizeRequested;

    public event EventHandler? Focused;

    private Canvas? Surface => Parent as Canvas;

    private void Close_Tapped(object sender, TappedRoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Minimize_Tapped(object sender, TappedRoutedEventArgs e) => MinimizeRequested?.Invoke(this, EventArgs.Empty);

    private void Zoom_Tapped(object sender, TappedRoutedEventArgs e) => ToggleZoom();

    private void TitleBar_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ToggleZoom();

    private void ToggleZoom()
    {
        if (Surface is not { } surface)
        {
            return;
        }

        if (_zoomed)
        {
            Canvas.SetLeft(this, _restore.X);
            Canvas.SetTop(this, _restore.Y);
            Width = _restore.Width;
            Height = _restore.Height;
        }
        else
        {
            _restore = new Rect(Canvas.GetLeft(this), Canvas.GetTop(this), Width, Height);
            Canvas.SetLeft(this, 0);
            Canvas.SetTop(this, 0);
            Width = surface.ActualWidth;
            Height = surface.ActualHeight;
        }

        _zoomed = !_zoomed;
    }

    private void TitleBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_zoomed || Surface is not { } surface)
        {
            return;
        }

        var p = e.GetCurrentPoint(surface).Position;
        _grab = new Point(p.X - Canvas.GetLeft(this), p.Y - Canvas.GetTop(this));
        _dragging = true;
        TitleBar.CapturePointer(e.Pointer);
    }

    private void TitleBar_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || Surface is not { } surface)
        {
            return;
        }

        var p = e.GetCurrentPoint(surface).Position;
        Canvas.SetLeft(this, Math.Clamp(p.X - _grab.X, -Width + 80, Math.Max(0, surface.ActualWidth - 80)));
        Canvas.SetTop(this, Math.Clamp(p.Y - _grab.Y, 0, Math.Max(0, surface.ActualHeight - 40)));
    }

    private void TitleBar_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        TitleBar.ReleasePointerCaptures();
    }

    private void Grip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_zoomed || Surface is not { } surface)
        {
            return;
        }

        _grab = e.GetCurrentPoint(surface).Position;
        _restore = new Rect(0, 0, Width, Height);
        _resizing = true;
        Grip.CapturePointer(e.Pointer);
    }

    private void Grip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing || Surface is not { } surface)
        {
            return;
        }

        var p = e.GetCurrentPoint(surface).Position;
        Width = Math.Max(MinWidth, _restore.Width + (p.X - _grab.X));
        Height = Math.Max(MinHeight, _restore.Height + (p.Y - _grab.Y));
    }

    private void Grip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _resizing = false;
        Grip.ReleasePointerCaptures();
    }
}
