using G3MLib.DataFile;

namespace G3MToolCLI.Commands;

/// <summary>The per-resource listing that <c>info -v</c> prints for a data file.</summary>
internal static class InfoResourceListing
{
    private static readonly string[] EventTypeNames =
    [
        "Create", "Destroy", "Alarm", "Step", "Collision", "Keyboard", "Mouse", "Other", "Draw", "KeyPress",
        "KeyRelease", "Trigger", "CleanUp", "Gesture", "PreCreate"
    ];

    public static void Print(GameMakerData data)
    {
        Console.WriteLine();
        Console.WriteLine("=".PadRight(60, '='));
        Console.WriteLine("DETAILED RESOURCE LISTING");
        Console.WriteLine("=".PadRight(60, '='));
        PrintSprites(data);
        PrintSounds(data);
        PrintBackgrounds(data);
        PrintFonts(data);
        PrintPaths(data);
        PrintScripts(data);
        PrintShaders(data);
        PrintGameObjects(data);
        PrintRooms(data);
        PrintCode(data);
        PrintFunctions(data);
        PrintVariables(data);
        PrintStrings(data);
        PrintEmbeddedTextures(data);
        PrintTexturePageItems(data);
        PrintAudioGroups(data);
        PrintExtensions(data);
        PrintTimelines(data);
    }

