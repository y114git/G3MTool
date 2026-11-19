using System.CommandLine;
using G3MToolCLI.Services.Execution;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

public static class XPatchCommand
{
    public static Command Create()
    {
        var command = new Command("xpatch", "Create or apply xdelta patches. Subcommands: create, apply");
        var createCommand = new Command("create", "Create an xdelta patch from two files.\n  Usage: xpatch create <original> <modified> [output] [--xdelta-path <path>] [--json]");
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
            var outputPath = output?.FullName ?? Path.Combine(PlatformUtil.GetExecutableDirectory(), Path.ChangeExtension(Path.GetFileName(modified.FullName), ".xdelta"));
            if (!Program.JsonOutput)
            {
                Console.WriteLine("Creating xdelta patch...");
                Console.WriteLine("  Original: " + original.FullName);
                Console.WriteLine("  Modified: " + modified.FullName);
                Console.WriteLine("  Output:   " + outputPath);
            }
            await RunAsync("xpatch create", outputPath, "Patch created successfully: ", xdelta => XDeltaProcess.CreateAsync(xdelta, original.FullName, modified.FullName, outputPath, Program.Cancellation));
        });
        var applyCommand = new Command("apply", "Apply an xdelta patch to a file.\n  Usage: xpatch apply <original> <patch> [output] [--xdelta-path <path>] [--json]");
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
            var outputPath = output?.FullName ?? Path.Combine(PlatformUtil.GetExecutableDirectory(), Path.GetFileNameWithoutExtension(original.FullName) + "_patched" + GetDataFileOutputExtension(original.FullName));
            if (!Program.JsonOutput)
            {
                Console.WriteLine("Applying xdelta patch...");
                Console.WriteLine("  Original: " + original.FullName);
                Console.WriteLine("  Patch:    " + patch.FullName);
                Console.WriteLine("  Output:   " + outputPath);
            }
            await RunAsync("xpatch apply", outputPath, "Patch applied successfully: ", xdelta => XDeltaProcess.ApplyAsync(xdelta, original.FullName, patch.FullName, outputPath, Program.Cancellation));
        });
        command.Add(createCommand);
        command.Add(applyCommand);
        return command;
    }

    private static async Task RunAsync(string commandName, string outputPath, string successText, Func<string, Task<XDeltaRun>> run)
    {
        string? error;
        try
        {
            var result = await run(EmbeddedXDeltaPathProvider.Resolve());
            error = result.Success ? null : result.Error;
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
        }
        if (error is not null)
        {
            PatchCommand.WriteErrorJsonOrText(commandName, error);
            Environment.ExitCode = 1;
        }
        else if (Program.JsonOutput)
        {
            PatchCommand.WriteSuccessJsonOrText(commandName, outputPath, new { format = "xdelta" });
        }
        else
        {
            Console.WriteLine(successText + outputPath);
        }
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
