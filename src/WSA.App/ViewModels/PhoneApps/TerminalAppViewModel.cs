using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WSA.Core.Interfaces;
using WSA.Core.Services;

namespace WSA.App.ViewModels;

/// <summary>A terminal for the phone: each line runs once as <c>adb shell ...</c>. Interactive programs are not supported.</summary>
public sealed partial class TerminalAppViewModel : PhoneAppViewModel
{
    private readonly IPhoneToolsService _tools;
    private readonly List<string> _history = new();
    private int _historyIndex;

    public TerminalAppViewModel(IPhoneToolsService tools) => _tools = tools;

    public ObservableCollection<string> Lines { get; } = new();

    [ObservableProperty]
    private string _input = string.Empty;

    public override void Initialize(string serial)
    {
        base.Initialize(serial);
        Lines.Add("Phone terminal. Each line runs once on the phone (adb shell). Type 'help' for tips.");
    }

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        var text = Input.Trim();
        if (text.Length == 0)
        {
            return;
        }

        Input = string.Empty;
        _history.Add(text);
        _historyIndex = _history.Count;
        Lines.Add("$ " + text);

        if (text == "clear")
        {
            Lines.Clear();
            return;
        }

        if (text == "help")
        {
            Lines.Add("Examples: ls /sdcard | getprop ro.product.model | pm list packages | dumpsys battery | df -h | ip addr");
            Lines.Add("Pipes and && run on the phone's own shell. Interactive programs (top, vi, a bare 'sh') are not supported; use 'top -n 1'.");
            Lines.Add("clear = empty this window.");
            return;
        }

        if (!CommandLineTokenizer.TryTokenize(text, out var tokens, out var error))
        {
            Lines.Add(error ?? "Could not read that command.");
            return;
        }

        await RunAsync("Running...", async (_, ct) =>
        {
            var (output, exit) = await _tools.RunShellAsync(Serial, tokens, null, ct);
            foreach (var line in output.Split('\n'))
            {
                Lines.Add(line.TrimEnd('\r'));
            }

            if (exit is not (null or 0))
            {
                Lines.Add($"[exit code {exit}]");
            }

            return null;
        });

        if (HasError)
        {
            Lines.Add(ErrorMessage!);
            ErrorMessage = null;
        }
    }

    public void HistoryPrevious()
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Max(0, _historyIndex - 1);
        Input = _history[_historyIndex];
    }

    public void HistoryNext()
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
        Input = _historyIndex >= _history.Count ? string.Empty : _history[_historyIndex];
    }
}
