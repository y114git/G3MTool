using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

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

        var objectsByName = new Dictionary<string, UndertaleGameObject>(StringComparer.Ordinal);
        for (int i = 0; i < data.GameObjects.Count; i++)
        {
            var obj = data.GameObjects[i];
            if (obj?.Name?.Content != null)
                objectsByName.TryAdd(obj.Name.Content, obj);
        }
        var objectIndices = new Dictionary<UndertaleGameObject, int>();
        for (int i = 0; i < data.GameObjects.Count; i++)
            if (data.GameObjects[i] != null)
                objectIndices[data.GameObjects[i]] = i;
        var spritesByName = new Dictionary<string, UndertaleSprite>(StringComparer.Ordinal);
        foreach (var sprite in data.Sprites)
            if (sprite?.Name?.Content != null)
                spritesByName.TryAdd(sprite.Name.Content, sprite);
        var stringLookup = new Dictionary<string, UndertaleString>(StringComparer.Ordinal);
        foreach (var str in data.Strings)
            if (str?.Content != null)
                stringLookup.TryAdd(str.Content, str);

        UndertaleGameObject? FindObject(string name) =>
            objectsByName.TryGetValue(name, out var obj) ? obj : null;

        UndertaleSprite? FindSprite(string name) =>
            spritesByName.TryGetValue(name, out var sprite) ? sprite : null;

        UndertaleString MakeObjectString(string content)
        {
            if (stringLookup.TryGetValue(content, out var existing))
                return existing;
            var created = new UndertaleString(content);
            if (data.Strings is UndertaleObservableList<UndertaleString> stringList)
                stringList.InternalAdd(created);
            else
                data.Strings.Add(created);
            stringLookup[content] = created;
            return created;
        }

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
                bool existsInData = FindObject(objName) != null;
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

            if (targetIndex >= 0 && targetIndex < data.GameObjects.Count &&
                string.Equals(data.GameObjects[targetIndex]?.Name?.Content, name, StringComparison.Ordinal))
            {
                importDataList[i] = (targetIndex, name, root, isNew, targetIndex);
                updated++;
                continue;
            }

            var go = new UndertaleGameObject { Name = MakeObjectString(name) };
            data.GameObjects.Add(go);
            objectIndices[go] = data.GameObjects.Count - 1;
            objectsByName.TryAdd(name, go);
            importDataList[i] = (targetIndex, name, root, isNew, data.GameObjects.Count - 1);
            created++;
        }

        // Phase 3: Apply properties
        foreach (var (targetIndex, name, root, isNew, actualIndex) in importDataList)
        {
            try
            {
                UndertaleGameObject? go = null;

                if (targetIndex >= 0 && targetIndex < data.GameObjects.Count &&
                    string.Equals(data.GameObjects[targetIndex]?.Name?.Content, name, StringComparison.Ordinal))
                    go = data.GameObjects[targetIndex];
                else if (isNew && actualIndex >= 0 && actualIndex < data.GameObjects.Count)
                    go = data.GameObjects[actualIndex];
                else if (targetIndex >= 0 && targetIndex < data.GameObjects.Count)
                {
                    go = data.GameObjects[targetIndex];
                    if (go?.Name?.Content != name)
                        go = FindObject(name);
                }
                else
                    go = FindObject(name);

                if (go == null) continue;

                if (root.TryGetProperty("parent", out JsonElement parentElm))
                {
                    string pName = parentElm.GetString() ?? "";
                    go.ParentId = pName.Length > 0 ? FindObject(pName) : null;
                }

                if (root.TryGetProperty("sprite", out JsonElement sprElm))
                {
                    string sn = sprElm.GetString() ?? "";
                    if (sn.Length > 0)
                    {
                        var spr = FindSprite(sn);
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
                    go.TextureMaskId = tmName.Length > 0 ? FindSprite(tmName) : null;
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
                                var co = FindObject(coName);
                                if (co != null && objectIndices.TryGetValue(co, out int coIndex)) eventSubtype = (uint)coIndex;
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

    private static int GetGameObjectOccurrence(UndertaleData data, int objectIndex)
    {
        if (objectIndex < 0 || objectIndex >= data.GameObjects.Count)
            return -1;
        var name = data.GameObjects[objectIndex]?.Name?.Content;
        if (name == null)
            return -1;
        int occurrence = 0;
        for (int i = 0; i < objectIndex; i++)
            if (string.Equals(data.GameObjects[i]?.Name?.Content, name, StringComparison.Ordinal))
                occurrence++;
        return occurrence;
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
                                Code = PatchService.ScriptCodeResolver.Resolve(data, sn)
                            };
                            data.Scripts.Add(script);
                            createdScripts++;
                        }
                    }
                    if (createdScripts > 0) Log($"[ImportAssetOrder] Scripts: Created {createdScripts} new script entries");
                    Reorganize(data.Scripts, currentList, "Scripts"); totalReorganized++;
                    while (data.Scripts.Count > currentList.Count)
                        data.Scripts.RemoveAt(data.Scripts.Count - 1);
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
                            int occurrence = evt.TryGetProperty("co", out var coElem) ? coElem.GetInt32() : -1;
                            collisionNames.Add($"{cn}#{occurrence}");
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
                            int occurrence = !string.IsNullOrEmpty(collObjName) ? GetGameObjectOccurrence(data, collObjIdx) : -1;
                            shouldKeep = !string.IsNullOrEmpty(collObjName) &&
                                (collisionNames.Contains($"{collObjName}#{occurrence}") || collisionNames.Contains($"{collObjName}#-1"));
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

        int importedEmbeddedTextures = ImportEmbeddedTexturesForAssetOrder(data, inputDir);

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
            if (!CanResolveTexturePages(tpiData, data.EmbeddedTextures.Count))
            {
                Log("[ImportAssetOrder] Skipping TexturePageItems relink because texture pages are not present in this patch");
                return;
            }
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

    private static bool CanResolveTexturePages(List<int[]> tpiData, int embeddedTextureCount)
    {
        foreach (var tpi in tpiData)
        {
            if (tpi.Length > 0 && tpi[0] >= embeddedTextureCount)
                return false;
        }
        return true;
    }

    private static int ImportEmbeddedTexturesForAssetOrder(UndertaleData data, string inputDir)
    {
        string embeddedDir = Path.Combine(inputDir, "EmbeddedTextures");
        if (!DirExists(embeddedDir))
        {
            string? parent = Path.GetDirectoryName(inputDir);
            embeddedDir = string.IsNullOrEmpty(parent)
                ? "EmbeddedTextures"
                : Path.Combine(parent, "EmbeddedTextures");
        }
        if (!DirExists(embeddedDir))
            return 0;

        int imported = 0;
        foreach (var texDir in GetDirs(embeddedDir))
        {
            string folderName = Path.GetFileName(texDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string jsonPath = Path.Combine(texDir, folderName + ".json");
            string pngPath = Path.Combine(texDir, folderName + ".png");
            string binPath = Path.Combine(texDir, folderName + ".bin");
            if (!FExists(jsonPath) || (!FExists(binPath) && !FExists(pngPath)))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(FReadText(jsonPath));
                var root = doc.RootElement;
                int folderIndex = ParseTrailingResourceIndex(folderName);
                int jsonIndex = root.TryGetProperty("index", out var idxElm) && idxElm.ValueKind == JsonValueKind.Number
                    ? idxElm.GetInt32()
                    : data.EmbeddedTextures.Count;
                int index = folderIndex >= 0 ? folderIndex : jsonIndex;
                string name = root.TryGetProperty("name", out var nameElm) && nameElm.ValueKind == JsonValueKind.String
                    ? nameElm.GetString() ?? ""
                    : "";
                string format = root.TryGetProperty("format", out var formatElm) && formatElm.ValueKind == JsonValueKind.String
                    ? formatElm.GetString() ?? "Png"
                    : "Png";

                while (data.EmbeddedTextures.Count <= index)
                {
                    data.EmbeddedTextures.Add(new UndertaleEmbeddedTexture
                    {
                        Name = new UndertaleString($"Texture {data.EmbeddedTextures.Count}")
                    });
                }

                var texture = data.EmbeddedTextures[index];
                texture.Name = new UndertaleString(string.IsNullOrWhiteSpace(name) ? $"Texture {index}" : name);
                if (root.TryGetProperty("scaled", out var scaledElm))
                    texture.Scaled = scaledElm.ValueKind == JsonValueKind.Number ? scaledElm.GetUInt32() : (scaledElm.GetBoolean() ? 1u : 0u);
                if (root.TryGetProperty("generatedMips", out var mipsElm))
                    texture.GeneratedMips = mipsElm.ValueKind == JsonValueKind.Number ? mipsElm.GetUInt32() : (mipsElm.GetBoolean() ? 1u : 0u);

                bool loadedImage = false;
                if (FExists(binPath))
                {
                    try
                    {
                        using var stream = new MemoryStream(FReadBytes(binPath), writable: false);
                        using var reader = new FileBinaryReader(stream);
                        texture.TextureData.Image = GMImage.FromBinaryReader(
                            reader,
                            stream.Length,
                            data.IsVersionAtLeast(2022, 5));
                        loadedImage = true;
                    }
                    catch (Exception ex) when (FExists(pngPath))
                    {
                        Log($"[ImportAssetOrder] EmbeddedTexture '{folderName}' bin import failed, using png: {ex.Message}");
                    }
                }

                if (!loadedImage)
                {
                    using var image = new MagickImage(FReadBytes(pngPath));
                    var gmImage = GMImage.FromMagickImage(image);
                    if (Enum.TryParse<GMImage.ImageFormat>(format, out var targetFormat) &&
                        targetFormat != GMImage.ImageFormat.Png)
                    {
                        gmImage = gmImage.ConvertToFormat(targetFormat);
                    }
                    else
                    {
                        gmImage = gmImage.ConvertToPng();
                    }
                    texture.TextureData.Image = gmImage;
                }
                imported++;
            }
            catch (Exception ex)
            {
                Log($"[ImportAssetOrder] EmbeddedTexture import error '{folderName}': {ex.Message}");
            }
        }

        if (imported > 0)
            Log($"[ImportAssetOrder] Imported {imported} embedded texture page(s)");
        return imported;
    }

    private static int ParseTrailingResourceIndex(string value)
    {
        var match = TrailingNumberRegex().Match(value);
        return match.Success && int.TryParse(match.Groups[1].Value, out int index) ? index : -1;
    }

    [GeneratedRegex(@"(\d+)$")]
    private static partial Regex TrailingNumberRegex();

    private static void Reorganize<T>(IList<T> list, List<string> order, string typeName) where T : UndertaleNamedResource, new()
    {
        if (order.Count == 0) return;

        var nameToIndices = new Dictionary<string, List<int>>();
        var emptyNameIndices = new List<int>();
        var nullIndices = new Queue<int>();

        for (int i = 0; i < list.Count; i++)
        {
            var asset = list[i];
            if (asset == null)
            {
                nullIndices.Enqueue(i);
                continue;
            }
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
            if (name == "(null)")
            {
                if (nullIndices.Count > 0)
                {
                    int idx = nullIndices.Dequeue();
                    if (!usedIndices.Contains(idx)) { newOrder.Add(default!); usedIndices.Add(idx); }
                }
                else if (typeName.Equals("Sprites", StringComparison.OrdinalIgnoreCase))
                    newOrder.Add(default!);
                continue;
            }
            if (int.TryParse(name, out _))
            {
                if (emptyNameIdx < emptyNameIndices.Count)
                {
                    int idx = emptyNameIndices[emptyNameIdx];
                    if (!usedIndices.Contains(idx)) { newOrder.Add(list[idx]); usedIndices.Add(idx); }
                    emptyNameIdx++;
                }
                else
                    newOrder.Add(default!);
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

        int preservedCount = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (!usedIndices.Contains(i) && list[i] != null)
            {
                newOrder.Add(list[i]);
                usedIndices.Add(i);
                preservedCount++;
            }
        }
        if (preservedCount > 0)
            Log($"[ImportAssetOrder] {typeName}: Preserved {preservedCount} resource(s) not in TARGET order");

        list.Clear();
        foreach (var asset in newOrder) list.Add(asset);
        Log($"[ImportAssetOrder] {typeName}: Reorganized {list.Count} items (missing: {missingCount}, preserved: {preservedCount})");
    }
}
