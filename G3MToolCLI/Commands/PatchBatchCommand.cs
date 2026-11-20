using System.CommandLine;
using G3MToolCLI.Services.Patching;

namespace G3MToolCLI.Commands;

internal static class PatchBatchCommand
{
    public static Command Create()
    {
        return new Command("batch", "Run multiple patch operations with hash-based job deduplication.")
        {
            CreateApply(),
            CreateCreate(),
            CreateMerge()
        };
    }

    private static Command CreateApply()
    {
        var command = new Command("apply", "Apply each patch independently to the same original data file.\n  Usage: patch batch apply <original> <patches...> --out-dir <dir> [--cache <dir>] [--continue-on-error] [--xdelta-fallback]");
        var originalArg = new Argument<FileInfo>("original") { Description = "Path to original data file (.win/.ios/.droid/.unx)" };
        var patchesArg = new Argument<FileInfo[]>("patches")
        {
            Description = "Patch files (.g3mpatch, .xdelta, or data files)",
            Arity = new ArgumentArity(1, 1000)
        };
        var xdeltaFallbackOption = new Option<bool>("--xdelta-fallback") { Description = "Try embedded xdelta fallback when applying .g3mpatch files." };
        var outDirOption = OutDirOption();
        var cacheOption = CacheOption();
        var continueOnErrorOption = ContinueOnErrorOption();
        command.Add(originalArg);
        command.Add(patchesArg);
        command.Add(outDirOption);
        command.Add(cacheOption);
        command.Add(continueOnErrorOption);
        command.Add(xdeltaFallbackOption);
        command.SetAction(async parseResult =>
        {
            var patches = parseResult.GetValue(patchesArg) ?? [];
            var result = await BatchPatchService.ApplyBatchAsync([.. patches.Select(p => p.FullName)], new BatchOptions
            {
                OriginalPath = parseResult.GetRequiredValue(originalArg).FullName,
                OutDir = parseResult.GetRequiredValue(outDirOption).FullName,
                CacheOptions = G3MCacheOptions.FromDirectory(parseResult.GetValue(cacheOption)?.FullName),
                ContinueOnError = parseResult.GetValue(continueOnErrorOption),
                CancellationToken = Program.Cancellation,
                XdeltaFallback = parseResult.GetValue(xdeltaFallbackOption)
            });
            WriteBatchResult("patch batch apply", result);
        });
        return command;
    }

    private static Command CreateCreate()
    {
        var command = new Command("create", "Create one .g3mpatch or xdelta patch for each input against the same original data file.\n  Usage: patch batch create <original> <modified...> --out-dir <dir> [--xdelta] [--cache <dir>] [--continue-on-error] [--xdelta-fallback]");
        var originalArg = new Argument<FileInfo>("original") { Description = "Path to original data file (.win/.ios/.droid/.unx)" };
        var modifiedArg = new Argument<FileInfo[]>("modified")
        {
            Description = "Data files, .g3mpatch, .xdelta, .vcdiff, or .csx inputs",
            Arity = new ArgumentArity(1, 1000)
        };
        var xdeltaFallbackOption = new Option<bool>("--xdelta-fallback") { Description = "Store xdelta fallback in created .g3mpatch files." };
        var xdeltaOption = new Option<bool>("--xdelta") { Description = "Create xdelta patches instead of .g3mpatch files." };
        var outDirOption = OutDirOption();
        var cacheOption = CacheOption();
        var continueOnErrorOption = ContinueOnErrorOption();
        command.Add(originalArg);
        command.Add(modifiedArg);
        command.Add(outDirOption);
        command.Add(cacheOption);
        command.Add(continueOnErrorOption);
        command.Add(xdeltaFallbackOption);
        command.Add(xdeltaOption);
        command.SetAction(async parseResult =>
        {
            var xdeltaFallback = parseResult.GetValue(xdeltaFallbackOption);
            var xdeltaOutput = parseResult.GetValue(xdeltaOption);
            if (xdeltaFallback && xdeltaOutput)
            {
                PatchCommand.WriteErrorJsonOrText("patch batch create", "--xdelta and --xdelta-fallback are mutually exclusive.");
                Environment.ExitCode = 1;
                return;
            }
            var modified = parseResult.GetValue(modifiedArg) ?? [];
            var result = await BatchPatchService.CreateBatchAsync([.. modified.Select(p => p.FullName)], new BatchOptions
            {
                OriginalPath = parseResult.GetRequiredValue(originalArg).FullName,
                OutDir = parseResult.GetRequiredValue(outDirOption).FullName,
                CacheOptions = G3MCacheOptions.FromDirectory(parseResult.GetValue(cacheOption)?.FullName),
                ContinueOnError = parseResult.GetValue(continueOnErrorOption),
                CancellationToken = Program.Cancellation,
                IncludeXdeltaFallback = xdeltaFallback,
                CreateXdelta = xdeltaOutput
            });
            WriteBatchResult("patch batch create", result);
        });
        return command;
    }

