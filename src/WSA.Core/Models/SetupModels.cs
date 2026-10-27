namespace WSA.Core.Models;

/// <summary>Progress report from a setup operation. <paramref name="NewStep"/> resets the progress bar to indeterminate.</summary>
public sealed record SetupProgress(string Message, double? Percent = null, bool NewStep = false);

/// <summary>What is installed, derived from real detection results.</summary>
public sealed record SetupState(
    string SdkRoot,
    bool JdkReady,
    string JdkDetail,
    bool CmdlineToolsReady,
    bool PlatformToolsReady,
    bool EmulatorReady,
    string? AccelerationNote)
{
    public bool IsComplete => JdkReady && CmdlineToolsReady && PlatformToolsReady && EmulatorReady;
}

/// <summary>An Android system image offered by Google's SDK catalog (from <c>sdkmanager --list</c>).</summary>
public sealed record StoreImage(
    string Package,
    string ApiName,
    string Title,
    string Variant,
    string Abi,
    string Description,
    bool Installed);

/// <summary>License text as printed by sdkmanager. <see cref="AllAccepted"/> means nothing needs review.</summary>
public sealed record LicensePreview(bool AllAccepted, string Text);

/// <summary>One row of <c>sdkmanager --list</c>.</summary>
public sealed record SdkListEntry(string Path, string Version, string Description, bool Installed);
