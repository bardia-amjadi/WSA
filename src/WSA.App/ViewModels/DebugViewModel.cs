using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.App.ViewModels;

/// <summary>Live view of the application log (shown only while Debug mode is on).</summary>
public sealed partial class DebugViewModel : ObservableObject
{
    private readonly IAppLog _log;
    private readonly ISdkLocator _sdk;
    private readonly IJavaDiagnostics _java;
    private readonly DispatcherQueue _dispatcher;

    public DebugViewModel(IAppLog log, ISdkLocator sdk, IJavaDiagnostics java)
    {
        _log = log;
        _sdk = sdk;
        _java = java;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _log.EntryAdded += OnEntryAdded;
        Reload();
    }

    public ObservableCollection<string> Lines { get; } = new();

    [ObservableProperty]
    private bool _errorsOnly;

    [ObservableProperty]
    private string _status = string.Empty;

    public string LogDirectory => _log.LogDirectory;

    partial void OnErrorsOnlyChanged(bool value) => Reload();

    public void Reload()
    {
        Lines.Clear();
        foreach (var entry in _log.Snapshot().Where(Matches))
        {
            Lines.Add(entry.Display);
        }
    }

    private bool Matches(LogEntry entry) => !ErrorsOnly || entry.Level == "ERROR";

    private void OnEntryAdded(object? sender, LogEntry entry)
    {
        if (!Matches(entry))
        {
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            Lines.Add(entry.Display);
            while (Lines.Count > 2000)
            {
                Lines.RemoveAt(0);
            }
        });
    }

    [RelayCommand]
    private void Clear()
    {
        _log.Clear();
        Lines.Clear();
    }

    [RelayCommand]
    private void CopyAll()
    {
        SetClipboard(string.Join(Environment.NewLine, Lines));
        Status = $"Copied {Lines.Count} lines.";
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_log.LogDirectory);
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe");
            psi.ArgumentList.Add(_log.LogDirectory);
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Status = $"Could not open the folder: {ex.Message}";
        }
    }

    /// <summary>Copies a report for bug reports: versions, SDK state and the Java the tools get. Contains local paths, no secrets.</summary>
    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"WSA diagnostics {DateTime.Now:O}");
            sb.AppendLine($"OS: {Environment.OSVersion}   .NET: {Environment.Version}   64-bit: {Environment.Is64BitProcess}");
            var status = await _sdk.DetectAsync();
            sb.AppendLine($"SDK root: {status.SdkRoot ?? "(none)"}  [{status.SdkRootSource}]");
            foreach (var tool in status.Tools)
            {
                sb.AppendLine($"  {tool.DisplayName}: {(tool.IsAvailable ? "ok" : tool.Path is null ? "missing" : "problem")}  {tool.Path}  {tool.Version}  {tool.Problem}");
            }

            foreach (var d in status.Diagnostics)
            {
                sb.AppendLine($"  ! {d}");
            }

            sb.AppendLine("Java:");
            var report = await _java.ProbeAsync();
            foreach (var line in report.Lines)
            {
                sb.AppendLine("  " + line);
            }

            SetClipboard(sb.ToString());
            Status = "Diagnostics copied to the clipboard.";
        }
        catch (Exception ex)
        {
            Status = $"Could not collect diagnostics: {ex.Message}";
        }
    }

    private static void SetClipboard(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }
}
