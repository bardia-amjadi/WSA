using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WSA.App.ViewModels;
using WSA.Core.Models;

namespace WSA.App.Views;

public sealed partial class DevicesPage : Page
{
    public DevicesPage()
    {
        ViewModel = App.GetService<DevicesViewModel>();
        InitializeComponent();
    }

    public DevicesViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ConfirmAsync = ConfirmAsync;
        await ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static AvdRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as AvdRow;

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            await ViewModel.StartAsync(row);
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            await ViewModel.StopAsync(row);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            await ViewModel.DeleteAsync(row);
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<SystemImageInfo> images;
        IReadOnlyList<DeviceProfile> profiles;
        try
        {
            (images, profiles) = await ViewModel.LoadCreateOptionsAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Cannot create a virtual device", ex.Message);
            return;
        }

        if (images.Count == 0)
        {
            await ShowMessageAsync(
                "No system images installed",
                "Install one first, for example in WSA Terminal:\n\n" +
                "sdkmanager \"system-images;android-34;google_apis;x86_64\"\n\n" +
                "Then press Create again. See Runtime & SDK for the full setup steps.");
            return;
        }

        if (profiles.Count == 0)
        {
            await ShowMessageAsync("No device profiles", "avdmanager returned no device profiles (avdmanager list device -c).");
            return;
        }

        var nameBox = new TextBox { Header = "Name", PlaceholderText = "Pixel_34" };
        var imageBox = new ComboBox
        {
            Header = "System image",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = images,
            DisplayMemberPath = nameof(SystemImageInfo.Display),
            SelectedIndex = 0,
        };
        var deviceBox = new ComboBox
        {
            Header = "Device profile",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = profiles.Select(p => p.Id).ToList(),
            IsEditable = false,
        };
        var preferred = profiles.Select(p => p.Id).ToList().IndexOf("pixel_6");
        deviceBox.SelectedIndex = preferred >= 0 ? preferred : 0;
        var errorText = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
        panel.Children.Add(nameBox);
        panel.Children.Add(imageBox);
        panel.Children.Add(deviceBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Create virtual device",
            Content = panel,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        // Keep the dialog open until the input is valid.
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var name = nameBox.Text.Trim();
            var error = ViewModel.ValidateName(name);
            if (error is null && ViewModel.NameExists(name))
            {
                error = $"A virtual device named '{name}' already exists.";
            }

            if (error is not null)
            {
                errorText.Text = error;
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (imageBox.SelectedItem is SystemImageInfo image && deviceBox.SelectedItem is string deviceId)
        {
            await ViewModel.CreateAsync(new CreateAvdRequest(nameBox.Text.Trim(), image.Package, deviceId));
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }
}
