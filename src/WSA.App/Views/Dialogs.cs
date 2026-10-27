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
