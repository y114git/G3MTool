using System.CommandLine;
using G3MToolCLI.Services;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

public static class ExecuteCommand
{
    public static Command Create()
    {
        var command = new Command("execute", "Execute .csx scripts, external programs, or xdelta commands.\n  Usage: execute <target> [args] --data <data-file> --output <output-file> [--xdelta-path <path>]\n  Examples:\n    execute script.csx --data data.win --output patched.win\n    execute xdelta -d -s original.win patch.xdelta output.win --xdelta-path ./xdelta");

        var targetArg = new Argument<string>("target", "Program, script (.csx), or 'xdelta' to execute");
        var argsArg = new Argument<string[]>("args", () => [], "Arguments to pass");

        var dataOption = new Option<FileInfo?>(
            aliases: ["--data", "-d"],
            description: "Path to data file (.win/.ios/.droid/.unx) (optional for .csx scripts)");

        var outputOption = new Option<FileInfo?>(
            aliases: ["--output", "-o"],
            description: "Output file path (required when --data is used)");

        var inputOption = new Option<DirectoryInfo?>(
            aliases: ["--input", "-i"],
            description: "Input directory for scripts (e.g., sprites folder for ImportSprites)");

        command.AddArgument(targetArg);
        command.AddArgument(argsArg);
        command.AddOption(dataOption);
        command.AddOption(outputOption);
        command.AddOption(inputOption);

        command.SetHandler(async (target, args, data, output, input) =>
        {
            if (target.Equals("xdelta", StringComparison.OrdinalIgnoreCase))
            {
                var xdelta = new XDeltaService();
                var result = await xdelta.ExecuteRawAsync(args);

                if (!result.Success)
                {
                    Console.Error.WriteLine($"Error: {result.Error}");
                    Environment.ExitCode = 1;
                }
            }
            else if (target.EndsWith(".csx", StringComparison.OrdinalIgnoreCase))
            {
                var dataPath = data?.FullName;
                var outputPath = output?.FullName
                    ?? (data != null ? Path.Combine(PlatformUtil.GetExecutableDirectory(), Path.GetFileName(data.FullName)) : string.Empty);

                var finalArgs = input != null
                    ? [input.FullName, .. args]
                    : args;

                var result = await ExecuteService.ExecuteScriptAsync(
                    target,
                    dataPath,
                    outputPath,
                    finalArgs);

                if (!result.Success)
                {
                    Console.Error.WriteLine($"Error: {result.Error}");
                    Environment.ExitCode = 1;
                }
            }
            else
            {
                var result = await ExecuteExternalProgramAsync(target, args);
                Environment.ExitCode = result;
            }
        }, targetArg, argsArg, dataOption, outputOption, inputOption);

        return command;
    }

    private static async Task<int> ExecuteExternalProgramAsync(string program, string[] args)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = program,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                Console.Error.WriteLine($"Failed to start process: {program}");
                return 1;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            var output = await outputTask;
            var error = await errorTask;

            if (!string.IsNullOrEmpty(output))
                Console.Write(output);
            if (!string.IsNullOrEmpty(error))
                Console.Error.Write(error);

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error executing {program}: {ex.Message}");
            return 1;
        }
    }
}
