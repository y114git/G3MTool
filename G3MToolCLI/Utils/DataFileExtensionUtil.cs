namespace G3MToolCLI.Utils;

/// <summary>
/// Utility class for handling GameMaker data file extensions across platforms.
/// </summary>
public static class DataFileExtensionUtil
{
    /// <summary>
    /// Valid GameMaker data file extensions for all platforms.
    /// </summary>
    public static readonly string[] ValidExtensions = { ".win", ".ios", ".droid", ".unx" };

    /// <summary>
    /// Checks if the given extension is a valid GameMaker data file extension.
    /// </summary>
    public static bool IsValidDataExtension(string extension)
    {
        return ValidExtensions.Contains(extension.ToLowerInvariant());
    }

    /// <summary>
    /// Checks if the file path has a valid GameMaker data file extension.
    /// </summary>
    public static bool IsDataFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return IsValidDataExtension(ext);
    }

    /// <summary>
    /// Gets the appropriate output extension based on input file or defaults to .win
    /// </summary>
    public static string GetOutputExtension(string? inputPath)
    {
        if (string.IsNullOrEmpty(inputPath))
            return ".win";

        var ext = Path.GetExtension(inputPath).ToLowerInvariant();
        return IsValidDataExtension(ext) ? ext : ".win";
    }

    /// <summary>
    /// Returns a display string of all valid extensions.
    /// </summary>
    public static string GetValidExtensionsDisplay()
    {
        return string.Join(", ", ValidExtensions.Select(e => $"`{e}`"));
    }
}
