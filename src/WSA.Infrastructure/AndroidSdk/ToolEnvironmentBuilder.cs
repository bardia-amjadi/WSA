using System.Runtime.Versioning;
using Microsoft.Win32;
using WSA.Core.Models;
using WSA.Infrastructure.Configuration;

namespace WSA.Infrastructure.AndroidSdk;

/// <summary>
/// Decides which Java the SDK tools get and builds the environment overrides for them.
///
/// Child processes always inherit WSA's own environment. That alone is not enough on Windows: a program only sees the
/// variables that existed when it was started, so a JAVA_HOME or PATH change made afterwards is invisible until WSA is
/// restarted (or the user signs out). To avoid a stale view, the persisted Windows environment (registry) is consulted too.
///
/// JAVA_HOME precedence: 1) the JDK folder configured in WSA Settings, 2) a JDK installed by WSA, 3) JAVA_HOME of the WSA
/// process, 4) JAVA_HOME from the Windows user environment, 5) from the Windows system environment, 6) first java.exe on PATH.
/// An invalid Settings value is reported, never silently replaced.
/// </summary>
internal static class ToolEnvironmentBuilder
{
    private static string JavaFile => OperatingSystem.IsWindows() ? "java.exe" : "java";

    public static (IReadOnlyDictionary<string, string> Environment, JavaResolution Java) Build(string? sdkRoot, AppSettings settings)
    {
        var notes = new List<string>();
        var procHome = Clean(System.Environment.GetEnvironmentVariable("JAVA_HOME"), notes, "JAVA_HOME in WSA's process");
        string? userHome = null, machineHome = null, userPath = null, machinePath = null;

        if (OperatingSystem.IsWindows())
        {
            // Registry values may be REG_EXPAND_SZ (for example %ProgramFiles%\Java\jdk), so expand them.
            userHome = Clean(Expand(ReadUser("JAVA_HOME")), notes, "JAVA_HOME in the Windows user environment");
            machineHome = Clean(Expand(ReadMachine("JAVA_HOME")), notes, "JAVA_HOME in the Windows system environment");
            userPath = ReadUser("Path");
            machinePath = ReadMachine("Path");
        }

        notes.Insert(0, $"JAVA_HOME seen by WSA's process: {procHome ?? "(not set)"}");
        if (OperatingSystem.IsWindows())
        {
            var persisted = userHome ?? machineHome;
            notes.Insert(1, $"JAVA_HOME persisted in Windows: {persisted ?? "(not set)"}");
            if (!string.Equals(procHome, persisted, StringComparison.OrdinalIgnoreCase) && persisted is not null)
            {
                notes.Add("WSA's process has a different JAVA_HOME than Windows has stored (WSA was probably started before it was set or changed). " +
                          "WSA falls back to the stored value; restarting WSA also fixes this.");
            }
        }

        string? home = null;
        string source = "none";
        string? problem = null;

        var configured = Normalize(settings.JavaHomeOverride);
        if (!string.IsNullOrEmpty(configured))
        {
            if (HasJava(configured))
            {
                home = configured;
                source = "WSA Settings";
            }
            else
            {
                source = "WSA Settings (invalid)";
                problem = $"The JDK folder configured in Settings has no bin\\{JavaFile}: {configured}";
            }
        }
        else if (File.Exists(ManagedPaths.JavaExe))
        {
            home = ManagedPaths.JdkDir;
            source = "JDK installed by WSA";
        }
        else
        {
            foreach (var (candidate, name) in new[]
                     {
                         (procHome, "JAVA_HOME environment variable (WSA process)"),
                         (userHome, "JAVA_HOME in Windows user environment"),
                         (machineHome, "JAVA_HOME in Windows system environment"),
                     })
            {
                if (candidate is null)
                {
                    continue;
                }

                if (HasJava(candidate))
                {
                    home = candidate;
                    source = name;
                    break;
                }

                notes.Add($"{name} points to '{candidate}', which has no bin\\{JavaFile}; ignored.");
            }
        }

        var path = MergePath(System.Environment.GetEnvironmentVariable("PATH"), userPath, machinePath);
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(sdkRoot))
        {
            env["ANDROID_SDK_ROOT"] = sdkRoot;
            env["ANDROID_HOME"] = sdkRoot;
        }

        string? javaExe = null;
        if (home is not null)
        {
            // The launcher scripts prefer JAVA_HOME, but putting its bin first on PATH makes any fallback to a bare
            // "java" resolve to the same JDK.
            env["JAVA_HOME"] = home;
            path = Path.Combine(home, "bin") + Path.PathSeparator + path;
            javaExe = Path.Combine(home, "bin", JavaFile);
        }
        else if (problem is null)
        {
            javaExe = FindOnPath(path);
            source = javaExe is null ? "none" : "first java on PATH";
        }

        env["PATH"] = path;
        return (env, new JavaResolution(home, source, javaExe, problem, notes));
    }

    private static string? Expand(string? value) =>
        value is null ? null : System.Environment.ExpandEnvironmentVariables(value);

    private static bool HasJava(string home) => File.Exists(Path.Combine(home, "bin", JavaFile));

    /// <summary>Removes surrounding quotes and trailing separators; a quoted JAVA_HOME is a classic cause of launcher failures.</summary>
    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = value.Trim().Trim('"').Trim();
        cleaned = cleaned.TrimEnd('\\', '/');
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? Clean(string? raw, List<string> notes, string label)
    {
        var cleaned = Normalize(raw);
        if (raw is not null && cleaned is not null && !string.Equals(raw, cleaned, StringComparison.Ordinal))
        {
            notes.Add($"{label} had quotes or a trailing slash ('{raw}'); WSA uses '{cleaned}'.");
        }

        return cleaned;
    }

    private static string MergePath(string? process, string? user, string? machine)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();

        void AddAll(string? list)
        {
            if (string.IsNullOrWhiteSpace(list))
            {
                return;
            }

            foreach (var raw in System.Environment.ExpandEnvironmentVariables(list).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var entry = raw.Trim().Trim('"');
                if (entry.Length > 0 && seen.Add(entry.TrimEnd('\\', '/')))
                {
                    parts.Add(entry);
                }
            }
        }

        // WSA's own PATH first (it is what the user's shell would have), then anything persisted in Windows that
        // WSA's process does not know about yet.
        AddAll(process);
        AddAll(machine);
        AddAll(user);
        return string.Join(Path.PathSeparator, parts);
    }

    private static string? FindOnPath(string path)
    {
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, JavaFile);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry; skip it.
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadUser(string name) => Read(Registry.CurrentUser, "Environment", name);

    [SupportedOSPlatform("windows")]
    private static string? ReadMachine(string name) =>
        Read(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", name);

    [SupportedOSPlatform("windows")]
    private static string? Read(RegistryKey root, string subKey, string name)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
