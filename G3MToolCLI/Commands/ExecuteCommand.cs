using System.CommandLine;
using System.Diagnostics;
using G3MToolCLI.Services.Execution;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

public static class ExecuteCommand
{
    public static Command Create()
    {
        var command = new Command("execute", "Execute .csx scripts, external programs, or xdelta commands.\n  Usage: execute <target> [options] -- [args]\n  Examples:\n    execute script.csx --data data.win --output patched.win\n    execute xdelta --xdelta-path ./xdelta -- -d -s original.win patch.xdelta output.win");
        var targetArg = new Argument<string>("target") { Description = "Program, script (.csx), or 'xdelta' to execute" };
        var argsArg = new Argument<string[]>("args") { DefaultValueFactory = _ => [], Description = "Arguments to pass" };
        var dataOption = new Option<FileInfo?>("--data", ["-d"]) { Description = "Path to data file (.win/.ios/.droid/.unx) (optional for .csx scripts)" };
        var outputOption = new Option<FileInfo?>("--output", ["-o"]) { Description = "Output file path (required when --data is used)" };
        var inputOption = new Option<DirectoryInfo?>("--input", ["-i"]) { Description = "Input directory for scripts (e.g., sprites folder for ImportSprites)" };
        command.Add(targetArg);
        command.Add(argsArg);
        command.Add(dataOption);
        command.Add(outputOption);
        command.Add(inputOption);
        command.SetAction(async parseResult =>
        {
            var target = parseResult.GetValue(targetArg)!;
            var args = parseResult.GetValue(argsArg) ?? [];
            var data = parseResult.GetValue(dataOption);
            var output = parseResult.GetValue(outputOption);
            var input = parseResult.GetValue(inputOption);
            if (target.Equals("xdelta", StringComparison.OrdinalIgnoreCase))
            {
                var result = await new XDeltaService().ExecuteRawAsync(args);
                if (!string.IsNullOrEmpty(result.Output)) await Console.Out.WriteAsync(result.Output);
                if (!result.Success)
                {
                    await Console.Error.WriteLineAsync("Error: " + result.Error);
                    Environment.ExitCode = 1;
                }
            }
            else if (target.EndsWith(".csx", StringComparison.OrdinalIgnoreCase))
            {
                var dataPath = data?.FullName;
                var outputPath = output?.FullName ?? ((data != null) ? Path.Combine(PlatformUtil.GetExecutableDirectory(), Path.GetFileName(data.FullName)) : string.Empty);
                var finalArgs = input is null ? args : [input.FullName, .. args];
                var result2 = await ExecuteService.ExecuteScriptAsync(target, dataPath, outputPath, finalArgs);
                if (!result2.Success)
                {
                    await Console.Error.WriteLineAsync("Error: " + result2.Error);
                    Environment.ExitCode = 1;
                }
            }
            else
            {
                Environment.ExitCode = await ExecuteExternalProgramAsync(target, args);
            }
        });
        return command;
    }

    private static async Task<int> ExecuteExternalProgramAsync(string program, string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
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
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                await Console.Error.WriteLineAsync("Failed to start process: " + program);
                return 1;
            }
#pragma warning disable CA2025 // both copies are awaited below, before the streams are disposed
            await using var standardOutput = Console.OpenStandardOutput();
            await using var standardError = Console.OpenStandardError();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(standardOutput);
            var errorTask = process.StandardError.BaseStream.CopyToAsync(standardError);
            await process.WaitForExitAsync();
            await Task.WhenAll(outputTask, errorTask);
#pragma warning restore CA2025
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Error executing " + program + ": " + ex.Message);
            return 1;
        }
    }
}
