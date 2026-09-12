using System.Reflection;
using System.Runtime.InteropServices;

namespace G3MToolCLI.Services.Execution;

internal static class EmbeddedXDeltaPathProvider
{
    private static readonly Lazy<string> ExecutablePath = new(Extract);

    public static string GetPath() => ExecutablePath.Value;

    private static string Extract()
    {
        string platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "mac_arm64" : "mac_x64") : "linux";
        string fileName = OperatingSystem.IsWindows() ? "xdelta.exe" : "xdelta";
        string resourceName = "G3MToolCLI.Assets.bin." + platform + "." + fileName;
        using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException("Bundled xdelta executable is unavailable for this platform.");

        string directory = Directory.CreateTempSubdirectory("g3mtool-xdelta-").FullName;
        string path = Path.Combine(directory, fileName);
        using (FileStream file = File.Create(path)) resource.CopyTo(file);
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
