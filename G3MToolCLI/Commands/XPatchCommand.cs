using System.CommandLine;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

public static class XPatchCommand
{
    public static Command Create()
    {
        var command = new Command("xpatch", "Create or apply xdelta patches. Subcommands: create, apply");
        var createCommand = new Command("create", "Create an xdelta patch from two files.\n  Usage: xpatch create <original> <modified> [output] [--xdelta-path <path>]");
        var originalArg = new Argument<FileInfo>("original") { Description = "Path to original file" };
        var modifiedArg = new Argument<FileInfo>("modified") { Description = "Path to modified file" };
        var outputArg = new Argument<FileInfo?>("output") { DefaultValueFactory = _ => null, Description = "Output patch file (optional). Default: next to the executable" };
        createCommand.Add(originalArg);
        createCommand.Add(modifiedArg);
        createCommand.Add(outputArg);
        createCommand.SetAction(async parseResult =>
        {
            var original = parseResult.GetValue(originalArg)!;
            var modified = parseResult.GetValue(modifiedArg)!;
            var output = parseResult.GetValue(outputArg);
            var xDeltaService = new XDeltaService();
            var defaultOutput = Path.Combine(PlatformUtil.GetExecutableDirectory(), Path.ChangeExtension(Path.GetFileName(modified.FullName), ".xdelta"));
            var outputPath = output?.FullName ?? defaultOutput;
            Console.WriteLine("Creating xdelta patch...");
            Console.WriteLine("  Original: " + original.FullName);
            Console.WriteLine("  Modified: " + modified.FullName);
            Console.WriteLine("  Output:   " + outputPath);
            var result = await xDeltaService.CreatePatchAsync(original.FullName, modified.FullName, outputPath);
            if (result.Success)
            {
                Console.WriteLine("Patch created successfully: " + outputPath);
            }
            else
            {
                await Console.Error.WriteLineAsync("Error: " + result.Error);
                Environment.ExitCode = 1;
            }
        });
        var applyCommand = new Command("apply", "Apply an xdelta patch to a file.\n  Usage: xpatch apply <original> <patch> [output] [--xdelta-path <path>]");
        var applyOriginalArg = new Argument<FileInfo>("original") { Description = "Path to original file" };
        var patchArg = new Argument<FileInfo>("patch") { Description = "Path to xdelta patch file" };
        var applyOutputArg = new Argument<FileInfo?>("output") { DefaultValueFactory = _ => null, Description = "Output file (optional). Default: next to the executable" };
        applyCommand.Add(applyOriginalArg);
        applyCommand.Add(patchArg);
        applyCommand.Add(applyOutputArg);
        applyCommand.SetAction(async parseResult =>
        {
            var original = parseResult.GetValue(applyOriginalArg)!;
            var patch = parseResult.GetValue(patchArg)!;
            var output = parseResult.GetValue(applyOutputArg);
            var xDeltaService = new XDeltaService();
            var outputExt = GetDataFileOutputExtension(original.FullName);
            var defaultOutput = Path.Combine(PlatformUtil.GetExecutableDirectory(), Path.GetFileNameWithoutExtension(original.FullName) + "_patched" + outputExt);
            var outputPath = output?.FullName ?? defaultOutput;
            Console.WriteLine("Applying xdelta patch...");
            Console.WriteLine("  Original: " + original.FullName);
            Console.WriteLine("  Patch:    " + patch.FullName);
            Console.WriteLine("  Output:   " + outputPath);
            var result = await xDeltaService.ApplyPatchAsync(original.FullName, patch.FullName, outputPath);
            if (result.Success)
            {
                Console.WriteLine("Patch applied successfully: " + outputPath);
            }
            else
            {
                await Console.Error.WriteLineAsync("Error: " + result.Error);
                Environment.ExitCode = 1;
            }
        });
        command.Add(createCommand);
        command.Add(applyCommand);
        return command;
    }

    private static string GetDataFileOutputExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".win", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ios", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".droid", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".unx", StringComparison.OrdinalIgnoreCase)
            ? extension.ToLowerInvariant()
            : ".win";
    }
}