    private static void Section(string title, int count)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title} ({count}) ===");
    }

    private static void PrintSprites(GameMakerData data)
    {
        if (data.Sprites is not { Count: > 0 }) return;
        Section("Sprites", data.Sprites.Count);
        for (var i = 0; i < data.Sprites.Count; i++)
        {
            var s = data.Sprites[i];
            Console.WriteLine($"  [{i}] {s.Name?.Content}  {s.Width}x{s.Height}  origin:({s.OriginX},{s.OriginY})  frames:{s.Textures.Count}  mask:{s.SepMasks}  bbox:({s.MarginLeft},{s.MarginTop},{s.MarginRight},{s.MarginBottom})  speed:{s.GMS2PlaybackSpeed}");
        }
    }

    private static void PrintSounds(GameMakerData data)
    {
        if (data.Sounds is not { Count: > 0 }) return;
        Section("Sounds", data.Sounds.Count);
        for (var i = 0; i < data.Sounds.Count; i++)
        {
            var s = data.Sounds[i];
            var group = s.AudioGroup?.Name?.Content ?? $"group{s.GroupID}";
            Console.WriteLine($"  [{i}] {s.Name?.Content}  type:{s.Type?.Content}  flags:{s.Flags}  vol:{s.Volume:F2}  group:{group}  file:{s.File?.Content}");
        }
    }

    private static void PrintBackgrounds(GameMakerData data)
    {
        if (data.Backgrounds is not { Count: > 0 }) return;
        Section("Backgrounds/Tilesets", data.Backgrounds.Count);
        for (var i = 0; i < data.Backgrounds.Count; i++)
        {
            var b = data.Backgrounds[i];
            var tpi = (b.Texture != null) ? $"tpi:{data.TexturePageItems.IndexOf(b.Texture)}" : "no-tex";
            Console.WriteLine($"  [{i}] {b.Name?.Content}  {tpi}  tileWidth:{b.GMS2TileWidth}  tileHeight:{b.GMS2TileHeight}  cols:{b.GMS2TileColumns}  count:{b.GMS2TileCount}");
        }
    }

    private static void PrintFonts(GameMakerData data)
    {
        if (data.Fonts is not { Count: > 0 }) return;
        Section("Fonts", data.Fonts.Count);
        for (var i = 0; i < data.Fonts.Count; i++)
        {
            var f = data.Fonts[i];
            Console.WriteLine($"  [{i}] {f.Name?.Content}  display:{f.DisplayName?.Content}  size:{f.EmSize}  bold:{f.Bold}  italic:{f.Italic}  glyphs:{f.Glyphs?.Count ?? 0}  range:{f.RangeStart}-{f.RangeEnd}");
        }
    }

    private static void PrintPaths(GameMakerData data)
    {
        if (data.Paths is not { Count: > 0 }) return;
        Section("Paths", data.Paths.Count);
        for (var i = 0; i < data.Paths.Count; i++)
        {
            var p = data.Paths[i];
            Console.WriteLine($"  [{i}] {p.Name?.Content}  smooth:{p.IsSmooth}  closed:{p.IsClosed}  precision:{p.Precision}  points:{p.Points.Count}");
        }
    }

    private static void PrintScripts(GameMakerData data)
    {
        if (data.Scripts is not { Count: > 0 }) return;
        Section("Scripts", data.Scripts.Count);
        for (var i = 0; i < data.Scripts.Count; i++)
        {
            var s = data.Scripts[i];
            Console.WriteLine($"  [{i}] {s.Name?.Content}  code:{s.Code?.Name?.Content ?? "<none>"}");
        }
    }

    private static void PrintShaders(GameMakerData data)
    {
        if (data.Shaders is not { Count: > 0 }) return;
        Section("Shaders", data.Shaders.Count);
        for (var i = 0; i < data.Shaders.Count; i++)
        {
            var s = data.Shaders[i];
            Console.WriteLine($"  [{i}] {s.Name?.Content}  type:{s.Type}  glsl_es_vtx:{(s.GLSL_ES_Vertex?.Content?.Length).GetValueOrDefault()}ch  glsl_es_frag:{(s.GLSL_ES_Fragment?.Content?.Length).GetValueOrDefault()}ch");
        }
    }

    private static void PrintGameObjects(GameMakerData data)
    {
        if (data.GameObjects is not { Count: > 0 }) return;
        Section("GameObjects", data.GameObjects.Count);
        for (var i = 0; i < data.GameObjects.Count; i++)
        {
            var o = data.GameObjects[i];
            var sprite = o.Sprite?.Name?.Content ?? "<none>";
            var parent = o.ParentId?.Name?.Content ?? "<none>";
            List<string> flags = [];
            if (o.Visible) flags.Add("visible");
            if (o.Solid) flags.Add("solid");
            if (o.Persistent) flags.Add("persistent");
            if (o.UsesPhysics) flags.Add("physics");
            var flagText = (flags.Count > 0) ? string.Join(",", flags) : "-";
            List<string> eventNames = [];
            for (var type = 0; type < o.Events.Count; type++)
            {
                foreach (var ev in o.Events[type])
                {
                    eventNames.Add($"{EventTypeName(type)}_{ev.EventSubtype}");
                }
            }
            var eventText = (eventNames.Count > 0) ? string.Join(", ", eventNames) : "none";
            Console.WriteLine($"  [{i}] {o.Name?.Content}  sprite:{sprite}  parent:{parent}  depth:{o.Depth}  [{flagText}]");
            Console.WriteLine("        events: " + eventText);
        }
    }

    private static string EventTypeName(int index)
    {
        if (index < 0 || index >= EventTypeNames.Length)
        {
            return $"Type{index}";
        }
        return EventTypeNames[index];
    }

    private static void PrintRooms(GameMakerData data)
    {
        if (data.Rooms is not { Count: > 0 }) return;
        Section("Rooms", data.Rooms.Count);
        for (var i = 0; i < data.Rooms.Count; i++)
        {
            var r = data.Rooms[i];
            var instanceCount = 0;
            foreach (var layer in r.Layers ?? [])
            {
                instanceCount += layer.InstancesData?.Instances?.Count ?? 0;
            }
            var creationCode = r.CreationCodeId?.Name?.Content ?? "<none>";
            Console.WriteLine($"  [{i}] {r.Name?.Content}  {r.Width}x{r.Height}  layers:{r.Layers?.Count ?? 0}  instances:{instanceCount}  speed:{r.Speed}  persistent:{r.Persistent}  creationCode:{creationCode}");
            if (r.Layers == null)
            {
                continue;
            }
            foreach (var layer in r.Layers)
            {
                Console.WriteLine($"        layer: {layer.LayerName?.Content}  type:{layer.LayerType}  depth:{layer.LayerDepth}  instances:{(layer.InstancesData?.Instances?.Count).GetValueOrDefault()}");
            }
        }
    }

    private static void PrintCode(GameMakerData data)
    {
        if (data.Code is not { Count: > 0 }) return;
        Section("Code Entries", data.Code.Count);
        for (var i = 0; i < data.Code.Count; i++)
        {
            var c = data.Code[i];
            var parent = c.ParentEntry?.Name?.Content ?? "-";
            Console.WriteLine($"  [{i}] {c.Name?.Content}  len:{c.Length}  locals:{c.LocalsCount}  args:{c.ArgumentsCount}  parent:{parent}  children:{c.ChildEntries.Count}");
        }
    }

    private static void PrintFunctions(GameMakerData data)
    {
        if (data.Functions is not { Count: > 0 }) return;
        Section("Functions", data.Functions.Count);
        for (var i = 0; i < data.Functions.Count; i++)
        {
            Console.WriteLine($"  [{i}] {data.Functions[i].Name?.Content}");
        }
    }

    private static void PrintVariables(GameMakerData data)
    {
        if (data.Variables is not { Count: > 0 }) return;
        Section("Variables", data.Variables.Count);
        for (var i = 0; i < data.Variables.Count; i++)
        {
            var v = data.Variables[i];
            Console.WriteLine($"  [{i}] {v.Name?.Content}  type:{v.InstanceType}  varId:{v.VarID}");
        }
    }

    private static void PrintStrings(GameMakerData data)
    {
        if (data.Strings is not { Count: > 0 }) return;
        Section("Strings", data.Strings.Count);
        for (var i = 0; i < data.Strings.Count; i++)
        {
            var text = data.Strings[i]?.Content ?? "";
            var display = (text.Length > 80) ? string.Concat(text.AsSpan(0, 77), "...".AsSpan()) : text;
            display = display.Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
            Console.WriteLine($"  [{i}] \"{display}\"");
        }
    }

    private static void PrintEmbeddedTextures(GameMakerData data)
    {
        if (data.EmbeddedTextures is not { Count: > 0 }) return;
        Section("EmbeddedTextures", data.EmbeddedTextures.Count);
        for (var i = 0; i < data.EmbeddedTextures.Count; i++)
        {
            var t = data.EmbeddedTextures[i];
            var size = (t.TextureData?.Image != null) ? $"{t.TextureData.Width}x{t.TextureData.Height}" : "no data";
            Console.WriteLine($"  [{i}] {t.Name?.Content}  {size}");
        }
    }

    private static void PrintTexturePageItems(GameMakerData data)
    {
        if (data.TexturePageItems is not { Count: > 0 }) return;
        Section("TexturePageItems", data.TexturePageItems.Count);
        for (var i = 0; i < data.TexturePageItems.Count; i++)
        {
            var t = data.TexturePageItems[i];
            if (t == null)
            {
                continue;
            }
            var texturePage = (t.TexturePage != null && data.EmbeddedTextures != null) ? data.EmbeddedTextures.IndexOf(t.TexturePage) : (-1);
            Console.WriteLine($"  [{i}] src:({t.SourceX},{t.SourceY},{t.SourceWidth},{t.SourceHeight})  tgt:({t.TargetX},{t.TargetY},{t.TargetWidth},{t.TargetHeight})  bound:({t.BoundingWidth},{t.BoundingHeight})  texPage:{texturePage}");
        }
    }

    private static void PrintAudioGroups(GameMakerData data)
    {
        if (data.AudioGroups is not { Count: > 0 }) return;
        Section("AudioGroups", data.AudioGroups.Count);
        for (var i = 0; i < data.AudioGroups.Count; i++)
        {
            Console.WriteLine($"  [{i}] {data.AudioGroups[i]?.Name?.Content}");
        }
    }

    private static void PrintExtensions(GameMakerData data)
    {
        if (data.Extensions is not { Count: > 0 }) return;
        Section("Extensions detail", data.Extensions.Count);
        for (var i = 0; i < data.Extensions.Count; i++)
        {
            var e = data.Extensions[i];
            Console.WriteLine($"  [{i}] {e.Name?.Content}  className:{e.ClassName?.Content}  files:{e.Files?.Count ?? 0}");
            if (e.Files == null)
            {
                continue;
            }
            foreach (var file in e.Files)
            {
                Console.WriteLine($"        file: {file.Filename?.Content}  kind:{file.Kind}  functions:{file.Functions?.Count ?? 0}");
            }
        }
    }

    private static void PrintTimelines(GameMakerData data)
    {
        if (data.Timelines is not { Count: > 0 }) return;
        Section("Timelines", data.Timelines.Count);
        for (var i = 0; i < data.Timelines.Count; i++)
        {
            var t = data.Timelines[i];
            Console.WriteLine($"  [{i}] {t.Name?.Content}  moments:{t.Moments?.Count ?? 0}");
        }
    }
}
