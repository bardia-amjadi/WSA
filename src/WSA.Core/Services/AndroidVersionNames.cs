using System.Text.RegularExpressions;

namespace WSA.Core.Services;

/// <summary>Maps SDK platform names such as <c>android-34</c> to marketing names. Unknown names are shown as given.</summary>
public static class AndroidVersionNames
{
    private static readonly Regex ApiRegex = new(@"^android-(\d+)(?:-ext(\d+))?$", RegexOptions.Compiled);

    private static readonly Dictionary<int, string> Names = new()
    {
        [21] = "5.0", [22] = "5.1", [23] = "6.0", [24] = "7.0", [25] = "7.1",
        [26] = "8.0", [27] = "8.1", [28] = "9", [29] = "10", [30] = "11",
        [31] = "12", [32] = "12L", [33] = "13", [34] = "14", [35] = "15", [36] = "16",
    };

    public static string Describe(string apiName)
    {
        var m = ApiRegex.Match(apiName);
        if (!m.Success)
        {
            return apiName;
        }

        var api = int.Parse(m.Groups[1].Value);
        var ext = m.Groups[2].Success ? $", extension {m.Groups[2].Value}" : string.Empty;
        return Names.TryGetValue(api, out var name)
            ? $"Android {name} (API {api}{ext})"
            : $"Android API {api}{ext}";
    }

    public static string VariantName(string tag) => tag switch
    {
        "google_apis_playstore" => "Google Play",
        "google_apis" => "Google APIs",
        "default" => "AOSP (no Google apps)",
        _ => tag,
    };
}
