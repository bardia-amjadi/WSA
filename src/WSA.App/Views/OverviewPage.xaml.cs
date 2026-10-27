using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WSA.App.ViewModels;

namespace WSA.App.Views;

public sealed partial class OverviewPage : Page
{
    public OverviewPage()
    {
        ViewModel = App.GetService<OverviewViewModel>();
        InitializeComponent();
    }

    public OverviewViewModel ViewModel { get; }

    private void OpenSetup_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("setup");

    private async void Page_Loaded(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshCommand.ExecuteAsync(null);
}
