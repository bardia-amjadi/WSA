using WSA.Core.Models;

namespace WSA.Core.Interfaces;

/// <summary>
/// Application log. Errors are always recorded; Debug and Info detail (every command line, exit code and output)
/// is only recorded while Debug mode is on in Settings.
/// </summary>
public interface IAppLog
{
    bool IsDebugEnabled { get; }

    string LogDirectory { get; }

    /// <summary>Raised on whatever thread wrote the entry.</summary>
    event EventHandler<LogEntry>? EntryAdded;

    IReadOnlyList<LogEntry> Snapshot();

    void Debug(string category, string message);

    void Info(string category, string message);

    void Error(string category, string message, Exception? exception = null);

    void Clear();
}
