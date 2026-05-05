using System.Reflection;

namespace G3MToolCLI.Services;

internal static class AppVersionService
{
    public static string Version { get; } =
        typeof(AppVersionService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "unknown";

    public static string GetBannerText() => $"G3MTool ({Version})";
}
