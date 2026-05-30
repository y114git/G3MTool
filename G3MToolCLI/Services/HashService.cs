using System.Security.Cryptography;

namespace G3MToolCLI.Services;

public class HashService
{
    private const int FileBufferSize = 1024 * 1024;

    public static async Task<string> ComputeFileHashAsync(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hashBytes = await MD5.HashDataAsync(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public static string ComputeFileHash(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileBufferSize, FileOptions.SequentialScan);
        var hashBytes = MD5.HashData(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public static bool FileSizesMatch(string file1, string file2)
        => new FileInfo(file1).Length == new FileInfo(file2).Length;
}
