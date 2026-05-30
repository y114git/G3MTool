using UndertaleModLib;
using UndertaleModLib.Models;
using System.Text.RegularExpressions;

namespace G3MToolCLI.Services;

public sealed class DataIntegrityResult
{
    public List<string> Errors { get; } = [];
    public List<string> Repairs { get; } = [];
    public bool Success => Errors.Count == 0;
}

public static partial class DataIntegrityService
{
    public static DataIntegrityResult RepairAndValidate(UndertaleData data)
    {
        var result = new DataIntegrityResult();
        RepairScripts(data, result);
        RepairCodeParentLinks(data, result);
        RepairRoomCodeReferences(data, result);
        RepairLocalChildFunctionReferences(data, result);
        RepairFunctionReferences(data, result);
        Validate(data, result);
        if (result.Repairs.Count > 0)
            LogService.Log($"[Integrity] Repairs: {string.Join("; ", result.Repairs.Take(12))}{(result.Repairs.Count > 12 ? " ..." : "")}");
        if (result.Errors.Count > 0)
            LogService.Warning($"[Integrity] Errors: {string.Join("; ", result.Errors.Take(12))}{(result.Errors.Count > 12 ? " ..." : "")}");
        return result;
    }

    public static DataIntegrityResult ValidateOnly(UndertaleData data)
    {
        var result = new DataIntegrityResult();
        Validate(data, result);
        if (result.Errors.Count > 0)
            LogService.Warning($"[Integrity] Errors: {string.Join("; ", result.Errors.Take(12))}{(result.Errors.Count > 12 ? " ..." : "")}");
        return result;
    }

    private static void RepairFunctionReferences(UndertaleData data, DataIntegrityResult result)
    {
        if (data.Functions == null)
            return;

        var functionSet = new HashSet<UndertaleFunction>(data.Functions);
        int relinked = 0;
        int restored = 0;
        var referencedFunctions = new HashSet<UndertaleFunction>();

        foreach (var code in data.Code ?? [])
        {
            foreach (var instruction in code?.Instructions ?? [])
            {
                var function = instruction.ValueFunction;
                if (function == null)
                    continue;

                var name = function.Name?.Content;
                if (string.IsNullOrEmpty(name))
                    continue;

                var canonicalName = CanonicalizeFunctionName(name);
                UndertaleFunction? replacement = null;
                if (functionSet.Contains(function) && string.Equals(name, canonicalName, StringComparison.Ordinal))
                {
                    replacement = function;
                }
                else
                {
                    replacement = data.Functions.ByName(canonicalName);
                    if (replacement == null)
                    {
                        replacement = data.Functions.Define(canonicalName, data.Strings);
                        functionSet.Add(replacement);
                        restored++;
                    }

                    instruction.ValueFunction = replacement;
                    relinked++;
                }

                referencedFunctions.Add(replacement);
            }
        }

        int removedNestedGlobals = 0;
        for (int i = data.Functions.Count - 1; i >= 0; i--)
        {
            var function = data.Functions[i];
            var name = function?.Name?.Content;
            if (function != null &&
                !referencedFunctions.Contains(function) &&
                !string.IsNullOrEmpty(name) &&
                !string.Equals(name, CanonicalizeFunctionName(name), StringComparison.Ordinal))
            {
                data.Functions.RemoveAt(i);
                removedNestedGlobals++;
            }
        }

        if (restored > 0)
            result.Repairs.Add($"restored {restored} missing Function entries referenced by CodeEntries");
        if (relinked > 0)
            result.Repairs.Add($"relinked {relinked} CodeEntry instruction Function references");
        if (removedNestedGlobals > 0)
            result.Repairs.Add($"removed {removedNestedGlobals} invalid nested GlobalScript Function entries");
    }

