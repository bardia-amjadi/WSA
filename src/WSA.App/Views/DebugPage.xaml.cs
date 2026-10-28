using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WSA.App.ViewModels;

namespace WSA.App.Views;

public sealed partial class DebugPage : Page
{
    public DebugPage()
    {
        ViewModel = App.GetService<DebugViewModel>();
        InitializeComponent();
    }

    public DebugViewModel ViewModel { get; }

    private void Page_Loaded(object sender, RoutedEventArgs e) => ViewModel.Reload();
}
