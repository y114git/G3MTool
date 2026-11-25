namespace G3MToolGUI.Services;

/// <summary>The g3mstudio:// link that opens a data file in G3MStudio.</summary>
public static class StudioLink
{
    public const string DownloadUrl = "https://github.com/y114git/G3MStudio";

    private static readonly string[] DataExtensions = [".win", ".ios", ".droid", ".unx"];

    public static bool IsDataFile(string? path) => !string.IsNullOrWhiteSpace(path)
        && DataExtensions.Contains(Path.GetExtension(path.Trim()), StringComparer.OrdinalIgnoreCase)
        && File.Exists(path.Trim());

    /// <summary>The link G3MStudio reads: the scheme followed by the file address.</summary>
    public static string ForFile(string path) => "g3mstudio://" + new Uri(Path.GetFullPath(path.Trim())).AbsoluteUri;
}