    private static void RepairLocalChildFunctionReferences(UndertaleData data, DataIntegrityResult result)
    {
        if (data.Functions == null || data.Code == null)
            return;

        var codeByName = BuildCodeByName(data);
        var functionByName = new Dictionary<string, UndertaleFunction>(StringComparer.Ordinal);
        foreach (var function in data.Functions)
        {
            var name = function?.Name?.Content;
            if (!string.IsNullOrEmpty(name) && function != null && !functionByName.ContainsKey(name))
                functionByName[name] = function;
        }

        var localChildMaps = new Dictionary<UndertaleCode, Dictionary<string, UndertaleFunction?>>();
        Dictionary<string, UndertaleFunction?> GetLocalChildMap(UndertaleCode parent)
        {
            if (localChildMaps.TryGetValue(parent, out var cached))
                return cached;

            var map = new Dictionary<string, UndertaleFunction?>(StringComparer.Ordinal);
            foreach (var child in parent.ChildEntries ?? [])
            {
                var childName = child?.Name?.Content;
                if (string.IsNullOrEmpty(childName) || !IsLocalStructFunctionName(childName))
                    continue;

                string signature = NormalizeLocalStructFunctionName(childName);
                if (!functionByName.TryGetValue(childName, out var childFunction))
                    continue;

                if (map.ContainsKey(signature))
                    map[signature] = null; // Ambiguous within this parent, leave refs untouched.
                else
                    map[signature] = childFunction;
            }

            localChildMaps[parent] = map;
            return map;
        }

        int repaired = 0;
        foreach (var code in data.Code)
        {
            if (code == null)
                continue;

            var owner = code.ParentEntry ?? code;
            if (owner.ChildEntries == null || owner.ChildEntries.Count == 0)
                continue;

            var localMap = GetLocalChildMap(owner);
            if (localMap.Count == 0)
                continue;

            foreach (var instruction in code.Instructions ?? [])
            {
                var currentFunction = instruction.ValueFunction;
                var currentName = currentFunction?.Name?.Content;
                if (string.IsNullOrEmpty(currentName) || !IsLocalStructFunctionName(currentName))
                    continue;

                if (!codeByName.TryGetValue(currentName, out var referencedCode))
                    continue;

                if (ReferenceEquals(referencedCode.ParentEntry, owner))
                    continue;

                string signature = NormalizeLocalStructFunctionName(currentName);
                if (!localMap.TryGetValue(signature, out var replacement) || replacement == null || ReferenceEquals(replacement, currentFunction))
                    continue;

                instruction.ValueFunction = replacement;
                repaired++;
            }
        }

        if (repaired > 0)
            result.Repairs.Add($"relinked {repaired} local child Function references to their owning CodeEntry");
    }

    private static bool IsLocalStructFunctionName(string functionName) =>
        functionName.Contains("____struct___", StringComparison.Ordinal);

    private static string NormalizeLocalStructFunctionName(string functionName) =>
        LocalStructOrdinalRegex().Replace(functionName, "____struct___#");

    [GeneratedRegex("____struct___\\d+")]
    private static partial Regex LocalStructOrdinalRegex();

    private static string CanonicalizeFunctionName(string functionName)
    {
        const string nestedGlobalScriptPrefix = "gml_Script_gml_GlobalScript_";
        if (functionName.StartsWith(nestedGlobalScriptPrefix, StringComparison.Ordinal))
            return "gml_Script_" + functionName[nestedGlobalScriptPrefix.Length..];
        return functionName;
    }

    private static Dictionary<string, UndertaleCode> BuildCodeByName(UndertaleData data)
    {
        var byName = new Dictionary<string, UndertaleCode>(StringComparer.Ordinal);
        foreach (var code in data.Code ?? [])
        {
            var name = code?.Name?.Content;
            if (!string.IsNullOrEmpty(name) && code != null)
                byName[name] = code;
        }

        return byName;
    }

    private static void RepairCodeParentLinks(UndertaleData data, DataIntegrityResult result)
    {
        var codeSet = new HashSet<UndertaleCode>(data.Code ?? []);
        int relinkedParents = 0;
        int addedChildren = 0;
        var byName = BuildCodeByName(data);

        foreach (var code in data.Code ?? [])
        {
            if (code == null)
                continue;

            if (code.ParentEntry != null && !codeSet.Contains(code.ParentEntry))
            {
                var parentName = code.ParentEntry.Name?.Content;
                if (!string.IsNullOrEmpty(parentName) &&
                    byName.TryGetValue(parentName, out var liveParent))
                {
                    code.ParentEntry = liveParent;
                    relinkedParents++;
                }
            }

            if (code.ParentEntry != null &&
                codeSet.Contains(code.ParentEntry) &&
                !code.ParentEntry.ChildEntries.Contains(code))
            {
                code.ParentEntry.ChildEntries.Add(code);
                addedChildren++;
            }
        }

        if (relinkedParents > 0)
            result.Repairs.Add($"relinked {relinkedParents} CodeEntry ParentEntry references by name");
        if (addedChildren > 0)
            result.Repairs.Add($"restored {addedChildren} missing CodeEntry parent child links");
    }

