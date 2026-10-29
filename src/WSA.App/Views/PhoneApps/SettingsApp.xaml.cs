using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WSA.App.ViewModels;

namespace WSA.App.Views.PhoneApps;

public sealed partial class SettingsApp : UserControl
{
    private readonly string _serial;
    private bool _started;

    public SettingsApp(string serial)
    {
        _serial = serial;
        ViewModel = App.GetService<PhoneSettingsViewModel>();
        InitializeComponent();
    }

    public PhoneSettingsViewModel ViewModel { get; }

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
