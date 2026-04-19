using System.CommandLine;
using G3MToolCLI.Services;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

public static class PatchCommand
{
    public static Command Create()
    {
        var command = new Command("patch", "Create, apply, validate, or merge G3M resource patches. Subcommands: create, apply, validate, merge");

        // patch create
        var createCommand = new Command("create", "Create a G3M patch by comparing original and modified data files.\n  Usage: patch create <original> <modified> [output] [--xdelta-fallback]");
        var originalArg = new Argument<FileInfo>("original", "Path to original data file (.win/.ios/.droid/.unx)");
        var modifiedArg = new Argument<FileInfo>("modified", "Path to modified data file (.win/.ios/.droid/.unx)");
        var outputArg = new Argument<FileInfo?>("output", () => null, "Output patch file (optional). Default: next to G3MTool executable");
        var xdeltaFallbackOption = new Option<bool>(
            name: "--xdelta-fallback",
            description: "Embed an optional xdelta fallback in Xdelta/. Disabled by default to keep g3mpatch semantic and merge-friendly.");

        createCommand.AddArgument(originalArg);
        createCommand.AddArgument(modifiedArg);
        createCommand.AddArgument(outputArg);
        createCommand.AddOption(xdeltaFallbackOption);

        createCommand.SetHandler(async (original, modified, output, xdeltaFallback) =>
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var defaultOutput = Path.Combine(PlatformUtil.GetExecutableDirectory(), $"patch_{timestamp}.g3mpatch");
            var outputPath = output?.FullName ?? defaultOutput;

            LogService.Log($"Creating G3M patch...");
            LogService.Log($"  Original: {original.FullName}");
            LogService.Log($"  Modified: {modified.FullName}");
            LogService.Log($"  Output:   {outputPath}");
            LogService.Log($"  Xdelta fallback: {(xdeltaFallback ? "enabled" : "disabled")}");

            var result = await PatchService.CreatePatchAsync(
                original.FullName,
                modified.FullName,
                outputPath,
                includeXdeltaFallback: xdeltaFallback);

            if (result.Success)
            {
                Console.WriteLine($"Patch created successfully: {outputPath}");
                var s = result.Statistics;
                if (s != null)
                {
                    Console.WriteLine(s.TotalChangedFiles > 0
                        ? $"  Changed: {s.TotalChanged} ({s.TotalChangedFiles} files)"
                        : $"  Changed: {s.TotalChanged}");
                    Console.WriteLine(s.TotalNewFiles > 0
                        ? $"  New:     {s.TotalNew} ({s.TotalNewFiles} files)"
                        : $"  New:     {s.TotalNew}");
                    Console.WriteLine($"  Deleted: {s.TotalDeleted}");
                }
            }
            else
            {
                Console.Error.WriteLine($"Error: {result.Error}");
                Environment.ExitCode = 1;
            }
        }, originalArg, modifiedArg, outputArg, xdeltaFallbackOption);

        // patch apply
        var applyCommand = new Command("apply", "Apply a G3M patch to a data file.\n  Input can be .g3mpatch, .xdelta, or data file (.win/.ios/.droid/.unx).\n  Non-g3mpatch inputs are auto-converted using the original data file as reference.\n  Usage: patch apply <data> <patch> [output]");
        var dataArg = new Argument<FileInfo>("data", "Path to original data file (.win/.ios/.droid/.unx)");
        var patchArg = new Argument<FileInfo>("patch", "Path to patch file (.g3mpatch, .xdelta, or data file)");
        var applyOutputArg = new Argument<FileInfo?>("output", () => null, "Output file (optional). Default: next to G3MTool executable");

        applyCommand.AddArgument(dataArg);
        applyCommand.AddArgument(patchArg);
        applyCommand.AddArgument(applyOutputArg);

