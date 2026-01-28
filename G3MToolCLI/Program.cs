using System.CommandLine;
using G3MToolCLI.Commands;
using G3MToolCLI.Services;

namespace G3MToolCLI;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("G3MTool - Cross-platform CLI tool for GameMaker data file patching")
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

        // Set verbose handler
        rootCommand.SetHandler((verbose) =>
        {
            LogService.Verbose = verbose;
        }, verboseOption);

        var logOption = new Option<string?>(
            aliases: ["--log", "-l"],
            description: "Enable logging. Default: logs/{command}_{timestamp}.log");
        
        var jsonOption = new Option<bool>(
            name: "--json",
            description: "Output in JSON format (for info, patch validate)");

        rootCommand.AddGlobalOption(logOption);
        rootCommand.AddGlobalOption(jsonOption);

        // Interactive mode when no arguments provided
        if (args.Length == 0)
        {
            return await RunInteractiveMode(rootCommand);
        }

        return await rootCommand.InvokeAsync(args);
    }

    static async Task<int> RunInteractiveMode(RootCommand rootCommand)
    {
        Console.WriteLine("G3MTool - Interactive Mode");
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

            var commandArgs = ParseCommandLine(trimmedInput);

            try
            {
                await rootCommand.InvokeAsync(commandArgs);
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

        return args.ToArray();
    }
}
