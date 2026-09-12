using System;
using System.CommandLine;
using System.IO;
using System.Text.Json;
using G3MToolCLI.Models.Scripting;
using G3MToolCLI.Services.Logging;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

public static class DiffCommand
{
    private static readonly JsonSerializerOptions s_compactJsonOptions = new()
    {
        WriteIndented = false
    };
    private static readonly string[] s_standardReportWarnings = ["standard mode omits unified text/code/JSON hunks; use --full for full changed-file diffs"];

    public static Command Create()
    {
        Command command = new Command("diff", "Compare data files or .g3mpatch files and write a Markdown report.\n  Standard mode writes summaries and changed file lists. --full adds unified text/code/JSON diffs and deeper TPI/asset-order details.\n  Usage: diff <file1> <file2> [output-dir] [--full] [--cache <dir>]");
        Argument<FileInfo> file1Arg = new Argument<FileInfo>("file1") { Description = "First file (data file or .g3mpatch)" };
        Argument<FileInfo> file2Arg = new Argument<FileInfo>("file2") { Description = "Second file (data file or .g3mpatch)" };
        Argument<DirectoryInfo?> outputArg = new Argument<DirectoryInfo?>("output") { DefaultValueFactory = _ => null, Description = "Output directory for diff report. Default: <executable>/diff" };
        Option<bool> fullOption = new Option<bool>("--full") { Description = "Generate full text/code/JSON diffs plus deeper TPI, reference, and asset-order details. Slower and larger." };
        Option<DirectoryInfo?> cacheOption = new Option<DirectoryInfo?>("--cache") { Description = "Read and write reusable .g3mcache analysis files in this directory." };
        command.Add(file1Arg);
        command.Add(file2Arg);
        command.Add(outputArg);
        command.Add(fullOption);
        command.Add(cacheOption);
        command.SetAction(async parseResult =>
        {
            FileInfo file1 = parseResult.GetValue(file1Arg)!;
            FileInfo file2 = parseResult.GetValue(file2Arg)!;
            DirectoryInfo? output = parseResult.GetValue(outputArg);
            bool full = parseResult.GetValue(fullOption);
            DirectoryInfo? cacheDir = parseResult.GetValue(cacheOption);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string defaultOutputDir = Path.Combine(PlatformUtil.GetExecutableDirectory(), "diff");
            string outputDir = output?.FullName ?? defaultOutputDir;
            string outputPath = Path.Combine(outputDir, "diff_" + timestamp + ".md");
            if (!Program.JsonOutput)
            {
                Console.WriteLine("Comparing files...");
                Console.WriteLine("  File 1: " + file1.FullName);
                Console.WriteLine("  File 2: " + file2.FullName);
                Console.WriteLine("  Output: " + outputPath);
            }
            DiffResult result = await DiffService.CompareAsync(file1.FullName, file2.FullName, outputPath, full ? DiffReportMode.Full : DiffReportMode.Standard, G3MCacheOptions.FromDirectory(cacheDir?.FullName));
            if (result.Success)
            {
                if (Program.JsonOutput)
                {
                    WriteJson(new
                    {
                        success = true,
                        command = "diff",
                        mode = result.Mode,
                        file1 = file1.FullName,
                        file2 = file2.FullName,
                        output = result.OutputPath,
                        differences = result.DifferenceCount,
                        changed = result.TotalChanged,
                        @new = result.TotalNew,
                        deleted = result.TotalDeleted,
                        textDiffs = result.TextDiffCount,
                        byType = result.ByType,
                        warnings = full || result.DifferenceCount == 0 ? [] : s_standardReportWarnings
                    });
                }
                else
                {
                    Console.WriteLine("Diff report created: " + outputPath);
                    Console.WriteLine($"  Differences: {result.DifferenceCount}");
                    Console.WriteLine(full ? "  Mode: full" : "  Mode: standard (use --full for unified text/code/JSON diffs)");
                }
            }
            else
            {
                if (Program.JsonOutput)
                {
                    WriteJson(new
                    {
                        success = false,
                        command = "diff",
                        error = result.Error
                    });
                }
                else
                {
                    Console.Error.WriteLine("Error: " + result.Error);
                }
                Environment.ExitCode = 1;
            }
        });
        return command;
    }

    private static void WriteJson<T>(T value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, s_compactJsonOptions));
    }
}
