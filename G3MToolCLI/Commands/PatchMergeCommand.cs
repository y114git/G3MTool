using System.CommandLine;
using G3MToolCLI.Services.Patching;

namespace G3MToolCLI.Commands;

internal static class PatchMergeCommand
{
    private static readonly string[] s_mergeConflictWarnings = ["merge completed with conflicts; inspect the merge report if one was requested"];

    public static Command Create()
    {
        var command = new Command("merge", "Merge multiple patches into one .g3mpatch.\n  The first argument is the original data file (required as context).\n  Subsequent arguments are patches (from lowest to highest priority).\n  Input can be .g3mpatch, .xdelta, or data file (.win/.ios/.droid/.unx).\n  Usage: patch merge <original> <patch1> <patch2> [patch3...] [flags] [--cache <dir>] [--xdelta-path <path>]");
        var originalArg = new Argument<FileInfo>("original") { Description = "Path to original data file (.win/.ios/.droid/.unx)" };
        var patchesArg = new Argument<FileInfo[]>("patches")
        {
            Description = "Patch files (low → high priority)",
            Arity = new ArgumentArity(2, 100)
        };
        var outOption = new Option<string?>("--out", ["-o"]) { Description = "Output path for merged .g3mpatch (default if no flags specified)" };
        var applyOption = new Option<string?>("--apply", ["-a"]) { Description = "Apply merged patch and save the resulting data file to this path" };
        var codeOption = new Option<bool>("--code") { Description = "Enable Git-style 3-way merge for GML code files" };
        var propertiesOption = new Option<bool>("--properties") { Description = "Enable deep merge for JSON property files" };
        var sequentialOption = new Option<bool>("--sequential") { Description = "Use the sequential low-memory merge pipeline (does not support --code or --properties)" };
        var reportOption = new Option<string?>("--report", ["-r"]) { Description = "Path for the merge report (Markdown)" };
        var cacheOption = new Option<DirectoryInfo?>("--cache") { Description = "Read and write reusable .g3mcache analysis files in this directory." };
        command.Add(originalArg);
        command.Add(patchesArg);
        command.Add(outOption);
        command.Add(applyOption);
        command.Add(codeOption);
        command.Add(propertiesOption);
        command.Add(sequentialOption);
        command.Add(reportOption);
        command.Add(cacheOption);
        command.SetAction(parseResult => RunAsync(
            parseResult.GetRequiredValue(originalArg).FullName,
            [.. (parseResult.GetValue(patchesArg) ?? []).Select(p => p.FullName)],
            new MergeOptions
            {
                OutputPath = parseResult.GetValue(outOption),
                ApplyPath = parseResult.GetValue(applyOption),
                UseCodeMerge = parseResult.GetValue(codeOption),
                UsePropertyMerge = parseResult.GetValue(propertiesOption),
                UseSequentialMerge = parseResult.GetValue(sequentialOption),
                ReportPath = parseResult.GetValue(reportOption),
                CacheOptions = G3MCacheOptions.FromDirectory(parseResult.GetValue(cacheOption)?.FullName)
            }));
        return command;
    }

    private static async Task RunAsync(string original, List<string> patchPaths, MergeOptions options)
    {
        var result = await ScriptPatchInputService.MergeAsync(original, patchPaths, options, Program.Cancellation);
        if (!result.Success)
        {
            PatchCommand.WriteErrorJsonOrText("patch merge", result.Error);
            Environment.ExitCode = 1;
            return;
        }
        if (Program.JsonOutput)
        {
            PatchCommand.WriteJson(new
            {
                success = true,
                command = "patch merge",
                original,
                patches = patchPaths,
                output = result.OutputPath,
                applied = options.ApplyPath,
                conflicts = result.TotalConflicts,
                autoMerged = result.AutoMerged,
                warnings = result.TotalConflicts <= 0 ? [] : s_mergeConflictWarnings
            });
            return;
        }
        Console.WriteLine($"Merged {patchPaths.Count} patches.");
        if (!string.IsNullOrEmpty(result.OutputPath)) Console.WriteLine("  Patch:  " + result.OutputPath);
        if (!string.IsNullOrEmpty(options.ApplyPath)) Console.WriteLine("  Data:   " + options.ApplyPath);
        Console.WriteLine($"  Conflicts: {result.TotalConflicts}, auto-merged: {result.AutoMerged}");
    }
}
