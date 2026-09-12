using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
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

    private static async Task<int> Main(string[] args)
    {
        GameSpecificResolver.BaseDirectory = PlatformUtil.GetExecutableDirectory();
        ModdingMetadata.ToolName = "G3MTool";
        ModdingMetadata.ToolVersion = AppVersionService.Version;
        G3MLib.Modding.Logging.LogService.MessageLogged += ForwardModdingLog;
        G3MLib.Modding.Logging.LogService.ProgressReported += ForwardModdingProgress;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--") break;
            if (args[i] == "-V")
            {
                args[i] = "--version";
            }
        }
        RootCommand rootCommand = new RootCommand("Create, apply, merge, inspect, and compare GameMaker data-file patches.");
        rootCommand.Add(XPatchCommand.Create());
        rootCommand.Add(ExecuteCommand.Create());
        rootCommand.Add(PatchCommand.Create());
        rootCommand.Add(InfoCommand.Create());
        rootCommand.Add(DiffCommand.Create());
        Option<bool> verboseOption = new Option<bool>("--verbose", ["-v"]) { Description = "Enable verbose output" };
        verboseOption.Recursive = true;
        rootCommand.Add(verboseOption);
        Option<string?> logOption = new Option<string?>("--log", ["-l"]) { Description = "Enable logging. Default: logs/{command}_{timestamp}.log" };
        Option<bool> jsonOption = new Option<bool>("--json") { Description = "Output machine-readable JSON for supported commands" };
        Option<string?> xdeltaPathOption = new Option<string?>("--xdelta-path") { Description = "Use this xdelta executable instead of the bundled binary." };
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
        int exitCode = await InvokeAsync(rootCommand, args, verboseOption, logOption, jsonOption, xdeltaPathOption);
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
            string? input = Console.ReadLine();
            if (input is null) return 0;
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }
            string trimmedInput = input.Trim();
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
            string[] commandArgs = ParseCommandLine(trimmedInput);
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
        ParseResult parseResult = rootCommand.Parse(args);
        LogService.Verbose = parseResult.GetValue(verboseOption);
        JsonOutput = parseResult.GetValue(jsonOption);
        XDeltaPathOverride = parseResult.GetValue(xdeltaPathOption);
        G3MLib.Modding.XDelta.XDeltaService.DefaultExecutablePath = XDeltaPathOverride ?? EmbeddedXDeltaPathProvider.GetPath();
        LogService.Suppress = JsonOutput;
        G3MLib.Modding.Logging.LogService.Verbose = LogService.Verbose;
        G3MLib.Modding.Logging.LogService.Suppress = LogService.Suppress;
        LogService.SetFileLogging(ResolveLogPath(parseResult.CommandResult.Command.Name, parseResult.GetValue(logOption)));
        try
        {
            return await parseResult.InvokeAsync();
        }
        finally
        {
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
        List<string> args = new List<string>();
        StringBuilder currentArg = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in input)
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
        return args.ToArray();
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
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return Path.Combine(PlatformUtil.GetExecutableDirectory(), "logs", commandName + "_" + timestamp + ".log");
    }
}
