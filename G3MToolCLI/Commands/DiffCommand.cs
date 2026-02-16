using System.CommandLine;
using G3MToolCLI.Services;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

public static class DiffCommand
{
    public static Command Create()
    {
        var command = new Command("diff", "Compare two data files or patch files and generate a diff report.\n  Usage: diff <file1> <file2> [output-dir]");

        var file1Arg = new Argument<FileInfo>("file1", "First file (data file or patch.zip)");
        var file2Arg = new Argument<FileInfo>("file2", "Second file (data file or patch.zip)");
        var outputArg = new Argument<DirectoryInfo?>("output", () => null, "Output directory for diff report (optional). Default: next to G3MTool executable");

        command.AddArgument(file1Arg);
        command.AddArgument(file2Arg);
        command.AddArgument(outputArg);

        command.SetHandler(async (file1, file2, output) =>
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var defaultOutputDir = Path.Combine(PlatformUtil.GetExecutableDirectory(), "diff");
            var outputDir = output?.FullName ?? defaultOutputDir;
            var outputPath = Path.Combine(outputDir, $"diff_{timestamp}.md");

            Console.WriteLine($"Comparing files...");
            Console.WriteLine($"  File 1: {file1.FullName}");
            Console.WriteLine($"  File 2: {file2.FullName}");
            Console.WriteLine($"  Output: {outputPath}");

            var result = await DiffService.CompareAsync(file1.FullName, file2.FullName, outputPath);

            if (result.Success)
            {
                Console.WriteLine($"Diff report created: {outputPath}");
                Console.WriteLine($"  Differences: {result.DifferenceCount}");
            }
            else
            {
                Console.Error.WriteLine($"Error: {result.Error}");
                Environment.ExitCode = 1;
            }
        }, file1Arg, file2Arg, outputArg);

        return command;
    }
}
