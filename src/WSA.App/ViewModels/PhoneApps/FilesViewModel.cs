using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.App.ViewModels;

public sealed partial class FilesViewModel : PhoneAppViewModel
{
    private readonly IPhoneToolsService _tools;

    public FilesViewModel(IPhoneToolsService tools) => _tools = tools;

    public ObservableCollection<DeviceFile> Items { get; } = new();

    [ObservableProperty]
    private string _currentPath = "/sdcard";

    [ObservableProperty]
    private string _pathText = "/sdcard";

    [ObservableProperty]
    private DeviceFile? _selected;

    [ObservableProperty]
    private string _summary = string.Empty;

    public override void Initialize(string serial)
    {
        base.Initialize(serial);
        _ = RefreshCommand.ExecuteAsync(null);
    }

    private string Child(string name) => CurrentPath.TrimEnd('/') + "/" + name;

    [RelayCommand]
    private Task RefreshAsync() =>
        RunAsync("Reading folder...", async (_, ct) =>
        {
            var list = await _tools.ListDirectoryAsync(Serial, CurrentPath, ct);
            Items.Clear();
            foreach (var f in list)
            {
                Items.Add(f);
            }

            PathText = CurrentPath;
            Summary = $"{list.Count} item(s)";
            return null;
        });

    public Task OpenAsync(DeviceFile file)
    {
        if (!file.IsDirectory)
        {
            return Task.CompletedTask;
        }

        CurrentPath = Child(file.Name);
        return RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private Task UpAsync()
    {
        var trimmed = CurrentPath.TrimEnd('/');
        var cut = trimmed.LastIndexOf('/');
        CurrentPath = cut <= 0 ? "/" : trimmed[..cut];
        return RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private Task GoAsync()
    {
        CurrentPath = PathText.Trim().Length == 0 ? "/sdcard" : PathText.Trim();
        return RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private Task GoSdcardAsync()
    {
        CurrentPath = "/sdcard";
        return RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (Selected is not { } file || PickFolderAsync is null)
        {
            ErrorMessage = "Select a file or folder first.";
            return;
        }

        var folder = await PickFolderAsync();
        if (folder is null)
        {
            return;
        }

        await RunAsync($"Downloading {file.Name}...", async (progress, ct) =>
        {
            await _tools.PullAsync(Serial, Child(file.Name), folder, Adapt(progress), ct);
            return $"Saved to {folder}";
        });
    }

    [RelayCommand]
    private async Task UploadAsync()
    {
        if (PickFilesAsync is null)
        {
            return;
        }

        var files = await PickFilesAsync(null);
        if (files.Count == 0)
        {
            return;
        }

        await RunAsync("Uploading...", async (progress, ct) =>
        {
            foreach (var path in files)
            {
                progress.Report(new SetupProgress($"Uploading {Path.GetFileName(path)}...", null, true));
                await _tools.PushAsync(Serial, path, CurrentPath, Adapt(progress), ct);
            }

            return $"Uploaded {files.Count} file(s) to {CurrentPath}";
        });
        await RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        var name = PromptAsync is { } ask ? await ask("New folder", "New folder") : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunAsync("Creating folder...", async (_, ct) =>
        {
            await _tools.MakeFolderAsync(Serial, Child(name.Trim()), ct);
            return null;
        });
        await RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (Selected is not { } file)
        {
            ErrorMessage = "Select a file or folder first.";
            return;
        }

        var name = PromptAsync is { } ask ? await ask("Rename", file.Name) : null;
        if (string.IsNullOrWhiteSpace(name) || name == file.Name)
        {
            return;
        }

        await RunAsync("Renaming...", async (_, ct) =>
        {
            await _tools.RenameAsync(Serial, Child(file.Name), name.Trim(), ct);
            return null;
        });
        await RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } file)
        {
            ErrorMessage = "Select a file or folder first.";
            return;
        }

        if (!await AskAsync("Delete from phone?", $"'{file.Name}' will be deleted from {CurrentPath}. This cannot be undone.", "Delete"))
        {
            return;
        }

        await RunAsync("Deleting...", async (_, ct) =>
        {
            await _tools.DeleteAsync(Serial, Child(file.Name), ct);
            return null;
        });
        await RefreshCommand.ExecuteAsync(null);
    }
}
