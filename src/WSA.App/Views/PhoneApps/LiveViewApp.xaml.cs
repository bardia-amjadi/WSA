using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using WSA.App.ViewModels;
using WSA.Core.Interfaces;
using WSA.Core.Models;
using WSA.Core.Services;

namespace WSA.App.Views.PhoneApps;

/// <summary>
/// Live View Control. "Smooth" mode streams H.264 from the phone (scrcpy server) and decodes it with Windows' own video
/// decoder; frame rate is the one chosen in Settings (15, 30 or 60). "Compatible" mode polls screenshots, a few frames per
/// second, and is used automatically when the stream cannot start.
/// </summary>
public sealed partial class LiveViewApp : UserControl
{
    private sealed record Frame(byte[] Data, TimeSpan Time);

    private readonly string _serial;
    private readonly IScreenStreamService _stream;
    private readonly IPhoneService _phone;
    private readonly ISettingsService _settings;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();

    private CancellationTokenSource? _cts;
    private IScreenStreamSession? _session;
    private Channel<Frame>? _frames;
    private MediaPlayer? _player;
    private MediaStreamSource? _mss;
    private DispatcherQueueTimer? _fpsTimer;
    private int _queued;
    private int _frameCount;
    private int _totalFrames;
    private int _videoW, _videoH, _devW, _devH;
    private int _fps = 30;
    private string _appliedKey = string.Empty;
    private Point? _down;
    private long _downTicks;
    private bool _started;

    public LiveViewApp(string serial)
    {
        _serial = serial;
        _stream = App.GetService<IScreenStreamService>();
        _phone = App.GetService<IPhoneService>();
        _settings = App.GetService<ISettingsService>();
        ViewModel = App.GetService<LiveViewViewModel>();
        ViewModel.Initialize(serial);
        InitializeComponent();
    }

    public LiveViewViewModel ViewModel { get; }

    private static string SettingsKey(AppSettings s) => $"{s.LiveViewMode}|{s.LiveViewFps}|{s.LiveViewMaxSize}|{s.LiveViewBitrateMbps}";

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        ViewModel.RestartRequested += OnRestartRequested;
        _settings.Changed += OnSettingsChanged;

        _fpsTimer = _ui.CreateTimer();
        _fpsTimer.Interval = TimeSpan.FromSeconds(1);
        _fpsTimer.Tick += (_, _) =>
        {
            var count = Interlocked.Exchange(ref _frameCount, 0);
            ViewModel.FpsText = count > 0 ? $"{count} fps" : string.Empty;
        };
        _fpsTimer.Start();

