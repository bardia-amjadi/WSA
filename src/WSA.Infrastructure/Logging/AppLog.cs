using System.Text;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.Infrastructure.Logging;

/// <summary>
/// In-memory ring buffer plus a daily log file under %LOCALAPPDATA%\WSA\logs. Errors are always recorded;
/// Debug/Info entries only while Debug mode is on. Logging never throws into the caller.
/// </summary>
public sealed class AppLog : IAppLog
{
    private const int MaxEntries = 2000;
    private const int KeepDays = 7;

    private readonly ISettingsService _settings;
    private readonly object _lock = new();
    private readonly List<LogEntry> _entries = new();
    private bool _pruned;

    public AppLog(ISettingsService settings, string? directoryOverride = null)
    {
        _settings = settings;
        LogDirectory = directoryOverride
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WSA", "logs");
    }

    public bool IsDebugEnabled => _settings.Current.DebugMode;

    public string LogDirectory { get; }

    public event EventHandler<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_lock)
        {
            return _entries.ToArray();
        }
    }

    public void Debug(string category, string message)
    {
        if (IsDebugEnabled)
        {
            Write("DEBUG", category, message);
        }
    }

    public void Info(string category, string message)
    {
        if (IsDebugEnabled)
        {
            Write("INFO", category, message);
        }
    }

    public void Error(string category, string message, Exception? exception = null) =>
        Write("ERROR", category, exception is null ? message : $"{message} | {exception.GetType().Name}: {exception.Message}");

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    private void Write(string level, string category, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, category, message);
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
            }

            try
            {
                Directory.CreateDirectory(LogDirectory);
                if (!_pruned)
                {
                    _pruned = true;
                    Prune();
                }

                File.AppendAllText(
                    Path.Combine(LogDirectory, $"wsa-{entry.Time:yyyyMMdd}.log"),
                    entry.Display + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The in-memory log still works when the disk is unwritable.
            }
        }

        EntryAdded?.Invoke(this, entry);
    }

    private void Prune()
    {
        foreach (var file in Directory.EnumerateFiles(LogDirectory, "wsa-*.log"))
        {
            if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-KeepDays))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Leave it; it will be pruned next time.
                }
            }
        }
    }
}
