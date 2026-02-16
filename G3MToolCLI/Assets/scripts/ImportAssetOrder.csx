// ImportAssetOrder.csx - Reorganizes assets to match a specified order
// Based on UTMT's ImportAssetOrder.csx by colinator27
// This script is independent and can be used standalone

using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using UndertaleModLib.Models;

EnsureDataLoaded();

string inputDir = InputDir;
if (string.IsNullOrEmpty(inputDir))
    throw new Exception("InputDir is not set.");

string assetOrderPath = Path.Combine(inputDir, "asset_order.txt");
if (!File.Exists(assetOrderPath))
{
    Console.WriteLine("[ImportAssetOrder] No asset_order.txt found - skipping asset reordering");
    return;
}

Console.WriteLine($"[ImportAssetOrder] Loading asset order from: {assetOrderPath}");

string[] lines = File.ReadAllLines(assetOrderPath);

void Reorganize<T>(IList<T> list, List<string> order, string typeName) where T : UndertaleNamedResource, new()
{
    if (order.Count == 0)
        return;

    int originalCount = list.Count;
    Console.WriteLine($"[ImportAssetOrder] {typeName}: Starting with {originalCount} items, order has {order.Count} entries");

    // Build lookup: name -> list of indices (to handle duplicates)
    Dictionary<string, List<int>> nameToIndices = new Dictionary<string, List<int>>();
    List<int> emptyNameIndices = new List<int>();

    for (int i = 0; i < list.Count; i++)
    {
        T asset = list[i];
        if (asset == null)
            continue;
        string assetName = asset.Name?.Content;
        if (string.IsNullOrEmpty(assetName))
        {
            emptyNameIndices.Add(i);
        }
        else
        {
            if (!nameToIndices.ContainsKey(assetName))
                nameToIndices[assetName] = new List<int>();
            nameToIndices[assetName].Add(i);
        }
    }

    // Build the new ordered list - ONLY include assets that exist in current data
    List<T> newOrder = new List<T>();
    HashSet<int> usedIndices = new HashSet<int>();
    int missingCount = 0;
    int emptyNameIdx = 0; // Track which empty-name asset to use next

    // Add assets in the order specified (only if they exist)
    foreach (string name in order)
    {
        // Skip null entries but NOT empty strings (those are valid unnamed assets)
        if (name == "(null)")
            continue;

        // Handle numeric indices - these represent assets with empty names
        if (int.TryParse(name, out int numericIndex))
        {
            // Use the next available empty-name asset
            if (emptyNameIdx < emptyNameIndices.Count)
            {
                int idx = emptyNameIndices[emptyNameIdx];
                if (!usedIndices.Contains(idx))
                {
                    newOrder.Add(list[idx]);
                    usedIndices.Add(idx);
                }
                emptyNameIdx++;
            }
            continue;
        }

        // Skip whitespace-only entries
        if (string.IsNullOrWhiteSpace(name))
            continue;

        // Find next unused asset with this name (handles duplicates)
        if (nameToIndices.TryGetValue(name, out var indices))
        {
            foreach (int idx in indices)
            {
                if (!usedIndices.Contains(idx))
                {
                    newOrder.Add(list[idx]);
                    usedIndices.Add(idx);
                    break;
                }
            }
        }
        else
        {
            // Asset from order file doesn't exist in our data - that's expected for deleted assets
            missingCount++;
        }
    }

    // Resources NOT in the TARGET order are ORIGINAL-only and should be dropped (deleted).
    // The order file comes from TARGET export and lists ALL resources that should exist.
    // Any resource in our data not in this list was deleted in TARGET.
    int droppedCount = 0;
    for (int i = 0; i < list.Count; i++)
    {
        if (!usedIndices.Contains(i) && list[i] != null)
        {
            droppedCount++;
        }
    }
    if (droppedCount > 0)
        Console.WriteLine($"[ImportAssetOrder] {typeName}: Dropping {droppedCount} resource(s) not in TARGET order (deleted in TARGET)");

    // Clear and rebuild the list
    list.Clear();
    foreach (T asset in newOrder)
    {
        list.Add(asset);
    }

    Console.WriteLine($"[ImportAssetOrder] {typeName}: Reorganized {list.Count} items (missing from order: {missingCount}, dropped: {droppedCount})");
}

string currentType = "";
List<string> currentList = new List<string>();
int totalReorganized = 0;

