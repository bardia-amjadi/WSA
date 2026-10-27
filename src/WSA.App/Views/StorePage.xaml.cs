using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WSA.App.ViewModels;

namespace WSA.App.Views;

public sealed partial class StorePage : Page
{
    public StorePage()
    {
        ViewModel = App.GetService<StoreViewModel>();
        InitializeComponent();
    }

    public StoreViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ConfirmLicensesAsync = text => Dialogs.ConfirmLicensesAsync(XamlRoot, text);
        ViewModel.ConfirmAsync = (title, message) => Dialogs.ConfirmAsync(XamlRoot, title, message, "Remove");

        // Loading the catalog needs the network, so it is done once per session; Reload list refreshes it.
        if (!ViewModel.HasLoaded)
        {
            await ViewModel.RefreshCommand.ExecuteAsync(null);
        }
    }

    private static StoreRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as StoreRow;

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            await ViewModel.InstallAsync(row);
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            await ViewModel.RemoveAsync(row);
        }
    }

    private void OpenSetup_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("setup");

    private void CreateDevice_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("devices", "create");
}
