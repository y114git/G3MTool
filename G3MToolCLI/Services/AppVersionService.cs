namespace G3MToolCLI.Services;

internal static class AppVersionService
{
    public static string Version { get; } =
        typeof(AppVersionService).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public static string GetBannerText() => $"G3MTool ({Version})";
}
