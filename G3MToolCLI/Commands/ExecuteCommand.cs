using System;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using G3MToolCLI.Services.Execution;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

public static class ExecuteCommand
{
    public static Command Create()
    {
        Command command = new Command("execute", "Execute .csx scripts, external programs, or xdelta commands.\n  Usage: execute <target> [options] -- [args]\n  Examples:\n    execute script.csx --data data.win --output patched.win\n    execute xdelta --xdelta-path ./xdelta -- -d -s original.win patch.xdelta output.win");
        Argument<string> targetArg = new Argument<string>("target") { Description = "Program, script (.csx), or 'xdelta' to execute" };
        Argument<string[]> argsArg = new Argument<string[]>("args") { DefaultValueFactory = _ => Array.Empty<string>(), Description = "Arguments to pass" };
        Option<FileInfo?> dataOption = new Option<FileInfo?>("--data", ["-d"]) { Description = "Path to data file (.win/.ios/.droid/.unx) (optional for .csx scripts)" };
        Option<FileInfo?> outputOption = new Option<FileInfo?>("--output", ["-o"]) { Description = "Output file path (required when --data is used)" };
        Option<DirectoryInfo?> inputOption = new Option<DirectoryInfo?>("--input", ["-i"]) { Description = "Input directory for scripts (e.g., sprites folder for ImportSprites)" };
        command.Add(targetArg);
        command.Add(argsArg);
        command.Add(dataOption);
        command.Add(outputOption);
        command.Add(inputOption);
        command.SetAction(async parseResult =>
        {
            string target = parseResult.GetValue(targetArg)!;
            string[] args = parseResult.GetValue(argsArg) ?? [];
            FileInfo? data = parseResult.GetValue(dataOption);
            FileInfo? output = parseResult.GetValue(outputOption);
            DirectoryInfo? input = parseResult.GetValue(inputOption);
            if (target.Equals("xdelta", StringComparison.OrdinalIgnoreCase))
            {
                XDeltaResult result = await new XDeltaService().ExecuteRawAsync(args);
                if (!string.IsNullOrEmpty(result.Output)) await Console.Out.WriteAsync(result.Output);
                if (!result.Success)
                {
                    Console.Error.WriteLine("Error: " + result.Error);
                    Environment.ExitCode = 1;
                }
            }
            else if (target.EndsWith(".csx", StringComparison.OrdinalIgnoreCase))
            {
                string? dataPath = data?.FullName;
                string outputPath = output?.FullName ?? ((data != null) ? Path.Combine(PlatformUtil.GetExecutableDirectory(), Path.GetFileName(data.FullName)) : string.Empty);
                string[] finalArgs = input is null ? args : [input.FullName, .. args];
                ScriptResult result2 = await ExecuteService.ExecuteScriptAsync(target, dataPath, outputPath, finalArgs);
                if (!result2.Success)
                {
                    Console.Error.WriteLine("Error: " + result2.Error);
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
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = program,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }
            using Process? process = Process.Start(startInfo);
            if (process == null)
            {
                await Console.Error.WriteLineAsync("Failed to start process: " + program);
                return 1;
            }
            Task outputTask = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
            Task errorTask = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
            await process.WaitForExitAsync();
            await Task.WhenAll(outputTask, errorTask);
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Error executing " + program + ": " + ex.Message);
            return 1;
        }
    }
}
