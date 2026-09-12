using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Text.Json;
using G3MToolCLI.Models.Scripting;
using G3MToolCLI.Services.Logging;
using G3MToolCLI.Services.Patching;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

public static class PatchCommand
{

    private static readonly JsonSerializerOptions s_compactJsonOptions = new()
    {
        WriteIndented = false
    };
    private static readonly string[] s_mergeConflictWarnings = ["merge completed with conflicts; inspect the merge report if one was requested"];

    public static Command Create()
    {
        Command command = new Command("patch", "Create, apply, validate, or merge .g3mpatch files.");
        Command createCommand = new Command("create", "Create a .g3mpatch or xdelta patch from an original data file and a supported input.\n  Usage: patch create <original> <input> [output] [--xdelta] [--xdelta-fallback] [--cache <dir>] [--xdelta-path <path>]");
        Argument<FileInfo> originalArg = new Argument<FileInfo>("original") { Description = "Path to original data file (.win/.ios/.droid/.unx)" };
        Argument<FileInfo> modifiedArg = new Argument<FileInfo>("modified") { Description = "Data file, .g3mpatch, .xdelta, .vcdiff, or .csx input" };
        Argument<FileInfo?> outputArg = new Argument<FileInfo?>("output") { DefaultValueFactory = _ => null, Description = "Output patch file (optional). Default: next to the executable" };
        Option<bool> xdeltaFallbackOption = new Option<bool>("--xdelta-fallback") { Description = "Store an xdelta fallback. Disabled by default to keep .g3mpatch smaller." };
        Option<bool> createXdeltaOption = new Option<bool>("--xdelta") { Description = "Create an xdelta patch instead of a .g3mpatch." };
        Option<DirectoryInfo?> createCacheOption = new Option<DirectoryInfo?>("--cache") { Description = "Read and write reusable .g3mcache analysis files in this directory." };
        createCommand.Add(originalArg);
        createCommand.Add(modifiedArg);
        createCommand.Add(outputArg);
        createCommand.Add(xdeltaFallbackOption);
        createCommand.Add(createXdeltaOption);
        createCommand.Add(createCacheOption);
        createCommand.SetAction(async parseResult =>
        {
            FileInfo original = parseResult.GetValue(originalArg)!;
            FileInfo modified = parseResult.GetValue(modifiedArg)!;
            FileInfo? output = parseResult.GetValue(outputArg);
            bool xdeltaFallback = parseResult.GetValue(xdeltaFallbackOption);
            bool xdeltaOutput = parseResult.GetValue(createXdeltaOption);
            DirectoryInfo? cacheDir = parseResult.GetValue(createCacheOption);
            if (xdeltaFallback && xdeltaOutput)
            {
                WriteErrorJsonOrText("patch create", "--xdelta and --xdelta-fallback are mutually exclusive.");
                Environment.ExitCode = 1;
                return;
            }
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string defaultOutput = Path.Combine(PlatformUtil.GetExecutableDirectory(), "patch_" + timestamp + (xdeltaOutput ? ".xdelta" : ".g3mpatch"));
            string outputPath = output?.FullName ?? defaultOutput;
            string tempDir = Path.Combine(Path.GetTempPath(), $"g3mtool_create_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string materialized;
            try
            {
                materialized = await ScriptPatchInputService.MaterializeDataAsync(original.FullName, modified.FullName, tempDir);
            }
            catch (Exception ex)
            {
                WriteErrorJsonOrText("patch create", ex.Message);
                Environment.ExitCode = 1;
                DeleteTemporaryDirectory(tempDir);
                return;
            }
            LogService.Log("Creating G3M patch...");
            LogService.Log("  Original: " + original.FullName);
            LogService.Log("  Modified: " + modified.FullName);
            LogService.Log("  Output:   " + outputPath);
            LogService.Log("  Xdelta fallback: " + (xdeltaFallback ? "enabled" : "disabled"));
            if (cacheDir != null)
            {
                LogService.Log("  Cache:    " + cacheDir.FullName);
            }
            if (xdeltaOutput)
            {
                XDeltaResult xresult = await new XDeltaService().CreatePatchAsync(original.FullName, materialized, outputPath);
                if (!xresult.Success)
                {
                    WriteErrorJsonOrText("patch create", xresult.Error);
                    Environment.ExitCode = 1;
                }
                else
                {
                    WriteSuccessJsonOrText("patch create", outputPath, new
                    {
                        format = "xdelta"
                    });
                }
                DeleteTemporaryDirectory(tempDir);
                return;
            }
            PatchCreateResult result = await PatchService.CreatePatchAsync(original.FullName, materialized, outputPath, null, null, null, null, null, xdeltaFallback, G3MCacheOptions.FromDirectory(cacheDir?.FullName));
            if (result.Success)
            {
                PatchStatistics? s = result.Statistics;
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
                    Console.WriteLine("Patch created successfully: " + outputPath);
                    if (s != null)
                    {
                        Console.WriteLine((s.TotalChangedFiles > 0) ? $"  Changed: {s.TotalChanged} ({s.TotalChangedFiles} files)" : $"  Changed: {s.TotalChanged}");
                        Console.WriteLine((s.TotalNewFiles > 0) ? $"  New:     {s.TotalNew} ({s.TotalNewFiles} files)" : $"  New:     {s.TotalNew}");
                        Console.WriteLine($"  Deleted: {s.TotalDeleted}");
                    }
                }
            }
            else
            {
                WriteErrorJsonOrText("patch create", result.Error);
                Environment.ExitCode = 1;
            }
            DeleteTemporaryDirectory(tempDir);
        });
        Command applyCommand = new Command("apply", "Apply a .g3mpatch to a data file. .xdelta input is applied directly; data-file input is converted first.\n  Usage: patch apply <data> <patch> [output] [--xdelta-fallback] [--cache <dir>] [--xdelta-path <path>]");
        Argument<FileInfo> dataArg = new Argument<FileInfo>("data") { Description = "Path to original data file (.win/.ios/.droid/.unx)" };
        Argument<FileInfo> patchArg = new Argument<FileInfo>("patch") { Description = "Path to patch file (.g3mpatch, .xdelta, or data file)" };
        Argument<FileInfo?> applyOutputArg = new Argument<FileInfo?>("output") { DefaultValueFactory = _ => null, Description = "Output file (optional). Default: next to the executable" };
        Option<bool> applyXdeltaFallbackOption = new Option<bool>("--xdelta-fallback") { Description = "Try the embedded xdelta copy first. If it fails, continue with normal .g3mpatch apply." };
        Option<DirectoryInfo?> applyCacheOption = new Option<DirectoryInfo?>("--cache") { Description = "Read and write reusable .g3mcache analysis files when converting data-file or xdelta input." };
        applyCommand.Add(dataArg);
        applyCommand.Add(patchArg);
        applyCommand.Add(applyOutputArg);
        applyCommand.Add(applyXdeltaFallbackOption);
        applyCommand.Add(applyCacheOption);
        applyCommand.SetAction(async parseResult =>
        {
            FileInfo data = parseResult.GetValue(dataArg)!;
            FileInfo patch = parseResult.GetValue(patchArg)!;
            FileInfo? output = parseResult.GetValue(applyOutputArg);
            bool xdeltaFallback = parseResult.GetValue(applyXdeltaFallbackOption);
            DirectoryInfo? cacheDir = parseResult.GetValue(applyCacheOption);
            string defaultOutput = Path.Combine(PlatformUtil.GetExecutableDirectory(), Path.GetFileName(data.FullName));
            string outputPath = output?.FullName ?? defaultOutput;
            if (ScriptPatchInputService.IsXDelta(patch.FullName) || Path.GetExtension(patch.FullName).Equals(".csx", StringComparison.OrdinalIgnoreCase))
            {
                string temporaryDirectory = Directory.CreateTempSubdirectory("g3mtool-apply-").FullName;
                try
                {
                    string materialized = await ScriptPatchInputService.MaterializeDataAsync(data.FullName, patch.FullName, temporaryDirectory);
                    PatchInputService.CopyDataFile(materialized, outputPath);
                    WriteSuccessJsonOrText("patch apply", outputPath, new { inputKind = ScriptPatchInputService.IsXDelta(patch.FullName) ? "xdelta" : "csx", data = data.FullName, patch = patch.FullName });
                }
                catch (Exception exception)
                {
                    WriteErrorJsonOrText("patch apply", exception.Message);
                    Environment.ExitCode = 1;
                }
                finally { Directory.Delete(temporaryDirectory, recursive: true); }
            }
            else
            {
                string patchPath;
                string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"g3mtool_apply_{Guid.NewGuid():N}");
                try
                {
                    Directory.CreateDirectory(temporaryDirectory);
                    patchPath = await PatchService.EnsureG3MPatchAsync(data.FullName, patch.FullName, temporaryDirectory, cacheOptions: G3MCacheOptions.FromDirectory(cacheDir?.FullName));
                    LogService.Log("Applying G3M patch...");
                    LogService.Log("  Data:   " + data.FullName);
                    LogService.Log("  Patch:  " + patchPath);
                    LogService.Log("  Output: " + outputPath);
                    LogService.Log("  Xdelta fallback: " + (xdeltaFallback ? "enabled" : "disabled"));
                    if (cacheDir != null)
                    {
                        LogService.Log("  Cache:  " + cacheDir.FullName);
                    }
                    PatchApplyResult result = await PatchService.ApplyPatchAsync(data.FullName, patchPath, outputPath, xdeltaFallback);
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
                }
                catch (Exception ex)
                {
                    WriteErrorJsonOrText("patch apply", ex.Message);
                    Environment.ExitCode = 1;
                }
                finally
                {
                    if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
        });
        Command validateCommand = new Command("validate", "Validate a G3M patch file and optionally check compatibility with a data file.\n  Usage: patch validate <patch> [--data <data-file>] [--cache <dir>]");
        Argument<FileInfo> validatePatchArg = new Argument<FileInfo>("patch") { Description = "Path to G3M patch file (.g3mpatch)" };
        Option<FileInfo?> validateDataOption = new Option<FileInfo?>("--data", ["-d"]) { Description = "Optional data file (.win/.ios/.droid/.unx) to check compatibility" };
        Option<DirectoryInfo?> validateCacheOption = new Option<DirectoryInfo?>("--cache") { Description = "Read reusable .g3mcache analysis when checking --data." };
        validateCommand.Add(validatePatchArg);
        validateCommand.Add(validateDataOption);
        validateCommand.Add(validateCacheOption);
        validateCommand.SetAction(async parseResult =>
        {
            FileInfo patch = parseResult.GetValue(validatePatchArg)!;
            FileInfo? data = parseResult.GetValue(validateDataOption);
            DirectoryInfo? cacheDir = parseResult.GetValue(validateCacheOption);
            if (!Program.JsonOutput)
            {
                Console.WriteLine("Validating G3M patch: " + patch.FullName);
            }
            PatchValidateResult result = await PatchService.ValidatePatchAsync(patch.FullName, data?.FullName, G3MCacheOptions.FromDirectory(cacheDir?.FullName));
            if (result.Success)
            {
                if (Program.JsonOutput)
                {
                    G3MPatchManifest? manifest = result.Manifest;
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
                        resourceTypes = manifest?.Resources?.Where((KeyValuePair<string, ResourceTypeChanges> kvp) => (kvp.Value.Changed?.Count ?? 0) > 0 || (kvp.Value.New?.Count ?? 0) > 0 || (kvp.Value.Deleted?.Count ?? 0) > 0).ToDictionary((KeyValuePair<string, ResourceTypeChanges> kvp) => kvp.Key, (KeyValuePair<string, ResourceTypeChanges> kvp) => new
                        {
                            changed = (kvp.Value.Changed?.Count ?? 0),
                            @new = (kvp.Value.New?.Count ?? 0),
                            deleted = (kvp.Value.Deleted?.Count ?? 0)
                        }, StringComparer.OrdinalIgnoreCase),
                        warnings = Array.Empty<string>()
                    });
                }
                else
                {
                    Console.WriteLine("Patch is valid.");
                    if (result.Manifest != null)
                    {
                        Console.WriteLine("  Tool: " + result.Manifest.Tool?.Name + " v" + result.Manifest.Tool?.Version);
                        Console.WriteLine("  Created: " + result.Manifest.CreatedAt);
                        Console.WriteLine($"  Resources: {result.Manifest.Statistics?.TotalChanged ?? 0} changed, {result.Manifest.Statistics?.TotalNew ?? 0} new, {result.Manifest.Statistics?.TotalDeleted ?? 0} deleted");
                    }
                }
            }
            else
            {
                WriteErrorJsonOrText("patch validate", result.Error);
                Environment.ExitCode = 1;
            }
        });
        Command batchCommand = new Command("batch", "Run multiple patch operations with hash-based job deduplication.");
        Command batchApplyCommand = new Command("apply", "Apply each patch independently to the same original data file.\n  Usage: patch batch apply <original> <patches...> --out-dir <dir> [--cache <dir>] [--continue-on-error] [--xdelta-fallback]");
        Argument<FileInfo> batchApplyOriginalArg = new Argument<FileInfo>("original") { Description = "Path to original data file (.win/.ios/.droid/.unx)" };
        Argument<FileInfo[]> batchApplyPatchesArg = new Argument<FileInfo[]>("patches")
        {
            Description = "Patch files (.g3mpatch, .xdelta, or data files)",
            Arity = new ArgumentArity(1, 1000)
        };
        Option<bool> batchApplyXdeltaFallbackOption = new Option<bool>("--xdelta-fallback") { Description = "Try embedded xdelta fallback when applying .g3mpatch files." };
        Option<DirectoryInfo> batchApplyOutDirOption = BatchOutDirOption();
        Option<DirectoryInfo?> batchApplyCacheOption = BatchCacheOption();
        Option<bool> batchApplyContinueOnErrorOption = ContinueOnErrorOption();
        batchApplyCommand.Add(batchApplyOriginalArg);
        batchApplyCommand.Add(batchApplyPatchesArg);
        batchApplyCommand.Add(batchApplyOutDirOption);
        batchApplyCommand.Add(batchApplyCacheOption);
        batchApplyCommand.Add(batchApplyContinueOnErrorOption);
        batchApplyCommand.Add(batchApplyXdeltaFallbackOption);
        batchApplyCommand.SetAction(async parseResult =>
        {
            FileInfo original = parseResult.GetValue(batchApplyOriginalArg)!;
            FileInfo[] patches = parseResult.GetValue(batchApplyPatchesArg) ?? [];
            DirectoryInfo outDir = parseResult.GetValue(batchApplyOutDirOption)!;
            DirectoryInfo? cacheDir = parseResult.GetValue(batchApplyCacheOption);
            bool continueOnError = parseResult.GetValue(batchApplyContinueOnErrorOption);
            bool xdeltaFallback = parseResult.GetValue(batchApplyXdeltaFallbackOption);
            BatchResult result = await BatchPatchService.ApplyBatchAsync(patches.Select((FileInfo p) => p.FullName).ToArray(), new BatchOptions
            {
                OriginalPath = original.FullName,
                OutDir = outDir.FullName,
                CacheOptions = G3MCacheOptions.FromDirectory(cacheDir?.FullName),
                ContinueOnError = continueOnError,
                XdeltaFallback = xdeltaFallback
            });
            WriteBatchResult("patch batch apply", result);
            if (!result.Success)
            {
                Environment.ExitCode = 1;
            }
        });
        Command batchCreateCommand = new Command("create", "Create one .g3mpatch or xdelta patch for each input against the same original data file.\n  Usage: patch batch create <original> <modified...> --out-dir <dir> [--xdelta] [--cache <dir>] [--continue-on-error] [--xdelta-fallback]");
        Argument<FileInfo> batchCreateOriginalArg = new Argument<FileInfo>("original") { Description = "Path to original data file (.win/.ios/.droid/.unx)" };
        Argument<FileInfo[]> batchCreateModifiedArg = new Argument<FileInfo[]>("modified")
        {
            Description = "Data files, .g3mpatch, .xdelta, .vcdiff, or .csx inputs",
            Arity = new ArgumentArity(1, 1000)
        };
        Option<bool> batchCreateXdeltaFallbackOption = new Option<bool>("--xdelta-fallback") { Description = "Store xdelta fallback in created .g3mpatch files." };
        Option<bool> batchCreateXdeltaOption = new Option<bool>("--xdelta") { Description = "Create xdelta patches instead of .g3mpatch files." };
        Option<DirectoryInfo> batchCreateOutDirOption = BatchOutDirOption();
        Option<DirectoryInfo?> batchCreateCacheOption = BatchCacheOption();
        Option<bool> batchCreateContinueOnErrorOption = ContinueOnErrorOption();
        batchCreateCommand.Add(batchCreateOriginalArg);
        batchCreateCommand.Add(batchCreateModifiedArg);
        batchCreateCommand.Add(batchCreateOutDirOption);
        batchCreateCommand.Add(batchCreateCacheOption);
        batchCreateCommand.Add(batchCreateContinueOnErrorOption);
        batchCreateCommand.Add(batchCreateXdeltaFallbackOption);
        batchCreateCommand.Add(batchCreateXdeltaOption);
        batchCreateCommand.SetAction(async parseResult =>
        {
            FileInfo original = parseResult.GetValue(batchCreateOriginalArg)!;
            FileInfo[] modified = parseResult.GetValue(batchCreateModifiedArg) ?? [];
            DirectoryInfo outDir = parseResult.GetValue(batchCreateOutDirOption)!;
            DirectoryInfo? cacheDir = parseResult.GetValue(batchCreateCacheOption);
            bool continueOnError = parseResult.GetValue(batchCreateContinueOnErrorOption);
            bool xdeltaFallback = parseResult.GetValue(batchCreateXdeltaFallbackOption);
            bool xdeltaOutput = parseResult.GetValue(batchCreateXdeltaOption);
            if (xdeltaFallback && xdeltaOutput)
            {
                WriteErrorJsonOrText("patch batch create", "--xdelta and --xdelta-fallback are mutually exclusive.");
                Environment.ExitCode = 1;
            }
            else
            {
                BatchResult result = await BatchPatchService.CreateBatchAsync(modified.Select((FileInfo p) => p.FullName).ToArray(), new BatchOptions
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
                {
                    Environment.ExitCode = 1;
                }
            }
        });
        Command batchMergeCommand = new Command("merge", "Run multiple independent patch merges. Each set is a quoted comma-separated patch list.\n  Usage: patch batch merge <original> <sets...> [--apply <data-dir>] [--out <patch-dir>] [--cache <dir>] [--continue-on-error] [--code] [--properties] [--report]\n  Example: patch batch merge game.win \"low.g3mpatch,high.xdelta\" \"a.win,b.xdelta,c.g3mpatch\" --apply data --out patches");
        Argument<FileInfo> batchMergeOriginalArg = new Argument<FileInfo>("original") { Description = "Path to original data file (.win/.ios/.droid/.unx)" };
        Argument<string[]> batchMergeSetsArg = new Argument<string[]>("sets")
        {
            Description = "Merge sets. Each set is comma-separated, low → high priority.",
            Arity = new ArgumentArity(1, 1000)
        };
        Option<bool> batchMergeCodeOption = new Option<bool>("--code") { Description = "Enable Git-style 3-way merge for GML code files in every set." };
        Option<bool> batchMergePropertiesOption = new Option<bool>("--properties") { Description = "Enable deep merge for JSON property files in every set." };
        Option<bool> batchMergeReportOption = new Option<bool>("--report") { Description = "Write a merge report next to each merged .g3mpatch." };
        Option<DirectoryInfo?> batchMergeOutOption = new Option<DirectoryInfo?>("--out") { Description = "Also save each merged .g3mpatch to this directory." };
        Option<DirectoryInfo?> batchMergeApplyOption = new Option<DirectoryInfo?>("--apply") { Description = "Write data outputs to this directory. Defaults to the current directory." };
        Option<DirectoryInfo?> batchMergeCacheOption = BatchCacheOption();
        Option<bool> batchMergeContinueOnErrorOption = ContinueOnErrorOption();
        batchMergeCommand.Add(batchMergeOriginalArg);
        batchMergeCommand.Add(batchMergeSetsArg);
        batchMergeCommand.Add(batchMergeOutOption);
        batchMergeCommand.Add(batchMergeApplyOption);
        batchMergeCommand.Add(batchMergeCacheOption);
        batchMergeCommand.Add(batchMergeContinueOnErrorOption);
        batchMergeCommand.Add(batchMergeCodeOption);
        batchMergeCommand.Add(batchMergePropertiesOption);
        batchMergeCommand.Add(batchMergeReportOption);
        batchMergeCommand.SetAction(async parseResult =>
        {
            FileInfo original = parseResult.GetValue(batchMergeOriginalArg)!;
            string[] valueForArgument = parseResult.GetValue(batchMergeSetsArg) ?? [];
            DirectoryInfo? applyDir = parseResult.GetValue(batchMergeApplyOption);
            DirectoryInfo? outDir = parseResult.GetValue(batchMergeOutOption);
            DirectoryInfo? cacheDir = parseResult.GetValue(batchMergeCacheOption);
            bool continueOnError = parseResult.GetValue(batchMergeContinueOnErrorOption);
            bool code = parseResult.GetValue(batchMergeCodeOption);
            bool properties = parseResult.GetValue(batchMergePropertiesOption);
            bool report = parseResult.GetValue(batchMergeReportOption);
            BatchResult result = await BatchPatchService.MergeBatchAsync(valueForArgument, new BatchOptions
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
            {
                Environment.ExitCode = 1;
            }
        });
        batchCommand.Add(batchApplyCommand);
        batchCommand.Add(batchCreateCommand);
        batchCommand.Add(batchMergeCommand);
        Command mergeCommand = new Command("merge", "Merge multiple patches into one .g3mpatch.\n  The first argument is the original data file (required as context).\n  Subsequent arguments are patches (from lowest to highest priority).\n  Input can be .g3mpatch, .xdelta, or data file (.win/.ios/.droid/.unx).\n  Usage: patch merge <original> <patch1> <patch2> [patch3...] [flags] [--cache <dir>] [--xdelta-path <path>]");
        Argument<FileInfo> mergeOriginalArg = new Argument<FileInfo>("original") { Description = "Path to original data file (.win/.ios/.droid/.unx)" };
        Argument<FileInfo[]> mergePatchesArg = new Argument<FileInfo[]>("patches")
        {
            Description = "Patch files (low → high priority)",
            Arity = new ArgumentArity(2, 100)
        };
        Option<string?> mergeOutOption = new Option<string?>("--out", ["-o"]) { Description = "Output path for merged .g3mpatch (default if no flags specified)" };
        Option<string?> mergeApplyOption = new Option<string?>("--apply", ["-a"]) { Description = "Apply merged patch and save the resulting data file to this path" };
        Option<bool> mergeCodeOption = new Option<bool>("--code") { Description = "Enable Git-style 3-way merge for GML code files" };
        Option<bool> mergePropertiesOption = new Option<bool>("--properties") { Description = "Enable deep merge for JSON property files" };
        Option<bool> mergeSequentialOption = new Option<bool>("--sequential") { Description = "Use the sequential low-memory merge pipeline (does not support --code or --properties)" };
        Option<string?> mergeReportOption = new Option<string?>("--report", ["-r"]) { Description = "Path for the merge report (Markdown)" };
        Option<DirectoryInfo?> mergeCacheOption = new Option<DirectoryInfo?>("--cache") { Description = "Read and write reusable .g3mcache analysis files in this directory." };
        mergeCommand.Add(mergeOriginalArg);
        mergeCommand.Add(mergePatchesArg);
        mergeCommand.Add(mergeOutOption);
        mergeCommand.Add(mergeApplyOption);
        mergeCommand.Add(mergeCodeOption);
        mergeCommand.Add(mergePropertiesOption);
        mergeCommand.Add(mergeSequentialOption);
        mergeCommand.Add(mergeReportOption);
        mergeCommand.Add(mergeCacheOption);
        mergeCommand.SetAction(async parseResult =>
        {
            FileInfo original = parseResult.GetValue(mergeOriginalArg)!;
            FileInfo[] patches = parseResult.GetValue(mergePatchesArg) ?? [];
            string? outPath = parseResult.GetValue(mergeOutOption);
            string? applyPath = parseResult.GetValue(mergeApplyOption);
            bool code = parseResult.GetValue(mergeCodeOption);
            bool properties = parseResult.GetValue(mergePropertiesOption);
            bool sequential = parseResult.GetValue(mergeSequentialOption);
            string? conflictsLog = parseResult.GetValue(mergeReportOption);
            DirectoryInfo? cacheDir = parseResult.GetValue(mergeCacheOption);
            List<string> patchPaths = patches.Select((FileInfo p) => p.FullName).ToList();
            MergeOptions options = new MergeOptions
            {
                OutputPath = outPath,
                ApplyPath = applyPath,
                UseCodeMerge = code,
                UsePropertyMerge = properties,
                UseSequentialMerge = sequential,
                ReportPath = conflictsLog,
                CacheOptions = G3MCacheOptions.FromDirectory(cacheDir?.FullName)
            };
            MergeResult result = await ScriptPatchInputService.MergeAsync(original.FullName, patchPaths, options);
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
                    warnings = result.TotalConflicts <= 0 ? [] : s_mergeConflictWarnings
                });
            }
        });
        command.Add(createCommand);
        command.Add(applyCommand);
        command.Add(validateCommand);
        command.Add(batchCommand);
        command.Add(mergeCommand);
        return command;
        static Option<DirectoryInfo?> BatchCacheOption()
        {
            return new Option<DirectoryInfo?>("--cache") { Description = "Read and write reusable .g3mcache analysis files in this directory." };
        }
        static Option<DirectoryInfo> BatchOutDirOption()
        {
            return new Option<DirectoryInfo>("--out-dir")
            {
                Description = "Directory where batch outputs are written.",
                Required = true
            };
        }
        static Option<bool> ContinueOnErrorOption()
        {
            return new Option<bool>("--continue-on-error") { Description = "Continue remaining batch jobs after a failure." };
        }
    }

    private static void WriteSuccessJsonOrText<T>(string command, string outputPath, T details)
    {
        if (Program.JsonOutput)
        {
            WriteJson(new
            {
                success = true,
                command,
                output = outputPath,
                details,
                warnings = Array.Empty<string>()
            });
        }
        else
        {
            Console.WriteLine("Patch applied successfully: " + outputPath);
        }
    }

    private static void WriteErrorJsonOrText(string command, string? error)
    {
        if (Program.JsonOutput)
        {
            WriteJson(new
            {
                success = false,
                command,
                error
            });
        }
        else
        {
            Console.Error.WriteLine("Error: " + error);
        }
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void WriteJson<T>(T value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, s_compactJsonOptions));
    }

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
                items = result.Items.Select((BatchItemResult batchItemResult) => new
                {
                    index = batchItemResult.Index,
                    kind = batchItemResult.Kind,
                    inputs = batchItemResult.Inputs,
                    outputs = batchItemResult.Outputs,
                    success = batchItemResult.Success,
                    deduplicated = batchItemResult.Deduplicated,
                    error = batchItemResult.Error,
                    seconds = batchItemResult.Seconds
                })
            });
            return;
        }
        Console.WriteLine($"Batch complete: {result.Completed}/{result.Total} succeeded, {result.Failed} failed, {result.Deduplicated} deduplicated");
        foreach (BatchItemResult item in result.Items)
        {
            string status = (item.Success ? "OK" : "FAIL");
            string dedup = (item.Deduplicated ? " dedup" : "");
            Console.WriteLine($"  [{item.Index}] {item.Kind} {status}{dedup} ({item.Seconds:F1}s)");
            string[] outputs = item.Outputs;
            foreach (string output in outputs)
            {
                Console.WriteLine("      " + output);
            }
            if (!item.Success && !string.IsNullOrWhiteSpace(item.Error))
            {
                Console.WriteLine("      error: " + item.Error);
            }
        }
    }
}
