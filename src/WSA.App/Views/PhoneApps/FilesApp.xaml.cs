using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WSA.App.ViewModels;
using WSA.Core.Models;

namespace WSA.App.Views.PhoneApps;

public sealed partial class FilesApp : UserControl
{
    public FilesApp(string serial)
    {
        ViewModel = App.GetService<FilesViewModel>();
        InitializeComponent();
        _serial = serial;
    }

    private readonly string _serial;
    private bool _started;

    public FilesViewModel ViewModel { get; }

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

    private async void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (List.SelectedItem is DeviceFile { IsDirectory: true } file)
        {
            await ViewModel.OpenAsync(file);
        }
    }

    private void Path_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            ViewModel.GoCommand.Execute(null);
        }
    }
}
