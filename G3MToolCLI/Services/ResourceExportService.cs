using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Project;
using UndertaleModLib.Util;
using static G3MToolCLI.Utils.ResourceAssetUtil;

namespace G3MToolCLI.Services;

/// <summary>
/// Exports all resource types from an in-memory UndertaleData to disk.
/// </summary>
public static class ResourceExportService
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly char[] s_invalidChars = Path.GetInvalidFileNameChars();
    private const int MaxSafeNameLength = 96;

    internal static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "_";

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(s_invalidChars.Contains(ch) ? '_' : ch);
        var safe = sb.ToString();
        if (safe.Length <= MaxSafeNameLength)
            return safe;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..12].ToLowerInvariant();
        var prefixLength = MaxSafeNameLength - hash.Length - 2;
        return $"{safe[..prefixLength]}__{hash}";
    }

    private static string EnsureDir(string outputDir, string typeName)
    {
        var dir = Path.Combine(outputDir, typeName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ────────────────────────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Export only the changed/new resources for each type (selective export).
    /// Fast selective export - only exports resources that differ from original.
    /// </summary>
    public static void ExportSelectiveExceptCode(UndertaleData data, string outputDir, string dataFilePath,
        Dictionary<string, HashSet<string>> changedNames)
    {
        var totalSw = Stopwatch.StartNew();
        var sw = new Stopwatch();
        void Time(string name, Action action)
        {
            var filter = changedNames.GetValueOrDefault(name);
            if (filter == null || filter.Count == 0) return;
            sw.Restart(); action(); LogService.Log($"[Export Timing] {name}: {sw.Elapsed.TotalSeconds:F1}s ({filter.Count} resources)");
        }

        // Always export these (tiny, no filter needed)
        sw.Restart();
        if (changedNames.ContainsKey("GeneralInfo")) ExportGeneralInfo(data, outputDir);
        if (changedNames.ContainsKey("Options")) ExportOptions(data, outputDir);
        if (changedNames.ContainsKey("GlobalScripts")) ExportGlobalScripts(data, outputDir);
        if (changedNames.ContainsKey("Language")) ExportLanguage(data, outputDir);
        if (changedNames.ContainsKey("FeatureFlags")) ExportFeatureFlags(data, outputDir);
        if (changedNames.ContainsKey("Tags")) ExportTags(data, outputDir);
        if (changedNames.ContainsKey("FilterEffects")) ExportFilterEffects(data, outputDir, changedNames.GetValueOrDefault("FilterEffects"));
        if (changedNames.ContainsKey("EmbeddedAudio")) ExportEmbeddedAudio(data, outputDir, changedNames.GetValueOrDefault("EmbeddedAudio"));
        if (changedNames.ContainsKey("TextureGroupInfo")) ExportTextureGroupInfo(data, outputDir);
        if (changedNames.ContainsKey("EmbeddedTextures")) ExportEmbeddedTextures(data, outputDir, changedNames.GetValueOrDefault("EmbeddedTextures"));
        if (changedNames.ContainsKey("TexturePageItems")) ExportTexturePageItems(data, outputDir);
        if (changedNames.ContainsKey("EmbeddedImages")) ExportEmbeddedImages(data, outputDir, changedNames.GetValueOrDefault("EmbeddedImages"));
        if (changedNames.ContainsKey("Extensions")) ExportExtensions(data, outputDir);
        LogService.Log($"[Export Timing] Small types: {sw.Elapsed.TotalSeconds:F1}s");

        Time("AudioGroups", () => ExportAudioGroups(data, outputDir, changedNames.GetValueOrDefault("AudioGroups")));
        Time("Scripts", () => ExportScripts(data, outputDir, changedNames.GetValueOrDefault("Scripts")));
        Time("Sprites", () => ExportSprites(data, outputDir, changedNames.GetValueOrDefault("Sprites")));
        Time("Backgrounds", () => ExportBackgrounds(data, outputDir, changedNames.GetValueOrDefault("Backgrounds")));
        Time("Fonts", () => ExportFonts(data, outputDir, changedNames.GetValueOrDefault("Fonts")));
        Time("Sounds", () => ExportSounds(data, outputDir, dataFilePath, changedNames.GetValueOrDefault("Sounds")));
        Time("Paths", () => ExportPaths(data, outputDir, changedNames.GetValueOrDefault("Paths")));
        Time("Tilesets", () => ExportTilesets(data, outputDir, changedNames.GetValueOrDefault("Tilesets")));
        Time("Shaders", () => ExportShaders(data, outputDir, changedNames.GetValueOrDefault("Shaders")));
        Time("Timelines", () => ExportTimelines(data, outputDir, changedNames.GetValueOrDefault("Timelines")));
        Time("GameObjects", () => ExportGameObjects(data, outputDir, changedNames.GetValueOrDefault("GameObjects")));
        Time("ParticleSystemEmitters", () => ExportParticleSystemEmitters(data, outputDir, changedNames.GetValueOrDefault("ParticleSystemEmitters")));
        Time("ParticleSystems", () => ExportParticleSystems(data, outputDir, changedNames.GetValueOrDefault("ParticleSystems")));
        Time("Sequences", () => ExportSequences(data, outputDir, changedNames.GetValueOrDefault("Sequences")));
        Time("Rooms", () => ExportRooms(data, outputDir, changedNames.GetValueOrDefault("Rooms")));
        Time("AnimationCurves", () => ExportAnimationCurves(data, outputDir, changedNames.GetValueOrDefault("AnimationCurves")));

        totalSw.Stop();
        LogService.Log($"[Export Timing] TOTAL (selective, excl. code): {totalSw.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>
    /// Export only the specified resource types.
    /// </summary>
    public static void ExportTypes(UndertaleData data, string outputDir, string dataFilePath, IEnumerable<string> resourceTypes)
    {
        foreach (var type in resourceTypes)
            ExportSingle(data, outputDir, dataFilePath, type);
    }

    private static void ExportSingle(UndertaleData data, string outputDir, string dataFilePath, string resourceType)
    {
        switch (resourceType)
        {
            case "GeneralInfo": ExportGeneralInfo(data, outputDir); break;
            case "Options": ExportOptions(data, outputDir); break;
            case "GlobalScripts": ExportGlobalScripts(data, outputDir); break;
            case "Scripts": ExportScripts(data, outputDir); break;
            case "Language": ExportLanguage(data, outputDir); break;
            case "FeatureFlags": ExportFeatureFlags(data, outputDir); break;
            case "Tags": ExportTags(data, outputDir); break;
            case "FilterEffects": ExportFilterEffects(data, outputDir); break;
            case "AudioGroups": ExportAudioGroups(data, outputDir); break;
            case "EmbeddedAudio": ExportEmbeddedAudio(data, outputDir); break;
            case "TextureGroupInfo": ExportTextureGroupInfo(data, outputDir); break;
            case "EmbeddedTextures": ExportEmbeddedTextures(data, outputDir); break;
            case "TexturePageItems": ExportTexturePageItems(data, outputDir); break;
            case "EmbeddedImages": ExportEmbeddedImages(data, outputDir); break;
            case "Sprites": ExportSprites(data, outputDir); break;
            case "Backgrounds": ExportBackgrounds(data, outputDir); break;
            case "Fonts": ExportFonts(data, outputDir); break;
            case "Sounds": ExportSounds(data, outputDir, dataFilePath); break;
            case "Paths": ExportPaths(data, outputDir); break;
            case "Tilesets": ExportTilesets(data, outputDir); break;
            case "Shaders": ExportShaders(data, outputDir); break;
            case "Timelines": ExportTimelines(data, outputDir); break;
            case "GameObjects": ExportGameObjects(data, outputDir); break;
            case "ParticleSystemEmitters": ExportParticleSystemEmitters(data, outputDir); break;
            case "ParticleSystems": ExportParticleSystems(data, outputDir); break;
            case "Sequences": ExportSequences(data, outputDir); break;
            case "Rooms": ExportRooms(data, outputDir); break;
            case "AnimationCurves": ExportAnimationCurves(data, outputDir); break;
            case "CodeEntries": ExportCodeEntries(data, outputDir); break;
            case "Extensions": ExportExtensions(data, outputDir); break;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // GeneralInfo
    // ────────────────────────────────────────────────────────────────

    public static void ExportGeneralInfo(UndertaleData data, string outputDir)
    {
        if (data.GeneralInfo == null) return;
        var dir = EnsureDir(outputDir, "GeneralInfo");
        var jsonPath = Path.Combine(dir, "GeneralInfo.json");

        using var stream = new FileStream(jsonPath, FileMode.Create);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        var gi = data.GeneralInfo;

        w.WriteStartObject();
        w.WriteBoolean("isDebuggerDisabled", gi.IsDebuggerDisabled);
        w.WriteNumber("bytecodeVersion", gi.BytecodeVersion);
        w.WriteNumber("padding", gi.Padding);
        w.WriteString("fileName", gi.FileName?.Content ?? "");
        w.WriteString("config", gi.Config?.Content ?? "");
        w.WriteNumber("lastObj", gi.LastObj);
        w.WriteNumber("lastTile", gi.LastTile);
        w.WriteNumber("gameID", gi.GameID);
        w.WriteString("directPlayGuid", gi.DirectPlayGuid.ToString());
        w.WriteString("name", gi.Name?.Content ?? "");
        w.WriteNumber("major", gi.Major);
        w.WriteNumber("minor", gi.Minor);
        w.WriteNumber("release", gi.Release);
        w.WriteNumber("build", gi.Build);
        w.WriteString("branch", gi.Branch.ToString());
        w.WriteNumber("defaultWindowWidth", gi.DefaultWindowWidth);
        w.WriteNumber("defaultWindowHeight", gi.DefaultWindowHeight);
        w.WriteNumber("infoFlags", (uint)gi.Info);

        w.WritePropertyName("infoFlagsDecoded");
        w.WriteStartObject();
        w.WriteBoolean("fullscreen", (gi.Info & UndertaleGeneralInfo.InfoFlags.Fullscreen) != 0);
        w.WriteBoolean("syncVertex1", (gi.Info & UndertaleGeneralInfo.InfoFlags.SyncVertex1) != 0);
        w.WriteBoolean("syncVertex2", (gi.Info & UndertaleGeneralInfo.InfoFlags.SyncVertex2) != 0);
        w.WriteBoolean("interpolate", (gi.Info & UndertaleGeneralInfo.InfoFlags.Interpolate) != 0);
        w.WriteBoolean("scale", (gi.Info & UndertaleGeneralInfo.InfoFlags.Scale) != 0);
        w.WriteBoolean("showCursor", (gi.Info & UndertaleGeneralInfo.InfoFlags.ShowCursor) != 0);
        w.WriteBoolean("sizeable", (gi.Info & UndertaleGeneralInfo.InfoFlags.Sizeable) != 0);
        w.WriteBoolean("screenKey", (gi.Info & UndertaleGeneralInfo.InfoFlags.ScreenKey) != 0);
        w.WriteBoolean("syncVertex3", (gi.Info & UndertaleGeneralInfo.InfoFlags.SyncVertex3) != 0);
        w.WriteBoolean("studioVersionB1", (gi.Info & UndertaleGeneralInfo.InfoFlags.StudioVersionB1) != 0);
        w.WriteBoolean("studioVersionB2", (gi.Info & UndertaleGeneralInfo.InfoFlags.StudioVersionB2) != 0);
        w.WriteBoolean("studioVersionB3", (gi.Info & UndertaleGeneralInfo.InfoFlags.StudioVersionB3) != 0);
        w.WriteBoolean("studioVersionMask", (gi.Info & UndertaleGeneralInfo.InfoFlags.StudioVersionMask) != 0);
        w.WriteBoolean("steamEnabled", (gi.Info & UndertaleGeneralInfo.InfoFlags.SteamEnabled) != 0);
        w.WriteBoolean("useAppDataSaveLocation", (gi.Info & UndertaleGeneralInfo.InfoFlags.UseAppDataSaveLocation) != 0);
        w.WriteBoolean("borderlessWindow", (gi.Info & UndertaleGeneralInfo.InfoFlags.BorderlessWindow) != 0);
        w.WriteBoolean("javaScriptMode", (gi.Info & UndertaleGeneralInfo.InfoFlags.JavaScriptMode) != 0);
        bool gameRunFromGms2Ide = (gi.Info & UndertaleGeneralInfo.InfoFlags.GameRunFromGMS2IDE) != 0;
        w.WriteBoolean("licenseExclusions", gameRunFromGms2Ide);
        w.WriteBoolean("gameRunFromGMS2IDE", gameRunFromGms2Ide);
        w.WriteEndObject();

        w.WriteNumber("licenseCRC32", gi.LicenseCRC32);
        w.WritePropertyName("licenseMD5");
        w.WriteStartArray();
        if (gi.LicenseMD5 != null)
            foreach (byte b in gi.LicenseMD5) w.WriteNumberValue(b);
        w.WriteEndArray();

        w.WriteNumber("timestamp", gi.Timestamp);
        w.WriteString("displayName", gi.DisplayName?.Content ?? "");
        w.WriteNumber("activeTargets", gi.ActiveTargets);
        w.WriteNumber("functionClassifications", (ulong)gi.FunctionClassifications);
        w.WriteNumber("steamAppID", gi.SteamAppID);

        if (gi.BytecodeVersion >= 14)
            w.WriteNumber("debuggerPort", gi.DebuggerPort);

        w.WritePropertyName("roomOrder");
        w.WriteStartArray();
        foreach (var roomRef in gi.RoomOrder)
        {
            if (roomRef?.Resource?.Name?.Content != null) w.WriteStringValue(roomRef.Resource.Name.Content);
            else w.WriteNullValue();
        }
        w.WriteEndArray();

        if (gi.Major >= 2)
        {
            w.WritePropertyName("gms2RandomUID");
            w.WriteStartArray();
            if (gi.GMS2RandomUID != null) foreach (long uid in gi.GMS2RandomUID) w.WriteNumberValue(uid);
            w.WriteEndArray();

            w.WriteNumber("gms2FPS", gi.GMS2FPS);
            w.WriteBoolean("gms2AllowStatistics", gi.GMS2AllowStatistics);

            w.WritePropertyName("gms2GameGUID");
            w.WriteStartArray();
            if (gi.GMS2GameGUID != null) foreach (byte b in gi.GMS2GameGUID) w.WriteNumberValue(b);
            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    public static void ExportOptions(UndertaleData data, string outputDir)
    {
        if (data.Options == null) return;

        var dir = EnsureDir(outputDir, "Options");
        var options = data.Options;
        var payload = new Dictionary<string, object?>
        {
            ["newFormat"] = options.NewFormat,
            ["shaderExtensionFlag"] = options.ShaderExtensionFlag,
            ["shaderExtensionVersion"] = options.ShaderExtensionVersion,
            ["info"] = (ulong)options.Info,
            ["scale"] = options.Scale,
            ["windowColor"] = options.WindowColor,
            ["colorDepth"] = options.ColorDepth,
            ["resolution"] = options.Resolution,
            ["frequency"] = options.Frequency,
            ["vertexSync"] = options.VertexSync,
            ["priority"] = options.Priority,
            ["loadAlpha"] = options.LoadAlpha,
            ["constants"] = options.Constants?.Select(c => new Dictionary<string, object?>
            {
                ["name"] = c?.Name?.Content ?? "",
                ["value"] = c?.Value?.Content ?? ""
            }).ToArray() ?? []
        };

        File.WriteAllText(Path.Combine(dir, "options.json"), JsonSerializer.Serialize(payload, s_jsonOpts));
    }

    public static void ExportGlobalScripts(UndertaleData data, string outputDir)
    {
        var dir = EnsureDir(outputDir, "GlobalScripts");
        var payload = new Dictionary<string, object?>
        {
            ["globalInitScripts"] = data.GlobalInitScripts?.Select(s => s?.Code?.Name?.Content ?? "").ToArray() ?? [],
            ["gameEndScripts"] = data.GameEndScripts?.Select(s => s?.Code?.Name?.Content ?? "").ToArray() ?? []
        };

        File.WriteAllText(Path.Combine(dir, "global_scripts.json"), JsonSerializer.Serialize(payload, s_jsonOpts));
    }

    public static void ExportScripts(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        if (data.Scripts == null) return;
        var dir = EnsureDir(outputDir, "Scripts");
        var entries = BuildUniqueExportNames(
            data.Scripts
                .Where(script => script?.Name?.Content != null)
                .Select(script => (script!, script!.Name.Content))
                .ToList());

        if (filter != null)
        {
            entries = [.. entries
                .Where(entry =>
                    filter.Contains(entry.ExportName) ||
                    filter.Contains(entry.Item.Name.Content))];
        }

        Parallel.ForEach(entries, entry =>
        {
            var script = entry.Item;
            var name = entry.ExportName;
            var resDir = Path.Combine(dir, name);
            Directory.CreateDirectory(resDir);
            var payload = new Dictionary<string, object?>
            {
                ["name"] = script.Name.Content,
                ["code"] = script.Code?.Name?.Content ?? "",
                ["isConstructor"] = script.IsConstructor
            };
            File.WriteAllText(Path.Combine(resDir, name + ".json"), JsonSerializer.Serialize(payload, s_jsonOpts));
        });
    }

    private static List<(T Item, string ExportName)> BuildUniqueExportNames<T>(List<(T Item, string Name)> items)
    {
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(T Item, string ExportName)>(items.Count);

        foreach (var (item, originalName) in items)
        {
            var safeName = SafeName(originalName);
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "_";

            if (!used.TryGetValue(safeName, out var count))
            {
                used[safeName] = 1;
                result.Add((item, safeName));
                continue;
            }

            string uniqueName;
            do
            {
                count++;
                uniqueName = $"{safeName}__{count}";
            }
            while (used.ContainsKey(uniqueName));

            used[safeName] = count;
            used[uniqueName] = 1;
            result.Add((item, uniqueName));
        }

        return result;
    }

    public static void ExportEmbeddedImages(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        if (data.EmbeddedImages == null) return;
        var dir = EnsureDir(outputDir, "EmbeddedImages");

        Parallel.ForEach(data.EmbeddedImages.ToList(), image =>
        {
            if (image?.Name?.Content == null) return;
            if (filter != null && !filter.Contains(image.Name.Content)) return;

            var name = SafeName(image.Name.Content);
            var resDir = Path.Combine(dir, name);
            Directory.CreateDirectory(resDir);
            var payload = new Dictionary<string, object?>
            {
                ["name"] = image.Name.Content,
                ["texturePageItemIndex"] = image.TextureEntry != null && data.TexturePageItems != null
                    ? data.TexturePageItems.IndexOf(image.TextureEntry)
                    : -1,
                ["texturePageItemName"] = image.TextureEntry?.Name?.Content ?? ""
            };
            File.WriteAllText(Path.Combine(resDir, name + ".json"), JsonSerializer.Serialize(payload, s_jsonOpts));
        });
    }

    public static void ExportFeatureFlags(UndertaleData data, string outputDir)
    {
        if (data.FeatureFlags?.List == null) return;

        var dir = EnsureDir(outputDir, "FeatureFlags");
        var flags = data.FeatureFlags.List.Select(f => f?.Content ?? "").ToArray();
        File.WriteAllText(Path.Combine(dir, "feature_flags.json"),
            JsonSerializer.Serialize(flags, s_jsonOpts));
    }

    public static void ExportLanguage(UndertaleData data, string outputDir)
    {
        if (data.Language == null) return;

        var dir = EnsureDir(outputDir, "Language");
        var payload = new Dictionary<string, object?>
        {
            ["unknown1"] = data.Language.Unknown1,
            ["entryIds"] = data.Language.EntryIDs?.Select(e => e?.Content ?? "").ToArray() ?? [],
            ["languages"] = data.Language.Languages?.Where(l => l != null).Select(language => new Dictionary<string, object?>
            {
                ["name"] = language.Name?.Content ?? "",
                ["region"] = language.Region?.Content ?? "",
                ["entries"] = language.Entries?.Select(e => e?.Content ?? "").ToArray() ?? []
            }).ToArray() ?? []
        };

        File.WriteAllText(Path.Combine(dir, "language.json"),
            JsonSerializer.Serialize(payload, s_jsonOpts));
    }

    public static void ExportTags(UndertaleData data, string outputDir)
    {
        if (data.Tags == null) return;

        var dir = EnsureDir(outputDir, "Tags");
        var assetTags = new List<Dictionary<string, object?>>();
        if (data.Tags.AssetTags != null)
        {
            foreach (var (assetId, tags) in data.Tags.AssetTags.OrderBy(kvp => kvp.Key))
            {
                var decoded = DecodeAssetTagId(data, assetId);
                assetTags.Add(new Dictionary<string, object?>
                {
                    ["assetId"] = assetId,
                    ["assetType"] = decoded.Type,
                    ["assetIndex"] = decoded.Index,
                    ["assetName"] = decoded.Name,
                    ["tags"] = tags?.Select(t => t?.Content ?? "").ToArray() ?? []
                });
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["tags"] = data.Tags.Tags?.Select(t => t?.Content ?? "").ToArray() ?? [],
            ["assetTags"] = assetTags
        };

        File.WriteAllText(Path.Combine(dir, "tags.json"),
            JsonSerializer.Serialize(payload, s_jsonOpts));
    }

    public static void ExportFilterEffects(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        var items = data.FilterEffects?.ToList();
        if (items == null || items.Count == 0) return;
        var dir = EnsureDir(outputDir, "FilterEffects");

        Parallel.ForEach(items, effect =>
        {
            if (effect?.Name?.Content == null) return;
            if (filter != null && !filter.Contains(effect.Name.Content)) return;

            try
            {
                var name = SafeName(effect.Name.Content);
                var effectDir = Path.Combine(dir, name);
                Directory.CreateDirectory(effectDir);
                var payload = new Dictionary<string, object?>
                {
                    ["name"] = effect.Name.Content,
                    ["value"] = effect.Value?.Content ?? ""
                };
                File.WriteAllText(Path.Combine(effectDir, name + ".json"),
                    JsonSerializer.Serialize(payload, s_jsonOpts),
                    Encoding.UTF8);
            }
            catch (Exception ex)
            {
                LogService.Log($"[ExportFilterEffects] Failed to export '{effect.Name.Content}': {ex.Message}");
            }
        });
    }

    private static (string Type, int Index, string Name) DecodeAssetTagId(UndertaleData data, int assetId)
    {
        var type = (ResourceType)(assetId >> 24);
        int rawIndex = assetId & 0xFFFFFF;
        int index = type == ResourceType.Script ? rawIndex - 100000 : rawIndex;
        string name = GetAssetNameByTagType(data, type, index) ?? "";
        return (type.ToString(), index, name);
    }

    // ────────────────────────────────────────────────────────────────
    // AudioGroups
    // ────────────────────────────────────────────────────────────────

    public static void ExportAudioGroups(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        var items = data.AudioGroups?.ToList();
        if (items == null || items.Count == 0) return;
        var dir = EnsureDir(outputDir, "AudioGroups");

        Parallel.ForEach(items, ag =>
        {
            if (ag?.Name?.Content == null) return;
            if (filter != null && !filter.Contains(ag.Name.Content)) return;
            try
            {
                var name = SafeName(ag.Name.Content);
                var resDir = Path.Combine(dir, name);
                Directory.CreateDirectory(resDir);
                using var s = new FileStream(Path.Combine(resDir, name + ".json"), FileMode.Create);
                using var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = true });
                w.WriteStartObject();
                w.WriteString("name", ag.Name.Content);
                if (ag.Path != null) w.WriteString("path", ag.Path.Content ?? "");
                w.WriteEndObject();
            }
            catch { }
        });
    }

    // ────────────────────────────────────────────────────────────────
    // TextureGroupInfo
    // ────────────────────────────────────────────────────────────────

    public static void ExportTextureGroupInfo(UndertaleData data, string outputDir)
    {
        if (data.TextureGroupInfo == null || data.TextureGroupInfo.Count == 0) return;
        var dir = EnsureDir(outputDir, "TextureGroupInfo");

        Parallel.ForEach(data.TextureGroupInfo.ToList(), tg =>
        {
            if (tg?.Name?.Content == null) return;
            try
            {
                var name = SafeName(tg.Name.Content);
                var resDir = Path.Combine(dir, name);
                Directory.CreateDirectory(resDir);
                using var s = new FileStream(Path.Combine(resDir, name + ".json"), FileMode.Create);
                using var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = true });
                w.WriteStartObject();
                w.WriteString("name", tg.Name.Content);

                if (data.IsVersionAtLeast(2022, 9))
                {
                    if (tg.Directory != null) w.WriteString("directory", tg.Directory.Content ?? "");
                    if (tg.Extension != null) w.WriteString("extension", tg.Extension.Content ?? "");
                    w.WriteNumber("loadType", (int)tg.LoadType);
                    w.WriteString("loadTypeDescription", tg.LoadType.ToString());
                }

                w.WriteStartArray("texturePages");
                if (tg.TexturePages != null)
                    foreach (var r in tg.TexturePages)
                        if (r.Resource?.Name?.Content != null) w.WriteStringValue(r.Resource.Name.Content);
                w.WriteEndArray();

                w.WriteStartArray("sprites");
                if (tg.Sprites != null)
                    foreach (var r in tg.Sprites)
                        if (r.Resource?.Name?.Content != null) w.WriteStringValue(r.Resource.Name.Content);
                w.WriteEndArray();

                if (!data.IsNonLTSVersionAtLeast(2023, 1))
                {
                    w.WriteStartArray("spineSprites");
                    if (tg.SpineSprites != null)
                        foreach (var r in tg.SpineSprites)
                            if (r.Resource?.Name?.Content != null) w.WriteStringValue(r.Resource.Name.Content);
                    w.WriteEndArray();
                }

                w.WriteStartArray("fonts");
                if (tg.Fonts != null)
                    foreach (var r in tg.Fonts)
                        if (r.Resource?.Name?.Content != null) w.WriteStringValue(r.Resource.Name.Content);
                w.WriteEndArray();

                w.WriteStartArray("tilesets");
                if (tg.Tilesets != null)
                    foreach (var r in tg.Tilesets)
                        if (r.Resource?.Name?.Content != null) w.WriteStringValue(r.Resource.Name.Content);
                w.WriteEndArray();

                w.WriteEndObject();
            }
            catch { }
        });
    }


    // ────────────────────────────────────────────────────────────────
    // EmbeddedTextures
    // ────────────────────────────────────────────────────────────────

    public static void ExportEmbeddedTextures(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        if (data.EmbeddedTextures == null || data.EmbeddedTextures.Count == 0) return;
        var dir = EnsureDir(outputDir, "EmbeddedTextures");

        Parallel.For(0, data.EmbeddedTextures.Count, i =>
        {
            var texName = $"texture_{i:D4}";
            if (filter != null && !filter.Contains(texName))
                return;

            var tex = data.EmbeddedTextures[i];
            try
            {
                var texDir = Path.Combine(dir, texName);
                Directory.CreateDirectory(texDir);

                if (tex.TextureData?.Image != null)
                {
                    bool wroteExactBinary = false;
                    try
                    {
                        var exactBytes = tex.TextureData.Image.ToSpan(data.IsVersionAtLeast(2022, 5)).ToArray();
                        File.WriteAllBytes(Path.Combine(texDir, texName + ".bin"), exactBytes);
                        wroteExactBinary = true;
                    }
                    catch (Exception ex)
                    {
                        LogService.Warning($"[ExportEmbeddedTextures] Binary export failed for {texName}, keeping PNG payload: {ex.Message}");
                    }

                    if (!wroteExactBinary)
                    {
                        using var img = tex.TextureData.Image.GetMagickImage();
                        img.Strip();
                        var pngBytes = GMImage.FromMagickImage(img).ConvertToPng().GetData();
                        File.WriteAllBytes(Path.Combine(texDir, texName + ".png"), pngBytes);
                    }
                }

                var meta = new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["name"] = tex.Name?.Content ?? "",
                    ["scaled"] = tex.Scaled,
                    ["generatedMips"] = tex.GeneratedMips,
                    ["format"] = tex.TextureData?.Image?.Format.ToString() ?? "Png"
                };
                File.WriteAllText(Path.Combine(texDir, texName + ".json"),
                    JsonSerializer.Serialize(meta, s_jsonOpts), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                LogService.Warning($"[ExportEmbeddedTextures] Failed to export {texName}: {ex.Message}");
            }
        });
    }

    public static void ExportTexturePageItems(UndertaleData data, string outputDir)
    {
        if (data.TexturePageItems == null) return;

        var dir = EnsureDir(outputDir, "TexturePageItems");
        var embeddedTextureIndices = BuildReferenceIndex(data.EmbeddedTextures);
        var texturePageItems = new List<Dictionary<string, object?>>(data.TexturePageItems.Count);

        for (int i = 0; i < data.TexturePageItems.Count; i++)
        {
            var tpi = data.TexturePageItems[i];
            if (tpi == null)
            {
                texturePageItems.Add(new Dictionary<string, object?> { ["index"] = i, ["isNull"] = true });
                continue;
            }

            int texIdx = tpi.TexturePage != null && embeddedTextureIndices.TryGetValue(tpi.TexturePage, out var textureIndex)
                ? textureIndex
                : -1;

            texturePageItems.Add(new Dictionary<string, object?>
            {
                ["index"] = i,
                ["name"] = tpi.Name?.Content ?? "",
                ["texturePageIndex"] = texIdx,
                ["sourceX"] = tpi.SourceX,
                ["sourceY"] = tpi.SourceY,
                ["sourceWidth"] = tpi.SourceWidth,
                ["sourceHeight"] = tpi.SourceHeight,
                ["targetX"] = tpi.TargetX,
                ["targetY"] = tpi.TargetY,
                ["targetWidth"] = tpi.TargetWidth,
                ["targetHeight"] = tpi.TargetHeight,
                ["boundingWidth"] = tpi.BoundingWidth,
                ["boundingHeight"] = tpi.BoundingHeight
            });
        }

        File.WriteAllText(Path.Combine(dir, "texture_page_items.json"),
            JsonSerializer.Serialize(texturePageItems, s_jsonOpts));
    }

    // ────────────────────────────────────────────────────────────────
    // Sprites
    // ────────────────────────────────────────────────────────────────

    public static void ExportSprites(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        if (data.Sprites == null || data.Sprites.Count == 0) return;
        var dir = EnsureDir(outputDir, "Sprites");

        var sprites = data.Sprites
            .Select((sprite, index) => (Sprite: sprite, Index: index))
            .Where(item => item.Sprite?.Name?.Content != null &&
                        (filter == null || filter.Contains(item.Sprite.Name.Content)))
            .ToArray();
        if (sprites.Length == 0) return;

        var texturePageIndexes = new Dictionary<UndertaleEmbeddedTexture, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < (data.EmbeddedTextures?.Count ?? 0); i++)
        {
            var texture = data.EmbeddedTextures![i];
            if (texture != null)
                texturePageIndexes[texture] = i;
        }

        using var worker = new TextureWorker();
        Parallel.ForEach(sprites, item =>
        {
            var sprite = item.Sprite!;
            try
            {
                int idx = item.Index;
                var spriteName = SafeName(sprite.Name.Content);
                var folderName = string.IsNullOrEmpty(spriteName) ? $"__unnamed_sprite__idx{idx}" : spriteName;
                var spriteDir = Path.Combine(dir, folderName);
                Directory.CreateDirectory(spriteDir);

                // Build metadata
                var meta = new Dictionary<string, object>
                {
                    ["index"] = idx,
                    ["name"] = sprite.Name?.Content ?? "",
                    ["width"] = sprite.Width,
                    ["height"] = sprite.Height,
                    ["marginLeft"] = sprite.MarginLeft,
                    ["marginRight"] = sprite.MarginRight,
                    ["marginTop"] = sprite.MarginTop,
                    ["marginBottom"] = sprite.MarginBottom,
                    ["originX"] = sprite.OriginX,
                    ["originY"] = sprite.OriginY,
                    ["transparent"] = sprite.Transparent,
                    ["smooth"] = sprite.Smooth,
                    ["preload"] = sprite.Preload,
                    ["bboxMode"] = sprite.BBoxMode,
                    ["sepMasks"] = (uint)sprite.SepMasks,
                    ["sepMasksDescription"] = sprite.SepMasks.ToString(),
                    ["textureCount"] = sprite.Textures.Count
                };

                var frames = new List<Dictionary<string, object>>();
                for (int i = 0; i < sprite.Textures.Count; i++)
                {
                    if (sprite.Textures[i]?.Texture != null)
                    {
                        var tex = sprite.Textures[i].Texture;
                        int tpi = tex.TexturePage != null && texturePageIndexes.TryGetValue(tex.TexturePage, out int textureIndex)
                            ? textureIndex
                            : -1;
                        frames.Add(new Dictionary<string, object>
                        {
                            ["frameIndex"] = i,
                            ["texturePageIndex"] = tpi,
                            ["sourceX"] = tex.SourceX,
                            ["sourceY"] = tex.SourceY,
                            ["sourceWidth"] = tex.SourceWidth,
                            ["sourceHeight"] = tex.SourceHeight,
                            ["targetX"] = tex.TargetX,
                            ["targetY"] = tex.TargetY,
                            ["targetWidth"] = tex.TargetWidth,
                            ["targetHeight"] = tex.TargetHeight,
                            ["boundingWidth"] = tex.BoundingWidth,
                            ["boundingHeight"] = tex.BoundingHeight
                        });
                    }
                    else
                    {
                        frames.Add(new Dictionary<string, object> { ["frameIndex"] = i, ["isNull"] = true });
                    }
                }
                meta["textureFrames"] = frames;

                if (data.IsGameMaker2())
                {
                    meta["isSpecialType"] = sprite.IsSpecialType;
                    meta["sVersion"] = sprite.SVersion;
                    meta["sSpriteType"] = (uint)sprite.SSpriteType;
                    meta["sSpriteTypeDescription"] = sprite.SSpriteType.ToString();
                    meta["gms2PlaybackSpeed"] = sprite.GMS2PlaybackSpeed;
                    meta["gms2PlaybackSpeedType"] = (uint)sprite.GMS2PlaybackSpeedType;
                    meta["gms2PlaybackSpeedTypeDescription"] = sprite.GMS2PlaybackSpeedType.ToString();
                }

                if (sprite.CollisionMasks?.Count > 0)
                {
                    var masks = new List<Dictionary<string, object>>();
                    foreach (var mask in sprite.CollisionMasks)
                        if (mask?.Data?.Length > 0)
                            masks.Add(new Dictionary<string, object> { ["width"] = mask.Width, ["height"] = mask.Height, ["data"] = Convert.ToBase64String(mask.Data) });
                    meta["collisionMasks"] = masks;
                }

                if (sprite.V3NineSlice != null)
                {
                    var ns = new Dictionary<string, object>
                    {
                        ["left"] = sprite.V3NineSlice.Left,
                        ["top"] = sprite.V3NineSlice.Top,
                        ["right"] = sprite.V3NineSlice.Right,
                        ["bottom"] = sprite.V3NineSlice.Bottom,
                        ["enabled"] = sprite.V3NineSlice.Enabled
                    };
                    if (sprite.V3NineSlice.TileModes != null)
                        ns["tileModes"] = sprite.V3NineSlice.TileModes.Select(t => (int)t).ToArray();
                    meta["nineSlice"] = ns;
                }

                if (sprite.IsSpineSprite)
                {
                    meta["isSpineSprite"] = true;
                    meta["spineVersion"] = sprite.SpineVersion;
                }
                if (sprite.IsYYSWFSprite)
                {
                    meta["isYYSWFSprite"] = true;
                    meta["swfVersion"] = sprite.SWFVersion;
                }

                File.WriteAllText(Path.Combine(spriteDir, folderName + ".json"),
                    JsonSerializer.Serialize(meta, s_jsonOpts), Encoding.UTF8);

                // Export frame PNGs
                for (int i = 0; i < sprite.Textures.Count; i++)
                {
                    var tpi = sprite.Textures[i]?.Texture;
                    if (tpi != null)
                    {
                        try { worker.ExportAsPNG(tpi, Path.Combine(spriteDir, $"{spriteName}_{i}.png")); }
                        catch { }
                    }
                }
            }
            catch { }
        });
    }

    // ────────────────────────────────────────────────────────────────
    // Backgrounds
    // ────────────────────────────────────────────────────────────────

    public static void ExportBackgrounds(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        List<UndertaleBackground> items = data.IsGameMaker2()
            ? [.. data.Backgrounds.Where(bg => bg.GMS2TileWidth == 0 && bg.GMS2TileHeight == 0)]
            : [.. data.Backgrounds];
        if (items.Count == 0) return;
        var dir = EnsureDir(outputDir, "Backgrounds");

        using var worker = new TextureWorker();
        Parallel.ForEach(items, bg =>
        {
            if (bg?.Name?.Content == null) return;
            if (filter != null && !filter.Contains(bg.Name.Content)) return;
            try
            {
                var name = SafeName(bg.Name.Content);
                var resDir = Path.Combine(dir, name);
                Directory.CreateDirectory(resDir);
                if (bg.Texture != null) worker.ExportAsPNG(bg.Texture, Path.Combine(resDir, name + ".png"));

                var meta = new Dictionary<string, object>
                {
                    ["name"] = bg.Name?.Content ?? "",
                    ["transparent"] = bg.Transparent,
                    ["smooth"] = bg.Smooth,
                    ["preload"] = bg.Preload
                };
                if (data.IsGameMaker2()) meta["gms2UnknownAlways2"] = bg.GMS2UnknownAlways2;

                File.WriteAllText(Path.Combine(resDir, name + ".json"),
                    JsonSerializer.Serialize(meta, s_jsonOpts), Encoding.UTF8);
            }
            catch { }
        });
    }

    // ────────────────────────────────────────────────────────────────
    // Fonts
    // ────────────────────────────────────────────────────────────────

    public static void ExportFonts(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        if (data.Fonts == null || data.Fonts.Count == 0) return;
        var dir = EnsureDir(outputDir, "Fonts");

        using var worker = new TextureWorker();
        Parallel.ForEach(data.Fonts.ToList(), font =>
        {
            if (font?.Name?.Content == null) return;
            if (filter != null && !filter.Contains(font.Name.Content)) return;
            try
            {
                var name = SafeName(font.Name.Content);
                var fontDir = Path.Combine(dir, name);
                Directory.CreateDirectory(fontDir);
                if (font.Texture != null) worker.ExportAsPNG(font.Texture, Path.Combine(fontDir, "texture.png"));

                using var s = new FileStream(Path.Combine(fontDir, "font.json"), FileMode.Create);
                using var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = true });
                w.WriteStartObject();
                w.WriteString("name", font.Name?.Content ?? "");
                w.WriteString("displayName", font.DisplayName?.Content ?? "");
                w.WriteNumber("emSize", font.EmSize);
                w.WriteBoolean("bold", font.Bold);
                w.WriteBoolean("italic", font.Italic);
                w.WriteNumber("rangeStart", font.RangeStart);
                w.WriteNumber("rangeEnd", font.RangeEnd);
                w.WriteNumber("charset", font.Charset);
                w.WriteNumber("antiAliasing", font.AntiAliasing);
                w.WriteNumber("scaleX", font.ScaleX);
                w.WriteNumber("scaleY", font.ScaleY);
                w.WriteBoolean("emSizeIsFloat", font.EmSizeIsFloat);
                if (data.GeneralInfo?.BytecodeVersion >= 17) w.WriteNumber("ascenderOffset", font.AscenderOffset);
                if (data.IsVersionAtLeast(2022, 2)) w.WriteNumber("ascender", font.Ascender);
                if (data.IsVersionAtLeast(2023, 2)) w.WriteNumber("sdfSpread", font.SDFSpread);
                if (data.IsVersionAtLeast(2023, 6)) w.WriteNumber("lineHeight", font.LineHeight);

                w.WritePropertyName("glyphs");
                w.WriteStartArray();
                foreach (var g in font.Glyphs)
                {
                    w.WriteStartObject();
                    w.WriteNumber("character", g.Character);
                    w.WriteNumber("sourceX", g.SourceX);
                    w.WriteNumber("sourceY", g.SourceY);
                    w.WriteNumber("sourceWidth", g.SourceWidth);
                    w.WriteNumber("sourceHeight", g.SourceHeight);
                    w.WriteNumber("shift", g.Shift);
                    w.WriteNumber("offset", g.Offset);
                    if (g.Kerning?.Count > 0)
                    {
                        w.WritePropertyName("kerning");
                        w.WriteStartArray();
                        foreach (var k in g.Kerning)
                        {
                            w.WriteStartObject();
                            w.WriteNumber("character", k.Character);
                            w.WriteNumber("shiftModifier", k.ShiftModifier);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            catch { }
        });
    }

    // ────────────────────────────────────────────────────────────────
    // Sounds
    // ────────────────────────────────────────────────────────────────

    public static void ExportSounds(UndertaleData data, string outputDir, string dataFilePath, HashSet<string>? filter = null)
    {
        if (data.Sounds == null || data.Sounds.Count == 0) return;
        var dir = EnsureDir(outputDir, "Sounds");
        var dataDir = Path.GetDirectoryName(dataFilePath) ?? "";

        // Pre-load audio groups
        var audioGroupData = new Dictionary<int, byte[]?>();
        foreach (var sound in data.Sounds)
        {
            if (sound == null) continue;
            int gid = sound.GroupID;
            if (audioGroupData.ContainsKey(gid)) continue;
            if (gid == 0 || data.AudioGroups == null || gid >= data.AudioGroups.Count)
            {
                audioGroupData[gid] = null;
                continue;
            }
            var agName = data.AudioGroups[gid]?.Name?.Content;
            if (agName == null) { audioGroupData[gid] = null; continue; }
            var agPath = ResolveAudioGroupPath(data, dataDir, gid);
            if (!File.Exists(agPath)) { audioGroupData[gid] = null; continue; }
            audioGroupData[gid] = null; // placeholder - loaded per-sound below
        }

        Parallel.ForEach(data.Sounds.ToList(), sound =>
        {
            if (sound?.Name?.Content == null) return;
            if (filter != null && !filter.Contains(sound.Name.Content)) return;
            try
            {
                var name = SafeName(sound.Name.Content);
                var resDir = Path.Combine(dir, name);
                Directory.CreateDirectory(resDir);

                bool isInternalEmbeddedAudio = sound.GroupID <= 0 &&
                    sound.AudioFile != null &&
                    sound.AudioFile.Data != null &&
                    sound.AudioFile.Data.Length > 0 &&
                    sound.AudioID >= 0;

                // Export audio data
                if (!isInternalEmbeddedAudio &&
                    sound.AudioFile != null &&
                    sound.AudioFile.Data != null &&
                    sound.AudioFile.Data.Length > 0)
                {
                    var ext = sound.Type?.Content ?? ".ogg";
                    if (!ext.StartsWith('.')) ext = "." + ext;
                    File.WriteAllBytes(Path.Combine(resDir, name + ext), sound.AudioFile.Data);
                }
                else if (sound.GroupID > 0 && data.AudioGroups != null && sound.GroupID < data.AudioGroups.Count)
                {
                    // Try loading from audiogroup dat
                    var agPath = ResolveAudioGroupPath(data, dataDir, sound.GroupID);
                    if (File.Exists(agPath))
                    {
                        try
                        {
                            using (var agStream = new FileStream(agPath, FileMode.Open, FileAccess.Read))
                            using (var agData = UndertaleIO.Read(agStream))
                            if (agData.EmbeddedAudio != null && sound.AudioID >= 0 && sound.AudioID < agData.EmbeddedAudio.Count)
                            {
                                var audio = agData.EmbeddedAudio[sound.AudioID];
                                if (audio?.Data?.Length > 0)
                                {
                                    var ext = sound.Type?.Content ?? ".ogg";
                                    if (!ext.StartsWith('.')) ext = "." + ext;
                                    File.WriteAllBytes(Path.Combine(resDir, name + ext), audio.Data);
                                }
                            }
                        }
                        catch { }
                    }
                }

                // Export metadata
                using var s = new FileStream(Path.Combine(resDir, name + ".json"), FileMode.Create);
                using var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = true });
                w.WriteStartObject();
                w.WriteString("name", sound.Name.Content);
                w.WriteString("type", sound.Type?.Content ?? "");
                w.WriteString("file", sound.File?.Content ?? "");
                w.WriteNumber("effects", (uint)sound.Effects);
                w.WriteNumber("volume", sound.Volume);
                w.WriteNumber("pitch", sound.Pitch);
                w.WriteBoolean("preload", sound.Preload);
                w.WriteNumber("audioID", sound.AudioID);
                w.WriteNumber("groupID", sound.GroupID);
                w.WriteNumber("flags", (uint)sound.Flags);
                if (data.IsNonLTSVersionAtLeast(2024, 6))
                    w.WriteNumber("audioLength", sound.AudioLength);
                if (sound.AudioGroup?.Name?.Content != null)
                    w.WriteString("audioGroupName", sound.AudioGroup.Name.Content);
                w.WriteEndObject();
            }
            catch { }
        });
    }

    public static void ExportEmbeddedAudio(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        if (data.EmbeddedAudio == null || data.EmbeddedAudio.Count == 0) return;
        var dir = EnsureDir(outputDir, "EmbeddedAudio");

        Parallel.For(0, data.EmbeddedAudio.Count, i =>
        {
            string name = $"audio_{i:D4}";
            if (filter != null && !filter.Contains(name))
                return;

            try
            {
                var audio = data.EmbeddedAudio[i];
                var audioDir = Path.Combine(dir, name);
                Directory.CreateDirectory(audioDir);

                File.WriteAllBytes(Path.Combine(audioDir, name + ".bin"), audio?.Data ?? []);
                var meta = new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["bytes"] = audio?.Data?.Length ?? 0
                };
                File.WriteAllText(Path.Combine(audioDir, name + ".json"),
                    JsonSerializer.Serialize(meta, s_jsonOpts), Encoding.UTF8);
            }
            catch { }
        });
    }

    // ────────────────────────────────────────────────────────────────
    // Paths
    // ────────────────────────────────────────────────────────────────

    public static void ExportPaths(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        var items = data.Paths?.ToList();
        if (items == null || items.Count == 0) return;
        var dir = EnsureDir(outputDir, "Paths");

        Parallel.ForEach(items, path =>
        {
            if (path?.Name?.Content == null) return;
            if (filter != null && !filter.Contains(path.Name.Content)) return;
            try
            {
                var name = SafeName(path.Name.Content);
                var resDir = Path.Combine(dir, name);
                Directory.CreateDirectory(resDir);
                using var s = new FileStream(Path.Combine(resDir, name + ".json"), FileMode.Create);
                using var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = true });
                w.WriteStartObject();
                w.WriteString("name", path.Name.Content);
                w.WriteBoolean("isSmooth", path.IsSmooth);
                w.WriteBoolean("isClosed", path.IsClosed);
                w.WriteNumber("precision", (int)path.Precision);
                w.WriteStartArray("points");
                foreach (var pt in path.Points)
                {
                    w.WriteStartObject();
                    w.WriteNumber("x", pt.X); w.WriteNumber("y", pt.Y); w.WriteNumber("speed", pt.Speed);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            catch { }
        });
    }

    // ────────────────────────────────────────────────────────────────
    // Tilesets
    // ────────────────────────────────────────────────────────────────

    public static void ExportTilesets(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        if (!data.IsGameMaker2()) return;
        var items = data.Backgrounds.Where(bg => bg.GMS2TileWidth > 0 || bg.GMS2TileHeight > 0).ToList();
        if (items.Count == 0) return;
        var dir = EnsureDir(outputDir, "Tilesets");
        bool exportAllTilesets = filter == null || filter.Contains("Tilesets");
        var selectedItems = items
            .Where(ts => ts?.Name?.Content != null && (exportAllTilesets || filter!.Contains(ts.Name.Content)))
            .ToList();
        if (selectedItems.Count == 0) return;

        using var worker = new TextureWorker();
        var noTextureItems = selectedItems.Where(ts => ts!.Texture == null).ToList();
        var groupedByTexturePage = selectedItems
            .Where(ts => ts!.Texture?.TexturePage != null)
            .GroupBy(ts => ts!.Texture!.TexturePage)
            .ToList();

        Parallel.ForEach(groupedByTexturePage, group =>
        {
            try
            {
                var embeddedImage = worker.GetEmbeddedTexture(group.Key);
                foreach (var ts in group)
                {
                    if (ts?.Name?.Content == null || ts.Texture == null)
                        continue;

                    var name = SafeName(ts.Name.Content);
                    using var image = ExportTexturePageItemFromCachedImage(embeddedImage, ts.Texture, name);
                    TextureWorker.SaveImageToFile(image, Path.Combine(dir, name + ".png"));
                    WriteTilesetMetadata(data, dir, ts, name);
                }
            }
            catch { }
        });

        Parallel.ForEach(noTextureItems, ts =>
        {
            if (ts?.Name?.Content == null) return;
            try
            {
                var name = SafeName(ts.Name.Content);
                WriteTilesetMetadata(data, dir, ts, name);
            }
            catch { }
        });
    }

    private static void WriteTilesetMetadata(UndertaleData data, string dir, UndertaleBackground ts, string name)
    {
        var meta = new Dictionary<string, object>
        {
            ["name"] = ts.Name?.Content ?? "",
            ["transparent"] = ts.Transparent,
            ["smooth"] = ts.Smooth,
            ["preload"] = ts.Preload,
            ["gms2UnknownAlways2"] = ts.GMS2UnknownAlways2,
            ["gms2TileWidth"] = ts.GMS2TileWidth,
            ["gms2TileHeight"] = ts.GMS2TileHeight,
            ["gms2OutputBorderX"] = ts.GMS2OutputBorderX,
            ["gms2OutputBorderY"] = ts.GMS2OutputBorderY,
            ["gms2TileColumns"] = ts.GMS2TileColumns,
            ["gms2ItemsPerTileCount"] = ts.GMS2ItemsPerTileCount,
            ["gms2TileCount"] = ts.GMS2TileCount,
            ["gms2ExportedSpriteIndex"] = ts.GMS2ExportedSpriteIndex,
            ["gms2FrameLength"] = ts.GMS2FrameLength
        };
        if (data.IsVersionAtLeast(2024, 14, 1))
        {
            meta["gms2TileSeparationX"] = ts.GMS2TileSeparationX;
            meta["gms2TileSeparationY"] = ts.GMS2TileSeparationY;
        }
        if (ts.GMS2TileIds?.Count > 0)
        {
            var ids = new List<uint>();
            foreach (var tid in ts.GMS2TileIds) ids.Add(tid.ID);
            meta["gms2TileIds"] = ids;
        }

        File.WriteAllText(Path.Combine(dir, name + ".json"),
            JsonSerializer.Serialize(meta, s_jsonOpts), Encoding.UTF8);
    }

    private static IMagickImage<byte> ExportTexturePageItemFromCachedImage(
        MagickImage embeddedImage,
        UndertaleTexturePageItem texPageItem,
        string imageName)
    {
        int exportWidth = texPageItem.BoundingWidth;
        int exportHeight = texPageItem.BoundingHeight;
        if (texPageItem.TargetWidth > exportWidth || texPageItem.TargetHeight > exportHeight)
            throw new InvalidDataException($"{imageName}'s texture is larger than its bounding box!");

        IMagickImage<byte> image;
        lock (embeddedImage)
        {
            image = embeddedImage.CloneArea(texPageItem.SourceX, texPageItem.SourceY, texPageItem.SourceWidth, texPageItem.SourceHeight);
        }

        if (texPageItem.SourceWidth != texPageItem.TargetWidth || texPageItem.SourceHeight != texPageItem.TargetHeight)
        {
            uint resizeWidth = texPageItem.TargetWidth;
            uint resizeHeight = texPageItem.TargetHeight;
            if (image.Width != resizeWidth || image.Height != resizeHeight)
                image.InterpolativeResize(resizeWidth, resizeHeight, PixelInterpolateMethod.Bilinear);
        }

        image.Strip();
        return image;
    }

    // ────────────────────────────────────────────────────────────────
    // Shaders
    // ────────────────────────────────────────────────────────────────

    public static void ExportShaders(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        if (data.Shaders == null || data.Shaders.Count == 0) return;
        var dir = EnsureDir(outputDir, "Shaders");

        Parallel.ForEach(data.Shaders.ToList(), shader =>
        {
            if (shader?.Name?.Content == null) return;
            if (filter != null && !filter.Contains(shader.Name.Content)) return;
            try
            {
                var name = SafeName(shader.Name.Content);
                var shaderDir = Path.Combine(dir, name);
                Directory.CreateDirectory(shaderDir);

                File.WriteAllText(Path.Combine(shaderDir, "Type.txt"), shader.Type.ToString(), Encoding.UTF8);
                if (shader.GLSL_ES_Fragment != null) File.WriteAllText(Path.Combine(shaderDir, "GLSL_ES_Fragment.txt"), shader.GLSL_ES_Fragment.Content ?? "", Encoding.UTF8);
                if (shader.GLSL_ES_Vertex != null) File.WriteAllText(Path.Combine(shaderDir, "GLSL_ES_Vertex.txt"), shader.GLSL_ES_Vertex.Content ?? "", Encoding.UTF8);
                if (shader.GLSL_Fragment != null) File.WriteAllText(Path.Combine(shaderDir, "GLSL_Fragment.txt"), shader.GLSL_Fragment.Content ?? "", Encoding.UTF8);
                if (shader.GLSL_Vertex != null) File.WriteAllText(Path.Combine(shaderDir, "GLSL_Vertex.txt"), shader.GLSL_Vertex.Content ?? "", Encoding.UTF8);
                if (shader.HLSL9_Fragment != null) File.WriteAllText(Path.Combine(shaderDir, "HLSL9_Fragment.txt"), shader.HLSL9_Fragment.Content ?? "", Encoding.UTF8);
                if (shader.HLSL9_Vertex != null) File.WriteAllText(Path.Combine(shaderDir, "HLSL9_Vertex.txt"), shader.HLSL9_Vertex.Content ?? "", Encoding.UTF8);

                if (shader.HLSL11_VertexData?.Data?.Length > 0) File.WriteAllBytes(Path.Combine(shaderDir, "HLSL11_VertexData.bin"), shader.HLSL11_VertexData.Data);
                if (shader.HLSL11_PixelData?.Data?.Length > 0) File.WriteAllBytes(Path.Combine(shaderDir, "HLSL11_PixelData.bin"), shader.HLSL11_PixelData.Data);
                if (shader.PSSL_VertexData?.Data?.Length > 0) File.WriteAllBytes(Path.Combine(shaderDir, "PSSL_VertexData.bin"), shader.PSSL_VertexData.Data);
                if (shader.PSSL_PixelData?.Data?.Length > 0) File.WriteAllBytes(Path.Combine(shaderDir, "PSSL_PixelData.bin"), shader.PSSL_PixelData.Data);
                if (shader.Cg_PSVita_VertexData?.Data?.Length > 0) File.WriteAllBytes(Path.Combine(shaderDir, "Cg_PSVita_VertexData.bin"), shader.Cg_PSVita_VertexData.Data);
                if (shader.Cg_PSVita_PixelData?.Data?.Length > 0) File.WriteAllBytes(Path.Combine(shaderDir, "Cg_PSVita_PixelData.bin"), shader.Cg_PSVita_PixelData.Data);
                if (shader.Cg_PS3_VertexData?.Data?.Length > 0) File.WriteAllBytes(Path.Combine(shaderDir, "Cg_PS3_VertexData.bin"), shader.Cg_PS3_VertexData.Data);
                if (shader.Cg_PS3_PixelData?.Data?.Length > 0) File.WriteAllBytes(Path.Combine(shaderDir, "Cg_PS3_PixelData.bin"), shader.Cg_PS3_PixelData.Data);

                if (shader.VertexShaderAttributes?.Count > 0)
                {
                    var attrs = new StringBuilder();
                    foreach (var attr in shader.VertexShaderAttributes)
                        if (attr?.Name?.Content != null) attrs.AppendLine(attr.Name.Content);
                    File.WriteAllText(Path.Combine(shaderDir, "VertexShaderAttributes.txt"), attrs.ToString(), Encoding.UTF8);
                }

                var meta = new Dictionary<string, object> { ["name"] = shader.Name.Content, ["type"] = shader.Type.ToString() };
                File.WriteAllText(Path.Combine(shaderDir, name + ".json"),
                    JsonSerializer.Serialize(meta, s_jsonOpts), Encoding.UTF8);
            }
            catch { }
        });
    }

    // ────────────────────────────────────────────────────────────────
    // Timelines
    // ────────────────────────────────────────────────────────────────

    public static void ExportTimelines(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        var items = data.Timelines?.ToList();
        if (items == null || items.Count == 0) return;
        var dir = EnsureDir(outputDir, "Timelines");

        Parallel.ForEach(items, tl =>
        {
            if (tl?.Name?.Content == null) return;
            if (filter != null && !filter.Contains(tl.Name.Content)) return;
            try
            {
                var name = SafeName(tl.Name.Content);
                var resDir = Path.Combine(dir, name);
                Directory.CreateDirectory(resDir);
                using var s = new FileStream(Path.Combine(resDir, name + ".json"), FileMode.Create);
                using var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = true });
                w.WriteStartObject();
                w.WriteString("name", tl.Name.Content);
                w.WriteStartArray("moments");
                foreach (var moment in tl.Moments)
                {
                    w.WriteStartObject();
                    w.WriteNumber("step", (int)moment.Step);
                    if (moment.Event?.Count > 0)
                    {
                        w.WriteStartArray("actions");
                        foreach (var action in moment.Event)
                        {
                            w.WriteStartObject();
                            if (action.CodeId?.Name?.Content != null) w.WriteString("codeId", action.CodeId.Name.Content);
                            else w.WriteNull("codeId");
                            w.WriteNumber("libId", action.LibID);
                            w.WriteNumber("id", action.ID);
                            w.WriteNumber("kind", action.Kind);
                            w.WriteBoolean("useRelative", action.UseRelative);
                            w.WriteBoolean("isQuestion", action.IsQuestion);
                            w.WriteBoolean("useApplyTo", action.UseApplyTo);
                            w.WriteNumber("exeType", action.ExeType);
                            w.WriteString("actionName", action.ActionName?.Content ?? "");
                            w.WriteNumber("argumentCount", action.ArgumentCount);
                            w.WriteNumber("who", action.Who);
                            w.WriteBoolean("relative", action.Relative);
                            w.WriteBoolean("isNot", action.IsNot);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            catch { }
        });
    }

    // ────────────────────────────────────────────────────────────────
    // AnimationCurves
    // ────────────────────────────────────────────────────────────────

    public static void ExportAnimationCurves(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        var items = data.AnimationCurves?.ToList();
        if (items == null || items.Count == 0) return;
        var dir = EnsureDir(outputDir, "AnimationCurves");

        Parallel.ForEach(items, curve =>
        {
            if (curve?.Name?.Content == null) return;
            if (filter != null && !filter.Contains(curve.Name.Content)) return;

            try
            {
                var name = SafeName(curve.Name.Content);
                var curveDir = Path.Combine(dir, name);
                Directory.CreateDirectory(curveDir);
                File.WriteAllText(Path.Combine(curveDir, name + ".json"),
                    JsonSerializer.Serialize(ToAnimationCurvePayload(data, curve), s_jsonOpts),
                    Encoding.UTF8);
            }
            catch (Exception ex)
            {
                LogService.Log($"[ExportAnimationCurves] Failed to export '{curve.Name.Content}': {ex.Message}");
            }
        });
    }

    // ────────────────────────────────────────────────────────────────
    // Sequences
    // ────────────────────────────────────────────────────────────────

    public static void ExportSequences(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        var items = data.Sequences?.ToList();
        if (items == null || items.Count == 0) return;
        var dir = EnsureDir(outputDir, "Sequences");
        var context = CreateProjectContext(data, Path.Combine(dir, ".project_context"));

        foreach (var sequence in items)
        {
            if (sequence?.Name?.Content == null) continue;
            if (filter != null && !filter.Contains(sequence.Name.Content)) continue;

            try
            {
                var name = SafeName(sequence.Name.Content);
                var seqDir = Path.Combine(dir, name);
                Directory.CreateDirectory(seqDir);
                SerializableProjectAssetBridge.Export(context, sequence, Path.Combine(seqDir, name + ".json"));
            }
            catch (Exception ex)
            {
                LogService.Log($"[ExportSequences] Failed to export '{sequence.Name.Content}': {ex.Message}");
            }
        }

        try { Directory.Delete(Path.Combine(dir, ".project_context"), recursive: true); } catch { }
    }

    public static void ExportParticleSystems(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        var items = data.ParticleSystems?.ToList();
        if (items == null || items.Count == 0) return;
        var dir = EnsureDir(outputDir, "ParticleSystems");
        Parallel.ForEach(items, ps =>
        {
            if (ps?.Name?.Content == null) return;
            if (filter != null && !filter.Contains(ps.Name.Content)) return;
            var name = SafeName(ps.Name.Content);
            var psDir = Path.Combine(dir, name);
            Directory.CreateDirectory(psDir);
            File.WriteAllText(Path.Combine(psDir, name + ".json"),
                JsonSerializer.Serialize(ToParticleSystemPayload(ps), s_jsonOpts), Encoding.UTF8);
        });
    }

    public static void ExportParticleSystemEmitters(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        var items = data.ParticleSystemEmitters?.ToList();
        if (items == null || items.Count == 0) return;
        var dir = EnsureDir(outputDir, "ParticleSystemEmitters");
        Parallel.ForEach(items, emitter =>
        {
            if (emitter?.Name?.Content == null) return;
            if (filter != null && !filter.Contains(emitter.Name.Content)) return;
            var name = SafeName(emitter.Name.Content);
            var emitterDir = Path.Combine(dir, name);
            Directory.CreateDirectory(emitterDir);
            File.WriteAllText(Path.Combine(emitterDir, name + ".json"),
                JsonSerializer.Serialize(ToParticleEmitterPayload(emitter), s_jsonOpts), Encoding.UTF8);
        });
    }

    private static Dictionary<string, object?> ToParticleSystemPayload(UndertaleParticleSystem ps)
    {
        return new Dictionary<string, object?>
        {
            ["name"] = ps.Name?.Content ?? "",
            ["originX"] = ps.OriginX,
            ["originY"] = ps.OriginY,
            ["drawOrder"] = (int)ps.DrawOrder,
            ["globalSpaceParticles"] = ps.GlobalSpaceParticles,
            ["emitters"] = ps.Emitters?.Select(e => e?.Resource?.Name?.Content ?? "").Where(n => !string.IsNullOrEmpty(n)).ToArray() ?? []
        };
    }

    private static Dictionary<string, object?> ToParticleEmitterPayload(UndertaleParticleSystemEmitter e)
    {
        Dictionary<string, object?> payload = new()
        {
            ["name"] = e.Name?.Content ?? "",
            ["sprite"] = e.Sprite?.Name?.Content ?? "",
            ["spawnOnDeath"] = e.SpawnOnDeath?.Name?.Content ?? "",
            ["spawnOnUpdate"] = e.SpawnOnUpdate?.Name?.Content ?? ""
        };

        foreach (var prop in typeof(UndertaleParticleSystemEmitter).GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (prop.Name is "Name" or "Sprite" or "SpawnOnDeath" or "SpawnOnUpdate" or
                "SizeMin" or "SizeMax" or "SizeIncrease" or "SizeWiggle")
                continue;
            var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (type.IsEnum)
                payload[prop.Name] = Convert.ToInt32(prop.GetValue(e));
            else if (type == typeof(bool) || type == typeof(int) || type == typeof(uint) || type == typeof(float))
                payload[prop.Name] = prop.GetValue(e);
        }

        return payload;
    }

    private static Dictionary<string, object?> ToAnimationCurvePayload(UndertaleData data, UndertaleAnimationCurve curve)
    {
        return new Dictionary<string, object?>
        {
            ["name"] = curve.Name?.Content ?? "",
            ["graphType"] = (uint)curve.GraphType,
            ["channels"] = curve.Channels?.Where(channel => channel != null).Select(channel => new Dictionary<string, object?>
            {
                ["name"] = channel!.Name?.Content ?? "",
                ["curve"] = (uint)channel.Curve,
                ["iterations"] = channel.Iterations,
                ["points"] = channel.Points?.Where(point => point != null).Select(point => ToAnimationCurvePointPayload(data, point!)).ToArray() ?? []
            }).ToArray() ?? []
        };
    }

    private static Dictionary<string, object?> ToAnimationCurvePointPayload(UndertaleData data, UndertaleAnimationCurve.Channel.Point point)
    {
        var payload = new Dictionary<string, object?>
        {
            ["x"] = point.X,
            ["value"] = point.Value
        };

        if (data.IsVersionAtLeast(2, 3, 1))
        {
            payload["bezierX0"] = point.BezierX0;
            payload["bezierY0"] = point.BezierY0;
            payload["bezierX1"] = point.BezierX1;
            payload["bezierY1"] = point.BezierY1;
        }

        return payload;
    }

    // ────────────────────────────────────────────────────────────────
    // GameObjects
    // ────────────────────────────────────────────────────────────────

    public static void ExportGameObjects(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        var items = data.GameObjects?.ToList();
        if (items == null || items.Count == 0) return;
        var dir = EnsureDir(outputDir, "GameObjects");

        Parallel.ForEach(items, obj =>
        {
            if (obj?.Name?.Content == null) return;
            if (filter != null && !filter.Contains(obj.Name.Content)) return;
            try
            {
                var objName = SafeName(obj.Name.Content);
                int objIdx = data.GameObjects?.IndexOf(obj) ?? -1;
                var folderName = $"{objName}__idx{objIdx:D4}";
                var objDir = Path.Combine(dir, folderName);
                Directory.CreateDirectory(objDir);

                using var s = new FileStream(Path.Combine(objDir, "object.json"), FileMode.Create);
                using var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = true });
                w.WriteStartObject();
                w.WriteString("name", obj.Name.Content);
                w.WriteNumber("index", objIdx);
                w.WriteString("sprite", obj.Sprite?.Name?.Content ?? "");
                w.WriteBoolean("visible", obj.Visible);
                w.WriteBoolean("solid", obj.Solid);
                w.WriteNumber("depth", obj.Depth);
                w.WriteBoolean("persistent", obj.Persistent);
                w.WriteString("parent", obj.ParentId?.Name?.Content ?? "");
                w.WriteString("textureMask", obj.TextureMaskId?.Name?.Content ?? "");
                if (data.IsVersionAtLeast(2022, 5))
                    w.WriteBoolean("managed", obj.Managed);
                w.WriteBoolean("usesPhysics", obj.UsesPhysics);
                w.WriteBoolean("isSensor", obj.IsSensor);
                w.WriteNumber("collisionShape", (int)obj.CollisionShape);
                w.WriteNumber("density", obj.Density);
                w.WriteNumber("restitution", obj.Restitution);
                w.WriteNumber("group", obj.Group);
                w.WriteNumber("linearDamping", obj.LinearDamping);
                w.WriteNumber("angularDamping", obj.AngularDamping);
                w.WriteNumber("friction", obj.Friction);
                w.WriteBoolean("awake", obj.Awake);
                w.WriteBoolean("kinematic", obj.Kinematic);

                w.WriteStartArray("events");
                for (int evType = 0; evType < obj.Events.Count; evType++)
                {
                    foreach (var ev in obj.Events[evType])
                    {
                        w.WriteStartObject();
                        w.WriteNumber("eventType", evType);
                        w.WriteNumber("eventSubtype", ev.EventSubtype);
                        if (evType == 4 && data.GameObjects != null && ev.EventSubtype < (uint)data.GameObjects.Count)
                        {
                            var collObj = data.GameObjects[(int)ev.EventSubtype];
                            w.WriteString("collisionObjectName", collObj?.Name?.Content ?? "");
                        }
                        w.WriteStartArray("actions");
                        foreach (var action in ev.Actions)
                        {
                            w.WriteStartObject();
                            w.WriteString("codeId", action.CodeId?.Name?.Content ?? "");
                            w.WriteNumber("libId", action.LibID);
                            w.WriteNumber("id", action.ID);
                            w.WriteNumber("kind", action.Kind);
                            w.WriteBoolean("useRelative", action.UseRelative);
                            w.WriteBoolean("isQuestion", action.IsQuestion);
                            w.WriteBoolean("useApplyTo", action.UseApplyTo);
                            w.WriteNumber("exeType", action.ExeType);
                            w.WriteString("actionName", action.ActionName?.Content ?? "");
                            w.WriteNumber("argumentCount", action.ArgumentCount);
                            w.WriteNumber("who", action.Who);
                            w.WriteBoolean("relative", action.Relative);
                            w.WriteBoolean("isNot", action.IsNot);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                }
                w.WriteEndArray();

                if (obj.PhysicsVertices?.Count > 0)
                {
                    w.WriteStartArray("physicsVertices");
                    foreach (var v in obj.PhysicsVertices)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("x", v.X); w.WriteNumber("y", v.Y);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                w.WriteEndObject();
            }
            catch { }
        });
    }

    // ────────────────────────────────────────────────────────────────
    // Rooms
    // ────────────────────────────────────────────────────────────────

    public static void ExportRooms(UndertaleData data, string outputDir, HashSet<string>? filter = null)
    {
        if (data.Rooms == null || data.Rooms.Count == 0) return;
        var dir = EnsureDir(outputDir, "Rooms");

        Parallel.ForEach(data.Rooms.ToList(), room =>
        {
            if (room?.Name?.Content == null) return;
            if (filter != null && !filter.Contains(room.Name.Content)) return;
            try
            {
                ExportSingleRoom(data, room, dir);
            }
            catch (Exception ex)
            {
                LogService.Log($"[ExportRooms] Failed to export room '{room.Name.Content}': {ex.Message}");
            }
        });
    }

    private static void ExportSingleRoom(UndertaleData data, UndertaleRoom room, string dir)
    {
        var name = SafeName(room.Name.Content);
        var roomDir = Path.Combine(dir, name);
        Directory.CreateDirectory(roomDir);

        using var s = new FileStream(Path.Combine(roomDir, "room.json"), FileMode.Create);
        using var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("name", room.Name.Content);
        w.WriteString("caption", room.Caption?.Content ?? "");
        w.WriteNumber("width", room.Width);
        w.WriteNumber("height", room.Height);
        w.WriteNumber("speed", room.Speed);
        w.WriteBoolean("persistent", room.Persistent);
        w.WriteNumber("backgroundColor", room.BackgroundColor);
        w.WriteBoolean("drawBackgroundColor", room.DrawBackgroundColor);
        w.WriteString("creationCodeId", room.CreationCodeId?.Name?.Content ?? "");
        w.WriteNumber("flags", (int)room.Flags);
        w.WriteBoolean("world", room.World);
        w.WriteNumber("top", room.Top);
        w.WriteNumber("left", room.Left);
        w.WriteNumber("right", room.Right);
        w.WriteNumber("bottom", room.Bottom);
        w.WriteNumber("gravityX", room.GravityX);
        w.WriteNumber("gravityY", room.GravityY);
        w.WriteNumber("metersPerPixel", room.MetersPerPixel);
        w.WriteNumber("gridWidth", (float)room.GridWidth);
        w.WriteNumber("gridHeight", (float)room.GridHeight);
        w.WriteNumber("gridThicknessPx", (float)room.GridThicknessPx);

        // Backgrounds
        w.WriteStartArray("backgrounds");
        foreach (var bg in room.Backgrounds)
        {
            w.WriteStartObject();
            w.WriteBoolean("enabled", bg.Enabled);
            w.WriteBoolean("foreground", bg.Foreground);
            w.WriteString("backgroundDefinition", bg.BackgroundDefinition?.Name?.Content ?? "");
            w.WriteNumber("x", bg.X); w.WriteNumber("y", bg.Y);
            w.WriteBoolean("tiledHorizontally", bg.TiledHorizontally);
            w.WriteBoolean("tiledVertically", bg.TiledVertically);
            w.WriteNumber("speedX", bg.SpeedX); w.WriteNumber("speedY", bg.SpeedY);
            w.WriteBoolean("stretch", bg.Stretch);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // Views
        w.WriteStartArray("views");
        foreach (var v in room.Views)
        {
            w.WriteStartObject();
            w.WriteBoolean("enabled", v.Enabled);
            w.WriteNumber("viewX", v.ViewX); w.WriteNumber("viewY", v.ViewY);
            w.WriteNumber("viewWidth", v.ViewWidth); w.WriteNumber("viewHeight", v.ViewHeight);
            w.WriteNumber("portX", v.PortX); w.WriteNumber("portY", v.PortY);
            w.WriteNumber("portWidth", v.PortWidth); w.WriteNumber("portHeight", v.PortHeight);
            w.WriteNumber("borderX", v.BorderX); w.WriteNumber("borderY", v.BorderY);
            w.WriteNumber("speedX", v.SpeedX); w.WriteNumber("speedY", v.SpeedY);
            w.WriteString("objectId", v.ObjectId?.Name?.Content ?? "");
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // GameObjects
        w.WriteStartArray("gameObjects");
        foreach (var o in room.GameObjects)
        {
            w.WriteStartObject();
            w.WriteNumber("x", o.X); w.WriteNumber("y", o.Y);
            w.WriteString("objectDefinition", o.ObjectDefinition?.Name?.Content ?? "");
            w.WriteNumber("instanceID", o.InstanceID);
            w.WriteString("creationCode", o.CreationCode?.Name?.Content ?? "");
            w.WriteNumber("scaleX", o.ScaleX); w.WriteNumber("scaleY", o.ScaleY);
            w.WriteNumber("color", o.Color); w.WriteNumber("rotation", o.Rotation);
            w.WriteString("preCreateCode", o.PreCreateCode?.Name?.Content ?? "");
            if (data.IsVersionAtLeast(2, 2, 2, 302))
            {
                w.WriteNumber("imageSpeed", o.ImageSpeed);
                w.WriteNumber("imageIndex", o.ImageIndex);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // Tiles
        w.WriteStartArray("tiles");
        foreach (var t in room.Tiles)
        {
            w.WriteStartObject();
            w.WriteNumber("x", t.X); w.WriteNumber("y", t.Y);
            w.WriteBoolean("spriteMode", t.spriteMode);
            if (t.spriteMode) w.WriteString("spriteDefinition", t.SpriteDefinition?.Name?.Content ?? "");
            else w.WriteString("backgroundDefinition", t.BackgroundDefinition?.Name?.Content ?? "");
            w.WriteNumber("sourceX", t.SourceX); w.WriteNumber("sourceY", t.SourceY);
            w.WriteNumber("width", t.Width); w.WriteNumber("height", t.Height);
            w.WriteNumber("tileDepth", t.TileDepth); w.WriteNumber("instanceID", t.InstanceID);
            w.WriteNumber("scaleX", t.ScaleX); w.WriteNumber("scaleY", t.ScaleY);
            w.WriteNumber("color", t.Color);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // Layers (GMS2)
        if (data.IsGameMaker2() && room.Layers?.Count > 0)
        {
            w.WriteStartArray("layers");
            foreach (var layer in room.Layers) WriteRoomLayer(data, w, layer);
            w.WriteEndArray();
        }

        // Sequences
        if (data.IsVersionAtLeast(2, 3) && room.Sequences?.Count > 0)
        {
            w.WriteStartArray("sequences");
            foreach (var seq in room.Sequences) w.WriteStringValue(seq?.Resource?.Name?.Content ?? "");
            w.WriteEndArray();
        }

        // InstanceCreationOrderIDs
        if (data.IsVersionAtLeast(2024, 13) && room.InstanceCreationOrderIDs?.InstanceIDs?.Count > 0)
        {
            w.WriteStartArray("instanceCreationOrderIDs");
            foreach (var id in room.InstanceCreationOrderIDs.InstanceIDs) w.WriteNumberValue(id);
            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private static void WriteRoomLayer(UndertaleData data, Utf8JsonWriter w, UndertaleRoom.Layer layer)
    {
        w.WriteStartObject();
        w.WriteString("layerName", layer.LayerName?.Content ?? "");
        w.WriteNumber("layerId", layer.LayerId);
        w.WriteNumber("layerType", (int)layer.LayerType);
        w.WriteNumber("layerDepth", layer.LayerDepth);
        w.WriteNumber("xOffset", layer.XOffset); w.WriteNumber("yOffset", layer.YOffset);
        w.WriteNumber("hSpeed", layer.HSpeed); w.WriteNumber("vSpeed", layer.VSpeed);
        w.WriteBoolean("isVisible", layer.IsVisible);

        if (data.IsVersionAtLeast(2022, 1))
        {
            w.WriteBoolean("effectEnabled", layer.EffectEnabled);
            w.WriteString("effectType", layer.EffectType?.Content ?? "");
        }

        if (layer.LayerType == UndertaleRoom.LayerType.Instances && layer.InstancesData != null)
        {
            w.WriteStartArray("instanceIds");
            if (layer.InstancesData.Instances != null)
                foreach (var inst in layer.InstancesData.Instances) w.WriteNumberValue(inst.InstanceID);
            w.WriteEndArray();
        }
        else if (layer.LayerType == UndertaleRoom.LayerType.Tiles && layer.TilesData != null)
        {
            w.WriteString("tilesBackground", layer.TilesData.Background?.Name?.Content ?? "");
            w.WriteNumber("tilesX", layer.TilesData.TilesX);
            w.WriteNumber("tilesY", layer.TilesData.TilesY);
            w.WriteStartArray("tileData");
            if (layer.TilesData.TileData != null)
                foreach (var row in layer.TilesData.TileData)
                {
                    w.WriteStartArray();
                    if (row != null) foreach (var val in row) w.WriteNumberValue(val);
                    w.WriteEndArray();
                }
            w.WriteEndArray();
        }
        else if (layer.LayerType == UndertaleRoom.LayerType.Background && layer.BackgroundData != null)
        {
            var bd = layer.BackgroundData;
            w.WriteStartObject("backgroundData");
            w.WriteBoolean("visible", bd.Visible); w.WriteBoolean("foreground", bd.Foreground);
            w.WriteString("sprite", bd.Sprite?.Name?.Content ?? "");
            w.WriteBoolean("tiledHorizontally", bd.TiledHorizontally);
            w.WriteBoolean("tiledVertically", bd.TiledVertically);
            w.WriteBoolean("stretch", bd.Stretch);
            w.WriteNumber("color", bd.Color); w.WriteNumber("firstFrame", bd.FirstFrame);
            w.WriteNumber("animationSpeed", bd.AnimationSpeed);
            w.WriteNumber("animationSpeedType", (int)bd.AnimationSpeedType);
            w.WriteEndObject();
        }
        else if (layer.LayerType == UndertaleRoom.LayerType.Assets && layer.AssetsData != null)
        {
            var ad = layer.AssetsData;
            w.WriteStartObject("assetsData");
            w.WriteStartArray("legacyTiles");
            if (ad.LegacyTiles != null)
                foreach (var t in ad.LegacyTiles)
                {
                    w.WriteStartObject();
                    w.WriteNumber("x", t.X); w.WriteNumber("y", t.Y);
                    w.WriteNumber("sourceX", (int)t.SourceX); w.WriteNumber("sourceY", (int)t.SourceY);
                    w.WriteNumber("width", t.Width); w.WriteNumber("height", t.Height);
                    w.WriteNumber("tileDepth", t.TileDepth); w.WriteNumber("instanceID", t.InstanceID);
                    w.WriteNumber("scaleX", t.ScaleX); w.WriteNumber("scaleY", t.ScaleY);
                    w.WriteNumber("color", t.Color);
                    w.WriteBoolean("spriteMode", t.spriteMode);
                    if (t.spriteMode)
                        w.WriteString("spriteDefinition", t.SpriteDefinition?.Name?.Content ?? "");
                    else
                        w.WriteString("backgroundDefinition", t.BackgroundDefinition?.Name?.Content ?? "");
                    w.WriteString("background", t.BackgroundDefinition?.Name?.Content ?? "");
                    w.WriteEndObject();
                }
            w.WriteEndArray();
            w.WriteStartArray("sprites");
            if (ad.Sprites != null)
                foreach (var spr in ad.Sprites)
                {
                    w.WriteStartObject();
                    w.WriteString("name", spr.Name?.Content ?? "");
                    w.WriteString("sprite", spr.Sprite?.Name?.Content ?? "");
                    w.WriteNumber("x", spr.X); w.WriteNumber("y", spr.Y);
                    w.WriteNumber("scaleX", spr.ScaleX); w.WriteNumber("scaleY", spr.ScaleY);
                    w.WriteNumber("color", spr.Color);
                    w.WriteNumber("animationSpeed", spr.AnimationSpeed);
                    w.WriteNumber("animationSpeedType", (int)spr.AnimationSpeedType);
                    w.WriteNumber("frameIndex", spr.FrameIndex);
                    w.WriteNumber("rotation", spr.Rotation);
                    w.WriteEndObject();
                }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    // ────────────────────────────────────────────────────────────────
    // CodeEntries
    // ────────────────────────────────────────────────────────────────

    public static void ExportCodeEntries(UndertaleData data, string outputDir, int maxDegreeOfParallelism = -1)
    {
        if (data.Code == null || data.Code.Count == 0) return;
        ExportCodeEntriesInternal(data, outputDir, null, maxDegreeOfParallelism);
    }

    /// <summary>
    /// Export only the specified code entries (by name). Used for selective patch create.
    /// </summary>
    public static void ExportCodeEntriesSelective(UndertaleData data, string outputDir, HashSet<string> entryNames)
    {
        if (data.Code == null || data.Code.Count == 0 || entryNames.Count == 0) return;
        ExportCodeEntriesInternal(data, outputDir, entryNames);
    }

    /// <summary>
    /// Export code entries to memory (no disk I/O). Returns dictionary: codeName → (gml, asm, childAsms).
    /// Used for direct patch writing in patch create.
    /// </summary>
    public static Dictionary<string, (string LogicalName, string? gml, string? asm, Dictionary<string, (string LogicalName, string Asm)>? childAsms)>
        ExportCodeEntriesToMemory(UndertaleData data, HashSet<string> entryNames)
    {
        var result = new System.Collections.Concurrent.ConcurrentDictionary<string, (string LogicalName, string? gml, string? asm, Dictionary<string, (string LogicalName, string Asm)>? childAsms)>();
        if (data.Code == null || data.Code.Count == 0 || entryNames.Count == 0)
            return new(result);

        var entryDescriptors = CodeEntryArchiveIdentity.DescribeEntries(data, entryNames);
        var topLevel = data.Code
            .Where(c => c?.ParentEntry == null && c?.Name?.Content != null && entryNames.Contains(c.Name.Content))
            .ToList();
        var topLevelDescriptors = entryDescriptors.Where(x => x.ParentArchiveKey == null).ToList();
        var (childDescriptorsByParent, duplicateChildDescriptorParents) = BuildChildDescriptorsByParent(entryDescriptors);

        var context = new GlobalDecompileContext(data);
        var decompilerSettings = data.ToolInfo.DecompilerSettings;

        Parallel.ForEach(Enumerable.Range(0, Math.Min(topLevel.Count, topLevelDescriptors.Count)), index =>
        {
            var entry = topLevel[index];
            var descriptor = topLevelDescriptors[index];
            if (entry?.Name?.Content == null) return;
            try
            {
                string? asm = null, gml = null;
                Dictionary<string, (string LogicalName, string Asm)>? childAsms = null;

                try { gml = new Underanalyzer.Decompiler.DecompileContext(context, entry, decompilerSettings).DecompileToString(); } catch { }
                try { asm = entry.Disassemble(data.Variables, data.CodeLocals?.For(entry)); } catch { }

                if (entry.ChildEntries.Count > 0 && !duplicateChildDescriptorParents.Contains(descriptor.ArchiveKey))
                {
                    childAsms = [];
                    childDescriptorsByParent.TryGetValue(descriptor.ArchiveKey, out var childDescriptors);
                    foreach (var child in entry.ChildEntries)
                    {
                        if (child?.Name?.Content == null) continue;
                        try
                        {
                            var childAsm = child.Disassemble(data.Variables, data.CodeLocals?.For(child));
                            if (childDescriptors != null && childDescriptors.TryGetValue(child.Name.Content, out var childDescriptor))
                                childAsms[childDescriptor.ArchiveKey[(descriptor.ArchiveKey.Length + 1)..]] = (child.Name.Content, childAsm);
                        }
                        catch { }
                    }
                }

                result[descriptor.ArchiveKey] = (entry.Name.Content, gml, asm, childAsms);
            }
            catch { }
        });

        return new(result);
    }

    private static void ExportCodeEntriesInternal(UndertaleData data, string outputDir, HashSet<string>? filterNames, int maxDegreeOfParallelism = -1)
    {
        var dir = EnsureDir(outputDir, "CodeEntries");

        var entryDescriptors = CodeEntryArchiveIdentity.DescribeEntries(data, filterNames);
        var topLevel = data.Code.Where(c => c.ParentEntry == null).ToList();
        if (filterNames != null)
            topLevel = [.. topLevel.Where(c => c?.Name?.Content != null && filterNames.Contains(c.Name.Content))];
        var topLevelDescriptors = entryDescriptors.Where(x => x.ParentArchiveKey == null).ToList();
        var (childDescriptorsByParent, duplicateChildDescriptorParents) = BuildChildDescriptorsByParent(entryDescriptors);

        var context = new GlobalDecompileContext(data);
        var decompilerSettings = data.ToolInfo.DecompilerSettings;
        var opts = maxDegreeOfParallelism > 0
            ? new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism }
            : new ParallelOptions();

        Parallel.ForEach(Enumerable.Range(0, Math.Min(topLevel.Count, topLevelDescriptors.Count)), opts, index =>
        {
            var entry = topLevel[index];
            var descriptor = topLevelDescriptors[index];
            if (entry?.Name?.Content == null) return;
            try
            {
                var entryDir = Path.Combine(dir, descriptor.ArchiveKey);
                Directory.CreateDirectory(entryDir);

                // Export ASM (byte-perfect)
                try
                {
                    var asm = entry.Disassemble(data.Variables, data.CodeLocals?.For(entry));
                    File.WriteAllText(Path.Combine(entryDir, descriptor.ArchiveKey + ".asm"), asm, Encoding.UTF8);
                }
                catch { }

                // Export GML (readable)
                try
                {
                    var gml = new Underanalyzer.Decompiler.DecompileContext(context, entry, decompilerSettings).DecompileToString();
                    File.WriteAllText(Path.Combine(entryDir, descriptor.ArchiveKey + ".gml"), gml, Encoding.UTF8);
                }
                catch { }

                // Export child entries' ASM
                if (duplicateChildDescriptorParents.Contains(descriptor.ArchiveKey))
                    return;
                childDescriptorsByParent.TryGetValue(descriptor.ArchiveKey, out var childDescriptors);
                foreach (var child in entry.ChildEntries)
                {
                    if (child?.Name?.Content == null) continue;
                    try
                    {
                        var childAsm = child.Disassemble(data.Variables, data.CodeLocals?.For(child));
                        if (childDescriptors != null && childDescriptors.TryGetValue(child.Name.Content, out var childDescriptor))
                        {
                            var childLeafKey = childDescriptor.ArchiveKey[(descriptor.ArchiveKey.Length + 1)..];
                            File.WriteAllText(Path.Combine(entryDir, childLeafKey + ".asm"), childAsm, Encoding.UTF8);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        });
    }

    private static (Dictionary<string, Dictionary<string, CodeArchiveEntryDescriptor>> Descriptors, HashSet<string> DuplicateParents) BuildChildDescriptorsByParent(
        IEnumerable<CodeArchiveEntryDescriptor> descriptors)
    {
        var result = new Dictionary<string, Dictionary<string, CodeArchiveEntryDescriptor>>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            if (descriptor.ParentArchiveKey == null)
                continue;

            if (!result.TryGetValue(descriptor.ParentArchiveKey, out var children))
            {
                children = new Dictionary<string, CodeArchiveEntryDescriptor>(StringComparer.Ordinal);
                result[descriptor.ParentArchiveKey] = children;
            }
            if (!children.TryAdd(descriptor.LogicalName, descriptor))
                duplicates.Add(descriptor.ParentArchiveKey);
        }
        return (result, duplicates);
    }

    // ────────────────────────────────────────────────────────────────
    // AssetOrder + Helpers (asset_order.txt, variables_functions.json, etc.)
    // ────────────────────────────────────────────────────────────────

    public static void ExportAssetOrder(
        UndertaleData data,
        string outputDir,
        bool includeObjectEvents = true,
        bool includeVariablesFunctions = true,
        bool includeTextureHelpers = true,
        ISet<string>? assetOrderSections = null,
        bool includeAssetOrder = true,
        HashSet<string>? codeMetadataEntryNames = null)
    {
        Directory.CreateDirectory(outputDir);

        if (includeAssetOrder)
        {
            using var writer = new StreamWriter(Path.Combine(outputDir, "asset_order.txt"));
            static void WriteNames<T>(StreamWriter w, IList<T>? assets) where T : UndertaleNamedResource
            {
                if (assets == null) return;
                for (int i = 0; i < assets.Count; i++)
                {
                    var asset = assets[i];
                    if (asset is not null)
                    {
                        var name = asset.Name?.Content;
                        w.WriteLine(string.IsNullOrEmpty(name) ? i.ToString() : name);
                    }
                    else w.WriteLine("(null)");
                }
            }

            void WriteSection<T>(string name, IList<T>? assets) where T : UndertaleNamedResource
            {
                if (assetOrderSections != null && !assetOrderSections.Contains(name))
                    return;
                writer.WriteLine($"@@{name}@@");
                WriteNames(writer, assets);
            }

            WriteSection("sounds", data.Sounds);
            WriteSection("sprites", data.Sprites);
            WriteSection("backgrounds", data.Backgrounds);
            WriteSection("paths", data.Paths);
            WriteSection("scripts", data.Scripts);
            WriteSection("fonts", data.Fonts);
            WriteSection("objects", data.GameObjects);
            WriteSection("timelines", data.Timelines);
            WriteSection("rooms", data.Rooms);
            WriteSection("shaders", data.Shaders);
            WriteSection("extensions", data.Extensions);
            WriteSection("audiogroups", data.AudioGroups);
            writer.WriteLine("@@counts@@");
            writer.WriteLine($"EmbeddedTextures={data.EmbeddedTextures.Count}");
            writer.WriteLine($"TexturePageItems={data.TexturePageItems.Count}");
        }

        if (includeVariablesFunctions || codeMetadataEntryNames != null)
        {
            var metadata = new Dictionary<string, object>();
            if (includeVariablesFunctions)
            {
                var varList = new List<Dictionary<string, object>>();
                foreach (var v in data.Variables)
                    varList.Add(new Dictionary<string, object> { ["n"] = v.Name?.Content ?? "", ["t"] = (int)v.InstanceType, ["id"] = v.VarID });

                var funcList = new List<string>();
                foreach (var f in data.Functions)
                    funcList.Add(f.Name?.Content ?? "");

                metadata["variables"] = varList;
                metadata["functions"] = funcList;
            }

            var codeEntryList = new List<Dictionary<string, object>>();
            foreach (var descriptor in CodeEntryArchiveIdentity.DescribeEntries(data, codeMetadataEntryNames))
            {
                var entry = new Dictionary<string, object>
                {
                    ["key"] = descriptor.ArchiveKey,
                    ["name"] = descriptor.LogicalName,
                    ["occurrence"] = descriptor.Occurrence
                };
                if (!string.IsNullOrEmpty(descriptor.ParentArchiveKey))
                    entry["parent"] = descriptor.ParentArchiveKey;
                codeEntryList.Add(entry);
            }

            metadata["codeEntries"] = codeEntryList;
            metadata["completeCodeEntries"] = codeMetadataEntryNames == null;
            string metadataFileName = codeMetadataEntryNames == null
                ? "variables_functions.json"
                : "code_patch_metadata.json";
            File.WriteAllText(Path.Combine(outputDir, metadataFileName), JsonSerializer.Serialize(metadata));
        }

        if (includeObjectEvents)
        {
            var objectNameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var obj in data.GameObjects)
            {
                var name = obj?.Name?.Content;
                if (name != null)
                    objectNameCounts[name] = objectNameCounts.GetValueOrDefault(name) + 1;
            }

            var objectEventsMap = new Dictionary<string, List<Dictionary<string, object>>>();
            for (int objIndex = 0; objIndex < data.GameObjects.Count; objIndex++)
            {
                var obj = data.GameObjects[objIndex];
                if (obj?.Name?.Content == null) continue;
                var events = new List<Dictionary<string, object>>();
                for (int evtType = 0; evtType < obj.Events.Count; evtType++)
                {
                    foreach (var evt in obj.Events[evtType])
                    {
                        var evtData = new Dictionary<string, object>
                        {
                            ["t"] = evtType,
                            ["s"] = evt.EventSubtype,
                            ["c"] = (evt.Actions.Count > 0 && evt.Actions[0].CodeId != null) ? evt.Actions[0].CodeId.Name?.Content ?? "" : ""
                        };
                        if (evtType == 4 && evt.EventSubtype < data.GameObjects.Count) // EventType.Collision = 4
                        {
                            var collObj = data.GameObjects[(int)evt.EventSubtype];
                            evtData["cn"] = collObj?.Name?.Content ?? "";
                            if (collObj?.Name?.Content != null)
                                evtData["co"] = GetGameObjectOccurrence(data, (int)evt.EventSubtype);
                        }

                        var actions = new List<Dictionary<string, object>>();
                        foreach (var action in evt.Actions)
                        {
                            actions.Add(new Dictionary<string, object>
                            {
                                ["libId"] = action.LibID,
                                ["id"] = action.ID,
                                ["kind"] = action.Kind,
                                ["useRelative"] = action.UseRelative,
                                ["isQuestion"] = action.IsQuestion,
                                ["useApplyTo"] = action.UseApplyTo,
                                ["exeType"] = action.ExeType,
                                ["actionName"] = action.ActionName?.Content ?? "",
                                ["codeId"] = action.CodeId?.Name?.Content ?? "",
                                ["argumentCount"] = action.ArgumentCount,
                                ["who"] = action.Who,
                                ["relative"] = action.Relative,
                                ["isNot"] = action.IsNot
                            });
                        }
                        evtData["actions"] = actions;
                        events.Add(evtData);
                    }
                }
                var key = objectNameCounts.GetValueOrDefault(obj.Name.Content) > 1
                    ? $"{obj.Name.Content}__idx{objIndex:D4}"
                    : obj.Name.Content;
                objectEventsMap[key] = events;
            }
            File.WriteAllText(Path.Combine(outputDir, "object_events.json"), JsonSerializer.Serialize(objectEventsMap));
        }

        if (includeTextureHelpers)
        {
            ExportEmbeddedTextures(data, outputDir);

            // texture_page_items.json
            var embeddedTextureIndices = BuildReferenceIndex(data.EmbeddedTextures);
            var texturePageItemIndices = BuildReferenceIndex(data.TexturePageItems);

            var tpiList = new List<int[]>();
            foreach (var tpi in data.TexturePageItems)
            {
                int texIdx = tpi.TexturePage != null && embeddedTextureIndices.TryGetValue(tpi.TexturePage, out var textureIndex)
                    ? textureIndex
                    : -1;
                tpiList.Add([texIdx, tpi.SourceX, tpi.SourceY, tpi.SourceWidth, tpi.SourceHeight,
                    tpi.TargetX, tpi.TargetY, tpi.TargetWidth, tpi.TargetHeight, tpi.BoundingWidth, tpi.BoundingHeight]);
            }
            File.WriteAllText(Path.Combine(outputDir, "texture_page_items.json"), JsonSerializer.Serialize(tpiList));

            // sprite_frame_map.json
            var spriteFrameMap = new Dictionary<string, int[]>();
            foreach (var sprite in data.Sprites)
            {
                if (sprite?.Textures == null || sprite.Textures.Count == 0) continue;
                var key = sprite.Name?.Content ?? data.Sprites.IndexOf(sprite).ToString();
                var indices = new int[sprite.Textures.Count];
                for (int f = 0; f < sprite.Textures.Count; f++)
                {
                    var tpi = sprite.Textures[f]?.Texture;
                    indices[f] = tpi != null && texturePageItemIndices.TryGetValue(tpi, out var tpiIndex)
                        ? tpiIndex
                        : -1;
                }
                spriteFrameMap[key] = indices;
            }
            var bgFrameMap = new Dictionary<string, int>();
            foreach (var bg in data.Backgrounds)
            {
                if (bg?.Texture == null) continue;
                bgFrameMap[bg.Name?.Content ?? data.Backgrounds.IndexOf(bg).ToString()] =
                    texturePageItemIndices.TryGetValue(bg.Texture, out var tpiIndex) ? tpiIndex : -1;
            }
            var fontFrameMap = new Dictionary<string, int>();
            foreach (var font in data.Fonts)
            {
                if (font?.Texture == null) continue;
                fontFrameMap[font.Name?.Content ?? data.Fonts.IndexOf(font).ToString()] =
                    texturePageItemIndices.TryGetValue(font.Texture, out var tpiIndex) ? tpiIndex : -1;
            }
            File.WriteAllText(Path.Combine(outputDir, "sprite_frame_map.json"),
                JsonSerializer.Serialize(new Dictionary<string, object> { ["sprites"] = spriteFrameMap, ["backgrounds"] = bgFrameMap, ["fonts"] = fontFrameMap }));
        }
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

    private static Dictionary<T, int> BuildReferenceIndex<T>(IList<T> items) where T : class
    {
        var result = new Dictionary<T, int>(ReferenceEqualityComparer<T>.Instance);
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] != null)
                result.TryAdd(items[i], i);
        }
        return result;
    }

    private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceEqualityComparer<T> Instance = new();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    // ────────────────────────────────────────────────────────────────
    // Extensions
    // ────────────────────────────────────────────────────────────────

    public static void ExportExtensions(UndertaleData data, string outputDir)
    {
        var items = data.Extensions?.ToList();
        if (items == null || items.Count == 0) return;
        var dir = EnsureDir(outputDir, "Extensions");

        Parallel.ForEach(items, ext =>
        {
            if (ext?.Name?.Content == null) return;
            try
            {
                var name = SafeName(ext.Name.Content);
                var resDir = Path.Combine(dir, name);
                Directory.CreateDirectory(resDir);

                using var s = new FileStream(Path.Combine(resDir, name + ".json"), FileMode.Create);
                using var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = true });
                w.WriteStartObject();
                w.WriteString("name", ext.Name.Content);
                w.WriteString("folderName", ext.FolderName?.Content ?? "");
                if (ext.Version != null) w.WriteString("version", ext.Version.Content ?? "");
                if (ext.ClassName != null) w.WriteString("className", ext.ClassName.Content ?? "");

                if (ext.Files?.Count > 0)
                {
                    w.WriteStartArray("files");
                    foreach (var file in ext.Files)
                    {
                        w.WriteStartObject();
                        w.WriteString("filename", file.Filename?.Content ?? "");
                        w.WriteNumber("kind", (int)file.Kind);
                        if (file.InitScript != null) w.WriteString("initScript", file.InitScript.Content ?? "");
                        if (file.CleanupScript != null) w.WriteString("cleanupScript", file.CleanupScript.Content ?? "");

                        w.WriteStartArray("functions");
                        if (file.Functions != null)
                            foreach (var func in file.Functions)
                            {
                                w.WriteStartObject();
                                w.WriteString("name", func.Name?.Content ?? "");
                                if (func.ExtName != null) w.WriteString("extName", func.ExtName.Content ?? "");
                                w.WriteNumber("id", (int)func.ID);
                                w.WriteNumber("kind", (int)func.Kind);
                                w.WriteNumber("retType", (int)func.RetType);
                                w.WriteStartArray("arguments");
                                if (func.Arguments != null)
                                    foreach (var arg in func.Arguments)
                                    {
                                        w.WriteStartObject();
                                        w.WriteNumber("type", (int)arg.Type);
                                        w.WriteEndObject();
                                    }
                                w.WriteEndArray();
                                w.WriteEndObject();
                            }
                        w.WriteEndArray();

                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                if (ext.Options?.Count > 0)
                {
                    w.WriteStartArray("options");
                    foreach (var opt in ext.Options)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", opt.Name?.Content ?? "");
                        w.WriteString("value", opt.Value?.Content ?? "");
                        w.WriteNumber("kind", (int)opt.Kind);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                w.WriteEndObject();
            }
            catch { }
        });
    }
}
