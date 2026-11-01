using System.Reflection;
using System.Runtime.InteropServices;

namespace G3MToolGUI.Services;

internal static class EmbeddedXDeltaPathProvider
{
    private static readonly Lazy<string> ExecutablePath = new(Extract);

    public static string GetPath() => ExecutablePath.Value;

    private static string Extract()
    {
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "mac_arm64" : "mac_x64") : "linux";
        var fileName = OperatingSystem.IsWindows() ? "xdelta.exe" : "xdelta";
        var resourceName = "G3MToolCLI.Assets.bin." + platform + "." + fileName;
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException("Bundled xdelta executable is unavailable for this platform.");

        var directory = Directory.CreateTempSubdirectory("g3mtool-xdelta-").FullName;
        var path = Path.Combine(directory, fileName);
        using (var file = File.Create(path)) resource.CopyTo(file);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteDirectory(directory);
        return path;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch { }
    }
}
