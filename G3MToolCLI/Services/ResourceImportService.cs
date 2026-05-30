using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using G3MToolCLI.Models;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Project;
using UndertaleModLib.Util;
using static UndertaleModLib.Models.UndertaleSound;

namespace G3MToolCLI.Services;

/// <summary>
/// Native C# resource importers for the patch apply pipeline.
/// Uses PatchFileSystem for in-memory patch access.
/// Falls back to disk File/Directory when PFS is not set.
/// </summary>
public static partial class ResourceImportService
{
    // === PatchFileSystem integration ===
    // Thread-static so concurrent usage is safe
    [ThreadStatic]
    private static PatchFileSystem? _pfs;
    [ThreadStatic]
    private static (int Start, int End)? _progressRange;
    [ThreadStatic]
    private static string? _loadedDataPath;
    [ThreadStatic]
    private static string? _savingDataPath;

    /// <summary>Set in-memory file system for imports. Pass null to revert to disk.</summary>
    public static void SetPatchFileSystem(PatchFileSystem? pfs) => _pfs = pfs;

    public static void SetDataPaths(string? loadedDataPath, string? savingDataPath)
    {
        _loadedDataPath = loadedDataPath;
        _savingDataPath = savingDataPath;
    }

    /// <summary>Get current PatchFileSystem (may be null if using disk).</summary>
    public static PatchFileSystem? GetPatchFileSystem() => _pfs;

    public static void SetProgressRange(int? startPercent = null, int? endPercent = null)
    {
        _progressRange = startPercent.HasValue && endPercent.HasValue
            ? (startPercent.Value, endPercent.Value)
            : null;
    }

    private static void ReportProgress(int current, int total)
    {
        if (_progressRange is { } range)
            LogService.ProgressRange(current, total, range.Start, range.End);
    }

    // File-system wrappers: use PFS when available, disk otherwise
    private static string[] GetDirs(string path) =>
        _pfs != null ? _pfs.GetDirectories(path) : Directory.GetDirectories(path);
    private static string[] GetFilesIn(string path, string pattern = "*") =>
        _pfs != null ? _pfs.GetFiles(path, pattern) : Directory.GetFiles(path, pattern);
    private static bool FExists(string path) =>
        _pfs != null ? _pfs.FileExists(path) : File.Exists(path);
    private static string FReadText(string path) =>
        _pfs != null ? _pfs.ReadAllText(path) : File.ReadAllText(path, Encoding.UTF8);
    private static byte[] FReadBytes(string path) =>
        _pfs != null ? _pfs.ReadAllBytes(path) : File.ReadAllBytes(path);
    private static string[] FReadLines(string path) =>
        _pfs != null ? _pfs.ReadAllLines(path) : File.ReadAllLines(path);
    private static bool DirExists(string path) =>
        _pfs != null ? _pfs.DirectoryExists(path) : Directory.Exists(path);

