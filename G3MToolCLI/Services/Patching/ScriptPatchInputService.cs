using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using G3MLib.DataFile;
using G3MLib.Modding.Patching;
using G3MToolCLI.Services.Execution;

namespace G3MToolCLI.Services.Patching;

public static class ScriptPatchInputService
{
    public static async Task<MergeResult> MergeAsync(string originalPath, List<string> inputs, MergeOptions options)
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"g3mtool_merge_{Guid.NewGuid():N}");
        try
        {
            List<string> materialized = [];
            foreach (string input in inputs)
                materialized.Add(Path.GetExtension(input).Equals(".csx", StringComparison.OrdinalIgnoreCase)
                    ? await MaterializeScriptAsync(originalPath, input, Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N")))
                    : input);
            return await MergeService.MergePatchesAsync(originalPath, materialized, options);
        }
        catch (Exception exception)
        {
            return new MergeResult { Success = false, Error = exception.Message };
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    public static bool IsXDelta(string path) => PatchInputService.IsXDelta(path);

    public static Task<string> MaterializeDataAsync(string originalPath, string inputPath, string tempDir)
    {
        return Path.GetExtension(inputPath).Equals(".csx", StringComparison.OrdinalIgnoreCase)
            ? MaterializeScriptAsync(originalPath, inputPath, tempDir)
            : PatchInputService.MaterializeDataAsync(originalPath, inputPath, tempDir);
    }

    private static async Task<string> MaterializeScriptAsync(string originalPath, string scriptPath, string tempDir)
    {
        if (!File.Exists(originalPath))
        {
            throw new FileNotFoundException("Original data file not found", originalPath);
        }
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("Patch input not found", scriptPath);
        }

        Directory.CreateDirectory(tempDir);
        string output = Path.Combine(tempDir, $"materialized_{Guid.NewGuid():N}{Path.GetExtension(originalPath)}");
        ScriptResult result = await ExecuteService.ExecuteScriptAsync(scriptPath, originalPath, output, []);
        if (!result.Success)
        {
            throw new InvalidOperationException("CSX execution failed for '" + Path.GetFileName(scriptPath) + "': " + result.Error);
        }

        PatchInputService.CopyExternalAudioGroups(originalPath, output);
        ValidateDataFile(output, scriptPath);
        return output;
    }

    private static void ValidateDataFile(string dataPath, string inputPath)
    {
        try
        {
            using FileStream stream = new(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using GameMakerData data = GameMakerIO.Read(stream);
        }
        catch (Exception ex)
        {
            if (!string.Equals(dataPath, inputPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(dataPath);
                }
                catch
                {
                }
            }
            throw new InvalidDataException("Validation failed for materialized input '" + Path.GetFileName(inputPath) + "': " + ex.Message, ex);
        }
    }
}
