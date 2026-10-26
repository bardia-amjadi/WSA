using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WSA.App.ViewModels;

namespace WSA.App.Views;

public sealed partial class TerminalPage : Page
{
    public TerminalPage()
    {
        ViewModel = App.GetService<TerminalViewModel>();
        InitializeComponent();
    }

    public TerminalViewModel ViewModel { get; }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.LinesAdded += OnLinesAdded;
        ScrollToEnd();
        InputBox.Focus(FocusState.Programmatic);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) => ViewModel.LinesAdded -= OnLinesAdded;

    private void OnLinesAdded(object? sender, EventArgs e) => ScrollToEnd();

    private void ScrollToEnd()
    {
        if (ViewModel.Lines.Count > 0)
        {
            OutputList.ScrollIntoView(ViewModel.Lines[^1]);
        }
    }

    private void InputBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                _ = ViewModel.SubmitCommand.ExecuteAsync(null);
                break;

            case VirtualKey.Up:
                e.Handled = true;
                SetInput(ViewModel.HistoryUp());
                break;

            case VirtualKey.Down:
                e.Handled = true;
                SetInput(ViewModel.HistoryDown());
                break;

            case VirtualKey.Escape:
                e.Handled = true;
                ViewModel.CancelCommand.Execute(null);
                break;
        }
    }

    private void SetInput(string? value)
    {
        if (value is null)
        {
            return;
        }

        ViewModel.InputText = value;
        InputBox.SelectionStart = value.Length;
    }
}
