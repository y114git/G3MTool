using UndertaleModLib;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Services;

public static class PatchInputService
{
    public static bool IsXDelta(string path) =>
        Path.GetExtension(path).Equals(".xdelta", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".vcdiff", StringComparison.OrdinalIgnoreCase);

    public static async Task<string> MaterializeDataAsync(string originalPath, string inputPath, string tempDir)
    {
        if (!File.Exists(originalPath)) throw new FileNotFoundException("Original data file not found", originalPath);
        if (!File.Exists(inputPath)) throw new FileNotFoundException("Patch input not found", inputPath);
        Directory.CreateDirectory(tempDir);
        var ext = Path.GetExtension(inputPath).ToLowerInvariant();
        var output = Path.Combine(tempDir, $"materialized_{Guid.NewGuid():N}{Path.GetExtension(originalPath)}");

        if (DataFileExtensionUtil.IsDataFile(inputPath))
        {
            ValidateDataFile(inputPath, inputPath);
            return inputPath;
        }
        else if (IsXDelta(inputPath))
        {
            var result = await new XDeltaService().ApplyPatchAsync(originalPath, inputPath, output);
            if (!result.Success) throw new InvalidOperationException($"Failed to apply xdelta '{Path.GetFileName(inputPath)}': {result.Error}");
            CopyExternalAudioGroups(originalPath, output);
        }
        else if (ext == ".csx")
        {
            File.Copy(originalPath, output, true);
            var result = await ExecuteService.ExecuteScriptAsync(inputPath, output, output, []);
            if (!result.Success) throw new InvalidOperationException($"CSX execution failed for '{Path.GetFileName(inputPath)}': {result.Error}");
            CopyExternalAudioGroups(originalPath, output);
        }
        else if (ext is ".g3mpatch" or ".zip")
        {
            var result = await PatchService.ApplyPatchAsync(originalPath, inputPath, output);
            if (!result.Success) throw new InvalidOperationException($"Failed to apply patch '{Path.GetFileName(inputPath)}': {result.Error}");
            CopyExternalAudioGroups(originalPath, output);
        }
        else
            throw new NotSupportedException($"Unsupported patch input '{Path.GetFileName(inputPath)}' ({ext}). Supported: .g3mpatch, .xdelta, .vcdiff, .csx and GameMaker data files.");

        ValidateDataFile(output, inputPath);
        return output;
    }

    private static void ValidateDataFile(string dataPath, string inputPath)
    {
        try
        {
            using var stream = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var data = UndertaleIO.Read(stream);
        }
        catch (Exception ex)
        {
            if (!string.Equals(dataPath, inputPath, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(dataPath); } catch { }
            throw new InvalidDataException($"Validation failed for materialized input '{Path.GetFileName(inputPath)}': {ex.Message}", ex);
        }
    }

    internal static void CopyExternalAudioGroups(string sourceDataPath, string materializedDataPath)
    {
        var sourceDir = Path.GetDirectoryName(sourceDataPath);
        var materializedDir = Path.GetDirectoryName(materializedDataPath);
        if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(materializedDir))
            return;

        try
        {
            using var stream = new FileStream(materializedDataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var data = UndertaleIO.Read(stream);
            var relativePaths = data.AudioGroups?
                .Select((group, index) => (Group: group, Index: index))
                .Where(item => item.Index > 0)
                .Select(item => string.IsNullOrWhiteSpace(item.Group?.Path?.Content)
                    ? $"audiogroup{item.Index}.dat"
                    : item.Group.Path.Content)
                .Distinct(StringComparer.OrdinalIgnoreCase) ?? [];

            foreach (var relativePath in relativePaths)
            {
                var sourcePath = GetSafeAudioGroupPath(sourceDir, relativePath);
                var destinationPath = GetSafeAudioGroupPath(materializedDir, relativePath);
                if (sourcePath == null || destinationPath == null || !File.Exists(sourcePath))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }
        }
        catch
        {
            // External audio is optional; the materialized data file remains usable without it.
        }
    }

    private static string? GetSafeAudioGroupPath(string baseDirectory, string relativePath)
    {
        try
        {
            var root = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var path = Path.GetFullPath(Path.Combine(root, relativePath));
            return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }
        catch
        {
            return null;
        }
    }
}