    private static UndertaleCode? LiveCode(
        Dictionary<string, UndertaleCode> byName,
        HashSet<UndertaleCode> codeSet,
        UndertaleCode? code)
    {
        if (code == null)
            return null;
        if (codeSet.Contains(code))
            return code;

        var name = code.Name?.Content;
        if (!string.IsNullOrEmpty(name) && byName.TryGetValue(name, out var live))
            return live;

        return code;
    }

    private static void RepairRoomCodeReferences(UndertaleData data, DataIntegrityResult result)
    {
        var codeSet = new HashSet<UndertaleCode>(data.Code ?? []);
        var byName = BuildCodeByName(data);
        int relinked = 0;

        foreach (var room in data.Rooms ?? [])
        {
            if (room == null)
                continue;

            var liveCreation = LiveCode(byName, codeSet, room.CreationCodeId);
            if (!ReferenceEquals(liveCreation, room.CreationCodeId))
            {
                room.CreationCodeId = liveCreation;
                relinked++;
            }

            foreach (var instance in room.GameObjects ?? [])
                relinked += RepairRoomObjectCodeReferences(instance, byName, codeSet);

            foreach (var layer in room.Layers ?? [])
            {
                if (layer?.InstancesData?.Instances == null)
                    continue;
                foreach (var instance in layer.InstancesData.Instances)
                    relinked += RepairRoomObjectCodeReferences(instance, byName, codeSet);
            }
        }

        if (relinked > 0)
            result.Repairs.Add($"relinked {relinked} Room CodeEntry references by name");
    }

    private static int RepairRoomObjectCodeReferences(
        UndertaleRoom.GameObject? instance,
        Dictionary<string, UndertaleCode> byName,
        HashSet<UndertaleCode> codeSet)
    {
        if (instance == null)
            return 0;

        int relinked = 0;
        var liveCreation = LiveCode(byName, codeSet, instance.CreationCode);
        if (!ReferenceEquals(liveCreation, instance.CreationCode))
        {
            instance.CreationCode = liveCreation;
            relinked++;
        }

        var livePreCreate = LiveCode(byName, codeSet, instance.PreCreateCode);
        if (!ReferenceEquals(livePreCreate, instance.PreCreateCode))
        {
            instance.PreCreateCode = livePreCreate;
            relinked++;
        }

        return relinked;
    }

    private static void RepairScripts(UndertaleData data, DataIntegrityResult result)
    {
        int relinked = 0;
        int created = 0;
        var codeSet = new HashSet<UndertaleCode>(data.Code);
        var scriptNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var script in data.Scripts)
        {
            if (script?.Name?.Content == null)
                continue;
            scriptNames.Add(script.Name.Content);
            if (script.Code != null && codeSet.Contains(script.Code))
                continue;

            var replacement = PatchService.ScriptCodeResolver.Resolve(data, script.Name.Content);
            if (replacement == null)
                continue;

            script.Code = replacement;
            relinked++;
        }

        foreach (var code in data.Code)
        {
            var codeName = code?.Name?.Content;
            if (string.IsNullOrEmpty(codeName) ||
                !codeName.StartsWith("gml_Script_", StringComparison.Ordinal) ||
                !scriptNames.Add(codeName))
            {
                continue;
            }

            data.Scripts.Add(new UndertaleScript
            {
                Name = data.Strings.MakeString(codeName),
                Code = code
            });
            created++;
        }

