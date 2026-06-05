using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using G3MToolCLI.Models;
using G3MToolCLI.Utils;
using ImageMagick;
using Underanalyzer.Decompiler;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;

namespace G3MToolCLI.Services;

public class MergeOptions
{
    public string? OutputPath { get; set; }
    public string? ApplyPath { get; set; }
    public bool UseCodeMerge { get; set; }
    public bool UsePropertyMerge { get; set; }
    public string? ReportPath { get; set; }
    public G3MCacheOptions? CacheOptions { get; set; }
}

public class MergeResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? OutputPath { get; set; }
    public int TotalConflicts { get; set; }
    public int AutoMerged { get; set; }
}

public static partial class MergeService
{
    private const int DataFileBufferSize = 1024 * 1024;

    private static FileStream OpenDataReadStream(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, DataFileBufferSize, FileOptions.SequentialScan);

    private static FileStream OpenDataWriteStream(string path) =>
        new(path, FileMode.Create, FileAccess.Write, FileShare.None, DataFileBufferSize);

    private static readonly HashSet<string> s_codeSensitiveResourceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CodeEntries",
        "Scripts",
        "GlobalScripts",
        "Functions",
        "Variables",
        "GeneralInfo"
    };

    private static readonly HashSet<string> s_assetIndexSensitiveResourceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sprites",
        "Sounds",
        "Fonts",
        "Backgrounds",
        "Paths",
        "Shaders",
        "AudioGroups",
        "EmbeddedAudio",
        "TextureGroupInfo",
        "Tilesets",
        "TexturePageItems",
        "EmbeddedTextures",
        "Rooms",
        "GameObjects",
        "CodeEntries",
        "Scripts",
        "GlobalScripts",
        "Functions",
        "Variables"
    };

    private sealed record ConflictEntry(
        string File, string Status, string Strategy, string Winner, string? Details = null, string? Diff = null);

    private sealed record ResourceTouchKey(string ResourceType, string ResourceName);

    private sealed class PatchAssetRemaps
    {
        public Dictionary<int, int> Objects { get; } = [];
        public Dictionary<int, int> Sprites { get; } = [];
        public Dictionary<int, int> Sounds { get; } = [];
        public Dictionary<int, int> Backgrounds { get; } = [];
        public Dictionary<int, int> Paths { get; } = [];
        public Dictionary<int, int> Scripts { get; } = [];
        public Dictionary<int, int> Fonts { get; } = [];
        public Dictionary<int, int> Timelines { get; } = [];
        public Dictionary<int, int> Rooms { get; } = [];
        public Dictionary<int, int> Shaders { get; } = [];
        public Dictionary<int, int> Extensions { get; } = [];
        public Dictionary<int, int> AudioGroups { get; } = [];
        public HashSet<string> ShiftedAssetNames { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ShiftedAssetNameIndices { get; } = new(StringComparer.Ordinal);

        public bool HasAny =>
            Objects.Count > 0 || Sprites.Count > 0 || Sounds.Count > 0 ||
            Backgrounds.Count > 0 || Paths.Count > 0 || Scripts.Count > 0 ||
            Fonts.Count > 0 || Timelines.Count > 0 || Rooms.Count > 0 ||
            Shaders.Count > 0 || Extensions.Count > 0 || AudioGroups.Count > 0 ||
            ShiftedAssetNameIndices.Count > 0;
    }

    private static bool RequiresPatchConversion(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext != ".g3mpatch" && ext != ".zip";
    }

    private static async Task<G3MPatchManifest?> TryReadPatchManifestAsync(string patchPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(patchPath);
            var manifestEntry = archive.GetEntry("g3mpatch.json");
            if (manifestEntry == null)
                return null;

            await using var manifestStream = manifestEntry.Open();
            return await JsonSerializer.DeserializeAsync<G3MPatchManifest>(manifestStream);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsNoOpPatch(G3MPatchManifest? manifest)
    {
        var stats = manifest?.Statistics;
        if (stats == null)
            return false;

        return stats.TotalChanged == 0 &&
               stats.TotalNew == 0 &&
               stats.TotalDeleted == 0 &&
               stats.TotalChangedFiles == 0 &&
               stats.TotalNewFiles == 0;
    }

    private static bool ManifestTouchesCodeSensitiveResources(G3MPatchManifest? manifest)
    {
        if (manifest?.Resources == null)
            return false;

        foreach (var resourceType in s_codeSensitiveResourceTypes)
        {
            if (!manifest.Resources.TryGetValue(resourceType, out var resource))
                continue;
            if ((resource.Changed?.Count ?? 0) > 0 ||
                (resource.New?.Count ?? 0) > 0 ||
                (resource.Deleted?.Count ?? 0) > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SourceSupportsExactBase(string sourcePath)
    {
        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        return ext == ".xdelta" || DataFileExtensionUtil.IsDataFile(sourcePath);
    }

    private static int TryGetBestExactCodeBasePatchIndex(PatchFileSystem[] patches, string[] sourcePaths)
    {
        int best = -1;
        long bestScore = 0;

        for (int i = 0; i < patches.Length; i++)
        {
            if (!SourceSupportsExactBase(sourcePaths[i]) ||
                !ManifestTouchesCodeSensitiveResources(patches[i].Manifest))
            {
                continue;
            }

            var stats = patches[i].Manifest?.Statistics;
            long score =
                (stats?.TotalChanged ?? 0) +
                (stats?.TotalNew ?? 0) +
                (stats?.TotalDeleted ?? 0) +
                patches[i].GmlEntries.Count * 10L +
                patches[i].AsmEntries.Count * 10L;

            if (score > bestScore)
            {
                best = i;
                bestScore = score;
            }
        }

        return best;
    }

    private sealed class ExactCodeBaseFinalizeResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
    }

    private static async Task<bool> TryMaterializeExactBaseDataAsync(
        string originalPath,
        string sourceInputPath,
        string outputPath)
    {
        var outDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outDir))
            Directory.CreateDirectory(outDir);

        string ext = Path.GetExtension(sourceInputPath).ToLowerInvariant();
        if (DataFileExtensionUtil.IsDataFile(sourceInputPath))
        {
            File.Copy(sourceInputPath, outputPath, overwrite: true);
            string? sourceDir = Path.GetDirectoryName(sourceInputPath);
            if (!string.IsNullOrWhiteSpace(sourceDir) && Directory.Exists(sourceDir) &&
                !string.IsNullOrWhiteSpace(outDir))
            {
                foreach (var audioGroupPath in Directory.GetFiles(sourceDir, "audiogroup*.dat"))
                    File.Copy(audioGroupPath, Path.Combine(outDir, Path.GetFileName(audioGroupPath)), overwrite: true);
            }
            return true;
        }

        string? xdeltaPath = null;
        if (ext == ".xdelta")
        {
            xdeltaPath = sourceInputPath;
        }
        if (string.IsNullOrWhiteSpace(xdeltaPath))
            return false;

        var xdelta = new XDeltaService();
        var result = await xdelta.ApplyPatchAsync(originalPath, xdeltaPath, outputPath);
        return result.Success;
    }

    private static async Task<ExactCodeBaseFinalizeResult> TryFinalizeUsingExactCodeBaseAsync(
        string originalPath,
        string[] sourcePatchPaths,
        PatchFileSystem[] normalizedPatchFileSystems,
        int basePatchIndex,
        PatchFileSystem finalPfs,
        string zipOutputPath,
        bool saveZip,
        string? applyPath,
        Dictionary<string, Dictionary<string, string>>? sharedOriginalHashes,
        DataFileInfo? sharedOriginalInfo,
        Dictionary<string, Dictionary<string, int>>? sharedOriginalNameCounts,
        Dictionary<string, IReadOnlyList<string>>? sharedOriginalOrderedNames,
        G3MCacheOptions? cacheOptions,
        PatchAssetRemaps exactBaseAssetRemap,
        SoundAudioIdRemapResult soundAudioIdRemaps)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"g3mtool_merge_exact_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        string baseDataPath = Path.Combine(tempRoot, "base.win");
        string overlayPatchPath = Path.Combine(tempRoot, "overlay.g3mpatch");
        string mergedDataPath = string.IsNullOrWhiteSpace(applyPath)
            ? Path.Combine(tempRoot, "merged.win")
            : applyPath!;

        try
        {
            if (!await TryMaterializeExactBaseDataAsync(originalPath, sourcePatchPaths[basePatchIndex], baseDataPath))
            {
                return new ExactCodeBaseFinalizeResult
                {
                    Success = false,
                    Error = $"Merge failed: exact code-base patch '{Path.GetFileName(sourcePatchPaths[basePatchIndex])}' has no exact source"
                };
            }

            UndertaleData baseData;
            using (var baseStream = OpenDataReadStream(baseDataPath))
                baseData = UndertaleIO.Read(baseStream);

            var baseResourceNormalizationPfs = BuildExactBaseResourceNormalizationPatch(normalizedPatchFileSystems[basePatchIndex]);
            if (baseResourceNormalizationPfs.FileCount > 0)
            {
                string baseNormalizationPatchPath = Path.Combine(tempRoot, "base_normalize.g3mpatch");
                baseResourceNormalizationPfs.SaveToZip(baseNormalizationPatchPath, manifest: null);
                var baseNormalizationResult = await PatchService.ApplyPatchInMemoryAsync(baseData, baseNormalizationPatchPath, baseDataPath);
                if (!baseNormalizationResult.Success)
                {
                    return new ExactCodeBaseFinalizeResult
                    {
                        Success = false,
                        Error = $"Exact code-base resource normalization failed: {baseNormalizationResult.Error}"
                    };
                }
            }

            var overlayPfs = BuildOverlayAgainstExactCodeBase(
                finalPfs,
                normalizedPatchFileSystems[basePatchIndex],
                exactBaseAssetRemap,
                excludeCodeSensitiveResources: false);
            if (basePatchIndex == normalizedPatchFileSystems.Length - 1)
            {
                RemoveOverlayResourcesTouchedByBasePatch(
                    overlayPfs,
                    normalizedPatchFileSystems[basePatchIndex],
                    "Sprites",
                    "Sounds",
                    "Fonts",
                    "Backgrounds",
                    "Paths",
                    "Shaders",
                    "AudioGroups",
                    "TextureGroupInfo",
                    "Tilesets");

                foreach (string helperPath in overlayPfs.GetAllFilePaths().Where(path =>
                    path.EndsWith("texture_page_items.json", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("sprite_frame_map.json", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    overlayPfs.RemoveFile(helperPath);
                }

                foreach (string path in overlayPfs.GetAllFilePaths().Where(path =>
                    GetTopLevelFolder(path).Equals("TexturePageItems", StringComparison.OrdinalIgnoreCase) ||
                    GetTopLevelFolder(path).Equals("EmbeddedTextures", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    overlayPfs.RemoveFile(path);
                }
                overlayPfs.RebuildDirectoryIndex();
            }
            if (overlayPfs.FileCount > 0 || overlayPfs.GmlEntries.Count > 0 || overlayPfs.AsmEntries.Count > 0)
            {
                overlayPfs.SaveToZip(overlayPatchPath, manifest: null);
                var overlayResult = await PatchService.ApplyPatchInMemoryAsync(baseData, overlayPatchPath, baseDataPath);
                if (!overlayResult.Success)
                {
                    return new ExactCodeBaseFinalizeResult
                    {
                        Success = false,
                        Error = $"Exact code-base overlay apply failed: {overlayResult.Error}"
                    };
                }
            }

            RepairMergedEmbeddedSoundAudioSlots(baseData, finalPfs, normalizedPatchFileSystems, soundAudioIdRemaps.AffectedSoundNames);
            RepairExactMergedSoundPayloadsFromOwners(baseData, finalPfs, normalizedPatchFileSystems);

            var mergedDir = Path.GetDirectoryName(mergedDataPath);
            if (!string.IsNullOrWhiteSpace(mergedDir))
                Directory.CreateDirectory(mergedDir);

            using (var outStream = OpenDataWriteStream(mergedDataPath))
                UndertaleIO.Write(outStream, baseData);

            if (saveZip)
            {
                var createResult = await PatchService.CreatePatchAsync(
                    originalPath,
                    mergedDataPath,
                    zipOutputPath,
                    sharedOriginalHashes,
                    sharedOriginalInfo,
                    sharedOriginalNameCounts,
                    sharedOriginalOrderedNames,
                    baseData,
                    includeXdeltaFallback: false,
                    cacheOptions: cacheOptions);

                if (!createResult.Success)
                {
                    return new ExactCodeBaseFinalizeResult
                    {
                        Success = false,
                        Error = $"Failed to create merged patch from exact code-base output: {createResult.Error}"
                    };
                }
            }

            return new ExactCodeBaseFinalizeResult { Success = true };
        }
        finally
        {
            try
            {
                if (File.Exists(baseDataPath)) File.Delete(baseDataPath);
                if (File.Exists(overlayPatchPath)) File.Delete(overlayPatchPath);
                foreach (var sidecar in Directory.Exists(tempRoot) ? Directory.GetFiles(tempRoot, "audiogroup*.dat") : [])
                    File.Delete(sidecar);
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static PatchFileSystem BuildExactBaseResourceNormalizationPatch(PatchFileSystem basePatchPfs)
    {
        var normalization = new PatchFileSystem();
        var normalizedResourceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AudioGroups",
            "EmbeddedAudio",
            "EmbeddedImages",
            "EmbeddedTextures",
            "Fonts",
            "Sounds",
            "Sprites",
            "TextureGroupInfo",
            "TexturePageItems"
        };

        foreach (var (path, data) in basePatchPfs.GetAllFiles())
        {
            string resourceType = GetTopLevelFolder(path);
            bool isTextureHelper =
                path.EndsWith("texture_page_items.json", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("sprite_frame_map.json", StringComparison.OrdinalIgnoreCase);

            if (isTextureHelper || normalizedResourceTypes.Contains(resourceType))
                normalization.AddFile(path, data);
        }

        return normalization;
    }

    private static PatchFileSystem BuildOverlayAgainstExactCodeBase(
        PatchFileSystem finalPfs,
        PatchFileSystem basePatchPfs,
        PatchAssetRemaps? baseAssetRemap,
        bool excludeCodeSensitiveResources = true,
        bool excludeGlobalTextureWorld = true)
    {
        var overlay = new PatchFileSystem();

        foreach (var (path, data) in finalPfs.GetAllFiles())
        {
            string resourceType = GetTopLevelFolder(path);
            bool isHelper = path.StartsWith("Helpers/", StringComparison.OrdinalIgnoreCase) ||
                            path.StartsWith("AssetOrder/", StringComparison.OrdinalIgnoreCase);

            if (isHelper)
            {
                if (excludeGlobalTextureWorld &&
                    (path.EndsWith("texture_page_items.json", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith("sprite_frame_map.json", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (excludeCodeSensitiveResources &&
                    (path.EndsWith("variables_functions.json", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith("object_events.json", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                overlay.AddFile(path, data);
                continue;
            }

            if (excludeCodeSensitiveResources && s_codeSensitiveResourceTypes.Contains(resourceType))
                continue;

            if (excludeGlobalTextureWorld &&
                (resourceType.Equals("TexturePageItems", StringComparison.OrdinalIgnoreCase) ||
                 resourceType.Equals("EmbeddedTextures", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (basePatchPfs.TryGetFile(path, out var baseBytes) &&
                baseBytes.AsSpan().SequenceEqual(data))
            {
                continue;
            }

            overlay.AddFile(path, data);
        }

        if (!excludeCodeSensitiveResources)
        {
            foreach (var (entryKey, gml) in finalPfs.GmlEntries)
            {
                bool needsFinalContextCompile = baseAssetRemap != null && ContainsShiftedAssetName(gml, baseAssetRemap);
                if (basePatchPfs.GmlEntries.TryGetValue(entryKey, out var baseGml) &&
                    string.Equals(baseGml, gml, StringComparison.Ordinal) &&
                    !needsFinalContextCompile)
                {
                    continue;
                }

                overlay.AddGmlEntry(
                    entryKey,
                    gml,
                    finalPfs.CodeEntryLogicalNames.GetValueOrDefault(entryKey));
            }

            foreach (var (entryKey, asm) in finalPfs.AsmEntries)
            {
                if (overlay.GmlEntries.ContainsKey(entryKey))
                    continue;

                if (basePatchPfs.AsmEntries.TryGetValue(entryKey, out var baseAsm) &&
                    string.Equals(baseAsm, asm, StringComparison.Ordinal))
                {
                    continue;
                }

                overlay.AddAsmEntry(
                    entryKey,
                    asm,
                    finalPfs.AsmEntryPaths.GetValueOrDefault(entryKey),
                    finalPfs.CodeEntryLogicalNames.GetValueOrDefault(entryKey));
            }
        }

        return overlay;
    }

    private static void RemoveOverlayResourcesTouchedByBasePatch(
        PatchFileSystem overlay,
        PatchFileSystem basePatchPfs,
        params string[] resourceTypes)
    {
        var baseResources = basePatchPfs.Manifest?.Resources;
        if (baseResources == null)
            return;

        foreach (string resourceType in resourceTypes)
        {
            if (!baseResources.TryGetValue(resourceType, out var changes))
                continue;

            foreach (string resourceName in (changes.Changed ?? []).Select(c => c.Name)
                .Concat((changes.New ?? []).Select(c => c.Name))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList())
            {
                RemoveResourceFromPatch(overlay, resourceType, resourceName);
            }
        }
    }

    private static PatchFileSystem ClonePatchFileSystem(PatchFileSystem source)
    {
        var clone = new PatchFileSystem();
        foreach (var (path, data) in source.GetAllFiles())
            clone.AddFile(path, data);

        foreach (var (entryKey, gml) in source.GmlEntries)
            clone.AddGmlEntry(entryKey, gml, source.CodeEntryLogicalNames.GetValueOrDefault(entryKey));

        foreach (var (entryKey, asm) in source.AsmEntries)
            clone.AddAsmEntry(
                entryKey,
                asm,
                source.AsmEntryPaths.GetValueOrDefault(entryKey),
                source.CodeEntryLogicalNames.GetValueOrDefault(entryKey));

        return clone;
    }

    private static bool IsSingletonResourceType(string resourceType) =>
        resourceType.Equals("GeneralInfo", StringComparison.OrdinalIgnoreCase) ||
        resourceType.Equals("TexturePageItems", StringComparison.OrdinalIgnoreCase) ||
        resourceType.Equals("Variables", StringComparison.OrdinalIgnoreCase) ||
        resourceType.Equals("Functions", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldFilterOutTouch(HashSet<ResourceTouchKey> blockedTouches, string resourceType, string resourceName)
    {
        if (resourceType.Equals("TexturePageItems", StringComparison.OrdinalIgnoreCase))
            return false;

        return blockedTouches.Contains(new ResourceTouchKey(resourceType, resourceName));
    }

    private static bool ScriptDeletionConflictsWithBlockedCodeTouch(HashSet<ResourceTouchKey> blockedTouches, string scriptName)
    {
        foreach (var touch in blockedTouches)
        {
            if (!touch.ResourceType.Equals("CodeEntries", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(scriptName, touch.ResourceName, StringComparison.OrdinalIgnoreCase) ||
                scriptName.EndsWith("_" + touch.ResourceName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPathForResource(string path, string resourceType, string resourceName)
    {
        string prefix = resourceType + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        if (IsSingletonResourceType(resourceType))
            return true;

        string remainder = path[prefix.Length..];
        return remainder.StartsWith(resourceName + "/", StringComparison.OrdinalIgnoreCase) ||
               remainder.Equals(resourceName, StringComparison.OrdinalIgnoreCase) ||
               remainder.StartsWith(resourceName + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveResourceFromPatch(PatchFileSystem patch, string resourceType, string resourceName)
    {
        if (resourceType.Equals("CodeEntries", StringComparison.OrdinalIgnoreCase))
        {
            patch.RemoveGmlEntry(resourceName);
            patch.RemoveAsmEntry(resourceName);
        }

        foreach (var path in patch.GetAllFilePaths().Where(p => IsPathForResource(p, resourceType, resourceName)).ToList())
            patch.RemoveFile(path);
    }

    private static string? TryGetParentCodeNameFromAsmPath(PatchFileSystem patch, string entryKey)
    {
        var asmPath = patch.AsmEntryPaths.GetValueOrDefault(entryKey);
        if (string.IsNullOrWhiteSpace(asmPath))
            return null;

        var segments = asmPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3)
            return null;

        string parentName = segments[1];
        string logicalName = patch.CodeEntryLogicalNames.GetValueOrDefault(entryKey) ?? entryKey;
        if (string.Equals(parentName, logicalName, StringComparison.OrdinalIgnoreCase))
            return null;

        return parentName;
    }

    private static bool PatchHasCodeEntryLogicalName(PatchFileSystem patch, string logicalName)
    {
        foreach (var name in patch.CodeEntryLogicalNames.Values)
        {
            if (string.Equals(name, logicalName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return patch.GmlEntries.ContainsKey(logicalName) || patch.AsmEntries.ContainsKey(logicalName);
    }

    private static void RemoveOrphanChildCodeEntries(PatchFileSystem patch, G3MPatchManifest? manifest)
    {
        var orphanKeys = new List<string>();
        foreach (var entryKey in patch.CodeEntryLogicalNames.Keys.ToList())
        {
            string? parentName = TryGetParentCodeNameFromAsmPath(patch, entryKey);
            if (string.IsNullOrWhiteSpace(parentName))
                continue;

            if (!PatchHasCodeEntryLogicalName(patch, parentName))
                orphanKeys.Add(entryKey);
        }

        if (orphanKeys.Count == 0)
            return;

        foreach (var entryKey in orphanKeys)
        {
            string logicalName = patch.CodeEntryLogicalNames.GetValueOrDefault(entryKey) ?? entryKey;
            RemoveResourceFromPatch(patch, "CodeEntries", entryKey);
            RemoveResourceFromPatch(patch, "Scripts", logicalName);

            if (manifest?.Resources != null)
            {
                if (manifest.Resources.TryGetValue("CodeEntries", out var codeChanges))
                {
                    codeChanges.Changed?.RemoveAll(c => string.Equals(c.Name, logicalName, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Name, entryKey, StringComparison.OrdinalIgnoreCase));
                    codeChanges.New?.RemoveAll(c => string.Equals(c.Name, logicalName, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Name, entryKey, StringComparison.OrdinalIgnoreCase));
                }

                if (manifest.Resources.TryGetValue("Scripts", out var scriptChanges))
                {
                    scriptChanges.Changed?.RemoveAll(c => string.Equals(c.Name, logicalName, StringComparison.OrdinalIgnoreCase));
                    scriptChanges.New?.RemoveAll(c => string.Equals(c.Name, logicalName, StringComparison.OrdinalIgnoreCase));
                    scriptChanges.Deleted?.RemoveAll(d => string.Equals(d, logicalName, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
    }

    private static void PruneVariablesFunctionsHelper(PatchFileSystem patch)
    {
        string vfPath = $"{patch.HelpersPrefix}/variables_functions.json";
        if (!patch.TryGetFile(vfPath, out var bytes))
            return;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(bytes);
        }
        catch
        {
            return;
        }

        if (parsed is not JsonObject root || root["codeEntries"] is not JsonArray codeEntries)
            return;

        var remainingArchiveKeys = new HashSet<string>(patch.CodeEntryLogicalNames.Keys, StringComparer.OrdinalIgnoreCase);
        var remainingLogicalNames = new HashSet<string>(patch.CodeEntryLogicalNames.Values, StringComparer.OrdinalIgnoreCase);

        var filtered = new JsonArray();
        foreach (var node in codeEntries)
        {
            if (node is not JsonObject obj)
                continue;

            string? archiveKey = obj["key"]?.GetValue<string>();
            string? logicalName = obj["name"]?.GetValue<string>();
            string? parentName = obj["parent"]?.GetValue<string>();

            bool keep =
                (!string.IsNullOrWhiteSpace(archiveKey) && remainingArchiveKeys.Contains(archiveKey)) ||
                (!string.IsNullOrWhiteSpace(logicalName) && remainingLogicalNames.Contains(logicalName));

            if (!keep)
                continue;

            if (!string.IsNullOrWhiteSpace(parentName) && !remainingLogicalNames.Contains(parentName))
                continue;

            filtered.Add(obj.DeepClone());
        }

        root["codeEntries"] = filtered;
        patch.AddTextFile(vfPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RemoveAllDeletions(G3MPatchManifest? manifest, params string[] resourceTypes)
    {
        if (manifest?.Resources == null)
            return;

        foreach (string resourceType in resourceTypes)
        {
            if (manifest.Resources.TryGetValue(resourceType, out var changes))
                changes.Deleted?.Clear();
        }
    }

    private static void RemoveScriptEntriesMissingCode(
        PatchFileSystem patch,
        G3MPatchManifest? manifest,
        HashSet<string> availableCodeNames)
    {
        if (manifest?.Resources == null || !manifest.Resources.TryGetValue("Scripts", out var scriptChanges))
            return;

        var removeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in (scriptChanges.Changed ?? []).Concat(scriptChanges.New ?? []))
        {
            if (!string.IsNullOrWhiteSpace(change.Name) && !availableCodeNames.Contains(change.Name))
                removeNames.Add(change.Name);
        }

        foreach (string name in removeNames)
            RemoveResourceFromPatch(patch, "Scripts", name);

        scriptChanges.Changed?.RemoveAll(c => !string.IsNullOrWhiteSpace(c.Name) && removeNames.Contains(c.Name));
        scriptChanges.New?.RemoveAll(c => !string.IsNullOrWhiteSpace(c.Name) && removeNames.Contains(c.Name));
        scriptChanges.Deleted?.RemoveAll(d => !string.IsNullOrWhiteSpace(d) && removeNames.Contains(d));
    }

    private static void SanitizeRoomCodeReferences(
        PatchFileSystem patch,
        HashSet<string> availableCodeNames)
    {
        bool IsMissing(string? codeName) =>
            !string.IsNullOrWhiteSpace(codeName) && !availableCodeNames.Contains(codeName);

        foreach (string path in patch.GetAllFilePaths()
            .Where(p => p.StartsWith("Rooms/", StringComparison.OrdinalIgnoreCase) &&
                        p.EndsWith("/room.json", StringComparison.OrdinalIgnoreCase))
            .ToList())
        {
            if (!patch.TryGetFile(path, out var bytes))
                continue;

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(bytes);
            }
            catch
            {
                continue;
            }

            if (parsed is not JsonObject roomObj)
                continue;

            bool changed = false;

            string? roomCreationCode = roomObj["creationCodeId"]?.GetValue<string>();
            if (IsMissing(roomCreationCode))
            {
                roomObj["creationCodeId"] = "";
                changed = true;
            }

            if (roomObj["gameObjects"] is JsonArray gameObjects)
            {
                foreach (var node in gameObjects)
                {
                    if (node is not JsonObject gameObject)
                        continue;

                    string? creationCode = gameObject["creationCode"]?.GetValue<string>();
                    if (IsMissing(creationCode))
                    {
                        gameObject["creationCode"] = "";
                        changed = true;
                    }

                    string? preCreateCode = gameObject["preCreateCode"]?.GetValue<string>();
                    if (IsMissing(preCreateCode))
                    {
                        gameObject["preCreateCode"] = "";
                        changed = true;
                    }
                }
            }

            if (changed)
                patch.AddTextFile(path, roomObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void SanitizeLowerPriorityExactOverlay(PatchFileSystem patch, G3MPatchManifest? manifest, UndertaleData baseData)
    {
        RemoveAllDeletions(manifest, "Scripts", "CodeEntries");
        var availableCodeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in baseData.Code)
        {
            var codeName = code?.Name?.Content;
            if (!string.IsNullOrWhiteSpace(codeName))
                availableCodeNames.Add(codeName);
        }

        foreach (var logicalName in patch.CodeEntryLogicalNames.Values)
        {
            if (!string.IsNullOrWhiteSpace(logicalName))
                availableCodeNames.Add(logicalName);
        }

        RemoveScriptEntriesMissingCode(patch, manifest, availableCodeNames);
        PruneVariablesFunctionsHelper(patch);
        SanitizeRoomCodeReferences(patch, availableCodeNames);
    }

    private static void StripRuntimeSensitiveResourcesFromOverlay(PatchFileSystem patch, G3MPatchManifest? manifest)
    {
        string[] resourceTypes =
        [
            "CodeEntries",
            "Scripts",
            "GlobalScripts",
            "Functions",
            "Variables",
            "Rooms",
            "GameObjects"
        ];

        foreach (string resourceType in resourceTypes)
        {
            foreach (string path in patch.GetAllFilePaths()
                .Where(p => GetTopLevelFolder(p).Equals(resourceType, StringComparison.OrdinalIgnoreCase))
                .ToList())
            {
                patch.RemoveFile(path);
            }

            if (resourceType.Equals("CodeEntries", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string entryKey in patch.GmlEntries.Keys.ToList())
                    patch.RemoveGmlEntry(entryKey);
                foreach (string entryKey in patch.AsmEntries.Keys.ToList())
                    patch.RemoveAsmEntry(entryKey);
            }

            if (manifest?.Resources != null)
                manifest.Resources.Remove(resourceType);
        }

        foreach (string helperPath in patch.GetAllFilePaths().Where(path =>
            path.EndsWith("variables_functions.json", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("object_events.json", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            patch.RemoveFile(helperPath);
        }
    }

    private static void StripAssetIndexSensitiveResourcesFromOverlay(PatchFileSystem patch, G3MPatchManifest? manifest)
    {
        foreach (string resourceType in s_assetIndexSensitiveResourceTypes)
        {
            foreach (string path in patch.GetAllFilePaths()
                .Where(p => GetTopLevelFolder(p).Equals(resourceType, StringComparison.OrdinalIgnoreCase))
                .ToList())
            {
                patch.RemoveFile(path);
            }

            if (resourceType.Equals("CodeEntries", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string entryKey in patch.GmlEntries.Keys.ToList())
                    patch.RemoveGmlEntry(entryKey);
                foreach (string entryKey in patch.AsmEntries.Keys.ToList())
                    patch.RemoveAsmEntry(entryKey);
            }

            manifest?.Resources?.Remove(resourceType);
        }

        foreach (string helperPath in patch.GetAllFilePaths().Where(path =>
            path.EndsWith("variables_functions.json", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("object_events.json", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("texture_page_items.json", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("sprite_frame_map.json", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            patch.RemoveFile(helperPath);
        }
    }

    private static HashSet<ResourceTouchKey> BuildHigherPriorityTouches(PatchFileSystem[] patches, int patchIndexExclusive)
    {
        var touches = new HashSet<ResourceTouchKey>();
        for (int i = patchIndexExclusive + 1; i < patches.Length; i++)
        {
            var resources = patches[i].Manifest?.Resources;
            if (resources == null)
                continue;

            foreach (var (resourceType, changes) in resources)
            {
                foreach (var changed in changes.Changed ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(changed.Name))
                        touches.Add(new ResourceTouchKey(resourceType, changed.Name));
                }

                foreach (var added in changes.New ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(added.Name))
                        touches.Add(new ResourceTouchKey(resourceType, added.Name));
                }

                foreach (var deleted in changes.Deleted ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(deleted))
                        touches.Add(new ResourceTouchKey(resourceType, deleted));
                }
            }
        }

        return touches;
    }

    private static G3MPatchManifest? BuildFilteredManifest(
        G3MPatchManifest? sourceManifest,
        HashSet<ResourceTouchKey> blockedTouches)
    {
        if (sourceManifest == null)
            return null;

        var resources = new Dictionary<string, ResourceTypeChanges>(StringComparer.OrdinalIgnoreCase);
        var stats = new PatchStatistics();

        foreach (var (resourceType, changes) in sourceManifest.Resources ?? [])
        {
            var filtered = new ResourceTypeChanges
            {
                Changed = [],
                New = [],
                Deleted = []
            };

            foreach (var changed in changes.Changed ?? [])
            {
                if (!string.IsNullOrWhiteSpace(changed.Name) &&
                    !ShouldFilterOutTouch(blockedTouches, resourceType, changed.Name))
                {
                    filtered.Changed!.Add(changed);
                }
            }

            foreach (var added in changes.New ?? [])
            {
                if (!string.IsNullOrWhiteSpace(added.Name) &&
                    !ShouldFilterOutTouch(blockedTouches, resourceType, added.Name))
                {
                    filtered.New!.Add(added);
                }
            }

            foreach (var deleted in changes.Deleted ?? [])
            {
                if (!string.IsNullOrWhiteSpace(deleted) &&
                    !ShouldFilterOutTouch(blockedTouches, resourceType, deleted) &&
                    !(resourceType.Equals("Scripts", StringComparison.OrdinalIgnoreCase) &&
                      ScriptDeletionConflictsWithBlockedCodeTouch(blockedTouches, deleted)))
                {
                    filtered.Deleted!.Add(deleted);
                }
            }

            if (!filtered.HasChanges)
                continue;

            resources[resourceType] = filtered;
            stats.TotalChanged += filtered.Changed?.Count ?? 0;
            stats.TotalNew += filtered.New?.Count ?? 0;
            stats.TotalDeleted += filtered.Deleted?.Count ?? 0;
        }

        return new G3MPatchManifest
        {
            CreatedAt = sourceManifest.CreatedAt,
            Tool = sourceManifest.Tool,
            Original = sourceManifest.Original,
            Modified = sourceManifest.Modified,
            Resources = resources,
            Statistics = stats,
            ApplyPlan = PatchService.BuildPatchApplyPlan(resources)
        };
    }

    private static (PatchFileSystem Patch, G3MPatchManifest? Manifest) BuildConflictFilteredPatch(
        PatchFileSystem sourcePatch,
        HashSet<ResourceTouchKey> blockedTouches)
    {
        var filteredPatch = ClonePatchFileSystem(sourcePatch);
        var filteredManifest = BuildFilteredManifest(sourcePatch.Manifest, blockedTouches);

        if (sourcePatch.Manifest?.Resources != null)
        {
            foreach (var (resourceType, changes) in sourcePatch.Manifest.Resources)
            {
                foreach (var changed in changes.Changed ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(changed.Name) &&
                        ShouldFilterOutTouch(blockedTouches, resourceType, changed.Name))
                    {
                        RemoveResourceFromPatch(filteredPatch, resourceType, changed.Name);
                    }
                }

                foreach (var added in changes.New ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(added.Name) &&
                        ShouldFilterOutTouch(blockedTouches, resourceType, added.Name))
                    {
                        RemoveResourceFromPatch(filteredPatch, resourceType, added.Name);
                    }
                }
            }
        }

        PruneVariablesFunctionsHelper(filteredPatch);
        filteredPatch.RebuildDirectoryIndex();
        return (filteredPatch, filteredManifest);
    }

    private static void StripGlobalTextureWorldFromPatch(PatchFileSystem patch, G3MPatchManifest? manifest)
    {
        foreach (var path in patch.GetAllFilePaths().ToList())
        {
            if (path.StartsWith("TexturePageItems/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("EmbeddedTextures/", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("texture_page_items.json", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("sprite_frame_map.json", StringComparison.OrdinalIgnoreCase))
            {
                patch.RemoveFile(path);
            }
        }

        if (manifest?.Resources != null)
        {
            manifest.Resources.Remove("TexturePageItems");
            manifest.Resources.Remove("EmbeddedTextures");
        }

        patch.RebuildDirectoryIndex();
    }

    private static string GetTopLevelFolder(string path)
    {
        int slash = path.IndexOf('/');
        return slash >= 0 ? path[..slash] : path;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Main Pipeline
    // ═══════════════════════════════════════════════════════════════════

    public static async Task<MergeResult> MergePatchesAsync(
        string originalPath,
        List<string> patchPaths,
        MergeOptions options)
    {
        if (!File.Exists(originalPath))
            return new MergeResult { Success = false, Error = $"Original file not found: {originalPath}" };

        foreach (var p in patchPaths)
        {
            if (!File.Exists(p))
                return new MergeResult { Success = false, Error = $"Patch not found: {p}" };
        }

        if (patchPaths.Count < 2)
            return new MergeResult { Success = false, Error = "At least 2 patches are required for merge" };

        // Determine output paths
        bool saveZip = options.OutputPath != null || options.ApplyPath == null;
        bool applyData = options.ApplyPath != null;
        string zipOutputPath;
        if (options.OutputPath != null)
            zipOutputPath = options.OutputPath;
        else
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            zipOutputPath = Path.Combine(PlatformUtil.GetExecutableDirectory(), $"merged_{timestamp}.g3mpatch");
        }

        var totalSw = Stopwatch.StartNew();
        var stepSw = new Stopwatch();
        var conflicts = new List<ConflictEntry>();
        var tempFiles = new List<string>();

        // Progress: 0-5 load original, 5-15 normalize patches, 15-65 merge loop,
        //           65-75 helpers, 75-80 manifest, 80-88 save patch, 88-98 apply, 98-100 report
        var patchNames = BuildPatchNames(patchPaths);

        // Initial display (always visible)
        LogService.Info($"Merge: {patchPaths.Count} patches with original {Path.GetFileName(originalPath)}");
        for (int i = 0; i < patchPaths.Count; i++)
            LogService.Info($"  [{i + 1}] {patchNames[i]}");

        LogService.SetOperation("Merging");
        LogService.Progress(0, 100);

        try
        {
            // ══════════════════════════════════════════════════════════
            // Phase 1: Load original
            // ══════════════════════════════════════════════════════════
            stepSw.Restart();
            LogService.Progress(1, 100);

            UndertaleData originalData;
            using (var stream = OpenDataReadStream(originalPath))
                originalData = UndertaleIO.Read(stream);

            var originalAssetOrder = ExtractAssetOrder(originalData);
            int originalEmbeddedTextureCount = originalData.EmbeddedTextures?.Count ?? 0;
            int originalTpiCount = originalData.TexturePageItems?.Count ?? 0;

            // Pre-compute original hashes + info only when raw data/xdelta inputs need conversion.
            var origHashSw = Stopwatch.StartNew();
            bool needsPatchConversion = patchPaths.Any(RequiresPatchConversion);
            Dictionary<string, Dictionary<string, string>>? sharedOriginalHashes = null;
            DataFileInfo? sharedOriginalInfo = null;
            Dictionary<string, Dictionary<string, int>>? sharedOriginalNameCounts = null;
            Dictionary<string, IReadOnlyList<string>>? sharedOriginalOrderedNames = null;
            if (needsPatchConversion)
            {
                var cachedOriginal = G3MCacheService.TryReadDataCache(originalPath, options.CacheOptions);
                if (cachedOriginal != null)
                {
                    sharedOriginalHashes = cachedOriginal.ResourceHashes;
                    sharedOriginalNameCounts = cachedOriginal.ResourceNameCounts;
                    sharedOriginalOrderedNames = G3MCacheService.ToReadOnlyOrderNames(cachedOriginal.OrderSensitiveNames);
                    sharedOriginalInfo = cachedOriginal.DataInfo;
                }
                else
                {
                    sharedOriginalHashes = ResourceHashService.HashAll(originalData);
                    sharedOriginalNameCounts = PatchService.GetResourceNameCountsForReuse(originalData);
                    sharedOriginalOrderedNames = PatchService.GetOrderSensitiveResourceNamesForReuse(originalData);
                    sharedOriginalInfo = new DataFileInfo
                    {
                        Filename = Path.GetFileName(originalPath),
                        Size = new FileInfo(originalPath).Length,
                        Md5 = await HashService.ComputeFileHashAsync(originalPath),
                        BytecodeVersion = originalData.GeneralInfo?.BytecodeVersion ?? 0,
                        GmsVersion = GeneralInfoUtil.GetVersionDisplay(originalData.GeneralInfo),
                        GeneralInfo = GeneralInfoUtil.ExtractGeneralInfo(originalData)
                    };
                    await G3MCacheService.WriteDataCacheAsync(originalPath, sharedOriginalInfo, sharedOriginalHashes, sharedOriginalNameCounts, sharedOriginalOrderedNames, options.CacheOptions);
                }
            }

            LogService.Log($"[Bench] Load original: {stepSw.Elapsed.TotalSeconds:F2}s " +
                $"(prep hash: {origHashSw.Elapsed.TotalSeconds:F2}s, convert inputs: {needsPatchConversion}, {originalData.GeneralInfo?.DisplayName?.Content ?? "?"})");
            LogService.Progress(5, 100);

            // ══════════════════════════════════════════════════════════
            // Phase 2: Normalize patches to PFS (parallel)
            // ══════════════════════════════════════════════════════════
            stepSw.Restart();
            var patchFileSystems = new PatchFileSystem[patchPaths.Count];

            // Suppress sub-operation output during parallel normalization
            if (!LogService.Verbose) LogService.Suppress = true;

            // Limit concurrent normalizations to avoid overloading the system
            const int maxConcurrent = 5;
            using var normSemaphore = new SemaphoreSlim(maxConcurrent);
            var normalizeTasks = new Task<string>[patchPaths.Count];
            for (int i = 0; i < patchPaths.Count; i++)
            {
                var idx = i;
                var path = patchPaths[idx];
                normalizeTasks[idx] = Task.Run(async () =>
                {
                    await normSemaphore.WaitAsync();
                    try { return await PatchService.EnsureG3MPatchAsync(originalPath, path, null, sharedOriginalHashes, sharedOriginalInfo, sharedOriginalNameCounts, sharedOriginalOrderedNames, options.CacheOptions); }
                    finally { normSemaphore.Release(); }
                });
            }

            var zipPaths = await Task.WhenAll(normalizeTasks);

            LogService.Suppress = false;
            LogService.SetOperation("Merging");

            for (int i = 0; i < patchPaths.Count; i++)
            {
                if (zipPaths[i] != patchPaths[i]) tempFiles.Add(zipPaths[i]);
                patchFileSystems[i] = PatchFileSystem.LoadFromZip(zipPaths[i], loadExactPayloads: false);
                LogService.Log($"[Bench] Patch {i + 1}/{patchPaths.Count} ({patchNames[i]}): " +
                    $"{patchFileSystems[i].FileCount} files + {patchFileSystems[i].GmlEntries.Count} GML + {patchFileSystems[i].AsmEntries.Count} ASM");
            }

            var resourceTouchCounts = BuildResourceTouchCounts(patchFileSystems);

            LogService.Log($"[Bench] All patches normalized (parallel): {stepSw.Elapsed.TotalSeconds:F2}s");
            LogService.Progress(15, 100);

            // Prepare decompiler for 3-way code merge
            var decompileCache = new Dictionary<string, string?>();
            GlobalDecompileContext decompileCtx = new(originalData);

            // ══════════════════════════════════════════════════════════
            // Pre-compute asset index remaps (patch order → merged order)
            // so hardcoded numeric asset ids in GML/ASM code are corrected.
            // ══════════════════════════════════════════════════════════
            var patchAssetOrders = LoadPatchAssetOrders(patchFileSystems);
            var patchAssetRemaps = BuildPatchAssetRemaps(originalAssetOrder, patchAssetOrders, patchNames);
            var originalAssetRemap = BuildOriginalAssetRemap(originalAssetOrder, patchAssetOrders);

            // ══════════════════════════════════════════════════════════
            // Phase 3: Merge loop (low priority → high priority)
            // ══════════════════════════════════════════════════════════
            stepSw.Restart();
            var finalPfs = new PatchFileSystem();
            // Track which patch last wrote each file/code entry (for report)
            var fileOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var codeOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int pi = 0; pi < patchFileSystems.Length; pi++)
            {
                var patchStepSw = Stopwatch.StartNew();
                var pfs = patchFileSystems[pi];
                var patchName = patchNames[pi];
                int patchPriority = pi + 1;
                var assetRemap = patchAssetRemaps[pi];

                // Sub-progress range for this patch within 15-65
                int patchProgressStart = 15 + pi * 50 / patchFileSystems.Length;
                int patchProgressEnd = 15 + (pi + 1) * 50 / patchFileSystems.Length;

                // ─── Merge regular files ───
                var allFiles = pfs.GetAllFiles();
                int fileIdx = 0;
                int totalFiles = allFiles.Count;
                int filesAdded = 0, filesConflict = 0;

                foreach (var (filePath, fileData) in allFiles)
                {
                    fileIdx++;
                    if (fileIdx % 50 == 0)
                    {
                        int subPct = patchProgressStart + fileIdx * (patchProgressEnd - patchProgressStart) / 2 / Math.Max(totalFiles, 1);
                        LogService.Progress(subPct, 100);
                    }

                    if (filePath.StartsWith("Helpers/", StringComparison.OrdinalIgnoreCase) ||
                        filePath.StartsWith("AssetOrder/", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (filePath.Equals("g3mpatch.json", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!finalPfs.TryGetFile(filePath, out var existingData))
                    {
                        finalPfs.AddFile(filePath, fileData);
                        fileOwner[filePath] = patchName;
                        filesAdded++;
                        continue;
                    }

                    if (existingData.AsSpan().SequenceEqual(fileData))
                        continue;

                    if (ShouldKeepExistingLanguageSprite(filePath))
                    {
                        var prevOwner = fileOwner.GetValueOrDefault(filePath, "?");
                        conflicts.Add(new ConflictEntry(filePath, "Resolved", "Language Sprite Preservation",
                            prevOwner,
                            "Kept lower-priority language sprite payload; higher-priority patch supplied a conflicting ja/lang sprite file that would revert translated UI art."));
                        continue;
                    }

                    if (ShouldKeepExistingFontCoverage(filePath, finalPfs, pfs, originalData, out var fontDetails))
                    {
                        var prevOwner = fileOwner.GetValueOrDefault(filePath, "?");
                        conflicts.Add(new ConflictEntry(filePath, "Resolved", "Font Coverage Preservation",
                            prevOwner,
                            fontDetails));
                        continue;
                    }

                    if (options.UsePropertyMerge && filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var merged = TryThreeWayJsonMerge(filePath, originalData, existingData, fileData)
                                ?? JsonDeepMerge(existingData, fileData);
                            finalPfs.AddFile(filePath, merged);
                            var prevOwner = fileOwner.GetValueOrDefault(filePath, "?");
                            fileOwner[filePath] = $"{prevOwner} + {patchName}";
                            conflicts.Add(new ConflictEntry(filePath, "Resolved", "Properties Merge",
                                $"{prevOwner} + {patchName}",
                                DescribeJsonMerge(filePath, originalData, existingData, fileData, merged)));
                            continue;
                        }
                        catch { /* fall through to overwrite */ }
                    }

                    if (TryResolveSpriteFrameConflict(filePath, originalData, existingData, fileData, out var chosenFrame, out var frameDetails))
                    {
                        finalPfs.AddFile(filePath, chosenFrame);
                        var prevOwner = fileOwner.GetValueOrDefault(filePath, "?");
                        fileOwner[filePath] = $"{prevOwner} + {patchName}";
                        conflicts.Add(new ConflictEntry(filePath, "Resolved", "Sprite Frame Merge",
                            $"{prevOwner} + {patchName}", frameDetails));
                        continue;
                    }

                    var prevFileOwner = fileOwner.GetValueOrDefault(filePath, "Original");
                    finalPfs.AddFile(filePath, fileData);
                    fileOwner[filePath] = patchName;
                    filesConflict++;
                    conflicts.Add(new ConflictEntry(filePath, "Conflict", "Overwrite",
                        $"{patchName} over {prevFileOwner}",
                        DescribeFileConflict(filePath, originalData, existingData, fileData)));
                }

                int midProgress = (patchProgressStart + patchProgressEnd) / 2;
                LogService.Progress(midProgress, 100);

                // ─── Merge code entries (GML + ASM) ───
                int codeIdx = 0;
                int totalCode = pfs.GmlEntries.Count;
                int codeAdded = 0, codeConflict = 0, codeMerged = 0;

                foreach (var (codeName, rawGmlCode) in pfs.GmlEntries)
                {
                    string logicalCodeName = pfs.CodeEntryLogicalNames.GetValueOrDefault(codeName) ?? codeName;
                    var gmlCode = RemapAssetIndicesGml(rawGmlCode, assetRemap);
                    bool canPreserveAsm = false;
                    codeIdx++;
                    if (codeIdx % 100 == 0)
                    {
                        int subPct = midProgress + codeIdx * (patchProgressEnd - midProgress) / 2 / Math.Max(totalCode, 1);
                        LogService.Progress(subPct, 100);
                    }

                    var originalGmlForCode =
                        GetOriginalGml(originalData, decompileCtx!, decompileCache, codeName)
                        ?? GetOriginalGml(originalData, decompileCtx!, decompileCache, logicalCodeName);
                    bool incomingIsBase = originalGmlForCode != null &&
                        (GmlTextEquals(rawGmlCode, originalGmlForCode) ||
                         GmlTextEquals(gmlCode, originalGmlForCode));
                    bool baseNeedsAssetRecompile = incomingIsBase &&
                        originalGmlForCode != null &&
                        ContainsShiftedAssetName(originalGmlForCode, originalAssetRemap);
                    if (baseNeedsAssetRecompile)
                    {
                        gmlCode = RemapAssetIndicesGml(originalGmlForCode!, originalAssetRemap);
                    }

                    if (incomingIsBase && !baseNeedsAssetRecompile && !finalPfs.GmlEntries.ContainsKey(codeName))
                    {
                        continue;
                    }

                    if (!finalPfs.GmlEntries.TryGetValue(codeName, out var existingGml))
                    {
                        finalPfs.AddGmlEntry(codeName, gmlCode, logicalCodeName);
                        if (canPreserveAsm && pfs.AsmEntries.TryGetValue(codeName, out var asmCode))
                            finalPfs.AddAsmEntry(
                                codeName,
                                RemapAssetIndicesAsm(asmCode, assetRemap),
                                pfs.AsmEntryPaths.GetValueOrDefault(codeName),
                                logicalCodeName);
                        else
                            finalPfs.RemoveAsmEntry(codeName);
                        codeOwner[codeName] = patchName;
                        codeAdded++;
                        continue;
                    }

                    if (existingGml == gmlCode) continue;

                    if (originalGmlForCode != null)
                    {
                        bool rawIncomingIsBase = incomingIsBase;
                        bool existingIsBase = GmlTextEquals(existingGml, originalGmlForCode);

                        if (rawIncomingIsBase &&
                            !GmlTextEquals(existingGml, originalGmlForCode))
                        {
                            continue;
                        }

                        if (existingIsBase && !rawIncomingIsBase)
                        {
                            finalPfs.AddGmlEntry(codeName, gmlCode, logicalCodeName);
                            if (canPreserveAsm && pfs.AsmEntries.TryGetValue(codeName, out var incomingAsm))
                                finalPfs.AddAsmEntry(
                                    codeName,
                                    RemapAssetIndicesAsm(incomingAsm, assetRemap),
                                    pfs.AsmEntryPaths.GetValueOrDefault(codeName),
                                    logicalCodeName);
                            else
                                finalPfs.RemoveAsmEntry(codeName);
                            codeOwner[codeName] = patchName;
                            codeAdded++;
                            continue;
                        }
                    }

                    if (options.UseCodeMerge)
                    {
                        var baseGml = originalGmlForCode ?? GetOriginalGml(originalData, decompileCtx!, decompileCache, codeName);
                        if (baseGml == null && !string.Equals(logicalCodeName, codeName, StringComparison.Ordinal))
                            baseGml = GetOriginalGml(originalData, decompileCtx!, decompileCache, logicalCodeName);
                        if (baseGml != null)
                        {
                            var (merged, hasConflicts) = ThreeWayMerge(baseGml, existingGml, gmlCode);
                            if (merged != null)
                            {
                                finalPfs.AddGmlEntry(codeName, merged, logicalCodeName);
                                finalPfs.RemoveAsmEntry(codeName);
                                codeMerged++;
                                var prevCodeOwner = codeOwner.GetValueOrDefault(codeName, "?");
                                codeOwner[codeName] = $"{prevCodeOwner} + {patchName}";
                                var mergeDiff = GenerateThreeWayDiff(baseGml, merged, prevCodeOwner, patchName);
                                conflicts.Add(new ConflictEntry(
                                    $"CodeEntries/{codeName}",
                                    hasConflicts ? "Conflict" : "Resolved",
                                    hasConflicts ? "Code Merge (partial)" : "Code Merge",
                                    hasConflicts
                                        ? $"{patchName} over {prevCodeOwner}"
                                        : $"{prevCodeOwner} + {patchName}",
                                    hasConflicts ? "Overlapping changes resolved with higher priority" : null,
                                    mergeDiff));
                                continue;
                            }
                        }
                    }

                    var prevCodeOwner2 = codeOwner.GetValueOrDefault(codeName, "Original");
                    finalPfs.AddGmlEntry(codeName, gmlCode, logicalCodeName);
                    if (canPreserveAsm && pfs.AsmEntries.TryGetValue(codeName, out var newAsm))
                        finalPfs.AddAsmEntry(
                            codeName,
                            RemapAssetIndicesAsm(newAsm, assetRemap),
                            pfs.AsmEntryPaths.GetValueOrDefault(codeName),
                            logicalCodeName);
                    else
                        finalPfs.RemoveAsmEntry(codeName);
                    codeOwner[codeName] = patchName;
                    codeConflict++;
                    conflicts.Add(new ConflictEntry(
                        $"CodeEntries/{codeName}", "Conflict", "Overwrite",
                        $"{patchName} over {prevCodeOwner2}",
                        null,
                        GenerateUnifiedDiff(existingGml, gmlCode, prevCodeOwner2, patchName)));
                }

                // ASM-only entries have no GML source to recompile, so keep
                // them. Paired ASM for GML entries stays disabled above to
                // avoid overwriting freshly compiled bytecode with source
                // VARI/FUNC indices from another data file.
                foreach (var (codeName, asmCode) in pfs.AsmEntries)
                {
                    if (!pfs.GmlEntries.ContainsKey(codeName) &&
                        !finalPfs.GmlEntries.ContainsKey(codeName) &&
                        !finalPfs.AsmEntries.ContainsKey(codeName))
                    {
                        string logicalCodeName = pfs.CodeEntryLogicalNames.GetValueOrDefault(codeName) ?? codeName;
                        finalPfs.AddAsmEntry(
                            codeName,
                            RemapAssetIndicesAsm(asmCode, assetRemap),
                            pfs.AsmEntryPaths.GetValueOrDefault(codeName),
                            logicalCodeName);
                    }
                }

                PropagateDeletions(pfs, patchName, conflicts, resourceTouchCounts);

                LogService.Progress(patchProgressEnd, 100);
                LogService.Log($"[Bench] Patch {patchPriority}/{patchFileSystems.Length} ({patchName}): {patchStepSw.Elapsed.TotalSeconds:F2}s " +
                    $"(+{filesAdded} files, {filesConflict} file conflicts, +{codeAdded} code, {codeConflict} code conflicts, {codeMerged} code merged)");
            }

            LogService.Log($"[Bench] Merge loop total: {stepSw.Elapsed.TotalSeconds:F2}s, {conflicts.Count} conflict entries");
            LogService.Progress(65, 100);

            int addedBaseAssetRecompile = AddOriginalCodeEntriesForShiftedAssets(
                originalData,
                decompileCtx,
                decompileCache,
                originalAssetRemap,
                finalPfs);
            if (addedBaseAssetRecompile > 0)
                LogService.Log($"[MergeService] Added {addedBaseAssetRecompile} unchanged code entries for asset-index recompilation");

            // ══════════════════════════════════════════════════════════
            // Phase 4: Merge helper files
            // ══════════════════════════════════════════════════════════
            stepSw.Restart();

            bool includeTexturePageHelpers = HasEmbeddedTexturePayload(patchFileSystems);
            var mergedAssetOrder = MergeAssetOrders(originalAssetOrder, patchFileSystems, originalEmbeddedTextureCount, originalTpiCount, includeTexturePageHelpers);
            finalPfs.AddTextFile("Helpers/asset_order.txt", mergedAssetOrder);
            LogService.Progress(67, 100);

            var mergedVarFuncs = MergeVariablesFunctions(originalData, patchFileSystems);
            if (mergedVarFuncs != null)
                finalPfs.AddTextFile("Helpers/variables_functions.json", mergedVarFuncs);
            LogService.Progress(69, 100);

            var mergedObjEvents = MergeObjectEvents(patchFileSystems);
            if (mergedObjEvents != null)
                finalPfs.AddTextFile("Helpers/object_events.json", mergedObjEvents);
            LogService.Progress(71, 100);

            MergeTextureHelpers(finalPfs, patchFileSystems, originalData, originalEmbeddedTextureCount, originalTpiCount, conflicts);
            RepairInvalidPngEntries(finalPfs, patchFileSystems, originalData, conflicts);
            var soundAudioIdRemaps = RemapMergedEmbeddedSoundAudioIds(finalPfs, originalData, mergedAssetOrder, patchFileSystems);
            if (soundAudioIdRemaps.Remaps.Count > 0)
            {
                foreach (var pfs in patchFileSystems)
                    ApplySoundAudioIdRemaps(pfs, soundAudioIdRemaps.Remaps);
            }
            LogService.Progress(73, 100);

            LogService.Log($"[Bench] Helper files merge: {stepSw.Elapsed.TotalSeconds:F2}s");

            int exactCodeBasePatchIndex = TryGetBestExactCodeBasePatchIndex(patchFileSystems, [.. patchPaths]);
            if (exactCodeBasePatchIndex >= 0)
            {
                LogService.Log($"[MergeService] Using exact code-base merge path from '{patchNames[exactCodeBasePatchIndex]}'");
                var exactCodeBaseResult = await TryFinalizeUsingExactCodeBaseAsync(
                    originalPath,
                    [.. patchPaths],
                    patchFileSystems,
                    exactCodeBasePatchIndex,
                    finalPfs,
                    zipOutputPath,
                    saveZip,
                    options.ApplyPath,
                    sharedOriginalHashes,
                    sharedOriginalInfo,
                    sharedOriginalNameCounts,
                    sharedOriginalOrderedNames,
                    options.CacheOptions,
                    patchAssetRemaps[exactCodeBasePatchIndex],
                    soundAudioIdRemaps);

                if (!exactCodeBaseResult.Success)
                {
                    return new MergeResult
                    {
                        Success = false,
                        Error = exactCodeBaseResult.Error,
                        TotalConflicts = conflicts.Count(c => c.Status == "Conflict"),
                        AutoMerged = conflicts.Count(c => c.Status == "Resolved")
                    };
                }

                string? exactLogPath = options.ReportPath;
                if (exactLogPath != null || conflicts.Count > 0)
                {
                    exactLogPath ??= Path.ChangeExtension(zipOutputPath, ".merge_log.md");
                    File.WriteAllText(exactLogPath, GenerateConflictReport(patchNames, conflicts));
                    LogService.Log($"[Bench] Conflict report: {exactLogPath} ({conflicts.Count} entries)");
                }

                LogService.Progress(100, 100);
                LogService.ProgressComplete();

                totalSw.Stop();
                int exactTotalConflicts = conflicts.Count(c => c.Status == "Conflict");
                int exactAutoMerged = conflicts.Count(c => c.Status == "Resolved");
                LogService.Info($"Merge complete in {totalSw.Elapsed.TotalSeconds:F1}s: exact code-base path, {exactTotalConflicts} conflicts, {exactAutoMerged} auto-merged");
                if (saveZip) LogService.Info($"  Patch: {zipOutputPath}");
                if (!string.IsNullOrWhiteSpace(options.ApplyPath)) LogService.Info($"  Applied: {options.ApplyPath}");
                if (exactLogPath != null) LogService.Info($"  Report: {exactLogPath}");

                return new MergeResult
                {
                    Success = true,
                    OutputPath = saveZip ? zipOutputPath : options.ApplyPath,
                    TotalConflicts = exactTotalConflicts,
                    AutoMerged = exactAutoMerged
                };
            }

            // Release original data
            originalData = null!;
            decompileCache.Clear();
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            LogService.Progress(75, 100);

            // ══════════════════════════════════════════════════════════
            // Phase 5: Build manifest + save
            // ══════════════════════════════════════════════════════════
            stepSw.Restart();

            var manifest = BuildMergedManifest(patchFileSystems, finalPfs);
            finalPfs.RebuildDirectoryIndex();
            LogService.Progress(78, 100);

            if (saveZip)
            {
                finalPfs.SaveToZip(zipOutputPath, manifest);
                LogService.Log($"[Bench] Save patch: {stepSw.Elapsed.TotalSeconds:F2}s ({zipOutputPath})");
            }
            LogService.Progress(88, 100);

            // ══════════════════════════════════════════════════════════
            // Phase 6: Apply if requested
            // ══════════════════════════════════════════════════════════
            if (applyData)
            {
                stepSw.Restart();

                // If the patch was not saved yet, save it to temp.
                string applyZipPath = zipOutputPath;
                if (!saveZip)
                {
                    applyZipPath = Path.Combine(Path.GetTempPath(), $"g3m_merge_{Guid.NewGuid():N}.g3mpatch");
                    finalPfs.SaveToZip(applyZipPath, manifest);
                    tempFiles.Add(applyZipPath);
                }

                PatchApplyResult applyResult = await PatchService.ApplyPatchAsync(originalPath, applyZipPath, options.ApplyPath!);
                LogService.SetOperation("Merging");
                if (!applyResult.Success)
                    return new MergeResult
                    {
                        Success = false,
                        Error = $"Merge succeeded but apply failed: {applyResult.Error}",
                        TotalConflicts = conflicts.Count
                    };

                LogService.Log($"[Bench] Apply patch: {stepSw.Elapsed.TotalSeconds:F2}s ({options.ApplyPath})");
            }
            LogService.Progress(98, 100);

            // ══════════════════════════════════════════════════════════
            // Phase 7: Conflict report
            // ══════════════════════════════════════════════════════════
            string? logPath = options.ReportPath;
            if (logPath != null || conflicts.Count > 0)
            {
                logPath ??= Path.ChangeExtension(zipOutputPath, ".merge_log.md");
                File.WriteAllText(logPath, GenerateConflictReport(patchNames, conflicts));
                LogService.Log($"[Bench] Conflict report: {logPath} ({conflicts.Count} entries)");
            }

            LogService.Progress(100, 100);
            LogService.ProgressComplete();

            totalSw.Stop();
            int totalConflicts = conflicts.Count(c => c.Status == "Conflict");
            int autoMerged = conflicts.Count(c => c.Status == "Resolved");

            // Final summary (always visible)
            LogService.Info($"Merge complete in {totalSw.Elapsed.TotalSeconds:F1}s: " +
                $"{finalPfs.FileCount} files + {finalPfs.GmlEntries.Count} GML + {finalPfs.AsmEntries.Count} ASM, " +
                $"{totalConflicts} conflicts, {autoMerged} auto-merged");
            if (saveZip) LogService.Info($"  Patch: {zipOutputPath}");
            if (applyData) LogService.Info($"  Applied: {options.ApplyPath}");
            if (logPath != null) LogService.Info($"  Report: {logPath}");

            return new MergeResult
            {
                Success = true,
                OutputPath = saveZip ? zipOutputPath : options.ApplyPath,
                TotalConflicts = totalConflicts,
                AutoMerged = autoMerged
            };
        }
        catch (Exception ex)
        {
            return new MergeResult { Success = false, Error = $"Merge failed: {ex.Message}" };
        }
        finally
        {
            foreach (var temp in tempFiles)
                try { File.Delete(temp); } catch { }
        }
    }

    private static async Task<MergeResult> MergePatchesSequentiallyAsync(
        string originalPath,
        List<string> patchPaths,
        MergeOptions options)
    {
        if (!File.Exists(originalPath))
            return new MergeResult { Success = false, Error = $"Original file not found: {originalPath}" };

        foreach (var patchPath in patchPaths)
        {
            if (!File.Exists(patchPath))
                return new MergeResult { Success = false, Error = $"Patch not found: {patchPath}" };
        }

        if (patchPaths.Count < 2)
            return new MergeResult { Success = false, Error = "At least 2 patches are required for merge" };

        string? zipOutputPath = options.OutputPath;
        if (zipOutputPath == null && options.ApplyPath == null)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            zipOutputPath = Path.Combine(PlatformUtil.GetExecutableDirectory(), $"merged_{timestamp}.g3mpatch");
        }

        var patchNames = BuildPatchNames(patchPaths);
        var tempFiles = new List<string>();
        bool writeReport = !string.IsNullOrWhiteSpace(options.ReportPath);
        var report = writeReport ? new StringBuilder() : null;
        var totalSw = Stopwatch.StartNew();
        var precomputeSw = new Stopwatch();
        var applyLoopSw = new Stopwatch();
        var finalPatchSw = new Stopwatch();

        LogService.Info($"Merge: {patchPaths.Count} patches with original {Path.GetFileName(originalPath)}");
        for (int i = 0; i < patchPaths.Count; i++)
            LogService.Info($"  [{i + 1}] {patchNames[i]}");
        LogService.Info("  pipeline: sequential");

        LogService.SetOperation("Sequential merge");
        LogService.Progress(0, 100);

        try
        {
            var normalizedPatchPaths = patchPaths.ToArray();
            bool needsPatchConversion = patchPaths.Any(RequiresPatchConversion);
            bool needsOriginalSnapshot = needsPatchConversion || !string.IsNullOrWhiteSpace(zipOutputPath);
            Dictionary<string, Dictionary<string, string>>? sharedOriginalHashes = null;
            DataFileInfo? sharedOriginalInfo = null;
            Dictionary<string, Dictionary<string, int>>? sharedOriginalNameCounts = null;
            Dictionary<string, IReadOnlyList<string>>? sharedOriginalOrderedNames = null;

            if (needsOriginalSnapshot)
            {
                precomputeSw.Start();
                LogService.Progress(2, 100);
                LogService.Log(needsPatchConversion
                    ? "[Bench] Sequential precompute: loading original hashes/info for raw inputs + final patch creation..."
                    : "[Bench] Sequential precompute: loading original hashes/info for final patch creation...");

                using (var stream = OpenDataReadStream(originalPath))
                {
                    var originalData = UndertaleIO.Read(stream);
                    var cachedOriginal = G3MCacheService.TryReadDataCache(originalPath, options.CacheOptions);
                    if (cachedOriginal != null)
                    {
                        sharedOriginalHashes = cachedOriginal.ResourceHashes;
                        sharedOriginalNameCounts = cachedOriginal.ResourceNameCounts;
                        sharedOriginalOrderedNames = G3MCacheService.ToReadOnlyOrderNames(cachedOriginal.OrderSensitiveNames);
                        sharedOriginalInfo = cachedOriginal.DataInfo;
                    }
                    else
                    {
                        sharedOriginalHashes = ResourceHashService.HashAll(originalData);
                        sharedOriginalNameCounts = PatchService.GetResourceNameCountsForReuse(originalData);
                        sharedOriginalOrderedNames = PatchService.GetOrderSensitiveResourceNamesForReuse(originalData);
                        sharedOriginalInfo = new DataFileInfo
                        {
                            Filename = Path.GetFileName(originalPath),
                            Size = new FileInfo(originalPath).Length,
                            Md5 = await HashService.ComputeFileHashAsync(originalPath),
                            BytecodeVersion = originalData.GeneralInfo?.BytecodeVersion ?? 0,
                            GmsVersion = GeneralInfoUtil.GetVersionDisplay(originalData.GeneralInfo),
                            GeneralInfo = GeneralInfoUtil.ExtractGeneralInfo(originalData)
                        };
                        await G3MCacheService.WriteDataCacheAsync(originalPath, sharedOriginalInfo, sharedOriginalHashes, sharedOriginalNameCounts, sharedOriginalOrderedNames, options.CacheOptions);
                    }
                }

                if (needsPatchConversion)
                {
                    LogService.Progress(4, 100);
                    LogService.Log("[Bench] Sequential pre-normalization: converting raw inputs against original...");

                    const int maxConcurrent = 5;
                    using var normSemaphore = new SemaphoreSlim(maxConcurrent);
                    var normalizeTasks = new Task<string>[patchPaths.Count];
                    for (int i = 0; i < patchPaths.Count; i++)
                    {
                        var idx = i;
                        normalizeTasks[idx] = Task.Run(async () =>
                        {
                            if (!RequiresPatchConversion(patchPaths[idx]))
                                return patchPaths[idx];

                            await normSemaphore.WaitAsync();
                            try
                            {
                                return await PatchService.EnsureG3MPatchAsync(
                                    originalPath,
                                    patchPaths[idx],
                                    null,
                                    sharedOriginalHashes,
                                    sharedOriginalInfo,
                                    sharedOriginalNameCounts,
                                    sharedOriginalOrderedNames,
                                    options.CacheOptions);
                            }
                            finally
                            {
                                normSemaphore.Release();
                            }
                        });
                    }

                    normalizedPatchPaths = await Task.WhenAll(normalizeTasks);
                    for (int i = 0; i < normalizedPatchPaths.Length; i++)
                    {
                        if (!string.Equals(normalizedPatchPaths[i], patchPaths[i], StringComparison.OrdinalIgnoreCase))
                            tempFiles.Add(normalizedPatchPaths[i]);
                    }
                }
                precomputeSw.Stop();
            }

            string currentDataPath = Path.Combine(Path.GetTempPath(), $"g3m_merge_seq_base_{Guid.NewGuid():N}{Path.GetExtension(originalPath)}");
            File.Copy(originalPath, currentDataPath, overwrite: true);
            tempFiles.Add(currentDataPath);

            string? currentKnownMd5 = sharedOriginalInfo?.Md5;
            if (string.IsNullOrWhiteSpace(currentKnownMd5))
                currentKnownMd5 = await HashService.ComputeFileHashAsync(originalPath);

            UndertaleData currentData;
            using (var stream = OpenDataReadStream(currentDataPath))
            {
                currentData = UndertaleIO.Read(stream);
            }

            if (report != null)
            {
                report.AppendLine("# Sequential Merge Report");
                report.AppendLine();
                report.AppendLine($"Original: `{Path.GetFileName(originalPath)}`");
                report.AppendLine("Pipeline: `sequential`");
                report.AppendLine();
                report.AppendLine("| Step | Patch | Apply Seconds | Output Md5 |");
                report.AppendLine("| ---: | :---- | ------------: | :--------- |");
            }

            applyLoopSw.Start();
            for (int i = 0; i < patchPaths.Count; i++)
            {
                LogService.Progress(5 + i * 70 / patchPaths.Count, 100);

                string normalizedPatchPath = normalizedPatchPaths[i];
                var manifest = await TryReadPatchManifestAsync(normalizedPatchPath);

                if (IsNoOpPatch(manifest))
                {
                    if (report != null)
                    {
                        string outputMd5 = await HashService.ComputeFileHashAsync(currentDataPath);
                        report.AppendLine($"| {i + 1} | `{patchNames[i]}` | 0.00 | `{outputMd5}` |");
                    }
                    continue;
                }

                var applySw = Stopwatch.StartNew();
                PatchApplyResult applyResult;

                bool canUseExactDiskApply =
                    manifest?.Original?.Md5 != null &&
                    manifest?.Modified?.Md5 != null &&
                    currentKnownMd5 != null &&
                    manifest.Original.Md5.Equals(currentKnownMd5, StringComparison.OrdinalIgnoreCase);

                if (canUseExactDiskApply)
                {
                    applyResult = await PatchService.ApplyPatchAsync(
                        currentDataPath,
                        normalizedPatchPath,
                        currentDataPath,
                        allowXdeltaFallback: false,
                        verifyModifiedHash: true);

                    if (applyResult.Success)
                    {
                        using var reloadStream = OpenDataReadStream(currentDataPath);
                        currentData = UndertaleIO.Read(reloadStream);
                        currentKnownMd5 = manifest!.Modified!.Md5;
                    }
                }
                else
                {
                    applyResult = await PatchService.ApplyPatchInMemoryAsync(
                        currentData,
                        normalizedPatchPath,
                        currentDataPath);

                    if (applyResult.Success)
                        currentKnownMd5 = null;
                }
                applySw.Stop();

                if (!applyResult.Success)
                {
                    return new MergeResult
                    {
                        Success = false,
                        Error = $"Sequential merge failed on patch '{patchNames[i]}': {applyResult.Error}"
                    };
                }

                if (report != null)
                {
                    using (var outStream = OpenDataWriteStream(currentDataPath))
                    {
                        UndertaleIO.Write(outStream, currentData);
                    }
                    string outputMd5 = await HashService.ComputeFileHashAsync(currentDataPath);
                    report.AppendLine($"| {i + 1} | `{patchNames[i]}` | {applySw.Elapsed.TotalSeconds:F2} | `{outputMd5}` |");
                }
            }
            applyLoopSw.Stop();

            LogService.Progress(78, 100);

            using (var outStream = OpenDataWriteStream(currentDataPath))
            {
                UndertaleIO.Write(outStream, currentData);
            }

            if (!string.IsNullOrWhiteSpace(options.ApplyPath))
            {
                var applyDir = Path.GetDirectoryName(options.ApplyPath!);
                if (!string.IsNullOrWhiteSpace(applyDir))
                    Directory.CreateDirectory(applyDir);
                File.Copy(currentDataPath, options.ApplyPath!, overwrite: true);
            }

            if (!string.IsNullOrWhiteSpace(zipOutputPath))
            {
                finalPatchSw.Start();
                var createResult = await PatchService.CreatePatchAsync(
                    originalPath,
                    currentDataPath,
                    zipOutputPath!,
                    sharedOriginalHashes,
                    sharedOriginalInfo,
                    sharedOriginalNameCounts,
                    sharedOriginalOrderedNames,
                    currentData,
                    includeXdeltaFallback: false,
                    cacheOptions: options.CacheOptions);
                finalPatchSw.Stop();

                if (!createResult.Success)
                {
                    return new MergeResult
                    {
                        Success = false,
                        Error = $"Sequential merge produced merged data, but patch creation failed: {createResult.Error}"
                    };
                }
            }

            LogService.Progress(92, 100);

            string? reportPath = options.ReportPath;
            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                var reportDir = Path.GetDirectoryName(reportPath!);
                if (!string.IsNullOrWhiteSpace(reportDir))
                    Directory.CreateDirectory(reportDir);
                report!.AppendLine();
                report.AppendLine($"Precompute seconds: `{precomputeSw.Elapsed.TotalSeconds:F2}`");
                report.AppendLine($"Apply loop seconds: `{applyLoopSw.Elapsed.TotalSeconds:F2}`");
                report.AppendLine($"Final patch create seconds: `{finalPatchSw.Elapsed.TotalSeconds:F2}`");
                report.AppendLine($"Total seconds: `{totalSw.Elapsed.TotalSeconds:F2}`");
                await File.WriteAllTextAsync(reportPath!, report.ToString());
            }

            LogService.Progress(100, 100);
            LogService.ProgressComplete();
            LogService.Info($"Sequential merge complete in {totalSw.Elapsed.TotalSeconds:F1}s");
            if (!string.IsNullOrWhiteSpace(zipOutputPath))
                LogService.Info($"  Patch: {zipOutputPath}");
            if (!string.IsNullOrWhiteSpace(options.ApplyPath))
                LogService.Info($"  Applied: {options.ApplyPath}");
            if (!string.IsNullOrWhiteSpace(reportPath))
                LogService.Info($"  Report: {reportPath}");

            return new MergeResult
            {
                Success = true,
                OutputPath = options.ApplyPath ?? zipOutputPath,
                TotalConflicts = 0,
                AutoMerged = 0
            };
        }
        catch (Exception ex)
        {
            return new MergeResult { Success = false, Error = $"Sequential merge failed: {ex.Message}" };
        }
        finally
        {
            foreach (var temp in tempFiles)
                try { File.Delete(temp); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Asset Order Merge (union approach - preserves original indices)
    // ═══════════════════════════════════════════════════════════════════

    private static Dictionary<string, List<string>> ExtractAssetOrder(UndertaleData data)
    {
        var order = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        void AddList<T>(string section, IList<T>? assets) where T : UndertaleNamedResource
        {
            var list = new List<string>();
            if (assets != null)
                foreach (var a in assets)
                    list.Add(a?.Name?.Content ?? "(null)");
            order[section] = list;
        }

        AddList("sounds", data.Sounds);
        AddList("sprites", data.Sprites);
        AddList("backgrounds", data.Backgrounds);
        AddList("paths", data.Paths);
        AddList("scripts", data.Scripts);
        AddList("fonts", data.Fonts);
        AddList("objects", data.GameObjects);
        AddList("timelines", data.Timelines);
        AddList("rooms", data.Rooms);
        AddList("shaders", data.Shaders);
        AddList("extensions", data.Extensions);
        AddList("audiogroups", data.AudioGroups);

        return order;
    }

    private static string MergeAssetOrders(
        Dictionary<string, List<string>> originalOrder,
        PatchFileSystem[] patches,
        int originalEmbeddedTextureCount,
        int originalTpiCount,
        bool includeTextureCounts)
    {
        // Parse each patch's asset_order.txt
        var patchOrders = new List<Dictionary<string, List<string>>>();
        var patchCounts = new List<Dictionary<string, string>>();
        Dictionary<string, string>? latestCounts = null;

        foreach (var pfs in patches)
        {
            var aoPath = $"{pfs.HelpersPrefix}/asset_order.txt";
            if (!pfs.FileExists(aoPath)) continue;
            var text = pfs.ReadAllText(aoPath);
            var (sections, counts) = ParseAssetOrderText(text);
            patchOrders.Add(sections);
            patchCounts.Add(counts);
            if (counts.Count > 0) latestCounts = counts;
        }

        if (patchOrders.Count == 0)
            return ""; // No asset orders found

        var mergedOrder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var sectionNames = new[] { "sounds", "sprites", "backgrounds", "paths", "scripts",
            "fonts", "objects", "timelines", "rooms", "shaders", "extensions", "audiogroups" };

        foreach (var section in sectionNames)
        {
            var origList = originalOrder.GetValueOrDefault(section) ?? [];
            mergedOrder[section] = BuildMergedAssetOrderSection(origList, patchOrders, section);
        }

        // Build output text
        var sb = new StringBuilder();
        foreach (var section in sectionNames)
        {
            sb.AppendLine($"@@{section}@@");
            if (mergedOrder.TryGetValue(section, out var items))
                foreach (var item in items)
                    sb.AppendLine(item);
        }

        // Append @@counts@@ section from the latest patch (with adjustments)
        sb.AppendLine("@@counts@@");
        if (latestCounts != null)
        {
            if (includeTextureCounts)
            {
                latestCounts["EmbeddedTextures"] = ComputeMergedCount(patchCounts, "EmbeddedTextures", originalEmbeddedTextureCount).ToString();
                latestCounts["TexturePageItems"] = ComputeMergedCount(patchCounts, "TexturePageItems", originalTpiCount).ToString();
            }
            else
            {
                latestCounts.Remove("EmbeddedTextures");
                latestCounts.Remove("TexturePageItems");
            }
            foreach (var (key, value) in latestCounts)
                sb.AppendLine($"{key}={value}");
        }

        return sb.ToString();
    }

    private static List<string> BuildMergedAssetOrderSection(
        List<string> originalList,
        IReadOnlyList<Dictionary<string, List<string>>> patchOrders,
        string section)
    {
        List<string> merged = [];
        var mergedCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        static void AddEntry(List<string> target, Dictionary<string, int> counts, string? entry)
        {
            if (string.IsNullOrWhiteSpace(entry))
                return;
            target.Add(entry);
            counts[entry] = counts.GetValueOrDefault(entry) + 1;
        }

        var baseList = SelectBaseAssetOrderSection(originalList, patchOrders, section);

        foreach (var entry in baseList)
            AddEntry(merged, mergedCounts, entry);
        AppendMissingOrderMultiplicity(merged, mergedCounts, originalList);
        foreach (var patchOrder in patchOrders)
        {
            if (patchOrder.TryGetValue(section, out var patchList))
                AppendMissingOrderMultiplicity(merged, mergedCounts, patchList);
        }

        return merged;
    }

    private sealed record SoundAudioIdRemapResult(
        Dictionary<string, (int OldAudioId, int NewAudioId)> Remaps,
        HashSet<string> AffectedSoundNames);

    private static SoundAudioIdRemapResult RemapMergedEmbeddedSoundAudioIds(
        PatchFileSystem finalPfs,
        UndertaleData originalData,
        string mergedAssetOrder,
        PatchFileSystem[] sourcePatches)
    {
        var remaps = new Dictionary<string, (int OldAudioId, int NewAudioId)>(StringComparer.Ordinal);
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var soundJsonPaths = finalPfs.GetAllFilePaths()
            .Where(path => path.StartsWith("Sounds/", StringComparison.OrdinalIgnoreCase) &&
                           path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (soundJsonPaths.Count == 0)
            return new SoundAudioIdRemapResult(remaps, affected);

        var sourcePayloads = new Dictionary<string, MergedSoundPayload>(StringComparer.Ordinal);
        foreach (var payloads in sourcePatches.Select(ReadMergedSoundPayloads))
        {
            foreach (var (name, payload) in payloads)
                sourcePayloads[name] = payload;
        }

        var audioIdOwners = new Dictionary<int, string>();
        int nextAudioId = Math.Max(
            originalData.EmbeddedAudio?.Count ?? 0,
            originalData.Sounds.Where(s => s != null && s.GroupID <= 0 && s.AudioID >= 0)
                .Select(s => s!.AudioID + 1)
                .DefaultIfEmpty(0)
                .Max());
        int NextFreeAudioId()
        {
            while (audioIdOwners.ContainsKey(nextAudioId))
                nextAudioId++;
            return nextAudioId++;
        }

        var pathsBySoundName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in soundJsonPaths)
        {
            try
            {
                var node = JsonNode.Parse(finalPfs.ReadAllText(path))?.AsObject();
                string? name = node?["name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name))
                    pathsBySoundName[name] = path;
            }
            catch
            {
            }
        }

        var (sections, _) = ParseAssetOrderText(mergedAssetOrder);
        var orderedNames = sections.TryGetValue("sounds", out var sounds)
            ? sounds.Where(IsRealAssetOrderEntry).ToList()
            : [];
        var orderedNameSet = new HashSet<string>(orderedNames, StringComparer.Ordinal);

        foreach (var name in pathsBySoundName.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (orderedNameSet.Add(name))
                orderedNames.Add(name);
        }

        int remapped = 0;
        foreach (var soundName in orderedNames)
        {
            if (!pathsBySoundName.TryGetValue(soundName, out var path))
                continue;

            JsonObject? node;
            try
            {
                node = JsonNode.Parse(finalPfs.ReadAllText(path))?.AsObject();
            }
            catch
            {
                continue;
            }

            if (node == null)
                continue;

            int groupId = node["groupID"]?.GetValue<int>() ?? -1;
            int audioId = node["audioID"]?.GetValue<int>() ?? -1;
            if (groupId > 0 || audioId < 0)
                continue;

            if (!audioIdOwners.TryGetValue(audioId, out var ownerName) ||
                string.Equals(ownerName, soundName, StringComparison.Ordinal))
            {
                audioIdOwners[audioId] = soundName;
                continue;
            }

            int newAudioId = NextFreeAudioId();
            node["audioID"] = newAudioId;
            audioIdOwners[newAudioId] = soundName;
            finalPfs.AddTextFile(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            RemapEmbeddedAudioEntry(finalPfs, audioId, newAudioId);
            remaps[soundName] = (audioId, newAudioId);
            affected.Add(ownerName);
            affected.Add(soundName);
            remapped++;
        }

        if (remapped > 0)
        {
            RestoreAffectedEmbeddedAudioEntries(finalPfs, sourcePayloads, affected);
            LogService.Log($"[MergeService] Remapped {remapped} embedded sound audioID collision(s)");
        }
        return new SoundAudioIdRemapResult(remaps, affected);
    }

    private static void RestoreAffectedEmbeddedAudioEntries(
        PatchFileSystem finalPfs,
        Dictionary<string, MergedSoundPayload> sourcePayloads,
        HashSet<string> affectedSoundNames)
    {
        foreach (var soundName in affectedSoundNames)
        {
            if (!sourcePayloads.TryGetValue(soundName, out var payload) ||
                payload.GroupId > 0 ||
                payload.AudioId < 0 ||
                payload.AudioBytes == null)
            {
                continue;
            }

            WriteEmbeddedAudioEntry(finalPfs, payload.AudioId, payload.AudioBytes);
            WriteSoundAudioPayload(finalPfs, soundName, payload);
        }
    }

    private static void WriteSoundAudioPayload(PatchFileSystem pfs, string soundName, MergedSoundPayload payload)
    {
        if (payload.AudioBytes == null)
            return;

        string extension = string.IsNullOrWhiteSpace(payload.Extension) ? ".wav" : payload.Extension;
        if (!extension.StartsWith(".", StringComparison.Ordinal))
            extension = "." + extension;

        pfs.AddFile($"Sounds/{soundName}/{soundName}{extension}", payload.AudioBytes);
    }

    private static void WriteEmbeddedAudioEntry(PatchFileSystem pfs, int audioId, byte[] bytes)
    {
        string name = $"audio_{audioId:D4}";
        string root = $"EmbeddedAudio/{name}";
        pfs.AddFile($"{root}/{name}.bin", bytes);
        pfs.AddTextFile($"{root}/{name}.json", JsonSerializer.Serialize(new Dictionary<string, int>
        {
            ["index"] = audioId
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ApplySoundAudioIdRemaps(
        PatchFileSystem pfs,
        Dictionary<string, (int OldAudioId, int NewAudioId)> remaps)
    {
        if (remaps.Count == 0)
            return;

        foreach (var path in pfs.GetAllFilePaths()
                     .Where(path => path.StartsWith("Sounds/", StringComparison.OrdinalIgnoreCase) &&
                                    path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            JsonObject? node;
            try
            {
                node = JsonNode.Parse(pfs.ReadAllText(path))?.AsObject();
            }
            catch
            {
                continue;
            }

            string? soundName = node?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(soundName) ||
                !remaps.TryGetValue(soundName, out var remap))
            {
                continue;
            }

            int audioId = node?["audioID"]?.GetValue<int>() ?? -1;
            int groupId = node?["groupID"]?.GetValue<int>() ?? -1;
            if (groupId > 0 || audioId != remap.OldAudioId)
                continue;

            node!["audioID"] = remap.NewAudioId;
            pfs.AddTextFile(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            RemapEmbeddedAudioEntry(pfs, remap.OldAudioId, remap.NewAudioId);
        }
    }

    private static void RepairMergedEmbeddedSoundAudioSlots(
        UndertaleData data,
        PatchFileSystem finalPfs,
        PatchFileSystem[] sourcePatches,
        HashSet<string> affectedSoundNames)
    {
        if (affectedSoundNames.Count == 0)
            return;

        string assetOrder = finalPfs.FileExists("Helpers/asset_order.txt")
            ? finalPfs.ReadAllText("Helpers/asset_order.txt")
            : "";
        var (sections, _) = ParseAssetOrderText(assetOrder);
        var orderedNames = sections.TryGetValue("sounds", out var sounds)
            ? sounds.Where(IsRealAssetOrderEntry).ToList()
            : [.. data.Sounds.Select(s => s?.Name?.Content ?? "").Where(n => !string.IsNullOrWhiteSpace(n))];

        var soundPayloads = ReadMergedSoundPayloads(finalPfs);
        foreach (var sourcePayloads in sourcePatches.Select(ReadMergedSoundPayloads))
        {
            foreach (var (name, payload) in sourcePayloads)
                soundPayloads[name] = payload;
        }
        var audioIdOwners = new Dictionary<(int GroupId, int AudioId), string>();
        if (data.EmbeddedAudio == null)
            return;
        int nextAudioId = data.EmbeddedAudio.Count;
        int repaired = 0;

        UndertaleEmbeddedAudio EnsureSlot(int audioId)
        {
            while (data.EmbeddedAudio.Count <= audioId)
                data.EmbeddedAudio.Add(new UndertaleEmbeddedAudio());
            return data.EmbeddedAudio[audioId];
        }

        int NextFreeAudioId()
        {
            while (audioIdOwners.ContainsKey((0, nextAudioId)))
                nextAudioId++;
            return nextAudioId++;
        }

        foreach (var soundName in orderedNames)
        {
            if (!affectedSoundNames.Contains(soundName))
                continue;

            var sound = data.Sounds.ByName(soundName);
            if (sound == null)
                continue;

            soundPayloads.TryGetValue(soundName, out var payload);
            int groupId = payload?.GroupId >= 0 ? payload.GroupId : sound.GroupID;
            int audioId = payload?.AudioId >= 0 ? payload.AudioId : sound.AudioID;
            if (groupId > 0 || audioId < 0)
                continue;

            if (audioIdOwners.TryGetValue((groupId, audioId), out var owner) &&
                !string.Equals(owner, soundName, StringComparison.Ordinal))
            {
                audioId = NextFreeAudioId();
                repaired++;
            }

            audioIdOwners[(groupId, audioId)] = soundName;
            sound.GroupID = groupId;
            if (data.AudioGroups != null && groupId >= 0 && groupId < data.AudioGroups.Count)
                sound.AudioGroup = data.AudioGroups[groupId];
            sound.AudioID = audioId;
            sound.AudioFile = EnsureSlot(audioId);
            if (payload?.AudioBytes != null)
                sound.AudioFile.Data = payload.AudioBytes;
        }

        if (repaired > 0)
            LogService.Log($"[MergeService] Repaired {repaired} duplicate embedded sound slot(s) in merged data");
    }

    private static void RepairExactMergedSoundPayloadsFromOwners(
        UndertaleData data,
        PatchFileSystem finalPfs,
        PatchFileSystem[] sourcePatches)
    {
        if (data.EmbeddedAudio == null)
            return;

        var ownerPayloads = BuildFinalSoundOwnerPayloads(finalPfs, sourcePatches);
        int repaired = 0;
        foreach (var (soundName, payload) in ownerPayloads)
        {
            if (payload.AudioBytes == null || payload.GroupId > 0 || payload.AudioId < 0)
                continue;

            var sound = data.Sounds.ByName(soundName);
            if (sound == null)
                continue;

            while (data.EmbeddedAudio.Count <= payload.AudioId)
                data.EmbeddedAudio.Add(new UndertaleEmbeddedAudio());

            data.EmbeddedAudio[payload.AudioId].Data = payload.AudioBytes;
            sound.AudioID = payload.AudioId;
            sound.GroupID = payload.GroupId;
            sound.AudioFile = data.EmbeddedAudio[payload.AudioId];
            if (data.AudioGroups != null && payload.GroupId >= 0 && payload.GroupId < data.AudioGroups.Count)
                sound.AudioGroup = data.AudioGroups[payload.GroupId];
            repaired++;
        }

        if (repaired > 0)
            LogService.Log($"[MergeService] Restored {repaired} exact-base sound payload(s) from patch owners");
    }

    private static Dictionary<string, MergedSoundPayload> BuildFinalSoundOwnerPayloads(
        PatchFileSystem finalPfs,
        PatchFileSystem[] sourcePatches)
    {
        var sourcePayloads = sourcePatches.Select(ReadMergedSoundPayloads).ToArray();
        var finalPayloads = ReadMergedSoundPayloads(finalPfs);
        var result = new Dictionary<string, MergedSoundPayload>(StringComparer.Ordinal);

        foreach (var finalPath in finalPfs.GetAllFilePaths()
                     .Where(path => path.StartsWith("Sounds/", StringComparison.OrdinalIgnoreCase) &&
                                    path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            JsonObject? node;
            try
            {
                node = JsonNode.Parse(finalPfs.ReadAllText(finalPath))?.AsObject();
            }
            catch
            {
                continue;
            }

            string? soundName = node?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(soundName))
                continue;

            byte[] finalBytes = finalPfs.ReadAllBytes(finalPath);
            MergedSoundPayload? selected = null;
            for (int i = sourcePatches.Length - 1; i >= 0; i--)
            {
                if (sourcePatches[i].TryGetFile(finalPath, out var sourceBytes) &&
                    sourceBytes.AsSpan().SequenceEqual(finalBytes) &&
                    sourcePayloads[i].TryGetValue(soundName, out var payload))
                {
                    selected = payload;
                    break;
                }
            }

            selected ??= finalPayloads.GetValueOrDefault(soundName);
            if (selected != null)
                result[soundName] = selected;
        }

        return result;
    }

    private sealed record MergedSoundPayload(int AudioId, int GroupId, string Extension, byte[]? AudioBytes);

    private static Dictionary<string, MergedSoundPayload> ReadMergedSoundPayloads(PatchFileSystem finalPfs)
    {
        var result = new Dictionary<string, MergedSoundPayload>(StringComparer.Ordinal);
        var allPaths = finalPfs.GetAllFilePaths().ToArray();
        foreach (var path in allPaths
                     .Where(path => path.StartsWith("Sounds/", StringComparison.OrdinalIgnoreCase) &&
                                    path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var node = JsonNode.Parse(finalPfs.ReadAllText(path))?.AsObject();
                string? soundName = node?["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(soundName))
                    continue;

                int audioId = node?["audioID"]?.GetValue<int>() ?? -1;
                int groupId = node?["groupID"]?.GetValue<int>() ?? -1;
                string extension = node?["type"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(extension))
                {
                    var fileName = node?["file"]?.GetValue<string>() ?? "";
                    extension = Path.GetExtension(fileName);
                }
                string dir = path[..path.LastIndexOf('/')];
                byte[]? audioBytes = null;
                foreach (var audioPath in allPaths.Where(p =>
                             p.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase) &&
                             (p.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                              p.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))))
                {
                    audioBytes = finalPfs.ReadAllBytes(audioPath);
                    break;
                }

                if (audioBytes == null && groupId <= 0 && audioId >= 0)
                {
                    string audioRoot = $"EmbeddedAudio/audio_{audioId:D4}/";
                    foreach (var audioPath in allPaths.Where(p =>
                                 p.StartsWith(audioRoot, StringComparison.OrdinalIgnoreCase) &&
                                 p.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
                    {
                        audioBytes = finalPfs.ReadAllBytes(audioPath);
                        break;
                    }
                }

                result[soundName] = new MergedSoundPayload(audioId, groupId, extension, audioBytes);
            }
            catch
            {
            }
        }

        return result;
    }

    private static void RemapEmbeddedAudioEntry(PatchFileSystem finalPfs, int oldAudioId, int newAudioId)
    {
        string oldName = $"audio_{oldAudioId:D4}";
        string newName = $"audio_{newAudioId:D4}";
        string oldRoot = $"EmbeddedAudio/{oldName}";
        string newRoot = $"EmbeddedAudio/{newName}";

        foreach (var path in finalPfs.GetAllFilePaths()
                     .Where(path => path.StartsWith(oldRoot + "/", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            var bytes = finalPfs.ReadAllBytes(path);
            string fileName = Path.GetFileName(path);
            string extension = Path.GetExtension(fileName);
            string newPath = $"{newRoot}/{newName}{extension}";

            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var node = JsonNode.Parse(bytes)?.AsObject();
                    if (node != null)
                    {
                        node["index"] = newAudioId;
                        bytes = Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
                catch
                {
                }
            }

            finalPfs.AddFile(newPath, bytes);
            finalPfs.RemoveFile(path);
        }
    }

    private static List<string> SelectBaseAssetOrderSection(
        List<string> originalList,
        IReadOnlyList<Dictionary<string, List<string>>> patchOrders,
        string section)
    {
        var selected = originalList;
        int selectedScore = 0;

        for (int i = 0; i < patchOrders.Count; i++)
        {
            if (!patchOrders[i].TryGetValue(section, out var patchList))
                continue;

            int score = ComputeAssetOrderDifferenceScore(originalList, patchList);
            if (score >= selectedScore)
            {
                selected = patchList;
                selectedScore = score;
            }
        }

        return selected;
    }

    private static int ComputeAssetOrderDifferenceScore(List<string> originalList, List<string> patchList)
    {
        int common = Math.Min(originalList.Count, patchList.Count);
        int score = Math.Abs(originalList.Count - patchList.Count);
        for (int i = 0; i < common; i++)
        {
            if (!string.Equals(originalList[i], patchList[i], StringComparison.Ordinal))
                score++;
        }
        return score;
    }

    private static void AppendMissingOrderMultiplicity(
        List<string> merged,
        Dictionary<string, int> mergedCounts,
        List<string> source)
    {
        var sourceCounts = CountOrderEntries(source);
        foreach (var entry in source)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            var sourceCount = sourceCounts.GetValueOrDefault(entry);
            var mergedCount = mergedCounts.GetValueOrDefault(entry);
            if (sourceCount > mergedCount)
            {
                merged.Add(entry);
                mergedCounts[entry] = mergedCount + 1;
            }
        }
    }

    private static int ComputeMergedCount(List<Dictionary<string, string>> patchCounts, string key, int originalCount)
    {
        int merged = originalCount;
        foreach (var counts in patchCounts)
        {
            if (counts.TryGetValue(key, out var text) && int.TryParse(text, out int patchCount) && patchCount > originalCount)
                merged += patchCount - originalCount;
        }
        return merged;
    }

    private static Dictionary<string, int> CountOrderEntries(List<string> entries)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            result[entry] = result.GetValueOrDefault(entry) + 1;
        }
        return result;
    }

    private static Dictionary<string, List<string>>[] LoadPatchAssetOrders(PatchFileSystem[] patches)
    {
        var result = new Dictionary<string, List<string>>[patches.Length];
        for (int i = 0; i < patches.Length; i++)
        {
            var aoPath = $"{patches[i].HelpersPrefix}/asset_order.txt";
            if (!patches[i].FileExists(aoPath))
            {
                result[i] = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var (sections, _) = ParseAssetOrderText(patches[i].ReadAllText(aoPath));
            result[i] = sections;
        }
        return result;
    }

    private static PatchAssetRemaps[] BuildPatchAssetRemaps(
        Dictionary<string, List<string>> originalOrder,
        Dictionary<string, List<string>>[] patchOrders,
        string[] patchNames)
    {
        var result = new PatchAssetRemaps[patchOrders.Length];
        var sections = new (string Section, Func<PatchAssetRemaps, Dictionary<int, int>> Target)[]
        {
            ("objects", r => r.Objects),
            ("sprites", r => r.Sprites),
            ("sounds", r => r.Sounds),
            ("backgrounds", r => r.Backgrounds),
            ("paths", r => r.Paths),
            ("scripts", r => r.Scripts),
            ("fonts", r => r.Fonts),
            ("rooms", r => r.Rooms),
            ("timelines", r => r.Timelines),
            ("shaders", r => r.Shaders),
            ("extensions", r => r.Extensions),
            ("audiogroups", r => r.AudioGroups),
        };

        foreach (var (section, target) in sections)
        {
            var originalList = originalOrder.GetValueOrDefault(section) ?? [];
            var mergedNames = BuildMergedAssetOrderSection(originalList, patchOrders, section);

            var mergedIndicesByName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < mergedNames.Count; i++)
            {
                var name = mergedNames[i];
                if (!IsRealAssetOrderEntry(name))
                    continue;
                if (!mergedIndicesByName.TryGetValue(name, out var indices))
                {
                    indices = [];
                    mergedIndicesByName[name] = indices;
                }
                indices.Add(i);
            }
            var mergedUniqueIndex = mergedIndicesByName
                .Where(kvp => kvp.Value.Count == 1)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value[0], StringComparer.Ordinal);

            for (int pi = 0; pi < patchOrders.Length; pi++)
            {
                result[pi] ??= new PatchAssetRemaps();
                if (!patchOrders[pi].TryGetValue(section, out var patchList))
                    continue;

                var remap = target(result[pi]);
                var seenInPatch = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int currentIndex = 0; currentIndex < patchList.Count; currentIndex++)
                {
                    var name = patchList[currentIndex];
                    if (!IsRealAssetOrderEntry(name))
                        continue;

                    int occurrence = seenInPatch.GetValueOrDefault(name);
                    seenInPatch[name] = occurrence + 1;

                    if (mergedIndicesByName.TryGetValue(name, out var mergedOccurrences) &&
                        occurrence < mergedOccurrences.Count &&
                        mergedOccurrences[occurrence] != currentIndex)
                    {
                        int mergedIdx = mergedOccurrences[occurrence];
                        remap[currentIndex] = mergedIdx;
                        result[pi].ShiftedAssetNames.Add(name);
                        if (mergedUniqueIndex.TryGetValue(name, out int uniqueIndex))
                            result[pi].ShiftedAssetNameIndices[name] = uniqueIndex;
                    }
                }

                if (remap.Count > 0)
                    LogService.Log($"[MergeService] {section} index remap for '{patchNames[pi]}': {remap.Count} indices shifted");
            }
        }

        for (int i = 0; i < result.Length; i++)
            result[i] ??= new PatchAssetRemaps();
        return result;
    }

    private static PatchAssetRemaps BuildOriginalAssetRemap(
        Dictionary<string, List<string>> originalOrder,
        Dictionary<string, List<string>>[] patchOrders)
    {
        var result = new PatchAssetRemaps();
        var sections = new (string Section, Func<PatchAssetRemaps, Dictionary<int, int>> Target)[]
        {
            ("objects", r => r.Objects),
            ("sprites", r => r.Sprites),
            ("sounds", r => r.Sounds),
            ("backgrounds", r => r.Backgrounds),
            ("paths", r => r.Paths),
            ("scripts", r => r.Scripts),
            ("fonts", r => r.Fonts),
            ("rooms", r => r.Rooms),
            ("timelines", r => r.Timelines),
            ("shaders", r => r.Shaders),
            ("extensions", r => r.Extensions),
            ("audiogroups", r => r.AudioGroups),
        };

        foreach (var (section, target) in sections)
        {
            var originalList = originalOrder.GetValueOrDefault(section) ?? [];
            var mergedNames = BuildMergedAssetOrderSection(originalList, patchOrders, section);

            var mergedIndicesByName = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
            for (int i = 0; i < mergedNames.Count; i++)
            {
                var name = mergedNames[i];
                if (!IsRealAssetOrderEntry(name))
                    continue;
                if (!mergedIndicesByName.TryGetValue(name, out var indices))
                {
                    indices = new Queue<int>();
                    mergedIndicesByName[name] = indices;
                }
                indices.Enqueue(i);
            }

            var remap = target(result);
            for (int originalIndex = 0; originalIndex < originalList.Count; originalIndex++)
            {
                var name = originalList[originalIndex];
                if (!IsRealAssetOrderEntry(name))
                    continue;
                if (!mergedIndicesByName.TryGetValue(name, out var indices) || indices.Count == 0)
                    continue;

                int mergedIndex = indices.Dequeue();
                if (mergedIndex == originalIndex)
                    continue;

                remap[originalIndex] = mergedIndex;
                result.ShiftedAssetNames.Add(name);
                result.ShiftedAssetNameIndices[name] = mergedIndex;
            }
        }

        if (result.HasAny)
            LogService.Log($"[MergeService] Original asset index remap for unchanged code: {result.ShiftedAssetNameIndices.Count} shifted names");
        return result;
    }

    private static int AddOriginalCodeEntriesForShiftedAssets(
        UndertaleData originalData,
        GlobalDecompileContext decompileCtx,
        Dictionary<string, string?> decompileCache,
        PatchAssetRemaps originalAssetRemap,
        PatchFileSystem finalPfs)
    {
        if (!originalAssetRemap.HasAny)
            return 0;

        int added = 0;
        foreach (var code in originalData.Code)
        {
            string? codeName = code?.Name?.Content;
            if (string.IsNullOrWhiteSpace(codeName) ||
                finalPfs.GmlEntries.ContainsKey(codeName) ||
                finalPfs.AsmEntries.ContainsKey(codeName))
            {
                continue;
            }

            string? originalGml = GetOriginalGml(originalData, decompileCtx, decompileCache, codeName);
            if (string.IsNullOrWhiteSpace(originalGml) ||
                !ContainsShiftedAssetName(originalGml, originalAssetRemap))
            {
                continue;
            }

            finalPfs.AddGmlEntry(codeName, RemapAssetIndicesGml(originalGml, originalAssetRemap), codeName);
            added++;
        }

        return added;
    }

    private static bool IsRealAssetOrderEntry(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, "(null)", StringComparison.Ordinal) &&
        !int.TryParse(value, out _);

    private static (Dictionary<string, List<string>> sections, Dictionary<string, string> counts) ParseAssetOrderText(string text)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentSection = null;
        bool inCounts = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("@@") && line.EndsWith("@@"))
            {
                currentSection = line.Trim('@').ToLowerInvariant();
                inCounts = currentSection == "counts";
                if (!inCounts && !sections.ContainsKey(currentSection))
                    sections[currentSection] = [];
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) continue;

            if (inCounts)
            {
                var eqIdx = line.IndexOf('=');
                if (eqIdx > 0)
                    counts[line[..eqIdx]] = line[(eqIdx + 1)..];
            }
            else if (currentSection != null && sections.TryGetValue(currentSection, out var list))
            {
                list.Add(line);
            }
        }

        return (sections, counts);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Variables/Functions Merge (union)
    // ═══════════════════════════════════════════════════════════════════

    private static string? MergeVariablesFunctions(UndertaleData originalData, PatchFileSystem[] patches)
    {
        // Variables/Functions are index-sensitive for bytecode. Preserve a full
        // table order from the most complete code-bearing patch, then append
        // entries only present in other patches.
        var variables = new List<(string name, int instType, int varId)>();
        var variableKeys = new HashSet<(string name, int instType)>();
        var functions = new List<string>();
        var functionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codeEntries = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, object>? varCounts = null;
        var codeMetadata = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        bool foundAny = SeedVariablesFunctionsFromOriginal(originalData, variables, functions, codeEntries);
        variableKeys = [.. variables.Select(v => (v.name, v.instType))];
        functionKeys = functions.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var pfs in patches)
        {
            var vfPath = $"{pfs.HelpersPrefix}/variables_functions.json";
            if (!pfs.FileExists(vfPath)) continue;
            foundAny = true;

            using var doc = JsonDocument.Parse(pfs.ReadAllText(vfPath));
            var root = doc.RootElement;

            // Variables: union by (name, instanceType), later patch wins on VarID conflict
            if (root.TryGetProperty("variables", out var varArray))
            {
                var patchVariables = new List<(string name, int instType, int varId)>();
                foreach (var v in varArray.EnumerateArray())
                {
                    var name = v.GetProperty("n").GetString() ?? "";
                    int instType = v.GetProperty("t").GetInt32();
                    int varId = v.GetProperty("id").GetInt32();
                    if (!string.IsNullOrEmpty(name))
                        patchVariables.Add((name, instType, varId));
                }

                if (patchVariables.Count > variables.Count)
                {
                    variables = patchVariables;
                    variableKeys = [.. patchVariables.Select(v => (v.name, v.instType))];
                }
                else
                {
                    foreach (var variable in patchVariables)
                    {
                        if (variableKeys.Add((variable.name, variable.instType)))
                            variables.Add(variable);
                    }
                }
            }

            // Functions: union
            if (root.TryGetProperty("functions", out var funcArray))
            {
                var patchFunctions = new List<string>();
                foreach (var f in funcArray.EnumerateArray())
                {
                    var fname = f.GetString();
                    if (!string.IsNullOrEmpty(fname))
                    {
                        fname = CanonicalizeFunctionName(fname);
                        patchFunctions.Add(fname);
                    }
                }

                if (patchFunctions.Count > functions.Count)
                {
                    functions = patchFunctions;
                    functionKeys = patchFunctions.ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    foreach (var function in patchFunctions)
                    {
                        if (functionKeys.Add(function))
                            functions.Add(function);
                    }
                }
            }

            // CodeEntries: union
            if (root.TryGetProperty("codeEntries", out var ceArray))
            {
                foreach (var ce in ceArray.EnumerateArray())
                {
                    if (ce.ValueKind == JsonValueKind.Object)
                    {
                        string? key = ce.TryGetProperty("key", out var keyElem) ? keyElem.GetString() : null;
                        string? name = ce.TryGetProperty("name", out var nameElem) ? nameElem.GetString() : null;
                        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name))
                            continue;

                        var descriptor = new Dictionary<string, object>
                        {
                            ["key"] = key,
                            ["name"] = name,
                            ["occurrence"] = ce.TryGetProperty("occurrence", out var occElem) ? occElem.GetInt32() : 1
                        };
                        if (ce.TryGetProperty("parent", out var parentElem))
                        {
                            var parent = parentElem.GetString();
                            if (!string.IsNullOrWhiteSpace(parent))
                                descriptor["parent"] = parent;
                        }
                        codeEntries[key] = descriptor;
                    }
                    else if (ce.ValueKind == JsonValueKind.String)
                    {
                        var ceName = ce.GetString();
                        if (string.IsNullOrWhiteSpace(ceName))
                            continue;

                        if (!codeEntries.ContainsKey(ceName))
                        {
                            codeEntries[ceName] = new Dictionary<string, object>
                            {
                                ["key"] = ceName,
                                ["name"] = ceName,
                                ["occurrence"] = 1
                            };
                        }
                    }
                }
            }

            // VarCounts: later patch wins (highest priority has final state)
            if (root.TryGetProperty("varCounts", out var vcElem))
            {
                varCounts = [];
                if (vcElem.TryGetProperty("varCount1", out var vc1)) varCounts["varCount1"] = vc1.GetInt32();
                if (vcElem.TryGetProperty("varCount2", out var vc2)) varCounts["varCount2"] = vc2.GetInt32();
                if (vcElem.TryGetProperty("maxLocalVarCount", out var mlvc)) varCounts["maxLocalVarCount"] = mlvc.GetInt32();
            }

            // CodeMetadata: union (later patch wins per entry)
            if (root.TryGetProperty("codeMetadata", out var cmElem) && cmElem.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in cmElem.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        var arr = prop.Value.EnumerateArray().Select(e => e.GetInt32()).ToArray();
                        if (arr.Length >= 2)
                            codeMetadata[prop.Name] = arr;
                    }
                }
            }
        }

        if (!foundAny) return null;

        // Serialize
        var varList = variables
            .Select(v => new Dictionary<string, object> { ["n"] = v.name, ["t"] = v.instType, ["id"] = v.varId })
            .ToList();

        var result = new Dictionary<string, object>
        {
            ["variables"] = varList,
            ["functions"] = functions,
            ["codeEntries"] = codeEntries
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => kvp.Value)
                .ToList()
        };

        if (varCounts != null)
            result["varCounts"] = varCounts;

        if (codeMetadata.Count > 0)
            result["codeMetadata"] = codeMetadata;

        return JsonSerializer.Serialize(result);
    }

    private static bool SeedVariablesFunctionsFromOriginal(
        UndertaleData originalData,
        List<(string name, int instType, int varId)> variables,
        List<string> functions,
        Dictionary<string, Dictionary<string, object>> codeEntries)
    {
        bool added = false;

        foreach (var variable in originalData.Variables ?? [])
        {
            var variableEntry = variable;
            if (variableEntry == null)
                continue;

            var name = variableEntry.Name?.Content;
            if (string.IsNullOrEmpty(name))
                continue;

            int instType = (int)variableEntry.InstanceType;
            variables.Add((name, instType, variableEntry.VarID));
            added = true;
        }

        foreach (var function in originalData.Functions ?? [])
        {
            var name = function?.Name?.Content;
            if (string.IsNullOrEmpty(name))
                continue;

            functions.Add(name);
            added = true;
        }

        foreach (var descriptor in CodeEntryArchiveIdentity.DescribeEntries(originalData))
        {
            if (string.IsNullOrWhiteSpace(descriptor.ArchiveKey) || string.IsNullOrWhiteSpace(descriptor.LogicalName))
                continue;

            var entry = new Dictionary<string, object>
            {
                ["key"] = descriptor.ArchiveKey,
                ["name"] = descriptor.LogicalName,
                ["occurrence"] = descriptor.Occurrence
            };
            if (!string.IsNullOrWhiteSpace(descriptor.ParentArchiveKey))
                entry["parent"] = descriptor.ParentArchiveKey!;

            codeEntries[descriptor.ArchiveKey] = entry;
            added = true;
        }

        return added;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Object Events Merge (union per object)
    // ═══════════════════════════════════════════════════════════════════

    private static string? MergeObjectEvents(PatchFileSystem[] patches)
    {
        // Union events per object; later patch wins on conflict.
        // Collision events (type=4) keyed by object name (cn), others by type+subtype.
        var merged = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
        bool foundAny = false;

        foreach (var pfs in patches)
        {
            var oePath = $"{pfs.HelpersPrefix}/object_events.json";
            if (!pfs.FileExists(oePath)) continue;
            foundAny = true;

            using var doc = JsonDocument.Parse(pfs.ReadAllText(oePath));
            foreach (var obj in doc.RootElement.EnumerateObject())
            {
                var events = new List<Dictionary<string, object>>();

                foreach (var evt in obj.Value.EnumerateArray())
                {
                    int t = evt.GetProperty("t").GetInt32();
                    uint s = evt.GetProperty("s").GetUInt32();

                    var evtDict = new Dictionary<string, object> { ["t"] = t, ["s"] = s };
                    if (evt.TryGetProperty("c", out var c)) evtDict["c"] = c.GetString() ?? "";
                    if (evt.TryGetProperty("cn", out var cn)) evtDict["cn"] = cn.GetString() ?? "";
                    if (evt.TryGetProperty("co", out var co)) evtDict["co"] = co.GetInt32();

                    events.Add(evtDict);
                }

                if (!merged.TryGetValue(obj.Name, out var existingEvents))
                {
                    merged[obj.Name] = events;
                }
                else
                {
                    // Merge: add events from this patch, replacing duplicates
                    // For collision events (type=4), key by collision object NAME (cn) to handle different orderings
                    // For all other events, key by (type, subtype)
                    var existingKeys = new Dictionary<string, int>();
                    for (int i = 0; i < existingEvents.Count; i++)
                    {
                        int et = Convert.ToInt32(existingEvents[i]["t"]);
                        string key = et == 4 && existingEvents[i].TryGetValue("cn", out var ecn) && ecn is string ecnStr && ecnStr != ""
                            ? $"{et}_cn_{ecnStr}_{(existingEvents[i].TryGetValue("co", out var eco) ? Convert.ToInt32(eco) : -1)}"
                            : $"{et}_{existingEvents[i]["s"]}";
                        existingKeys[key] = i;
                    }

                    foreach (var evt in events)
                    {
                        int et = Convert.ToInt32(evt["t"]);
                        string key = et == 4 && evt.TryGetValue("cn", out var ecn) && ecn is string ecnStr && ecnStr != ""
                            ? $"{et}_cn_{ecnStr}_{(evt.TryGetValue("co", out var eco) ? Convert.ToInt32(eco) : -1)}"
                            : $"{et}_{evt["s"]}";
                        if (existingKeys.TryGetValue(key, out int idx))
                            existingEvents[idx] = evt; // Overwrite
                        else
                            existingEvents.Add(evt); // New event
                    }
                }
            }
        }

        if (!foundAny) return null;
        return JsonSerializer.Serialize(merged);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Texture Helpers Merge (TPIs + frame maps)
    // ═══════════════════════════════════════════════════════════════════

    private static void MergeTextureHelpers(
        PatchFileSystem finalPfs,
        PatchFileSystem[] patches,
        UndertaleData originalData,
        int originalEmbeddedTextureCount,
        int originalTpiCount,
        List<ConflictEntry> conflicts)
    {
        foreach (var path in finalPfs.GetAllFilePaths()
                     .Where(p => p.StartsWith("EmbeddedTextures/", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            finalPfs.RemoveFile(path);
        }

        finalPfs.RemoveFile("Helpers/texture_page_items.json");
        finalPfs.RemoveFile("Helpers/sprite_frame_map.json");
        finalPfs.RemoveFile("TexturePageItems/texture_page_items.json");

        var textureOffsets = new int[patches.Length];
        int mergedTextureCount = originalEmbeddedTextureCount;
        if (HasEmbeddedTexturePayload(patches))
        {
            for (int pi = 0; pi < patches.Length; pi++)
            {
                textureOffsets[pi] = mergedTextureCount - originalEmbeddedTextureCount;

                var embeddedRoot = "EmbeddedTextures";
                if (!patches[pi].DirectoryExists(embeddedRoot))
                    continue;

                foreach (var texDir in patches[pi].GetDirectories(embeddedRoot))
                {
                    int patchIndex = ParseTrailingIndex(Path.GetFileName(texDir));
                    if (patchIndex < 0)
                        continue;

                    int mergedIndex = patchIndex < originalEmbeddedTextureCount
                        ? patchIndex
                        : originalEmbeddedTextureCount + textureOffsets[pi] + (patchIndex - originalEmbeddedTextureCount);
                    string mergedName = $"texture_{mergedIndex:D4}";
                    foreach (var file in patches[pi].GetFiles(texDir))
                    {
                        var bytes = patches[pi].ReadAllBytes(file);
                        string ext = Path.GetExtension(file);
                        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
                            continue;
                        string dest = $"EmbeddedTextures/{mergedName}/{mergedName}{ext}";
                        finalPfs.AddFile(dest, bytes);
                    }

                    var sourceJsonPath = $"{texDir}/{Path.GetFileName(texDir)}.json";
                    if (patches[pi].FileExists(sourceJsonPath))
                    {
                        var remappedJson = RemapEmbeddedTextureJson(patches[pi].ReadAllBytes(sourceJsonPath), mergedIndex);
                        finalPfs.AddFile($"EmbeddedTextures/{mergedName}/{mergedName}.json", remappedJson);
                    }
                }

                int patchTextureCount = GetPatchCount(patches[pi], "EmbeddedTextures");
                if (patchTextureCount > originalEmbeddedTextureCount)
                    mergedTextureCount += patchTextureCount - originalEmbeddedTextureCount;
            }
        }

        // texture_page_items.json: merge existing TPIs by priority, then append new TPIs.
        // Existing TPIs can be modified in-place by mods, so indices 0..origCount-1
        // are not necessarily identical across patches.
        var tpiOffsets = new int[patches.Length]; // TPI index offset for each patch's new entries

        // Collect all patches' TPI data
        var allTpis = new List<int[][]>();
        var referencedChangedTpis = new List<HashSet<int>>();
        foreach (var pfs in patches)
        {
            var tpiPath = $"{pfs.HelpersPrefix}/texture_page_items.json";
            if (!pfs.FileExists(tpiPath))
            {
                allTpis.Add([]);
                referencedChangedTpis.Add([]);
                continue;
            }
            var tpiData = JsonSerializer.Deserialize<int[][]>(pfs.ReadAllText(tpiPath));
            allTpis.Add(tpiData ?? []);
            referencedChangedTpis.Add(BuildTexturePageItemsReferencedByChangedResources(pfs));
        }

        // Build merged TPI list
        var tpiList = new List<int[]>();
        var originalTpis = BuildOriginalTexturePageItems(originalData);

        for (int i = 0; i < originalTpiCount; i++)
        {
            int[] selected = i < originalTpis.Length
                ? (int[])originalTpis[i].Clone()
                : [];

            for (int pi = 0; pi < patches.Length; pi++)
            {
                var patchTpis = allTpis[pi];
                if (i >= patchTpis.Length)
                    continue;

                var candidate = patchTpis[i];
                if (!referencedChangedTpis[pi].Contains(i))
                    continue;

                bool candidateChanged = i >= originalTpis.Length ||
                    !TexturePageItemEquals(candidate, originalTpis[i]);
                if (!candidateChanged)
                    continue;

                selected = RemapTexturePageItemTextureIndex(candidate, originalEmbeddedTextureCount, textureOffsets[pi]);
            }

            tpiList.Add(selected);
        }

        // Append new TPIs from each patch, tracking offsets
        for (int pi = 0; pi < patches.Length; pi++)
        {
            tpiOffsets[pi] = tpiList.Count - originalTpiCount;
            var patchTpis = allTpis[pi];
            for (int i = originalTpiCount; i < patchTpis.Length; i++)
            {
                tpiList.Add(RemapTexturePageItemTextureIndex(patchTpis[i], originalEmbeddedTextureCount, textureOffsets[pi]));
            }
        }

        bool mergedTpiHelpers = false;
        if (tpiList.Count > 0)
        {
            finalPfs.AddTextFile("Helpers/texture_page_items.json", JsonSerializer.Serialize(tpiList.ToArray()));
            mergedTpiHelpers = true;
        }

        // sprite_frame_map.json: union of entries, adjusting TPI indices for each patch
        var mergedSprites = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        var mergedBgs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var mergedFonts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int pi = 0; pi < patches.Length; pi++)
        {
            var sfmPath = $"{patches[pi].HelpersPrefix}/sprite_frame_map.json";
            if (!patches[pi].FileExists(sfmPath)) continue;

            using var doc = JsonDocument.Parse(patches[pi].ReadAllText(sfmPath));
            int offset = tpiOffsets[pi];

            if (doc.RootElement.TryGetProperty("sprites", out var sprites))
            {
                foreach (var sp in sprites.EnumerateObject())
                {
                    if (!PatchHasResourcePayload(patches[pi], "Sprites", sp.Name))
                        continue;

                    var indices = sp.Value.EnumerateArray().Select(e => e.GetInt32()).ToArray();
                    // Shift indices for new TPIs (>= originalTpiCount)
                    for (int i = 0; i < indices.Length; i++)
                    {
                        if (indices[i] >= originalTpiCount)
                            indices[i] += offset;
                    }
                    mergedSprites[sp.Name] = indices; // Later patches overwrite
                }
            }

            if (doc.RootElement.TryGetProperty("backgrounds", out var bgs))
            {
                foreach (var bg in bgs.EnumerateObject())
                {
                    if (!PatchHasAnyResourcePayload(patches[pi], bg.Name, "Backgrounds", "Tilesets"))
                        continue;

                    int idx = bg.Value.GetInt32();
                    mergedBgs[bg.Name] = idx >= originalTpiCount ? idx + offset : idx;
                }
            }

            if (doc.RootElement.TryGetProperty("fonts", out var fonts))
            {
                foreach (var font in fonts.EnumerateObject())
                {
                    if (!PatchHasResourcePayload(patches[pi], "Fonts", font.Name))
                        continue;
                    if (!patches[pi].FileExists($"Fonts/{font.Name}/texture.png"))
                        continue;

                    int idx = font.Value.GetInt32();
                    mergedFonts[font.Name] = idx >= originalTpiCount ? idx + offset : idx;
                }
            }
        }

        if (mergedSprites.Count > 0 || mergedBgs.Count > 0 || mergedFonts.Count > 0)
        {
            var sfm = new Dictionary<string, object>
            {
                ["sprites"] = mergedSprites,
                ["backgrounds"] = mergedBgs,
                ["fonts"] = mergedFonts
            };
            finalPfs.AddTextFile("Helpers/sprite_frame_map.json", JsonSerializer.Serialize(sfm));
        }

        if (mergedTpiHelpers)
        {
            conflicts.RemoveAll(c =>
                c.File.Equals("TexturePageItems/texture_page_items.json", StringComparison.OrdinalIgnoreCase));

            conflicts.Add(new ConflictEntry(
                "TexturePageItems/texture_page_items.json",
                "Resolved",
                "TexturePageItems Merge",
                "Merged helper output",
                "TexturePageItems were rebuilt through helper merge and sprite/frame remapping, so the raw file overwrite was not the final outcome"));
        }

        NormalizeEmbeddedTextureMetadata(finalPfs);
    }

    private static void NormalizeEmbeddedTextureMetadata(PatchFileSystem pfs)
    {
        foreach (var path in pfs.GetAllFilePaths()
                     .Where(p => p.StartsWith("EmbeddedTextures/", StringComparison.OrdinalIgnoreCase) &&
                                 p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                continue;

            int folderIndex = ParseTrailingIndex(parts[^2]);
            int fileIndex = ParseTrailingIndex(Path.GetFileNameWithoutExtension(parts[^1]));
            if (folderIndex < 0 || fileIndex != folderIndex)
                continue;

            pfs.AddFile(path, RemapEmbeddedTextureJson(pfs.ReadAllBytes(path), folderIndex));
        }
    }

    private static int[][] BuildOriginalTexturePageItems(UndertaleData data)
    {
        if (data.TexturePageItems == null)
            return [];

        var embeddedTextureIndices = new Dictionary<UndertaleEmbeddedTexture, int>();
        for (int i = 0; i < data.EmbeddedTextures.Count; i++)
        {
            if (data.EmbeddedTextures[i] != null)
                embeddedTextureIndices[data.EmbeddedTextures[i]] = i;
        }

        var result = new int[data.TexturePageItems.Count][];
        for (int i = 0; i < data.TexturePageItems.Count; i++)
        {
            var tpi = data.TexturePageItems[i];
            int texIdx = tpi?.TexturePage != null && embeddedTextureIndices.TryGetValue(tpi.TexturePage, out int textureIndex)
                ? textureIndex
                : -1;
            result[i] = tpi == null
                ? []
                : [texIdx, tpi.SourceX, tpi.SourceY, tpi.SourceWidth, tpi.SourceHeight,
                    tpi.TargetX, tpi.TargetY, tpi.TargetWidth, tpi.TargetHeight, tpi.BoundingWidth, tpi.BoundingHeight];
        }
        return result;
    }

    private static int[] RemapTexturePageItemTextureIndex(int[] tpi, int originalEmbeddedTextureCount, int textureOffset)
    {
        var result = (int[])tpi.Clone();
        if (result.Length > 0 && result[0] >= originalEmbeddedTextureCount)
            result[0] += textureOffset;
        return result;
    }

    private static bool TexturePageItemEquals(int[] left, int[] right)
    {
        if (left.Length != right.Length)
            return false;
        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
                return false;
        }
        return true;
    }

    private static bool PatchHasResourcePayload(PatchFileSystem pfs, string resourceRoot, string resourceName)
    {
        string prefix = $"{resourceRoot}/{resourceName}/";
        return pfs.GetAllFilePaths().Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PatchHasAnyResourcePayload(PatchFileSystem pfs, string resourceName, params string[] resourceRoots)
    {
        foreach (string root in resourceRoots)
        {
            if (PatchHasResourcePayload(pfs, root, resourceName))
                return true;
        }
        return false;
    }

    private static HashSet<int> BuildTexturePageItemsReferencedByChangedResources(PatchFileSystem pfs)
    {
        var result = new HashSet<int>();
        string sfmPath = $"{pfs.HelpersPrefix}/sprite_frame_map.json";
        if (!pfs.FileExists(sfmPath))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(pfs.ReadAllText(sfmPath));
            if (doc.RootElement.TryGetProperty("sprites", out var sprites))
            {
                foreach (var sp in sprites.EnumerateObject())
                {
                    if (!PatchHasResourcePayload(pfs, "Sprites", sp.Name))
                        continue;

                    foreach (var idx in sp.Value.EnumerateArray())
                        AddTpiIndex(result, idx.GetInt32());
                }
            }

            if (doc.RootElement.TryGetProperty("backgrounds", out var bgs))
            {
                foreach (var bg in bgs.EnumerateObject())
                {
                    if (!PatchHasAnyResourcePayload(pfs, bg.Name, "Backgrounds", "Tilesets"))
                        continue;

                    AddTpiIndex(result, bg.Value.GetInt32());
                }
            }

            if (doc.RootElement.TryGetProperty("fonts", out var fonts))
            {
                foreach (var font in fonts.EnumerateObject())
                {
                    if (!PatchHasResourcePayload(pfs, "Fonts", font.Name))
                        continue;

                    AddTpiIndex(result, font.Value.GetInt32());
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private static void AddTpiIndex(HashSet<int> set, int index)
    {
        if (index >= 0)
            set.Add(index);
    }

    private static bool ShouldKeepExistingLanguageSprite(string filePath)
    {
        if (!filePath.StartsWith("Sprites/", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = filePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return false;

        string spriteName = parts[1];
        return spriteName.StartsWith("spr_ja_", StringComparison.OrdinalIgnoreCase) ||
               spriteName.StartsWith("bg_lang_ja_", StringComparison.OrdinalIgnoreCase) ||
               spriteName.StartsWith("spr_fnt_ja_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLanguageSpriteName(string spriteName) =>
        spriteName.StartsWith("spr_ja_", StringComparison.OrdinalIgnoreCase) ||
        spriteName.StartsWith("bg_lang_ja_", StringComparison.OrdinalIgnoreCase) ||
        spriteName.StartsWith("spr_fnt_ja_", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocaleArtSpriteName(string spriteName) =>
        spriteName.StartsWith("spr_ja_", StringComparison.OrdinalIgnoreCase) ||
        spriteName.StartsWith("bg_lang_ja_", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldKeepExistingFontCoverage(
        string filePath,
        PatchFileSystem finalPfs,
        PatchFileSystem incomingPfs,
        UndertaleData originalData,
        out string details)
    {
        details = "";
        var parts = filePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 ||
            !parts[0].Equals("Fonts", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fontName = parts[1];
        if (!fontName.StartsWith("fnt_ja_", StringComparison.OrdinalIgnoreCase))
            return false;

        string fontJsonPath = $"Fonts/{fontName}/font.json";
        if (!finalPfs.FileExists(fontJsonPath) || !incomingPfs.FileExists(fontJsonPath))
            return false;

        int existingGlyphs = TryGetFontGlyphCount(finalPfs.ReadAllBytes(fontJsonPath));
        int incomingGlyphs = TryGetFontGlyphCount(incomingPfs.ReadAllBytes(fontJsonPath));
        int originalGlyphs = originalData.Fonts.ByName(fontName)?.Glyphs?.Count ?? -1;

        if (existingGlyphs < 0 || incomingGlyphs < 0 || originalGlyphs < 0)
            return false;

        bool existingHasExpandedCoverage = existingGlyphs >= originalGlyphs + 500 &&
                                           existingGlyphs >= incomingGlyphs + 500;
        if (!existingHasExpandedCoverage)
            return false;

        details = $"Kept existing {fontName} payload because it has expanded glyph coverage ({existingGlyphs} glyphs) while incoming has {incomingGlyphs}; replacing it would drop Korean glyphs from the lower-priority translation.";
        return true;
    }

    private static int TryGetFontGlyphCount(byte[] fontJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(fontJson);
            return doc.RootElement.TryGetProperty("glyphs", out var glyphs) &&
                   glyphs.ValueKind == JsonValueKind.Array
                ? glyphs.GetArrayLength()
                : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static bool ShouldHighPriorityBaseCodeOverrideLowerChange(string codeName, PatchFileSystem incomingPatch)
    {
        if (!codeName.Equals("gml_Object_DEVICE_MENU_Draw_0", StringComparison.Ordinal) &&
            !codeName.Equals("gml_Object_DEVICE_MENU_Step_0", StringComparison.Ordinal))
        {
            return false;
        }

        return incomingPatch.DirectoryExists("Fonts") ||
               incomingPatch.DirectoryExists("EmbeddedTextures") ||
               incomingPatch.FileExists($"{incomingPatch.HelpersPrefix}/texture_page_items.json") ||
               incomingPatch.FileExists($"{incomingPatch.HelpersPrefix}/sprite_frame_map.json");
    }

    private static string CanonicalizeFunctionName(string functionName)
    {
        const string nestedGlobalScriptPrefix = "gml_Script_gml_GlobalScript_";
        if (functionName.StartsWith(nestedGlobalScriptPrefix, StringComparison.Ordinal))
            return "gml_Script_" + functionName[nestedGlobalScriptPrefix.Length..];
        return functionName;
    }

    private static bool GmlTextEquals(string? left, string? right)
    {
        if (left == null || right == null)
            return left == right;
        return string.Equals(NormalizeGmlText(left), NormalizeGmlText(right), StringComparison.Ordinal);
    }

    private static string NormalizeGmlText(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Trim().TrimStart('\uFEFF');
    }

    // ═══════════════════════════════════════════════════════════════════
    // 3-Way Code Merge (LCS-based)
    // ═══════════════════════════════════════════════════════════════════

    private static string? GetOriginalGml(
        UndertaleData data,
        GlobalDecompileContext ctx,
        Dictionary<string, string?> cache,
        string codeName)
    {
        if (cache.TryGetValue(codeName, out var cached)) return cached;

        var code = data.Code.ByName(codeName);
        if (code == null)
        {
            cache[codeName] = null;
            return null;
        }

        try
        {
            var gml = new DecompileContext(ctx, code, data.ToolInfo.DecompilerSettings).DecompileToString();
            cache[codeName] = gml;
            return gml;
        }
        catch
        {
            cache[codeName] = null;
            return null;
        }
    }

    /// <summary>
    /// 3-way merge: combine changes from ours (accumulated FinalPFS) and theirs (current patch)
    /// relative to the base (original). On conflict, theirs (higher priority) wins.
    /// Returns (mergedText, hasConflicts). Null mergedText = total failure.
    /// </summary>
    private static (string? merged, bool hasConflicts) ThreeWayMerge(
        string baseText, string oursText, string theirsText)
    {
        // Trivial cases
        if (oursText == theirsText) return (oursText, false);
        if (oursText == baseText) return (theirsText, false);
        if (theirsText == baseText) return (oursText, false);

        var baseLines = SplitLines(baseText);
        var oursLines = SplitLines(oursText);
        var theirsLines = SplitLines(theirsText);

        // Size limit for O(NM) LCS
        long complexity = Math.Max(
            (long)baseLines.Length * oursLines.Length,
            (long)baseLines.Length * theirsLines.Length);
        if (complexity > 10_000_000)
            return (theirsText, true); // Too large for 3-way, use higher priority

        // Compute edits from base → each side
        var oursEdits = ComputeEdits(baseLines, oursLines);
        var theirsEdits = ComputeEdits(baseLines, theirsLines);

        if (oursEdits.Count == 0) return (theirsText, false);
        if (theirsEdits.Count == 0) return (oursText, false);

        // Check for overlapping non-identical edits (conflicts)
        bool hasConflicts = false;
        foreach (var (oBaseStart, oBaseEnd, oReplacement) in oursEdits)
        {
            foreach (var (tBaseStart, tBaseEnd, tReplacement) in theirsEdits)
            {
                if (oBaseStart < tBaseEnd && tBaseStart < oBaseEnd)
                {
                    if (oBaseStart != tBaseStart || oBaseEnd != tBaseEnd ||
                        !oReplacement.SequenceEqual(tReplacement))
                    {
                        hasConflicts = true;
                        break;
                    }
                }
            }
            if (hasConflicts) break;
        }

        var allEdits = new List<(int BaseStart, int BaseEnd, string[] Replacement, int Side)>();
        allEdits.AddRange(oursEdits.Select(e => (e.BaseStart, e.BaseEnd, e.Replacement, Side: 0)));
        allEdits.AddRange(theirsEdits.Select(e => (e.BaseStart, e.BaseEnd, e.Replacement, Side: 1)));
        allEdits = [.. allEdits
            .OrderBy(e => e.BaseStart)
            .ThenBy(e => e.BaseEnd)
            .ThenBy(e => e.Side)];

        var result = new List<string>();
        int pos = 0;
        for (int editIdx = 0; editIdx < allEdits.Count;)
        {
            var cluster = new List<(int BaseStart, int BaseEnd, string[] Replacement, int Side)> { allEdits[editIdx] };
            int start = allEdits[editIdx].BaseStart;
            int end = allEdits[editIdx].BaseEnd;
            editIdx++;

            while (editIdx < allEdits.Count && EditsOverlapCluster(allEdits[editIdx], start, end))
            {
                cluster.Add(allEdits[editIdx]);
                end = Math.Max(end, allEdits[editIdx].BaseEnd);
                editIdx++;
            }

            // Add unchanged base lines before this edit
            for (int i = pos; i < start; i++)
                result.Add(baseLines[i]);

            if (cluster.Count == 1)
            {
                result.AddRange(cluster[0].Replacement);
            }
            else
            {
                hasConflicts = true;
                var chosen = cluster.Any(e => e.Side == 1)
                    ? cluster.Where(e => e.Side == 1).ToList()
                    : [.. cluster.Where(e => e.Side == 0)];
                result.AddRange(BuildClusterReplacement(baseLines, start, end, chosen));
            }
            pos = end;
        }
        // Add remaining base lines
        for (int i = pos; i < baseLines.Length; i++)
            result.Add(baseLines[i]);

        return (string.Join("\n", result), hasConflicts);
    }

    private static bool HasEmbeddedTexturePayload(PatchFileSystem[] patches) =>
        patches.Any(p => p.DirectoryExists("EmbeddedTextures"));

    private static int ParseTrailingIndex(string value)
    {
        var match = TrailingNumberRegex().Match(value);
        return match.Success && int.TryParse(match.Groups[1].Value, out int index) ? index : -1;
    }

    private static byte[] RemapEmbeddedTextureJson(byte[] bytes, int index)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", index);
                writer.WriteString("name", $"Texture {index}");
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("index") || prop.NameEquals("name"))
                        continue;
                    prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return stream.ToArray();
        }
        catch
        {
            return bytes;
        }
    }

    private static int GetPatchCount(PatchFileSystem pfs, string key)
    {
        var aoPath = $"{pfs.HelpersPrefix}/asset_order.txt";
        if (!pfs.FileExists(aoPath))
            return 0;
        var (_, counts) = ParseAssetOrderText(pfs.ReadAllText(aoPath));
        return counts.TryGetValue(key, out var value) && int.TryParse(value, out var count) ? count : 0;
    }

    private static bool EditsOverlapCluster(
        (int BaseStart, int BaseEnd, string[] Replacement, int Side) edit,
        int clusterStart,
        int clusterEnd)
    {
        if (edit.BaseStart < clusterEnd)
            return true;
        if (edit.BaseStart == clusterStart && edit.BaseEnd == clusterStart)
            return true;
        if (edit.BaseStart == clusterEnd && edit.BaseStart == edit.BaseEnd)
            return true;
        return false;
    }

    private static IEnumerable<string> BuildClusterReplacement(
        string[] baseLines,
        int clusterStart,
        int clusterEnd,
        List<(int BaseStart, int BaseEnd, string[] Replacement, int Side)> chosen)
    {
        chosen = [.. chosen.OrderBy(e => e.BaseStart).ThenBy(e => e.BaseEnd)];
        int pos = clusterStart;
        foreach (var (BaseStart, BaseEnd, Replacement, Side) in chosen)
        {
            if (BaseStart < pos)
                continue;
            for (int i = pos; i < BaseStart; i++)
                yield return baseLines[i];
            foreach (var line in Replacement)
                yield return line;
            pos = BaseEnd;
        }
        for (int i = pos; i < clusterEnd; i++)
            yield return baseLines[i];
    }

    /// <summary>
    /// Compute edits: list of (baseStart, baseEnd, replacement[]) that transform base into mod.
    /// Uses LCS (Longest Common Subsequence) to find matching lines.
    /// </summary>
    private static List<(int BaseStart, int BaseEnd, string[] Replacement)> ComputeEdits(
        string[] baseLines, string[] modLines)
    {
        var lcs = ComputeLCS(baseLines, modLines);
        var edits = new List<(int, int, string[])>();

        int basePrev = 0, modPrev = 0;
        foreach (var (bi, mi) in lcs)
        {
            if (bi != basePrev || mi != modPrev)
                edits.Add((basePrev, bi, modLines[modPrev..mi]));
            basePrev = bi + 1;
            modPrev = mi + 1;
        }

        if (basePrev < baseLines.Length || modPrev < modLines.Length)
            edits.Add((basePrev, baseLines.Length, modLines[modPrev..]));

        return edits;
    }

    /// <summary>
    /// O(NM) LCS using dynamic programming. Returns matched (baseIndex, modIndex) pairs.
    /// </summary>
    private static List<(int, int)> ComputeLCS(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0 || m == 0) return [];

        var dp = new int[n + 1, m + 1];
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
                dp[i, j] = a[i - 1] == b[j - 1] ? dp[i - 1, j - 1] + 1 : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        // Backtrack
        var result = new List<(int, int)>();
        int x = n, y = m;
        while (x > 0 && y > 0)
        {
            if (a[x - 1] == b[y - 1])
            {
                result.Add((x - 1, y - 1));
                x--; y--;
            }
            else if (dp[x - 1, y] >= dp[x, y - 1])
                x--;
            else
                y--;
        }

        result.Reverse();
        return result;
    }

    private static string[] SplitLines(string text)
    {
        return text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
    }

    // ═══════════════════════════════════════════════════════════════════
    // JSON Deep Merge
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Deep merge two JSON documents. Object fields are merged recursively;
    /// arrays and primitives from the incoming document overwrite the existing.
    /// </summary>
    private static byte[] JsonDeepMerge(byte[] existingBytes, byte[] incomingBytes)
    {
        var existing = JsonNode.Parse(existingBytes);
        var incoming = JsonNode.Parse(incomingBytes);

        if (existing is JsonObject existObj && incoming is JsonObject incomingObj)
        {
            MergeJsonObjects(existObj, incomingObj);
            return Encoding.UTF8.GetBytes(existObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        // Non-object root: incoming overwrites
        return incomingBytes;
    }

    private static byte[]? TryThreeWayJsonMerge(
        string filePath,
        UndertaleData originalData,
        byte[] existingBytes,
        byte[] incomingBytes)
    {
        if (TryGetSpriteJsonName(filePath, out var spriteName))
            return TryThreeWayExportedJsonMerge(
                existingBytes,
                incomingBytes,
                tempDir => ResourceExportService.ExportSprites(originalData, tempDir, new HashSet<string>(StringComparer.Ordinal) { spriteName }),
                Path.Combine("Sprites", SafeFileName(spriteName), SafeFileName(spriteName) + ".json"));

        if (filePath.Equals("Options/options.json", StringComparison.OrdinalIgnoreCase))
            return TryThreeWayOptionsMerge(originalData, existingBytes, incomingBytes);

        if (!filePath.Equals("GeneralInfo/GeneralInfo.json", StringComparison.OrdinalIgnoreCase))
            return null;

        var tempDir = Path.Combine(Path.GetTempPath(), $"g3m_base_json_{Guid.NewGuid():N}");
        try
        {
            ResourceExportService.ExportGeneralInfo(originalData, tempDir);
            var basePath = Path.Combine(tempDir, "GeneralInfo", "GeneralInfo.json");
            if (!File.Exists(basePath))
                return null;

            var baseNode = ParseJsonObject(File.ReadAllBytes(basePath));
            var existingNode = ParseJsonObject(existingBytes);
            var incomingNode = ParseJsonObject(incomingBytes);
            if (baseNode == null || existingNode == null || incomingNode == null)
                return null;

            var merged = ThreeWayMergeJsonObjects(baseNode, existingNode, incomingNode);
            return Encoding.UTF8.GetBytes(merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static byte[]? TryThreeWayExportedJsonMerge(
        byte[] existingBytes,
        byte[] incomingBytes,
        Action<string> exportBase,
        string relativeBasePath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"g3m_base_json_{Guid.NewGuid():N}");
        try
        {
            exportBase(tempDir);
            var basePath = Path.Combine(tempDir, relativeBasePath);
            if (!File.Exists(basePath))
                return null;

            var baseNode = ParseJsonObject(File.ReadAllBytes(basePath));
            var existingNode = ParseJsonObject(existingBytes);
            var incomingNode = ParseJsonObject(incomingBytes);
            if (baseNode == null || existingNode == null || incomingNode == null)
                return null;

            var merged = ThreeWayMergeJsonObjects(baseNode, existingNode, incomingNode);
            return Encoding.UTF8.GetBytes(merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static byte[]? TryThreeWayOptionsMerge(
        UndertaleData originalData,
        byte[] existingBytes,
        byte[] incomingBytes)
    {
        if (originalData.Options == null)
            return null;

        var tempDir = Path.Combine(Path.GetTempPath(), $"g3m_base_options_{Guid.NewGuid():N}");
        try
        {
            ResourceExportService.ExportOptions(originalData, tempDir);
            var basePath = Path.Combine(tempDir, "Options", "options.json");
            if (!File.Exists(basePath))
                return null;

            var baseNode = ParseJsonObject(File.ReadAllBytes(basePath));
            var existingNode = ParseJsonObject(existingBytes);
            var incomingNode = ParseJsonObject(incomingBytes);
            if (baseNode == null || existingNode == null || incomingNode == null)
                return null;

            var merged = ThreeWayMergeJsonObjects(baseNode, existingNode, incomingNode);
            merged["constants"] = MergeOptionsConstants(
                baseNode["constants"] as JsonArray,
                existingNode["constants"] as JsonArray,
                incomingNode["constants"] as JsonArray);
            return Encoding.UTF8.GetBytes(merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static JsonArray MergeOptionsConstants(JsonArray? baseArray, JsonArray? oursArray, JsonArray? theirsArray)
    {
        var result = new JsonArray();
        var baseByName = ConstantsByName(baseArray);
        var oursByName = ConstantsByName(oursArray);
        var theirsByName = ConstantsByName(theirsArray);

        var names = baseByName.Keys
            .Concat(oursByName.Keys)
            .Concat(theirsByName.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var name in names)
        {
            baseByName.TryGetValue(name, out var baseValue);
            oursByName.TryGetValue(name, out var oursValue);
            theirsByName.TryGetValue(name, out var theirsValue);

            JsonNode? chosen;
            if (JsonNodeDeepEquals(oursValue, theirsValue))
                chosen = oursValue;
            else if (JsonNodeDeepEquals(oursValue, baseValue))
                chosen = theirsValue;
            else if (JsonNodeDeepEquals(theirsValue, baseValue))
                chosen = oursValue;
            else
                chosen = theirsValue;

            if (chosen != null)
                result.Add(chosen.DeepClone());
        }

        return result;
    }

    private static JsonObject? ParseJsonObject(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bytes = bytes[3..];
        return JsonNode.Parse(bytes) as JsonObject;
    }

    private static Dictionary<string, JsonNode?> ConstantsByName(JsonArray? array)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (array == null) return result;

        foreach (var item in array)
        {
            if (item is JsonObject obj &&
                obj.TryGetPropertyValue("name", out var nameNode) &&
                nameNode?.GetValue<string>() is string name &&
                !string.IsNullOrWhiteSpace(name))
            {
                result[name] = item.DeepClone();
            }
        }

        return result;
    }

    private static JsonObject ThreeWayMergeJsonObjects(JsonObject baseObj, JsonObject oursObj, JsonObject theirsObj)
    {
        var result = new JsonObject();
        var keys = baseObj.Select(p => p.Key)
            .Concat(oursObj.Select(p => p.Key))
            .Concat(theirsObj.Select(p => p.Key))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal);

        foreach (var key in keys)
        {
            baseObj.TryGetPropertyValue(key, out var baseValue);
            oursObj.TryGetPropertyValue(key, out var oursValue);
            theirsObj.TryGetPropertyValue(key, out var theirsValue);

            if (baseValue is JsonObject baseChild &&
                oursValue is JsonObject oursChild &&
                theirsValue is JsonObject theirsChild)
            {
                result[key] = ThreeWayMergeJsonObjects(baseChild, oursChild, theirsChild);
                continue;
            }

            if (baseValue is JsonArray baseArray &&
                oursValue is JsonArray oursArray &&
                theirsValue is JsonArray theirsArray)
            {
                result[key] = ThreeWayMergeJsonArrays(baseArray, oursArray, theirsArray);
                continue;
            }

            bool oursEqualsBase = JsonNodeDeepEquals(oursValue, baseValue);
            bool theirsEqualsBase = JsonNodeDeepEquals(theirsValue, baseValue);

            if (JsonNodeDeepEquals(oursValue, theirsValue))
                result[key] = oursValue?.DeepClone();
            else if (oursEqualsBase)
                result[key] = theirsValue?.DeepClone();
            else if (theirsEqualsBase)
                result[key] = oursValue?.DeepClone();
            else
                result[key] = theirsValue?.DeepClone();
        }

        return result;
    }

    private static JsonArray ThreeWayMergeJsonArrays(JsonArray baseArray, JsonArray oursArray, JsonArray theirsArray)
    {
        if (TryKeyJsonObjectArray(baseArray, out var baseByKey) &&
            TryKeyJsonObjectArray(oursArray, out var oursByKey) &&
            TryKeyJsonObjectArray(theirsArray, out var theirsByKey))
        {
            var result = new JsonArray();
            var keys = baseByKey.Keys
                .Concat(oursByKey.Keys)
                .Concat(theirsByKey.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(k => ArrayKeyOrder(baseArray, oursArray, theirsArray, k))
                .ThenBy(k => k, StringComparer.Ordinal);

            foreach (var key in keys)
            {
                baseByKey.TryGetValue(key, out var baseValue);
                oursByKey.TryGetValue(key, out var oursValue);
                theirsByKey.TryGetValue(key, out var theirsValue);

                if (baseValue is JsonObject baseObj &&
                    oursValue is JsonObject oursObj &&
                    theirsValue is JsonObject theirsObj)
                {
                    result.Add(ThreeWayMergeJsonObjects(baseObj, oursObj, theirsObj));
                    continue;
                }

                var chosen = ChooseThreeWayJsonValue(baseValue, oursValue, theirsValue);
                if (chosen != null)
                    result.Add(chosen.DeepClone());
            }

            return result;
        }

        if (baseArray.Count == oursArray.Count && baseArray.Count == theirsArray.Count)
        {
            var result = new JsonArray();
            for (int i = 0; i < baseArray.Count; i++)
            {
                var baseValue = baseArray[i];
                var oursValue = oursArray[i];
                var theirsValue = theirsArray[i];
                if (baseValue is JsonObject baseObj &&
                    oursValue is JsonObject oursObj &&
                    theirsValue is JsonObject theirsObj)
                    result.Add(ThreeWayMergeJsonObjects(baseObj, oursObj, theirsObj));
                else
                    result.Add(ChooseThreeWayJsonValue(baseValue, oursValue, theirsValue)?.DeepClone());
            }
            return result;
        }

        return ChooseThreeWayJsonValue(baseArray, oursArray, theirsArray)?.DeepClone() as JsonArray
            ?? [];
    }

    private static JsonNode? ChooseThreeWayJsonValue(JsonNode? baseValue, JsonNode? oursValue, JsonNode? theirsValue)
    {
        bool oursEqualsBase = JsonNodeDeepEquals(oursValue, baseValue);
        bool theirsEqualsBase = JsonNodeDeepEquals(theirsValue, baseValue);

        if (JsonNodeDeepEquals(oursValue, theirsValue))
            return oursValue;
        if (oursEqualsBase)
            return theirsValue;
        if (theirsEqualsBase)
            return oursValue;
        return theirsValue;
    }

    private static bool TryKeyJsonObjectArray(JsonArray array, out Dictionary<string, JsonNode?> byKey)
    {
        byKey = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JsonObject obj || !TryGetJsonObjectStableKey(obj, out var key) || byKey.ContainsKey(key))
            {
                byKey.Clear();
                return false;
            }
            byKey[key] = item;
        }
        return true;
    }

    private static bool TryGetJsonObjectStableKey(JsonObject obj, out string key)
    {
        foreach (var propertyName in new[] { "name", "id", "index", "frameIndex" })
        {
            if (!obj.TryGetPropertyValue(propertyName, out var value) || value == null)
                continue;
            key = value.ToJsonString();
            return true;
        }

        key = string.Empty;
        return false;
    }

    private static int ArrayKeyOrder(JsonArray baseArray, JsonArray oursArray, JsonArray theirsArray, string key)
    {
        var order = FindArrayKeyIndex(baseArray, key);
        if (order >= 0) return order;
        order = FindArrayKeyIndex(oursArray, key);
        if (order >= 0) return baseArray.Count + order;
        order = FindArrayKeyIndex(theirsArray, key);
        return order >= 0 ? baseArray.Count + oursArray.Count + order : int.MaxValue;
    }

    private static int FindArrayKeyIndex(JsonArray array, string key)
    {
        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonObject obj &&
                TryGetJsonObjectStableKey(obj, out var itemKey) &&
                itemKey == key)
                return i;
        }
        return -1;
    }

    private static bool JsonNodeDeepEquals(JsonNode? left, JsonNode? right) =>
        left?.ToJsonString() == right?.ToJsonString();

    private static void MergeJsonObjects(JsonObject target, JsonObject source)
    {
        foreach (var prop in source)
        {
            if (prop.Value == null)
            {
                target[prop.Key] = null;
                continue;
            }

            if (target[prop.Key] is JsonObject targetObj && prop.Value is JsonObject sourceObj)
            {
                // Recursive merge for nested objects
                MergeJsonObjects(targetObj, sourceObj);
            }
            else if (target[prop.Key] is JsonArray targetArray && prop.Value is JsonArray sourceArray)
            {
                target[prop.Key] = MergeJsonArrays(targetArray, sourceArray);
            }
            else
            {
                // Overwrite (arrays and primitives)
                target[prop.Key] = prop.Value.DeepClone();
            }
        }
    }

    private static JsonArray MergeJsonArrays(JsonArray target, JsonArray source)
    {
        if (TryKeyJsonObjectArray(target, out var targetByKey) &&
            TryKeyJsonObjectArray(source, out var sourceByKey))
        {
            var result = new JsonArray();
            var keys = targetByKey.Keys
                .Concat(sourceByKey.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(k => ArrayKeyOrder(target, source, [], k))
                .ThenBy(k => k, StringComparer.Ordinal);

            foreach (var key in keys)
            {
                targetByKey.TryGetValue(key, out var targetValue);
                sourceByKey.TryGetValue(key, out var sourceValue);

                if (targetValue is JsonObject targetObj && sourceValue is JsonObject sourceObj)
                {
                    var mergedObj = targetObj.DeepClone().AsObject();
                    MergeJsonObjects(mergedObj, sourceObj);
                    result.Add(mergedObj);
                }
                else
                {
                    result.Add((sourceValue ?? targetValue)?.DeepClone());
                }
            }

            return result;
        }

        if (target.Count == source.Count)
        {
            var result = new JsonArray();
            for (int i = 0; i < target.Count; i++)
            {
                if (target[i] is JsonObject targetObj && source[i] is JsonObject sourceObj)
                {
                    var mergedObj = targetObj.DeepClone().AsObject();
                    MergeJsonObjects(mergedObj, sourceObj);
                    result.Add(mergedObj);
                }
                else
                {
                    result.Add(source[i]?.DeepClone());
                }
            }
            return result;
        }

        return source.DeepClone().AsArray();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Manifest Generation
    // ═══════════════════════════════════════════════════════════════════

    private static G3MPatchManifest BuildMergedManifest(
        PatchFileSystem[] patches,
        PatchFileSystem finalPfs)
    {
        // Use first patch's original info (all patches share the same original)
        DataFileInfo? originalInfo = null;
        foreach (var pfs in patches)
        {
            if (pfs.Manifest?.Original != null)
            {
                originalInfo = pfs.Manifest.Original;
                break;
            }
        }

        // Combine resource changes from all patches
        var mergedResources = new Dictionary<string, ResourceTypeChanges>();

        foreach (var pfs in patches)
        {
            if (pfs.Manifest?.Resources == null) continue;
            foreach (var (resType, changes) in pfs.Manifest.Resources)
            {
                if (!mergedResources.TryGetValue(resType, out var merged))
                {
                    merged = new ResourceTypeChanges { Changed = [], New = [], Deleted = [] };
                    mergedResources[resType] = merged;
                }
                merged.Changed ??= [];
                merged.New ??= [];
                merged.Deleted ??= [];

                // Merge changed/new (deduplicate by name)
                var existingNames = new HashSet<string>(
                    (merged.Changed ?? []).Select(c => c.Name ?? "")
                    .Concat((merged.New ?? []).Select(n => n.Name ?? "")),
                    StringComparer.OrdinalIgnoreCase);

                var mergedChanged = merged.Changed!;
                var mergedNew = merged.New!;

                foreach (var c in changes.Changed ?? [])
                    if (!string.IsNullOrEmpty(c.Name) && existingNames.Add(c.Name))
                        mergedChanged.Add(c);

                foreach (var n in changes.New ?? [])
                    if (!string.IsNullOrEmpty(n.Name) && existingNames.Add(n.Name))
                        mergedNew.Add(n);

                var deletedNames = new HashSet<string>(merged.Deleted ?? [], StringComparer.OrdinalIgnoreCase);
                foreach (var deleted in changes.Deleted ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(deleted) && deletedNames.Add(deleted))
                        merged.Deleted!.Add(deleted);
                }
            }
        }

        // Calculate statistics from final PFS
        var stats = new PatchStatistics();
        foreach (var (_, changes) in mergedResources)
        {
            stats.TotalChanged += changes.Changed?.Count ?? 0;
            stats.TotalNew += changes.New?.Count ?? 0;
            stats.TotalDeleted += changes.Deleted?.Count ?? 0;
        }
        stats.TotalChangedFiles = finalPfs.FileCount + finalPfs.GmlEntries.Count + finalPfs.AsmEntries.Count;

        return new G3MPatchManifest
        {
            CreatedAt = DateTime.UtcNow.ToString("o"),
            Tool = new Models.ToolInfo { Name = "G3MTool", Version = AppVersionService.Version },
            Original = originalInfo,
            Resources = mergedResources,
            Statistics = stats,
            ApplyPlan = PatchService.BuildPatchApplyPlan(mergedResources)
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // Conflict Report Generation
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Build display names for patches. Uses parent folder name when filenames collide.
    /// </summary>
    private static string[] BuildPatchNames(List<string> patchPaths)
    {
        var rawNames = patchPaths.Select(Path.GetFileNameWithoutExtension).ToArray();

        // Check for duplicates
        var nameGroups = rawNames.Select((n, i) => (name: n, idx: i))
            .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .Select(x => x.idx)
            .ToHashSet();

        if (nameGroups.Count == 0) return rawNames!;

        var result = new string[rawNames.Length];
        for (int i = 0; i < rawNames.Length; i++)
        {
            if (nameGroups.Contains(i))
            {
                // For DELTAHUB paths like .../mods/ModName/chapter_X/Chapter4.xdelta,
                // use grandParent (mod name) as the display name
                var grandParent = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(patchPaths[i]))) ?? "";
                result[i] = !string.IsNullOrEmpty(grandParent) ? grandParent : rawNames[i]!;
            }
            else
            {
                result[i] = rawNames[i]!;
            }
        }

        return result;
    }

    private static void PropagateDeletions(
        PatchFileSystem patchPfs,
        string patchName,
        List<ConflictEntry> conflicts,
        Dictionary<ResourceTouchKey, int> resourceTouchCounts)
    {
        if (patchPfs.Manifest?.Resources == null)
            return;

        foreach (var (resourceType, changes) in patchPfs.Manifest.Resources)
        {
            if (changes.Deleted?.Count > 0)
            {
                foreach (var deletedName in changes.Deleted ?? [])
                {
                    if (string.IsNullOrWhiteSpace(deletedName))
                        continue;

                    var key = new ResourceTouchKey(resourceType, deletedName);
                    if (!resourceTouchCounts.TryGetValue(key, out int touchCount) || touchCount <= 1)
                    {
                        continue;
                    }

                    // Only report skipped deletion when another patch also touched the
                    // same resource relative to the shared original. Otherwise this is
                    // not a real merge conflict and must not inflate the report.
                    conflicts.Add(new ConflictEntry(
                        $"{resourceType}/{deletedName}",
                        "Skipped",
                        "Deletion",
                        patchName,
                        "Resource deletion skipped because another patch also modified this resource relative to the original"));
                }
            }
        }
    }

    private static Dictionary<ResourceTouchKey, int> BuildResourceTouchCounts(PatchFileSystem[] patches)
    {
        var counts = new Dictionary<ResourceTouchKey, int>();

        foreach (var patch in patches)
        {
            if (patch.Manifest?.Resources == null)
                continue;

            var uniqueTouches = new HashSet<ResourceTouchKey>();
            foreach (var (resourceType, changes) in patch.Manifest.Resources)
            {
                foreach (var changed in changes.Changed ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(changed.Name))
                    {
                        uniqueTouches.Add(new ResourceTouchKey(resourceType, changed.Name));
                    }
                }

                foreach (var added in changes.New ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(added.Name))
                    {
                        uniqueTouches.Add(new ResourceTouchKey(resourceType, added.Name));
                    }
                }

                foreach (var deleted in changes.Deleted ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(deleted))
                    {
                        uniqueTouches.Add(new ResourceTouchKey(resourceType, deleted));
                    }
                }
            }

            foreach (var touch in uniqueTouches)
            {
                counts[touch] = counts.TryGetValue(touch, out int existing) ? existing + 1 : 1;
            }
        }

        return counts;
    }

    private static bool IsOrderedAssetResourceType(string resourceType) =>
        resourceType.Equals("Sounds", StringComparison.OrdinalIgnoreCase) ||
        resourceType.Equals("Sprites", StringComparison.OrdinalIgnoreCase) ||
        resourceType.Equals("Backgrounds", StringComparison.OrdinalIgnoreCase) ||
        resourceType.Equals("Paths", StringComparison.OrdinalIgnoreCase) ||
        resourceType.Equals("Scripts", StringComparison.OrdinalIgnoreCase) ||
        resourceType.Equals("Fonts", StringComparison.OrdinalIgnoreCase) ||
        resourceType.Equals("GameObjects", StringComparison.OrdinalIgnoreCase) ||
        resourceType.Equals("Timelines", StringComparison.OrdinalIgnoreCase) ||
        resourceType.Equals("Rooms", StringComparison.OrdinalIgnoreCase) ||
        resourceType.Equals("Shaders", StringComparison.OrdinalIgnoreCase) ||
        resourceType.Equals("Extensions", StringComparison.OrdinalIgnoreCase) ||
        resourceType.Equals("AudioGroups", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extract short display name from conflict file path.
    /// "CodeEntries/gml_Object_obj_darkcontroller_Step_0" → "gml_Object_obj_darkcontroller_Step_0"
    /// "EmbeddedTextures/texture_0033/texture_0033.png" → "texture_0033.png"
    /// </summary>
    private static string ShortName(string path)
    {
        var parts = path.Split('/');
        return parts.Length > 0 ? parts[^1] : path;
    }

    /// <summary>
    /// Extract resource type section from conflict file path.
    /// "CodeEntries/gml_..." → "CodeEntries"
    /// "EmbeddedTextures/..." → "EmbeddedTextures"
    /// "Sprites/spr_kris/..." → "Sprites"
    /// </summary>
    private static string ResourceSection(string path)
    {
        int slash = path.IndexOf('/');
        return slash > 0 ? path[..slash] : "Other";
    }

    private static bool TryGetSpriteJsonName(string path, out string spriteName)
    {
        spriteName = string.Empty;
        var normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith("Sprites/", StringComparison.OrdinalIgnoreCase) ||
            !normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = normalized.Split('/');
        if (parts.Length < 3)
            return false;

        spriteName = Path.GetFileNameWithoutExtension(parts[^1]);
        return !string.IsNullOrWhiteSpace(spriteName);
    }

    private static bool TryGetSpriteFramePath(string path, out string spriteName, out int frameIndex)
    {
        spriteName = string.Empty;
        frameIndex = -1;
        var normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith("Sprites/", StringComparison.OrdinalIgnoreCase) ||
            !normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = normalized.Split('/');
        if (parts.Length < 3)
            return false;

        var file = Path.GetFileNameWithoutExtension(parts[^1]);
        var match = SpriteFrameFileRegex().Match(file);
        if (!match.Success || !int.TryParse(match.Groups["index"].Value, out frameIndex))
            return false;

        spriteName = match.Groups["name"].Value;
        return !string.IsNullOrWhiteSpace(spriteName);
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    private static string DescribeFileConflict(string filePath, UndertaleData originalData, byte[] existingData, byte[] incomingData)
    {
        if (TryGetSpriteFramePath(filePath, out var spriteName, out var frameIndex))
        {
            var baseBytes = TryExportOriginalSpriteFrame(originalData, spriteName, frameIndex);
            var existingChanged = baseBytes == null || !existingData.AsSpan().SequenceEqual(baseBytes);
            var incomingChanged = baseBytes == null || !incomingData.AsSpan().SequenceEqual(baseBytes);
            return $"Sprite frame conflict: sprite={spriteName}, frame={frameIndex}, previousChanged={existingChanged}, incomingChanged={incomingChanged}, previousSha256={ShortHash(existingData)}, incomingSha256={ShortHash(incomingData)}";
        }

        if (TryGetSpriteJsonName(filePath, out var spriteJsonName))
            return $"Sprite metadata conflict: sprite={spriteJsonName}, previousBytes={existingData.Length}, incomingBytes={incomingData.Length}";

        return $"Binary/text file conflict: previousBytes={existingData.Length}, incomingBytes={incomingData.Length}, previousSha256={ShortHash(existingData)}, incomingSha256={ShortHash(incomingData)}";
    }

    private static string? DescribeJsonMerge(string filePath, UndertaleData originalData, byte[] existingData, byte[] incomingData, byte[] mergedData)
    {
        if (!TryGetSpriteJsonName(filePath, out var spriteName))
            return null;

        var baseJson = TryExportOriginalSpriteJson(originalData, spriteName);
        var baseFrames = CountSpriteJsonFrames(baseJson);
        var existingFrames = CountSpriteJsonFrames(existingData);
        var incomingFrames = CountSpriteJsonFrames(incomingData);
        var mergedFrames = CountSpriteJsonFrames(mergedData);
        return $"Sprite metadata merge: sprite={spriteName}, baseFrames={baseFrames}, previousFrames={existingFrames}, incomingFrames={incomingFrames}, mergedFrames={mergedFrames}";
    }

    private static bool TryResolveSpriteFrameConflict(
        string filePath,
        UndertaleData originalData,
        byte[] existingData,
        byte[] incomingData,
        out byte[] chosen,
        out string details)
    {
        chosen = incomingData;
        details = string.Empty;
        if (!TryGetSpriteFramePath(filePath, out var spriteName, out var frameIndex))
            return false;

        var baseBytes = TryExportOriginalSpriteFrame(originalData, spriteName, frameIndex);
        if (baseBytes == null)
            return false;

        var existingEqualsBase = PngBytesEqual(existingData, baseBytes);
        var incomingEqualsBase = PngBytesEqual(incomingData, baseBytes);
        if (existingEqualsBase == incomingEqualsBase)
            return false;

        chosen = existingEqualsBase ? incomingData : existingData;
        details = $"Sprite frame non-overlap: sprite={spriteName}, frame={frameIndex}, kept={(existingEqualsBase ? "incoming" : "previous")} change; baseSha256={ShortHash(baseBytes)}, previousSha256={ShortHash(existingData)}, incomingSha256={ShortHash(incomingData)}";
        return true;
    }

    private static byte[]? TryExportOriginalSpriteJson(UndertaleData originalData, string spriteName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"g3m_sprite_json_{Guid.NewGuid():N}");
        try
        {
            ResourceExportService.ExportSprites(originalData, tempDir, new HashSet<string>(StringComparer.Ordinal) { spriteName });
            var path = Path.Combine(tempDir, "Sprites", SafeFileName(spriteName), SafeFileName(spriteName) + ".json");
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static byte[]? TryExportOriginalSpriteFrame(UndertaleData originalData, string spriteName, int frameIndex)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"g3m_sprite_frame_{Guid.NewGuid():N}");
        try
        {
            ResourceExportService.ExportSprites(originalData, tempDir, new HashSet<string>(StringComparer.Ordinal) { spriteName });
            var path = Path.Combine(tempDir, "Sprites", SafeFileName(spriteName), $"{SafeFileName(spriteName)}_{frameIndex}.png");
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static bool PngBytesEqual(byte[] left, byte[] right)
    {
        if (left.AsSpan().SequenceEqual(right))
            return true;

        try
        {
            using var leftImage = new MagickImage(left);
            using var rightImage = new MagickImage(right);
            if (leftImage.Width != rightImage.Width || leftImage.Height != rightImage.Height)
                return false;

            leftImage.Strip();
            rightImage.Strip();
            return leftImage.ToByteArray(MagickFormat.Png).AsSpan()
                .SequenceEqual(rightImage.ToByteArray(MagickFormat.Png));
        }
        catch
        {
            return false;
        }
    }

    private static int CountSpriteJsonFrames(byte[]? json)
    {
        if (json == null)
            return -1;
        try
        {
            var node = ParseJsonObject(json);
            return node?["textureFrames"] is JsonArray arr ? arr.Count : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string ShortHash(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()[..12];

    private static string EscapeMarkdownTable(string value) =>
        value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static void RepairInvalidPngEntries(PatchFileSystem finalPfs, PatchFileSystem[] sources, UndertaleData originalData, List<ConflictEntry> conflicts)
    {
        var pngPaths = finalPfs.GetAllFilePaths()
            .Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var path in pngPaths)
        {
            if (!finalPfs.TryGetFile(path, out var data) || IsPngSignature(data))
                continue;

            byte[]? replacement = null;
            foreach (var source in sources.Reverse())
            {
                if (source.TryGetFile(path, out var candidate) && IsPngSignature(candidate))
                {
                    replacement = candidate;
                    break;
                }
            }

            if (replacement == null &&
                TryGetSpriteFramePath(path, out var spriteName, out var frameIndex))
            {
                replacement = TryExportOriginalSpriteFrame(originalData, spriteName, frameIndex);
            }

            if (replacement == null)
            {
                finalPfs.RemoveFile(path);
                conflicts.Add(new ConflictEntry(path, "Resolved", "PNG Payload Repair", "Removed invalid payload",
                    $"Invalid PNG payload removed from merged patch; sha256={ShortHash(data)}"));
                continue;
            }

            finalPfs.AddFile(path, replacement);
            conflicts.Add(new ConflictEntry(path, "Resolved", "PNG Payload Repair", "Valid source payload",
                $"Replaced invalid PNG payload in merged patch; badSha256={ShortHash(data)}, fixedSha256={ShortHash(replacement)}"));
        }
    }

    private static bool IsPngSignature(byte[] data) =>
        data.Length >= 8 &&
        data[0] == 0x89 &&
        data[1] == 0x50 &&
        data[2] == 0x4E &&
        data[3] == 0x47 &&
        data[4] == 0x0D &&
        data[5] == 0x0A &&
        data[6] == 0x1A &&
        data[7] == 0x0A;

    private static string GenerateConflictReport(string[] patchNames, List<ConflictEntry> conflicts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# G3MTool Merge Report");
        sb.AppendLine();
        sb.AppendLine($"**Date:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"**Patches:** {string.Join(", ", patchNames.Select((n, i) => $"{n} (priority {i + 1})"))}");
        sb.AppendLine();

        int totalConflicts = conflicts.Count(c => c.Status == "Conflict");
        int totalResolved = conflicts.Count(c => c.Status == "Resolved");
        sb.AppendLine($"**Summary:** {totalConflicts} conflicts, {totalResolved} auto-resolved");
        sb.AppendLine();

        if (conflicts.Count == 0)
        {
            sb.AppendLine("No conflicts detected. All patches merged cleanly.");
            return sb.ToString();
        }

        // Group by resource type, CodeEntries last (most verbose)
        var groups = conflicts
            .GroupBy(c => ResourceSection(c.File))
            .OrderBy(g => g.Key == "CodeEntries" ? 1 : 0)
            .ThenBy(g => g.Key);

        foreach (var group in groups)
        {
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();
            sb.AppendLine("| File | Status | Strategy | Winner | Details |");
            sb.AppendLine("|------|--------|----------|--------|---------|");

            foreach (var c in group.OrderBy(c => c.File))
            {
                // Use raw name without backtick-wrapping to avoid markdown escaping underscores
                sb.AppendLine($"| {ShortName(c.File)} | **{c.Status}** | {c.Strategy} | {c.Winner} | {EscapeMarkdownTable(c.Details ?? "")} |");
            }

            sb.AppendLine();

            // Append unified diffs for entries that have them
            var withDiffs = group.Where(c => !string.IsNullOrEmpty(c.Diff)).ToList();
            if (withDiffs.Count > 0)
            {
                sb.AppendLine("### Changes");
                sb.AppendLine();
                foreach (var c in withDiffs)
                {
                    sb.AppendLine($"#### {ShortName(c.File)}");
                    sb.AppendLine();
                    sb.AppendLine("```diff");
                    sb.AppendLine(c.Diff);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Diff Helpers (for conflict report)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generate a unified diff between two code versions (for overwrite conflicts).
    /// Shows only changed hunks with context lines.
    /// </summary>
    private static string GenerateUnifiedDiff(string oldText, string newText, string oldLabel, string newLabel)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        var lcs = ComputeLCS(oldLines, newLines);

        var sb = new StringBuilder();
        sb.AppendLine($"--- {oldLabel}");
        sb.AppendLine($"+++ {newLabel}");

        // Build hunks from LCS
        int oldPos = 0, newPos = 0;

        foreach (var (oi, ni) in lcs)
        {
            if (oi != oldPos || ni != newPos)
            {
                // Show context before the change
                int hunkOldStart = Math.Max(0, oldPos);
                int hunkNewStart = Math.Max(0, newPos);
                sb.AppendLine($"@@ -{hunkOldStart + 1},{oi - oldPos} +{hunkNewStart + 1},{ni - newPos} @@");
                for (int i = oldPos; i < oi; i++)
                    sb.AppendLine($"-{oldLines[i]}");
                for (int i = newPos; i < ni; i++)
                    sb.AppendLine($"+{newLines[i]}");
            }
            oldPos = oi + 1;
            newPos = ni + 1;
        }

        // Trailing changes
        if (oldPos < oldLines.Length || newPos < newLines.Length)
        {
            sb.AppendLine($"@@ -{oldPos + 1},{oldLines.Length - oldPos} +{newPos + 1},{newLines.Length - newPos} @@");
            for (int i = oldPos; i < oldLines.Length; i++)
                sb.AppendLine($"-{oldLines[i]}");
            for (int i = newPos; i < newLines.Length; i++)
                sb.AppendLine($"+{newLines[i]}");
        }

        // Limit output size
        var result = sb.ToString();
        if (result.Length > 4000)
            result = result[..4000] + "\n... (truncated)";
        return result;
    }

    /// <summary>
    /// Generate a diff showing how a 3-way merge combined changes from two patches.
    /// Shows: base → each side's changes → final merged result.
    /// </summary>
    private static string GenerateThreeWayDiff(
        string baseText, string mergedText,
        string oursLabel, string theirsLabel)
    {
        var baseLines = SplitLines(baseText);
        var mergedLines = SplitLines(mergedText);

        var sb = new StringBuilder();
        sb.AppendLine($"Base: Original | Side A: {oursLabel} | Side B: {theirsLabel}");
        sb.AppendLine($"--- Original");
        sb.AppendLine($"+++ Merged ({oursLabel} + {theirsLabel})");

        // Show diff from base → merged
        var lcs = ComputeLCS(baseLines, mergedLines);
        int oldPos = 0, newPos = 0;

        foreach (var (oi, ni) in lcs)
        {
            if (oi != oldPos || ni != newPos)
            {
                sb.AppendLine($"@@ -{oldPos + 1},{oi - oldPos} +{newPos + 1},{ni - newPos} @@");
                for (int i = oldPos; i < oi; i++)
                    sb.AppendLine($"-{baseLines[i]}");
                for (int i = newPos; i < ni; i++)
                    sb.AppendLine($"+{mergedLines[i]}");
            }
            oldPos = oi + 1;
            newPos = ni + 1;
        }

        if (oldPos < baseLines.Length || newPos < mergedLines.Length)
        {
            sb.AppendLine($"@@ -{oldPos + 1},{baseLines.Length - oldPos} +{newPos + 1},{mergedLines.Length - newPos} @@");
            for (int i = oldPos; i < baseLines.Length; i++)
                sb.AppendLine($"-{baseLines[i]}");
            for (int i = newPos; i < mergedLines.Length; i++)
                sb.AppendLine($"+{mergedLines[i]}");
        }

        var result = sb.ToString();
        if (result.Length > 4000)
            result = result[..4000] + "\n... (truncated)";
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Asset index remapping helpers
    // ═══════════════════════════════════════════════════════════════════

    [GeneratedRegex(@"(?<fn>\b(?:i_ex|instance_number|instance_exists|instance_find)\s*\(\s*)(?<value>-?\d+)(?<suffix>\s*(?:,|\)))",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlObjectFirstArgRegex();

    [GeneratedRegex(@"(?<fn>\b(?:instance_create|instance_place|place_meeting|position_meeting|collision_point)\s*\((?:[^,\r\n]*,\s*){2})(?<value>-?\d+)(?<suffix>\s*(?:,|\)))",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlObjectThirdArgRegex();

    [GeneratedRegex(@"(?<fn>\b(?:instance_create_depth|instance_create_layer|collision_circle)\s*\((?:[^,\r\n]*,\s*){3})(?<value>-?\d+)(?<suffix>\s*(?:,|\)))",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlObjectFourthArgRegex();

    [GeneratedRegex(@"(?<fn>\b(?:collision_rectangle|collision_line)\s*\((?:[^,\r\n]*,\s*){4})(?<value>-?\d+)(?<suffix>\s*(?:,|\)))",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlObjectFifthArgRegex();

    [GeneratedRegex(@"(?<fn>\bwith\s*\(\s*)(?<value>-?\d+)(?<suffix>\s*\))",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlWithObjectRegex();

    [GeneratedRegex(@"(?<fn>\b(?:var\s+)?(?<lhs>(?:(?:[A-Za-z_]\w*)\s*\.\s*)?[A-Za-z_]\w*(?:\s*\[[^\r\n;=]+\])?)\s*=\s*)(?<value>-?\d+)(?<suffix>\s*(?:;|$))",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex GmlNumericAssignmentRegex();

    [GeneratedRegex(@"(?<fn>\b(?:draw_sprite|draw_sprite_ext|draw_sprite_part|draw_sprite_part_ext|draw_sprite_tiled|draw_sprite_tiled_ext|sprite_get_\w+|c_sprite)\s*\(\s*)(?<value>-?\d+)(?<suffix>\s*(?:,|\)))",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlSpriteFirstArgRegex();

    [GeneratedRegex(@"(?<fn>\b(?:scr_marker|scr_dark_marker)\s*\((?:[^,\r\n]*,\s*){2})(?<value>-?\d+)(?<suffix>\s*(?:,|\)))",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlSpriteThirdArgRegex();

    [GeneratedRegex(@"(?<fn>\b(?:sprite_index|mask_index)\s*=\s*)(?<value>-?\d+)(?<suffix>\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlSpriteAssignmentRegex();


    [GeneratedRegex(@"(?<fn>\b(?:audio_play_sound|sound_play|snd_play|snd_play_volume|audio_stop_sound|audio_pause_sound|audio_resume_sound|audio_is_playing)\s*\(\s*)(?<value>-?\d+)(?<suffix>\s*(?:,|\)))",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlSoundFirstArgRegex();

    [GeneratedRegex(@"(?<fn>\b(?:draw_set_font|font_get_name|font_get_size)\s*\(\s*)(?<value>-?\d+)(?<suffix>\s*(?:,|\)))",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlFontFirstArgRegex();

    [GeneratedRegex(@"(?<fn>\b(?:background_get_width|background_get_height|draw_background|draw_background_ext)\s*\(\s*)(?<value>-?\d+)(?<suffix>\s*(?:,|\)))",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlBackgroundFirstArgRegex();

    [GeneratedRegex(@"(?<fn>\bbackground_index\s*=\s*)(?<value>-?\d+)(?<suffix>\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlBackgroundAssignmentRegex();

    [GeneratedRegex(@"(?<fn>\b(?:path_start|path_get_length|path_get_kind|path_get_closed|path_get_precision|path_get_number|path_get_point_x|path_get_point_y|path_get_point_speed|path_delete|path_duplicate|path_assign|path_append|path_add_point|path_insert_point|path_change_point|path_delete_point|path_clear_points|path_reverse|path_mirror|path_flip|path_rotate|path_scale|path_shift)\s*\(\s*)(?<value>-?\d+)(?<suffix>\s*(?:,|\)))",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlPathFirstArgRegex();

    [GeneratedRegex(@"(?<fn>\bpath_index\s*=\s*)(?<value>-?\d+)(?<suffix>\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlPathAssignmentRegex();

    [GeneratedRegex(@"(?<fn>\bscript_execute\s*\(\s*)(?<value>-?\d+)(?<suffix>\s*(?:,|\)))",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlScriptFirstArgRegex();

    [GeneratedRegex(@"(?<fn>\b(?:timeline_exists|timeline_get_name|timeline_moment_add_script|timeline_moment_clear|timeline_clear)\s*\(\s*)(?<value>-?\d+)(?<suffix>\s*(?:,|\)))",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlTimelineFirstArgRegex();

    [GeneratedRegex(@"(?<fn>\btimeline_index\s*=\s*)(?<value>-?\d+)(?<suffix>\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlTimelineAssignmentRegex();

    [GeneratedRegex(@"(?<fn>\b(?:room_goto|room_goto_next|room_goto_previous|room_exists)\s*\(\s*)(?<value>-?\d+)(?<suffix>\s*(?:,|\)))",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlRoomFirstArgRegex();

    [GeneratedRegex(@"(?<fn>\broom\s*=\s*)(?<value>-?\d+)(?<suffix>\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlRoomAssignmentRegex();

    [GeneratedRegex(@"(?<fn>\b(?:shader_set|shader_is_compiled|shader_get_sampler_index|shader_get_uniform|shader_set_uniform_i|shader_set_uniform_i_array|shader_set_uniform_f|shader_set_uniform_f_array|shader_set_uniform_matrix|shader_set_uniform_matrix_array|shader_enable_corner_id)\s*\(\s*)(?<value>-?\d+)(?<suffix>\s*(?:,|\)))",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlShaderFirstArgRegex();

    [GeneratedRegex(@"(?<fn>\b(?:audio_group_load|audio_group_load_progress|audio_group_unload|audio_group_is_loaded|audio_group_name)\s*\(\s*)(?<value>-?\d+)(?<suffix>\s*(?:,|\)))",
        RegexOptions.IgnoreCase)]
    private static partial Regex GmlAudioGroupFirstArgRegex();

    [GeneratedRegex(@"^(?<prefix>\s*push(?:i\.e|\.i)\s+)(?<value>-?\d+)(?<suffix>\s*;.*\bg3m-resource\s*=\s*(?<type>\w+)\b.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex AsmResourcePushRegex();

    [GeneratedRegex(@"^(?<prefix>\s*(?:push\.v|pop\.v\.[a-z]+)\s+)(?<value>\d+)(?<suffix>\.[A-Za-z_]\w*.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex AsmObjectMemberReferenceRegex();

    [GeneratedRegex(@"(?<prefix>^\s*push(?:i\.e|\.i)\s+)(?<value>-?\d+)(?<suffix>\s*\r?\n\s*conv\.i\.v\s*\r?\n\s*call\.i\s+(?:draw_sprite(?:_ext|_part|_part_ext|_tiled|_tiled_ext)?|sprite_get_\w+|c_sprite)\(argc=\d+\).*$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex AsmSpriteCallFirstArgRegex();

    private static string RemapAssetIndicesGml(string gml, PatchAssetRemaps remap)
    {
        if (!remap.HasAny)
            return gml;

        gml = RemapGmlRegex(gml, GmlObjectFirstArgRegex(), remap.Objects);
        gml = RemapGmlRegex(gml, GmlObjectThirdArgRegex(), remap.Objects);
        gml = RemapGmlRegex(gml, GmlObjectFourthArgRegex(), remap.Objects);
        gml = RemapGmlRegex(gml, GmlObjectFifthArgRegex(), remap.Objects);
        gml = RemapGmlRegex(gml, GmlWithObjectRegex(), remap.Objects);
        gml = RemapGmlVariableAssignments(gml, remap.Objects, IsIdentifierUsedAsObjectArgument);

        gml = RemapGmlRegex(gml, GmlSpriteFirstArgRegex(), remap.Sprites);
        gml = RemapGmlRegex(gml, GmlSpriteThirdArgRegex(), remap.Sprites);
        gml = RemapGmlRegex(gml, GmlSpriteAssignmentRegex(), remap.Sprites);
        gml = RemapGmlVariableAssignments(gml, remap.Sprites, IsIdentifierUsedAsSpriteArgument);

        gml = RemapGmlRegex(gml, GmlSoundFirstArgRegex(), remap.Sounds);
        gml = RemapGmlVariableAssignments(gml, remap.Sounds, IsIdentifierUsedAsSoundArgument);

        gml = RemapGmlRegex(gml, GmlFontFirstArgRegex(), remap.Fonts);
        gml = RemapGmlVariableAssignments(gml, remap.Fonts, IsIdentifierUsedAsFontArgument);
        gml = RemapGmlRegex(gml, GmlBackgroundFirstArgRegex(), remap.Backgrounds);
        gml = RemapGmlRegex(gml, GmlBackgroundAssignmentRegex(), remap.Backgrounds);
        gml = RemapGmlVariableAssignments(gml, remap.Backgrounds, IsIdentifierUsedAsBackgroundArgument);
        gml = RemapGmlRegex(gml, GmlPathFirstArgRegex(), remap.Paths);
        gml = RemapGmlRegex(gml, GmlPathAssignmentRegex(), remap.Paths);
        gml = RemapGmlVariableAssignments(gml, remap.Paths, IsIdentifierUsedAsPathArgument);
        gml = RemapGmlRegex(gml, GmlScriptFirstArgRegex(), remap.Scripts);
        gml = RemapGmlVariableAssignments(gml, remap.Scripts, IsIdentifierUsedAsScriptArgument);
        gml = RemapGmlRegex(gml, GmlTimelineFirstArgRegex(), remap.Timelines);
        gml = RemapGmlRegex(gml, GmlTimelineAssignmentRegex(), remap.Timelines);
        gml = RemapGmlVariableAssignments(gml, remap.Timelines, IsIdentifierUsedAsTimelineArgument);
        gml = RemapGmlRegex(gml, GmlRoomFirstArgRegex(), remap.Rooms);
        gml = RemapGmlRegex(gml, GmlRoomAssignmentRegex(), remap.Rooms);
        gml = RemapGmlVariableAssignments(gml, remap.Rooms, IsIdentifierUsedAsRoomArgument);
        gml = RemapGmlRegex(gml, GmlShaderFirstArgRegex(), remap.Shaders);
        gml = RemapGmlVariableAssignments(gml, remap.Shaders, IsIdentifierUsedAsShaderArgument);
        gml = RemapGmlRegex(gml, GmlAudioGroupFirstArgRegex(), remap.AudioGroups);
        gml = RemapGmlVariableAssignments(gml, remap.AudioGroups, IsIdentifierUsedAsAudioGroupArgument);
        return gml;
    }

    private static string RemapGmlAssetNameIdentifiers(string gml, PatchAssetRemaps remap)
    {
        if (remap.ShiftedAssetNameIndices.Count == 0)
            return gml;

        foreach (var (name, index) in remap.ShiftedAssetNameIndices.OrderByDescending(p => p.Key.Length))
        {
            if (!HasSafeAssetNamePrefix(name))
                continue;
            gml = Regex.Replace(
                gml,
                $@"(?<![\w.]){Regex.Escape(name)}(?!\w|\s*\(|\s*\.)",
                index.ToString(),
                RegexOptions.CultureInvariant);
        }
        return gml;
    }

    private static string RemapGmlObjectVariableAssignments(string gml, Dictionary<int, int> remap)
    {
        return RemapGmlVariableAssignments(gml, remap, IsIdentifierUsedAsObjectArgument);
    }

    private static string RemapGmlVariableAssignments(
        string gml,
        Dictionary<int, int> remap,
        Func<string, string, bool> isIdentifierUsedAsResource)
    {
        if (remap.Count == 0)
            return gml;

        return GmlNumericAssignmentRegex().Replace(gml, m =>
        {
            var lhs = NormalizeGmlReference(m.Groups["lhs"].Value);
            var name = ExtractLastIdentifier(lhs);
            if (string.IsNullOrEmpty(lhs) ||
                !int.TryParse(m.Groups["value"].Value, out int value) ||
                !remap.TryGetValue(value, out int newValue) ||
                (!isIdentifierUsedAsResource(gml, lhs) &&
                 (string.IsNullOrEmpty(name) || !isIdentifierUsedAsResource(gml, name))))
            {
                return m.Value;
            }

            return m.Groups["fn"].Value + newValue.ToString() + m.Groups["suffix"].Value;
        });
    }

    private static string NormalizeGmlReference(string value) =>
        WhitespaceRegex().Replace(value, string.Empty);

    private static string ExtractLastIdentifier(string value)
    {
        var matches = IdentifierRegex().Matches(value);
        return matches.Count == 0 ? string.Empty : matches[^1].Value;
    }

    [GeneratedRegex(@"(\d+)$")]
    private static partial Regex TrailingNumberRegex();

    [GeneratedRegex(@"^(?<name>.+)_(?<index>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex SpriteFrameFileRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[A-Za-z_]\w*")]
    private static partial Regex IdentifierRegex();

    private static string RefPattern(string identifier) =>
        Regex.Escape(NormalizeGmlReference(identifier));

    private static bool IsIdentifierUsedAsObjectArgument(string gml, string identifier)
    {
        var escaped = RefPattern(identifier);
        return Regex.IsMatch(gml, $@"\b(?:i_ex|instance_number|instance_exists|instance_find)\s*\(\s*{escaped}\b", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(gml, $@"\b{escaped}\s*\.\s*[A-Za-z_]\w*", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(gml, $@"\b(?:instance_create|instance_create_depth|instance_create_layer|instance_place|place_meeting|position_meeting|collision_point)\s*\((?:[^,\r\n]*,\s*){{2}}{escaped}\b", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(gml, $@"\bcollision_circle\s*\((?:[^,\r\n]*,\s*){{3}}{escaped}\b", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(gml, $@"\b(?:collision_rectangle|collision_line)\s*\((?:[^,\r\n]*,\s*){{4}}{escaped}\b", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(gml, $@"\bwith\s*\(\s*{escaped}\s*\)", RegexOptions.IgnoreCase);
    }

    private static bool IsIdentifierUsedAsSoundArgument(string gml, string identifier)
    {
        var escaped = RefPattern(identifier);
        return Regex.IsMatch(
            gml,
            $@"\b(?:audio_play_sound|sound_play|snd_play|snd_play_volume|audio_stop_sound|audio_pause_sound|audio_resume_sound|audio_is_playing)\s*\(\s*{escaped}\b",
            RegexOptions.IgnoreCase);
    }

    private static bool IsIdentifierUsedAsSpriteArgument(string gml, string identifier)
    {
        var escaped = RefPattern(identifier);
        if (IsLikelySpriteVariableName(identifier))
            return true;
        return Regex.IsMatch(
                   gml,
                   $@"\b(?:draw_sprite|draw_sprite_ext|draw_sprite_part|draw_sprite_part_ext|sprite_get_width|sprite_get_height|sprite_get_number|c_sprite)\s*\(\s*{escaped}\b",
                   RegexOptions.IgnoreCase) ||
               Regex.IsMatch(
                   gml,
                   $@"\b(?:scr_marker|scr_dark_marker)\s*\((?:[^,\r\n]*,\s*){{2}}{escaped}\b",
                   RegexOptions.IgnoreCase) ||
               Regex.IsMatch(
                   gml,
                   $@"\b(?:sprite_index|mask_index)\s*=\s*{escaped}\b",
                   RegexOptions.IgnoreCase);
    }

    private static bool IsIdentifierUsedAsFontArgument(string gml, string identifier)
    {
        var escaped = RefPattern(identifier);
        return IsLikelyResourceVariableName(identifier, "font", "fnt") ||
               Regex.IsMatch(gml, $@"\b(?:draw_set_font|font_get_name|font_get_size)\s*\(\s*{escaped}\b", RegexOptions.IgnoreCase);
    }

    private static bool IsIdentifierUsedAsBackgroundArgument(string gml, string identifier)
    {
        var escaped = RefPattern(identifier);
        return IsLikelyResourceVariableName(identifier, "background", "bg", "bkg") ||
               Regex.IsMatch(gml, $@"\b(?:background_get_width|background_get_height|draw_background|draw_background_ext)\s*\(\s*{escaped}\b", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(gml, $@"\bbackground_index\s*=\s*{escaped}\b", RegexOptions.IgnoreCase);
    }

    private static bool IsIdentifierUsedAsPathArgument(string gml, string identifier)
    {
        var escaped = RefPattern(identifier);
        return IsLikelyResourceVariableName(identifier, "path", "pth") ||
               Regex.IsMatch(gml, $@"\bpath_\w+\s*\(\s*{escaped}\b", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(gml, $@"\bpath_index\s*=\s*{escaped}\b", RegexOptions.IgnoreCase);
    }

    private static bool IsIdentifierUsedAsScriptArgument(string gml, string identifier)
    {
        var escaped = RefPattern(identifier);
        return IsLikelyResourceVariableName(identifier, "script", "scr") ||
               Regex.IsMatch(gml, $@"\bscript_execute\s*\(\s*{escaped}\b", RegexOptions.IgnoreCase);
    }

    private static bool IsIdentifierUsedAsTimelineArgument(string gml, string identifier)
    {
        var escaped = RefPattern(identifier);
        return IsLikelyResourceVariableName(identifier, "timeline", "tl") ||
               Regex.IsMatch(gml, $@"\btimeline_\w+\s*\(\s*{escaped}\b", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(gml, $@"\btimeline_index\s*=\s*{escaped}\b", RegexOptions.IgnoreCase);
    }

    private static bool IsIdentifierUsedAsRoomArgument(string gml, string identifier)
    {
        var escaped = RefPattern(identifier);
        return IsLikelyResourceVariableName(identifier, "room", "rm") ||
               Regex.IsMatch(gml, $@"\b(?:room_goto|room_goto_next|room_goto_previous|room_exists)\s*\(\s*{escaped}\b", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(gml, $@"\broom\s*=\s*{escaped}\b", RegexOptions.IgnoreCase);
    }

    private static bool IsIdentifierUsedAsShaderArgument(string gml, string identifier)
    {
        var escaped = RefPattern(identifier);
        return IsLikelyResourceVariableName(identifier, "shader", "shd") ||
               Regex.IsMatch(gml, $@"\bshader_\w+\s*\(\s*{escaped}\b", RegexOptions.IgnoreCase);
    }

    private static bool IsIdentifierUsedAsAudioGroupArgument(string gml, string identifier)
    {
        var escaped = RefPattern(identifier);
        return IsLikelyResourceVariableName(identifier, "audiogroup", "audio_group") ||
               Regex.IsMatch(gml, $@"\baudio_group_\w+\s*\(\s*{escaped}\b", RegexOptions.IgnoreCase);
    }

    private static bool IsLikelySpriteVariableName(string identifier) =>
        identifier.Contains("sprite", StringComparison.OrdinalIgnoreCase) ||
        identifier.Contains("spr", StringComparison.OrdinalIgnoreCase) ||
        identifier.Contains("mask", StringComparison.OrdinalIgnoreCase);

    private static bool IsLikelyResourceVariableName(string identifier, params string[] tokens) =>
        tokens.Any(token => identifier.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsShiftedAssetName(string gml, PatchAssetRemaps remap)
    {
        if (remap.ShiftedAssetNames.Count == 0)
            return false;

        foreach (var name in remap.ShiftedAssetNames)
        {
            if (!HasSafeAssetNamePrefix(name))
                continue;
            if (gml.Contains(name, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool HasSafeAssetNamePrefix(string name) =>
        name.StartsWith("obj_", StringComparison.Ordinal) ||
        name.StartsWith("spr_", StringComparison.Ordinal) ||
        name.StartsWith("snd_", StringComparison.Ordinal) ||
        name.StartsWith("bg_", StringComparison.Ordinal) ||
        name.StartsWith("bkg_", StringComparison.Ordinal) ||
        name.StartsWith("fnt_", StringComparison.Ordinal) ||
        name.StartsWith("font_", StringComparison.Ordinal) ||
        name.StartsWith("path_", StringComparison.Ordinal) ||
        name.StartsWith("pth_", StringComparison.Ordinal) ||
        name.StartsWith("tl_", StringComparison.Ordinal) ||
        name.StartsWith("timeline_", StringComparison.Ordinal) ||
        name.StartsWith("rm_", StringComparison.Ordinal) ||
        name.StartsWith("shd_", StringComparison.Ordinal);

    private static string RemapGmlRegex(string gml, Regex regex, Dictionary<int, int> remap)
    {
        if (remap.Count == 0)
            return gml;

        return regex.Replace(gml, m =>
        {
            if (!int.TryParse(m.Groups["value"].Value, out int value) ||
                !remap.TryGetValue(value, out int newValue))
            {
                return m.Value;
            }
            return m.Groups["fn"].Value + newValue.ToString() + m.Groups["suffix"].Value;
        });
    }

    private static string RemapAssetIndicesAsm(string asm, PatchAssetRemaps remap)
    {
        if (!remap.HasAny) return asm;
        asm = RemapAsmContextualResourcePush(asm, AsmSpriteCallFirstArgRegex(), remap.Sprites);

        asm = AsmResourcePushRegex().Replace(asm, m =>
        {
            if (!int.TryParse(m.Groups["value"].Value, out int val))
                return m.Value;

            var map = m.Groups["type"].Value.ToLowerInvariant() switch
            {
                "object" or "gameobject" or "gameobjects" => remap.Objects,
                "sprite" or "sprites" => remap.Sprites,
                "sound" or "sounds" => remap.Sounds,
                "background" or "backgrounds" => remap.Backgrounds,
                "path" or "paths" => remap.Paths,
                "script" or "scripts" => remap.Scripts,
                "font" or "fonts" => remap.Fonts,
                "timeline" or "timelines" => remap.Timelines,
                "room" or "rooms" => remap.Rooms,
                "shader" or "shaders" => remap.Shaders,
                "extension" or "extensions" => remap.Extensions,
                "audiogroup" or "audiogroups" or "audio_group" or "audio_groups" => remap.AudioGroups,
                _ => []
            };

            if (map.TryGetValue(val, out int newVal))
                return m.Groups["prefix"].Value + newVal.ToString() + m.Groups["suffix"].Value;
            return m.Value;
        });

        if (remap.Objects.Count == 0)
            return asm;

        return AsmObjectMemberReferenceRegex().Replace(asm, m =>
        {
            if (!int.TryParse(m.Groups["value"].Value, out int val) ||
                !remap.Objects.TryGetValue(val, out int newVal))
            {
                return m.Value;
            }
            return m.Groups["prefix"].Value + newVal.ToString() + m.Groups["suffix"].Value;
        });
    }

    private static string RemapAsmContextualResourcePush(string asm, Regex regex, Dictionary<int, int> remap)
    {
        if (remap.Count == 0)
            return asm;

        return regex.Replace(asm, m =>
        {
            if (!int.TryParse(m.Groups["value"].Value, out int value) ||
                !remap.TryGetValue(value, out int newValue))
            {
                return m.Value;
            }

            return m.Groups["prefix"].Value + newValue.ToString() + m.Groups["suffix"].Value;
        });
    }
}