void SubmitList()
{
    if (currentList.Count == 0)
        return;

    switch (currentType)
    {
        case "sounds":
            Reorganize(Data.Sounds, currentList, "Sounds");
            totalReorganized++;
            break;
        case "sprites":
            Reorganize(Data.Sprites, currentList, "Sprites");
            totalReorganized++;
            break;
        case "backgrounds":
            Reorganize(Data.Backgrounds, currentList, "Backgrounds");
            totalReorganized++;
            break;
        case "paths":
            Reorganize(Data.Paths, currentList, "Paths");
            totalReorganized++;
            break;
        case "scripts":
            // Create missing scripts that exist in TARGET order but not in our data
            int createdScripts = 0;
            foreach (var scriptName in currentList)
            {
                if (Data.Scripts.ByName(scriptName) == null)
                {
                    var script = new UndertaleScript();
                    script.Name = Data.Strings.MakeString(scriptName);
                    // Link to code entry (convention: gml_Script_<name> or gml_GlobalScript_<name>)
                    var code = Data.Code.ByName("gml_Script_" + scriptName)
                            ?? Data.Code.ByName("gml_GlobalScript_" + scriptName);
                    if (code != null)
                        script.Code = code;
                    Data.Scripts.Add(script);
                    createdScripts++;
                }
            }
            if (createdScripts > 0)
                Console.WriteLine($"[ImportAssetOrder] Scripts: Created {createdScripts} new script entries from TARGET order");
            Reorganize(Data.Scripts, currentList, "Scripts");
            totalReorganized++;
            break;
        case "fonts":
            Reorganize(Data.Fonts, currentList, "Fonts");
            totalReorganized++;
            break;
        case "objects":
            // Save collision target names BEFORE reorder (subtypes are indices that will change)
            var collisionInfo = new List<(UndertaleGameObject obj, int evtIdx, string targetName)>();
            foreach (var obj in Data.GameObjects)
            {
                if (obj == null) continue;
                var collisions = obj.Events[(int)EventType.Collision];
                for (int ci = 0; ci < collisions.Count; ci++)
                {
                    uint subtype = collisions[ci].EventSubtype;
                    if (subtype < (uint)Data.GameObjects.Count)
                    {
                        var target = Data.GameObjects[(int)subtype];
                        if (target?.Name?.Content != null)
                            collisionInfo.Add((obj, ci, target.Name.Content));
                    }
                }
            }

            Reorganize(Data.GameObjects, currentList, "GameObjects");

            // Fix collision event subtypes after reorder
            int fixedCollisions = 0;
            foreach (var (obj, evtIdx, targetName) in collisionInfo)
            {
                var target = Data.GameObjects.ByName(targetName);
                if (target != null)
                {
                    var collisions = obj.Events[(int)EventType.Collision];
                    if (evtIdx < collisions.Count)
                    {
                        uint newIdx = (uint)Data.GameObjects.IndexOf(target);
                        if (collisions[evtIdx].EventSubtype != newIdx)
                        {
                            collisions[evtIdx].EventSubtype = newIdx;
                            fixedCollisions++;
                        }
                    }
                }
            }
            if (fixedCollisions > 0)
                Console.WriteLine($"[ImportAssetOrder] Fixed {fixedCollisions} collision event subtypes after GameObjects reorder");

            totalReorganized++;
            break;
        case "timelines":
            Reorganize(Data.Timelines, currentList, "Timelines");
            totalReorganized++;
            break;
        case "rooms":
            Reorganize(Data.Rooms, currentList, "Rooms");
            totalReorganized++;
            break;
        case "shaders":
            Reorganize(Data.Shaders, currentList, "Shaders");
            totalReorganized++;
            break;
        case "extensions":
            Reorganize(Data.Extensions, currentList, "Extensions");
            totalReorganized++;
            break;
        case "audiogroups":
            Reorganize(Data.AudioGroups, currentList, "AudioGroups");
            totalReorganized++;
            break;
    }
}

Dictionary<string, int> targetCounts = new Dictionary<string, int>();

foreach (string line in lines)
{
    if (line.StartsWith("@@") && line.EndsWith("@@"))
    {
        SubmitList();
        currentType = line.Substring(2, line.Length - 4).ToLower();
        currentList.Clear();
    }
    else if (currentType == "counts" && line.Contains('='))
    {
        var parts = line.Trim().Split('=', 2);
        if (parts.Length == 2 && int.TryParse(parts[1], out int count))
            targetCounts[parts[0]] = count;
    }
    else if (!string.IsNullOrWhiteSpace(line))
    {
        currentList.Add(line.Trim());
    }
}
SubmitList();

