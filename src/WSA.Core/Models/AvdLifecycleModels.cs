namespace WSA.Core.Models;

/// <summary>An installed system image found under &lt;SDK&gt;\system-images.</summary>
public sealed record SystemImageInfo(string Package, string ApiLevel, string Tag, string Abi)
{
    public string Display => $"{ApiLevel}  |  {Tag}  |  {Abi}";
}

/// <summary>A hardware profile id reported by <c>avdmanager list device -c</c>.</summary>
public sealed record DeviceProfile(string Id);

public sealed record CreateAvdRequest(string Name, string SystemImagePackage, string DeviceId);

/// <summary>Outcome of an AVD lifecycle operation. <see cref="Message"/> is user-facing and comes from real tool output where possible.</summary>
public sealed record OperationResult(bool Success, string Message);
