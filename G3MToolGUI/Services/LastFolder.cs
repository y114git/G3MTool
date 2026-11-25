namespace G3MToolGUI.Services;

/// <summary>The folder the last file dialog ended in, kept in <c>last-folder.txt</c> in the app data folder so the next dialog opens there, also after a restart.</summary>
public static class LastFolder
{
    /// <summary>Where the folder is kept; tests point it at a temp folder.</summary>
    public static string FilePath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "G3MTool", "last-folder.txt");

    /// <summary>The remembered folder, or null when none was kept or it is gone.</summary>
    public static string? Get()
    {
        try
        {
            return File.ReadAllText(FilePath).Trim() is { Length: > 0 } folder && Directory.Exists(folder) ? folder : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Remembers the folder of a picked file, or the picked folder itself. A path that is not local (null) or not on disk changes nothing.</summary>
    public static void Remember(string? picked)
    {
        if (string.IsNullOrWhiteSpace(picked)) return;
        var folder = Directory.Exists(picked) ? picked : Path.GetDirectoryName(picked);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder) || folder == Get()) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, folder);
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be kept only means the next dialog opens in its default place.
        }
    }
}
