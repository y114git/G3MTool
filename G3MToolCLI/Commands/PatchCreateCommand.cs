using System.CommandLine;
using System.Globalization;
using G3MToolCLI.Services.Execution;
using G3MToolCLI.Services.Logging;
using G3MToolCLI.Services.Patching;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

internal static class PatchCreateCommand
{
    public static Command Create()
    {
        var command = new Command("create", "Create a .g3mpatch or xdelta patch from an original data file and a supported input.\n  Usage: patch create <original> <input> [output] [--xdelta] [--xdelta-fallback] [--cache <dir>] [--xdelta-path <path>]");
        var originalArg = new Argument<FileInfo>("original") { Description = "Path to original data file (.win/.ios/.droid/.unx)" };
        var modifiedArg = new Argument<FileInfo>("modified") { Description = "Data file, .g3mpatch, .xdelta, .vcdiff, or .csx input" };
        var outputArg = new Argument<FileInfo?>("output") { DefaultValueFactory = _ => null, Description = "Output patch file (optional). Default: next to the executable" };
        var xdeltaFallbackOption = new Option<bool>("--xdelta-fallback") { Description = "Store an xdelta fallback. Disabled by default to keep .g3mpatch smaller." };
        var xdeltaOption = new Option<bool>("--xdelta") { Description = "Create an xdelta patch instead of a .g3mpatch." };
        var cacheOption = new Option<DirectoryInfo?>("--cache") { Description = "Read and write reusable .g3mcache analysis files in this directory." };
        command.Add(originalArg);
        command.Add(modifiedArg);
        command.Add(outputArg);
        command.Add(xdeltaFallbackOption);
        command.Add(xdeltaOption);
        command.Add(cacheOption);
        command.SetAction(parseResult => RunAsync(
            parseResult.GetRequiredValue(originalArg),
            parseResult.GetRequiredValue(modifiedArg),
            parseResult.GetValue(outputArg),
            parseResult.GetValue(xdeltaFallbackOption),
            parseResult.GetValue(xdeltaOption),
            parseResult.GetValue(cacheOption)));
        return command;
    }

    private static async Task RunAsync(FileInfo original, FileInfo modified, FileInfo? output, bool xdeltaFallback, bool xdeltaOutput, DirectoryInfo? cacheDir)
    {
        if (xdeltaFallback && xdeltaOutput)
        {
            PatchCommand.WriteErrorJsonOrText("patch create", "--xdelta and --xdelta-fallback are mutually exclusive.");
            Environment.ExitCode = 1;
            return;
        }
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var defaultOutput = Path.Combine(PlatformUtil.GetExecutableDirectory(), "patch_" + timestamp + (xdeltaOutput ? ".xdelta" : ".g3mpatch"));
        var outputPath = output?.FullName ?? defaultOutput;
        var tempDir = Path.Combine(Path.GetTempPath(), $"g3mtool_create_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var materialized = await ScriptPatchInputService.MaterializeDataAsync(original.FullName, modified.FullName, tempDir);
            LogCreate(original, modified, outputPath, xdeltaFallback, cacheDir);
            if (xdeltaOutput)
            {
                await CreateXDeltaAsync(original.FullName, materialized, outputPath);
                return;
            }
            await CreatePatchAsync(original, modified, materialized, outputPath, xdeltaFallback, cacheDir);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PatchCommand.WriteErrorJsonOrText("patch create", ex.Message);
            Environment.ExitCode = 1;
        }
        finally
        {
            PatchCommand.DeleteTemporaryDirectory(tempDir);
        }
    }

    private static void LogCreate(FileInfo original, FileInfo modified, string outputPath, bool xdeltaFallback, DirectoryInfo? cacheDir)
    {
        LogService.Log("Creating G3M patch...");
        LogService.Log("  Original: " + original.FullName);
        LogService.Log("  Modified: " + modified.FullName);
        LogService.Log("  Output:   " + outputPath);
        LogService.Log("  Xdelta fallback: " + (xdeltaFallback ? "enabled" : "disabled"));
        if (cacheDir != null)
        {
            LogService.Log("  Cache:    " + cacheDir.FullName);
        }
    }

    private static async Task CreateXDeltaAsync(string originalPath, string materializedPath, string outputPath)
    {
        var result = await XDeltaProcess.CreateAsync(EmbeddedXDeltaPathProvider.Resolve(), originalPath, materializedPath, outputPath, Program.Cancellation);
        if (!result.Success)
        {
            PatchCommand.WriteErrorJsonOrText("patch create", result.Error);
            Environment.ExitCode = 1;
            return;
        }
        if (Program.JsonOutput) PatchCommand.WriteSuccessJsonOrText("patch create", outputPath, new { format = "xdelta" });
        else Console.WriteLine("Patch created successfully: " + outputPath);
    }

    private static async Task CreatePatchAsync(FileInfo original, FileInfo modified, string materializedPath, string outputPath, bool xdeltaFallback, DirectoryInfo? cacheDir)
    {
        var result = await PatchService.CreatePatchAsync(original.FullName, materializedPath, outputPath, null, null, null, null, null, xdeltaFallback, G3MCacheOptions.FromDirectory(cacheDir?.FullName), Program.Cancellation);
        if (!result.Success)
        {
            PatchCommand.WriteErrorJsonOrText("patch create", result.Error);
            Environment.ExitCode = 1;
            return;
        }
        var statistics = result.Statistics;
        if (Program.JsonOutput)
        {
            PatchCommand.WriteJson(new
            {
                success = true,
                command = "patch create",
                original = original.FullName,
                modified = modified.FullName,
                output = outputPath,
                xdeltaFallback,
                statistics,
                warnings = Array.Empty<string>()
            });
            return;
        }
        Console.WriteLine("Patch created successfully: " + outputPath);
        if (statistics != null)
        {
            Console.WriteLine((statistics.TotalChangedFiles > 0) ? $"  Changed: {statistics.TotalChanged} ({statistics.TotalChangedFiles} files)" : $"  Changed: {statistics.TotalChanged}");
            Console.WriteLine((statistics.TotalNewFiles > 0) ? $"  New:     {statistics.TotalNew} ({statistics.TotalNewFiles} files)" : $"  New:     {statistics.TotalNew}");
            Console.WriteLine($"  Deleted: {statistics.TotalDeleted}");
        }
    }
}