        applyCommand.SetHandler(async (data, patch, output) =>
        {
            var defaultOutput = Path.Combine(PlatformUtil.GetExecutableDirectory(), Path.GetFileName(data.FullName));
            var outputPath = output?.FullName ?? defaultOutput;

            // Auto-convert non-g3mpatch inputs
            var patchPath = await PatchService.EnsureG3MPatchAsync(data.FullName, patch.FullName);

            LogService.Log($"Applying G3M patch...");
            LogService.Log($"  Data:   {data.FullName}");
            LogService.Log($"  Patch:  {patchPath}");
            LogService.Log($"  Output: {outputPath}");

            var result = await PatchService.ApplyPatchAsync(data.FullName, patchPath, outputPath);

            if (result.Success)
            {
                Console.WriteLine($"Patch applied successfully: {outputPath}");
            }
            else
            {
                Console.Error.WriteLine($"Error: {result.Error}");
                Environment.ExitCode = 1;
            }
        }, dataArg, patchArg, applyOutputArg);

        // patch validate
        var validateCommand = new Command("validate", "Validate a G3M patch file and optionally check compatibility with a data file.\n  Usage: patch validate <patch> [--data <data-file>]");
        var validatePatchArg = new Argument<FileInfo>("patch", "Path to G3M patch file (.g3mpatch)");
        var validateDataOption = new Option<FileInfo?>(
            aliases: ["--data", "-d"],
            description: "Optional data file (.win/.ios/.droid/.unx) to check compatibility");

        validateCommand.AddArgument(validatePatchArg);
        validateCommand.AddOption(validateDataOption);

        validateCommand.SetHandler(async (patch, data) =>
        {
            Console.WriteLine($"Validating G3M patch: {patch.FullName}");

            var result = await PatchService.ValidatePatchAsync(patch.FullName, data?.FullName);

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

        // patch merge
        var mergeCommand = new Command("merge",
            "Merge multiple G3M patches into a single coherent patch.\n" +
            "  The first argument is the original data file (required as context).\n" +
            "  Subsequent arguments are patches (from lowest to highest priority).\n" +
            "  Input can be .g3mpatch, .xdelta, or data file (.win/.ios/.droid/.unx).\n" +
            "  Usage: patch merge <original> <patch1> <patch2> [patch3...] [flags]");

        var mergeOriginalArg = new Argument<FileInfo>("original", "Path to original data file (.win/.ios/.droid/.unx)");
        var mergePatchesArg = new Argument<FileInfo[]>("patches", "Patch files (low → high priority)")
        {
            Arity = new ArgumentArity(2, 100)
        };

        var mergeOutOption = new Option<string?>(
            aliases: ["--out", "-o"],
            description: "Output path for merged .g3mpatch (default if no flags specified)");

        var mergeApplyOption = new Option<string?>(
            aliases: ["--apply", "-a"],
            description: "Apply merged patch and save the resulting data file to this path");

        var mergeCodeOption = new Option<bool>(
            name: "--code",
            description: "Enable Git-style 3-way merge for GML code files");

        var mergePropertiesOption = new Option<bool>(
            name: "--properties",
            description: "Enable deep merge for JSON property files");

        var mergeReportOption = new Option<string?>(
            aliases: ["--report", "-r"],
            description: "Path for the merge report (Markdown)");

        mergeCommand.AddArgument(mergeOriginalArg);
        mergeCommand.AddArgument(mergePatchesArg);
        mergeCommand.AddOption(mergeOutOption);
        mergeCommand.AddOption(mergeApplyOption);
        mergeCommand.AddOption(mergeCodeOption);
        mergeCommand.AddOption(mergePropertiesOption);
        mergeCommand.AddOption(mergeReportOption);

        mergeCommand.SetHandler(async (original, patches, outPath, applyPath, code, properties, conflictsLog) =>
        {
            var patchPaths = patches.Select(p => p.FullName).ToList();

            var options = new MergeOptions
            {
                OutputPath = outPath,
                ApplyPath = applyPath,
                UseCodeMerge = code,
                UsePropertyMerge = properties,
                ReportPath = conflictsLog
            };

            var result = await MergeService.MergePatchesAsync(original.FullName, patchPaths, options);

            if (!result.Success)
            {
                Console.Error.WriteLine($"Error: {result.Error}");
                Environment.ExitCode = 1;
            }
        }, mergeOriginalArg, mergePatchesArg, mergeOutOption, mergeApplyOption,
           mergeCodeOption, mergePropertiesOption, mergeReportOption);

        command.AddCommand(createCommand);
        command.AddCommand(applyCommand);
        command.AddCommand(validateCommand);
        command.AddCommand(mergeCommand);

        return command;
    }

}
