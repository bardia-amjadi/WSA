using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WSA.App.ViewModels;

namespace WSA.App.Views.PhoneApps;

public sealed partial class ToolsApp : UserControl
{
    private readonly string _serial;
    private bool _started;

    public ToolsApp(string serial)
    {
        _serial = serial;
        ViewModel = App.GetService<ToolsViewModel>();
        InitializeComponent();
    }

    public ToolsViewModel ViewModel { get; }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        AppHooks.Wire(ViewModel, this);
        ViewModel.Initialize(_serial);
    }
}
