namespace WSA.Core.Models;

/// <summary>Values stored in <see cref="AppSettings.SetupGoal"/>.</summary>
public static class SetupGoals
{
    public const string Phone = "phone";
    public const string Everything = "everything";
}

/// <summary>User configuration persisted per user (see <c>SettingsService</c>).</summary>
public sealed class AppSettings
{
    /// <summary>Explicit Android SDK root. When empty, ANDROID_SDK_ROOT, ANDROID_HOME and default install locations are used.</summary>
    public string? SdkRootOverride { get; set; }

    /// <summary>Explicit JDK folder (contains bin\java.exe) used as JAVA_HOME for sdkmanager/avdmanager. Empty = WSA-managed JDK or the system one.</summary>
    public string? JavaHomeOverride { get; set; }

    /// <summary>What the user chose in the setup wizard: <see cref="SetupGoals.Phone"/>, <see cref="SetupGoals.Everything"/>, or null before the first choice.</summary>
    public string? SetupGoal { get; set; }

    /// <summary>True once the user finished the setup wizard at least once.</summary>
    public bool WizardCompleted { get; set; }

    /// <summary>When on, WSA records every command, its output and setup steps to a log file and shows the Debug page.</summary>
    public bool DebugMode { get; set; }

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Phone Live View: "stream" (H.264 video, 15-60 fps) or "screenshots" (compatible, a few fps).</summary>
    public string LiveViewMode { get; set; } = "stream";

    /// <summary>Phone Live View target frame rate (15, 30 or 60).</summary>
    public int LiveViewFps { get; set; } = 30;

    /// <summary>Longest side of the streamed video in pixels (720, 1024, 1280 or 1600).</summary>
    public int LiveViewMaxSize { get; set; } = 1280;

    /// <summary>Video bit rate in megabits per second.</summary>
    public int LiveViewBitrateMbps { get; set; } = 8;

    /// <summary>Command timeout (seconds) for short, non-streaming tool invocations.</summary>
    public int ShortCommandTimeoutSeconds { get; set; } = 60;

    /// <summary>Maximum number of lines kept in the WSA Terminal output buffer.</summary>
    public int TerminalMaxLines { get; set; } = 5000;

    /// <summary>Command history persisted across restarts.</summary>
    public List<string> TerminalHistory { get; set; } = new();
}
