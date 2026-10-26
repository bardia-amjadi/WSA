using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WSA.Core.Interfaces;
using WSA.Core.Models;
using WSA.Core.Services;

namespace WSA.App.ViewModels;

public enum TerminalLineKind
{
    Command,
    Output,
    Error,
    Info,
    Status,
}

/// <summary>One rendered line in the terminal buffer.</summary>
public sealed class TerminalLine
{
    public TerminalLine(string text, TerminalLineKind kind)
    {
        Text = text;
        Kind = kind;
    }

    public string Text { get; }

    public TerminalLineKind Kind { get; }

    public Brush Foreground => TerminalPalette.For(Kind);
}

/// <summary>Brushes are created lazily on first use, which always happens on the UI thread.</summary>
internal static class TerminalPalette
{
    private static Brush? _output, _error, _info, _command, _status;

    public static Brush For(TerminalLineKind kind) => kind switch
    {
        TerminalLineKind.Command => _command ??= Make(0x7C, 0xC4, 0xFF),
        TerminalLineKind.Error => _error ??= Make(0xFF, 0x8A, 0x80),
        TerminalLineKind.Info => _info ??= Make(0x9E, 0xE0, 0xC0),
        TerminalLineKind.Status => _status ??= Make(0x9A, 0x9A, 0xA6),
        _ => _output ??= Make(0xE6, 0xE6, 0xEA),
    };

    private static SolidColorBrush Make(byte r, byte g, byte b) =>
        new(Windows.UI.Color.FromArgb(0xFF, r, g, b));
}

/// <summary>
/// WSA Terminal state. Output from background processes is queued thread-safely and flushed to the UI
/// collection in small batches on a dispatcher timer, so fast producers such as <c>adb logcat</c> cannot freeze the UI.
/// </summary>
public partial class TerminalViewModel : ObservableObject
{
    private const int FlushBatchSize = 400;
    private const int MaxHistory = 200;

    private readonly TerminalService _terminal;
    private readonly ISettingsService _settings;
    private readonly ISdkLocator _sdk;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly ConcurrentQueue<TerminalLine> _pending = new();
    private CancellationTokenSource? _cts;
    private int _historyIndex;

    public TerminalViewModel(TerminalService terminal, ISettingsService settings, ISdkLocator sdk)
    {
        _terminal = terminal;
        _settings = settings;
        _sdk = sdk;

        _dispatcher = DispatcherQueue.GetForCurrentThread()
                      ?? throw new InvalidOperationException("TerminalViewModel must be created on the UI thread.");
        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(80);
        _timer.Tick += (_, _) => Flush();
        _timer.Start();

        _historyIndex = _settings.Current.TerminalHistory.Count;
        _sdk.StatusChanged += (_, _) => _dispatcher.TryEnqueue(UpdatePrompt);
        UpdatePrompt();

        Enqueue("WSA Terminal - runs real Android SDK tools directly (no shell). Type 'help' for commands.", TerminalLineKind.Info);
        if (_sdk.Last is null)
        {
            _ = InitialDetectAsync();
        }
    }

    private async Task InitialDetectAsync()
    {
        try
        {
            await _sdk.DetectAsync();
        }
        catch (Exception ex)
        {
            Enqueue($"SDK detection failed: {ex.Message}", TerminalLineKind.Error);
        }
    }

    public ObservableCollection<TerminalLine> Lines { get; } = new();

