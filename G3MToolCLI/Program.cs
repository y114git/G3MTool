using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using G3MToolCLI.Commands;
using G3MToolCLI.Services;
using G3MToolCLI.Utils;
using UndertaleModLib.Decompiler;

namespace G3MToolCLI;

class Program
{
    public static bool JsonOutput { get; set; }
    public static string? XDeltaPathOverride { get; private set; }

    static async Task<int> Main(string[] args)
    {
        GameSpecificResolver.BaseDirectory = PlatformUtil.GetExecutableDirectory();

        var rootCommand = new RootCommand("Create, apply, merge, inspect, and compare GameMaker data-file patches.")
        {
            Name = "G3MTool"
        };

        rootCommand.AddCommand(XPatchCommand.Create());
        rootCommand.AddCommand(ExecuteCommand.Create());
        rootCommand.AddCommand(PatchCommand.Create());
        rootCommand.AddCommand(InfoCommand.Create());
        rootCommand.AddCommand(DiffCommand.Create());

        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "Enable verbose output");
        rootCommand.AddGlobalOption(verboseOption);

        var logOption = new Option<string?>(
            aliases: ["--log", "-l"],
            description: "Enable logging. Default: logs/{command}_{timestamp}.log");

        var jsonOption = new Option<bool>(
            name: "--json",
            description: "Output machine-readable JSON for supported commands");
        var xdeltaPathOption = new Option<string?>(
            name: "--xdelta-path",
            description: "Use this xdelta executable instead of the bundled binary.");

        rootCommand.AddGlobalOption(logOption);
        rootCommand.AddGlobalOption(jsonOption);
        rootCommand.AddGlobalOption(xdeltaPathOption);

        var parser = new CommandLineBuilder(rootCommand)
            .UseDefaults()
            .UseVersionOption(["--version", "-V"])
            .AddMiddleware(async (context, next) =>
            {
                var parseResult = context.ParseResult;
                LogService.Verbose = parseResult.GetValueForOption(verboseOption);
                JsonOutput = parseResult.GetValueForOption(jsonOption);
                XDeltaPathOverride = parseResult.GetValueForOption(xdeltaPathOption);
                LogService.Suppress = JsonOutput;
                var requestedLogPath = parseResult.GetValueForOption(logOption);
                var resolvedLogPath = ResolveLogPath(parseResult.CommandResult.Command.Name, requestedLogPath);
                LogService.SetFileLogging(resolvedLogPath);
                try
                {
                    await next(context);
                }
                finally
                {
                    LogService.Shutdown();
                }
            })
            .Build();

        if (args.Length == 0)
        {
            return await RunInteractiveMode(parser);
        }

        if (args.Length == 1 && (args[0] == "--version" || args[0] == "-V"))
        {
            Console.WriteLine(AppVersionService.Version);
            return 0;
        }

        var exitCode = await parser.InvokeAsync(args);

        if (exitCode == 0 && Environment.ExitCode != 0)
        {
            return Environment.ExitCode;
        }

        return exitCode;
    }

    static async Task<int> RunInteractiveMode(Parser parser)
    {
        Console.WriteLine($"{AppVersionService.GetBannerText()} - by Y114");
        Console.WriteLine("Type 'help' for available commands or 'exit' to quit");
        Console.WriteLine();

        while (true)
        {
            Console.Write("(G3MTool) ");
            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input))
                continue;

            var trimmedInput = input.Trim();

            if (trimmedInput.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                trimmedInput.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Exiting G3MTool...");
                return 0;
            }

            if (trimmedInput.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
                trimmedInput.Equals("cls", StringComparison.OrdinalIgnoreCase))
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
                await parser.InvokeAsync(commandArgs);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
            }

            Console.WriteLine();
        }
    }

    static string[] ParseCommandLine(string input)
    {
        var args = new List<string>();
        var currentArg = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];

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

    static string? ResolveLogPath(string commandName, string? requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            return null;

        if (!requestedPath.Equals("default", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(requestedPath);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return Path.Combine(
            PlatformUtil.GetExecutableDirectory(),
            "logs",
            $"{commandName}_{timestamp}.log");
    }
}
