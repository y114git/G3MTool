using System;
using System.CommandLine;
using System.IO;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

public static class XPatchCommand
{
    public static Command Create()
    {
        Command command = new Command("xpatch", "Create or apply xdelta patches. Subcommands: create, apply");
        Command createCommand = new Command("create", "Create an xdelta patch from two files.\n  Usage: xpatch create <original> <modified> [output] [--xdelta-path <path>]");
        Argument<FileInfo> originalArg = new Argument<FileInfo>("original") { Description = "Path to original file" };
        Argument<FileInfo> modifiedArg = new Argument<FileInfo>("modified") { Description = "Path to modified file" };
        Argument<FileInfo?> outputArg = new Argument<FileInfo?>("output") { DefaultValueFactory = _ => null, Description = "Output patch file (optional). Default: next to the executable" };
        createCommand.Add(originalArg);
        createCommand.Add(modifiedArg);
        createCommand.Add(outputArg);
        createCommand.SetAction(async parseResult =>
        {
            FileInfo original = parseResult.GetValue(originalArg)!;
            FileInfo modified = parseResult.GetValue(modifiedArg)!;
            FileInfo? output = parseResult.GetValue(outputArg);
            XDeltaService xDeltaService = new XDeltaService();
            string defaultOutput = Path.Combine(PlatformUtil.GetExecutableDirectory(), Path.ChangeExtension(Path.GetFileName(modified.FullName), ".xdelta"));
            string outputPath = output?.FullName ?? defaultOutput;
            Console.WriteLine("Creating xdelta patch...");
            Console.WriteLine("  Original: " + original.FullName);
            Console.WriteLine("  Modified: " + modified.FullName);
            Console.WriteLine("  Output:   " + outputPath);
            XDeltaResult result = await xDeltaService.CreatePatchAsync(original.FullName, modified.FullName, outputPath);
            if (result.Success)
            {
                Console.WriteLine("Patch created successfully: " + outputPath);
            }
            else
            {
                Console.Error.WriteLine("Error: " + result.Error);
                Environment.ExitCode = 1;
            }
        });
        Command applyCommand = new Command("apply", "Apply an xdelta patch to a file.\n  Usage: xpatch apply <original> <patch> [output] [--xdelta-path <path>]");
        Argument<FileInfo> applyOriginalArg = new Argument<FileInfo>("original") { Description = "Path to original file" };
        Argument<FileInfo> patchArg = new Argument<FileInfo>("patch") { Description = "Path to xdelta patch file" };
        Argument<FileInfo?> applyOutputArg = new Argument<FileInfo?>("output") { DefaultValueFactory = _ => null, Description = "Output file (optional). Default: next to the executable" };
        applyCommand.Add(applyOriginalArg);
        applyCommand.Add(patchArg);
        applyCommand.Add(applyOutputArg);
        applyCommand.SetAction(async parseResult =>
        {
            FileInfo original = parseResult.GetValue(applyOriginalArg)!;
            FileInfo patch = parseResult.GetValue(patchArg)!;
            FileInfo? output = parseResult.GetValue(applyOutputArg);
            XDeltaService xDeltaService = new XDeltaService();
            string outputExt = GetDataFileOutputExtension(original.FullName);
            string defaultOutput = Path.Combine(PlatformUtil.GetExecutableDirectory(), Path.GetFileNameWithoutExtension(original.FullName) + "_patched" + outputExt);
            string outputPath = output?.FullName ?? defaultOutput;
            Console.WriteLine("Applying xdelta patch...");
            Console.WriteLine("  Original: " + original.FullName);
            Console.WriteLine("  Patch:    " + patch.FullName);
            Console.WriteLine("  Output:   " + outputPath);
            XDeltaResult result = await xDeltaService.ApplyPatchAsync(original.FullName, patch.FullName, outputPath);
            if (result.Success)
            {
                Console.WriteLine("Patch applied successfully: " + outputPath);
            }
            else
            {
                Console.Error.WriteLine("Error: " + result.Error);
                Environment.ExitCode = 1;
            }
        });
        command.Add(createCommand);
        command.Add(applyCommand);
        return command;
    }

    private static string GetDataFileOutputExtension(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".win", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ios", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".droid", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".unx", StringComparison.OrdinalIgnoreCase)
            ? extension.ToLowerInvariant()
            : ".win";
    }
}