    private static string SafeName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "_";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        return sb.ToString();
    }

    /// <summary>
    /// Import a resource type using native C# code.
    /// Returns true if the resource type was handled natively.
    /// </summary>
    public static bool Import(string resourceType, UndertaleData data, string inputDir)
    {
        switch (resourceType)
        {
            case "Language": ImportLanguage(data, inputDir); return true;
            case "Options": ImportOptions(data, inputDir); return true;
            case "GlobalScripts": ImportGlobalScripts(data, inputDir); return true;
            case "Scripts": ImportScripts(data, inputDir); return true;
            case "FeatureFlags": ImportFeatureFlags(data, inputDir); return true;
            case "Tags": ImportTags(data, inputDir); return true;
            case "FilterEffects": ImportFilterEffects(data, inputDir); return true;
            case "AudioGroups": ImportAudioGroups(data, inputDir); return true;
            case "EmbeddedAudio": ImportEmbeddedAudio(data, inputDir); return true;
            case "TextureGroupInfo": ImportTextureGroupInfo(data, inputDir); return true;
            case "EmbeddedTextures": ImportEmbeddedTexturesForAssetOrder(data, inputDir); return true;
            case "Sprites": ImportSprites(data, inputDir); return true;
            case "EmbeddedImages": ImportEmbeddedImages(data, inputDir); return true;
            case "Fonts": ImportFonts(data, inputDir); return true;
            case "Sounds": ImportSounds(data, inputDir); return true;
            case "Paths": ImportPaths(data, inputDir); return true;
            case "Shaders": ImportShaders(data, inputDir); return true;
            case "GameObjects": ImportGameObjects(data, inputDir); return true;
            case "Rooms": ImportRooms(data, inputDir); return true;
            case "Sequences": ImportSequences(data, inputDir); return true;
            case "Tilesets": ImportTilesets(data, inputDir); return true;
            case "Backgrounds": ImportBackgrounds(data, inputDir); return true;
            case "Extensions": ImportExtensions(data, inputDir); return true;
            case "Timelines": ImportTimelines(data, inputDir); return true;
            case "AnimationCurves": ImportAnimationCurves(data, inputDir); return true;
            case "ParticleSystemEmitters": ImportParticleSystemEmitters(data, inputDir); return true;
            case "ParticleSystems": ImportParticleSystems(data, inputDir); return true;
            case "GeneralInfo": ImportGeneralInfo(data, inputDir); return true;
            case "TexturePageItems": ImportTexturePageItems(data, inputDir); return true;
            default: return false;
        }
    }

    /// <summary>
    /// Import asset order (reorder assets to match TARGET indices).
    /// </summary>
    public static void ImportAssetOrder(UndertaleData data, string inputDir)
    {
        ImportAssetOrderInternal(data, inputDir);
    }

    private static void Log(string msg) => LogService.Log(msg);

    private static void ImportOptions(UndertaleData data, string inputDir)
    {
        if (data.Options == null) return;

        string jsonPath = Path.Combine(inputDir, "options.json");
        if (!FExists(jsonPath)) return;

        using var jsonDoc = JsonDocument.Parse(FReadText(jsonPath));
        var root = jsonDoc.RootElement;
        var options = data.Options;

        if (root.TryGetProperty("newFormat", out var nf)) options.NewFormat = nf.GetBoolean();
        if (root.TryGetProperty("shaderExtensionFlag", out var sef)) options.ShaderExtensionFlag = sef.GetInt32();
        if (root.TryGetProperty("shaderExtensionVersion", out var sev)) options.ShaderExtensionVersion = sev.GetInt32();
        if (root.TryGetProperty("info", out var info)) options.Info = (UndertaleOptions.OptionsFlags)info.GetUInt64();
        if (root.TryGetProperty("scale", out var scale)) options.Scale = scale.GetInt32();
        if (root.TryGetProperty("windowColor", out var wc)) options.WindowColor = wc.GetUInt32();
        if (root.TryGetProperty("colorDepth", out var cd)) options.ColorDepth = cd.GetUInt32();
        if (root.TryGetProperty("resolution", out var res)) options.Resolution = res.GetUInt32();
        if (root.TryGetProperty("frequency", out var freq)) options.Frequency = freq.GetUInt32();
        if (root.TryGetProperty("vertexSync", out var vs)) options.VertexSync = vs.GetUInt32();
        if (root.TryGetProperty("priority", out var prio)) options.Priority = prio.GetUInt32();
        if (root.TryGetProperty("loadAlpha", out var la)) options.LoadAlpha = la.GetUInt32();

        if (root.TryGetProperty("constants", out var constants) && constants.ValueKind == JsonValueKind.Array)
        {
            options.Constants.Clear();
            foreach (var constantJson in constants.EnumerateArray())
            {
                var name = constantJson.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var value = constantJson.TryGetProperty("value", out var valueEl) ? valueEl.GetString() ?? "" : "";
                options.Constants.Add(new UndertaleOptions.Constant
                {
                    Name = data.Strings.MakeString(name),
                    Value = data.Strings.MakeString(value)
                });
            }
        }
    }

    private static void ImportGlobalScripts(UndertaleData data, string inputDir)
    {
        string jsonPath = Path.Combine(inputDir, "global_scripts.json");
        if (!FExists(jsonPath)) return;

        using var jsonDoc = JsonDocument.Parse(FReadText(jsonPath));
        var root = jsonDoc.RootElement;
        if (data.GlobalInitScripts != null && root.TryGetProperty("globalInitScripts", out var globalInit))
            ImportCodeRefList(data, data.GlobalInitScripts, globalInit);
        if (data.GameEndScripts != null && root.TryGetProperty("gameEndScripts", out var gameEnd))
            ImportCodeRefList(data, data.GameEndScripts, gameEnd);
    }

    private static void ImportCodeRefList(UndertaleData data, IList<UndertaleGlobalInit> target, JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) return;

        target.Clear();
        foreach (var item in array.EnumerateArray())
        {
            string? codeName = item.GetString();
            if (string.IsNullOrWhiteSpace(codeName)) continue;

            var code = data.Code?.ByName(codeName);
            if (code == null)
            {
                Log($"[ImportGlobalScripts] Missing code ref '{codeName}', skipped.");
                continue;
            }

            target.Add(new UndertaleGlobalInit { Code = code });
        }
    }

    private static void ImportScripts(UndertaleData data, string inputDir)
    {
        if (data.Scripts == null) return;

        var scriptByExportFolder = BuildScriptExportFolderMap(data);
        var usedScripts = new HashSet<UndertaleScript>();
        int imported = 0, created = 0;
        foreach (var dir in GetDirs(inputDir))
        {
            string folderName = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(folderName)) continue;

            string jsonPath = Path.Combine(dir, folderName + ".json");
            if (!FExists(jsonPath)) continue;

            using var jsonDoc = JsonDocument.Parse(FReadText(jsonPath));
            var root = jsonDoc.RootElement;
            string name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? folderName : folderName;
            string codeName = root.TryGetProperty("code", out var codeEl) ? codeEl.GetString() ?? "" : "";
            bool isConstructor = root.TryGetProperty("isConstructor", out var ctorEl) && ctorEl.GetBoolean();
            var resolvedCode = PatchService.ScriptCodeResolver.Resolve(data, name, codeName);

            var script = FindUnusedScriptByCode(data, resolvedCode, usedScripts);
            if (script == null)
                scriptByExportFolder.TryGetValue(folderName, out script);
            if (script != null && usedScripts.Contains(script))
                script = null;
            script ??= FindUnusedScriptByName(data, name, usedScripts);
            if (script == null)
            {
                script = new UndertaleScript { Name = data.Strings.MakeString(name) };
                data.Scripts.Add(script);
                scriptByExportFolder[folderName] = script;
                created++;
            }
            usedScripts.Add(script);

            script.IsConstructor = isConstructor;
            script.Code = resolvedCode;
            if (script.Code == null && !string.IsNullOrWhiteSpace(codeName))
                Log($"[ImportScripts] Missing code ref '{codeName}' for script '{name}'.");
            imported++;
        }

        Log($"[ImportScripts] Done. {imported} imported ({created} new).");
    }

    private static UndertaleScript? FindUnusedScriptByCode(
        UndertaleData data,
        UndertaleCode? code,
        HashSet<UndertaleScript> usedScripts)
    {
        if (code == null)
            return null;

        foreach (var script in data.Scripts)
        {
            if (script != null && !usedScripts.Contains(script) && ReferenceEquals(script.Code, code))
                return script;
        }

        return null;
    }

    private static UndertaleScript? FindUnusedScriptByName(
        UndertaleData data,
        string name,
        HashSet<UndertaleScript> usedScripts)
    {
        foreach (var script in data.Scripts)
        {
            if (script?.Name?.Content == name && !usedScripts.Contains(script))
                return script;
        }

        return null;
    }

    private static Dictionary<string, UndertaleScript> BuildScriptExportFolderMap(UndertaleData data)
    {
        var result = new Dictionary<string, UndertaleScript>(StringComparer.OrdinalIgnoreCase);
        if (data.Scripts == null || data.Scripts.Count == 0)
            return result;

        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in data.Scripts)
        {
            var originalName = script?.Name?.Content;
            if (string.IsNullOrWhiteSpace(originalName) || script == null)
                continue;

            var safeName = SafeName(originalName);
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "_";

            string exportName;
            if (!used.TryGetValue(safeName, out var count))
            {
                used[safeName] = 1;
                exportName = safeName;
            }
            else
            {
                do
                {
                    count++;
                    exportName = $"{safeName}__{count}";
                }
                while (used.ContainsKey(exportName));

                used[safeName] = count;
                used[exportName] = 1;
            }

            result[exportName] = script;
        }

        return result;
    }

    private static void ImportEmbeddedImages(UndertaleData data, string inputDir)
    {
        if (data.EmbeddedImages == null) return;

        int imported = 0, created = 0;
        foreach (var dir in GetDirs(inputDir))
        {
            string folderName = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(folderName)) continue;

            string jsonPath = Path.Combine(dir, folderName + ".json");
            if (!FExists(jsonPath)) continue;

            using var jsonDoc = JsonDocument.Parse(FReadText(jsonPath));
            var root = jsonDoc.RootElement;
            string name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? folderName : folderName;
            int tpiIndex = root.TryGetProperty("texturePageItemIndex", out var indexEl) ? indexEl.GetInt32() : -1;
            string tpiName = root.TryGetProperty("texturePageItemName", out var tpiNameEl) ? tpiNameEl.GetString() ?? "" : "";

            var image = data.EmbeddedImages.ByName(name);
            if (image == null)
            {
                image = new UndertaleEmbeddedImage { Name = data.Strings.MakeString(name) };
                data.EmbeddedImages.Add(image);
                created++;
            }

            image.TextureEntry = ResolveTexturePageItem(data, tpiIndex, tpiName);
            imported++;
        }

        Log($"[ImportEmbeddedImages] Done. {imported} imported ({created} new).");
    }

    private static UndertaleTexturePageItem? ResolveTexturePageItem(UndertaleData data, int index, string name)
    {
        if (data.TexturePageItems == null) return null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var byName = data.TexturePageItems.ByName(name);
            if (byName != null)
                return byName;
        }
        if (index >= 0 && index < data.TexturePageItems.Count)
            return data.TexturePageItems[index];
        return null;
    }

    private static void ImportFeatureFlags(UndertaleData data, string inputDir)
    {
        string jsonPath = Path.Combine(inputDir, "feature_flags.json");
        if (!FExists(jsonPath)) return;

        if (data.FeatureFlags?.List == null)
        {
            Log("[ImportFeatureFlags] Skipped: target data has no FEAT chunk.");
            return;
        }

        using var jsonDoc = JsonDocument.Parse(FReadText(jsonPath));
        if (jsonDoc.RootElement.ValueKind != JsonValueKind.Array) return;

        data.FeatureFlags.List.Clear();
        foreach (var flagElm in jsonDoc.RootElement.EnumerateArray())
        {
            var flag = flagElm.GetString();
            if (!string.IsNullOrEmpty(flag))
                data.FeatureFlags.List.Add(data.Strings.MakeString(flag));
        }

        Log($"[ImportFeatureFlags] Done. {data.FeatureFlags.List.Count} flags.");
    }

    private static void ImportLanguage(UndertaleData data, string inputDir)
    {
        string jsonPath = Path.Combine(inputDir, "language.json");
        if (!FExists(jsonPath)) return;

        if (data.Language == null)
        {
            Log("[ImportLanguage] Skipped: target data has no LANG chunk.");
            return;
        }

        using var jsonDoc = JsonDocument.Parse(FReadText(jsonPath));
        var root = jsonDoc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        data.Language.Unknown1 = GetJsonValue(root, "unknown1", data.Language.Unknown1);
        data.Language.EntryIDs = [];
        if (root.TryGetProperty("entryIds", out var entryIdsElm) && entryIdsElm.ValueKind == JsonValueKind.Array)
        {
            foreach (var entryElm in entryIdsElm.EnumerateArray())
                data.Language.EntryIDs.Add(data.Strings.MakeString(entryElm.GetString() ?? ""));
        }

        data.Language.Languages = [];
        if (root.TryGetProperty("languages", out var languagesElm) && languagesElm.ValueKind == JsonValueKind.Array)
        {
            foreach (var languageElm in languagesElm.EnumerateArray())
            {
                if (languageElm.ValueKind != JsonValueKind.Object) continue;
                var language = new UndertaleLanguage.LanguageData
                {
                    Name = data.Strings.MakeString(languageElm.TryGetProperty("name", out var nameElm) ? nameElm.GetString() ?? "" : ""),
                    Region = data.Strings.MakeString(languageElm.TryGetProperty("region", out var regionElm) ? regionElm.GetString() ?? "" : ""),
                    Entries = []
                };

                if (languageElm.TryGetProperty("entries", out var entriesElm) && entriesElm.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entryElm in entriesElm.EnumerateArray())
                        language.Entries.Add(data.Strings.MakeString(entryElm.GetString() ?? ""));
                }

                while (language.Entries.Count < data.Language.EntryIDs.Count)
                    language.Entries.Add(data.Strings.MakeString(""));
                if (language.Entries.Count > data.Language.EntryIDs.Count)
                    language.Entries = [.. language.Entries.Take(data.Language.EntryIDs.Count)];

                data.Language.Languages.Add(language);
            }
        }

        data.Language.EntryCount = (uint)data.Language.EntryIDs.Count;
        data.Language.LanguageCount = (uint)data.Language.Languages.Count;
        Log($"[ImportLanguage] Done. {data.Language.LanguageCount} languages, {data.Language.EntryCount} entries.");
    }

    private static void ImportTags(UndertaleData data, string inputDir)
    {
        string jsonPath = Path.Combine(inputDir, "tags.json");
        if (!FExists(jsonPath)) return;

        if (data.Tags == null)
        {
            Log("[ImportTags] Skipped: target data has no TAGS chunk.");
            return;
        }

        using var jsonDoc = JsonDocument.Parse(FReadText(jsonPath));
        var root = jsonDoc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        data.Tags.Tags ??= [];
        data.Tags.Tags.Clear();
        if (root.TryGetProperty("tags", out var tagsElm) && tagsElm.ValueKind == JsonValueKind.Array)
        {
            foreach (var tagElm in tagsElm.EnumerateArray())
            {
                var tag = tagElm.GetString();
                if (!string.IsNullOrEmpty(tag))
                    data.Tags.Tags.Add(data.Strings.MakeString(tag));
            }
        }

        data.Tags.AssetTags ??= [];
        data.Tags.AssetTags.Clear();
        if (root.TryGetProperty("assetTags", out var assetTagsElm) && assetTagsElm.ValueKind == JsonValueKind.Array)
        {
            foreach (var assetTagElm in assetTagsElm.EnumerateArray())
            {
                if (!TryResolveAssetTagId(data, assetTagElm, out int assetId))
                {
                    Log("[ImportTags] Skipped asset tag entry: unresolved asset identity.");
                    continue;
                }

                var list = new UndertaleSimpleListString();
                if (assetTagElm.TryGetProperty("tags", out var itemTagsElm) && itemTagsElm.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tagElm in itemTagsElm.EnumerateArray())
                    {
                        var tag = tagElm.GetString();
                        if (!string.IsNullOrEmpty(tag))
                            list.Add(data.Strings.MakeString(tag));
                    }
                }
                data.Tags.AssetTags[assetId] = list;
            }
        }

        Log($"[ImportTags] Done. {data.Tags.Tags.Count} global tags, {data.Tags.AssetTags.Count} asset tag entries.");
    }

    private static void ImportFilterEffects(UndertaleData data, string inputDir)
    {
        if (data.FilterEffects == null)
        {
            Log("[ImportFilterEffects] Skipped: target data has no FEDS chunk.");
            return;
        }

        var dirs = GetDirs(inputDir);
        if (dirs.Length == 0) return;

        int created = 0, updated = 0;
        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            string jsonFile = Path.Combine(dir, name + ".json");
            if (!FExists(jsonFile)) continue;

            try
            {
                using var jsonDoc = JsonDocument.Parse(FReadText(jsonFile));
                var root = jsonDoc.RootElement;
                if (root.TryGetProperty("name", out JsonElement nameElm))
                    name = nameElm.GetString() ?? name;

                var effect = data.FilterEffects.ByName(name);
                bool isNew = effect == null;
                if (isNew)
                {
                    effect = new UndertaleFilterEffect
                    {
                        Name = data.Strings.MakeString(name)
                    };
                    data.FilterEffects.Add(effect);
                    created++;
                }
                else updated++;

                effect!.Value = data.Strings.MakeString(root.TryGetProperty("value", out var valueElm) ? valueElm.GetString() ?? "" : "");
            }
            catch (Exception ex) { Log($"[ImportFilterEffects] Error: {name}: {ex.Message}"); }
        }
        Log($"[ImportFilterEffects] Done. Created: {created}, Updated: {updated}");
    }

    private static bool TryResolveAssetTagId(UndertaleData data, JsonElement assetTagElm, out int assetId)
    {
        assetId = 0;

        string assetType = assetTagElm.TryGetProperty("assetType", out var typeElm) ? typeElm.GetString() ?? "" : "";
        string assetName = assetTagElm.TryGetProperty("assetName", out var nameElm) ? nameElm.GetString() ?? "" : "";
        if (!string.IsNullOrEmpty(assetType) && !string.IsNullOrEmpty(assetName) &&
            TryEncodeAssetTagId(data, assetType, assetName, out assetId))
        {
            return true;
        }

        if (assetTagElm.TryGetProperty("assetId", out var assetIdElm))
        {
            int rawAssetId = assetIdElm.GetInt32();
            if (IsAssetTagIdValid(data, rawAssetId))
            {
                assetId = rawAssetId;
                return true;
            }
        }

        return false;
    }

    private static bool TryEncodeAssetTagId(UndertaleData data, string assetType, string assetName, out int assetId)
    {
        assetId = 0;
        if (!Enum.TryParse<ResourceType>(assetType, ignoreCase: true, out var type))
            return false;

        int index = GetAssetIndexByTagType(data, type, assetName);
        if (index < 0) return false;

        int storedIndex = type == ResourceType.Script ? index + 100000 : index;
        assetId = ((int)type << 24) | (storedIndex & 0xFFFFFF);
        return true;
    }

    private static bool IsAssetTagIdValid(UndertaleData data, int assetId)
    {
        var type = (ResourceType)(assetId >> 24);
        int rawIndex = assetId & 0xFFFFFF;
        int index = type == ResourceType.Script ? rawIndex - 100000 : rawIndex;
        return GetAssetNameByTagType(data, type, index) != null;
    }

    private static int GetAssetIndexByTagType(UndertaleData data, ResourceType type, string assetName)
    {
        return type switch
        {
            ResourceType.Object => data.GameObjects.IndexOfName(assetName),
            ResourceType.Sprite => data.Sprites.IndexOfName(assetName),
            ResourceType.Sound => data.Sounds.IndexOfName(assetName),
            ResourceType.Room => data.Rooms.IndexOfName(assetName),
            ResourceType.Path => data.Paths.IndexOfName(assetName),
            ResourceType.Script => data.Scripts.IndexOfName(assetName),
            ResourceType.Font => data.Fonts.IndexOfName(assetName),
            ResourceType.Timeline => data.Timelines.IndexOfName(assetName),
            ResourceType.Background => data.Backgrounds.IndexOfName(assetName),
            ResourceType.Shader => data.Shaders.IndexOfName(assetName),
            ResourceType.Sequence when data.Sequences != null => data.Sequences.IndexOfName(assetName),
            ResourceType.AnimCurve when data.AnimationCurves != null => data.AnimationCurves.IndexOfName(assetName),
            ResourceType.ParticleSystem when data.ParticleSystems != null => data.ParticleSystems.IndexOfName(assetName),
            _ => -1
        };
    }

    private static string? GetAssetNameByTagType(UndertaleData data, ResourceType type, int index)
    {
        return type switch
        {
            ResourceType.Object when index >= 0 && index < data.GameObjects.Count => data.GameObjects[index]?.Name?.Content,
            ResourceType.Sprite when index >= 0 && index < data.Sprites.Count => data.Sprites[index]?.Name?.Content,
            ResourceType.Sound when index >= 0 && index < data.Sounds.Count => data.Sounds[index]?.Name?.Content,
            ResourceType.Room when index >= 0 && index < data.Rooms.Count => data.Rooms[index]?.Name?.Content,
            ResourceType.Path when index >= 0 && index < data.Paths.Count => data.Paths[index]?.Name?.Content,
            ResourceType.Script when index >= 0 && index < data.Scripts.Count => data.Scripts[index]?.Name?.Content,
            ResourceType.Font when index >= 0 && index < data.Fonts.Count => data.Fonts[index]?.Name?.Content,
            ResourceType.Timeline when index >= 0 && index < data.Timelines.Count => data.Timelines[index]?.Name?.Content,
            ResourceType.Background when index >= 0 && index < data.Backgrounds.Count => data.Backgrounds[index]?.Name?.Content,
            ResourceType.Shader when index >= 0 && index < data.Shaders.Count => data.Shaders[index]?.Name?.Content,
            ResourceType.Sequence when data.Sequences != null && index >= 0 && index < data.Sequences.Count => data.Sequences[index]?.Name?.Content,
            ResourceType.AnimCurve when data.AnimationCurves != null && index >= 0 && index < data.AnimationCurves.Count => data.AnimationCurves[index]?.Name?.Content,
            ResourceType.ParticleSystem when data.ParticleSystems != null && index >= 0 && index < data.ParticleSystems.Count => data.ParticleSystems[index]?.Name?.Content,
            _ => null
        };
    }

    private static T GetJsonValue<T>(JsonElement root, string propertyName, T defaultValue)
    {
        if (root.TryGetProperty(propertyName, out JsonElement elm))
        {
            try
            {
                if (typeof(T) == typeof(uint))
                    return (T)(object)(uint)elm.GetInt64();
                if (typeof(T) == typeof(int))
                    return (T)(object)elm.GetInt32();
                if (typeof(T) == typeof(long))
                    return (T)(object)elm.GetInt64();
                if (typeof(T) == typeof(bool))
                {
                    if (elm.ValueKind == JsonValueKind.True) return (T)(object)true;
                    if (elm.ValueKind == JsonValueKind.False) return (T)(object)false;
                    if (elm.ValueKind == JsonValueKind.Number) return (T)(object)(elm.GetInt32() != 0);
                    return (T)(object)elm.GetBoolean();
                }
                if (typeof(T) == typeof(float))
                    return (T)(object)(float)elm.GetDouble();
                if (typeof(T) == typeof(string))
                    return (T)(object)(elm.GetString() ?? "");
            }
            catch { }
        }
        return defaultValue;
    }

    // =========================================================================
    // AudioGroups
    // =========================================================================
    private static void ImportAudioGroups(UndertaleData data, string inputDir)
    {
        var dirs = GetDirs(inputDir);
        if (dirs.Length == 0) return;

        Log($"[ImportAudioGroups] Found {dirs.Length} audio group(s) to import.");
        int created = 0, updated = 0;

        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            string jsonFile = Path.Combine(dir, name + ".json");
            if (!FExists(jsonFile)) continue;

            try
            {
                using var jsonDoc = JsonDocument.Parse(FReadText(jsonFile));
                var root = jsonDoc.RootElement;

                var ag = data.AudioGroups?.ByName(name);
                bool isNew = ag == null;
                if (isNew)
                {
                    ag = new UndertaleAudioGroup { Name = data.Strings.MakeString(name) };
                }

                if (root.TryGetProperty("path", out JsonElement pathElm))
                {
                    string path = pathElm.GetString() ?? "";
                    if (!string.IsNullOrEmpty(path))
                        ag!.Path = data.Strings.MakeString(path);
                }

                if (isNew) { data.AudioGroups!.Add(ag!); created++; } else updated++;
            }
            catch (Exception ex) { Log($"[ImportAudioGroups] Error: {name}: {ex.Message}"); }
        }
        Log($"[ImportAudioGroups] Done. Created: {created}, Updated: {updated}");
    }

    // =========================================================================
    // Paths
    // =========================================================================
    private static void ImportPaths(UndertaleData data, string inputDir)
    {
        var dirs = GetDirs(inputDir);
        if (dirs.Length == 0) return;

        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            string jsonFile = Path.Combine(dir, name + ".json");
            if (!FExists(jsonFile)) continue;

            try
            {
                using var jsonDoc = JsonDocument.Parse(FReadText(jsonFile));
                var root = jsonDoc.RootElement;

                var path = data.Paths?.ByName(name);
                bool isNew = path == null;
                if (isNew)
                {
                    path = new UndertalePath
                    {
                        Name = data.Strings.MakeString(name),
                        Points = [],
                        IsSmooth = false,
                        IsClosed = false,
                        Precision = 4
                    };
                }

                if (root.TryGetProperty("isSmooth", out JsonElement isSmoothElm))
                    path!.IsSmooth = isSmoothElm.GetBoolean();
                if (root.TryGetProperty("isClosed", out JsonElement isClosedElm))
                    path!.IsClosed = isClosedElm.GetBoolean();
                if (root.TryGetProperty("precision", out JsonElement precisionElm))
                    path!.Precision = (uint)precisionElm.GetInt32();

                if (root.TryGetProperty("points", out JsonElement pointsElm) && pointsElm.ValueKind == JsonValueKind.Array)
                {
                    path!.Points.Clear();
                    foreach (var ptElm in pointsElm.EnumerateArray())
                    {
                        var pt = new UndertalePath.PathPoint();
                        if (ptElm.TryGetProperty("x", out JsonElement xElm)) pt.X = (float)xElm.GetDouble();
                        if (ptElm.TryGetProperty("y", out JsonElement yElm)) pt.Y = (float)yElm.GetDouble();
                        if (ptElm.TryGetProperty("speed", out JsonElement sElm)) pt.Speed = (float)sElm.GetDouble();
                        path.Points.Add(pt);
                    }
                }

                if (isNew) data.Paths!.Add(path!);
            }
            catch (Exception ex) { Log($"[ImportPaths] Error: {name}: {ex.Message}"); }
        }
    }

    // =========================================================================
    // TexturePageItems
    // =========================================================================
    private static void ImportTexturePageItems(UndertaleData data, string inputDir)
    {
        string jsonPath = Path.Combine(inputDir, "texture_page_items.json");
        if (!FExists(jsonPath)) return;

        using var jsonDoc = JsonDocument.Parse(FReadText(jsonPath));
        if (jsonDoc.RootElement.ValueKind != JsonValueKind.Array) return;

        var items = jsonDoc.RootElement.EnumerateArray().ToList();
        int updated = 0, created = 0;

        foreach (var itemElm in items)
        {
            try
            {
                if (itemElm.TryGetProperty("isNull", out var nullElm) && nullElm.GetBoolean())
                    continue;

                int index = itemElm.TryGetProperty("index", out var idxElm) ? idxElm.GetInt32() : -1;
                string name = itemElm.TryGetProperty("name", out var nameElm) ? nameElm.GetString() ?? "" : "";
                ushort sourceX = itemElm.TryGetProperty("sourceX", out var sxElm) ? (ushort)sxElm.GetInt32() : (ushort)0;
                ushort sourceY = itemElm.TryGetProperty("sourceY", out var syElm) ? (ushort)syElm.GetInt32() : (ushort)0;
                ushort sourceWidth = itemElm.TryGetProperty("sourceWidth", out var swElm) ? (ushort)swElm.GetInt32() : (ushort)0;
                ushort sourceHeight = itemElm.TryGetProperty("sourceHeight", out var shElm) ? (ushort)shElm.GetInt32() : (ushort)0;
                ushort targetX = itemElm.TryGetProperty("targetX", out var txElm) ? (ushort)txElm.GetInt32() : (ushort)0;
                ushort targetY = itemElm.TryGetProperty("targetY", out var tyElm) ? (ushort)tyElm.GetInt32() : (ushort)0;
                ushort targetWidth = itemElm.TryGetProperty("targetWidth", out var twElm) ? (ushort)twElm.GetInt32() : (ushort)0;
                ushort targetHeight = itemElm.TryGetProperty("targetHeight", out var thElm) ? (ushort)thElm.GetInt32() : (ushort)0;
                ushort boundingWidth = itemElm.TryGetProperty("boundingWidth", out var bwElm) ? (ushort)bwElm.GetInt32() : (ushort)0;
                ushort boundingHeight = itemElm.TryGetProperty("boundingHeight", out var bhElm) ? (ushort)bhElm.GetInt32() : (ushort)0;
                int texturePageIndex = itemElm.TryGetProperty("texturePageIndex", out var tpElm) ? tpElm.GetInt32() : -1;

                UndertaleTexturePageItem item;
                if (index >= 0 && index < data.TexturePageItems.Count)
                {
                    item = data.TexturePageItems[index];
                    updated++;
                }
                else
                {
                    item = new UndertaleTexturePageItem
                    {
                        Name = new UndertaleString(name.Length > 0 ? name : $"PageItem {data.TexturePageItems.Count}")
                    };
                    data.TexturePageItems.Add(item);
                    created++;
                }

                item.SourceX = sourceX; item.SourceY = sourceY;
                item.SourceWidth = sourceWidth; item.SourceHeight = sourceHeight;
                item.TargetX = targetX; item.TargetY = targetY;
                item.TargetWidth = targetWidth; item.TargetHeight = targetHeight;
                item.BoundingWidth = boundingWidth; item.BoundingHeight = boundingHeight;

                if (texturePageIndex >= 0 && texturePageIndex < data.EmbeddedTextures.Count)
                    item.TexturePage = data.EmbeddedTextures[texturePageIndex];
            }
            catch (Exception ex) { Log($"[ImportTexturePageItems] Error: {ex.Message}"); }
        }
        for (int i = data.TexturePageItems.Count - 1; i >= items.Count; i--)
            data.TexturePageItems.RemoveAt(i);
        Log($"[ImportTexturePageItems] Done. {updated} updated, {created} created.");
    }

    // =========================================================================
    // Backgrounds
    // =========================================================================
    private static void ImportBackgrounds(UndertaleData data, string inputDir)
    {
        var dirs = GetDirs(inputDir);
        if (dirs.Length == 0) return;

        int imported = 0, created = 0;
        using TextureWorker worker = new();

        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            string pngPath = Path.Combine(dir, name + ".png");
            string jsonPath = Path.Combine(dir, name + ".json");
            if (!FExists(pngPath) && !FExists(jsonPath)) continue;

            try
            {
                var bg = data.Backgrounds.ByName(name);
                bool isNew = bg == null;
                if (isNew)
                {
                    bg = new UndertaleBackground
                    {
                        Name = data.Strings.MakeString(name),
                        Transparent = false,
                        Smooth = false,
                        Preload = false
                    };
                    if (data.IsGameMaker2())
                    {
                        bg.GMS2TileWidth = 0; bg.GMS2TileHeight = 0;
                        bg.GMS2TileColumns = 0; bg.GMS2TileCount = 0;
                        bg.GMS2OutputBorderX = 0; bg.GMS2OutputBorderY = 0;
                        bg.GMS2FrameLength = 0;
                    }
                    created++;
                }

                if (FExists(pngPath))
                {
                    using var img = new MagickImage(FReadBytes(pngPath));
                    int lastTP = data.EmbeddedTextures.Count - 1;
                    int lastTPI = data.TexturePageItems.Count - 1;

                    var newTex = new UndertaleEmbeddedTexture
                    {
                        Name = new UndertaleString($"Texture {++lastTP}")
                    };
                    newTex.TextureData.Image = GMImage.FromMagickImage(img).ConvertToPng();
                    data.EmbeddedTextures.Add(newTex);

                    var newTPI = new UndertaleTexturePageItem
                    {
                        Name = new UndertaleString($"PageItem {++lastTPI}"),
                        SourceX = 0,
                        SourceY = 0,
                        SourceWidth = (ushort)img.Width,
                        SourceHeight = (ushort)img.Height,
                        TargetX = 0,
                        TargetY = 0,
                        TargetWidth = (ushort)img.Width,
                        TargetHeight = (ushort)img.Height,
                        BoundingWidth = (ushort)img.Width,
                        BoundingHeight = (ushort)img.Height,
                        TexturePage = newTex
                    };
                    data.TexturePageItems.Add(newTPI);
                    bg!.Texture = newTPI;
                }

                if (FExists(jsonPath))
                {
                    using var jsonDoc = JsonDocument.Parse(FReadText(jsonPath));
                    var root = jsonDoc.RootElement;
                    bg!.Transparent = GetJsonValue(root, "transparent", bg.Transparent);
                    bg.Smooth = GetJsonValue(root, "smooth", bg.Smooth);
                    bg.Preload = GetJsonValue(root, "preload", bg.Preload);
                    if (data.IsGameMaker2() && root.TryGetProperty("gms2UnknownAlways2", out _))
                        bg.GMS2UnknownAlways2 = GetJsonValue(root, "gms2UnknownAlways2", bg.GMS2UnknownAlways2);
                }

                if (isNew) data.Backgrounds.Add(bg!);
                imported++;
            }
            catch (Exception ex) { Log($"[ImportBackgrounds] Failed: {name}: {ex.Message}"); }
        }
        Log($"[ImportBackgrounds] Done. {imported} processed ({created} new).");
    }

    // =========================================================================
    // Timelines
    // =========================================================================
    private static void ImportTimelines(UndertaleData data, string inputDir)
    {
        var dirs = GetDirs(inputDir);
        if (dirs.Length == 0) return;

        int created = 0, updated = 0;
        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            string jsonFile = Path.Combine(dir, name + ".json");
            if (!FExists(jsonFile)) continue;

            try
            {
                using var jsonDoc = JsonDocument.Parse(FReadText(jsonFile));
                var root = jsonDoc.RootElement;
                if (root.TryGetProperty("name", out JsonElement nameElm))
                    name = nameElm.GetString() ?? name;

                var tl = data.Timelines?.ByName(name);
                bool isNew = tl == null;
                if (isNew)
                {
                    tl = new UndertaleTimeline
                    {
                        Name = data.Strings.MakeString(name),
                        Moments = []
                    };
                    created++;
                }
                else updated++;

                if (root.TryGetProperty("moments", out JsonElement momentsElm) && momentsElm.ValueKind == JsonValueKind.Array)
                {
                    var arr = momentsElm.EnumerateArray().ToArray();
                    for (int mi = 0; mi < arr.Length; mi++)
                    {
                        var mElm = arr[mi];
                        UndertaleTimeline.UndertaleTimelineMoment moment;
                        if (mi < tl!.Moments.Count) { moment = tl.Moments[mi]; }
                        else
                        {
                            moment = new UndertaleTimeline.UndertaleTimelineMoment
                            {
                                Event = []
                            };
                            tl.Moments.Add(moment);
                        }
                        if (mElm.TryGetProperty("step", out JsonElement stepElm))
                            moment.Step = (uint)stepElm.GetInt64();
                        if (mElm.TryGetProperty("actions", out JsonElement actionsElm) && actionsElm.ValueKind == JsonValueKind.Array)
                        {
                            moment.Event ??= [];
                            var actArr = actionsElm.EnumerateArray().ToArray();
                            for (int ai = 0; ai < actArr.Length; ai++)
                            {
                                var aElm = actArr[ai];
                                UndertaleGameObject.EventAction action;
                                if (ai < moment.Event.Count) action = moment.Event[ai];
                                else { action = new UndertaleGameObject.EventAction(); moment.Event.Add(action); }
                                ApplyEventAction(data, action, aElm);
                            }
                            for (int ai = moment.Event.Count - 1; ai >= actArr.Length; ai--)
                                moment.Event.RemoveAt(ai);
                        }
                    }
                    for (int mi = tl!.Moments.Count - 1; mi >= arr.Length; mi--)
                        tl.Moments.RemoveAt(mi);
                }
                if (isNew) data.Timelines!.Add(tl!);
            }
            catch (Exception ex) { Log($"[ImportTimelines] Error: {name}: {ex.Message}"); }
        }
        Log($"[ImportTimelines] Done. Created: {created}, Updated: {updated}");
    }

    // =========================================================================
    // AnimationCurves
    // =========================================================================
    private static void ImportAnimationCurves(UndertaleData data, string inputDir)
    {
        if (data.AnimationCurves == null)
        {
            Log("[ImportAnimationCurves] Skipped: target data has no ACRV chunk.");
            return;
        }

        var dirs = GetDirs(inputDir);
        if (dirs.Length == 0) return;

        int created = 0, updated = 0;
        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            string jsonFile = Path.Combine(dir, name + ".json");
            if (!FExists(jsonFile)) continue;

            try
            {
                using var jsonDoc = JsonDocument.Parse(FReadText(jsonFile));
                var root = jsonDoc.RootElement;
                if (root.TryGetProperty("name", out JsonElement nameElm))
                    name = nameElm.GetString() ?? name;

                var curve = data.AnimationCurves.ByName(name);
                bool isNew = curve == null;
                if (isNew)
                {
                    curve = new UndertaleAnimationCurve
                    {
                        Name = data.Strings.MakeString(name),
                        Channels = []
                    };
                    data.AnimationCurves.Add(curve);
                    created++;
                }
                else updated++;

                curve!.GraphType = (UndertaleAnimationCurve.GraphTypeEnum)GetJsonValue(root, "graphType", (int)curve.GraphType);
                curve.Channels ??= [];
                curve.Channels.Clear();

                if (root.TryGetProperty("channels", out var channelsElm) && channelsElm.ValueKind == JsonValueKind.Array)
                {
                    foreach (var channelElm in channelsElm.EnumerateArray())
                    {
                        if (channelElm.ValueKind != JsonValueKind.Object) continue;
                        var channel = new UndertaleAnimationCurve.Channel
                        {
                            Name = data.Strings.MakeString(channelElm.TryGetProperty("name", out var channelNameElm) ? channelNameElm.GetString() ?? "" : ""),
                            Curve = (UndertaleAnimationCurve.Channel.CurveType)GetJsonValue(channelElm, "curve", 0),
                            Iterations = GetJsonValue(channelElm, "iterations", (uint)0),
                            Points = []
                        };

                        if (channelElm.TryGetProperty("points", out var pointsElm) && pointsElm.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var pointElm in pointsElm.EnumerateArray())
                            {
                                if (pointElm.ValueKind != JsonValueKind.Object) continue;
                                channel.Points.Add(new UndertaleAnimationCurve.Channel.Point
                                {
                                    X = GetJsonValue(pointElm, "x", 0f),
                                    Value = GetJsonValue(pointElm, "value", 0f),
                                    BezierX0 = GetJsonValue(pointElm, "bezierX0", 0f),
                                    BezierY0 = GetJsonValue(pointElm, "bezierY0", 0f),
                                    BezierX1 = GetJsonValue(pointElm, "bezierX1", 0f),
                                    BezierY1 = GetJsonValue(pointElm, "bezierY1", 0f)
                                });
                            }
                        }

                        curve.Channels.Add(channel);
                    }
                }
            }
            catch (Exception ex) { Log($"[ImportAnimationCurves] Error: {name}: {ex.Message}"); }
        }
        Log($"[ImportAnimationCurves] Done. Created: {created}, Updated: {updated}");
    }

    // =========================================================================
    // Sequences
    // =========================================================================
    private static void ImportSequences(UndertaleData data, string inputDir)
    {
        if (data.Sequences == null)
        {
            Log("[ImportSequences] Skipped: target data has no SEQN chunk.");
            return;
        }

        var dirs = GetDirs(inputDir);
        if (dirs.Length == 0) return;

        var files = new List<string>();
        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            string jsonFile = Path.Combine(dir, name + ".json");
            if (FExists(jsonFile))
                files.Add(jsonFile);
        }
        if (files.Count == 0) return;

        string tempRoot = Path.Combine(Path.GetTempPath(), "g3mtool_seq_import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var localFiles = new List<string>(files.Count);
            foreach (string source in files)
            {
                string local = Path.Combine(tempRoot, Path.GetFileName(Path.GetDirectoryName(source)!) + ".json");
                File.WriteAllText(local, FReadText(source), Encoding.UTF8);
                localFiles.Add(local);
            }

            var context = CreateProjectContext(data, Path.Combine(tempRoot, ".project_context"));
            SerializableProjectAssetBridge.ImportMany(context, localFiles);
            Log($"[ImportSequences] Done. Imported: {localFiles.Count}");
        }
        catch (Exception ex)
        {
            Log($"[ImportSequences] Error: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static void ImportParticleSystems(UndertaleData data, string inputDir)
    {
        if (data.ParticleSystems == null)
        {
            Log("[ImportParticleSystems] Skipped: target data has no PSYS chunk.");
            return;
        }

        foreach (string dir in GetDirs(inputDir))
        {
            string name = Path.GetFileName(dir);
            string jsonFile = Path.Combine(dir, name + ".json");
            if (!FExists(jsonFile)) continue;
            try
            {
                using var jsonDoc = JsonDocument.Parse(FReadText(jsonFile));
                var root = jsonDoc.RootElement;
                if (root.TryGetProperty("name", out var nameElm))
                    name = nameElm.GetString() ?? name;
                var ps = data.ParticleSystems.ByName(name);
                if (ps == null)
                {
                    ps = new UndertaleParticleSystem { Name = data.Strings.MakeString(name) };
                    data.ParticleSystems.Add(ps);
                }
                ps.OriginX = GetJsonValue(root, "originX", ps.OriginX);
                ps.OriginY = GetJsonValue(root, "originY", ps.OriginY);
                ps.DrawOrder = (UndertaleParticleSystem.DrawOrderEnum)GetJsonValue(root, "drawOrder", (int)ps.DrawOrder);
                ps.GlobalSpaceParticles = GetJsonValue(root, "globalSpaceParticles", ps.GlobalSpaceParticles);
                ps.Emitters = [];
                if (root.TryGetProperty("emitters", out var emittersElm) && emittersElm.ValueKind == JsonValueKind.Array)
                {
                    foreach (var emitterElm in emittersElm.EnumerateArray())
                    {
                        var emitterName = emitterElm.GetString();
                        var emitter = !string.IsNullOrEmpty(emitterName) ? data.ParticleSystemEmitters?.ByName(emitterName) : null;
                        if (emitter != null)
                            ps.Emitters.Add(new UndertaleResourceById<UndertaleParticleSystemEmitter, UndertaleChunkPSEM>(emitter));
                    }
                }
            }
            catch (Exception ex) { Log($"[ImportParticleSystems] Error: {name}: {ex.Message}"); }
        }
    }

    private static void ImportParticleSystemEmitters(UndertaleData data, string inputDir)
    {
        if (data.ParticleSystemEmitters == null)
        {
            Log("[ImportParticleSystemEmitters] Skipped: target data has no PSEM chunk.");
            return;
        }

        var pendingRefs = new List<(UndertaleParticleSystemEmitter Emitter, string Sprite, string SpawnOnDeath, string SpawnOnUpdate)>();
        foreach (string dir in GetDirs(inputDir))
        {
            string name = Path.GetFileName(dir);
            string jsonFile = Path.Combine(dir, name + ".json");
            if (!FExists(jsonFile)) continue;
            try
            {
                using var jsonDoc = JsonDocument.Parse(FReadText(jsonFile));
                var root = jsonDoc.RootElement;
                if (root.TryGetProperty("name", out var nameElm))
                    name = nameElm.GetString() ?? name;
                var emitter = data.ParticleSystemEmitters.ByName(name);
                if (emitter == null)
                {
                    emitter = new UndertaleParticleSystemEmitter { Name = data.Strings.MakeString(name) };
                    data.ParticleSystemEmitters.Add(emitter);
                }
                ApplyParticleEmitterScalars(emitter, root);
                string sprite = root.TryGetProperty("sprite", out var spriteElm) ? spriteElm.GetString() ?? "" : "";
                string sod = root.TryGetProperty("spawnOnDeath", out var sodElm) ? sodElm.GetString() ?? "" : "";
                string sou = root.TryGetProperty("spawnOnUpdate", out var souElm) ? souElm.GetString() ?? "" : "";
                pendingRefs.Add((emitter, sprite, sod, sou));
            }
            catch (Exception ex) { Log($"[ImportParticleSystemEmitters] Error: {name}: {ex.Message}"); }
        }

        foreach (var (emitter, sprite, spawnOnDeath, spawnOnUpdate) in pendingRefs)
        {
            if (!string.IsNullOrEmpty(sprite))
                emitter.Sprite = data.Sprites.ByName(sprite);
            if (!string.IsNullOrEmpty(spawnOnDeath))
                emitter.SpawnOnDeath = data.ParticleSystemEmitters.ByName(spawnOnDeath);
            if (!string.IsNullOrEmpty(spawnOnUpdate))
                emitter.SpawnOnUpdate = data.ParticleSystemEmitters.ByName(spawnOnUpdate);
        }
    }

    private static void ApplyParticleEmitterScalars(UndertaleParticleSystemEmitter emitter, JsonElement root)
    {
        foreach (var prop in typeof(UndertaleParticleSystemEmitter).GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (prop.Name is "Name" or "Sprite" or "SpawnOnDeath" or "SpawnOnUpdate" or
                "SizeMin" or "SizeMax" or "SizeIncrease" or "SizeWiggle")
                continue;
            if (!root.TryGetProperty(prop.Name, out var value)) continue;
            var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (type.IsEnum)
                prop.SetValue(emitter, Enum.ToObject(type, value.GetInt32()));
            else if (type == typeof(bool))
                prop.SetValue(emitter, value.GetBoolean());
            else if (type == typeof(int))
                prop.SetValue(emitter, value.GetInt32());
            else if (type == typeof(uint))
                prop.SetValue(emitter, (uint)value.GetInt64());
            else if (type == typeof(float))
                prop.SetValue(emitter, (float)value.GetDouble());
        }
    }

    private static ProjectContext CreateProjectContext(UndertaleData data, string root)
    {
        Directory.CreateDirectory(root);
        string load = Path.Combine(root, "load.win");
        string save = Path.Combine(root, "save.win");
        string project = Path.Combine(root, "project", "project.yy");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        return new ProjectContext(data, load, save, project, "G3MTool");
    }

    // Shared helper for event action properties (used by Timelines and GameObjects)
    private static void ApplyEventAction(UndertaleData data, UndertaleGameObject.EventAction action, JsonElement elm)
    {
        if (elm.TryGetProperty("libId", out JsonElement libIdElm))
            action.LibID = (uint)libIdElm.GetInt64();
        if (elm.TryGetProperty("id", out JsonElement idElm))
            action.ID = (uint)idElm.GetInt64();
        if (elm.TryGetProperty("kind", out JsonElement kindElm))
            action.Kind = (uint)kindElm.GetInt64();
        if (elm.TryGetProperty("useRelative", out JsonElement useRelativeElm))
            action.UseRelative = useRelativeElm.GetBoolean();
        if (elm.TryGetProperty("isQuestion", out JsonElement isQuestionElm))
            action.IsQuestion = isQuestionElm.GetBoolean();
        if (elm.TryGetProperty("useApplyTo", out JsonElement useApplyToElm))
            action.UseApplyTo = useApplyToElm.GetBoolean();
        if (elm.TryGetProperty("exeType", out JsonElement exeTypeElm))
            action.ExeType = (uint)exeTypeElm.GetInt64();
        if (elm.TryGetProperty("actionName", out JsonElement actionNameElm))
        {
            string actionName = actionNameElm.GetString() ?? "";
            action.ActionName = !string.IsNullOrEmpty(actionName) ? data.Strings.MakeString(actionName) : null;
        }
        if (elm.TryGetProperty("codeId", out JsonElement codeIdElm))
        {
            if (codeIdElm.ValueKind == JsonValueKind.String)
            {
                string codeName = codeIdElm.GetString() ?? "";
                if (!string.IsNullOrEmpty(codeName))
                    action.CodeId = data.Code.ByName(codeName);
            }
            else if (codeIdElm.ValueKind == JsonValueKind.Null)
                action.CodeId = null;
        }
        if (elm.TryGetProperty("argumentCount", out JsonElement argumentCountElm))
            action.ArgumentCount = (uint)argumentCountElm.GetInt64();
        if (elm.TryGetProperty("who", out JsonElement whoElm))
            action.Who = whoElm.GetInt32();
        if (elm.TryGetProperty("relative", out JsonElement relativeElm))
            action.Relative = relativeElm.GetBoolean();
        if (elm.TryGetProperty("isNot", out JsonElement isNotElm))
            action.IsNot = isNotElm.GetBoolean();
    }

    // =========================================================================
    // Shaders
    // =========================================================================
    private static void ImportShaders(UndertaleData data, string inputDir)
    {
        var dirs = GetDirs(inputDir);
        int imported = 0, updated = 0;

        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            try
            {
                bool existed = data.Shaders.ByName(name) != null;
                ImportShaderSingle(data, dir);
                if (existed) updated++; else imported++;
            }
            catch (Exception ex) { Log($"[ImportShaders] Error: {name}: {ex.Message}"); }
        }
        Log($"[ImportShaders] Done. {imported} new, {updated} updated.");
    }

    private static void ImportShaderSingle(UndertaleData data, string shaderDir)
    {
        string name = Path.GetFileName(shaderDir);
        if (string.IsNullOrEmpty(name)) return;

        var shader = data.Shaders.ByName(name);
        if (shader == null)
        {
            shader = new UndertaleShader { Name = new UndertaleString(name) };
            data.Strings.Add(shader.Name);
            data.Shaders.Add(shader);
        }

        string typeFile = Path.Combine(shaderDir, "Type.txt");
        if (FExists(typeFile))
        {
            string typeStr = FReadText(typeFile);
            if (!string.IsNullOrEmpty(typeStr) && Enum.TryParse<UndertaleShader.ShaderType>(typeStr, out var st))
                shader.Type = st;
        }

        string[] textFiles = ["GLSL_ES_Fragment.txt", "GLSL_ES_Vertex.txt", "GLSL_Fragment.txt", "GLSL_Vertex.txt", "HLSL9_Fragment.txt", "HLSL9_Vertex.txt"];
        foreach (string fileName in textFiles)
        {
            string fp = Path.Combine(shaderDir, fileName);
            if (!FExists(fp)) continue;
            try
            {
                string code = FReadText(fp);
                UndertaleString? shaderString = null;
                switch (fileName)
                {
                    case "GLSL_ES_Fragment.txt": shader.GLSL_ES_Fragment = data.Strings.MakeString(code); shaderString = shader.GLSL_ES_Fragment; break;
                    case "GLSL_ES_Vertex.txt": shader.GLSL_ES_Vertex = data.Strings.MakeString(code); shaderString = shader.GLSL_ES_Vertex; break;
                    case "GLSL_Fragment.txt": shader.GLSL_Fragment = data.Strings.MakeString(code); shaderString = shader.GLSL_Fragment; break;
                    case "GLSL_Vertex.txt": shader.GLSL_Vertex = data.Strings.MakeString(code); shaderString = shader.GLSL_Vertex; break;
                    case "HLSL9_Fragment.txt": shader.HLSL9_Fragment = data.Strings.MakeString(code); shaderString = shader.HLSL9_Fragment; break;
                    case "HLSL9_Vertex.txt": shader.HLSL9_Vertex = data.Strings.MakeString(code); shaderString = shader.HLSL9_Vertex; break;
                }
                if (shaderString != null && !data.Strings.Any(s => s == shaderString))
                    data.Strings.Add(shaderString);
            }
            catch { }
        }

        string[] binaryFiles = ["HLSL11_VertexData.bin", "HLSL11_PixelData.bin", "PSSL_VertexData.bin", "PSSL_PixelData.bin",
                                  "Cg_PSVita_VertexData.bin", "Cg_PSVita_PixelData.bin", "Cg_PS3_VertexData.bin", "Cg_PS3_PixelData.bin"];
        foreach (string fileName in binaryFiles)
        {
            string fp = Path.Combine(shaderDir, fileName);
            if (!FExists(fp)) continue;
            try
            {
                byte[] bytes = FReadBytes(fp);
                if (bytes.Length == 0) continue;
                static UndertaleShader.UndertaleRawShaderData GetOrCreate(ref UndertaleShader.UndertaleRawShaderData? field)
                {
                    field ??= new UndertaleShader.UndertaleRawShaderData();
                    return field;
                }
                switch (fileName)
                {
                    case "HLSL11_VertexData.bin": var d1 = GetOrCreate(ref shader.HLSL11_VertexData); d1.Data = bytes; d1.IsNull = false; break;
                    case "HLSL11_PixelData.bin": var d2 = GetOrCreate(ref shader.HLSL11_PixelData); d2.Data = bytes; d2.IsNull = false; break;
                    case "PSSL_VertexData.bin": var d3 = GetOrCreate(ref shader.PSSL_VertexData); d3.Data = bytes; d3.IsNull = false; break;
                    case "PSSL_PixelData.bin": var d4 = GetOrCreate(ref shader.PSSL_PixelData); d4.Data = bytes; d4.IsNull = false; break;
                    case "Cg_PSVita_VertexData.bin": var d5 = GetOrCreate(ref shader.Cg_PSVita_VertexData); d5.Data = bytes; d5.IsNull = false; break;
                    case "Cg_PSVita_PixelData.bin": var d6 = GetOrCreate(ref shader.Cg_PSVita_PixelData); d6.Data = bytes; d6.IsNull = false; break;
                    case "Cg_PS3_VertexData.bin": var d7 = GetOrCreate(ref shader.Cg_PS3_VertexData); d7.Data = bytes; d7.IsNull = false; break;
                    case "Cg_PS3_PixelData.bin": var d8 = GetOrCreate(ref shader.Cg_PS3_PixelData); d8.Data = bytes; d8.IsNull = false; break;
                }
            }
            catch { }
        }

        string attrsFile = Path.Combine(shaderDir, "VertexShaderAttributes.txt");
        if (FExists(attrsFile))
        {
            try
            {
                string text = FReadText(attrsFile);
                if (!string.IsNullOrEmpty(text))
                {
                    shader.VertexShaderAttributes ??= [];
                    shader.VertexShaderAttributes.Clear();
                    foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var attrName = data.Strings.MakeString(line.Trim());
                        shader.VertexShaderAttributes.Add(new UndertaleShader.VertexShaderAttribute { Name = attrName });
                    }
                }
            }
            catch { }
        }
    }

    // =========================================================================
    // GeneralInfo
    // =========================================================================
    private static void ImportGeneralInfo(UndertaleData data, string inputDir)
    {
        if (data.GeneralInfo == null) return;

        string jsonPath = Path.Combine(inputDir, "GeneralInfo.json");
        if (!FExists(jsonPath))
            jsonPath = Path.Combine(inputDir, "GeneralInfo", "GeneralInfo.json");
        if (!FExists(jsonPath)) return;

        try
        {
            using var jsonDoc = JsonDocument.Parse(FReadText(jsonPath));
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("isDebuggerDisabled", out JsonElement dbgElm)) data.GeneralInfo.IsDebuggerDisabled = dbgElm.GetBoolean();
            if (root.TryGetProperty("bytecodeVersion", out JsonElement bcElm)) data.GeneralInfo.BytecodeVersion = (byte)bcElm.GetInt32();
            if (root.TryGetProperty("padding", out JsonElement padElm)) data.GeneralInfo.Padding = (ushort)padElm.GetInt32();
            if (root.TryGetProperty("fileName", out JsonElement fnElm)) { string fn = fnElm.GetString() ?? ""; if (fn.Length > 0) data.GeneralInfo.FileName = data.Strings.MakeString(fn); }
            if (root.TryGetProperty("config", out JsonElement cfgElm)) { string cfg = cfgElm.GetString() ?? ""; if (cfg.Length > 0) data.GeneralInfo.Config = data.Strings.MakeString(cfg); }
            if (root.TryGetProperty("lastObj", out JsonElement loElm)) data.GeneralInfo.LastObj = (uint)loElm.GetInt64();
            if (root.TryGetProperty("lastTile", out JsonElement ltElm)) data.GeneralInfo.LastTile = (uint)ltElm.GetInt64();
            if (root.TryGetProperty("gameID", out JsonElement giElm)) data.GeneralInfo.GameID = (uint)giElm.GetInt64();
            if (root.TryGetProperty("directPlayGuid", out JsonElement dpElm)) { string gs = dpElm.GetString() ?? ""; if (Guid.TryParse(gs, out Guid g)) data.GeneralInfo.DirectPlayGuid = g; }
            if (root.TryGetProperty("name", out JsonElement nmElm)) { string nm = nmElm.GetString() ?? ""; if (nm.Length > 0) data.GeneralInfo.Name = data.Strings.MakeString(nm); }
            if (root.TryGetProperty("major", out JsonElement majElm)) data.GeneralInfo.Major = (uint)majElm.GetInt64();
            if (root.TryGetProperty("minor", out JsonElement minElm)) data.GeneralInfo.Minor = (uint)minElm.GetInt64();
            if (root.TryGetProperty("release", out JsonElement relElm)) data.GeneralInfo.Release = (uint)relElm.GetInt64();
            if (root.TryGetProperty("build", out JsonElement bldElm)) data.GeneralInfo.Build = (uint)bldElm.GetInt64();
            if (root.TryGetProperty("defaultWindowWidth", out JsonElement wwElm)) data.GeneralInfo.DefaultWindowWidth = (uint)wwElm.GetInt64();
            if (root.TryGetProperty("defaultWindowHeight", out JsonElement whElm)) data.GeneralInfo.DefaultWindowHeight = (uint)whElm.GetInt64();
            if (root.TryGetProperty("infoFlags", out JsonElement ifElm)) data.GeneralInfo.Info = (UndertaleGeneralInfo.InfoFlags)ifElm.GetUInt32();
            if (root.TryGetProperty("licenseCRC32", out JsonElement lcElm)) data.GeneralInfo.LicenseCRC32 = (uint)lcElm.GetInt64();
            if (root.TryGetProperty("licenseMD5", out JsonElement lmElm) && lmElm.ValueKind == JsonValueKind.Array)
                data.GeneralInfo.LicenseMD5 = [.. lmElm.EnumerateArray().Select(e => (byte)e.GetInt32())];
            if (root.TryGetProperty("timestamp", out JsonElement tsElm)) data.GeneralInfo.Timestamp = (ulong)tsElm.GetUInt64();
            if (root.TryGetProperty("displayName", out JsonElement dnElm)) { string dn = dnElm.GetString() ?? ""; if (dn.Length > 0) data.GeneralInfo.DisplayName = data.Strings.MakeString(dn); }
            if (root.TryGetProperty("activeTargets", out JsonElement atElm)) data.GeneralInfo.ActiveTargets = atElm.GetUInt64();
            if (root.TryGetProperty("functionClassifications", out JsonElement fcElm)) data.GeneralInfo.FunctionClassifications = (UndertaleGeneralInfo.FunctionClassification)fcElm.GetUInt64();
            if (root.TryGetProperty("steamAppID", out JsonElement saElm)) data.GeneralInfo.SteamAppID = saElm.GetInt32();
            if (data.GeneralInfo.BytecodeVersion >= 14 && root.TryGetProperty("debuggerPort", out JsonElement dprtElm))
                data.GeneralInfo.DebuggerPort = (uint)dprtElm.GetInt64();

            if (root.TryGetProperty("roomOrder", out JsonElement roElm) && roElm.ValueKind == JsonValueKind.Array)
            {
                data.GeneralInfo.RoomOrder.Clear();
                foreach (var rElm in roElm.EnumerateArray())
                {
                    if (rElm.ValueKind == JsonValueKind.Null) continue;
                    string rn = rElm.GetString() ?? "";
                    if (string.IsNullOrEmpty(rn)) continue;
                    var room = data.Rooms.ByName(rn);
                    if (room != null)
                        data.GeneralInfo.RoomOrder.Add(new UndertaleResourceById<UndertaleRoom, UndertaleChunkROOM> { Resource = room });
                }
            }

            if (data.GeneralInfo.Major >= 2)
            {
                if (root.TryGetProperty("gms2RandomUID", out JsonElement uidElm) && uidElm.ValueKind == JsonValueKind.Array)
                    data.GeneralInfo.GMS2RandomUID = [.. uidElm.EnumerateArray().Select(e => e.GetInt64())];
                if (root.TryGetProperty("gms2FPS", out JsonElement fpsElm))
                    data.GeneralInfo.GMS2FPS = (float)fpsElm.GetDouble();
                if (root.TryGetProperty("gms2AllowStatistics", out JsonElement asElm))
                    data.GeneralInfo.GMS2AllowStatistics = asElm.GetBoolean();
                if (root.TryGetProperty("gms2GameGUID", out JsonElement ggElm) && ggElm.ValueKind == JsonValueKind.Array)
                    data.GeneralInfo.GMS2GameGUID = [.. ggElm.EnumerateArray().Select(e => (byte)e.GetInt32())];
            }

            Log($"[ImportGeneralInfo] Done. Game: {data.GeneralInfo.DisplayName?.Content ?? "N/A"}");
        }
        catch (Exception ex) { Log($"[ImportGeneralInfo] Failed: {ex.Message}"); throw; }
    }
}
