using System.CommandLine;
using System.Text.Json;
using G3MToolCLI.Models;
using G3MToolCLI.Services;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

public static class DiffCommand
{
    public static Command Create()
    {
        var command = new Command("diff", "Compare data files or .g3mpatch files and write a Markdown report.\n  Standard mode writes summaries and changed file lists. --full adds unified text/code/JSON diffs and deeper TPI/asset-order details.\n  Usage: diff <file1> <file2> [output-dir] [--full] [--cache <dir>]");

        var file1Arg = new Argument<FileInfo>("file1", "First file (data file or .g3mpatch)");
        var file2Arg = new Argument<FileInfo>("file2", "Second file (data file or .g3mpatch)");
        var outputArg = new Argument<DirectoryInfo?>("output", () => null, "Output directory for diff report. Default: <executable>/diff");
        var fullOption = new Option<bool>(
            name: "--full",
            description: "Generate full text/code/JSON diffs plus deeper TPI, reference, and asset-order details. Slower and larger.");
        var cacheOption = new Option<DirectoryInfo?>(
            name: "--cache",
            description: "Read and write reusable .g3mcache analysis files in this directory.");

        command.AddArgument(file1Arg);
        command.AddArgument(file2Arg);
        command.AddArgument(outputArg);
        command.AddOption(fullOption);
        command.AddOption(cacheOption);

        command.SetHandler(async (file1, file2, output, full, cacheDir) =>
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var defaultOutputDir = Path.Combine(PlatformUtil.GetExecutableDirectory(), "diff");
            var outputDir = output?.FullName ?? defaultOutputDir;
            var outputPath = Path.Combine(outputDir, $"diff_{timestamp}.md");

            if (!Program.JsonOutput)
            {
                Console.WriteLine("Comparing files...");
                Console.WriteLine($"  File 1: {file1.FullName}");
                Console.WriteLine($"  File 2: {file2.FullName}");
                Console.WriteLine($"  Output: {outputPath}");
            }

            var result = await DiffService.CompareAsync(
                file1.FullName,
                file2.FullName,
                outputPath,
                full ? DiffReportMode.Full : DiffReportMode.Standard,
                G3MCacheOptions.FromDirectory(cacheDir?.FullName));

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
                        warnings = full || result.DifferenceCount == 0
                            ? []
                            : new[] { "standard mode omits unified text/code/JSON hunks; use --full for full changed-file diffs" }
                    });
                }
                else
                {
                    Console.WriteLine($"Diff report created: {outputPath}");
                    Console.WriteLine($"  Differences: {result.DifferenceCount}");
                    Console.WriteLine(full
                        ? "  Mode: full"
                        : "  Mode: standard (use --full for unified text/code/JSON diffs)");
                }
            }
            else
            {
                if (Program.JsonOutput)
                    WriteJson(new { success = false, command = "diff", error = result.Error });
                else
                    Console.Error.WriteLine($"Error: {result.Error}");
                Environment.ExitCode = 1;
            }
        }, file1Arg, file2Arg, outputArg, fullOption, cacheOption);

        return command;
    }

    private static void WriteJson<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false }));
}
