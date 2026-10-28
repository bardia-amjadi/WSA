using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WSA.App.ViewModels;

namespace WSA.App.Views;

public sealed partial class SetupPage : Page
{
    public SetupPage()
    {
        ViewModel = App.GetService<SetupViewModel>();
        InitializeComponent();
    }

    public SetupViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ConfirmLicensesAsync = text => Dialogs.ConfirmLicensesAsync(XamlRoot, text);
        ViewModel.ConfirmAsync = (title, message, button) => Dialogs.ConfirmAsync(XamlRoot, title, message, button);
        ViewModel.PickFolderAsync = Dialogs.PickFolderAsync;
        await ViewModel.LoadAsync();
    }
}
