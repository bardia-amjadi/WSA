using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WSA.Core.Models;

namespace WSA.App.ViewModels;

/// <summary>
/// Shared plumbing for pages that run one long operation at a time: busy state, determinate/indeterminate progress,
/// a capped output log, Cancel, and uniform error reporting.
/// </summary>
public abstract partial class OperationViewModelBase : ObservableObject
{
    private const int MaxLogLines = 200;
    private static readonly Regex ProgressBarLine = new(@"^\[[=\s>-]*\]\s*\d+%", RegexOptions.Compiled);

    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuccess))]
    private string? _successMessage;

    [ObservableProperty]
    private string _operationText = string.Empty;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    public bool IsNotBusy => !IsBusy;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasSuccess => !string.IsNullOrEmpty(SuccessMessage);

    public ObservableCollection<string> OperationLog { get; } = new();

    [RelayCommand]
    private void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation finished at the same moment.
        }
    }

    /// <summary>Called after every operation (success, failure or cancel), on the UI thread.</summary>
    protected virtual Task OnOperationFinishedAsync() => Task.CompletedTask;

    /// <summary>Runs <paramref name="work"/> exclusively. It returns a success message (or null for none).</summary>
    protected async Task RunAsync(string title, Func<IProgress<SetupProgress>, CancellationToken, Task<string?>> work)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        SuccessMessage = null;
        OperationText = title;
        ProgressValue = 0;
        IsIndeterminate = true;
        OperationLog.Clear();
        _cts = new CancellationTokenSource();

        // Created on the UI thread, so reports from background work are posted back to it.
        var progress = new Progress<SetupProgress>(Handle);

        try
        {
            SuccessMessage = await work(progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Cancelled.";
        }
        catch (Exception ex) when (ex is ToolFailedException or ToolUnavailableException)
        {
            ErrorMessage = ex.Message;
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Network error: {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"File error: {ex.Message}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Unexpected error: {ex.Message}";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            OperationText = string.Empty;
            IsBusy = false;
        }

        try
        {
            await OnOperationFinishedAsync();
        }
        catch (Exception ex) when (ex is ToolFailedException or ToolUnavailableException)
        {
            ErrorMessage ??= ex.Message;
        }
    }

    private void Handle(SetupProgress p)
    {
        if (p.NewStep)
        {
            IsIndeterminate = true;
            ProgressValue = 0;
            OperationText = p.Message;
        }

        if (p.Percent is { } percent)
        {
            IsIndeterminate = false;
            ProgressValue = percent;
        }

        if (string.IsNullOrWhiteSpace(p.Message) || p.NewStep)
        {
            return;
        }

        // sdkmanager redraws "[=====   ] 40% Downloading..." constantly; show it live instead of flooding the log.
        if (ProgressBarLine.IsMatch(p.Message) || p.Message.Contains(" of ", StringComparison.Ordinal) && p.Message.EndsWith("MB", StringComparison.Ordinal))
        {
            OperationText = p.Message;
            return;
        }

        OperationLog.Add(p.Message);
        while (OperationLog.Count > MaxLogLines)
        {
            OperationLog.RemoveAt(0);
        }
    }
}
