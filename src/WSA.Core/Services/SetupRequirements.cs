using WSA.Core.Models;

namespace WSA.Core.Services;

/// <summary>Decides what "set up" means for the goal the user picked, so each route installs only what it needs.</summary>
public static class SetupRequirements
{
    public static bool WantsVirtualDevices(AppSettings settings) =>
        settings.SetupGoal != SetupGoals.Phone;

    public static bool IsSatisfied(AppSettings settings, SdkStatus status) =>
        settings.SetupGoal == SetupGoals.Phone
            ? status.Get(SdkTool.Adb).IsAvailable
            : status.IsComplete;
}
