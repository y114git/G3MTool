using System.CommandLine;
using System.CommandLine.Invocation;
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

        var createCommand = new Command("create", "Create a .g3mpatch or xdelta patch from an original data file and a supported input.\n  Usage: patch create <original> <input> [output] [--xdelta] [--xdelta-fallback] [--cache <dir>] [--xdelta-path <path>]");
        var originalArg = new Argument<FileInfo>("original", "Path to original data file (.win/.ios/.droid/.unx)");
        var modifiedArg = new Argument<FileInfo>("modified", "Data file, .g3mpatch, .xdelta, .vcdiff, or .csx input");
        var outputArg = new Argument<FileInfo?>("output", () => null, "Output patch file (optional). Default: next to G3MTool executable");
        var xdeltaFallbackOption = new Option<bool>(
            name: "--xdelta-fallback",
            description: "Store an xdelta fallback. Disabled by default to keep .g3mpatch smaller.");
        var createXdeltaOption = new Option<bool>(
            name: "--xdelta",
            description: "Create an xdelta patch instead of a .g3mpatch.");
        var createCacheOption = new Option<DirectoryInfo?>(
            name: "--cache",
            description: "Read and write reusable .g3mcache analysis files in this directory.");

        createCommand.AddArgument(originalArg);
        createCommand.AddArgument(modifiedArg);
        createCommand.AddArgument(outputArg);
        createCommand.AddOption(xdeltaFallbackOption);
        createCommand.AddOption(createXdeltaOption);
        createCommand.AddOption(createCacheOption);

        createCommand.SetHandler(async (original, modified, output, xdeltaFallback, xdeltaOutput, cacheDir) =>
        {
            if (xdeltaFallback && xdeltaOutput)
            {
                WriteErrorJsonOrText("patch create", "--xdelta and --xdelta-fallback are mutually exclusive.");
                Environment.ExitCode = 1;
                return;
            }
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var defaultOutput = Path.Combine(PlatformUtil.GetExecutableDirectory(), $"patch_{timestamp}{(xdeltaOutput ? ".xdelta" : ".g3mpatch")}");
            var outputPath = output?.FullName ?? defaultOutput;
            var tempDir = Path.Combine(Path.GetTempPath(), $"g3mtool_create_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string materialized;
            try { materialized = await PatchInputService.MaterializeDataAsync(original.FullName, modified.FullName, tempDir); }
            catch (Exception ex) { WriteErrorJsonOrText("patch create", ex.Message); Environment.ExitCode = 1; return; }

            LogService.Log($"Creating G3M patch...");
            LogService.Log($"  Original: {original.FullName}");
            LogService.Log($"  Modified: {modified.FullName}");
            LogService.Log($"  Output:   {outputPath}");
            LogService.Log($"  Xdelta fallback: {(xdeltaFallback ? "enabled" : "disabled")}");
            if (cacheDir != null)
                LogService.Log($"  Cache:    {cacheDir.FullName}");

            if (xdeltaOutput)
            {
                var xresult = await new XDeltaService().CreatePatchAsync(original.FullName, materialized, outputPath);
                if (!xresult.Success) { WriteErrorJsonOrText("patch create", xresult.Error); Environment.ExitCode = 1; }
                else WriteSuccessJsonOrText("patch create", outputPath, new { format = "xdelta" });
                try { Directory.Delete(tempDir, true); } catch { }
                return;
            }
            var result = await PatchService.CreatePatchAsync(
                original.FullName,
                materialized,
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
            try { Directory.Delete(tempDir, true); } catch { }
        }, originalArg, modifiedArg, outputArg, xdeltaFallbackOption, createXdeltaOption, createCacheOption);

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

        var batchCommand = new Command("batch", "Run multiple patch operations with hash-based job deduplication.");
        static Option<DirectoryInfo> BatchOutDirOption() => new(
            name: "--out-dir",
            description: "Directory where batch outputs are written.")
        {
            IsRequired = true
        };
        static Option<DirectoryInfo?> BatchCacheOption() => new(
            name: "--cache",
            description: "Read and write reusable .g3mcache analysis files in this directory.");
        static Option<bool> ContinueOnErrorOption() => new(
            name: "--continue-on-error",
            description: "Continue remaining batch jobs after a failure.");

        var batchApplyCommand = new Command("apply",
            "Apply each patch independently to the same original data file.\n" +
            "  Usage: patch batch apply <original> <patches...> --out-dir <dir> [--cache <dir>] [--continue-on-error] [--xdelta-fallback]");
        var batchApplyOriginalArg = new Argument<FileInfo>("original", "Path to original data file (.win/.ios/.droid/.unx)");
        var batchApplyPatchesArg = new Argument<FileInfo[]>("patches", "Patch files (.g3mpatch, .xdelta, or data files)")
        {
            Arity = new ArgumentArity(1, 1000)
        };
        var batchApplyXdeltaFallbackOption = new Option<bool>(
            name: "--xdelta-fallback",
            description: "Try embedded xdelta fallback when applying .g3mpatch files.");
        var batchApplyOutDirOption = BatchOutDirOption();
        var batchApplyCacheOption = BatchCacheOption();
        var batchApplyContinueOnErrorOption = ContinueOnErrorOption();
        batchApplyCommand.AddArgument(batchApplyOriginalArg);
        batchApplyCommand.AddArgument(batchApplyPatchesArg);
        batchApplyCommand.AddOption(batchApplyOutDirOption);
        batchApplyCommand.AddOption(batchApplyCacheOption);
        batchApplyCommand.AddOption(batchApplyContinueOnErrorOption);
        batchApplyCommand.AddOption(batchApplyXdeltaFallbackOption);
        batchApplyCommand.SetHandler(async (original, patches, outDir, cacheDir, continueOnError, xdeltaFallback) =>
        {
            var result = await BatchPatchService.ApplyBatchAsync(
                patches.Select(p => p.FullName).ToArray(),
                new BatchOptions
                {
                    OriginalPath = original.FullName,
                    OutDir = outDir.FullName,
                    CacheOptions = G3MCacheOptions.FromDirectory(cacheDir?.FullName),
                    ContinueOnError = continueOnError,
                    XdeltaFallback = xdeltaFallback
                });
            WriteBatchResult("patch batch apply", result);
            if (!result.Success)
                Environment.ExitCode = 1;
        }, batchApplyOriginalArg, batchApplyPatchesArg, batchApplyOutDirOption, batchApplyCacheOption, batchApplyContinueOnErrorOption, batchApplyXdeltaFallbackOption);

        var batchCreateCommand = new Command("create",
            "Create one .g3mpatch or xdelta patch for each input against the same original data file.\n" +
            "  Usage: patch batch create <original> <modified...> --out-dir <dir> [--xdelta] [--cache <dir>] [--continue-on-error] [--xdelta-fallback]");
        var batchCreateOriginalArg = new Argument<FileInfo>("original", "Path to original data file (.win/.ios/.droid/.unx)");
        var batchCreateModifiedArg = new Argument<FileInfo[]>("modified", "Data files, .g3mpatch, .xdelta, .vcdiff, or .csx inputs")
        {
            Arity = new ArgumentArity(1, 1000)
        };
        var batchCreateXdeltaFallbackOption = new Option<bool>(
            name: "--xdelta-fallback",
            description: "Store xdelta fallback in created .g3mpatch files.");
        var batchCreateXdeltaOption = new Option<bool>(
            name: "--xdelta",
            description: "Create xdelta patches instead of .g3mpatch files.");
        var batchCreateOutDirOption = BatchOutDirOption();
        var batchCreateCacheOption = BatchCacheOption();
        var batchCreateContinueOnErrorOption = ContinueOnErrorOption();
        batchCreateCommand.AddArgument(batchCreateOriginalArg);
        batchCreateCommand.AddArgument(batchCreateModifiedArg);
        batchCreateCommand.AddOption(batchCreateOutDirOption);
        batchCreateCommand.AddOption(batchCreateCacheOption);
        batchCreateCommand.AddOption(batchCreateContinueOnErrorOption);
        batchCreateCommand.AddOption(batchCreateXdeltaFallbackOption);
        batchCreateCommand.AddOption(batchCreateXdeltaOption);
        batchCreateCommand.SetHandler(async (original, modified, outDir, cacheDir, continueOnError, xdeltaFallback, xdeltaOutput) =>
        {
            if (xdeltaFallback && xdeltaOutput)
            {
                WriteErrorJsonOrText("patch batch create", "--xdelta and --xdelta-fallback are mutually exclusive.");
                Environment.ExitCode = 1;
                return;
            }
            var result = await BatchPatchService.CreateBatchAsync(
                modified.Select(p => p.FullName).ToArray(),
                new BatchOptions
                {
                    OriginalPath = original.FullName,
                    OutDir = outDir.FullName,
                    CacheOptions = G3MCacheOptions.FromDirectory(cacheDir?.FullName),
                    ContinueOnError = continueOnError,
                    IncludeXdeltaFallback = xdeltaFallback,
                    CreateXdelta = xdeltaOutput
                });
            WriteBatchResult("patch batch create", result);
            if (!result.Success)
                Environment.ExitCode = 1;
        }, batchCreateOriginalArg, batchCreateModifiedArg, batchCreateOutDirOption, batchCreateCacheOption, batchCreateContinueOnErrorOption, batchCreateXdeltaFallbackOption, batchCreateXdeltaOption);

        var batchMergeCommand = new Command("merge",
            "Run multiple independent patch merges. Each set is a quoted comma-separated patch list.\n" +
            "  Usage: patch batch merge <original> <sets...> [--apply <data-dir>] [--out <patch-dir>] [--cache <dir>] [--continue-on-error] [--code] [--properties] [--report]\n" +
            "  Example: patch batch merge game.win \"low.g3mpatch,high.xdelta\" \"a.win,b.xdelta,c.g3mpatch\" --apply data --out patches");
        var batchMergeOriginalArg = new Argument<FileInfo>("original", "Path to original data file (.win/.ios/.droid/.unx)");
        var batchMergeSetsArg = new Argument<string[]>("sets", "Merge sets. Each set is comma-separated, low → high priority.")
        {
            Arity = new ArgumentArity(1, 1000)
        };
        var batchMergeCodeOption = new Option<bool>(
            name: "--code",
            description: "Enable Git-style 3-way merge for GML code files in every set.");
        var batchMergePropertiesOption = new Option<bool>(
            name: "--properties",
            description: "Enable deep merge for JSON property files in every set.");
        var batchMergeReportOption = new Option<bool>(
            name: "--report",
            description: "Write a merge report next to each merged .g3mpatch.");
        var batchMergeOutOption = new Option<DirectoryInfo?>(
            name: "--out",
            description: "Also save each merged .g3mpatch to this directory.");
        var batchMergeApplyOption = new Option<DirectoryInfo?>(
            name: "--apply",
            description: "Write data outputs to this directory. Defaults to the current directory.");
        var batchMergeCacheOption = BatchCacheOption();
        var batchMergeContinueOnErrorOption = ContinueOnErrorOption();
        batchMergeCommand.AddArgument(batchMergeOriginalArg);
        batchMergeCommand.AddArgument(batchMergeSetsArg);
        batchMergeCommand.AddOption(batchMergeOutOption);
        batchMergeCommand.AddOption(batchMergeApplyOption);
        batchMergeCommand.AddOption(batchMergeCacheOption);
        batchMergeCommand.AddOption(batchMergeContinueOnErrorOption);
        batchMergeCommand.AddOption(batchMergeCodeOption);
        batchMergeCommand.AddOption(batchMergePropertiesOption);
        batchMergeCommand.AddOption(batchMergeReportOption);
        batchMergeCommand.SetHandler(async (InvocationContext context) =>
        {
            var original = context.ParseResult.GetValueForArgument(batchMergeOriginalArg);
            var sets = context.ParseResult.GetValueForArgument(batchMergeSetsArg);
            var applyDir = context.ParseResult.GetValueForOption(batchMergeApplyOption);
            var outDir = context.ParseResult.GetValueForOption(batchMergeOutOption);
            var cacheDir = context.ParseResult.GetValueForOption(batchMergeCacheOption);
            var continueOnError = context.ParseResult.GetValueForOption(batchMergeContinueOnErrorOption);
            var code = context.ParseResult.GetValueForOption(batchMergeCodeOption);
            var properties = context.ParseResult.GetValueForOption(batchMergePropertiesOption);
            var report = context.ParseResult.GetValueForOption(batchMergeReportOption);
            var result = await BatchPatchService.MergeBatchAsync(
                sets,
                new BatchOptions
                {
                    OriginalPath = original.FullName,
                    OutDir = outDir?.FullName,
                    ApplyDir = applyDir?.FullName,
                    CacheOptions = G3MCacheOptions.FromDirectory(cacheDir?.FullName),
                    ContinueOnError = continueOnError,
                    UseCodeMerge = code,
                    UsePropertyMerge = properties,
                    WriteReports = report
                });
            WriteBatchResult("patch batch merge", result);
            if (!result.Success)
                Environment.ExitCode = 1;
        });

        batchCommand.AddCommand(batchApplyCommand);
        batchCommand.AddCommand(batchCreateCommand);
        batchCommand.AddCommand(batchMergeCommand);

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
        var mergeSequentialOption = new Option<bool>(
            name: "--sequential",
            description: "Use the sequential low-memory merge pipeline (does not support --code or --properties)");

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
        mergeCommand.AddOption(mergeSequentialOption);
        mergeCommand.AddOption(mergeReportOption);
        mergeCommand.AddOption(mergeCacheOption);

        mergeCommand.SetHandler(async (InvocationContext context) =>
        {
            var parseResult = context.ParseResult;
            var original = parseResult.GetValueForArgument(mergeOriginalArg);
            var patches = parseResult.GetValueForArgument(mergePatchesArg);
            var outPath = parseResult.GetValueForOption(mergeOutOption);
            var applyPath = parseResult.GetValueForOption(mergeApplyOption);
            var code = parseResult.GetValueForOption(mergeCodeOption);
            var properties = parseResult.GetValueForOption(mergePropertiesOption);
            var sequential = parseResult.GetValueForOption(mergeSequentialOption);
            var conflictsLog = parseResult.GetValueForOption(mergeReportOption);
            var cacheDir = parseResult.GetValueForOption(mergeCacheOption);
            var patchPaths = patches.Select(p => p.FullName).ToList();
            var options = new MergeOptions
            {
                OutputPath = outPath,
                ApplyPath = applyPath,
                UseCodeMerge = code,
                UsePropertyMerge = properties,
                UseSequentialMerge = sequential,
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
        });

        command.AddCommand(createCommand);
        command.AddCommand(applyCommand);
        command.AddCommand(validateCommand);
        command.AddCommand(batchCommand);
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

    private static void WriteBatchResult(string command, BatchResult result)
    {
        if (Program.JsonOutput)
        {
            WriteJson(new
            {
                success = result.Success,
                command,
                total = result.Total,
                completed = result.Completed,
                failed = result.Failed,
                deduplicated = result.Deduplicated,
                items = result.Items.Select(item => new
                {
                    index = item.Index,
                    kind = item.Kind,
                    inputs = item.Inputs,
                    outputs = item.Outputs,
                    success = item.Success,
                    deduplicated = item.Deduplicated,
                    error = item.Error,
                    seconds = item.Seconds
                })
            });
            return;
        }

        Console.WriteLine($"Batch complete: {result.Completed}/{result.Total} succeeded, {result.Failed} failed, {result.Deduplicated} deduplicated");
        foreach (var item in result.Items)
        {
            var status = item.Success ? "OK" : "FAIL";
            var dedup = item.Deduplicated ? " dedup" : "";
            Console.WriteLine($"  [{item.Index}] {item.Kind} {status}{dedup} ({item.Seconds:F1}s)");
            foreach (var output in item.Outputs)
                Console.WriteLine($"      {output}");
            if (!item.Success && !string.IsNullOrWhiteSpace(item.Error))
                Console.WriteLine($"      error: {item.Error}");
        }
    }
}
