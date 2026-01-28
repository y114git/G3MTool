using System.Reflection;
using System.Runtime.InteropServices;

namespace G3MToolCLI.Utils;

public static class PlatformUtils
{
    private static string? _cachedXDeltaPath;

    public static string GetPlatformName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "win";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "mac";
        
        return "unknown";
    }

    public static string? GetXDeltaPath()
    {
        if (_cachedXDeltaPath != null && File.Exists(_cachedXDeltaPath))
            return _cachedXDeltaPath;

        var platform = GetPlatformName();
        var exeName = platform == "win" ? "xdelta.exe" : "xdelta";
        
        // First try to extract from embedded resource
        _cachedXDeltaPath = ExtractXDeltaFromResource(platform, exeName);
        if (_cachedXDeltaPath != null)
            return _cachedXDeltaPath;

        // Fallback: try file system locations (for development)
        var searchPaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "bin", platform, exeName),
            Path.Combine(Environment.CurrentDirectory, "Assets", "bin", platform, exeName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "bin", platform, exeName),
        };

        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
            {
                SetExecutablePermission(path, platform);
                _cachedXDeltaPath = path;
                return path;
            }
        }

        return null;
    }

    private static string? ExtractXDeltaFromResource(string platform, string exeName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"G3MToolCLI.Assets.bin.{platform}.{exeName}";
        
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return null;

        var tempDir = Path.Combine(Path.GetTempPath(), "G3MTool");
        Directory.CreateDirectory(tempDir);
        
        var outputPath = Path.Combine(tempDir, exeName);
        
        // Only extract if not exists or different size
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length != stream.Length)
        {
            using var fileStream = File.Create(outputPath);
            stream.CopyTo(fileStream);
        }

        SetExecutablePermission(outputPath, platform);
        return outputPath;
    }

    private static void SetExecutablePermission(string path, string platform)
    {
        if (platform != "win" && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
#pragma warning disable CA1416
                File.SetUnixFileMode(path, 
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
            }
            catch { }
        }
    }

    public static string GetExecutableExtension()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
    }

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
}
