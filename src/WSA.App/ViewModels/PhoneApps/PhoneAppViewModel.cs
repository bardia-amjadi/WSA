using WSA.Core.Models;

namespace WSA.App.ViewModels;

/// <summary>Common base for the apps on the phone desktop: knows which phone it talks to and how to show dialogs.</summary>
public abstract class PhoneAppViewModel : OperationViewModelBase
{
    public string Serial { get; private set; } = string.Empty;

    /// <summary>Set by the app's view: yes/no dialog (title, message, button text).</summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Set by the view: asks for a line of text (title, initial value). Null when cancelled.</summary>
    public Func<string, string, Task<string?>>? PromptAsync { get; set; }

    /// <summary>Set by the view: folder picker on this computer.</summary>
    public Func<Task<string?>>? PickFolderAsync { get; set; }

    /// <summary>Set by the view: file picker on this computer (optional extension filter such as ".apk").</summary>
    public Func<string?, Task<IReadOnlyList<string>>>? PickFilesAsync { get; set; }

    public virtual void Initialize(string serial) => Serial = serial;

    protected async Task<bool> AskAsync(string title, string message, string button) =>
        ConfirmAsync is { } ask && await ask(title, message, button);

    /// <summary>Turns adb output lines into progress messages for the busy panel.</summary>
    protected static IProgress<OutputLine> Adapt(IProgress<SetupProgress> progress) => new LineProgress(progress);

    private sealed class LineProgress : IProgress<OutputLine>
    {
        private readonly IProgress<SetupProgress> _inner;

        public LineProgress(IProgress<SetupProgress> inner) => _inner = inner;

        public void Report(OutputLine value) => _inner.Report(new SetupProgress(value.Text));
    }
}
