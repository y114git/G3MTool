using System.CommandLine;
using System.Text.Json;
using G3MToolCLI.Models;
using G3MToolCLI.Services;
using G3MToolCLI.Utils;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace G3MToolCLI.Commands;

public static class InfoCommand
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private static bool IsPatchExtension(string? extension)
    {
        return extension?.ToLowerInvariant() is ".g3mpatch" or ".zip";
    }

    public static Command Create()
    {
        var command = new Command("info", "Show metadata for a data file or .g3mpatch.\n  Usage: info <target> [--cache <dir>]\n  Without -v: counts, GeneralInfo, and short breakdowns\n  With -v: full per-resource listing");

        var targetArg = new Argument<FileInfo>("target", "Path to data file (.win/.ios/.droid/.unx) or .g3mpatch");
        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "Show full per-resource listing");
        var cacheOption = new Option<DirectoryInfo?>(
            name: "--cache",
            description: "Read and write reusable .g3mcache info analysis in this directory.");

        command.AddArgument(targetArg);
        command.AddOption(verboseOption);
        command.AddOption(cacheOption);

        command.SetHandler(async (target, verbose, cacheDir) =>
        {
            var jsonOutput = Program.JsonOutput;
            var cacheOptions = G3MCacheOptions.FromDirectory(cacheDir?.FullName);

            var extension = Path.GetExtension(target.FullName);

            if (IsPatchExtension(extension))
            {
                await ShowPatchInfoAsync(target.FullName, verbose, jsonOutput);
            }
            else
            {
                await ShowDataFileInfoAsync(target.FullName, verbose, jsonOutput, cacheOptions);
            }
        }, targetArg, verboseOption, cacheOption);

        return command;
    }

    private static async Task ShowDataFileInfoAsync(string path, bool verbose, bool jsonOutput, G3MCacheOptions? cacheOptions)
    {
        try
        {
            if (!verbose)
            {
                var cached = G3MCacheService.TryReadDataInfoSnapshot(path, cacheOptions);
                if (cached != null)
                {
                    PrintInfoSnapshot(cached, jsonOutput);
                    return;
                }
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            using var data = UndertaleIO.Read(stream);
            var snapshot = G3MCacheService.BuildInfoSnapshot(path, data);
            if (!verbose)
                await G3MCacheService.WriteDataInfoCacheAsync(path, snapshot, cacheOptions);

            var generalInfo = data.GeneralInfo;
            var versionDisplay = GeneralInfoUtil.GetVersionDisplay(generalInfo);

            if (jsonOutput)
            {
                var info = new
                {
                    File = Path.GetFileName(path),
                    Size = new FileInfo(path).Length,
                    Game = generalInfo?.DisplayName?.Content ?? "Unknown",
                    BytecodeVersion = generalInfo?.BytecodeVersion ?? 0,
                    Version = versionDisplay,
                    GeneralInfo = GeneralInfoUtil.ExtractGeneralInfo(data),
                    Resources = new
                    {
                        Sprites = data.Sprites?.Count ?? 0,
                        Sounds = data.Sounds?.Count ?? 0,
                        Code = data.Code?.Count ?? 0,
                        GameObjects = data.GameObjects?.Count ?? 0,
                        Rooms = data.Rooms?.Count ?? 0,
                        Backgrounds = data.Backgrounds?.Count ?? 0,
                        Fonts = data.Fonts?.Count ?? 0,
                        Scripts = data.Scripts?.Count ?? 0,
                        Shaders = data.Shaders?.Count ?? 0,
                        Paths = data.Paths?.Count ?? 0,
                        Timelines = data.Timelines?.Count ?? 0,
                        Extensions = data.Extensions?.Count ?? 0,
                        Variables = data.Variables?.Count ?? 0,
                        Functions = data.Functions?.Count ?? 0,
                        Strings = data.Strings?.Count ?? 0,
                        AudioGroups = data.AudioGroups?.Count ?? 0,
                        EmbeddedTextures = data.EmbeddedTextures?.Count ?? 0,
                        TexturePageItems = data.TexturePageItems?.Count ?? 0,
                        TextureGroupInfo = data.TextureGroupInfo?.Count ?? 0,
                        Tilesets = data.Backgrounds?.Count ?? 0
                    }
                };
                Console.WriteLine(JsonSerializer.Serialize(info, s_jsonOptions));
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
                Console.WriteLine($"  Sprites:      {data.Sprites?.Count ?? 0,6}");
                Console.WriteLine($"  Sounds:       {data.Sounds?.Count ?? 0,6}");
                Console.WriteLine($"  Code:         {data.Code?.Count ?? 0,6}");
                Console.WriteLine($"  GameObjects:  {data.GameObjects?.Count ?? 0,6}");
                Console.WriteLine($"  Rooms:        {data.Rooms?.Count ?? 0,6}");
                Console.WriteLine($"  Backgrounds:  {data.Backgrounds?.Count ?? 0,6}");
                Console.WriteLine($"  Fonts:        {data.Fonts?.Count ?? 0,6}");
                Console.WriteLine($"  Scripts:      {data.Scripts?.Count ?? 0,6}");
                Console.WriteLine($"  Shaders:      {data.Shaders?.Count ?? 0,6}");
                Console.WriteLine($"  Paths:        {data.Paths?.Count ?? 0,6}");
                Console.WriteLine($"  Timelines:    {data.Timelines?.Count ?? 0,6}");
                Console.WriteLine($"  Extensions:   {data.Extensions?.Count ?? 0,6}");
                Console.WriteLine($"  Variables:    {data.Variables?.Count ?? 0,6}");
                Console.WriteLine($"  Functions:    {data.Functions?.Count ?? 0,6}");
                Console.WriteLine($"  Strings:      {data.Strings?.Count ?? 0,6}");
                Console.WriteLine($"  AudioGroups:  {data.AudioGroups?.Count ?? 0,6}");
                Console.WriteLine($"  EmbTextures:  {data.EmbeddedTextures?.Count ?? 0,6}");
                Console.WriteLine($"  TexPageItems: {data.TexturePageItems?.Count ?? 0,6}");
                Console.WriteLine($"  TexGroupInfo: {data.TextureGroupInfo?.Count ?? 0,6}");
                Console.WriteLine($"  Tilesets:     {data.Backgrounds?.Count ?? 0,6}");

                if (generalInfo != null)
                {
                    Console.WriteLine();
                    GeneralInfoUtil.PrintVerboseGeneralInfo(generalInfo);
                }

                if (!verbose)
                {
                    if (data.Variables != null && data.Variables.Count > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine("Variables by InstanceType:");
                        foreach (var g in data.Variables.GroupBy(v => v.InstanceType).OrderByDescending(g => g.Count()))
                            Console.WriteLine($"  {g.Key,-12} {g.Count(),6}");
                    }

                    if (data.Functions != null && data.Functions.Count > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine("Functions (first/last):");
                        Console.WriteLine($"  [0]    {data.Functions[0]?.Name?.Content}");
                        Console.WriteLine($"  [{data.Functions.Count - 1}] {data.Functions[data.Functions.Count - 1]?.Name?.Content}");
                    }

                    if (data.Code != null && data.Code.Count > 0)
                    {
                        int topLevel = data.Code.Count(c => c.ParentEntry == null);
                        Console.WriteLine();
                        Console.WriteLine($"Code entries: {topLevel} top-level, {data.Code.Count - topLevel} child");
                    }

                    if (data.AudioGroups != null && data.AudioGroups.Count > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine("AudioGroups:");
                        for (int i = 0; i < data.AudioGroups.Count; i++)
                            Console.WriteLine($"  [{i}] {data.AudioGroups[i]?.Name?.Content}");
                    }

                    if (data.Extensions != null && data.Extensions.Count > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine("Extensions:");
                        foreach (var ext in data.Extensions)
                            Console.WriteLine($"  {ext?.Name?.Content}");
                    }

                    if (generalInfo?.RoomOrder != null && generalInfo.RoomOrder.Count > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"Room order ({generalInfo.RoomOrder.Count} rooms):");
                        for (int i = 0; i < Math.Min(10, generalInfo.RoomOrder.Count); i++)
                        {
                            var room = generalInfo.RoomOrder[i]?.Resource;
                            Console.WriteLine($"  [{i}] {room?.Name?.Content ?? "?"}");
                        }
                        if (generalInfo.RoomOrder.Count > 10)
                            Console.WriteLine($"  ... and {generalInfo.RoomOrder.Count - 10} more");
                    }
                }
                else
                {
                    PrintDetailedResources(data);
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

    private static void PrintInfoSnapshot(G3MDataInfoSnapshot info, bool jsonOutput)
    {
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(info, s_jsonOptions));
            return;
        }

        int Count(string key) => info.ResourceCounts.GetValueOrDefault(key);
        Console.WriteLine($"Data File: {info.File}");
        Console.WriteLine($"Size: {info.Size:N0} bytes");
        Console.WriteLine($"Game: {info.Game}");
        Console.WriteLine($"Bytecode: {info.BytecodeVersion}");
        Console.WriteLine($"Version: {info.Version}");
        Console.WriteLine();
        Console.WriteLine("Resources:");
        Console.WriteLine($"  Sprites:      {Count("Sprites"),6}");
        Console.WriteLine($"  Sounds:       {Count("Sounds"),6}");
        Console.WriteLine($"  Code:         {Count("Code"),6}");
        Console.WriteLine($"  GameObjects:  {Count("GameObjects"),6}");
        Console.WriteLine($"  Rooms:        {Count("Rooms"),6}");
        Console.WriteLine($"  Backgrounds:  {Count("Backgrounds"),6}");
        Console.WriteLine($"  Fonts:        {Count("Fonts"),6}");
        Console.WriteLine($"  Scripts:      {Count("Scripts"),6}");
        Console.WriteLine($"  Shaders:      {Count("Shaders"),6}");
        Console.WriteLine($"  Paths:        {Count("Paths"),6}");
        Console.WriteLine($"  Timelines:    {Count("Timelines"),6}");
        Console.WriteLine($"  Extensions:   {Count("Extensions"),6}");
        Console.WriteLine($"  Variables:    {Count("Variables"),6}");
        Console.WriteLine($"  Functions:    {Count("Functions"),6}");
        Console.WriteLine($"  Strings:      {Count("Strings"),6}");
        Console.WriteLine($"  AudioGroups:  {Count("AudioGroups"),6}");
        Console.WriteLine($"  EmbTextures:  {Count("EmbeddedTextures"),6}");
        Console.WriteLine($"  TexPageItems: {Count("TexturePageItems"),6}");
        Console.WriteLine($"  TexGroupInfo: {Count("TextureGroupInfo"),6}");
        Console.WriteLine($"  Tilesets:     {Count("Tilesets"),6}");

        PrintGeneralInfoSnapshot(info.GeneralInfo);
        if (info.VariablesByInstanceType.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Variables by InstanceType:");
            foreach (var (key, count) in info.VariablesByInstanceType.OrderByDescending(kvp => kvp.Value))
                Console.WriteLine($"  {key,-12} {count,6}");
        }
        if (info.FirstFunction != null || info.LastFunction != null)
        {
            Console.WriteLine();
            Console.WriteLine("Functions (first/last):");
            Console.WriteLine($"  [0]    {info.FirstFunction}");
            Console.WriteLine($"  [{Math.Max(Count("Functions") - 1, 0)}] {info.LastFunction}");
        }
        if (Count("Code") > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Code entries: {info.TopLevelCodeCount} top-level, {info.ChildCodeCount} child");
        }
        if (info.AudioGroups.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("AudioGroups:");
            for (int i = 0; i < info.AudioGroups.Count; i++)
                Console.WriteLine($"  [{i}] {info.AudioGroups[i]}");
        }
        if (info.Extensions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Extensions:");
            foreach (var ext in info.Extensions)
                Console.WriteLine($"  {ext}");
        }
        if (info.RoomOrderCount > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Room order ({info.RoomOrderCount} rooms):");
            for (int i = 0; i < info.RoomOrderPreview.Count; i++)
                Console.WriteLine($"  [{i}] {info.RoomOrderPreview[i]}");
            if (info.RoomOrderCount > info.RoomOrderPreview.Count)
                Console.WriteLine($"  ... and {info.RoomOrderCount - info.RoomOrderPreview.Count} more");
        }
    }

    private static void PrintGeneralInfoSnapshot(GeneralInfoData? generalInfo)
    {
        if (generalInfo == null)
            return;

        Console.WriteLine();
        Console.WriteLine("GeneralInfo:");
        Console.WriteLine($"  DisplayName: {generalInfo.DisplayName}");
        Console.WriteLine($"  Name:        {generalInfo.Name}");
        Console.WriteLine($"  FileName:    {generalInfo.FileName}");
        Console.WriteLine($"  Config:      {generalInfo.Config}");
        Console.WriteLine($"  GameID:      {generalInfo.GameID}");
        Console.WriteLine($"  Version:     {generalInfo.Major}.{generalInfo.Minor}.{generalInfo.Release}.{generalInfo.Build}");
    }

    private static readonly string[] EventTypeNames =
    [
        "Create", "Destroy", "Alarm", "Step", "Collision",
        "Keyboard", "Mouse", "Other", "Draw", "KeyPress",
        "KeyRelease", "Trigger", "CleanUp", "Gesture", "PreCreate"
    ];

    private static string EventTypeName(int idx) =>
        idx >= 0 && idx < EventTypeNames.Length ? EventTypeNames[idx] : $"Type{idx}";

    private static void PrintDetailedResources(UndertaleData data)
    {
        Console.WriteLine();
        Console.WriteLine("=".PadRight(60, '='));
        Console.WriteLine("DETAILED RESOURCE LISTING");
        Console.WriteLine("=".PadRight(60, '='));

        // Sprites
        if (data.Sprites != null && data.Sprites.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Sprites ({data.Sprites.Count}) ===");
            for (int i = 0; i < data.Sprites.Count; i++)
            {
                var s = data.Sprites[i];
                Console.WriteLine($"  [{i}] {s.Name?.Content}  {s.Width}x{s.Height}  origin:({s.OriginX},{s.OriginY})  frames:{s.Textures.Count}  mask:{s.SepMasks}  bbox:({s.MarginLeft},{s.MarginTop},{s.MarginRight},{s.MarginBottom})  speed:{s.GMS2PlaybackSpeed}");
            }
        }

        // Sounds
        if (data.Sounds != null && data.Sounds.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Sounds ({data.Sounds.Count}) ===");
            for (int i = 0; i < data.Sounds.Count; i++)
            {
                var s = data.Sounds[i];
                string grp = s.AudioGroup?.Name?.Content ?? $"group{s.GroupID}";
                Console.WriteLine($"  [{i}] {s.Name?.Content}  type:{s.Type?.Content}  flags:{s.Flags}  vol:{s.Volume:F2}  group:{grp}  file:{s.File?.Content}");
            }
        }

        // Backgrounds
        if (data.Backgrounds != null && data.Backgrounds.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Backgrounds/Tilesets ({data.Backgrounds.Count}) ===");
            for (int i = 0; i < data.Backgrounds.Count; i++)
            {
                var b = data.Backgrounds[i];
                string tpi = b.Texture != null ? $"tpi:{data.TexturePageItems.IndexOf(b.Texture)}" : "no-tex";
                Console.WriteLine($"  [{i}] {b.Name?.Content}  {tpi}  tileWidth:{b.GMS2TileWidth}  tileHeight:{b.GMS2TileHeight}  cols:{b.GMS2TileColumns}  count:{b.GMS2TileCount}");
            }
        }

        // Fonts
        if (data.Fonts != null && data.Fonts.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Fonts ({data.Fonts.Count}) ===");
            for (int i = 0; i < data.Fonts.Count; i++)
            {
                var f = data.Fonts[i];
                Console.WriteLine($"  [{i}] {f.Name?.Content}  display:{f.DisplayName?.Content}  size:{f.EmSize}  bold:{f.Bold}  italic:{f.Italic}  glyphs:{f.Glyphs?.Count ?? 0}  range:{f.RangeStart}-{f.RangeEnd}");
            }
        }

        // Paths
        if (data.Paths != null && data.Paths.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Paths ({data.Paths.Count}) ===");
            for (int i = 0; i < data.Paths.Count; i++)
            {
                var p = data.Paths[i];
                Console.WriteLine($"  [{i}] {p.Name?.Content}  smooth:{p.IsSmooth}  closed:{p.IsClosed}  precision:{p.Precision}  points:{p.Points.Count}");
            }
        }

        // Scripts
        if (data.Scripts != null && data.Scripts.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Scripts ({data.Scripts.Count}) ===");
            for (int i = 0; i < data.Scripts.Count; i++)
            {
                var s = data.Scripts[i];
                string codeName = s.Code?.Name?.Content ?? "<none>";
                Console.WriteLine($"  [{i}] {s.Name?.Content}  code:{codeName}");
            }
        }

        // Shaders
        if (data.Shaders != null && data.Shaders.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Shaders ({data.Shaders.Count}) ===");
            for (int i = 0; i < data.Shaders.Count; i++)
            {
                var s = data.Shaders[i];
                Console.WriteLine($"  [{i}] {s.Name?.Content}  type:{s.Type}  glsl_es_vtx:{s.GLSL_ES_Vertex?.Content?.Length ?? 0}ch  glsl_es_frag:{s.GLSL_ES_Fragment?.Content?.Length ?? 0}ch");
            }
        }

        // GameObjects
        if (data.GameObjects != null && data.GameObjects.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== GameObjects ({data.GameObjects.Count}) ===");
            for (int i = 0; i < data.GameObjects.Count; i++)
            {
                var o = data.GameObjects[i];
                string spr = o.Sprite?.Name?.Content ?? "<none>";
                string par = o.ParentId?.Name?.Content ?? "<none>";
                List<string> flags = [];
                if (o.Visible) flags.Add("visible");
                if (o.Solid) flags.Add("solid");
                if (o.Persistent) flags.Add("persistent");
                if (o.UsesPhysics) flags.Add("physics");
                string flagStr = flags.Count > 0 ? string.Join(",", flags) : "-";

                // Collect events
                List<string> evNames = [];
                for (int et = 0; et < o.Events.Count; et++)
                {
                    foreach (var ev in o.Events[et])
                        evNames.Add($"{EventTypeName(et)}_{ev.EventSubtype}");
                }
                string evStr = evNames.Count > 0 ? string.Join(", ", evNames) : "none";

                Console.WriteLine($"  [{i}] {o.Name?.Content}  sprite:{spr}  parent:{par}  depth:{o.Depth}  [{flagStr}]");
                Console.WriteLine($"        events: {evStr}");
            }
        }

        // Rooms
        if (data.Rooms != null && data.Rooms.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Rooms ({data.Rooms.Count}) ===");
            for (int i = 0; i < data.Rooms.Count; i++)
            {
                var r = data.Rooms[i];
                int instanceCount = 0;
                foreach (var layer in r.Layers ?? Enumerable.Empty<UndertaleRoom.Layer>())
                    if (layer.InstancesData?.Instances != null)
                        instanceCount += layer.InstancesData.Instances.Count;

                string cc = r.CreationCodeId?.Name?.Content ?? "<none>";
                Console.WriteLine($"  [{i}] {r.Name?.Content}  {r.Width}x{r.Height}  layers:{r.Layers?.Count ?? 0}  instances:{instanceCount}  speed:{r.Speed}  persistent:{r.Persistent}  creationCode:{cc}");

                if (r.Layers != null)
                {
                    foreach (var layer in r.Layers)
                    {
                        int instInLayer = layer.InstancesData?.Instances?.Count ?? 0;
                        Console.WriteLine($"        layer: {layer.LayerName?.Content}  type:{layer.LayerType}  depth:{layer.LayerDepth}  instances:{instInLayer}");
                    }
                }
            }
        }

        // Code Entries
        if (data.Code != null && data.Code.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Code Entries ({data.Code.Count}) ===");
            for (int i = 0; i < data.Code.Count; i++)
            {
                var c = data.Code[i];
                string parent = c.ParentEntry?.Name?.Content ?? "-";
                int children = c.ChildEntries.Count;
                Console.WriteLine($"  [{i}] {c.Name?.Content}  len:{c.Length}  locals:{c.LocalsCount}  args:{c.ArgumentsCount}  parent:{parent}  children:{children}");
            }
        }

        // Functions
        if (data.Functions != null && data.Functions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Functions ({data.Functions.Count}) ===");
            for (int i = 0; i < data.Functions.Count; i++)
            {
                var f = data.Functions[i];
                Console.WriteLine($"  [{i}] {f.Name?.Content}");
            }
        }

        // Variables
        if (data.Variables != null && data.Variables.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Variables ({data.Variables.Count}) ===");
            for (int i = 0; i < data.Variables.Count; i++)
            {
                var v = data.Variables[i];
                Console.WriteLine($"  [{i}] {v.Name?.Content}  type:{v.InstanceType}  varId:{v.VarID}");
            }
        }

        // Strings (truncated)
        if (data.Strings != null && data.Strings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Strings ({data.Strings.Count}) ===");
            for (int i = 0; i < data.Strings.Count; i++)
            {
                var s = data.Strings[i]?.Content ?? "";
                string display = s.Length > 80 ? string.Concat(s.AsSpan(0, 77), "...") : s;
                display = display.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
                Console.WriteLine($"  [{i}] \"{display}\"");
            }
        }

        // EmbeddedTextures
        if (data.EmbeddedTextures != null && data.EmbeddedTextures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== EmbeddedTextures ({data.EmbeddedTextures.Count}) ===");
            for (int i = 0; i < data.EmbeddedTextures.Count; i++)
            {
                var t = data.EmbeddedTextures[i];
                string size = t.TextureData?.Image != null ? $"{t.TextureData.Width}x{t.TextureData.Height}" : "no data";
                Console.WriteLine($"  [{i}] {t.Name?.Content}  {size}");
            }
        }

        // TexturePageItems
        if (data.TexturePageItems != null && data.TexturePageItems.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== TexturePageItems ({data.TexturePageItems.Count}) ===");
            for (int i = 0; i < data.TexturePageItems.Count; i++)
            {
                var t = data.TexturePageItems[i];
                if (t == null) continue;
                int texIdx = t.TexturePage != null && data.EmbeddedTextures != null ? data.EmbeddedTextures.IndexOf(t.TexturePage) : -1;
                Console.WriteLine($"  [{i}] src:({t.SourceX},{t.SourceY},{t.SourceWidth},{t.SourceHeight})  tgt:({t.TargetX},{t.TargetY},{t.TargetWidth},{t.TargetHeight})  bound:({t.BoundingWidth},{t.BoundingHeight})  texPage:{texIdx}");
            }
        }

        // AudioGroups
        if (data.AudioGroups != null && data.AudioGroups.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== AudioGroups ({data.AudioGroups.Count}) ===");
            for (int i = 0; i < data.AudioGroups.Count; i++)
                Console.WriteLine($"  [{i}] {data.AudioGroups[i]?.Name?.Content}");
        }

        // Extensions (detailed)
        if (data.Extensions != null && data.Extensions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Extensions detail ({data.Extensions.Count}) ===");
            for (int i = 0; i < data.Extensions.Count; i++)
            {
                var e = data.Extensions[i];
                Console.WriteLine($"  [{i}] {e.Name?.Content}  className:{e.ClassName?.Content}  files:{e.Files?.Count ?? 0}");
                if (e.Files != null)
                {
                    foreach (var f in e.Files)
                        Console.WriteLine($"        file: {f.Filename?.Content}  kind:{f.Kind}  functions:{f.Functions?.Count ?? 0}");
                }
            }
        }

        // Timelines
        if (data.Timelines != null && data.Timelines.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Timelines ({data.Timelines.Count}) ===");
            for (int i = 0; i < data.Timelines.Count; i++)
            {
                var t = data.Timelines[i];
                Console.WriteLine($"  [{i}] {t.Name?.Content}  moments:{t.Moments?.Count ?? 0}");
            }
        }
    }

    private static async Task ShowPatchInfoAsync(string path, bool verbose, bool jsonOutput)
    {
        var result = await PatchService.ValidatePatchAsync(path, null);

        if (!result.Success || result.Manifest == null)
        {
            Console.WriteLine($"Warning: {result.Error}");
            Console.WriteLine("Attempting to show basic patch information...");
            Console.WriteLine();

            // Try to show basic info even without valid manifest
            try
            {
                using var zipStream = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read);

                var entries = archive.Entries.Select(e => e.FullName).ToList();
                var folders = entries.Select(e => e.Split('/')[0]).Where(f => !string.IsNullOrEmpty(f)).Distinct().ToList();

                Console.WriteLine($"Patch File: {Path.GetFileName(path)}");
                Console.WriteLine($"Size: {new FileInfo(path).Length:N0} bytes");
                Console.WriteLine($"Entries: {entries.Count}");
                Console.WriteLine($"Detected folders: {string.Join(", ", folders)}");

                if (verbose)
                {
                    Console.WriteLine();
                    Console.WriteLine("Patch contents:");
                    foreach (var entry in entries.Take(50))
                        Console.WriteLine($"  {entry}");
                    if (entries.Count > 50)
                        Console.WriteLine($"  ... and {entries.Count - 50} more");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to read patch file: {ex.Message}");
                Environment.ExitCode = 1;
            }
            return;
        }

        var manifest = result.Manifest;

        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(manifest, s_jsonOptions));
        }
        else
        {
            Console.WriteLine($"G3M Patch: {Path.GetFileName(path)}");
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
