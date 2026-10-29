using System.Diagnostics;
using WSA.Core.Models;

namespace WSA.Infrastructure.Installer;

/// <summary>Decides whether this process is the app or the installer, and handles running maintenance from a temporary copy.</summary>
public static class InstallerBootstrap
{
    private const string RelaunchedFlag = "--relaunched";
    private const string TempPrefix = "WSA-setup-";

    public static IReadOnlyList<string> Args => Environment.GetCommandLineArgs().Skip(1).ToArray();

    /// <summary>Returns the installer mode for this start, or null when WSA should run normally.</summary>
    public static InstallerMode? DetermineMode(IReadOnlyList<string> args)
    {
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            return InstallerMode.Uninstall;
        }

        if (args.Contains("--maintenance", StringComparer.OrdinalIgnoreCase))
        {
            return InstallerMode.Maintenance;
        }

        if (InstallLayout.IsSetupPackage)
        {
            return InstallLayout.IsInstalled ? InstallerMode.Maintenance : InstallerMode.Install;
        }

        return null;
    }

    /// <summary>
    /// Maintenance started from the installed copy would try to delete or overwrite the files it is running from. So it first
    /// copies itself to a temporary folder and continues from there.
    /// </summary>
    public static bool NeedsRelaunch(IReadOnlyList<string> args) =>
        InstallLayout.RunsFromInstallDir && !args.Contains(RelaunchedFlag, StringComparer.OrdinalIgnoreCase);

    public static void RelaunchFromTemp(IReadOnlyList<string> args)
    {
        var temp = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N")[..8]);
        CopyDirectory(InstallLayout.BaseDir, temp);

        var psi = new ProcessStartInfo(Path.Combine(temp, InstallLayout.ExeName)) { UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.ArgumentList.Add(RelaunchedFlag);
        Process.Start(psi)?.Dispose();
    }

    /// <summary>Removes the temporary copy after this process exits. Only folders this class created are ever touched.</summary>
    public static void ScheduleTempCleanup()
    {
        var dir = InstallLayout.BaseDir;
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(dir);
        if (parent is null
            || !string.Equals(Path.GetFullPath(parent).TrimEnd('\\', '/'), temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(dir).StartsWith(TempPrefix, StringComparison.Ordinal)
            || dir.Contains('"'))
        {
            return;
        }

        try
        {
            // The folder name is generated above (prefix + hex), never user input; cmd.exe is used only to wait and delete.
            var psi = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c ping -n 4 127.0.0.1 >nul & rd /s /q \"{dir}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The temporary copy stays; Windows clears the temp folder eventually.
        }
    }

    /// <summary>True while another WSA process (the normal app) is running from the install folder.</summary>
    public static bool IsAppRunning()
    {
        var self = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("WSA"))
        {
            using (process)
            {
                if (process.Id == self)
                {
                    continue;
                }

                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null && path.StartsWith(InstallLayout.InstallDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Access denied for a process of another user; it is not ours.
                }
            }
        }

        return false;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith(InstallLayout.PackagesFolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
