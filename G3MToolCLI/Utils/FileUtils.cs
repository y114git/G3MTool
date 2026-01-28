using System.Text;

namespace G3MToolCLI.Utils;

public static class FileUtils
{
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        }
        return sb.ToString();
    }

    public static void EnsureDirectoryExists(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public static void SafeDeleteDirectory(string path, int maxRetries = 3)
    {
        if (!Directory.Exists(path))
            return;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                Thread.Sleep(100 * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < maxRetries - 1)
            {
                // Try to reset attributes
                try
                {
                    foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                }
                catch { }
                Thread.Sleep(100 * (i + 1));
            }
        }

        // Final attempt with ignore errors
        try
        {
            Directory.Delete(path, true);
        }
        catch { }
    }

    public static string GetRelativePath(string basePath, string fullPath)
    {
        var baseUri = new Uri(basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        var fullUri = new Uri(fullPath);
        return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString().Replace('/', Path.DirectorySeparatorChar));
    }

    public static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
            return 0;

        return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
    }
}
