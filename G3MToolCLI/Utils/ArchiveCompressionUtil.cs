using System.IO.Compression;

namespace G3MToolCLI.Utils;

internal static class ArchiveCompressionUtil
{
    private static readonly HashSet<string> s_uncompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".gif",
        ".dds",
        ".qoi",
        ".bz2",
        ".mp3",
        ".ogg",
        ".wav",
        ".flac",
        ".bin",
        ".dat",
        ".xdelta"
    };

    public static CompressionLevel GetLevel(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
            return CompressionLevel.Optimal;

        string extension = Path.GetExtension(entryPath);
        if (s_uncompressedExtensions.Contains(extension))
            return CompressionLevel.NoCompression;

        return CompressionLevel.Optimal;
    }
}