Console.WriteLine($"[ImportAssetOrder] Reorganized {totalReorganized} asset types");

// Synchronize object events with TARGET - remove events that were deleted in TARGET
string objectEventsJsonPath = Path.Combine(inputDir, "object_events.json");
if (File.Exists(objectEventsJsonPath))
{
    var targetEventsRoot = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(objectEventsJsonPath));
    int eventsRemoved = 0;
    int objectsFixed = 0;

    foreach (var obj in Data.GameObjects)
    {
        if (obj?.Name?.Content == null) continue;
        if (!targetEventsRoot.TryGetProperty(obj.Name.Content, out JsonElement targetEvents)) continue;

        // Build set of (eventType, eventSubtype) that TARGET has
        var targetEventKeys = new HashSet<string>();
        foreach (var evt in targetEvents.EnumerateArray())
        {
            int t = evt.GetProperty("t").GetInt32();
            uint s = evt.GetProperty("s").GetUInt32();
            targetEventKeys.Add($"{t}_{s}");
        }

        // Remove events in OURS that TARGET doesn't have
        bool fixed_ = false;
        for (int evtType = 0; evtType < obj.Events.Count; evtType++)
        {
            for (int j = obj.Events[evtType].Count - 1; j >= 0; j--)
            {
                var evt = obj.Events[evtType][j];
                string key = $"{evtType}_{evt.EventSubtype}";
                if (!targetEventKeys.Contains(key))
                {
                    obj.Events[evtType].RemoveAt(j);
                    eventsRemoved++;
                    fixed_ = true;
                }
            }
        }
        if (fixed_) objectsFixed++;
    }

    if (eventsRemoved > 0)
        Console.WriteLine($"[ImportAssetOrder] Event sync: removed {eventsRemoved} deleted events from {objectsFixed} objects");
}

// Trim EmbeddedTextures to match TARGET count
if (targetCounts.TryGetValue("EmbeddedTextures", out int targetTexCount))
{
    int currentCount = Data.EmbeddedTextures.Count;
    if (currentCount > targetTexCount)
    {
        for (int i = currentCount - 1; i >= targetTexCount; i--)
            Data.EmbeddedTextures.RemoveAt(i);
        Console.WriteLine($"[ImportAssetOrder] EmbeddedTextures: trimmed from {currentCount} to {targetTexCount}");
    }
}

// Rebuild ALL TexturePageItems from TARGET export data
// This fixes the core issue: when MOD repacks texture atlases, ALL TPIs need updating,
// not just those referenced by changed sprites
string tpiJsonPath = Path.Combine(inputDir, "texture_page_items.json");
string frameMapJsonPath = Path.Combine(inputDir, "sprite_frame_map.json");

