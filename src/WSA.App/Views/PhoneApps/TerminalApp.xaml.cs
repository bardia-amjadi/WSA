using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WSA.App.ViewModels;

namespace WSA.App.Views.PhoneApps;

public sealed partial class TerminalApp : UserControl
{
    private readonly string _serial;
    private bool _started;

    public TerminalApp(string serial)
    {
        _serial = serial;
        ViewModel = App.GetService<TerminalAppViewModel>();
        InitializeComponent();
    }

    public TerminalAppViewModel ViewModel { get; }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        AppHooks.Wire(ViewModel, this);
        ViewModel.Initialize(_serial);
        ViewModel.Lines.CollectionChanged += OnLinesChanged;
        InputBox.Focus(FocusState.Programmatic);
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.Lines.Count > 0)
        {
            Output.ScrollIntoView(ViewModel.Lines[^1]);
        }
    }

    private void Input_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                ViewModel.ExecuteCommand.Execute(null);
                break;
            case Windows.System.VirtualKey.Up:
                e.Handled = true;
                ViewModel.HistoryPrevious();
                break;
            case Windows.System.VirtualKey.Down:
                e.Handled = true;
                ViewModel.HistoryNext();
                break;
        }
    }
}
