// ExportAssetOrder.csx - Exports the order of all assets to a text file
// Based on UTMT's ExportAssetOrder.csx by colinator27
// This script is independent and can be used standalone

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;

EnsureDataLoaded();

string outputDir = OutputDir;
if (string.IsNullOrEmpty(outputDir))
    throw new Exception("OutputDir is not set.");

string outputPath = Path.Combine(outputDir, "asset_order.txt");

void WriteAssetNames<T>(StreamWriter writer, IList<T> assets) where T : UndertaleNamedResource
{
    if (assets == null || assets.Count == 0)
        return;
    foreach (var asset in assets)
    {
        if (asset is not null)
        {
            string name = asset.Name?.Content;
            // Use index for assets with null or empty names
            if (string.IsNullOrEmpty(name))
                writer.WriteLine(assets.IndexOf(asset).ToString());
            else
                writer.WriteLine(name);
        }
        else
            writer.WriteLine("(null)");
    }
}

using (StreamWriter writer = new StreamWriter(outputPath))
{
    // Write Sounds
    writer.WriteLine("@@sounds@@");
    WriteAssetNames(writer, Data.Sounds);

    // Write Sprites
    writer.WriteLine("@@sprites@@");
    WriteAssetNames(writer, Data.Sprites);

    // Write Backgrounds
    writer.WriteLine("@@backgrounds@@");
    WriteAssetNames(writer, Data.Backgrounds);

    // Write Paths
    writer.WriteLine("@@paths@@");
    WriteAssetNames(writer, Data.Paths);

    // Write Scripts
    writer.WriteLine("@@scripts@@");
    WriteAssetNames(writer, Data.Scripts);

    // Write Fonts
    writer.WriteLine("@@fonts@@");
    WriteAssetNames(writer, Data.Fonts);

    // Write Objects
    writer.WriteLine("@@objects@@");
    WriteAssetNames(writer, Data.GameObjects);

    // Write Timelines
    writer.WriteLine("@@timelines@@");
    WriteAssetNames(writer, Data.Timelines);

    // Write Rooms
    writer.WriteLine("@@rooms@@");
    WriteAssetNames(writer, Data.Rooms);

    // Write Shaders
    writer.WriteLine("@@shaders@@");
    WriteAssetNames(writer, Data.Shaders);

    // Write Extensions
    writer.WriteLine("@@extensions@@");
    WriteAssetNames(writer, Data.Extensions);

    // Write AudioGroups
    writer.WriteLine("@@audiogroups@@");
    WriteAssetNames(writer, Data.AudioGroups);

    // Write indexed resource counts (these don't have names but counts must match TARGET)
    writer.WriteLine("@@counts@@");
    writer.WriteLine($"EmbeddedTextures={Data.EmbeddedTextures.Count}");
    writer.WriteLine($"TexturePageItems={Data.TexturePageItems.Count}");
}

Console.WriteLine($"[ExportAssetOrder] Asset order exported to: {outputPath}");

// Export Variables and Functions as JSON for pre-population before code compilation
// This ensures VarIDs match TARGET after recompilation
string varFuncPath = Path.Combine(outputDir, "variables_functions.json");

var varList = new List<Dictionary<string, object>>();
foreach (var v in Data.Variables)
{
    varList.Add(new Dictionary<string, object> {
        {"n", v.Name?.Content ?? ""},
        {"t", (int)v.InstanceType},
        {"id", v.VarID}
    });
}

var funcList = new List<string>();
foreach (var f in Data.Functions)
{
    funcList.Add(f.Name?.Content ?? "");
}

// Code entry names for deletion detection during ASM-only import
var codeEntryNames = new List<string>();
foreach (var code in Data.Code)
{
    if (code?.Name?.Content != null)
        codeEntryNames.Add(code.Name.Content);
}

// Variable/function counts for correct table setup
var varCounts = new Dictionary<string, object> {
    {"varCount1", Data.VarCount1},
    {"varCount2", Data.VarCount2},
    {"maxLocalVarCount", Data.MaxLocalVarCount}
};

// Code entry metadata: LocalsCount + ArgumentsCount per parent entry (needed for ASM-only import)
var codeMetadata = new Dictionary<string, int[]>();
foreach (var code in Data.Code)
{
    if (code?.Name?.Content != null && code.ParentEntry == null)
        codeMetadata[code.Name.Content] = new int[] { code.LocalsCount, code.ArgumentsCount };
}

var exportData = new Dictionary<string, object> {
    {"variables", varList},
    {"functions", funcList},
    {"codeEntries", codeEntryNames},
    {"varCounts", varCounts},
    {"codeMetadata", codeMetadata}
};

File.WriteAllText(varFuncPath, JsonSerializer.Serialize(exportData));
Console.WriteLine($"[ExportAssetOrder] Exported {varList.Count} variables and {funcList.Count} functions to: {varFuncPath}");

