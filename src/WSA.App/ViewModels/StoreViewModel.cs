using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.App.ViewModels;

public sealed record StoreRow(
    string Package, string Title, string Variant, string Description, bool Installed)
{
    public string Status => Installed ? "Installed" : "Not installed";

    public bool CanInstall => !Installed;

    public bool CanRemove => Installed;

    public static StoreRow From(StoreImage i) => new(i.Package, i.Title, i.Variant, i.Description, i.Installed);
}

public partial class StoreViewModel : OperationViewModelBase
{
    private readonly ISdkSetupService _setup;
    private readonly ISdkLocator _sdk;
    private IReadOnlyList<StoreImage> _all = Array.Empty<StoreImage>();

    public StoreViewModel(ISdkSetupService setup, ISdkLocator sdk)
    {
        _setup = setup;
        _sdk = sdk;
    }

    /// <summary>Set by the page: shows license text, true only if the user accepted.</summary>
    public Func<string, Task<bool>>? ConfirmLicensesAsync { get; set; }

    /// <summary>Set by the page: generic confirmation (title, message) -> true if confirmed.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>0 = all, 1 = Google Play, 2 = Google APIs, 3 = AOSP.</summary>
    [ObservableProperty]
    private int _filterIndex;

    [ObservableProperty]
    private bool _hasLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _sdkNotReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateDevice))]
    private string? _lastInstalledPackage;

    public bool CanCreateDevice => !string.IsNullOrEmpty(LastInstalledPackage);

    public bool ShowEmptyState => HasLoaded && IsNotBusy && !SdkNotReady && Items.Count == 0;

    public ObservableCollection<StoreRow> Items { get; } = new();

    partial void OnFilterIndexChanged(int value) => ApplyFilter();

    [RelayCommand]
    private Task RefreshAsync() =>
        RunAsync("Loading Android versions from Google...", async (_, ct) =>
        {
            await LoadCatalogAsync(ct);
            return null;
        });

    public Task InstallAsync(StoreRow row) =>
        RunAsync($"Installing {row.Title} ({row.Variant})...", async (progress, ct) =>
        {
            var license = await _setup.PreviewLicensesAsync(ct);
            if (!license.AllAccepted)
            {
                var accepted = ConfirmLicensesAsync is { } ask && await ask(license.Text);
                if (!accepted)
                {
                    return "Installation not started: the Android SDK licenses were not accepted.";
                }

                await _setup.AcceptLicensesAsync(progress, ct);
            }

            await _setup.InstallPackagesAsync(new[] { row.Package }, progress, ct);
            await LoadCatalogAsync(ct);
            LastInstalledPackage = row.Package;
            return $"{row.Title} ({row.Variant}) is installed. You can create a virtual device with it now.";
        });

    public async Task RemoveAsync(StoreRow row)
    {
        if (IsBusy)
        {
            return;
        }

        var confirmed = ConfirmAsync is { } ask && await ask(
            $"Remove {row.Title} ({row.Variant})?",
            "The system image is deleted from the SDK. Virtual devices that use it will no longer start until you install it again.");
        if (!confirmed)
        {
            return;
        }

        await RunAsync($"Removing {row.Title}...", async (progress, ct) =>
        {
            await _setup.UninstallPackageAsync(row.Package, progress, ct);
            await LoadCatalogAsync(ct);
            if (LastInstalledPackage == row.Package)
            {
                LastInstalledPackage = null;
            }

            return $"{row.Title} ({row.Variant}) was removed.";
        });
    }

    protected override Task OnOperationFinishedAsync()
    {
        HasLoaded = true;
        OnPropertyChanged(nameof(ShowEmptyState));
        return Task.CompletedTask;
    }

    private async Task LoadCatalogAsync(CancellationToken ct)
    {
        if (_sdk.Last is null)
        {
            await _sdk.DetectAsync(ct);
        }

        SdkNotReady = _sdk.Last?.Get(SdkTool.SdkManager).IsAvailable != true;
        if (SdkNotReady)
        {
            _all = Array.Empty<StoreImage>();
            ApplyFilter();
            throw new ToolUnavailableException(
                "sdkmanager is not ready yet, so Google's catalog cannot be read. Open Setup and install the missing components first.");
        }

        _all = await _setup.ListStoreImagesAsync(ct);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var variant = FilterIndex switch
        {
            1 => "Google Play",
            2 => "Google APIs",
            3 => "AOSP (no Google apps)",
            _ => null,
        };

        Items.Clear();
        foreach (var image in _all.Where(i => variant is null || i.Variant == variant))
        {
            Items.Add(StoreRow.From(image));
        }

        OnPropertyChanged(nameof(ShowEmptyState));
    }
}
