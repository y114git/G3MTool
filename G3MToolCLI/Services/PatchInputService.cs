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
            File.Copy(inputPath, output, true);
        else if (IsXDelta(inputPath))
        {
            var result = await new XDeltaService().ApplyPatchAsync(originalPath, inputPath, output);
            if (!result.Success) throw new InvalidOperationException($"Failed to apply xdelta '{Path.GetFileName(inputPath)}': {result.Error}");
        }
        else if (ext == ".csx")
        {
            File.Copy(originalPath, output, true);
            var result = await ExecuteService.ExecuteScriptAsync(inputPath, output, output, []);
            if (!result.Success) throw new InvalidOperationException($"CSX execution failed for '{Path.GetFileName(inputPath)}': {result.Error}");
        }
        else if (ext is ".g3mpatch" or ".zip")
        {
            var result = await PatchService.ApplyPatchAsync(originalPath, inputPath, output);
            if (!result.Success) throw new InvalidOperationException($"Failed to apply patch '{Path.GetFileName(inputPath)}': {result.Error}");
        }
        else
            throw new NotSupportedException($"Unsupported patch input '{Path.GetFileName(inputPath)}' ({ext}). Supported: .g3mpatch, .xdelta, .vcdiff, .csx and GameMaker data files.");

        try
        {
            using var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var data = UndertaleIO.Read(stream);
        }
        catch (Exception ex)
        {
            try { File.Delete(output); } catch { }
            throw new InvalidDataException($"Validation failed for materialized input '{Path.GetFileName(inputPath)}': {ex.Message}", ex);
        }
        return output;
    }
}
