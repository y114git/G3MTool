using System.Reflection;
using System.Runtime.InteropServices;

namespace G3MToolCLI.Utils;

public static class PlatformUtil
{
    public sealed class XDeltaPathInfo
    {
        public required string Path { get; init; }
        public bool IsTemporary { get; init; }
        public string? TempDirectory { get; init; }
    }

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

    public static XDeltaPathInfo? GetXDeltaPath(string? overridePath = null)
    {
        var platform = GetPlatformName();
        var exeName = platform == "win" ? "xdelta.exe" : "xdelta";

        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            SetExecutablePermission(overridePath, platform);
            return new XDeltaPathInfo
            {
                Path = overridePath,
                IsTemporary = false
            };
        }

        var extracted = ExtractXDeltaFromResource(platform, exeName);
        if (extracted != null)
            return extracted;

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
                return new XDeltaPathInfo
                {
                    Path = path,
                    IsTemporary = false
                };
            }
        }

        return null;
    }

    private static XDeltaPathInfo? ExtractXDeltaFromResource(string platform, string exeName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"G3MToolCLI.Assets.bin.{platform}.{exeName}";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return null;

        var tempDir = Path.Combine(Path.GetTempPath(), $"g3mtool_xdelta_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var outputPath = Path.Combine(tempDir, exeName);
        using (var fileStream = File.Create(outputPath))
            stream.CopyTo(fileStream);

        SetExecutablePermission(outputPath, platform);
        return new XDeltaPathInfo
        {
            Path = outputPath,
            IsTemporary = true,
            TempDirectory = tempDir
        };
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

    public static string GetExecutableDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var dir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(dir))
                return dir;
        }

        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        if (!string.IsNullOrEmpty(assemblyLocation))
        {
            var dir = Path.GetDirectoryName(assemblyLocation);
            if (!string.IsNullOrEmpty(dir))
                return dir;
        }

        return AppContext.BaseDirectory;
    }
}
