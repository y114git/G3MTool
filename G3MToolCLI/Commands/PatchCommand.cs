using System.CommandLine;
using G3MToolCLI.Services;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

public static class PatchCommand
{
    public static Command Create()
    {
        var command = new Command("patch", "Create, apply, or validate G3M resource patches");

        // patch create
        var createCommand = new Command("create", "Create a G3M patch from two data files");
        var originalArg = new Argument<FileInfo>("original", "Path to original data.win");
        var modifiedArg = new Argument<FileInfo>("modified", "Path to modified data.win");
        var outputArg = new Argument<FileInfo?>("output", () => null, "Output patch file (optional). Default: next to G3MTool executable");

        createCommand.AddArgument(originalArg);
        createCommand.AddArgument(modifiedArg);
        createCommand.AddArgument(outputArg);

        createCommand.SetHandler(async (original, modified, output) =>
        {
            var patchService = new PatchService();
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var defaultOutput = Path.Combine(PlatformUtils.GetExecutableDirectory(), $"patch_{timestamp}.zip");
            var outputPath = output?.FullName ?? defaultOutput;
            
            LogService.Log($"Creating G3M patch...");
            LogService.Log($"  Original: {original.FullName}");
            LogService.Log($"  Modified: {modified.FullName}");
            LogService.Log($"  Output:   {outputPath}");

            var modifiedPath = modified.FullName;
            string? tempModifiedFile = null;

            // If modified is an xdelta patch, apply it first to get the actual modified data
            if (modified.Extension.Equals(".xdelta", StringComparison.OrdinalIgnoreCase))
            {
                LogService.Log("[PatchCommand] Detected xdelta patch, applying to original first...");
                var xdeltaService = new XDeltaService();
                tempModifiedFile = Path.Combine(Path.GetTempPath(), $"g3mtool_mod_{Guid.NewGuid():N}.win");
                
                var xdeltaResult = await xdeltaService.ApplyPatchAsync(original.FullName, modified.FullName, tempModifiedFile);
                if (!xdeltaResult.Success)
                {
                    Console.Error.WriteLine($"Error applying xdelta: {xdeltaResult.Error}");
                    Environment.ExitCode = 1;
                    return;
                }
                
                LogService.Log($"[PatchCommand] xdelta applied, using temp file: {tempModifiedFile}");
                modifiedPath = tempModifiedFile;
            }

            try
            {
                var result = await patchService.CreatePatchAsync(original.FullName, modifiedPath, outputPath);
            
                if (result.Success)
                {
                    Console.WriteLine($"Patch created successfully: {outputPath}");
                    Console.WriteLine($"  Changed: {result.Statistics?.TotalChanged ?? 0}");
                    Console.WriteLine($"  New:     {result.Statistics?.TotalNew ?? 0}");
                    Console.WriteLine($"  Deleted: {result.Statistics?.TotalDeleted ?? 0}");
                }
                else
                {
                    Console.Error.WriteLine($"Error: {result.Error}");
                    Environment.ExitCode = 1;
                }
            }
            finally
            {
                // Cleanup temp file if created
                if (tempModifiedFile != null && File.Exists(tempModifiedFile))
                {
                    try { File.Delete(tempModifiedFile); } catch { }
                }
            }
        }, originalArg, modifiedArg, outputArg);

        // patch apply
        var applyCommand = new Command("apply", "Apply a G3M patch to a data file");
        var dataArg = new Argument<FileInfo>("data", "Path to data.win to patch");
        var patchArg = new Argument<FileInfo>("patch", "Path to G3M patch file (.zip)");
        var applyOutputArg = new Argument<FileInfo?>("output", () => null, "Output file (optional). Default: next to G3MTool executable");
        var skipValidationOption = new Option<bool>(
            name: "--skip-validation",
            description: "Skip patch validation before applying");

        applyCommand.AddArgument(dataArg);
        applyCommand.AddArgument(patchArg);
        applyCommand.AddArgument(applyOutputArg);
        applyCommand.AddOption(skipValidationOption);

        applyCommand.SetHandler(async (data, patch, output, skipValidation) =>
        {
            var patchService = new PatchService();
            var defaultOutput = Path.Combine(PlatformUtils.GetExecutableDirectory(), Path.GetFileName(data.FullName));
            var outputPath = output?.FullName ?? defaultOutput;
            
            LogService.Log($"Applying G3M patch...");
            LogService.Log($"  Data:   {data.FullName}");
            LogService.Log($"  Patch:  {patch.FullName}");
            LogService.Log($"  Output: {outputPath}");

            var result = await patchService.ApplyPatchAsync(data.FullName, patch.FullName, outputPath, skipValidation);
            
            if (result.Success)
            {
                Console.WriteLine($"Patch applied successfully: {outputPath}");
            }
            else
            {
                Console.Error.WriteLine($"Error: {result.Error}");
                Environment.ExitCode = 1;
            }
        }, dataArg, patchArg, applyOutputArg, skipValidationOption);

        // patch validate
        var validateCommand = new Command("validate", "Validate a G3M patch");
        var validatePatchArg = new Argument<FileInfo>("patch", "Path to G3M patch file (.zip)");
        var validateDataOption = new Option<FileInfo?>(
            aliases: ["--data", "-d"],
            description: "Optional data.win to check compatibility");

        validateCommand.AddArgument(validatePatchArg);
        validateCommand.AddOption(validateDataOption);

        validateCommand.SetHandler(async (patch, data) =>
        {
            var patchService = new PatchService();
            
            Console.WriteLine($"Validating G3M patch: {patch.FullName}");

            var result = await patchService.ValidatePatchAsync(patch.FullName, data?.FullName);
            
            if (result.Success)
            {
                Console.WriteLine("Patch is valid.");
                if (result.Manifest != null)
                {
                    Console.WriteLine($"  Version: {result.Manifest.Version}");
                    Console.WriteLine($"  Created: {result.Manifest.CreatedAt}");
                    Console.WriteLine($"  Resources: {result.Manifest.Statistics?.TotalChanged ?? 0} changed, {result.Manifest.Statistics?.TotalNew ?? 0} new, {result.Manifest.Statistics?.TotalDeleted ?? 0} deleted");
                }
            }
            else
            {
                Console.Error.WriteLine($"Validation failed: {result.Error}");
                Environment.ExitCode = 1;
            }
        }, validatePatchArg, validateDataOption);

        command.AddCommand(createCommand);
        command.AddCommand(applyCommand);
        command.AddCommand(validateCommand);

        return command;
    }
}
