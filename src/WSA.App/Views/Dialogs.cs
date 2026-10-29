using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WSA.App.Views;

internal static class Dialogs
{
    /// <summary>Shows the SDK license text exactly as sdkmanager printed it. Returns true only if the user clicks Accept.</summary>
    public static async Task<bool> ConfirmLicensesAsync(XamlRoot root, string licenseText)
    {
        var box = new TextBox
        {
            Text = licenseText,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Height = 360,
            MinWidth = 560,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "Read the Android SDK license terms below. Android components are only downloaded if you accept them.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Android SDK licenses",
            Content = panel,
            PrimaryButtonText = "I accept",
            CloseButtonText = "Decline",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Folder picker for the unpackaged app: the picker must be attached to the main window handle.</summary>
    public static async Task<string?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        if (App.MainWindow is { } window)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        }

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>Asks for one line of text. Returns null when cancelled.</summary>
    public static async Task<string?> PromptAsync(XamlRoot root, string title, string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 320 };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    public static async Task<IReadOnlyList<string>> PickFilesAsync(string? extension = null)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(extension ?? "*");
        if (App.MainWindow is { } window)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        }

        var files = await picker.PickMultipleFilesAsync();
        return files.Select(f => f.Path).ToList();
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string primaryText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
