using System.CommandLine;
using System.Diagnostics;
using G3MToolCLI.Services.Execution;

namespace G3MToolCLI.Commands;

public static class ExecuteCommand
{
    public static Command Create()
    {
        var command = new Command("execute", "Execute .csx scripts, external programs, or xdelta commands.\n  Usage: execute <target> [options] -- [args]\n  Examples:\n    execute script.csx --data data.win --output patched.win\n    execute export.csx --data data.win --output exports/\n    execute xdelta --xdelta-path ./xdelta -- -d -s original.win patch.xdelta output.win\n  --data, --output, --input and --timeout apply to .csx scripts only.");
        var targetArg = new Argument<string>("target") { Description = "Program, script (.csx), or 'xdelta' to execute" };
        var argsArg = new Argument<string[]>("args") { DefaultValueFactory = _ => [], Description = "Arguments to pass" };
        var dataOption = new Option<FileInfo?>("--data", ["-d"]) { Description = "Path to data file (.win/.ios/.droid/.unx) (optional for .csx scripts)" };
        var outputOption = new Option<string?>("--output", ["-o"]) { Description = "Script output. A folder receives the script's exports: an existing folder, a path ending with a separator, or a path without an extension. Any other path is a data file: the script's data is saved there and exports go to its folder. Default: exports go to the current folder and the data file is not saved." };
        var inputOption = new Option<DirectoryInfo?>("--input", ["-i"]) { Description = "Input directory for scripts (e.g., sprites folder for ImportSprites)" };
        var timeoutOption = new Option<int?>("--timeout") { Description = "Stop a .csx script after this many seconds" };
        timeoutOption.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int?>() is <= 0) result.AddError("--timeout must be a positive number of seconds.");
        });
        command.Add(targetArg);
        command.Add(argsArg);
        command.Add(dataOption);
        command.Add(outputOption);
        command.Add(inputOption);
        command.Add(timeoutOption);
        command.SetAction(async parseResult =>
        {
            var target = parseResult.GetValue(targetArg)!;
            var args = parseResult.GetValue(argsArg) ?? [];
            var data = parseResult.GetValue(dataOption);
            var output = parseResult.GetValue(outputOption);
            var input = parseResult.GetValue(inputOption);
            var timeout = parseResult.GetValue(timeoutOption) is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
            var isXDelta = target.Equals("xdelta", StringComparison.OrdinalIgnoreCase);
            var isScript = !isXDelta && target.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);
            var commandName = isXDelta ? "execute xdelta" : isScript ? "execute" : "execute program";
            if (!isScript)
            {
                foreach (var (name, option) in new (string, Option)[] { ("--data", dataOption), ("--output", outputOption), ("--input", inputOption), ("--timeout", timeoutOption) })
                {
                    if (parseResult.GetResult(option) is null) continue;
                    PatchCommand.WriteErrorJsonOrText(commandName, name + " applies to .csx scripts only.");
                    Environment.ExitCode = 1;
                    return;
                }
            }
            if (isXDelta)
            {
                await ExecuteXDeltaAsync(args);
            }
            else if (isScript)
            {
                await ExecuteScriptAsync(target, data?.FullName, output, input is null ? args : [input.FullName, .. args], timeout);
            }
            else
            {
                await ExecuteExternalProgramAsync(target, args);
            }
        });
        return command;
    }

    // A path is a folder when it exists as one, ends with a separator, or has no extension (and is not an existing file).
    private static bool IsFolderTarget(string path) =>
        Directory.Exists(path) || path.EndsWith('/') || path.EndsWith(Path.DirectorySeparatorChar) || (!Path.HasExtension(path) && !File.Exists(path));

    private static async Task ExecuteScriptAsync(string script, string? dataPath, string? output, string[] args, TimeSpan? timeout)
    {
        var exportsOnly = !string.IsNullOrWhiteSpace(output) && IsFolderTarget(output);
        var savePath = !string.IsNullOrWhiteSpace(output) && !exportsOnly ? Path.GetFullPath(output) : null;
        var exportDirectory = exportsOnly ? Path.GetFullPath(output!) : null;
        var result = await ExecuteService.ExecuteScriptAsync(script, dataPath, savePath, args, exportDirectory, timeout, Program.Cancellation);
        if (!result.Success)
        {
            PatchCommand.WriteErrorJsonOrText("execute", result.Error);
            Environment.ExitCode = 1;
            return;
        }
        if (Program.JsonOutput)
        {
            PatchCommand.WriteJson(new
            {
                success = true,
                command = "execute",
                script = Path.GetFullPath(script),
                data = dataPath,
                saved = savePath,
                exportDirectory = exportDirectory ?? (savePath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(savePath)),
                warnings = Array.Empty<string>()
            });
        }
        else if (dataPath is not null && savePath is null)
        {
            // Whether the script changed the data is not tracked, so the notice shows for every run with --data and no file path.
            await Console.Error.WriteLineAsync("Note: the data file was not saved. Pass --output <file> to save it.");
        }
    }

    private static async Task ExecuteXDeltaAsync(string[] args)
    {
        string? error;
        string? text = null;
        try
        {
            var result = await XDeltaProcess.RunAsync(EmbeddedXDeltaPathProvider.Resolve(), args, Program.Cancellation);
            error = result.Success ? null : result.Error;
            text = result.Output;
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
        }
        if (error is not null)
        {
            PatchCommand.WriteErrorJsonOrText("execute xdelta", error);
            Environment.ExitCode = 1;
        }
        else if (Program.JsonOutput)
        {
            PatchCommand.WriteJson(new { success = true, command = "execute xdelta", output = text, warnings = Array.Empty<string>() });
        }
        else if (!string.IsNullOrEmpty(text))
        {
            await Console.Out.WriteAsync(text);
        }
    }

    private static async Task ExecuteExternalProgramAsync(string program, string[] args)
    {
        Process? process = null;
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
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
            process = Process.Start(startInfo);
            if (process == null)
            {
                PatchCommand.WriteErrorJsonOrText("execute program", "Failed to start process: " + program);
                Environment.ExitCode = 1;
                return;
            }
            outputTask = PumpAsync(process.StandardOutput, Console.Out);
            errorTask = PumpAsync(process.StandardError, Console.Error);
            await process.WaitForExitAsync(Program.Cancellation);
            var output = await outputTask;
            var error = await errorTask;
            Environment.ExitCode = process.ExitCode;
            if (Program.JsonOutput)
            {
                PatchCommand.WriteJson(new { success = process.ExitCode == 0, command = "execute program", program, exitCode = process.ExitCode, output, error, warnings = Array.Empty<string>() });
            }
        }
        catch (OperationCanceledException)
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            throw;
        }
        catch (Exception ex)
        {
            PatchCommand.WriteErrorJsonOrText("execute program", $"Error executing {program}: {ex.Message}");
            Environment.ExitCode = 1;
        }
        finally
        {
            // Observe both readers so a cancelled or failed run leaves no unobserved task; killing the process ends them.
            foreach (var task in new[] { outputTask, errorTask })
            {
                if (task is null) continue;
                try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // the pipe was closed under the reader, or a grandchild still holds it
                }
            }
            process?.Dispose();
        }
    }

    // Programs run in their own process, so their text is copied as it arrives and only collected for --json.
    private static async Task<string> PumpAsync(StreamReader reader, TextWriter target)
    {
        var collected = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            collected.Append(buffer, 0, read);
            if (!Program.JsonOutput) await target.WriteAsync(buffer.AsMemory(0, read));
        }
        return collected.ToString();
    }
}
