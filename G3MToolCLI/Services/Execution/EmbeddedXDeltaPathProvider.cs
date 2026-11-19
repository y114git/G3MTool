using System.Reflection;
using System.Runtime.InteropServices;
using G3MToolCLI.Services.Logging;

namespace G3MToolCLI.Services.Execution;

internal static class EmbeddedXDeltaPathProvider
{
    private static readonly Lazy<string> ExecutablePath = new(Extract);

    /// <summary>Extracts the bundled xdelta on first use. Throws <see cref="InvalidOperationException"/> when this build bundles none for the platform.</summary>
    public static string GetPath() => ExecutablePath.Value;

    /// <summary>The xdelta to run: --xdelta-path or the bundled binary. Throws <see cref="InvalidOperationException"/> with a clear message when neither exists.</summary>
    public static string Resolve() => Program.XDeltaPathOverride ?? GetPath();

    /// <summary>
    /// Points the library at xdelta for commands that may read xdelta input, without failing when none is bundled:
    /// the library then reports a missing xdelta only for the input that needs it.
    /// </summary>
    public static void TryConfigure()
    {
        try { XDeltaService.DefaultExecutablePath = Resolve(); }
        catch (InvalidOperationException exception) { LogService.Warning(exception.Message); }
    }

    private static string Extract()
    {
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "mac_arm64" : "mac_x64") : "linux";
        var fileName = OperatingSystem.IsWindows() ? "xdelta.exe" : "xdelta";
        var resourceName = "G3MToolCLI.Assets.bin." + platform + "." + fileName;
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"No xdelta is bundled for {RuntimeInformation.RuntimeIdentifier}. Install xdelta3 and pass --xdelta-path <path>.");

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
