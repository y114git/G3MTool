using System.CommandLine;
using System.Text.Json;
using G3MToolCLI.Services;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

public static class InfoCommand
{
    public static Command Create()
    {
        var command = new Command("info", "Display information about data files or patches");

        var targetArg = new Argument<FileInfo>("target", "Path to data.win or patch.zip");
        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "Show detailed information");

        command.AddArgument(targetArg);
        command.AddOption(verboseOption);

        command.SetHandler(async (target, verbose) =>
        {
            // TODO: Get --json from global options when needed
            var jsonOutput = false;

            var extension = Path.GetExtension(target.FullName).ToLowerInvariant();
            
            if (extension == ".zip")
            {
                await ShowPatchInfoAsync(target.FullName, verbose, jsonOutput);
            }
            else
            {
                await ShowDataFileInfoAsync(target.FullName, verbose, jsonOutput);
            }
        }, targetArg, verboseOption);

        return command;
    }

    private static async Task ShowDataFileInfoAsync(string path, bool verbose, bool jsonOutput)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            var data = UndertaleModLib.UndertaleIO.Read(stream);

            var generalInfo = data.GeneralInfo;
            
            var versionDisplay = GeneralInfoHelper.GetVersionDisplay(generalInfo);

            if (jsonOutput)
            {
                var info = new
                {
                    File = Path.GetFileName(path),
                    Size = new FileInfo(path).Length,
                    Game = generalInfo?.DisplayName?.Content ?? "Unknown",
                    BytecodeVersion = generalInfo?.BytecodeVersion ?? 0,
                    Version = versionDisplay,
                    GeneralInfo = verbose ? GeneralInfoHelper.ExtractGeneralInfo(data) : null,
                    Resources = new
                    {
                        Sprites = data.Sprites?.Count ?? 0,
                        Sounds = data.Sounds?.Count ?? 0,
                        Backgrounds = data.Backgrounds?.Count ?? 0,
                        Fonts = data.Fonts?.Count ?? 0,
                        Code = data.Code?.Count ?? 0,
                        Scripts = data.Scripts?.Count ?? 0,
                        GameObjects = data.GameObjects?.Count ?? 0,
                        Rooms = data.Rooms?.Count ?? 0,
                        Shaders = data.Shaders?.Count ?? 0,
                        Paths = data.Paths?.Count ?? 0,
                        Timelines = data.Timelines?.Count ?? 0,
                        Extensions = data.Extensions?.Count ?? 0,
                        AudioGroups = data.AudioGroups?.Count ?? 0,
                        TextureGroupInfo = data.TextureGroupInfo?.Count ?? 0,
                        Tilesets = data.Backgrounds?.Count ?? 0
                    }
                };
                Console.WriteLine(JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine($"Data File: {Path.GetFileName(path)}");
                Console.WriteLine($"Size: {new FileInfo(path).Length:N0} bytes");
                Console.WriteLine($"Game: {generalInfo?.DisplayName?.Content ?? "Unknown"}");
                Console.WriteLine($"Bytecode: {generalInfo?.BytecodeVersion ?? 0}");
                Console.WriteLine($"Version: {versionDisplay}");
                Console.WriteLine();
                Console.WriteLine("Resources:");
                Console.WriteLine($"  Sprites: {data.Sprites?.Count ?? 0}");
                Console.WriteLine($"  Sounds: {data.Sounds?.Count ?? 0}");
                Console.WriteLine($"  Code: {data.Code?.Count ?? 0}");
                Console.WriteLine($"  GameObjects: {data.GameObjects?.Count ?? 0}");
                Console.WriteLine($"  Rooms: {data.Rooms?.Count ?? 0}");
                
                if (verbose)
                {
                    Console.WriteLine($"  Backgrounds: {data.Backgrounds?.Count ?? 0}");
                    Console.WriteLine($"  Fonts: {data.Fonts?.Count ?? 0}");
                    Console.WriteLine($"  Scripts: {data.Scripts?.Count ?? 0}");
                    Console.WriteLine($"  Shaders: {data.Shaders?.Count ?? 0}");
                    Console.WriteLine($"  Paths: {data.Paths?.Count ?? 0}");
                    Console.WriteLine($"  Timelines: {data.Timelines?.Count ?? 0}");
                    Console.WriteLine($"  Extensions: {data.Extensions?.Count ?? 0}");
                    Console.WriteLine($"  AudioGroups: {data.AudioGroups?.Count ?? 0}");
                    Console.WriteLine($"  TextureGroupInfo: {data.TextureGroupInfo?.Count ?? 0}");
                    Console.WriteLine($"  Tilesets: {data.Backgrounds?.Count ?? 0}");
                    
                    if (generalInfo != null)
                    {
                        GeneralInfoHelper.PrintVerboseGeneralInfo(generalInfo);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error reading data file: {ex.Message}");
            Environment.ExitCode = 1;
        }

        await Task.CompletedTask;
    }

    private static async Task ShowPatchInfoAsync(string path, bool verbose, bool jsonOutput)
    {
        var patchService = new PatchService();
        var result = await patchService.ValidatePatchAsync(path, null);

        if (!result.Success || result.Manifest == null)
        {
            Console.Error.WriteLine($"Error reading patch: {result.Error}");
            Environment.ExitCode = 1;
            return;
        }

        var manifest = result.Manifest;

        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"G3M Patch: {Path.GetFileName(path)}");
            Console.WriteLine($"Version: {manifest.Version}");
            Console.WriteLine($"Created: {manifest.CreatedAt}");
            Console.WriteLine($"Tool: {manifest.Tool?.Name} v{manifest.Tool?.Version}");
            Console.WriteLine();
            Console.WriteLine("Original:");
            Console.WriteLine($"  File: {manifest.Original?.Filename}");
            Console.WriteLine($"  Size: {manifest.Original?.Size:N0} bytes");
            Console.WriteLine($"  GMS: {manifest.Original?.GmsVersion}");
            Console.WriteLine();
            Console.WriteLine("Statistics:");
            Console.WriteLine($"  Changed: {manifest.Statistics?.TotalChanged ?? 0}");
            Console.WriteLine($"  New: {manifest.Statistics?.TotalNew ?? 0}");
            Console.WriteLine($"  Deleted: {manifest.Statistics?.TotalDeleted ?? 0}");

            if (verbose && manifest.Resources != null)
            {
                Console.WriteLine();
                Console.WriteLine("Resources by type:");
                foreach (var (type, resources) in manifest.Resources)
                {
                    var changed = resources.Changed?.Count ?? 0;
                    var newRes = resources.New?.Count ?? 0;
                    var deleted = resources.Deleted?.Count ?? 0;
                    if (changed + newRes + deleted > 0)
                    {
                        Console.WriteLine($"  {type}: {changed} changed, {newRes} new, {deleted} deleted");
                    }
                }
            }
        }
    }
}
