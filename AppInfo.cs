using System.Reflection;

namespace CartoonArchive;

public static class AppInfo
{
    // Read once from the assembly's baked-in InformationalVersion (comes from <Version>
    // in the csproj). Strip the trailing +hash the SDK adds so the UI shows a clean semver.
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var info = Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
