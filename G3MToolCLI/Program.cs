using System.CommandLine;
using System.Text;
using G3MLib.DataFile.Decompiler;
using G3MLib.Modding;
using G3MToolCLI.Commands;
using G3MToolCLI.Services.Application;
using G3MToolCLI.Services.Execution;
using G3MToolCLI.Services.Logging;
using G3MToolCLI.Utils;

namespace G3MToolCLI;

internal static class Program
{
    public static bool JsonOutput { get; set; }

    public static string? XDeltaPathOverride { get; private set; }

    public static CancellationToken Cancellation { get; private set; }

    private static CancellationTokenSource? s_cancellation;

    private static async Task<int> Main(string[] args)
    {
        GameSpecificResolver.BaseDirectory = PlatformUtil.GetExecutableDirectory();
        ModdingMetadata.ToolName = "G3MTool";
        ModdingMetadata.ToolVersion = AppVersionService.Version;
        G3MLib.Modding.Logging.LogService.MessageLogged += ForwardModdingLog;
        G3MLib.Modding.Logging.LogService.ProgressReported += ForwardModdingProgress;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--") break;
            if (args[i] == "-V")
            {
                args[i] = "--version";
            }
        }
        Console.CancelKeyPress += (_, e) =>
        {
            var running = s_cancellation;
            if (running is null) return;
            e.Cancel = true;
            running.Cancel();
        };
        var rootCommand = new RootCommand("Create, apply, merge, inspect, and compare GameMaker data-file patches.")
        {
            XPatchCommand.Create(),
            ExecuteCommand.Create(),
            PatchCommand.Create(),
            InfoCommand.Create(),
            DiffCommand.Create()
        };
        var verboseOption = new Option<bool>("--verbose", ["-v"])
        {
            Description = "Enable verbose output",
            Recursive = true
        };
        rootCommand.Add(verboseOption);
        var logOption = new Option<string?>("--log", ["-l"]) { Description = "Enable logging. Default: logs/{command}_{timestamp}.log" };
        var jsonOption = new Option<bool>("--json") { Description = "Output machine-readable JSON for supported commands" };
        var xdeltaPathOption = new Option<string?>("--xdelta-path") { Description = "Use this xdelta executable instead of the bundled binary." };
        logOption.Recursive = true;
        jsonOption.Recursive = true;
        xdeltaPathOption.Recursive = true;
        rootCommand.Add(logOption);
        rootCommand.Add(jsonOption);
        rootCommand.Add(xdeltaPathOption);
        if (args.Length == 0)
        {
            return await RunInteractiveMode(rootCommand, verboseOption, logOption, jsonOption, xdeltaPathOption);
        }
        if (args.Length == 1 && (args[0] == "--version" || args[0] == "-V"))
        {
            Console.WriteLine(AppVersionService.Version);
            return 0;
        }
        var exitCode = await InvokeAsync(rootCommand, args, verboseOption, logOption, jsonOption, xdeltaPathOption);
        if (exitCode == 0 && Environment.ExitCode != 0)
        {
            return Environment.ExitCode;
        }
        return exitCode;
    }

    private static async Task<int> RunInteractiveMode(RootCommand rootCommand, Option<bool> verboseOption, Option<string?> logOption, Option<bool> jsonOption, Option<string?> xdeltaPathOption)
    {
        Console.WriteLine(AppVersionService.GetBannerText() + " - by Y114");
        Console.WriteLine("Type 'help' for available commands or 'exit' to quit");
        Console.WriteLine();
        while (true)
        {
            Console.Write("(G3MTool) ");
            var input = Console.ReadLine();
            if (input is null) return 0;
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }
            var trimmedInput = input.Trim();
            if (trimmedInput.Equals("exit", StringComparison.OrdinalIgnoreCase) || trimmedInput.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            if (trimmedInput.Equals("clear", StringComparison.OrdinalIgnoreCase) || trimmedInput.Equals("cls", StringComparison.OrdinalIgnoreCase))
            {
                Console.Clear();
                continue;
            }
            if (trimmedInput.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                trimmedInput = "--help";
            }
            var commandArgs = ParseCommandLine(trimmedInput);
            try
            {
                await InvokeAsync(rootCommand, commandArgs, verboseOption, logOption, jsonOption, xdeltaPathOption);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: " + ex.Message);
                Console.ResetColor();
            }
            Console.WriteLine();
        }
        Console.WriteLine("Exiting G3MTool...");
        return 0;
    }

    private static async Task<int> InvokeAsync(RootCommand rootCommand, string[] args, Option<bool> verboseOption, Option<string?> logOption, Option<bool> jsonOption, Option<string?> xdeltaPathOption)
    {
        var parseResult = rootCommand.Parse(args);
        LogService.Verbose = parseResult.GetValue(verboseOption);
        JsonOutput = parseResult.GetValue(jsonOption);
        XDeltaPathOverride = parseResult.GetValue(xdeltaPathOption);
        G3MLib.Modding.XDelta.XDeltaService.DefaultExecutablePath = XDeltaPathOverride ?? EmbeddedXDeltaPathProvider.GetPath();
        LogService.Suppress = JsonOutput;
        G3MLib.Modding.Logging.LogService.Verbose = LogService.Verbose;
        G3MLib.Modding.Logging.LogService.Suppress = LogService.Suppress;
        LogService.SetFileLogging(ResolveLogPath(parseResult.CommandResult.Command.Name, parseResult.GetValue(logOption)));
        using var cancellation = new CancellationTokenSource();
        s_cancellation = cancellation;
        Cancellation = cancellation.Token;
        try
        {
            var exitCode = await parseResult.InvokeAsync();
            if (!cancellation.IsCancellationRequested) return exitCode;
            await Console.Error.WriteLineAsync("Cancelled.");
            Environment.ExitCode = 130;
            return 130;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("Cancelled.");
            Environment.ExitCode = 130;
            return 130;
        }
        finally
        {
            s_cancellation = null;
            LogService.Shutdown();
        }
    }

    private static void ForwardModdingLog(G3MLib.Modding.Logging.ModdingLogMessage message)
    {
        switch (message.Level)
        {
            case G3MLib.Modding.Logging.ModdingLogLevel.Information:
                LogService.Info(message.Message);
                break;
            case G3MLib.Modding.Logging.ModdingLogLevel.Warning:
                LogService.Warning(message.Message);
                break;
            case G3MLib.Modding.Logging.ModdingLogLevel.Error:
                LogService.Error(message.Message);
                break;
            default:
                LogService.Log(message.Message);
                break;
        }
    }

    private static void ForwardModdingProgress(G3MLib.Modding.Logging.ModdingProgress progress)
    {
        if (progress.IsComplete)
        {
            LogService.ProgressComplete();
            return;
        }

        LogService.SetOperation(progress.Operation);
        LogService.Progress(progress.Current, progress.Total);
    }

    private static string[] ParseCommandLine(string input)
    {
        List<string> args = [];
        var currentArg = new StringBuilder();
        var inQuotes = false;
        foreach (var c in input)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (currentArg.Length > 0)
                {
                    args.Add(currentArg.ToString());
                    currentArg.Clear();
                }
            }
            else
            {
                currentArg.Append(c);
            }
        }
        if (currentArg.Length > 0)
        {
            args.Add(currentArg.ToString());
        }
        return [.. args];
    }

    private static string? ResolveLogPath(string commandName, string? requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return null;
        }
        if (!requestedPath.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(requestedPath);
        }
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(PlatformUtil.GetExecutableDirectory(), "logs", commandName + "_" + timestamp + ".log");
    }
}