// Export object event mapping - used by ImportCodeEntries to validate events after compilation
string objectEventsPath = Path.Combine(outputDir, "object_events.json");
var objectEventsMap = new Dictionary<string, List<Dictionary<string, object>>>();
var objectNameCounts = new Dictionary<string, int>();
foreach (var obj in Data.GameObjects)
{
    if (obj?.Name?.Content == null) continue;
    objectNameCounts[obj.Name.Content] = objectNameCounts.GetValueOrDefault(obj.Name.Content) + 1;
}
var objectSeenCounts = new Dictionary<string, int>();
for (int objIndex = 0; objIndex < Data.GameObjects.Count; objIndex++)
{
    var obj = Data.GameObjects[objIndex];
    if (obj?.Name?.Content == null) continue;
    int occurrence = objectSeenCounts.GetValueOrDefault(obj.Name.Content);
    objectSeenCounts[obj.Name.Content] = occurrence + 1;
    string objectKey = objectNameCounts.GetValueOrDefault(obj.Name.Content) > 1
        ? $"{obj.Name.Content}__idx{objIndex:D4}"
        : obj.Name.Content;

    var events = new List<Dictionary<string, object>>();
    for (int evtType = 0; evtType < obj.Events.Count; evtType++)
    {
        foreach (var evt in obj.Events[evtType])
        {
            var evtData = new Dictionary<string, object>
            {
                {"t", evtType},
                {"s", evt.EventSubtype},
                {"c", (evt.Actions.Count > 0 && evt.Actions[0].CodeId != null) ? evt.Actions[0].CodeId.Name?.Content ?? "" : ""}
            };
            if (evtType == (int)EventType.Collision && evt.EventSubtype < Data.GameObjects.Count)
            {
                var collObj = Data.GameObjects[(int)evt.EventSubtype];
                evtData["cn"] = collObj?.Name?.Content ?? "";
                if (collObj?.Name?.Content != null)
                {
                    int collOccurrence = 0;
                    for (int i = 0; i < evt.EventSubtype; i++)
                    {
                        if (string.Equals(Data.GameObjects[i]?.Name?.Content, collObj.Name.Content, StringComparison.Ordinal))
                            collOccurrence++;
                    }
                    evtData["co"] = collOccurrence;
                }
            }

            var actionList = new List<Dictionary<string, object>>();
            foreach (var action in evt.Actions)
            {
                actionList.Add(new Dictionary<string, object>
                {
                    {"libId", action.LibID},
                    {"id", action.ID},
                    {"kind", action.Kind},
                    {"useRelative", action.UseRelative},
                    {"isQuestion", action.IsQuestion},
                    {"useApplyTo", action.UseApplyTo},
                    {"exeType", action.ExeType},
                    {"actionName", action.ActionName?.Content ?? ""},
                    {"codeId", action.CodeId?.Name?.Content ?? ""},
                    {"argumentCount", action.ArgumentCount},
                    {"who", action.Who},
                    {"relative", action.Relative},
                    {"isNot", action.IsNot}
                });
            }
            evtData["actions"] = actionList;
            events.Add(evtData);
        }
    }
    objectEventsMap[objectKey] = events;
}
File.WriteAllText(objectEventsPath, JsonSerializer.Serialize(objectEventsMap));
Console.WriteLine($"[ExportAssetOrder] Exported event mapping for {objectEventsMap.Count} objects to: {objectEventsPath}");

// Export full TexturePageItems data - needed to rebuild TPIs when texture atlases are repacked
string tpiPath = Path.Combine(outputDir, "texture_page_items.json");
var tpiList = new List<int[]>();
foreach (var tpi in Data.TexturePageItems)
{
    int texIdx = tpi.TexturePage != null ? Data.EmbeddedTextures.IndexOf(tpi.TexturePage) : -1;
    tpiList.Add(new int[] {
        texIdx,
        tpi.SourceX, tpi.SourceY, tpi.SourceWidth, tpi.SourceHeight,
        tpi.TargetX, tpi.TargetY, tpi.TargetWidth, tpi.TargetHeight,
        tpi.BoundingWidth, tpi.BoundingHeight
    });
}
File.WriteAllText(tpiPath, JsonSerializer.Serialize(tpiList));
Console.WriteLine($"[ExportAssetOrder] Exported {tpiList.Count} TexturePageItems to: {tpiPath}");

// Export sprite frame → TPI index mapping for ALL sprites
// This allows relinking sprite frames to correct TPIs after TPI rebuild
string spriteFrameMapPath = Path.Combine(outputDir, "sprite_frame_map.json");
var spriteFrameMap = new Dictionary<string, int[]>();
foreach (var sprite in Data.Sprites)
{
    if (sprite?.Textures == null || sprite.Textures.Count == 0) continue;
    string key = sprite.Name?.Content ?? Data.Sprites.IndexOf(sprite).ToString();
    var indices = new int[sprite.Textures.Count];
    for (int f = 0; f < sprite.Textures.Count; f++)
    {
        var tpi = sprite.Textures[f]?.Texture;
        indices[f] = tpi != null ? Data.TexturePageItems.IndexOf(tpi) : -1;
    }
    spriteFrameMap[key] = indices;
}
// Also export background and font TPI indices
var bgFrameMap = new Dictionary<string, int>();
foreach (var bg in Data.Backgrounds)
{
    if (bg?.Texture == null) continue;
    string key = bg.Name?.Content ?? Data.Backgrounds.IndexOf(bg).ToString();
    bgFrameMap[key] = Data.TexturePageItems.IndexOf(bg.Texture);
}
var fontFrameMap = new Dictionary<string, int>();
foreach (var font in Data.Fonts)
{
    if (font?.Texture == null) continue;
    string key = font.Name?.Content ?? Data.Fonts.IndexOf(font).ToString();
    fontFrameMap[key] = Data.TexturePageItems.IndexOf(font.Texture);
}
var frameMapExport = new Dictionary<string, object> {
    {"sprites", spriteFrameMap},
    {"backgrounds", bgFrameMap},
    {"fonts", fontFrameMap}
};
File.WriteAllText(spriteFrameMapPath, JsonSerializer.Serialize(frameMapExport));
Console.WriteLine($"[ExportAssetOrder] Exported frame maps: {spriteFrameMap.Count} sprites, {bgFrameMap.Count} backgrounds, {fontFrameMap.Count} fonts");
