namespace WSA.Core.Models;

/// <summary>User configuration persisted per user (see <c>SettingsService</c>).</summary>
public sealed class AppSettings
{
    /// <summary>Explicit Android SDK root. When empty, ANDROID_SDK_ROOT, ANDROID_HOME and default install locations are used.</summary>
    public string? SdkRootOverride { get; set; }

    /// <summary>Command timeout (seconds) for short, non-streaming tool invocations.</summary>
    public int ShortCommandTimeoutSeconds { get; set; } = 60;

    /// <summary>Maximum number of lines kept in the WSA Terminal output buffer.</summary>
    public int TerminalMaxLines { get; set; } = 5000;

    /// <summary>Command history persisted across restarts.</summary>
    public List<string> TerminalHistory { get; set; } = new();
}
