using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using G3MLib.DataFile;
using G3MLib.DataFile.Models;
using G3MToolCLI.Models.Scripting;
using G3MToolCLI.Services.Logging;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Commands;

public static class InfoCommand
{

    private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true
    };

    private static readonly string[] EventTypeNames = new string[15]
    {
        "Create", "Destroy", "Alarm", "Step", "Collision", "Keyboard", "Mouse", "Other", "Draw", "KeyPress",
        "KeyRelease", "Trigger", "CleanUp", "Gesture", "PreCreate"
    };

    private static bool IsPatchExtension(string? extension)
    {
        string? text = extension?.ToLowerInvariant();
        if (text == ".g3mpatch" || text == ".zip")
        {
            return true;
        }
        return false;
    }

    public static Command Create()
    {
        Command command = new Command("info", "Show metadata for a data file or .g3mpatch.\n  Usage: info <target> [--cache <dir>]\n  Without -v: counts, GeneralInfo, and short breakdowns\n  With -v: full per-resource listing");
        Argument<FileInfo> targetArg = new Argument<FileInfo>("target") { Description = "Path to data file (.win/.ios/.droid/.unx) or .g3mpatch" };
        Option<bool> verboseOption = new Option<bool>("--verbose", ["-v"]) { Description = "Show full per-resource listing" };
        Option<DirectoryInfo?> cacheOption = new Option<DirectoryInfo?>("--cache") { Description = "Read and write reusable .g3mcache info analysis in this directory." };
        command.Add(targetArg);
        command.Add(verboseOption);
        command.Add(cacheOption);
        command.SetAction(async parseResult =>
        {
            FileInfo target = parseResult.GetValue(targetArg)!;
            bool verbose = parseResult.GetValue(verboseOption);
            DirectoryInfo? cacheDir = parseResult.GetValue(cacheOption);
            bool jsonOutput = Program.JsonOutput;
            G3MCacheOptions cacheOptions = G3MCacheOptions.FromDirectory(cacheDir?.FullName);
            if (IsPatchExtension(Path.GetExtension(target.FullName)))
            {
                await ShowPatchInfoAsync(target.FullName, verbose, jsonOutput);
            }
            else
            {
                await ShowDataFileInfoAsync(target.FullName, verbose, jsonOutput, cacheOptions);
            }
        });
        return command;
    }

    private static async Task ShowDataFileInfoAsync(string path, bool verbose, bool jsonOutput, G3MCacheOptions? cacheOptions)
    {
        try
        {
            if (!verbose)
            {
                G3MDataInfoSnapshot? cached = G3MCacheService.TryReadDataInfoSnapshot(path, cacheOptions);
                if (cached != null)
                {
                    PrintInfoSnapshot(cached, jsonOutput);
                    return;
                }
            }
            using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
            using GameMakerData data = GameMakerIO.Read(stream);
            G3MDataInfoSnapshot snapshot = G3MCacheService.BuildInfoSnapshot(path, data);
            if (!verbose)
            {
                await G3MCacheService.WriteDataInfoCacheAsync(path, snapshot, cacheOptions);
            }
            GameMakerGeneralInfo? generalInfo = data.GeneralInfo;
            string versionDisplay = GeneralInfoUtil.GetVersionDisplay(generalInfo);
            if (jsonOutput)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    File = Path.GetFileName(path),
                    Size = new FileInfo(path).Length,
                    Game = (generalInfo?.DisplayName?.Content ?? "Unknown"),
                    BytecodeVersion = (generalInfo?.BytecodeVersion ?? 0),
                    Version = versionDisplay,
                    GeneralInfo = GeneralInfoUtil.ExtractGeneralInfo(data),
                    Resources = new
                    {
                        Sprites = (data.Sprites?.Count ?? 0),
                        Sounds = (data.Sounds?.Count ?? 0),
                        Code = (data.Code?.Count ?? 0),
                        GameObjects = (data.GameObjects?.Count ?? 0),
                        Rooms = (data.Rooms?.Count ?? 0),
                        Backgrounds = (data.Backgrounds?.Count ?? 0),
                        Fonts = (data.Fonts?.Count ?? 0),
                        Scripts = (data.Scripts?.Count ?? 0),
                        Shaders = (data.Shaders?.Count ?? 0),
                        Paths = (data.Paths?.Count ?? 0),
                        Timelines = (data.Timelines?.Count ?? 0),
                        Extensions = (data.Extensions?.Count ?? 0),
                        Variables = (data.Variables?.Count ?? 0),
                        Functions = (data.Functions?.Count ?? 0),
                        Strings = (data.Strings?.Count ?? 0),
                        AudioGroups = (data.AudioGroups?.Count ?? 0),
                        EmbeddedTextures = (data.EmbeddedTextures?.Count ?? 0),
                        TexturePageItems = (data.TexturePageItems?.Count ?? 0),
                        TextureGroupInfo = (data.TextureGroupInfo?.Count ?? 0),
                        Tilesets = (data.Backgrounds?.Count ?? 0)
                    }
                }, s_jsonOptions));
                return;
            }
            Console.WriteLine("Data File: " + Path.GetFileName(path));
            Console.WriteLine($"Size: {new FileInfo(path).Length:N0} bytes");
            Console.WriteLine("Game: " + (generalInfo?.DisplayName?.Content ?? "Unknown"));
            Console.WriteLine($"Bytecode: {generalInfo?.BytecodeVersion ?? 0}");
            Console.WriteLine("Version: " + versionDisplay);
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
                    foreach (IGrouping<GameMakerInstruction.InstanceType, GameMakerVariable> g in from v in data.Variables
                                                                                                  group v by v.InstanceType into source
                                                                                                  orderby source.Count() descending
                                                                                                  select source)
                    {
                        Console.WriteLine($"  {g.Key,-12} {g.Count(),6}");
                    }
                }
                if (data.Functions != null && data.Functions.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Functions (first/last):");
                    Console.WriteLine("  [0]    " + data.Functions[0]?.Name?.Content);
                    Console.WriteLine($"  [{data.Functions.Count - 1}] {data.Functions[data.Functions.Count - 1]?.Name?.Content}");
                }
                if (data.Code != null && data.Code.Count > 0)
                {
                    int topLevel = data.Code.Count((GameMakerCode c) => c.ParentEntry == null);
                    Console.WriteLine();
                    Console.WriteLine($"Code entries: {topLevel} top-level, {data.Code.Count - topLevel} child");
                }
                if (data.AudioGroups != null && data.AudioGroups.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("AudioGroups:");
                    for (int i = 0; i < data.AudioGroups.Count; i++)
                    {
                        Console.WriteLine($"  [{i}] {data.AudioGroups[i]?.Name?.Content}");
                    }
                }
                if (data.Extensions != null && data.Extensions.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Extensions:");
                    foreach (GameMakerExtension extension in data.Extensions)
                    {
                        Console.WriteLine("  " + extension?.Name?.Content);
                    }
                }
                if (generalInfo?.RoomOrder != null && generalInfo.RoomOrder.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Room order ({generalInfo.RoomOrder.Count} rooms):");
                    for (int i2 = 0; i2 < Math.Min(10, generalInfo.RoomOrder.Count); i2++)
                    {
                        GameMakerRoom? room = generalInfo.RoomOrder[i2]?.Resource;
                        Console.WriteLine($"  [{i2}] {room?.Name?.Content ?? "?"}");
                    }
                    if (generalInfo.RoomOrder.Count > 10)
                    {
                        Console.WriteLine($"  ... and {generalInfo.RoomOrder.Count - 10} more");
                    }
                }
            }
            else
            {
                PrintDetailedResources(data);
            }
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Error reading data file: " + ex.Message);
            Environment.ExitCode = 1;
        }
    }

    private static void PrintInfoSnapshot(G3MDataInfoSnapshot info, bool jsonOutput)
    {
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(info, s_jsonOptions));
            return;
        }
        Console.WriteLine("Data File: " + info.File);
        Console.WriteLine($"Size: {info.Size:N0} bytes");
        Console.WriteLine("Game: " + info.Game);
        Console.WriteLine($"Bytecode: {info.BytecodeVersion}");
        Console.WriteLine("Version: " + info.Version);
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
            foreach (var (key, count) in info.VariablesByInstanceType.OrderByDescending<KeyValuePair<string, int>, int>((KeyValuePair<string, int> kvp) => kvp.Value))
            {
                Console.WriteLine($"  {key,-12} {count,6}");
            }
        }
        if (info.FirstFunction != null || info.LastFunction != null)
        {
            Console.WriteLine();
            Console.WriteLine("Functions (first/last):");
            Console.WriteLine("  [0]    " + info.FirstFunction);
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
            {
                Console.WriteLine($"  [{i}] {info.AudioGroups[i]}");
            }
        }
        if (info.Extensions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Extensions:");
            foreach (string ext in info.Extensions)
            {
                Console.WriteLine("  " + ext);
            }
        }
        if (info.RoomOrderCount > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Room order ({info.RoomOrderCount} rooms):");
            for (int i2 = 0; i2 < info.RoomOrderPreview.Count; i2++)
            {
                Console.WriteLine($"  [{i2}] {info.RoomOrderPreview[i2]}");
            }
            if (info.RoomOrderCount > info.RoomOrderPreview.Count)
            {
                Console.WriteLine($"  ... and {info.RoomOrderCount - info.RoomOrderPreview.Count} more");
            }
        }
        int Count(string key2)
        {
            return info.ResourceCounts.GetValueOrDefault(key2);
        }
    }

    private static void PrintGeneralInfoSnapshot(GeneralInfoData? generalInfo)
    {
        if (generalInfo != null)
        {
            Console.WriteLine();
            Console.WriteLine("GeneralInfo:");
            Console.WriteLine("  DisplayName: " + generalInfo.DisplayName);
            Console.WriteLine("  Name:        " + generalInfo.Name);
            Console.WriteLine("  FileName:    " + generalInfo.FileName);
            Console.WriteLine("  Config:      " + generalInfo.Config);
            Console.WriteLine($"  GameID:      {generalInfo.GameID}");
            Console.WriteLine($"  Version:     {generalInfo.Major}.{generalInfo.Minor}.{generalInfo.Release}.{generalInfo.Build}");
        }
    }

    private static string EventTypeName(int idx)
    {
        if (idx < 0 || idx >= EventTypeNames.Length)
        {
            return $"Type{idx}";
        }
        return EventTypeNames[idx];
    }

    private static void PrintDetailedResources(GameMakerData data)
    {
        Console.WriteLine();
        Console.WriteLine("=".PadRight(60, '='));
        Console.WriteLine("DETAILED RESOURCE LISTING");
        Console.WriteLine("=".PadRight(60, '='));
        if (data.Sprites != null && data.Sprites.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Sprites ({data.Sprites.Count}) ===");
            for (int i = 0; i < data.Sprites.Count; i++)
            {
                GameMakerSprite s = data.Sprites[i];
                Console.WriteLine($"  [{i}] {s.Name?.Content}  {s.Width}x{s.Height}  origin:({s.OriginX},{s.OriginY})  frames:{s.Textures.Count}  mask:{s.SepMasks}  bbox:({s.MarginLeft},{s.MarginTop},{s.MarginRight},{s.MarginBottom})  speed:{s.GMS2PlaybackSpeed}");
            }
        }
        if (data.Sounds != null && data.Sounds.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Sounds ({data.Sounds.Count}) ===");
            for (int j = 0; j < data.Sounds.Count; j++)
            {
                GameMakerSound s2 = data.Sounds[j];
                string grp = s2.AudioGroup?.Name?.Content ?? $"group{s2.GroupID}";
                Console.WriteLine($"  [{j}] {s2.Name?.Content}  type:{s2.Type?.Content}  flags:{s2.Flags}  vol:{s2.Volume:F2}  group:{grp}  file:{s2.File?.Content}");
            }
        }
        if (data.Backgrounds != null && data.Backgrounds.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Backgrounds/Tilesets ({data.Backgrounds.Count}) ===");
            for (int k = 0; k < data.Backgrounds.Count; k++)
            {
                GameMakerBackground b = data.Backgrounds[k];
                string tpi = ((b.Texture != null) ? $"tpi:{data.TexturePageItems.IndexOf(b.Texture)}" : "no-tex");
                Console.WriteLine($"  [{k}] {b.Name?.Content}  {tpi}  tileWidth:{b.GMS2TileWidth}  tileHeight:{b.GMS2TileHeight}  cols:{b.GMS2TileColumns}  count:{b.GMS2TileCount}");
            }
        }
        if (data.Fonts != null && data.Fonts.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Fonts ({data.Fonts.Count}) ===");
            for (int l = 0; l < data.Fonts.Count; l++)
            {
                GameMakerFont f = data.Fonts[l];
                Console.WriteLine($"  [{l}] {f.Name?.Content}  display:{f.DisplayName?.Content}  size:{f.EmSize}  bold:{f.Bold}  italic:{f.Italic}  glyphs:{f.Glyphs?.Count ?? 0}  range:{f.RangeStart}-{f.RangeEnd}");
            }
        }
        if (data.Paths != null && data.Paths.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Paths ({data.Paths.Count}) ===");
            for (int m = 0; m < data.Paths.Count; m++)
            {
                GameMakerPath p = data.Paths[m];
                Console.WriteLine($"  [{m}] {p.Name?.Content}  smooth:{p.IsSmooth}  closed:{p.IsClosed}  precision:{p.Precision}  points:{p.Points.Count}");
            }
        }
        if (data.Scripts != null && data.Scripts.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Scripts ({data.Scripts.Count}) ===");
            for (int n = 0; n < data.Scripts.Count; n++)
            {
                GameMakerScript s3 = data.Scripts[n];
                string codeName = s3.Code?.Name?.Content ?? "<none>";
                Console.WriteLine($"  [{n}] {s3.Name?.Content}  code:{codeName}");
            }
        }
        if (data.Shaders != null && data.Shaders.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Shaders ({data.Shaders.Count}) ===");
            for (int num = 0; num < data.Shaders.Count; num++)
            {
                GameMakerShader s4 = data.Shaders[num];
                Console.WriteLine($"  [{num}] {s4.Name?.Content}  type:{s4.Type}  glsl_es_vtx:{(s4.GLSL_ES_Vertex?.Content?.Length).GetValueOrDefault()}ch  glsl_es_frag:{(s4.GLSL_ES_Fragment?.Content?.Length).GetValueOrDefault()}ch");
            }
        }
        if (data.GameObjects != null && data.GameObjects.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== GameObjects ({data.GameObjects.Count}) ===");
            for (int num2 = 0; num2 < data.GameObjects.Count; num2++)
            {
                GameMakerGameObject o = data.GameObjects[num2];
                string spr = o.Sprite?.Name?.Content ?? "<none>";
                string par = o.ParentId?.Name?.Content ?? "<none>";
                List<string> flags = new List<string>();
                if (o.Visible)
                {
                    flags.Add("visible");
                }
                if (o.Solid)
                {
                    flags.Add("solid");
                }
                if (o.Persistent)
                {
                    flags.Add("persistent");
                }
                if (o.UsesPhysics)
                {
                    flags.Add("physics");
                }
                string flagStr = ((flags.Count > 0) ? string.Join(",", flags) : "-");
                List<string> evNames = new List<string>();
                for (int et = 0; et < o.Events.Count; et++)
                {
                    foreach (GameMakerGameObject.Event ev in o.Events[et])
                    {
                        evNames.Add($"{EventTypeName(et)}_{ev.EventSubtype}");
                    }
                }
                string evStr = ((evNames.Count > 0) ? string.Join(", ", evNames) : "none");
                Console.WriteLine($"  [{num2}] {o.Name?.Content}  sprite:{spr}  parent:{par}  depth:{o.Depth}  [{flagStr}]");
                Console.WriteLine("        events: " + evStr);
            }
        }
        if (data.Rooms != null && data.Rooms.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Rooms ({data.Rooms.Count}) ===");
            for (int num3 = 0; num3 < data.Rooms.Count; num3++)
            {
                GameMakerRoom r = data.Rooms[num3];
                int instanceCount = 0;
                IEnumerable<GameMakerRoom.Layer> layers = r.Layers;
                foreach (GameMakerRoom.Layer layer in layers ?? Enumerable.Empty<GameMakerRoom.Layer>())
                {
                    if (layer.InstancesData?.Instances != null)
                    {
                        instanceCount += layer.InstancesData.Instances.Count;
                    }
                }
                string cc = r.CreationCodeId?.Name?.Content ?? "<none>";
                Console.WriteLine($"  [{num3}] {r.Name?.Content}  {r.Width}x{r.Height}  layers:{r.Layers?.Count ?? 0}  instances:{instanceCount}  speed:{r.Speed}  persistent:{r.Persistent}  creationCode:{cc}");
                if (r.Layers == null)
                {
                    continue;
                }
                foreach (GameMakerRoom.Layer layer2 in r.Layers)
                {
                    int instInLayer = (layer2.InstancesData?.Instances?.Count).GetValueOrDefault();
                    Console.WriteLine($"        layer: {layer2.LayerName?.Content}  type:{layer2.LayerType}  depth:{layer2.LayerDepth}  instances:{instInLayer}");
                }
            }
        }
        if (data.Code != null && data.Code.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Code Entries ({data.Code.Count}) ===");
            for (int num4 = 0; num4 < data.Code.Count; num4++)
            {
                GameMakerCode c = data.Code[num4];
                string parent = c.ParentEntry?.Name?.Content ?? "-";
                int children = c.ChildEntries.Count;
                Console.WriteLine($"  [{num4}] {c.Name?.Content}  len:{c.Length}  locals:{c.LocalsCount}  args:{c.ArgumentsCount}  parent:{parent}  children:{children}");
            }
        }
        if (data.Functions != null && data.Functions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Functions ({data.Functions.Count}) ===");
            for (int num5 = 0; num5 < data.Functions.Count; num5++)
            {
                GameMakerFunction f2 = data.Functions[num5];
                Console.WriteLine($"  [{num5}] {f2.Name?.Content}");
            }
        }
        if (data.Variables != null && data.Variables.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Variables ({data.Variables.Count}) ===");
            for (int num6 = 0; num6 < data.Variables.Count; num6++)
            {
                GameMakerVariable v = data.Variables[num6];
                Console.WriteLine($"  [{num6}] {v.Name?.Content}  type:{v.InstanceType}  varId:{v.VarID}");
            }
        }
        if (data.Strings != null && data.Strings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Strings ({data.Strings.Count}) ===");
            for (int num7 = 0; num7 < data.Strings.Count; num7++)
            {
                string s5 = data.Strings[num7]?.Content ?? "";
                string display = ((s5.Length > 80) ? string.Concat(s5.AsSpan(0, 77), "...".AsSpan()) : s5);
                display = display.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
                Console.WriteLine($"  [{num7}] \"{display}\"");
            }
        }
        if (data.EmbeddedTextures != null && data.EmbeddedTextures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== EmbeddedTextures ({data.EmbeddedTextures.Count}) ===");
            for (int num8 = 0; num8 < data.EmbeddedTextures.Count; num8++)
            {
                GameMakerEmbeddedTexture t = data.EmbeddedTextures[num8];
                string size = ((t.TextureData?.Image != null) ? $"{t.TextureData.Width}x{t.TextureData.Height}" : "no data");
                Console.WriteLine($"  [{num8}] {t.Name?.Content}  {size}");
            }
        }
        if (data.TexturePageItems != null && data.TexturePageItems.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== TexturePageItems ({data.TexturePageItems.Count}) ===");
            for (int num9 = 0; num9 < data.TexturePageItems.Count; num9++)
            {
                GameMakerTexturePageItem t2 = data.TexturePageItems[num9];
                if (t2 != null)
                {
                    int texIdx = ((t2.TexturePage != null && data.EmbeddedTextures != null) ? data.EmbeddedTextures.IndexOf(t2.TexturePage) : (-1));
                    Console.WriteLine($"  [{num9}] src:({t2.SourceX},{t2.SourceY},{t2.SourceWidth},{t2.SourceHeight})  tgt:({t2.TargetX},{t2.TargetY},{t2.TargetWidth},{t2.TargetHeight})  bound:({t2.BoundingWidth},{t2.BoundingHeight})  texPage:{texIdx}");
                }
            }
        }
        if (data.AudioGroups != null && data.AudioGroups.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== AudioGroups ({data.AudioGroups.Count}) ===");
            for (int num10 = 0; num10 < data.AudioGroups.Count; num10++)
            {
                Console.WriteLine($"  [{num10}] {data.AudioGroups[num10]?.Name?.Content}");
            }
        }
        if (data.Extensions != null && data.Extensions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Extensions detail ({data.Extensions.Count}) ===");
            for (int num11 = 0; num11 < data.Extensions.Count; num11++)
            {
                GameMakerExtension e = data.Extensions[num11];
                Console.WriteLine($"  [{num11}] {e.Name?.Content}  className:{e.ClassName?.Content}  files:{e.Files?.Count ?? 0}");
                if (e.Files == null)
                {
                    continue;
                }
                foreach (GameMakerExtensionFile f3 in e.Files)
                {
                    Console.WriteLine($"        file: {f3.Filename?.Content}  kind:{f3.Kind}  functions:{f3.Functions?.Count ?? 0}");
                }
            }
        }
        if (data.Timelines != null && data.Timelines.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Timelines ({data.Timelines.Count}) ===");
            for (int num12 = 0; num12 < data.Timelines.Count; num12++)
            {
                GameMakerTimeline t3 = data.Timelines[num12];
                Console.WriteLine($"  [{num12}] {t3.Name?.Content}  moments:{t3.Moments?.Count ?? 0}");
            }
        }
    }

    private static async Task ShowPatchInfoAsync(string path, bool verbose, bool jsonOutput)
    {
        PatchValidateResult result = await PatchService.ValidatePatchAsync(path);
        if (!result.Success || result.Manifest == null)
        {
            Console.WriteLine("Warning: " + result.Error);
            Console.WriteLine("Attempting to show basic patch information...");
            Console.WriteLine();
            try
            {
                using FileStream zipStream = new FileStream(path, FileMode.Open, FileAccess.Read);
                using ZipArchive archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                List<string> entries = archive.Entries.Select((ZipArchiveEntry e) => e.FullName).ToList();
                List<string> folders = (from e in entries
                                        select e.Split('/')[0] into f
                                        where !string.IsNullOrEmpty(f)
                                        select f).Distinct().ToList();
                Console.WriteLine("Patch File: " + Path.GetFileName(path));
                Console.WriteLine($"Size: {new FileInfo(path).Length:N0} bytes");
                Console.WriteLine($"Entries: {entries.Count}");
                Console.WriteLine("Detected folders: " + string.Join(", ", folders));
                if (!verbose)
                {
                    return;
                }
                Console.WriteLine();
                Console.WriteLine("Patch contents:");
                foreach (string entry in entries.Take(50))
                {
                    Console.WriteLine("  " + entry);
                }
                if (entries.Count > 50)
                {
                    Console.WriteLine($"  ... and {entries.Count - 50} more");
                }
                return;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync("Failed to read patch file: " + ex.Message);
                Environment.ExitCode = 1;
                return;
            }
        }
        G3MPatchManifest manifest = result.Manifest;
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(manifest, s_jsonOptions));
            return;
        }
        Console.WriteLine("G3M Patch: " + Path.GetFileName(path));
        Console.WriteLine("Created: " + manifest.CreatedAt);
        Console.WriteLine("Tool: " + manifest.Tool?.Name + " v" + manifest.Tool?.Version);
        Console.WriteLine();
        Console.WriteLine("Original:");
        Console.WriteLine("  File: " + manifest.Original?.Filename);
        Console.WriteLine($"  Size: {manifest.Original?.Size:N0} bytes");
        Console.WriteLine("  GMS: " + manifest.Original?.GmsVersion);
        Console.WriteLine();
        Console.WriteLine("Statistics:");
        Console.WriteLine($"  Changed: {manifest.Statistics?.TotalChanged ?? 0}");
        Console.WriteLine($"  New: {manifest.Statistics?.TotalNew ?? 0}");
        Console.WriteLine($"  Deleted: {manifest.Statistics?.TotalDeleted ?? 0}");
        if (!verbose || manifest.Resources == null)
        {
            return;
        }
        Console.WriteLine();
        Console.WriteLine("Resources by type:");
        foreach (KeyValuePair<string, ResourceTypeChanges> resource in manifest.Resources)
        {
            resource.Deconstruct(out var key, out var value);
            string type = key;
            ResourceTypeChanges resourceTypeChanges = value;
            int changed = resourceTypeChanges.Changed?.Count ?? 0;
            int newRes = resourceTypeChanges.New?.Count ?? 0;
            int deleted = resourceTypeChanges.Deleted?.Count ?? 0;
            if (changed + newRes + deleted > 0)
            {
                Console.WriteLine($"  {type}: {changed} changed, {newRes} new, {deleted} deleted");
            }
        }
    }
}