    /// <summary>Raised on the UI thread after new lines were added, so the view can scroll to the end.</summary>
    public event EventHandler? LinesAdded;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _prompt = "wsa>";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRunning))]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "Ready";

    public bool IsNotRunning => !IsRunning;

    [RelayCommand]
    private async Task SubmitAsync()
    {
        var text = InputText.Trim();
        if (IsRunning)
        {
            Enqueue("A command is still running. Press Esc or Cancel to stop it first.", TerminalLineKind.Info);
            return;
        }

        if (text.Length == 0)
        {
            return;
        }

        InputText = string.Empty;
        RememberHistory(text);
        Enqueue($"{Prompt} {text}", TerminalLineKind.Command);

        IsRunning = true;
        StatusText = "Running...";
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var outcome = await _terminal.ExecuteAsync(text, new QueueProgress(this), token);

            if (outcome.ClearRequested)
            {
                ClearBuffer();
                StatusText = "Cleared";
            }
            else
            {
                var seconds = outcome.Duration.TotalSeconds;
                var status = outcome.Cancelled
                    ? $"[cancelled after {seconds:0.0} s]"
                    : outcome.ExitCode is { } code
                        ? $"[exit code {code} in {seconds:0.0} s]"
                        : outcome.Failed ? $"[could not run - {seconds:0.0} s]" : $"[done in {seconds:0.0} s]";
                Enqueue(status, TerminalLineKind.Status);
                StatusText = status.Trim('[', ']');
            }
        }
        catch (Exception ex)
        {
            Enqueue($"Unexpected error: {ex.Message}", TerminalLineKind.Error);
            StatusText = "Error";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The command finished at the same moment; nothing to cancel.
        }
    }

    [RelayCommand]
    private void Clear() => ClearBuffer();

    [RelayCommand]
    private void Copy()
    {
        Flush();
        var text = string.Join(Environment.NewLine, Lines.Select(l => l.Text));
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        StatusText = $"Copied {Lines.Count} lines to the clipboard";
    }

    /// <summary>Previous history entry (Up arrow), or null if there is none.</summary>
    public string? HistoryUp()
    {
        var history = _settings.Current.TerminalHistory;
        if (history.Count == 0)
        {
            return null;
        }

        _historyIndex = Math.Max(0, Math.Min(_historyIndex, history.Count) - 1);
        return history[_historyIndex];
    }

    /// <summary>Next history entry (Down arrow); an empty string once past the newest entry.</summary>
    public string? HistoryDown()
    {
        var history = _settings.Current.TerminalHistory;
        if (_historyIndex >= history.Count - 1)
        {
            _historyIndex = history.Count;
            return string.Empty;
        }

        _historyIndex++;
        return history[_historyIndex];
    }

    private void RememberHistory(string text)
    {
        var history = _settings.Current.TerminalHistory;
        if (history.Count == 0 || history[^1] != text)
        {
            history.Add(text);
        }

        while (history.Count > MaxHistory)
        {
            history.RemoveAt(0);
        }

        _historyIndex = history.Count;
        _ = PersistHistoryAsync();
    }

    private async Task PersistHistoryAsync()
    {
        try
        {
            await _settings.SaveAsync();
        }
        catch (Exception ex)
        {
            Enqueue($"Could not save command history: {ex.Message}", TerminalLineKind.Info);
        }
    }

    private void UpdatePrompt()
    {
        var root = _sdk.Last?.SdkRoot;
        Prompt = root is null
            ? "wsa(no-sdk)>"
            : $"wsa({Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))})>";
    }

    private void Enqueue(string text, TerminalLineKind kind) => _pending.Enqueue(new TerminalLine(text, kind));

    private void ClearBuffer()
    {
        while (_pending.TryDequeue(out _))
        {
        }

        Lines.Clear();
    }

    private void Flush()
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        var added = 0;
        while (added < FlushBatchSize && _pending.TryDequeue(out var line))
        {
            Lines.Add(line);
            added++;
        }

        var max = Math.Max(200, _settings.Current.TerminalMaxLines);
        var excess = Lines.Count - max;
        for (var i = 0; i < excess; i++)
        {
            Lines.RemoveAt(0);
        }

        if (added > 0)
        {
            LinesAdded?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Thread-safe sink handed to the process runner; it only enqueues.</summary>
    private sealed class QueueProgress : IProgress<OutputLine>
    {
        private readonly TerminalViewModel _owner;

        public QueueProgress(TerminalViewModel owner) => _owner = owner;

        public void Report(OutputLine value)
        {
            var kind = value.Kind switch
            {
                OutputKind.StandardError => TerminalLineKind.Error,
                OutputKind.Info => TerminalLineKind.Info,
                _ => TerminalLineKind.Output,
            };
            _owner.Enqueue(value.Text, kind);
        }
    }
}
