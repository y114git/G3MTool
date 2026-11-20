using System.CommandLine;
using G3MToolCLI.Services.Logging;
using G3MToolCLI.Services.Patching;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

internal static class PatchApplyCommand
{
    public static Command Create()
    {
        var command = new Command("apply", "Apply a .g3mpatch to a data file. .xdelta input is applied directly; data-file input is converted first.\n  Usage: patch apply <data> <patch> [output] [--xdelta-fallback] [--cache <dir>] [--xdelta-path <path>]");
        var dataArg = new Argument<FileInfo>("data") { Description = "Path to original data file (.win/.ios/.droid/.unx)" };
        var patchArg = new Argument<FileInfo>("patch") { Description = "Path to patch file (.g3mpatch, .xdelta, or data file)" };
        var outputArg = new Argument<FileInfo?>("output") { DefaultValueFactory = _ => null, Description = "Output file (optional). Default: next to the executable" };
        var xdeltaFallbackOption = new Option<bool>("--xdelta-fallback") { Description = "Try the embedded xdelta copy first. If it fails, continue with normal .g3mpatch apply." };
        var cacheOption = new Option<DirectoryInfo?>("--cache") { Description = "Read and write reusable .g3mcache analysis files when converting data-file or xdelta input." };
        command.Add(dataArg);
        command.Add(patchArg);
        command.Add(outputArg);
        command.Add(xdeltaFallbackOption);
        command.Add(cacheOption);
        command.SetAction(async parseResult =>
        {
            var data = parseResult.GetRequiredValue(dataArg);
            var patch = parseResult.GetRequiredValue(patchArg);
            var outputPath = parseResult.GetValue(outputArg)?.FullName ?? Path.Combine(PlatformUtil.GetExecutableDirectory(), Path.GetFileName(data.FullName));
            if (ScriptPatchInputService.IsXDelta(patch.FullName) || Path.GetExtension(patch.FullName).Equals(".csx", StringComparison.OrdinalIgnoreCase))
            {
                await ApplyConvertedInputAsync(data, patch, outputPath);
                return;
            }
            await ApplyPatchFileAsync(data, patch, outputPath, parseResult.GetValue(xdeltaFallbackOption), parseResult.GetValue(cacheOption));
        });
        return command;
    }

    // An xdelta or script input is turned into a data file first, and that file is the output.
    private static async Task ApplyConvertedInputAsync(FileInfo data, FileInfo patch, string outputPath)
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory("g3mtool-apply-").FullName;
        try
        {
            var materialized = await ScriptPatchInputService.MaterializeDataAsync(data.FullName, patch.FullName, temporaryDirectory);
            Program.Cancellation.ThrowIfCancellationRequested();
            PatchInputService.CopyDataFile(materialized, outputPath);
            PatchCommand.WriteSuccessJsonOrText("patch apply", outputPath, new { inputKind = ScriptPatchInputService.IsXDelta(patch.FullName) ? "xdelta" : "csx", data = data.FullName, patch = patch.FullName });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PatchCommand.WriteErrorJsonOrText("patch apply", exception.Message);
            Environment.ExitCode = 1;
        }
        finally
        {
            PatchCommand.DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    private static async Task ApplyPatchFileAsync(FileInfo data, FileInfo patch, string outputPath, bool xdeltaFallback, DirectoryInfo? cacheDir)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"g3mtool_apply_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var patchPath = await PatchService.EnsureG3MPatchAsync(data.FullName, patch.FullName, temporaryDirectory, cacheOptions: G3MCacheOptions.FromDirectory(cacheDir?.FullName));
            LogService.Log("Applying G3M patch...");
            LogService.Log("  Data:   " + data.FullName);
            LogService.Log("  Patch:  " + patchPath);
            LogService.Log("  Output: " + outputPath);
            LogService.Log("  Xdelta fallback: " + (xdeltaFallback ? "enabled" : "disabled"));
            if (cacheDir != null)
            {
                LogService.Log("  Cache:  " + cacheDir.FullName);
            }
            var result = await PatchService.ApplyPatchAsync(data.FullName, patchPath, outputPath, xdeltaFallback, cancellationToken: Program.Cancellation);
            if (!result.Success)
            {
                PatchCommand.WriteErrorJsonOrText("patch apply", result.Error);
                Environment.ExitCode = 1;
                return;
            }
            PatchCommand.WriteSuccessJsonOrText("patch apply", outputPath, new
            {
                inputKind = "g3mpatch",
                data = data.FullName,
                patch = patchPath,
                xdeltaFallback
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PatchCommand.WriteErrorJsonOrText("patch apply", ex.Message);
            Environment.ExitCode = 1;
        }
        finally
        {
            PatchCommand.DeleteTemporaryDirectory(temporaryDirectory);
        }
    }
}