        await StartAsync();
    }

    private async void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.RestartRequested -= OnRestartRequested;
        _settings.Changed -= OnSettingsChanged;
        _fpsTimer?.Stop();
        _started = false;
        await StopAsync();
    }

    private async void OnRestartRequested(object? sender, EventArgs e) => await StartAsync();

    private void OnSettingsChanged(object? sender, EventArgs e) => _ui.TryEnqueue(async () =>
    {
        if (_started && SettingsKey(_settings.Current) != _appliedKey)
        {
            await StartAsync();
        }
    });

    // ---- start / stop -----------------------------------------------------------------------------------

    private async Task StartAsync()
    {
        await StopAsync();
        var settings = _settings.Current;
        _appliedKey = SettingsKey(settings);
        _fps = settings.LiveViewFps;
        ViewModel.ErrorMessage = null;
        await LoadDeviceSizeAsync();

        var cts = _cts = new CancellationTokenSource();
        if (settings.LiveViewMode == "screenshots")
        {
            ViewModel.StatusText = "Compatible mode (screenshots)";
            _ = ScreenshotLoopAsync(cts.Token);
            return;
        }

        try
        {
            ViewModel.StatusText = "Connecting...";
            var session = await _stream.StartAsync(
                _serial,
                new ScreenStreamOptions(settings.LiveViewFps, settings.LiveViewMaxSize, settings.LiveViewBitrateMbps),
                new Progress<string>(text => ViewModel.StatusText = text),
                cts.Token);
            _session = session;
            _frames = Channel.CreateUnbounded<Frame>();
            _totalFrames = 0;
            _ = Task.Run(() => PumpAsync(session, _frames, cts.Token));
            _ = WatchdogAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Stopped while starting.
        }
        catch (Exception ex) when (ex is ToolFailedException or ToolUnavailableException)
        {
            await FallBackAsync($"Smooth stream unavailable ({ex.Message}). Using screenshots.");
        }
    }

    private async Task StopAsync()
    {
        _cts?.Cancel();
        _frames?.Writer.TryComplete();
        var session = _session;
        _session = null;
        if (session is not null)
        {
            await session.DisposeAsync();
        }

        Player.SetMediaPlayer(null);
        _player?.Dispose();
        _player = null;
        _mss = null;
        _frames = null;
        _queued = 0;
        _videoW = _videoH = 0;
        Player.Visibility = Visibility.Collapsed;
        ShotImage.Visibility = Visibility.Collapsed;
        _cts = null;
    }

    private async Task FallBackAsync(string reason)
    {
        await StopAsync();
        ViewModel.StatusText = reason;
        var cts = _cts = new CancellationTokenSource();
        _ = ScreenshotLoopAsync(cts.Token);
    }

    private async Task WatchdogAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (Volatile.Read(ref _totalFrames) == 0)
        {
            await FallBackAsync("No video arrived from the phone. Using screenshots instead.");
        }
    }

    private async Task LoadDeviceSizeAsync()
    {
        try
        {
            var info = await _phone.GetInfoAsync(_serial);
            var m = Regex.Match(info.Resolution ?? string.Empty, @"(\d+)\s*x\s*(\d+)");
            if (m.Success)
            {
                _devW = int.Parse(m.Groups[1].Value);
                _devH = int.Parse(m.Groups[2].Value);
            }
        }
        catch (Exception ex) when (ex is ToolFailedException or ToolUnavailableException)
        {
            // Screen size is learned from the first frame instead.
        }
    }

    // ---- H.264 stream -----------------------------------------------------------------------------------

    private async Task PumpAsync(IScreenStreamSession session, Channel<Frame> frames, CancellationToken ct)
    {
        var splitter = new H264Splitter();
        var buffer = new byte[64 * 1024];
        var clock = Stopwatch.StartNew();
        TimeSpan? first = null;
        var needKey = true;
        var playerReady = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await session.Video.ReadAsync(buffer.AsMemory(), ct);
                if (read == 0)
                {
                    break;
                }

                foreach (var frame in splitter.Feed(buffer.AsSpan(0, read)))
                {
                    var key = H264Splitter.IsKeyFrame(frame);
                    if (needKey && !key)
                    {
                        continue;
                    }

                    if (Volatile.Read(ref _queued) > 30 && !key)
                    {
                        needKey = true;
                        continue;
                    }

                    needKey = false;
                    if (!playerReady)
                    {
                        var w = 720;
                        var h = 1280;
                        if (splitter.LastSps is { } sps && H264Splitter.TryReadSize(sps, out var sw, out var sh))
                        {
                            w = sw;
                            h = sh;
                        }

                        await OnUiAsync(() => CreatePlayer(w, h, frames));
                        playerReady = true;
                    }

                    first ??= clock.Elapsed;
                    Interlocked.Increment(ref _queued);
                    Interlocked.Increment(ref _frameCount);
                    Interlocked.Increment(ref _totalFrames);
                    frames.Writer.TryWrite(new Frame(frame, clock.Elapsed - first.Value));
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Normal when the window is closed or the phone disconnects.
        }
        finally
        {
            frames.Writer.TryComplete();
        }

        if (!ct.IsCancellationRequested)
        {
            _ui.TryEnqueue(async () => await FallBackAsync("The video stream ended. Using screenshots."));
        }
    }

    private void CreatePlayer(int width, int height, Channel<Frame> frames)
    {
        _videoW = width;
        _videoH = height;
        if (_devW == 0 || _devH == 0)
        {
            _devW = width;
            _devH = height;
        }

        var props = VideoEncodingProperties.CreateH264();
        props.Width = (uint)width;
        props.Height = (uint)height;
        var mss = new MediaStreamSource(new VideoStreamDescriptor(props))
        {
            BufferTime = TimeSpan.Zero,
            IsLive = true,
            CanSeek = false,
        };
        mss.SampleRequested += async (_, args) =>
        {
            var deferral = args.Request.GetDeferral();
            try
            {
                var frame = await frames.Reader.ReadAsync();
                Interlocked.Decrement(ref _queued);
                var sample = MediaStreamSample.CreateFromBuffer(frame.Data.AsBuffer(), frame.Time);
                sample.Duration = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _fps));
                args.Request.Sample = sample;
            }
            catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException)
            {
                // End of stream.
            }
            finally
            {
                deferral.Complete();
            }
        };

        _mss = mss;
        _player = new MediaPlayer { RealTimePlayback = true, AutoPlay = true };
        _player.Source = MediaSource.CreateFromMediaStreamSource(mss);
        Player.SetMediaPlayer(_player);
        ShotImage.Visibility = Visibility.Collapsed;
        Player.Visibility = Visibility.Visible;
        ViewModel.StatusText = $"Smooth stream {width}x{height}, up to {_fps} fps";
    }

    private Task OnUiAsync(Action action)
    {
        var tcs = new TaskCompletionSource();
        _ui.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    // ---- screenshot fallback ----------------------------------------------------------------------------

    private async Task ScreenshotLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var png = await _phone.CaptureScreenAsync(_serial, ct);
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(png.AsBuffer());
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                _videoW = bitmap.PixelWidth;
                _videoH = bitmap.PixelHeight;
                if (_devW == 0 || _devH == 0)
                {
                    _devW = _videoW;
                    _devH = _videoH;
                }

                ShotImage.Source = bitmap;
                Player.Visibility = Visibility.Collapsed;
                ShotImage.Visibility = Visibility.Visible;
                Interlocked.Increment(ref _frameCount);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        catch (Exception ex) when (ex is ToolFailedException or ToolUnavailableException)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    // ---- input ------------------------------------------------------------------------------------------

    private void Touch_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _down = e.GetCurrentPoint(TouchLayer).Position;
        _downTicks = Environment.TickCount64;
        TouchLayer.CapturePointer(e.Pointer);
    }

    private async void Touch_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        TouchLayer.ReleasePointerCaptures();
        if (_down is not { } start)
        {
            return;
        }

        _down = null;
        var end = e.GetCurrentPoint(TouchLayer).Position;
        var elapsed = (int)Math.Clamp(Environment.TickCount64 - _downTicks, 50, 2000);
        if (!MapToDevice(start, out var x1, out var y1))
        {
            return;
        }

        var moved = Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y);
        if (moved < 12)
        {
            await ViewModel.TapAsync(x1, y1);
        }
        else if (MapToDevice(end, out var x2, out var y2))
        {
            await ViewModel.SwipeAsync(x1, y1, x2, y2, elapsed);
        }
    }

    /// <summary>Converts a point on the letterboxed video into phone pixels.</summary>
    private bool MapToDevice(Point p, out int x, out int y)
    {
        x = y = 0;
        if (_videoW <= 0 || _videoH <= 0 || _devW <= 0 || _devH <= 0)
        {
            return false;
        }

        double w = TouchLayer.ActualWidth, h = TouchLayer.ActualHeight;
        var scale = Math.Min(w / _videoW, h / _videoH);
        var drawW = _videoW * scale;
        var drawH = _videoH * scale;
        var nx = (p.X - (w - drawW) / 2) / drawW;
        var ny = (p.Y - (h - drawH) / 2) / drawH;
        if (nx < 0 || nx > 1 || ny < 0 || ny > 1)
        {
            return false;
        }

        x = (int)(nx * (_devW - 1));
        y = (int)(ny * (_devH - 1));
        return true;
    }

    private void Text_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            ViewModel.SendTextCommand.Execute(null);
        }
    }
}