    private static Command CreateMerge()
    {
        var command = new Command("merge", "Run multiple independent patch merges. Each set is a quoted comma-separated patch list.\n  Usage: patch batch merge <original> <sets...> [--apply <data-dir>] [--out <patch-dir>] [--cache <dir>] [--continue-on-error] [--code] [--properties] [--report]\n  Example: patch batch merge game.win \"low.g3mpatch,high.xdelta\" \"a.win,b.xdelta,c.g3mpatch\" --apply data --out patches");
        var originalArg = new Argument<FileInfo>("original") { Description = "Path to original data file (.win/.ios/.droid/.unx)" };
        var setsArg = new Argument<string[]>("sets")
        {
            Description = "Merge sets. Each set is comma-separated, low → high priority.",
            Arity = new ArgumentArity(1, 1000)
        };
        var codeOption = new Option<bool>("--code") { Description = "Enable Git-style 3-way merge for GML code files in every set." };
        var propertiesOption = new Option<bool>("--properties") { Description = "Enable deep merge for JSON property files in every set." };
        var reportOption = new Option<bool>("--report") { Description = "Write a merge report next to each merged .g3mpatch." };
        var outOption = new Option<DirectoryInfo?>("--out") { Description = "Also save each merged .g3mpatch to this directory." };
        var applyOption = new Option<DirectoryInfo?>("--apply") { Description = "Write data outputs to this directory. Defaults to the current directory." };
        var cacheOption = CacheOption();
        var continueOnErrorOption = ContinueOnErrorOption();
        command.Add(originalArg);
        command.Add(setsArg);
        command.Add(outOption);
        command.Add(applyOption);
        command.Add(cacheOption);
        command.Add(continueOnErrorOption);
        command.Add(codeOption);
        command.Add(propertiesOption);
        command.Add(reportOption);
        command.SetAction(async parseResult =>
        {
            var result = await BatchPatchService.MergeBatchAsync(parseResult.GetValue(setsArg) ?? [], new BatchOptions
            {
                OriginalPath = parseResult.GetRequiredValue(originalArg).FullName,
                OutDir = parseResult.GetValue(outOption)?.FullName,
                ApplyDir = parseResult.GetValue(applyOption)?.FullName,
                CacheOptions = G3MCacheOptions.FromDirectory(parseResult.GetValue(cacheOption)?.FullName),
                ContinueOnError = parseResult.GetValue(continueOnErrorOption),
                CancellationToken = Program.Cancellation,
                UseCodeMerge = parseResult.GetValue(codeOption),
                UsePropertyMerge = parseResult.GetValue(propertiesOption),
                WriteReports = parseResult.GetValue(reportOption)
            });
            WriteBatchResult("patch batch merge", result);
        });
        return command;
    }

    private static Option<DirectoryInfo?> CacheOption() => new("--cache") { Description = "Read and write reusable .g3mcache analysis files in this directory." };

    private static Option<DirectoryInfo> OutDirOption() => new("--out-dir")
    {
        Description = "Directory where batch outputs are written.",
        Required = true
    };

    private static Option<bool> ContinueOnErrorOption() => new("--continue-on-error") { Description = "Continue remaining batch jobs after a failure." };

    private static void WriteBatchResult(string command, BatchResult result)
    {
        if (!result.Success)
        {
            Environment.ExitCode = 1;
        }
        if (Program.JsonOutput)
        {
            PatchCommand.WriteJson(new
            {
                success = result.Success,
                command,
                total = result.Total,
                completed = result.Completed,
                failed = result.Failed,
                deduplicated = result.Deduplicated,
                items = result.Items.Select(batchItemResult => new
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
        foreach (var item in result.Items)
        {
            var status = item.Success ? "OK" : "FAIL";
            var dedup = item.Deduplicated ? " dedup" : "";
            Console.WriteLine($"  [{item.Index}] {item.Kind} {status}{dedup} ({item.Seconds:F1}s)");
            foreach (var output in item.Outputs)
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
