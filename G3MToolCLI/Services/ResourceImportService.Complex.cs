using System.Text;
using System.Text.Json;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace G3MToolCLI.Services;

public static partial class ResourceImportService
{
    // =========================================================================
    // GameObjects
    // =========================================================================
    private static void ImportGameObjects(UndertaleData data, string inputDir)
    {
        var objDirs = GetDirs(inputDir);
        if (objDirs.Length == 0) return;

        Log($"[ImportGameObjects] Found {objDirs.Length} game object folders to import");

        // Phase 1: Collect all JSONs
        var importDataList = new List<(int TargetIndex, string Name, JsonElement Root, bool IsNew, int ActualIndex)>();

        foreach (string objDir in objDirs)
        {
            string jsonFile = Path.Combine(objDir, "object.json");
            if (!FExists(jsonFile)) continue;

            try
            {
                string jsonContent = FReadText(jsonFile);
                using var jsonDoc = JsonDocument.Parse(jsonContent);
                var root = jsonDoc.RootElement.Clone();

                string objName = "";
                int targetIndex = -1;

                if (root.TryGetProperty("index", out JsonElement indexElm))
                    targetIndex = indexElm.GetInt32();

                string folderName = Path.GetFileName(objDir);
                int idxPos = folderName.LastIndexOf("__idx");
                if (idxPos > 0 && folderName.Length >= idxPos + 9)
                {
                    if (int.TryParse(folderName.AsSpan(idxPos + 5, 4), out int parsedIdx))
                        targetIndex = parsedIdx;
                }

                if (root.TryGetProperty("name", out JsonElement nameElm))
                    objName = nameElm.GetString() ?? "";
                else
                    objName = idxPos > 0 ? folderName[..idxPos] : folderName;

                if (string.IsNullOrEmpty(objName)) continue;

                int nameCount = importDataList.Count(d => d.Name == objName);
                bool existsInData = data.GameObjects.ByName(objName) != null;
                bool isNew = !existsInData || nameCount > 0;

                importDataList.Add((targetIndex, objName, root, isNew, -1));
            }
            catch (Exception ex) { Log($"[ImportGameObjects] Parse error: {Path.GetFileName(objDir)}: {ex.Message}"); }
        }

        // Phase 2: Append new objects
        int created = 0, updated = 0, imported = 0;
        for (int i = 0; i < importDataList.Count; i++)
        {
            var (targetIndex, name, root, isNew, actualIndex) = importDataList[i];
            if (!isNew) continue;

            var go = new UndertaleGameObject { Name = data.Strings.MakeString(name) };
            data.GameObjects.Add(go);
            importDataList[i] = (targetIndex, name, root, isNew, data.GameObjects.Count - 1);
            created++;
        }

        // Phase 3: Apply properties
        foreach (var (targetIndex, name, root, isNew, actualIndex) in importDataList)
        {
            try
            {
                UndertaleGameObject? go = null;

                if (isNew && actualIndex >= 0 && actualIndex < data.GameObjects.Count)
                    go = data.GameObjects[actualIndex];
                else if (targetIndex >= 0 && targetIndex < data.GameObjects.Count)
                {
                    go = data.GameObjects[targetIndex];
                    if (go?.Name?.Content != name)
                        go = data.GameObjects.ByName(name);
                }
                else
                    go = data.GameObjects.ByName(name);

                if (go == null) continue;

                if (root.TryGetProperty("parent", out JsonElement parentElm))
                {
                    string pName = parentElm.GetString() ?? "";
                    go.ParentId = pName.Length > 0 ? data.GameObjects.ByName(pName) : null;
                }

                if (root.TryGetProperty("sprite", out JsonElement sprElm))
                {
                    string sn = sprElm.GetString() ?? "";
                    if (sn.Length > 0)
                    {
                        var spr = data.Sprites.ByName(sn);
                        if (spr != null) go.Sprite = spr;
                    }
                    else go.Sprite = null;
                }

                if (root.TryGetProperty("visible", out JsonElement visElm)) go.Visible = visElm.GetBoolean();
                if (root.TryGetProperty("solid", out JsonElement solElm)) go.Solid = solElm.GetBoolean();
                if (root.TryGetProperty("depth", out JsonElement depElm)) go.Depth = depElm.GetInt32();
                if (root.TryGetProperty("persistent", out JsonElement perElm)) go.Persistent = perElm.GetBoolean();

                if (root.TryGetProperty("textureMask", out JsonElement tmElm))
                {
                    string tmName = tmElm.GetString() ?? "";
                    go.TextureMaskId = tmName.Length > 0 ? data.Sprites.ByName(tmName) : null;
                }

                if (data.IsVersionAtLeast(2022, 5) && root.TryGetProperty("managed", out JsonElement mngElm))
                    go.Managed = mngElm.GetBoolean();

                if (root.TryGetProperty("usesPhysics", out JsonElement upElm)) go.UsesPhysics = upElm.GetBoolean();
                if (root.TryGetProperty("isSensor", out JsonElement isElm)) go.IsSensor = isElm.GetBoolean();
                if (root.TryGetProperty("collisionShape", out JsonElement csElm)) go.CollisionShape = (CollisionShapeFlags)csElm.GetInt32();
                if (root.TryGetProperty("density", out JsonElement denElm)) go.Density = (float)denElm.GetDouble();
                if (root.TryGetProperty("restitution", out JsonElement resElm)) go.Restitution = (float)resElm.GetDouble();
                if (root.TryGetProperty("group", out JsonElement grpElm)) go.Group = (uint)grpElm.GetInt64();
                if (root.TryGetProperty("linearDamping", out JsonElement ldElm)) go.LinearDamping = (float)ldElm.GetDouble();
                if (root.TryGetProperty("angularDamping", out JsonElement adElm)) go.AngularDamping = (float)adElm.GetDouble();
                if (root.TryGetProperty("friction", out JsonElement frElm)) go.Friction = (float)frElm.GetDouble();
                if (root.TryGetProperty("awake", out JsonElement awElm)) go.Awake = awElm.GetBoolean();
                if (root.TryGetProperty("kinematic", out JsonElement kinElm)) go.Kinematic = kinElm.GetBoolean();

                if (root.TryGetProperty("physicsVertices", out JsonElement pvElm) && pvElm.ValueKind == JsonValueKind.Array)
                {
                    go.PhysicsVertices.Clear();
                    var collection = go.PhysicsVertices;
                    var addMethod = collection.GetType().GetMethod("Add");
                    var vertexType = collection.GetType().GetGenericArguments()[0];
                    foreach (var ve in pvElm.EnumerateArray())
                    {
                        var vertex = Activator.CreateInstance(vertexType)!;
                        if (ve.TryGetProperty("x", out JsonElement vxElm))
                            vertexType.GetProperty("X")!.SetValue(vertex, (float)vxElm.GetDouble());
                        if (ve.TryGetProperty("y", out JsonElement vyElm))
                            vertexType.GetProperty("Y")!.SetValue(vertex, (float)vyElm.GetDouble());
                        addMethod!.Invoke(collection, [vertex]);
                    }
                }

                if (root.TryGetProperty("events", out JsonElement evtsElm) && evtsElm.ValueKind == JsonValueKind.Array)
                {
                    for (int ei = 0; ei < go.Events.Count; ei++)
                        go.Events[ei].Clear();

                    foreach (var evtElm in evtsElm.EnumerateArray())
                    {
                        if (!evtElm.TryGetProperty("eventType", out JsonElement etElm)) continue;
                        if (!evtElm.TryGetProperty("eventSubtype", out JsonElement esElm)) continue;

                        int eventType = etElm.GetInt32();
                        uint eventSubtype = (uint)esElm.GetInt64();

                        if (eventType == (int)EventType.Collision &&
                            evtElm.TryGetProperty("collisionObjectName", out JsonElement coElm))
                        {
                            string coName = coElm.GetString() ?? "";
                            if (coName.Length > 0)
                            {
                                var co = data.GameObjects.ByName(coName);
                                if (co != null) eventSubtype = (uint)data.GameObjects.IndexOf(co);
                                else continue;
                            }
                        }

                        if (eventType < 0 || eventType >= go.Events.Count) continue;

                        UndertaleGameObject.Event? existingEvt = null;
                        foreach (var e in go.Events[eventType])
                            if (e.EventSubtype == eventSubtype) { existingEvt = e; break; }

                        if (existingEvt == null)
                        {
                            existingEvt = new UndertaleGameObject.Event { EventSubtype = eventSubtype };
                            go.Events[eventType].Add(existingEvt);
                        }

                        if (evtElm.TryGetProperty("actions", out JsonElement actsElm) && actsElm.ValueKind == JsonValueKind.Array)
                        {
                            int ai = 0;
                            foreach (var aElm in actsElm.EnumerateArray())
                            {
                                UndertaleGameObject.EventAction action;
                                if (ai < existingEvt.Actions.Count) action = existingEvt.Actions[ai];
                                else { action = new UndertaleGameObject.EventAction(); existingEvt.Actions.Add(action); }
                                ApplyEventAction(data, action, aElm);
                                ai++;
                            }
                        }
                    }
                }

                if (!isNew) updated++;
                imported++;
            }
            catch (Exception ex) { Log($"[ImportGameObjects] Error: {name}: {ex.Message}"); }
        }

        Log($"[ImportGameObjects] Done. {imported} imported ({created} new, {updated} updated)");
    }