if (File.Exists(tpiJsonPath) && File.Exists(frameMapJsonPath))
{
    int oldTpiCount = Data.TexturePageItems.Count;
    var tpiData = JsonSerializer.Deserialize<List<int[]>>(File.ReadAllText(tpiJsonPath));
    int newTpiCount = tpiData.Count;

    // Update TPIs IN-PLACE to preserve object identity (avoids dangling pointers from
    // objects we don't scan, like dropped sprites still referenced by game objects)
    void ApplyTpiData(UndertaleTexturePageItem tpi, int[] arr)
    {
        int texIdx = arr[0];
        if (texIdx >= 0 && texIdx < Data.EmbeddedTextures.Count)
            tpi.TexturePage = Data.EmbeddedTextures[texIdx];
        else if (Data.EmbeddedTextures.Count > 0)
            tpi.TexturePage = Data.EmbeddedTextures[0];
        tpi.SourceX = (ushort)arr[1];
        tpi.SourceY = (ushort)arr[2];
        tpi.SourceWidth = (ushort)arr[3];
        tpi.SourceHeight = (ushort)arr[4];
        tpi.TargetX = (ushort)arr[5];
        tpi.TargetY = (ushort)arr[6];
        tpi.TargetWidth = (ushort)arr[7];
        tpi.TargetHeight = (ushort)arr[8];
        tpi.BoundingWidth = (ushort)arr[9];
        tpi.BoundingHeight = (ushort)arr[10];
    }

    // 1a. Update existing TPIs in-place
    int updated = Math.Min(oldTpiCount, newTpiCount);
    for (int i = 0; i < updated; i++)
        ApplyTpiData(Data.TexturePageItems[i], tpiData[i]);

    // 1b. Add new TPIs if target has more
    for (int i = oldTpiCount; i < newTpiCount; i++)
    {
        var tpi = new UndertaleTexturePageItem();
        ApplyTpiData(tpi, tpiData[i]);
        Data.TexturePageItems.Add(tpi);
    }

    // 1c. Remove excess TPIs if we have too many
    for (int i = oldTpiCount - 1; i >= newTpiCount; i--)
        Data.TexturePageItems.RemoveAt(i);

    Console.WriteLine($"[ImportAssetOrder] TexturePageItems: {oldTpiCount} -> {Data.TexturePageItems.Count} (updated {updated}, added {Math.Max(0, newTpiCount - oldTpiCount)}, removed {Math.Max(0, oldTpiCount - newTpiCount)})");

    // 2. Relink all sprite/background/font frame references to correct TPI indices
    var frameMapRoot = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(frameMapJsonPath));

    int spriteRelinked = 0;
    if (frameMapRoot.TryGetProperty("sprites", out JsonElement spriteMap))
    {
        foreach (var sprite in Data.Sprites)
        {
            if (sprite?.Textures == null) continue;
            string key = sprite.Name?.Content ?? Data.Sprites.IndexOf(sprite).ToString();
            if (!spriteMap.TryGetProperty(key, out JsonElement indices)) continue;

            var idxArr = indices.EnumerateArray().Select(e => e.GetInt32()).ToArray();
            sprite.Textures.Clear();
            foreach (int tpiIdx in idxArr)
            {
                var entry = new UndertaleSprite.TextureEntry();
                if (tpiIdx >= 0 && tpiIdx < Data.TexturePageItems.Count)
                    entry.Texture = Data.TexturePageItems[tpiIdx];
                sprite.Textures.Add(entry);
            }
            spriteRelinked++;
        }
    }

    int bgRelinked = 0;
    if (frameMapRoot.TryGetProperty("backgrounds", out JsonElement bgMap))
    {
        foreach (var bg in Data.Backgrounds)
        {
            if (bg == null) continue;
            string key = bg.Name?.Content ?? Data.Backgrounds.IndexOf(bg).ToString();
            if (bgMap.TryGetProperty(key, out JsonElement idx))
            {
                int tpiIdx = idx.GetInt32();
                if (tpiIdx >= 0 && tpiIdx < Data.TexturePageItems.Count)
                {
                    bg.Texture = Data.TexturePageItems[tpiIdx];
                    bgRelinked++;
                }
            }
        }
    }

    int fontRelinked = 0;
    if (frameMapRoot.TryGetProperty("fonts", out JsonElement fontMap))
    {
        foreach (var font in Data.Fonts)
        {
            if (font == null) continue;
            string key = font.Name?.Content ?? Data.Fonts.IndexOf(font).ToString();
            if (fontMap.TryGetProperty(key, out JsonElement idx))
            {
                int tpiIdx = idx.GetInt32();
                if (tpiIdx >= 0 && tpiIdx < Data.TexturePageItems.Count)
                {
                    font.Texture = Data.TexturePageItems[tpiIdx];
                    fontRelinked++;
                }
            }
        }
    }

    Console.WriteLine($"[ImportAssetOrder] Relinked frames: {spriteRelinked} sprites, {bgRelinked} backgrounds, {fontRelinked} fonts");
}
else
{
    // Fallback: old trimming logic for patches without TPI data
    if (targetCounts.TryGetValue("TexturePageItems", out int targetTpiCount))
    {
        int currentCount = Data.TexturePageItems.Count;
        if (currentCount > targetTpiCount)
        {
            for (int i = currentCount - 1; i >= targetTpiCount; i--)
                Data.TexturePageItems.RemoveAt(i);
            Console.WriteLine($"[ImportAssetOrder] TexturePageItems: trimmed from {currentCount} to {targetTpiCount} (legacy)");
        }
    }
}

// Note: Variables and Functions are NOT pre-populated here.
// The compiler (CodeImportGroup) creates variables/functions during code compilation.
// Local variables (InstanceType.Local) are ALWAYS created fresh by DefineLocal - never reused.
// Pre-populating causes massive duplication (locals from TARGET + new locals from compiler).
// The remaining differences in variable/function ordering and array owner IDs (Push|Int32 values)
// are cosmetic and do not affect runtime behavior.
