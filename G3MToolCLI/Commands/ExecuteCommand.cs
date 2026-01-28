using System.CommandLine;
using G3MToolCLI.Services;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

public static class ExecuteCommand
{
    public static Command Create()
    {
        var command = new Command("execute", "Execute programs, scripts, or xdelta commands");

        var targetArg = new Argument<string>("target", "Program, script (.csx), or 'xdelta' to execute");
        var argsArg = new Argument<string[]>("args", () => Array.Empty<string>(), "Arguments to pass");

        var dataOption = new Option<FileInfo?>(
            aliases: ["--data", "-d"],
            description: "Path to data.win file (required for .csx scripts)");
        
        var outputOption = new Option<FileInfo?>(
            aliases: ["--output", "-o"],
            description: "Output file path (required when --data is used)");

        command.AddArgument(targetArg);
        command.AddArgument(argsArg);
        command.AddOption(dataOption);
        command.AddOption(outputOption);

        command.SetHandler(async (target, args, data, output) =>
        {
            // Determine what to execute
            if (target.Equals("xdelta", StringComparison.OrdinalIgnoreCase))
            {
                // Passthrough to xdelta
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
                // Execute .csx script
                if (data == null)
                {
                    Console.Error.WriteLine("Error: --data is required for .csx scripts");
                    Environment.ExitCode = 1;
                    return;
                }
                var scriptExecutor = new ScriptExecutorService();
                var defaultOutput = Path.Combine(PlatformUtils.GetExecutableDirectory(), Path.GetFileName(data.FullName));
                var outputPath = output?.FullName ?? defaultOutput;
                var result = await scriptExecutor.ExecuteScriptAsync(
                    target, 
                    data.FullName, 
                    outputPath,
                    args);
                
                if (!result.Success)
                {
                    Console.Error.WriteLine($"Error: {result.Error}");
                    Environment.ExitCode = 1;
                }
            }
            else
            {
                // Execute external program
                var result = await ExecuteExternalProgramAsync(target, args);
                Environment.ExitCode = result;
            }
        }, targetArg, argsArg, dataOption, outputOption);

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
