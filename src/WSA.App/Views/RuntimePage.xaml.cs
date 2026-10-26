using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WSA.App.ViewModels;

namespace WSA.App.Views;

public sealed partial class RuntimePage : Page
{
    public RuntimePage()
    {
        ViewModel = App.GetService<RuntimeViewModel>();
        InitializeComponent();
    }

    public RuntimeViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshCommand.ExecuteAsync(null);
}
