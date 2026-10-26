using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.App.ViewModels;

public partial class DevicesViewModel : ObservableObject
{
    private const int MaxLogLines = 300;
    private static readonly TimeSpan BootPollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BootPollTimeout = TimeSpan.FromSeconds(90);

    private readonly ISdkLocator _sdk;
    private readonly IAvdService _avds;
    private readonly IAdbService _adb;
    private readonly IAvdLifecycleService _lifecycle;
    private CancellationTokenSource? _operationCts;

    public DevicesViewModel(ISdkLocator sdk, IAvdService avds, IAdbService adb, IAvdLifecycleService lifecycle)
    {
        _sdk = sdk;
        _avds = avds;
        _adb = adb;
        _lifecycle = lifecycle;
    }

    /// <summary>Set by the page: shows a confirmation dialog and returns true if the user confirmed.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuccess))]
    private string? _successMessage;

    [ObservableProperty]
    private string _operationText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _hasLoaded;

    [ObservableProperty]
    private string _summary = string.Empty;

    public bool IsNotBusy => !IsBusy;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasSuccess => !string.IsNullOrEmpty(SuccessMessage);

    public bool ShowEmptyState => HasLoaded && !IsBusy && !HasError && Devices.Count == 0;

    public ObservableCollection<AvdRow> Devices { get; } = new();

    public ObservableCollection<AdbRow> ConnectedDevices { get; } = new();

    public ObservableCollection<string> OperationLog { get; } = new();

    [RelayCommand]
    private Task RefreshAsync() =>
        RunAsync("Refreshing...", async (_, ct) =>
        {
            await ReloadAsync(ct);
            return null;
        });

    [RelayCommand]
    private void CancelOperation()
    {
        try
        {
            _operationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Operation just finished.
        }
    }

    public async Task<(IReadOnlyList<SystemImageInfo> Images, IReadOnlyList<DeviceProfile> Profiles)> LoadCreateOptionsAsync()
    {
        if (_sdk.Last is null)
        {
            await _sdk.DetectAsync();
        }

        var images = await _lifecycle.ListSystemImagesAsync();
        var profiles = await _lifecycle.ListDeviceProfilesAsync();
        return (images, profiles);
    }

    public string? ValidateName(string name) => _lifecycle.ValidateName(name);

    public bool NameExists(string name) =>
        Devices.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public Task CreateAsync(CreateAvdRequest request) =>
        RunAsync($"Creating '{request.Name}'...", async (log, ct) =>
        {
            var result = await _lifecycle.CreateAsync(request, log, ct);
            if (result.Success)
            {
                await ReloadAsync(ct);
            }

            return result;
        });

    public Task StartAsync(AvdRow row) =>
        RunAsync($"Starting '{row.Name}'...", async (log, ct) =>
        {
            if (row.IsRunning)
            {
                return new OperationResult(false, $"'{row.Name}' is already running ({row.Serial}).");
            }

            var result = await _lifecycle.StartAsync(row.Name, ct);
            if (!result.Success)
            {
                return result;
            }

            OperationText = $"Waiting for '{row.Name}' to appear in adb...";
            var deadline = DateTime.UtcNow + BootPollTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(BootPollInterval, ct);
                var list = await ReloadAsync(ct);
                if (list.Any(a => a.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase) && a.IsRunning))
                {
                    return new OperationResult(true, $"'{row.Name}' is running.");
                }
            }

            return new OperationResult(true,
                result.Message + " It has not shown up in adb yet; first boot can take a few minutes. Press Refresh later.");
        });

    public Task StopAsync(AvdRow row) =>
        RunAsync($"Stopping '{row.Name}'...", async (log, ct) =>
        {
            if (row.Serial is null)
            {
                return new OperationResult(false, $"'{row.Name}' is not running.");
            }

            var result = await _lifecycle.StopAsync(row.Serial, log, ct);
            if (result.Success)
            {
                // Give the emulator a moment to disappear from adb before re-reading state.
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                await ReloadAsync(ct);
            }

            return result;
        });

    public async Task DeleteAsync(AvdRow row)
    {
        if (IsBusy)
        {
            return;
        }

        if (row.IsRunning)
        {
            ErrorMessage = $"'{row.Name}' is running. Stop it before deleting.";
            return;
        }

        var confirm = ConfirmAsync;
        var confirmed = confirm is not null && await confirm(
            $"Delete '{row.Name}'?",
            $"This permanently deletes the virtual device and its data{(row.Path.Length > 0 ? $" at:\n{row.Path}" : string.Empty)}.");
        if (!confirmed)
        {
            return;
        }

        await RunAsync($"Deleting '{row.Name}'...", async (log, ct) =>
        {
            var result = await _lifecycle.DeleteAsync(row.Name, log, ct);
            if (result.Success)
            {
                await ReloadAsync(ct);
            }

            return result;
        });
    }

    /// <summary>Runs one operation at a time with a busy state, cancellation, output log and error capture.</summary>
    private async Task RunAsync(string title, Func<IProgress<OutputLine>, CancellationToken, Task<OperationResult?>> work)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        SuccessMessage = null;
        OperationText = title;
        OperationLog.Clear();
        _operationCts = new CancellationTokenSource();

        // Progress<T> posts to the UI thread because it is created here, on the UI thread.
        var progress = new Progress<OutputLine>(line =>
        {
            OperationLog.Add(line.Text);
            while (OperationLog.Count > MaxLogLines)
            {
                OperationLog.RemoveAt(0);
            }
        });

        try
        {
            var result = await work(progress, _operationCts.Token);
            if (result is { Success: false })
            {
                ErrorMessage = result.Message;
            }
            else if (result is not null)
            {
                SuccessMessage = result.Message;
            }
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Cancelled.";
            try
            {
                await ReloadAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is ToolUnavailableException or ToolFailedException)
            {
                // Reload after cancel is best effort; the error above already tells the user.
            }
        }
        catch (Exception ex) when (ex is ToolUnavailableException or ToolFailedException)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Unexpected error: {ex.Message}";
        }
        finally
        {
            _operationCts.Dispose();
            _operationCts = null;
            OperationText = string.Empty;
            HasLoaded = true;
            IsBusy = false;
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    private async Task<IReadOnlyList<AvdInfo>> ReloadAsync(CancellationToken ct)
    {
        if (_sdk.Last is null)
        {
            await _sdk.DetectAsync(ct);
        }

        var list = await _avds.ListAsync(ct);
        Devices.Clear();
        foreach (var avd in list)
        {
            Devices.Add(AvdRow.From(avd));
        }

        ConnectedDevices.Clear();
        if (_sdk.Last?.Get(SdkTool.Adb).IsAvailable == true)
        {
            foreach (var d in await _adb.ListDevicesAsync(ct))
            {
                ConnectedDevices.Add(AdbRow.From(d));
            }
        }

        var running = list.Count(a => a.IsRunning);
        Summary = $"{list.Count} virtual device(s), {running} running";
        return list;
    }
}