    // =========================================================================
    // ImportAssetOrder
    // =========================================================================
    private static void ImportAssetOrderInternal(UndertaleData data, string inputDir)
    {
        string assetOrderPath = Path.Combine(inputDir, "asset_order.txt");
        if (!FExists(assetOrderPath))
        {
            Log("[ImportAssetOrder] No asset_order.txt found - skipping");
            return;
        }

        Log($"[ImportAssetOrder] Loading asset order from: {assetOrderPath}");
        string[] lines = FReadLines(assetOrderPath);

        string currentType = "";
        var currentList = new List<string>();
        int totalReorganized = 0;
        var targetCounts = new Dictionary<string, int>();

        void SubmitList()
        {
            if (currentList.Count == 0) return;
            switch (currentType)
            {
                case "sounds": Reorganize(data.Sounds, currentList, "Sounds"); totalReorganized++; break;
                case "sprites": Reorganize(data.Sprites, currentList, "Sprites"); totalReorganized++; break;
                case "backgrounds": Reorganize(data.Backgrounds, currentList, "Backgrounds"); totalReorganized++; break;
                case "paths": Reorganize(data.Paths, currentList, "Paths"); totalReorganized++; break;
                case "scripts":
                    int createdScripts = 0;
                    foreach (var sn in currentList)
                    {
                        if (data.Scripts.ByName(sn) == null)
                        {
                            var script = new UndertaleScript
                            {
                                Name = data.Strings.MakeString(sn),
                                Code = data.Code.ByName("gml_Script_" + sn) ?? data.Code.ByName("gml_GlobalScript_" + sn)
                            };
                            data.Scripts.Add(script);
                            createdScripts++;
                        }
                    }
                    if (createdScripts > 0) Log($"[ImportAssetOrder] Scripts: Created {createdScripts} new script entries");
                    // Snapshot scripts that have a live Code entry before Reorganize -
                    // Reorganize drops entries not in TARGET order, but dropping a Script
                    // whose Code entry still exists causes a runtime crash.
                    var targetScriptNames = new HashSet<string>(currentList);
                    var preservedScripts = new List<UndertaleScript>();
                    foreach (var s in data.Scripts)
                    {
                        if (s?.Name?.Content != null && !targetScriptNames.Contains(s.Name.Content) && s.Code != null)
                            preservedScripts.Add(s);
                    }
                    Reorganize(data.Scripts, currentList, "Scripts"); totalReorganized++;
                    // Re-add scripts with live Code entries that were dropped by Reorganize
                    if (preservedScripts.Count > 0)
                    {
                        foreach (var ps in preservedScripts)
                        {
                            if (data.Scripts.All(s => s?.Name?.Content != ps.Name?.Content))
                                data.Scripts.Add(ps);
                        }
                        Log($"[ImportAssetOrder] Scripts: Preserved {preservedScripts.Count} script(s) with live Code entries not in TARGET order");
                    }
                    break;
                case "fonts": Reorganize(data.Fonts, currentList, "Fonts"); totalReorganized++; break;
                case "objects":
                    var collisionInfo = new List<(UndertaleGameObject obj, int evtIdx, string targetName)>();
                    foreach (var obj in data.GameObjects)
                    {
                        if (obj == null) continue;
                        var collisions = obj.Events[(int)EventType.Collision];
                        for (int ci = 0; ci < collisions.Count; ci++)
                        {
                            uint sub = collisions[ci].EventSubtype;
                            if (sub < (uint)data.GameObjects.Count)
                            {
                                var target = data.GameObjects[(int)sub];
                                if (target?.Name?.Content != null)
                                    collisionInfo.Add((obj, ci, target.Name.Content));
                            }
                        }
                    }
                    Reorganize(data.GameObjects, currentList, "GameObjects");
                    int fixedCollisions = 0;
                    foreach (var (obj, evtIdx, tName) in collisionInfo)
                    {
                        var target = data.GameObjects.ByName(tName);
                        if (target != null)
                        {
                            var collisions = obj.Events[(int)EventType.Collision];
                            if (evtIdx < collisions.Count)
                            {
                                uint newIdx = (uint)data.GameObjects.IndexOf(target);
                                if (collisions[evtIdx].EventSubtype != newIdx)
                                {
                                    collisions[evtIdx].EventSubtype = newIdx;
                                    fixedCollisions++;
                                }
                            }
                        }
                    }
                    if (fixedCollisions > 0) Log($"[ImportAssetOrder] Fixed {fixedCollisions} collision event subtypes");
                    totalReorganized++;
                    break;
                case "timelines": Reorganize(data.Timelines, currentList, "Timelines"); totalReorganized++; break;
                case "rooms": Reorganize(data.Rooms, currentList, "Rooms"); totalReorganized++; break;
                case "shaders": Reorganize(data.Shaders, currentList, "Shaders"); totalReorganized++; break;
                case "extensions": Reorganize(data.Extensions, currentList, "Extensions"); totalReorganized++; break;
                case "audiogroups": Reorganize(data.AudioGroups, currentList, "AudioGroups"); totalReorganized++; break;
            }
        }

        foreach (string line in lines)
        {
            if (line.StartsWith("@@") && line.EndsWith("@@"))
            {
                SubmitList();
                currentType = line[2..^2].ToLower();
                currentList.Clear();
            }
            else if (currentType == "counts" && line.Contains('='))
            {
                var parts = line.Trim().Split('=', 2);
                if (parts.Length == 2 && int.TryParse(parts[1], out int count))
                    targetCounts[parts[0]] = count;
            }
            else if (!string.IsNullOrWhiteSpace(line))
                currentList.Add(line.Trim());
        }
        SubmitList();
        Log($"[ImportAssetOrder] Reorganized {totalReorganized} asset types");

        // Sync object events
        string eventsPath = Path.Combine(inputDir, "object_events.json");
        if (FExists(eventsPath))
        {
            var targetEventsRoot = JsonSerializer.Deserialize<JsonElement>(FReadText(eventsPath));
            int eventsRemoved = 0, objectsFixed = 0;
            foreach (var obj in data.GameObjects)
            {
                if (obj?.Name?.Content == null) continue;
                if (!targetEventsRoot.TryGetProperty(obj.Name.Content, out JsonElement targetEvents)) continue;

                // Build key set: for collision events (type=4), use collision object NAME (cn) 
                // to handle different object orderings across patches
                var keys = new HashSet<string>();
                var collisionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var evt in targetEvents.EnumerateArray())
                {
                    int t = evt.GetProperty("t").GetInt32();
                    if (t == 4 && evt.TryGetProperty("cn", out var cnElem))
                    {
                        string? cn = cnElem.GetString();
                        if (!string.IsNullOrEmpty(cn))
                        {
                            collisionNames.Add(cn);
                            continue;
                        }
                    }
                    keys.Add($"{t}_{evt.GetProperty("s").GetUInt32()}");
                }

                bool fixedObj = false;
                for (int et = 0; et < obj.Events.Count; et++)
                {
                    for (int j = obj.Events[et].Count - 1; j >= 0; j--)
                    {
                        bool shouldKeep;
                        if (et == 4) // Collision event - match by object name
                        {
                            int collObjIdx = (int)obj.Events[et][j].EventSubtype;
                            string? collObjName = collObjIdx >= 0 && collObjIdx < data.GameObjects.Count
                                ? data.GameObjects[collObjIdx]?.Name?.Content : null;
                            shouldKeep = !string.IsNullOrEmpty(collObjName) && collisionNames.Contains(collObjName);
                        }
                        else
                        {
                            shouldKeep = keys.Contains($"{et}_{obj.Events[et][j].EventSubtype}");
                        }

                        if (!shouldKeep)
                        {
                            var removedEvt = obj.Events[et][j];
                            string evtCode = removedEvt.Actions.Count > 0 ? (removedEvt.Actions[0].CodeId?.Name?.Content ?? "?") : "?";
                            Log($"  EventSync: removing {obj.Name.Content} event type={et} sub={removedEvt.EventSubtype} code={evtCode}");
                            obj.Events[et].RemoveAt(j);
                            eventsRemoved++;
                            fixedObj = true;
                        }
                    }
                }
                if (fixedObj) objectsFixed++;
            }
            if (eventsRemoved > 0) Log($"[ImportAssetOrder] Event sync: removed {eventsRemoved} events from {objectsFixed} objects");
        }

