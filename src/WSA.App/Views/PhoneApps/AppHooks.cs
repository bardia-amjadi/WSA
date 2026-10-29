using Microsoft.UI.Xaml;
using WSA.App.ViewModels;

namespace WSA.App.Views.PhoneApps;

/// <summary>Connects an app's view-model to the dialogs and pickers of the window it is shown in.</summary>
internal static class AppHooks
{
    public static void Wire(PhoneAppViewModel vm, FrameworkElement view)
    {
        vm.ConfirmAsync = (title, message, button) => Dialogs.ConfirmAsync(view.XamlRoot, title, message, button);
        vm.PromptAsync = (title, initial) => Dialogs.PromptAsync(view.XamlRoot, title, initial);
        vm.PickFolderAsync = () => Dialogs.PickFolderAsync();
        vm.PickFilesAsync = extension => Dialogs.PickFilesAsync(extension);
    }
}
