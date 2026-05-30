using System.CommandLine;
using G3MToolCLI.Services;
using G3MToolCLI.Utils;
using static G3MToolCLI.Utils.DataFileExtensionUtil;

namespace G3MToolCLI.Commands;

public static class XPatchCommand
{
    public static Command Create()
    {
        var command = new Command("xpatch", "Create or apply xdelta patches. Subcommands: create, apply");

        var createCommand = new Command("create", "Create an xdelta patch from two files.\n  Usage: xpatch create <original> <modified> [output] [--xdelta-path <path>]");
        var originalArg = new Argument<FileInfo>("original", "Path to original file");
        var modifiedArg = new Argument<FileInfo>("modified", "Path to modified file");
        var outputArg = new Argument<FileInfo?>("output", () => null, "Output patch file (optional). Default: next to G3MTool executable");

        createCommand.AddArgument(originalArg);
        createCommand.AddArgument(modifiedArg);
        createCommand.AddArgument(outputArg);

        createCommand.SetHandler(async (original, modified, output) =>
        {
            var xdelta = new XDeltaService();
            var defaultOutput = Path.Combine(PlatformUtil.GetExecutableDirectory(), Path.ChangeExtension(Path.GetFileName(modified.FullName), ".xdelta"));
            var outputPath = output?.FullName ?? defaultOutput;

            Console.WriteLine($"Creating xdelta patch...");
            Console.WriteLine($"  Original: {original.FullName}");
            Console.WriteLine($"  Modified: {modified.FullName}");
            Console.WriteLine($"  Output:   {outputPath}");

            var result = await xdelta.CreatePatchAsync(original.FullName, modified.FullName, outputPath);

            if (result.Success)
            {
                Console.WriteLine($"Patch created successfully: {outputPath}");
            }
            else
            {
                Console.Error.WriteLine($"Error: {result.Error}");
                Environment.ExitCode = 1;
            }
        }, originalArg, modifiedArg, outputArg);

        var applyCommand = new Command("apply", "Apply an xdelta patch to a file.\n  Usage: xpatch apply <original> <patch> [output] [--xdelta-path <path>]");
        var applyOriginalArg = new Argument<FileInfo>("original", "Path to original file");
        var patchArg = new Argument<FileInfo>("patch", "Path to xdelta patch file");
        var applyOutputArg = new Argument<FileInfo?>("output", () => null, "Output file (optional). Default: next to G3MTool executable");

        applyCommand.AddArgument(applyOriginalArg);
        applyCommand.AddArgument(patchArg);
        applyCommand.AddArgument(applyOutputArg);

        applyCommand.SetHandler(async (original, patch, output) =>
        {
            var xdelta = new XDeltaService();
            var outputExt = GetOutputExtension(original.FullName);
            var defaultOutput = Path.Combine(PlatformUtil.GetExecutableDirectory(), Path.GetFileNameWithoutExtension(original.FullName) + "_patched" + outputExt);
            var outputPath = output?.FullName ?? defaultOutput;

            Console.WriteLine($"Applying xdelta patch...");
            Console.WriteLine($"  Original: {original.FullName}");
            Console.WriteLine($"  Patch:    {patch.FullName}");
            Console.WriteLine($"  Output:   {outputPath}");

            var result = await xdelta.ApplyPatchAsync(original.FullName, patch.FullName, outputPath);

            if (result.Success)
            {
                Console.WriteLine($"Patch applied successfully: {outputPath}");
            }
            else
            {
                Console.Error.WriteLine($"Error: {result.Error}");
                Environment.ExitCode = 1;
            }
        }, applyOriginalArg, patchArg, applyOutputArg);

        command.AddCommand(createCommand);
        command.AddCommand(applyCommand);

        return command;
    }
}