        // Trim EmbeddedTextures
        if (targetCounts.TryGetValue("EmbeddedTextures", out int targetTexCount))
        {
            int cur = data.EmbeddedTextures.Count;
            if (cur > targetTexCount)
            {
                for (int i = cur - 1; i >= targetTexCount; i--) data.EmbeddedTextures.RemoveAt(i);
                Log($"[ImportAssetOrder] EmbeddedTextures: trimmed from {cur} to {targetTexCount}");
            }
        }

        // Rebuild TPIs from TARGET
        string tpiPath = Path.Combine(inputDir, "texture_page_items.json");
        string fmPath = Path.Combine(inputDir, "sprite_frame_map.json");

        if (FExists(tpiPath) && FExists(fmPath))
        {
            int oldCount = data.TexturePageItems.Count;
            var tpiData = JsonSerializer.Deserialize<List<int[]>>(FReadText(tpiPath))!;
            int newCount = tpiData.Count;

            void ApplyTpi(UndertaleTexturePageItem tpi, int[] arr)
            {
                int texIdx = arr[0];
                tpi.TexturePage = (texIdx >= 0 && texIdx < data.EmbeddedTextures.Count)
                    ? data.EmbeddedTextures[texIdx]
                    : (data.EmbeddedTextures.Count > 0 ? data.EmbeddedTextures[0] : null!);
                tpi.SourceX = (ushort)arr[1]; tpi.SourceY = (ushort)arr[2];
                tpi.SourceWidth = (ushort)arr[3]; tpi.SourceHeight = (ushort)arr[4];
                tpi.TargetX = (ushort)arr[5]; tpi.TargetY = (ushort)arr[6];
                tpi.TargetWidth = (ushort)arr[7]; tpi.TargetHeight = (ushort)arr[8];
                tpi.BoundingWidth = (ushort)arr[9]; tpi.BoundingHeight = (ushort)arr[10];
            }

            int upd = Math.Min(oldCount, newCount);
            for (int i = 0; i < upd; i++) ApplyTpi(data.TexturePageItems[i], tpiData[i]);
            for (int i = oldCount; i < newCount; i++)
            {
                var tpi = new UndertaleTexturePageItem();
                ApplyTpi(tpi, tpiData[i]);
                data.TexturePageItems.Add(tpi);
            }
            for (int i = oldCount - 1; i >= newCount; i--) data.TexturePageItems.RemoveAt(i);

            Log($"[ImportAssetOrder] TexturePageItems: {oldCount} -> {data.TexturePageItems.Count}");

            var fmRoot = JsonSerializer.Deserialize<JsonElement>(FReadText(fmPath));

            int sprRelinked = 0;
            if (fmRoot.TryGetProperty("sprites", out JsonElement sprMap))
            {
                foreach (var spr in data.Sprites)
                {
                    if (spr?.Textures == null) continue;
                    string key = spr.Name?.Content ?? data.Sprites.IndexOf(spr).ToString();
                    if (!sprMap.TryGetProperty(key, out JsonElement indices)) continue;
                    var idxArr = indices.EnumerateArray().Select(e => e.GetInt32()).ToArray();
                    spr.Textures.Clear();
                    foreach (int tpiIdx in idxArr)
                    {
                        var entry = new UndertaleSprite.TextureEntry();
                        if (tpiIdx >= 0 && tpiIdx < data.TexturePageItems.Count)
                            entry.Texture = data.TexturePageItems[tpiIdx];
                        spr.Textures.Add(entry);
                    }
                    sprRelinked++;
                }
            }

            int bgRelinked = 0;
            if (fmRoot.TryGetProperty("backgrounds", out JsonElement bgMap))
            {
                foreach (var bg in data.Backgrounds)
                {
                    if (bg == null) continue;
                    string key = bg.Name?.Content ?? data.Backgrounds.IndexOf(bg).ToString();
                    if (bgMap.TryGetProperty(key, out JsonElement idx))
                    {
                        int ti = idx.GetInt32();
                        if (ti >= 0 && ti < data.TexturePageItems.Count) { bg.Texture = data.TexturePageItems[ti]; bgRelinked++; }
                    }
                }
            }

            int fontRelinked = 0;
            if (fmRoot.TryGetProperty("fonts", out JsonElement fontMap))
            {
                foreach (var font in data.Fonts)
                {
                    if (font == null) continue;
                    string key = font.Name?.Content ?? data.Fonts.IndexOf(font).ToString();
                    if (fontMap.TryGetProperty(key, out JsonElement idx))
                    {
                        int ti = idx.GetInt32();
                        if (ti >= 0 && ti < data.TexturePageItems.Count) { font.Texture = data.TexturePageItems[ti]; fontRelinked++; }
                    }
                }
            }

            Log($"[ImportAssetOrder] Relinked: {sprRelinked} sprites, {bgRelinked} backgrounds, {fontRelinked} fonts");
        }
        else
        {
            if (targetCounts.TryGetValue("TexturePageItems", out int targetTpiCount))
            {
                int cur = data.TexturePageItems.Count;
                if (cur > targetTpiCount)
                {
                    for (int i = cur - 1; i >= targetTpiCount; i--) data.TexturePageItems.RemoveAt(i);
                    Log($"[ImportAssetOrder] TexturePageItems: trimmed {cur} to {targetTpiCount} (legacy)");
                }
            }
        }
    }

    private static void Reorganize<T>(IList<T> list, List<string> order, string typeName) where T : UndertaleNamedResource, new()
    {
        if (order.Count == 0) return;

        var nameToIndices = new Dictionary<string, List<int>>();
        var emptyNameIndices = new List<int>();

        for (int i = 0; i < list.Count; i++)
        {
            var asset = list[i];
            if (asset == null) continue;
            string? name = asset.Name?.Content;
            if (string.IsNullOrEmpty(name)) { emptyNameIndices.Add(i); }
            else
            {
                if (!nameToIndices.ContainsKey(name)) nameToIndices[name] = [];
                nameToIndices[name].Add(i);
            }
        }

        var newOrder = new List<T>();
        var usedIndices = new HashSet<int>();
        int missingCount = 0, emptyNameIdx = 0;

        foreach (string name in order)
        {
            if (name == "(null)") continue;
            if (int.TryParse(name, out _))
            {
                if (emptyNameIdx < emptyNameIndices.Count)
                {
                    int idx = emptyNameIndices[emptyNameIdx];
                    if (!usedIndices.Contains(idx)) { newOrder.Add(list[idx]); usedIndices.Add(idx); }
                    emptyNameIdx++;
                }
                continue;
            }
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (nameToIndices.TryGetValue(name, out var indices))
            {
                foreach (int idx in indices)
                {
                    if (!usedIndices.Contains(idx)) { newOrder.Add(list[idx]); usedIndices.Add(idx); break; }
                }
            }
            else missingCount++;
        }

        int droppedCount = 0;
        for (int i = 0; i < list.Count; i++)
            if (!usedIndices.Contains(i) && list[i] != null) droppedCount++;
        if (droppedCount > 0) Log($"[ImportAssetOrder] {typeName}: Dropping {droppedCount} resource(s) not in TARGET order");

        list.Clear();
        foreach (var asset in newOrder) list.Add(asset);
        Log($"[ImportAssetOrder] {typeName}: Reorganized {list.Count} items (missing: {missingCount}, dropped: {droppedCount})");
    }
}
