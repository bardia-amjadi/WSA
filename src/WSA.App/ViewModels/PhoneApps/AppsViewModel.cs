using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.App.ViewModels;

public sealed partial class AppsViewModel : PhoneAppViewModel
{
    private readonly IPhoneToolsService _tools;
    private readonly IPhoneService _phone;
    private List<InstalledApp> _all = new();

    public AppsViewModel(IPhoneToolsService tools, IPhoneService phone)
    {
        _tools = tools;
        _phone = phone;
    }

    public ObservableCollection<InstalledApp> Items { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _includeSystem;

    [ObservableProperty]
    private InstalledApp? _selected;

    [ObservableProperty]
    private string _summary = string.Empty;

    public override void Initialize(string serial)
    {
        base.Initialize(serial);
        _ = RefreshCommand.ExecuteAsync(null);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnIncludeSystemChanged(bool value) => _ = RefreshCommand.ExecuteAsync(null);

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var app in _all.Where(a => SearchText.Length == 0
                                           || a.Package.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            Items.Add(app);
        }

        Summary = $"{Items.Count} of {_all.Count} apps";
    }

    [RelayCommand]
    private Task RefreshAsync() =>
        RunAsync("Reading installed apps...", async (_, ct) =>
        {
            _all = (await _tools.ListAppsAsync(Serial, IncludeSystem, ct)).ToList();
            ApplyFilter();
            return null;
        });

    private bool RequireSelection()
    {
        if (Selected is null)
        {
            ErrorMessage = "Select an app first.";
            return false;
        }

        return true;
    }

    [RelayCommand]
    private async Task OpenAppAsync()
    {
        if (!RequireSelection())
        {
            return;
        }

        var package = Selected!.Package;
        await RunAsync("Opening...", async (_, ct) =>
        {
            await _tools.LaunchAppAsync(Serial, package, ct);
            return $"Opened {package} on the phone.";
        });
    }

    [RelayCommand]
    private async Task StopAppAsync()
    {
        if (!RequireSelection())
        {
            return;
        }

        var package = Selected!.Package;
        await RunAsync("Stopping...", async (_, ct) =>
        {
            await _tools.ForceStopAsync(Serial, package, ct);
            return $"Stopped {package}.";
        });
    }

    [RelayCommand]
    private async Task ClearDataAsync()
    {
        if (!RequireSelection())
        {
            return;
        }

        var package = Selected!.Package;
        if (!await AskAsync("Clear app data?", $"All data of {package} (accounts, settings, files) is erased on the phone.", "Clear data"))
        {
            return;
        }

        await RunAsync("Clearing data...", async (_, ct) =>
        {
            await _tools.ClearDataAsync(Serial, package, ct);
            return $"Cleared the data of {package}.";
        });
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (!RequireSelection())
        {
            return;
        }

        var package = Selected!.Package;
        if (!await AskAsync("Uninstall app?", $"{package} will be removed from the phone.", "Uninstall"))
        {
            return;
        }

        await RunAsync("Uninstalling...", async (_, ct) =>
        {
            await _tools.UninstallAsync(Serial, package, ct);
            return $"Uninstalled {package}.";
        });
        await RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task InstallApkAsync()
    {
        if (PickFilesAsync is null)
        {
            return;
        }

        var files = await PickFilesAsync(".apk");
        if (files.Count == 0)
        {
            return;
        }

        await RunAsync("Installing...", async (progress, ct) =>
        {
            foreach (var apk in files)
            {
                progress.Report(new SetupProgress($"Installing {Path.GetFileName(apk)}...", null, true));
                var result = await _phone.InstallApkAsync(Serial, apk, Adapt(progress), ct);
                if (!result.Success)
                {
                    throw new ToolFailedException(result.Message);
                }
            }

            return $"Installed {files.Count} app(s).";
        });
        await RefreshCommand.ExecuteAsync(null);
    }
}
