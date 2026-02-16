using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using G3MToolCLI.Commands;
using G3MToolCLI.Services;

namespace G3MToolCLI;

class Program
{
    public static bool JsonOutput { get; set; }

    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Cross-platform tool for various actions with GameMaker data files.")
        {
            Name = "G3MTool"
        };

        // Add commands
        rootCommand.AddCommand(XPatchCommand.Create());
        rootCommand.AddCommand(ExecuteCommand.Create());
        rootCommand.AddCommand(PatchCommand.Create());
        rootCommand.AddCommand(InfoCommand.Create());
        rootCommand.AddCommand(DiffCommand.Create());

        // Global options
        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "Enable verbose output");
        rootCommand.AddGlobalOption(verboseOption);

        var logOption = new Option<string?>(
            aliases: ["--log", "-l"],
            description: "Enable logging. Default: logs/{command}_{timestamp}.log");

        var jsonOption = new Option<bool>(
            name: "--json",
            description: "Output in JSON format (for info, patch validate)");

        rootCommand.AddGlobalOption(logOption);
        rootCommand.AddGlobalOption(jsonOption);

        // Build parser with middleware to apply global options before any command handler
        var parser = new CommandLineBuilder(rootCommand)
            .UseDefaults()
            .AddMiddleware(async (context, next) =>
            {
                var parseResult = context.ParseResult;
                LogService.Verbose = parseResult.GetValueForOption(verboseOption);
                JsonOutput = parseResult.GetValueForOption(jsonOption);
                await next(context);
            })
            .Build();

        // Interactive mode when no arguments provided
        if (args.Length == 0)
        {
            return await RunInteractiveMode(parser);
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
        Console.WriteLine("G3MTool - by Y114");
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
}
