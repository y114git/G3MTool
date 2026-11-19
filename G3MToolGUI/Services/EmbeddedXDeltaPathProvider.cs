using System.Reflection;
using System.Runtime.InteropServices;
using G3MLib.Modding.Logging;
using G3MLib.Modding.XDelta;

namespace G3MToolGUI.Services;

internal static class EmbeddedXDeltaPathProvider
{
    private static readonly Lazy<string> ExecutablePath = new(Extract);

    /// <summary>Extracts the bundled xdelta on first use. Throws <see cref="InvalidOperationException"/> when this build bundles none for the platform.</summary>
    public static string GetPath() => ExecutablePath.Value;

    /// <summary>The xdelta to run: the library default when one is set, else the bundled copy.</summary>
    public static string Resolve() => XDeltaService.DefaultExecutablePath is { Length: > 0 } configured && File.Exists(configured) ? configured : GetPath();

    /// <summary>Points the library at the bundled xdelta for operations that may use it. A build without one only fails the operations that need it.</summary>
    public static void TryConfigure()
    {
        if (XDeltaService.DefaultExecutablePath is { Length: > 0 }) return;
        try { XDeltaService.DefaultExecutablePath = GetPath(); }
        catch (InvalidOperationException exception) { LogService.Warning(exception.Message); }
    }

    private static string Extract()
    {
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "mac_arm64" : "mac_x64") : "linux";
        var fileName = OperatingSystem.IsWindows() ? "xdelta.exe" : "xdelta";
        var resourceName = "G3MToolCLI.Assets.bin." + platform + "." + fileName;
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"No xdelta is bundled for {RuntimeInformation.RuntimeIdentifier}. Use the Run a program operation with your own xdelta.");

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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temporary folder.
        }
    }
}
