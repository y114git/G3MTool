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

        return await rootCommand.InvokeAsync(args);
    }
}
