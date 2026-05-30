using System.CommandLine;
using System.Text.Json;
using G3MToolCLI.Models;
using G3MToolCLI.Services;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

public static class PatchCommand
{
    public static Command Create()
    {
        var command = new Command("patch", "Create, apply, validate, or merge .g3mpatch files.");

        var createCommand = new Command("create", "Create a .g3mpatch from an original data file and a modified data file or .xdelta patch.\n  Usage: patch create <original> <modified> [output] [--xdelta-fallback] [--cache <dir>] [--xdelta-path <path>]");
        var originalArg = new Argument<FileInfo>("original", "Path to original data file (.win/.ios/.droid/.unx)");
        var modifiedArg = new Argument<FileInfo>("modified", "Path to modified data file or .xdelta patch");
        var outputArg = new Argument<FileInfo?>("output", () => null, "Output patch file (optional). Default: next to G3MTool executable");
        var xdeltaFallbackOption = new Option<bool>(
            name: "--xdelta-fallback",
            description: "Store an xdelta fallback. Disabled by default to keep .g3mpatch smaller.");
        var createCacheOption = new Option<DirectoryInfo?>(
            name: "--cache",
            description: "Read and write reusable .g3mcache analysis files in this directory.");

        createCommand.AddArgument(originalArg);
        createCommand.AddArgument(modifiedArg);
        createCommand.AddArgument(outputArg);
        createCommand.AddOption(xdeltaFallbackOption);
        createCommand.AddOption(createCacheOption);

        createCommand.SetHandler(async (original, modified, output, xdeltaFallback, cacheDir) =>
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var defaultOutput = Path.Combine(PlatformUtil.GetExecutableDirectory(), $"patch_{timestamp}.g3mpatch");
            var outputPath = output?.FullName ?? defaultOutput;

            LogService.Log($"Creating G3M patch...");
            LogService.Log($"  Original: {original.FullName}");
            LogService.Log($"  Modified: {modified.FullName}");
            LogService.Log($"  Output:   {outputPath}");
            LogService.Log($"  Xdelta fallback: {(xdeltaFallback ? "enabled" : "disabled")}");
            if (cacheDir != null)
                LogService.Log($"  Cache:    {cacheDir.FullName}");

            var result = await PatchService.CreatePatchAsync(
                original.FullName,
                modified.FullName,
                outputPath,
                includeXdeltaFallback: xdeltaFallback,
                cacheOptions: G3MCacheOptions.FromDirectory(cacheDir?.FullName));

            if (result.Success)
            {
                var s = result.Statistics;
                if (Program.JsonOutput)
                {
                    WriteJson(new
                    {
                        success = true,
                        command = "patch create",
                        original = original.FullName,
                        modified = modified.FullName,
                        output = outputPath,
                        xdeltaFallback,
                        statistics = s,
                        warnings = Array.Empty<string>()
                    });
                }
                else
                {
                    Console.WriteLine($"Patch created successfully: {outputPath}");
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
            }
            else
            {
                WriteErrorJsonOrText("patch create", result.Error);
                Environment.ExitCode = 1;
            }
        }, originalArg, modifiedArg, outputArg, xdeltaFallbackOption, createCacheOption);

        var applyCommand = new Command("apply", "Apply a .g3mpatch to a data file. .xdelta input is applied directly; data-file input is converted first.\n  Usage: patch apply <data> <patch> [output] [--xdelta-fallback] [--cache <dir>] [--xdelta-path <path>]");
        var dataArg = new Argument<FileInfo>("data", "Path to original data file (.win/.ios/.droid/.unx)");
        var patchArg = new Argument<FileInfo>("patch", "Path to patch file (.g3mpatch, .xdelta, or data file)");
        var applyOutputArg = new Argument<FileInfo?>("output", () => null, "Output file (optional). Default: next to G3MTool executable");
        var applyXdeltaFallbackOption = new Option<bool>(
            name: "--xdelta-fallback",
            description: "Try the embedded xdelta copy first. If it fails, continue with normal .g3mpatch apply.");
        var applyCacheOption = new Option<DirectoryInfo?>(
            name: "--cache",
            description: "Read and write reusable .g3mcache analysis files when converting data-file or xdelta input.");

        applyCommand.AddArgument(dataArg);
        applyCommand.AddArgument(patchArg);
        applyCommand.AddArgument(applyOutputArg);
        applyCommand.AddOption(applyXdeltaFallbackOption);
        applyCommand.AddOption(applyCacheOption);

        applyCommand.SetHandler(async (data, patch, output, xdeltaFallback, cacheDir) =>
        {
            var defaultOutput = Path.Combine(PlatformUtil.GetExecutableDirectory(), Path.GetFileName(data.FullName));
            var outputPath = output?.FullName ?? defaultOutput;
            var patchExtension = Path.GetExtension(patch.FullName);

            if (patchExtension.Equals(".xdelta", StringComparison.OrdinalIgnoreCase))
            {
                LogService.Warning($"Input '{Path.GetFileName(patch.FullName)}' is xdelta, applying directly...");

                var xdelta = new XDeltaService();
                var xdeltaResult = await xdelta.ApplyPatchAsync(data.FullName, patch.FullName, outputPath);

                if (xdeltaResult.Success)
                {
                    WriteSuccessJsonOrText("patch apply", outputPath, new
                    {
                        inputKind = "xdelta",
                        data = data.FullName,
                        patch = patch.FullName
                    });
                }
                else
                {
                    WriteErrorJsonOrText("patch apply", xdeltaResult.Error);
                    Environment.ExitCode = 1;
                }
                return;
            }

            string patchPath;
            try
            {
                patchPath = await PatchService.EnsureG3MPatchAsync(
                    data.FullName,
                    patch.FullName,
                    cacheOptions: G3MCacheOptions.FromDirectory(cacheDir?.FullName));
            }
            catch (Exception ex)
            {
                WriteErrorJsonOrText("patch apply", ex.Message);
                Environment.ExitCode = 1;
                return;
            }

            LogService.Log($"Applying G3M patch...");
            LogService.Log($"  Data:   {data.FullName}");
            LogService.Log($"  Patch:  {patchPath}");
            LogService.Log($"  Output: {outputPath}");
            LogService.Log($"  Xdelta fallback: {(xdeltaFallback ? "enabled" : "disabled")}");
            if (cacheDir != null)
                LogService.Log($"  Cache:  {cacheDir.FullName}");

            var result = await PatchService.ApplyPatchAsync(
                data.FullName,
                patchPath,
                outputPath,
                allowXdeltaFallback: xdeltaFallback);

            if (result.Success)
            {
                WriteSuccessJsonOrText("patch apply", outputPath, new
                {
                    inputKind = "g3mpatch",
                    data = data.FullName,
                    patch = patchPath,
                    xdeltaFallback
                });
            }
            else
            {
                WriteErrorJsonOrText("patch apply", result.Error);
                Environment.ExitCode = 1;
            }
        }, dataArg, patchArg, applyOutputArg, applyXdeltaFallbackOption, applyCacheOption);

        var validateCommand = new Command("validate", "Validate a G3M patch file and optionally check compatibility with a data file.\n  Usage: patch validate <patch> [--data <data-file>] [--cache <dir>]");
        var validatePatchArg = new Argument<FileInfo>("patch", "Path to G3M patch file (.g3mpatch)");
        var validateDataOption = new Option<FileInfo?>(
            aliases: ["--data", "-d"],
            description: "Optional data file (.win/.ios/.droid/.unx) to check compatibility");
        var validateCacheOption = new Option<DirectoryInfo?>(
            name: "--cache",
            description: "Read reusable .g3mcache analysis when checking --data.");

        validateCommand.AddArgument(validatePatchArg);
        validateCommand.AddOption(validateDataOption);
        validateCommand.AddOption(validateCacheOption);

        validateCommand.SetHandler(async (patch, data, cacheDir) =>
        {
            if (!Program.JsonOutput)
                Console.WriteLine($"Validating G3M patch: {patch.FullName}");

            var result = await PatchService.ValidatePatchAsync(
                patch.FullName,
                data?.FullName,
                G3MCacheOptions.FromDirectory(cacheDir?.FullName));

            if (result.Success)
            {
                if (Program.JsonOutput)
                {
                    var manifest = result.Manifest;
                    WriteJson(new
                    {
                        success = true,
                        command = "patch validate",
                        patch = patch.FullName,
                        data = data?.FullName,
                        tool = manifest?.Tool,
                        createdAt = manifest?.CreatedAt,
                        original = manifest?.Original,
                        modified = manifest?.Modified,
                        statistics = manifest?.Statistics,
                        applyPlan = manifest?.ApplyPlan,
                        resourceTypes = manifest?.Resources?
                            .Where(kvp =>
                                (kvp.Value.Changed?.Count ?? 0) > 0 ||
                                (kvp.Value.New?.Count ?? 0) > 0 ||
                                (kvp.Value.Deleted?.Count ?? 0) > 0)
                            .ToDictionary(
                                kvp => kvp.Key,
                                kvp => new
                                {
                                    changed = kvp.Value.Changed?.Count ?? 0,
                                    @new = kvp.Value.New?.Count ?? 0,
                                    deleted = kvp.Value.Deleted?.Count ?? 0
                                },
                                StringComparer.OrdinalIgnoreCase),
                        warnings = Array.Empty<string>()
                    });
                }
                else
                {
                    Console.WriteLine("Patch is valid.");
                    if (result.Manifest != null)
                    {
                        Console.WriteLine($"  Tool: {result.Manifest.Tool?.Name} v{result.Manifest.Tool?.Version}");
                        Console.WriteLine($"  Created: {result.Manifest.CreatedAt}");
                        Console.WriteLine($"  Resources: {result.Manifest.Statistics?.TotalChanged ?? 0} changed, {result.Manifest.Statistics?.TotalNew ?? 0} new, {result.Manifest.Statistics?.TotalDeleted ?? 0} deleted");
                    }
                }
            }
            else
            {
                WriteErrorJsonOrText("patch validate", result.Error);
                Environment.ExitCode = 1;
            }
        }, validatePatchArg, validateDataOption, validateCacheOption);

        var mergeCommand = new Command("merge",
            "Merge multiple patches into one .g3mpatch.\n" +
            "  The first argument is the original data file (required as context).\n" +
            "  Subsequent arguments are patches (from lowest to highest priority).\n" +
            "  Input can be .g3mpatch, .xdelta, or data file (.win/.ios/.droid/.unx).\n" +
            "  Usage: patch merge <original> <patch1> <patch2> [patch3...] [flags] [--cache <dir>] [--xdelta-path <path>]");

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
        var mergeCacheOption = new Option<DirectoryInfo?>(
            name: "--cache",
            description: "Read and write reusable .g3mcache analysis files in this directory.");

        mergeCommand.AddArgument(mergeOriginalArg);
        mergeCommand.AddArgument(mergePatchesArg);
        mergeCommand.AddOption(mergeOutOption);
        mergeCommand.AddOption(mergeApplyOption);
        mergeCommand.AddOption(mergeCodeOption);
        mergeCommand.AddOption(mergePropertiesOption);
        mergeCommand.AddOption(mergeReportOption);
        mergeCommand.AddOption(mergeCacheOption);

        mergeCommand.SetHandler(async (original, patches, outPath, applyPath, code, properties, conflictsLog, cacheDir) =>
        {
            var patchPaths = patches.Select(p => p.FullName).ToList();

            var options = new MergeOptions
            {
                OutputPath = outPath,
                ApplyPath = applyPath,
                UseCodeMerge = code,
                UsePropertyMerge = properties,
                ReportPath = conflictsLog,
                CacheOptions = G3MCacheOptions.FromDirectory(cacheDir?.FullName)
            };

            var result = await MergeService.MergePatchesAsync(original.FullName, patchPaths, options);

            if (!result.Success)
            {
                WriteErrorJsonOrText("patch merge", result.Error);
                Environment.ExitCode = 1;
            }
            else if (Program.JsonOutput)
            {
                WriteJson(new
                {
                    success = true,
                    command = "patch merge",
                    original = original.FullName,
                    patches = patchPaths,
                    output = result.OutputPath,
                    applied = applyPath,
                    conflicts = result.TotalConflicts,
                    autoMerged = result.AutoMerged,
                    warnings = result.TotalConflicts > 0
                        ? new[] { "merge completed with conflicts; inspect the merge report if one was requested" }
                        : []
                });
            }
        }, mergeOriginalArg, mergePatchesArg, mergeOutOption, mergeApplyOption,
           mergeCodeOption, mergePropertiesOption, mergeReportOption, mergeCacheOption);

        command.AddCommand(createCommand);
        command.AddCommand(applyCommand);
        command.AddCommand(validateCommand);
        command.AddCommand(mergeCommand);

        return command;
    }

    private static void WriteSuccessJsonOrText<T>(string command, string outputPath, T details)
    {
        if (Program.JsonOutput)
            WriteJson(new { success = true, command, output = outputPath, details, warnings = Array.Empty<string>() });
        else
            Console.WriteLine($"Patch applied successfully: {outputPath}");
    }

    private static void WriteErrorJsonOrText(string command, string? error)
    {
        if (Program.JsonOutput)
            WriteJson(new { success = false, command, error });
        else
            Console.Error.WriteLine($"Error: {error}");
    }

    private static void WriteJson<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false }));
}