        if (relinked > 0)
            result.Repairs.Add($"relinked {relinked} Script entries to live CodeEntries");
        if (created > 0)
            result.Repairs.Add($"created {created} Script entries for live gml_Script CodeEntries");
    }

    private static void Validate(UndertaleData data, DataIntegrityResult result)
    {
        var codeSet = new HashSet<UndertaleCode>(data.Code ?? []);
        var functionSet = new HashSet<UndertaleFunction>(data.Functions ?? []);
        var objectSet = new HashSet<UndertaleGameObject>(data.GameObjects ?? []);
        var spriteSet = new HashSet<UndertaleSprite>(data.Sprites ?? []);
        var backgroundSet = new HashSet<UndertaleBackground>(data.Backgrounds ?? []);
        var texturePageItemSet = new HashSet<UndertaleTexturePageItem>(data.TexturePageItems ?? []);
        var embeddedTextureSet = new HashSet<UndertaleEmbeddedTexture>(data.EmbeddedTextures ?? []);
        var audioGroupSet = new HashSet<UndertaleAudioGroup>(data.AudioGroups ?? []);
        var sequenceSet = new HashSet<UndertaleSequence>(data.Sequences ?? []);
        var particleSystemSet = new HashSet<UndertaleParticleSystem>(data.ParticleSystems ?? []);
        var particleEmitterSet = new HashSet<UndertaleParticleSystemEmitter>(data.ParticleSystemEmitters ?? []);

        ValidateScriptCodeFunction(data, codeSet, functionSet, result);
        ValidateObjects(data, codeSet, objectSet, spriteSet, result);
        ValidateTextures(data, spriteSet, texturePageItemSet, embeddedTextureSet, result);
        ValidateSounds(data, audioGroupSet, result);
        ValidateRooms(data, codeSet, objectSet, spriteSet, backgroundSet, sequenceSet, particleSystemSet, result);
        ValidateParticles(data, spriteSet, particleEmitterSet, result);
    }

    private static void ValidateScriptCodeFunction(
        UndertaleData data,
        HashSet<UndertaleCode> codeSet,
        HashSet<UndertaleFunction> functionSet,
        DataIntegrityResult result)
    {
        var scriptNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var script in data.Scripts)
        {
            if (script?.Name?.Content != null)
                scriptNames.Add(script.Name.Content);
            if (script?.Code != null && !codeSet.Contains(script.Code))
                result.Errors.Add($"Script '{script.Name?.Content ?? "<null>"}' references missing CodeEntry '{script.Code.Name?.Content ?? "<null>"}'");
        }

        foreach (var code in data.Code)
        {
            var codeName = code?.Name?.Content;
            if (string.IsNullOrEmpty(codeName))
                continue;

            foreach (var child in code!.ChildEntries ?? [])
            {
                if (child == null || !codeSet.Contains(child))
                    result.Errors.Add($"CodeEntry '{codeName}' has child missing from Code list");
                else if (!ReferenceEquals(child.ParentEntry, code))
                    result.Errors.Add($"CodeEntry '{codeName}' child '{child.Name?.Content ?? "<null>"}' has incorrect ParentEntry");
            }

            if (code.ParentEntry != null)
            {
                if (!codeSet.Contains(code.ParentEntry))
                    result.Errors.Add($"CodeEntry '{codeName}' references missing ParentEntry '{code.ParentEntry.Name?.Content ?? "<null>"}'");
                else if (!code.ParentEntry.ChildEntries.Contains(code))
                    result.Errors.Add($"CodeEntry '{codeName}' ParentEntry does not contain this child");
            }

            foreach (var instruction in code!.Instructions ?? [])
            {
                if (instruction.ValueFunction != null && !functionSet.Contains(instruction.ValueFunction))
                    result.Errors.Add($"CodeEntry '{codeName}' references missing Function '{instruction.ValueFunction.Name?.Content ?? "<null>"}'");
            }
        }

        foreach (var global in data.GlobalInitScripts ?? [])
        {
            if (global?.Code != null && !codeSet.Contains(global.Code))
                result.Errors.Add($"GlobalInitScript references missing CodeEntry '{global.Code.Name?.Content ?? "<null>"}'");
        }

    }

    private static void ValidateObjects(
        UndertaleData data,
        HashSet<UndertaleCode> codeSet,
        HashSet<UndertaleGameObject> objectSet,
        HashSet<UndertaleSprite> spriteSet,
        DataIntegrityResult result)
    {
        foreach (var obj in data.GameObjects)
        {
            if (obj == null)
                continue;
            var name = obj.Name?.Content ?? "<unnamed object>";
            if (obj.Sprite != null && !spriteSet.Contains(obj.Sprite))
                result.Errors.Add($"Object '{name}' references missing Sprite '{obj.Sprite.Name?.Content ?? "<null>"}'");
            if (obj.TextureMaskId != null && !spriteSet.Contains(obj.TextureMaskId))
                result.Errors.Add($"Object '{name}' references missing TextureMask Sprite '{obj.TextureMaskId.Name?.Content ?? "<null>"}'");
            if (obj.ParentId != null && !objectSet.Contains(obj.ParentId))
                result.Errors.Add($"Object '{name}' references missing parent Object '{obj.ParentId.Name?.Content ?? "<null>"}'");
            if (HasParentCycle(obj))
                result.Errors.Add($"Object '{name}' has parent cycle");
            if (obj.Events.Count <= (int)EventType.Collision)
                result.Errors.Add($"Object '{name}' has too few event lists: {obj.Events.Count}");

            for (int eventType = 0; eventType < obj.Events.Count; eventType++)
            {
                foreach (var evt in obj.Events[eventType])
                {
                    if (eventType == (int)EventType.Collision &&
                        (evt.EventSubtype >= data.GameObjects.Count ||
                         data.GameObjects[(int)evt.EventSubtype] == null ||
                         !objectSet.Contains(data.GameObjects[(int)evt.EventSubtype])))
                    {
                        result.Errors.Add($"Object '{name}' collision event references missing Object index {evt.EventSubtype}");
                    }

                    foreach (var action in evt.Actions ?? [])
                    {
                        if (action?.CodeId != null && !codeSet.Contains(action.CodeId))
                            result.Errors.Add($"Object '{name}' event {eventType}/{evt.EventSubtype} references missing CodeEntry '{action.CodeId.Name?.Content ?? "<null>"}'");
                    }
                }
            }
        }
    }

    private static bool HasParentCycle(UndertaleGameObject obj)
    {
        var seen = new HashSet<UndertaleGameObject>();
        var current = obj.ParentId;
        while (current != null)
        {
            if (!seen.Add(current))
                return true;
            current = current.ParentId;
        }
        return false;
    }

    private static void ValidateTextures(
        UndertaleData data,
        HashSet<UndertaleSprite> spriteSet,
        HashSet<UndertaleTexturePageItem> texturePageItemSet,
        HashSet<UndertaleEmbeddedTexture> embeddedTextureSet,
        DataIntegrityResult result)
    {
        foreach (var tpi in data.TexturePageItems)
        {
            if (tpi?.TexturePage != null && !embeddedTextureSet.Contains(tpi.TexturePage))
                result.Errors.Add($"TexturePageItem '{tpi.Name?.Content ?? "<unnamed>"}' references missing EmbeddedTexture");
        }

        foreach (var sprite in data.Sprites)
        {
            if (sprite == null)
                continue;
            foreach (var texture in sprite.Textures ?? [])
            {
                if (texture?.Texture != null && !texturePageItemSet.Contains(texture.Texture))
                    result.Errors.Add($"Sprite '{sprite.Name?.Content ?? "<unnamed>"}' references missing TexturePageItem");
            }
        }

        foreach (var background in data.Backgrounds)
        {
            if (background?.Texture != null && !texturePageItemSet.Contains(background.Texture))
                result.Errors.Add($"Background '{background.Name?.Content ?? "<unnamed>"}' references missing TexturePageItem");
            if (background?.GMS2ExportedSprite != null && !spriteSet.Contains(background.GMS2ExportedSprite))
                result.Errors.Add($"Background '{background.Name?.Content ?? "<unnamed>"}' references missing exported Sprite");
        }

        foreach (var font in data.Fonts)
        {
            if (font?.Texture != null && !texturePageItemSet.Contains(font.Texture))
                result.Errors.Add($"Font '{font.Name?.Content ?? "<unnamed>"}' references missing TexturePageItem");
        }
    }

    private static void ValidateSounds(UndertaleData data, HashSet<UndertaleAudioGroup> audioGroupSet, DataIntegrityResult result)
    {
        foreach (var sound in data.Sounds)
        {
            if (sound?.AudioGroup != null && !audioGroupSet.Contains(sound.AudioGroup))
                result.Errors.Add($"Sound '{sound.Name?.Content ?? "<unnamed>"}' references missing AudioGroup '{sound.AudioGroup.Name?.Content ?? "<null>"}'");
            if (sound != null && data.AudioGroups != null && data.AudioGroups.Count > 0 && sound.GroupID >= data.AudioGroups.Count)
                result.Errors.Add($"Sound '{sound.Name?.Content ?? "<unnamed>"}' has out-of-range GroupID {sound.GroupID}");
        }
    }

    private static void ValidateRooms(
        UndertaleData data,
        HashSet<UndertaleCode> codeSet,
        HashSet<UndertaleGameObject> objectSet,
        HashSet<UndertaleSprite> spriteSet,
        HashSet<UndertaleBackground> backgroundSet,
        HashSet<UndertaleSequence> sequenceSet,
        HashSet<UndertaleParticleSystem> particleSystemSet,
        DataIntegrityResult result)
    {
        foreach (var room in data.Rooms)
        {
            if (room == null)
                continue;
            var name = room.Name?.Content ?? "<unnamed room>";
            if (room.CreationCodeId != null && !codeSet.Contains(room.CreationCodeId))
                result.Errors.Add($"Room '{name}' references missing CreationCode '{room.CreationCodeId.Name?.Content ?? "<null>"}'");
            if (data.GeneralInfo?.RoomOrder != null)
            {
                foreach (var roomRef in data.GeneralInfo.RoomOrder)
                {
                    if (roomRef?.Resource != null && !data.Rooms.Contains(roomRef.Resource))
                        result.Errors.Add($"GeneralInfo room order references missing Room '{roomRef.Resource.Name?.Content ?? "<null>"}'");
                }
            }

            foreach (var view in room.Views ?? [])
            {
                if (view?.ObjectId != null && !objectSet.Contains(view.ObjectId))
                    result.Errors.Add($"Room '{name}' view references missing Object '{view.ObjectId.Name?.Content ?? "<null>"}'");
            }

            foreach (var instance in room.GameObjects ?? [])
                ValidateRoomObject(name, instance, codeSet, objectSet, result);
            foreach (var tile in room.Tiles ?? [])
                ValidateTile(name, tile, spriteSet, backgroundSet, result);

            foreach (var layer in room.Layers ?? [])
            {
                if (layer == null)
                    continue;
                if (!ReferenceEquals(layer.ParentRoom, room))
                    layer.ParentRoom = room;
                if (layer.InstancesData?.Instances != null)
                    foreach (var instance in layer.InstancesData.Instances)
                        ValidateRoomObject(name, instance, codeSet, objectSet, result);
                if (layer.TilesData?.Background != null && !backgroundSet.Contains(layer.TilesData.Background))
                    result.Errors.Add($"Room '{name}' tile layer references missing Background '{layer.TilesData.Background.Name?.Content ?? "<null>"}'");
                if (layer.TilesData?.TileData != null &&
                    (layer.TilesData.TileData.Length != layer.TilesData.TilesY ||
                     layer.TilesData.TileData.Any(row => row == null || row.Length != layer.TilesData.TilesX)))
                    result.Errors.Add($"Room '{name}' tile layer has invalid TileData dimensions");
                if (layer.BackgroundData?.Sprite != null && !spriteSet.Contains(layer.BackgroundData.Sprite))
                    result.Errors.Add($"Room '{name}' layer background references missing Sprite '{layer.BackgroundData.Sprite.Name?.Content ?? "<null>"}'");
                if (layer.AssetsData?.Sprites != null)
                    foreach (var spriteInstance in layer.AssetsData.Sprites)
                        if (spriteInstance?.Sprite != null && !spriteSet.Contains(spriteInstance.Sprite))
                            result.Errors.Add($"Room '{name}' asset layer references missing Sprite '{spriteInstance.Sprite.Name?.Content ?? "<null>"}'");
                if (layer.AssetsData?.Sequences != null)
                    foreach (var sequenceInstance in layer.AssetsData.Sequences)
                        if (sequenceInstance?.Sequence != null && !sequenceSet.Contains(sequenceInstance.Sequence))
                            result.Errors.Add($"Room '{name}' asset layer references missing Sequence '{sequenceInstance.Sequence.Name?.Content ?? "<null>"}'");
                if (layer.AssetsData?.ParticleSystems != null)
                    foreach (var particleInstance in layer.AssetsData.ParticleSystems)
                        if (particleInstance?.ParticleSystem != null && !particleSystemSet.Contains(particleInstance.ParticleSystem))
                            result.Errors.Add($"Room '{name}' asset layer references missing ParticleSystem '{particleInstance.ParticleSystem.Name?.Content ?? "<null>"}'");
            }
        }
    }

    private static void ValidateRoomObject(
        string roomName,
        UndertaleRoom.GameObject? instance,
        HashSet<UndertaleCode> codeSet,
        HashSet<UndertaleGameObject> objectSet,
        DataIntegrityResult result)
    {
        if (instance == null)
            return;
        if (instance.ObjectDefinition != null && !objectSet.Contains(instance.ObjectDefinition))
            result.Errors.Add($"Room '{roomName}' instance {instance.InstanceID} references missing Object '{instance.ObjectDefinition.Name?.Content ?? "<null>"}'");
        if (instance.CreationCode != null && !codeSet.Contains(instance.CreationCode))
            result.Errors.Add($"Room '{roomName}' instance {instance.InstanceID} references missing CreationCode '{instance.CreationCode.Name?.Content ?? "<null>"}'");
        if (instance.PreCreateCode != null && !codeSet.Contains(instance.PreCreateCode))
            result.Errors.Add($"Room '{roomName}' instance {instance.InstanceID} references missing PreCreateCode '{instance.PreCreateCode.Name?.Content ?? "<null>"}'");
    }

    private static void ValidateTile(
        string roomName,
        UndertaleRoom.Tile? tile,
        HashSet<UndertaleSprite> spriteSet,
        HashSet<UndertaleBackground> backgroundSet,
        DataIntegrityResult result)
    {
        if (tile == null)
            return;
        if (tile.spriteMode)
        {
            if (tile.SpriteDefinition != null && !spriteSet.Contains(tile.SpriteDefinition))
                result.Errors.Add($"Room '{roomName}' tile {tile.InstanceID} references missing Sprite '{tile.SpriteDefinition.Name?.Content ?? "<null>"}'");
        }
        else if (tile.BackgroundDefinition != null && !backgroundSet.Contains(tile.BackgroundDefinition))
        {
            result.Errors.Add($"Room '{roomName}' tile {tile.InstanceID} references missing Background '{tile.BackgroundDefinition.Name?.Content ?? "<null>"}'");
        }
    }

    private static void ValidateParticles(
        UndertaleData data,
        HashSet<UndertaleSprite> spriteSet,
        HashSet<UndertaleParticleSystemEmitter> particleEmitterSet,
        DataIntegrityResult result)
    {
        foreach (var emitter in data.ParticleSystemEmitters ?? [])
        {
            if (emitter == null)
                continue;
            var name = emitter.Name?.Content ?? "<unnamed emitter>";
            if (emitter.Sprite != null && !spriteSet.Contains(emitter.Sprite))
                result.Errors.Add($"ParticleEmitter '{name}' references missing Sprite '{emitter.Sprite.Name?.Content ?? "<null>"}'");
            if (emitter.SpawnOnDeath != null && !particleEmitterSet.Contains(emitter.SpawnOnDeath))
                result.Errors.Add($"ParticleEmitter '{name}' references missing SpawnOnDeath emitter '{emitter.SpawnOnDeath.Name?.Content ?? "<null>"}'");
            if (emitter.SpawnOnUpdate != null && !particleEmitterSet.Contains(emitter.SpawnOnUpdate))
                result.Errors.Add($"ParticleEmitter '{name}' references missing SpawnOnUpdate emitter '{emitter.SpawnOnUpdate.Name?.Content ?? "<null>"}'");
        }
    }
}
