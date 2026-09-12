using System.Reflection;

namespace G3MToolCLI.Services.Application;

internal static class AppVersionService
{
    public static string Version { get; } = typeof(AppVersionService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static string GetBannerText()
    {
        return "G3MTool (CLI) - " + Version;
    }
}
