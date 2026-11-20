using System.Text.Json;
using G3MLib.DataFile;
using G3MLib.DataFile.Models;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

/// <summary>Prints what <c>info</c> shows for a data file: from the loaded data, or from a cached snapshot.</summary>
internal static class InfoPrinter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true
    };

    // Label in the text table, key in the resource counts.
    private static readonly (string Label, string Key)[] s_resourceRows =
    [
        ("Sprites", "Sprites"), ("Sounds", "Sounds"), ("Code", "Code"), ("GameObjects", "GameObjects"), ("Rooms", "Rooms"),
        ("Backgrounds", "Backgrounds"), ("Fonts", "Fonts"), ("Scripts", "Scripts"), ("Shaders", "Shaders"), ("Paths", "Paths"),
        ("Timelines", "Timelines"), ("Extensions", "Extensions"), ("Variables", "Variables"), ("Functions", "Functions"),
        ("Strings", "Strings"), ("AudioGroups", "AudioGroups"), ("EmbTextures", "EmbeddedTextures"), ("TexPageItems", "TexturePageItems"),
        ("TexGroupInfo", "TextureGroupInfo"), ("Tilesets", "Tilesets")
    ];

    public static void PrintDataFile(string path, GameMakerData data, bool verbose, bool jsonOutput)
    {
        var generalInfo = data.GeneralInfo;
        var counts = CountResources(data);
        var size = new FileInfo(path).Length;
        var game = generalInfo?.DisplayName?.Content ?? "Unknown";
        var version = GeneralInfoUtil.GetVersionDisplay(generalInfo);
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                File = Path.GetFileName(path),
                Size = size,
                Game = game,
                BytecodeVersion = generalInfo?.BytecodeVersion ?? 0,
                Version = version,
                GeneralInfo = GeneralInfoUtil.ExtractGeneralInfo(data),
                Resources = counts
            }, s_jsonOptions));
            return;
        }
        PrintHeader(Path.GetFileName(path), size, game, generalInfo?.BytecodeVersion ?? 0, version);
        PrintResourceTable(key => counts[key]);
        if (generalInfo != null)
        {
            Console.WriteLine();
            GeneralInfoUtil.PrintVerboseGeneralInfo(generalInfo, verbose);
        }
        if (verbose)
        {
            InfoResourceListing.Print(data);
            return;
        }
        PrintVariablesByInstanceType(data);
        PrintFunctionRange(data);
        PrintCodeEntryCounts(data);
        PrintAudioGroups(data);
        PrintExtensions(data);
        PrintRoomOrder(generalInfo);
    }

    public static void PrintSnapshot(G3MDataInfoSnapshot info, bool jsonOutput)
    {
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(info, s_jsonOptions));
            return;
        }
        PrintHeader(info.File, info.Size, info.Game, info.BytecodeVersion, info.Version);
        PrintResourceTable(info.ResourceCounts.GetValueOrDefault);
        PrintGeneralInfoSnapshot(info.GeneralInfo);
        PrintSnapshotLists(info);
    }

    private static Dictionary<string, int> CountResources(GameMakerData data) => new()
    {
        ["Sprites"] = data.Sprites?.Count ?? 0,
        ["Sounds"] = data.Sounds?.Count ?? 0,
        ["Code"] = data.Code?.Count ?? 0,
        ["GameObjects"] = data.GameObjects?.Count ?? 0,
        ["Rooms"] = data.Rooms?.Count ?? 0,
        ["Backgrounds"] = data.Backgrounds?.Count ?? 0,
        ["Fonts"] = data.Fonts?.Count ?? 0,
        ["Scripts"] = data.Scripts?.Count ?? 0,
        ["Shaders"] = data.Shaders?.Count ?? 0,
        ["Paths"] = data.Paths?.Count ?? 0,
        ["Timelines"] = data.Timelines?.Count ?? 0,
        ["Extensions"] = data.Extensions?.Count ?? 0,
        ["Variables"] = data.Variables?.Count ?? 0,
        ["Functions"] = data.Functions?.Count ?? 0,
        ["Strings"] = data.Strings?.Count ?? 0,
        ["AudioGroups"] = data.AudioGroups?.Count ?? 0,
        ["EmbeddedTextures"] = data.EmbeddedTextures?.Count ?? 0,
        ["TexturePageItems"] = data.TexturePageItems?.Count ?? 0,
        ["TextureGroupInfo"] = data.TextureGroupInfo?.Count ?? 0,
        ["Tilesets"] = data.Backgrounds?.Count ?? 0
    };

    private static void PrintHeader(string file, long size, string game, int bytecodeVersion, string? version)
    {
        Console.WriteLine("Data File: " + file);
        Console.WriteLine($"Size: {size:N0} bytes");
        Console.WriteLine("Game: " + game);
        Console.WriteLine($"Bytecode: {bytecodeVersion}");
        Console.WriteLine("Version: " + version);
        Console.WriteLine();
        Console.WriteLine("Resources:");
    }

    private static void PrintResourceTable(Func<string, int> count)
    {
        foreach (var (label, key) in s_resourceRows)
        {
            Console.WriteLine($"  {label + ":",-14}{count(key),6}");
        }
    }

    private static void PrintVariablesByInstanceType(GameMakerData data)
    {
        if (data.Variables is not { Count: > 0 }) return;
        Console.WriteLine();
        Console.WriteLine("Variables by InstanceType:");
        foreach (var g in data.Variables.GroupBy(v => v.InstanceType).OrderByDescending(source => source.Count()))
        {
            Console.WriteLine($"  {g.Key,-12} {g.Count(),6}");
        }
    }

    private static void PrintFunctionRange(GameMakerData data)
    {
        if (data.Functions is not { Count: > 0 }) return;
        Console.WriteLine();
        Console.WriteLine("Functions (first/last):");
        Console.WriteLine("  [0]    " + data.Functions[0]?.Name?.Content);
        Console.WriteLine($"  [{data.Functions.Count - 1}] {data.Functions[data.Functions.Count - 1]?.Name?.Content}");
    }

    private static void PrintCodeEntryCounts(GameMakerData data)
    {
        if (data.Code is not { Count: > 0 }) return;
        var topLevel = data.Code.Count(c => c.ParentEntry == null);
        Console.WriteLine();
        Console.WriteLine($"Code entries: {topLevel} top-level, {data.Code.Count - topLevel} child");
    }

    private static void PrintAudioGroups(GameMakerData data)
    {
        if (data.AudioGroups is not { Count: > 0 }) return;
        Console.WriteLine();
        Console.WriteLine("AudioGroups:");
        for (var i = 0; i < data.AudioGroups.Count; i++)
        {
            Console.WriteLine($"  [{i}] {data.AudioGroups[i]?.Name?.Content}");
        }
    }

    private static void PrintExtensions(GameMakerData data)
    {
        if (data.Extensions is not { Count: > 0 }) return;
        Console.WriteLine();
        Console.WriteLine("Extensions:");
        foreach (var extension in data.Extensions)
        {
            Console.WriteLine("  " + extension?.Name?.Content);
        }
    }

    private static void PrintRoomOrder(GameMakerGeneralInfo? generalInfo)
    {
        if (generalInfo?.RoomOrder is not { Count: > 0 } roomOrder) return;
        Console.WriteLine();
        Console.WriteLine($"Room order ({roomOrder.Count} rooms):");
        for (var i = 0; i < Math.Min(10, roomOrder.Count); i++)
        {
            Console.WriteLine($"  [{i}] {roomOrder[i]?.Resource?.Name?.Content ?? "?"}");
        }
        if (roomOrder.Count > 10)
        {
            Console.WriteLine($"  ... and {roomOrder.Count - 10} more");
        }
    }

    private static void PrintGeneralInfoSnapshot(GeneralInfoData? generalInfo)
    {
        if (generalInfo == null) return;
        Console.WriteLine();
        Console.WriteLine("GeneralInfo:");
        Console.WriteLine("  DisplayName: " + generalInfo.DisplayName);
        Console.WriteLine("  Name:        " + generalInfo.Name);
        Console.WriteLine("  FileName:    " + generalInfo.FileName);
        Console.WriteLine("  Config:      " + generalInfo.Config);
        Console.WriteLine($"  GameID:      {generalInfo.GameID}");
        Console.WriteLine($"  Version:     {generalInfo.Major}.{generalInfo.Minor}.{generalInfo.Release}.{generalInfo.Build}");
    }

    private static void PrintSnapshotLists(G3MDataInfoSnapshot info)
    {
        var functionCount = info.ResourceCounts.GetValueOrDefault("Functions");
        if (info.VariablesByInstanceType.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Variables by InstanceType:");
            foreach (var (key, count) in info.VariablesByInstanceType.OrderByDescending(kvp => kvp.Value))
            {
                Console.WriteLine($"  {key,-12} {count,6}");
            }
        }
        if (info.FirstFunction != null || info.LastFunction != null)
        {
            Console.WriteLine();
            Console.WriteLine("Functions (first/last):");
            Console.WriteLine("  [0]    " + info.FirstFunction);
            Console.WriteLine($"  [{Math.Max(functionCount - 1, 0)}] {info.LastFunction}");
        }
        if (info.ResourceCounts.GetValueOrDefault("Code") > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Code entries: {info.TopLevelCodeCount} top-level, {info.ChildCodeCount} child");
        }
        PrintSnapshotNames("AudioGroups:", info.AudioGroups);
        PrintSnapshotNames("Extensions:", info.Extensions, indexed: false);
        if (info.RoomOrderCount > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Room order ({info.RoomOrderCount} rooms):");
            for (var i = 0; i < info.RoomOrderPreview.Count; i++)
            {
                Console.WriteLine($"  [{i}] {info.RoomOrderPreview[i]}");
            }
            if (info.RoomOrderCount > info.RoomOrderPreview.Count)
            {
                Console.WriteLine($"  ... and {info.RoomOrderCount - info.RoomOrderPreview.Count} more");
            }
        }
    }

    private static void PrintSnapshotNames(string title, List<string> names, bool indexed = true)
    {
        if (names.Count == 0) return;
        Console.WriteLine();
        Console.WriteLine(title);
        for (var i = 0; i < names.Count; i++)
        {
            Console.WriteLine(indexed ? $"  [{i}] {names[i]}" : "  " + names[i]);
        }
    }
}
