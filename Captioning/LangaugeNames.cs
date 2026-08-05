namespace CartoonArchive.Captioning;

public static class LanguageNames
{
    private static readonly Dictionary<string, string> _names = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["en"] = "English",
        ["tr"] = "Turkish",
        ["de"] = "German",
        ["fr"] = "French",
        ["es"] = "Spanish",
    };

    public static string Get(string bcp47) =>
        _names.TryGetValue(bcp47, out var name) ? name : bcp47;
}
