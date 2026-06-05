using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using G3MToolCLI.Models;
using G3MToolCLI.Utils;
using ImageMagick;
using Underanalyzer.Decompiler;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace G3MToolCLI.Services;

public partial class PatchService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private static readonly Lock s_codeImportLock = new();
    private const int DataFileBufferSize = 1024 * 1024;
    private sealed class PatchHelperSnapshot
    {
        public required string HelpersDir { get; init; }
        public string[]? AssetOrderLines { get; init; }
        public byte[]? ObjectEventsBytes { get; init; }
        public byte[]? TexturePageItemsBytes { get; init; }
        public byte[]? SpriteFrameMapBytes { get; init; }
    }

    private static string HexHash(byte[] data) =>
        Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    private static FileStream OpenDataReadStream(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, DataFileBufferSize, FileOptions.SequentialScan);

    private static FileStream OpenDataWriteStream(string path) =>
        new(path, FileMode.Create, FileAccess.Write, FileShare.None, DataFileBufferSize);


    private static List<string> BuildResourceTypesToProcess(PatchFileSystem pfs, G3MPatchManifest? manifest)
    {
        var importOrder = ResourceTypeRegistry.ImportOrder;
        var existingFolders = pfs.GetResourceTypes();

        var resourceTypesToProcess = new List<string>(importOrder.Length);
        foreach (var rt in importOrder)
        {
            if (existingFolders.Contains(rt))
                resourceTypesToProcess.Add(rt);
        }

        var deletionTypesToProcess = manifest?.Resources?
            .Where(kvp => (kvp.Value.Deleted?.Count ?? 0) > 0)
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var rt in deletionTypesToProcess)
        {
            if (!resourceTypesToProcess.Contains(rt, StringComparer.OrdinalIgnoreCase))
                resourceTypesToProcess.Add(rt);
        }

        if (resourceTypesToProcess.Remove("GeneralInfo"))
            resourceTypesToProcess.Insert(0, "GeneralInfo");

        return resourceTypesToProcess;
    }

    private static PatchHelperSnapshot CapturePatchHelpers(
        PatchFileSystem pfs,
        bool includeAssetOrder,
        bool includeObjectEvents,
        bool includeTextureMappingHelpers)
    {
        var helpersDir = pfs.HelpersPrefix;
        var capturedAssetOrderPath = Path.Combine(helpersDir, "asset_order.txt");
        var capturedObjectEventsPath = Path.Combine(helpersDir, "object_events.json");
        var capturedTexturePageItemsPath = Path.Combine(helpersDir, "texture_page_items.json");
        var capturedSpriteFrameMapPath = Path.Combine(helpersDir, "sprite_frame_map.json");

        return new PatchHelperSnapshot
        {
            HelpersDir = helpersDir,
            AssetOrderLines = includeAssetOrder && pfs.FileExists(capturedAssetOrderPath)
                ? pfs.ReadAllLines(capturedAssetOrderPath)
                : null,
            ObjectEventsBytes = includeObjectEvents && pfs.FileExists(capturedObjectEventsPath)
                ? pfs.ReadAllBytes(capturedObjectEventsPath)
                : null,
            TexturePageItemsBytes = includeTextureMappingHelpers && pfs.FileExists(capturedTexturePageItemsPath)
                ? pfs.ReadAllBytes(capturedTexturePageItemsPath)
                : null,
            SpriteFrameMapBytes = includeTextureMappingHelpers && pfs.FileExists(capturedSpriteFrameMapPath)
                ? pfs.ReadAllBytes(capturedSpriteFrameMapPath)
                : null
        };
    }


    public static async Task<PatchCreateResult> CreatePatchAsync(
        string originalPath,
        string modifiedPath,
        string outputPath,
        Dictionary<string, Dictionary<string, string>>? precomputedOriginalHashes = null,
        DataFileInfo? precomputedOriginalInfo = null,
        Dictionary<string, Dictionary<string, int>>? precomputedOriginalNameCounts = null,
        Dictionary<string, IReadOnlyList<string>>? precomputedOriginalOrderedNames = null,
        UndertaleData? precomputedModifiedData = null,
        bool includeXdeltaFallback = false,
        G3MCacheOptions? cacheOptions = null)
    {
        if (!File.Exists(originalPath))
            return new PatchCreateResult { Success = false, Error = $"Original file not found: {originalPath}" };

        if (!File.Exists(modifiedPath))
            return new PatchCreateResult { Success = false, Error = $"Modified file not found: {modifiedPath}" };

        string? tempXdeltaResult = null;
        string? tempExactPatch = null;
        string? exactPatchSourcePath = null;
        if (Path.GetExtension(modifiedPath).Equals(".xdelta", StringComparison.OrdinalIgnoreCase))
        {
            LogService.Log("[PatchService] Detected xdelta file, applying to original first...");

            var xdeltaService = new XDeltaService();
            tempXdeltaResult = Path.Combine(Path.GetTempPath(), $"g3mtool_xdelta_{Guid.NewGuid():N}.win");

            var xdeltaResult = await xdeltaService.ApplyPatchAsync(originalPath, modifiedPath, tempXdeltaResult);
            if (!xdeltaResult.Success)
            {
                if (File.Exists(tempXdeltaResult))
                    File.Delete(tempXdeltaResult);
                return new PatchCreateResult { Success = false, Error = $"Failed to apply xdelta: {xdeltaResult.Error}" };
            }

            LogService.Log("[PatchService] xdelta applied successfully, using result as modified file");

            // Copy audiogroup files to temp directory so ExportSounds can find them
            var originalDir = Path.GetDirectoryName(originalPath);
            var tempDir = Path.GetDirectoryName(tempXdeltaResult);
            if (!string.IsNullOrEmpty(originalDir) && !string.IsNullOrEmpty(tempDir))
            {
                foreach (var audioGroup in Directory.GetFiles(originalDir, "audiogroup*.dat"))
                {
                    var destPath = Path.Combine(tempDir, Path.GetFileName(audioGroup));
                    if (!File.Exists(destPath))
                        File.Copy(audioGroup, destPath);
                }
            }

            modifiedPath = tempXdeltaResult;
        }

        try
        {
            var totalSw = Stopwatch.StartNew();
            var phaseSw = new Stopwatch();

            // Create temp directories for export
            var tempDir = Path.Combine(Path.GetTempPath(), $"g3mtool_{Guid.NewGuid():N}");
            var modifiedExportDir = Path.Combine(tempDir, "modified");
            Directory.CreateDirectory(modifiedExportDir);

            LogService.SetOperation("Creating patch");
            LogService.Progress(0, 100);

            // --- Load original, extract manifest info, hash in memory, release ---
            DataFileInfo originalInfo;
            Dictionary<string, Dictionary<string, string>> originalHashes;
            Dictionary<string, Dictionary<string, int>> originalNameCounts;
            Dictionary<string, IReadOnlyList<string>> originalOrderedNames;
            if (precomputedOriginalHashes != null && precomputedOriginalInfo != null)
            {
                originalHashes = precomputedOriginalHashes;
                originalInfo = precomputedOriginalInfo;
                originalNameCounts = precomputedOriginalNameCounts ?? ReadResourceNameCounts(originalPath);
                originalOrderedNames = precomputedOriginalOrderedNames ?? ReadOrderSensitiveResourceNames(originalPath);
                LogService.Log("[PatchService] Using precomputed original hashes (shared)");
            }
            else
            {
                var cachedOriginal = G3MCacheService.TryReadDataCache(originalPath, cacheOptions);
                if (cachedOriginal != null)
                {
                    originalInfo = cachedOriginal.DataInfo;
                    originalHashes = cachedOriginal.ResourceHashes;
                    originalNameCounts = cachedOriginal.ResourceNameCounts;
                    originalOrderedNames = G3MCacheService.ToReadOnlyOrderNames(cachedOriginal.OrderSensitiveNames);
                    LogService.Log($"[PatchService] Original: {originalInfo.GeneralInfo?.DisplayName ?? "Unknown"}");
                    LogService.Log($"[Timing] Original cache: {phaseSw.Elapsed.TotalMilliseconds:F0}ms, peak RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
                }
                else
                {
                    phaseSw.Restart();
                    LogService.Log("[PatchService] Loading original data file...");
                    using var stream = OpenDataReadStream(originalPath);
                    var originalData = UndertaleIO.Read(stream);
                    var originalLoadTime = phaseSw.Elapsed;
                    LogService.Log($"[PatchService] Original: {originalData.GeneralInfo?.DisplayName?.Content ?? "Unknown"}");
                    originalInfo = new DataFileInfo
                    {
                        Filename = Path.GetFileName(originalPath),
                        Size = new FileInfo(originalPath).Length,
                        Md5 = await HashService.ComputeFileHashAsync(originalPath),
                        BytecodeVersion = originalData.GeneralInfo?.BytecodeVersion ?? 0,
                        GmsVersion = GeneralInfoUtil.GetVersionDisplay(originalData.GeneralInfo),
                        GeneralInfo = GeneralInfoUtil.ExtractGeneralInfo(originalData)
                    };

                    phaseSw.Restart();
                    LogService.Log("[PatchService] Hashing original resources in memory...");
                    originalHashes = ResourceHashService.HashAll(originalData, originalPath);
                    originalNameCounts = GetResourceNameCounts(originalData);
                    originalOrderedNames = GetOrderSensitiveResourceNames(originalData);
                    await G3MCacheService.WriteDataCacheAsync(originalPath, originalInfo, originalHashes, originalNameCounts, originalOrderedNames, cacheOptions);
                    LogService.Log($"[Timing] Original load: {originalLoadTime.TotalSeconds:F1}s, hash: {phaseSw.Elapsed.TotalSeconds:F1}s, peak RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
                }
                phaseSw.Restart();
                GC.Collect();
                LogService.Log($"[Timing] GC after original: {phaseSw.Elapsed.TotalMilliseconds:F0}ms, RAM after GC: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
            }
            LogService.Progress(20, 100);

            // --- Load modified, extract manifest info, hash + export, release ---
            phaseSw.Restart();
            LogService.Log(precomputedModifiedData != null
                ? "[PatchService] Using preloaded modified data..."
                : "[PatchService] Loading modified data file...");
            DataFileInfo modifiedInfo;
            Dictionary<string, Dictionary<string, string>> modifiedHashes;
            var modifiedCacheOptions = tempXdeltaResult == null ? cacheOptions : null;
            Dictionary<string, HashSet<string>> changedNamesPerType = [];
            var helperForcedResourceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, (string LogicalName, string? gml, string? asm, Dictionary<string, (string LogicalName, string Asm)>? childAsms)> codeEntriesInMemory = [];

            if (precomputedModifiedData != null)
            {
                var modifiedData = precomputedModifiedData;
                var loadTime = phaseSw.Elapsed;
                LogService.Log($"[PatchService] Modified: {modifiedData.GeneralInfo?.DisplayName?.Content ?? "Unknown"}");
                var cachedModified = G3MCacheService.TryReadDataCache(modifiedPath, modifiedCacheOptions);
                if (cachedModified != null)
                {
                    modifiedInfo = cachedModified.DataInfo;
                    modifiedHashes = cachedModified.ResourceHashes;
                }
                else
                {
                    modifiedInfo = new DataFileInfo
                    {
                        Filename = Path.GetFileName(modifiedPath),
                        Size = new FileInfo(modifiedPath).Length,
                        Md5 = await HashService.ComputeFileHashAsync(modifiedPath),
                        BytecodeVersion = modifiedData.GeneralInfo?.BytecodeVersion ?? 0,
                        GmsVersion = GeneralInfoUtil.GetVersionDisplay(modifiedData.GeneralInfo),
                        GeneralInfo = GeneralInfoUtil.ExtractGeneralInfo(modifiedData)
                    };
                    phaseSw.Restart();
                    LogService.Log("[PatchService] Hashing modified resources in memory...");
                    modifiedHashes = ResourceHashService.HashAll(modifiedData, modifiedPath);
                    await G3MCacheService.WriteDataCacheAsync(
                        modifiedPath,
                        modifiedInfo,
                        modifiedHashes,
                        GetResourceNameCounts(modifiedData),
                        GetOrderSensitiveResourceNames(modifiedData),
                        modifiedCacheOptions);
                }

                var compatibilityError = GetCreateCompatibilityError(originalInfo, modifiedInfo);
                if (compatibilityError != null)
                    return new PatchCreateResult { Success = false, Error = compatibilityError };

                // Hash modified resources in memory (fast comparison)
                LogService.Progress(40, 100);
                var hashTime = phaseSw.Elapsed;

                // Compare ALL hashes NOW (before export) to identify changed/new resources per type
                phaseSw.Restart();
                LogService.Log("[PatchService] Comparing hashes to build selective export filter...");
                changedNamesPerType = [];
                foreach (var resourceType in ResourceTypeRegistry.AllTypes)
                {
                    var origTypeHashes = originalHashes.GetValueOrDefault(resourceType) ?? [];
                    var modTypeHashes = modifiedHashes.GetValueOrDefault(resourceType) ?? [];
                    var changedNames = new HashSet<string>();
                    foreach (var (name, hash) in modTypeHashes)
                    {
                        if (!origTypeHashes.TryGetValue(name, out var origHash) || origHash != hash)
                            changedNames.Add(name);
                    }
                    if (changedNames.Count > 0)
                        changedNamesPerType[resourceType] = changedNames;
                }

                AddDuplicateCountChanges(originalNameCounts, modifiedData, changedNamesPerType);
                PromoteOrderSensitiveExportsIfNeeded(
                    originalOrderedNames,
                    modifiedData,
                    modifiedHashes,
                    changedNamesPerType,
                    helperForcedResourceTypes);

                PromoteVersionSensitiveExportsIfNeeded(
                    originalInfo.GeneralInfo,
                    modifiedInfo.GeneralInfo,
                    modifiedHashes,
                    changedNamesPerType);
                LogService.Log($"[Timing] Hash comparison (pre-export): {phaseSw.Elapsed.TotalMilliseconds:F0}ms");

                // If a parent code entry changed, include all its children too.
                if (changedNamesPerType.TryGetValue("CodeEntries", out var changedCode))
                {
                    var additionalChildren = new HashSet<string>();
                    foreach (var code in modifiedData.Code)
                    {
                        if (code?.Name?.Content != null && code.ParentEntry?.Name?.Content != null)
                        {
                            if (changedCode.Contains(code.ParentEntry.Name.Content) && !changedCode.Contains(code.Name.Content))
                                additionalChildren.Add(code.Name.Content);
                        }
                    }
                    if (additionalChildren.Count > 0)
                    {
                        foreach (var child in additionalChildren)
                            changedCode.Add(child);
                        LogService.Log($"[PatchService] Added {additionalChildren.Count} child code entries for changed parents");
                    }
                }

                // Export only changed/new resources (selective export for ALL types)
                phaseSw.Restart();
                LogService.Log("[PatchService] Exporting modified resources (selective)...");
                ResourceExportService.ExportSelectiveExceptCode(modifiedData, modifiedExportDir, modifiedPath, changedNamesPerType);

                // Export code entries to memory; patch writing uses them directly.
                var changedCodeNames = changedNamesPerType.GetValueOrDefault("CodeEntries") ?? [];
                var codeExportSw = Stopwatch.StartNew();
                codeEntriesInMemory = ResourceExportService.ExportCodeEntriesToMemory(modifiedData, changedCodeNames);
                codeExportSw.Stop();
                LogService.Log($"[Export Timing] CodeEntries (in-memory {changedCodeNames.Count}): {codeExportSw.Elapsed.TotalSeconds:F1}s");
                var exportTime = phaseSw.Elapsed;

                // Export asset order + helpers while data is still in memory
                phaseSw.Restart();
                var helpersExportDir = Path.Combine(tempDir, "Helpers");
                var helperRelevantResourceTypes = GetHelperRelevantResourceTypes(originalHashes, modifiedHashes, changedNamesPerType);
                foreach (var forcedType in helperForcedResourceTypes)
                    helperRelevantResourceTypes.Add(forcedType);
                if (RequiresPatchHelpers(helperRelevantResourceTypes, helperForcedResourceTypes))
                {
                    bool includeVariablesFunctions = RequiresVariableFunctionsHelper(helperRelevantResourceTypes);
                    bool includeTextureHelpers = RequiresTextureMappingHelpers(helperRelevantResourceTypes);
                    ResourceExportService.ExportAssetOrder(
                        modifiedData,
                        helpersExportDir,
                        includeObjectEvents: RequiresObjectEventsHelper(helperRelevantResourceTypes),
                        includeVariablesFunctions: includeVariablesFunctions,
                        includeTextureHelpers: includeTextureHelpers);
                }
                LogService.Log($"[Timing] Modified load: {loadTime.TotalSeconds:F1}s, hash: {hashTime.TotalSeconds:F1}s, export: {exportTime.TotalSeconds:F1}s, helpers: {phaseSw.Elapsed.TotalSeconds:F1}s, peak RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
            }
            else
            {
                using (var stream = OpenDataReadStream(modifiedPath))
                {
                    var modifiedData = UndertaleIO.Read(stream);
                    var loadTime = phaseSw.Elapsed;
                    LogService.Log($"[PatchService] Modified: {modifiedData.GeneralInfo?.DisplayName?.Content ?? "Unknown"}");
                    var cachedModified = G3MCacheService.TryReadDataCache(modifiedPath, modifiedCacheOptions);
                    if (cachedModified != null)
                    {
                        modifiedInfo = cachedModified.DataInfo;
                        modifiedHashes = cachedModified.ResourceHashes;
                    }
                    else
                    {
                        modifiedInfo = new DataFileInfo
                        {
                            Filename = Path.GetFileName(modifiedPath),
                            Size = new FileInfo(modifiedPath).Length,
                            Md5 = await HashService.ComputeFileHashAsync(modifiedPath),
                            BytecodeVersion = modifiedData.GeneralInfo?.BytecodeVersion ?? 0,
                            GmsVersion = GeneralInfoUtil.GetVersionDisplay(modifiedData.GeneralInfo),
                            GeneralInfo = GeneralInfoUtil.ExtractGeneralInfo(modifiedData)
                        };
                        phaseSw.Restart();
                        LogService.Log("[PatchService] Hashing modified resources in memory...");
                        modifiedHashes = ResourceHashService.HashAll(modifiedData, modifiedPath);
                        await G3MCacheService.WriteDataCacheAsync(
                            modifiedPath,
                            modifiedInfo,
                            modifiedHashes,
                            GetResourceNameCounts(modifiedData),
                            GetOrderSensitiveResourceNames(modifiedData),
                            modifiedCacheOptions);
                    }

                    var compatibilityError = GetCreateCompatibilityError(originalInfo, modifiedInfo);
                    if (compatibilityError != null)
                        return new PatchCreateResult { Success = false, Error = compatibilityError };

                    // Hash modified resources in memory (fast comparison)
                    LogService.Progress(40, 100);
                    var hashTime = phaseSw.Elapsed;

                    // Compare ALL hashes NOW (before export) to identify changed/new resources per type
                    phaseSw.Restart();
                    LogService.Log("[PatchService] Comparing hashes to build selective export filter...");
                    changedNamesPerType = [];
                    foreach (var resourceType in ResourceTypeRegistry.AllTypes)
                    {
                        var origTypeHashes = originalHashes.GetValueOrDefault(resourceType) ?? [];
                        var modTypeHashes = modifiedHashes.GetValueOrDefault(resourceType) ?? [];
                        var changedNames = new HashSet<string>();
                        foreach (var (name, hash) in modTypeHashes)
                        {
                            if (!origTypeHashes.TryGetValue(name, out var origHash) || origHash != hash)
                                changedNames.Add(name);
                        }
                        if (changedNames.Count > 0)
                            changedNamesPerType[resourceType] = changedNames;
                    }

                    AddDuplicateCountChanges(originalNameCounts, modifiedData, changedNamesPerType);
                    PromoteOrderSensitiveExportsIfNeeded(
                        originalOrderedNames,
                        modifiedData,
                        modifiedHashes,
                        changedNamesPerType,
                        helperForcedResourceTypes);

                    PromoteVersionSensitiveExportsIfNeeded(
                        originalInfo.GeneralInfo,
                        modifiedInfo.GeneralInfo,
                        modifiedHashes,
                        changedNamesPerType);
                    LogService.Log($"[Timing] Hash comparison (pre-export): {phaseSw.Elapsed.TotalMilliseconds:F0}ms");

                    // If a parent code entry changed, include all its children too.
                    if (changedNamesPerType.TryGetValue("CodeEntries", out var changedCode))
                    {
                        var additionalChildren = new HashSet<string>();
                        foreach (var code in modifiedData.Code)
                        {
                            if (code?.Name?.Content != null && code.ParentEntry?.Name?.Content != null)
                            {
                                if (changedCode.Contains(code.ParentEntry.Name.Content) && !changedCode.Contains(code.Name.Content))
                                    additionalChildren.Add(code.Name.Content);
                            }
                        }
                        if (additionalChildren.Count > 0)
                        {
                            foreach (var child in additionalChildren)
                                changedCode.Add(child);
                            LogService.Log($"[PatchService] Added {additionalChildren.Count} child code entries for changed parents");
                        }
                    }

                    // Export only changed/new resources (selective export for ALL types)
                    phaseSw.Restart();
                    LogService.Log("[PatchService] Exporting modified resources (selective)...");
                    ResourceExportService.ExportSelectiveExceptCode(modifiedData, modifiedExportDir, modifiedPath, changedNamesPerType);

                    // Export code entries to memory; patch writing uses them directly.
                    var changedCodeNames = changedNamesPerType.GetValueOrDefault("CodeEntries") ?? [];
                    var codeExportSw = Stopwatch.StartNew();
                    codeEntriesInMemory = ResourceExportService.ExportCodeEntriesToMemory(modifiedData, changedCodeNames);
                    codeExportSw.Stop();
                    LogService.Log($"[Export Timing] CodeEntries (in-memory {changedCodeNames.Count}): {codeExportSw.Elapsed.TotalSeconds:F1}s");
                    var exportTime = phaseSw.Elapsed;

                    // Export asset order + helpers while data is still in memory
                    phaseSw.Restart();
                    var helpersExportDir = Path.Combine(tempDir, "Helpers");
                    var helperRelevantResourceTypes = GetHelperRelevantResourceTypes(originalHashes, modifiedHashes, changedNamesPerType);
                    foreach (var forcedType in helperForcedResourceTypes)
                        helperRelevantResourceTypes.Add(forcedType);
                    if (RequiresPatchHelpers(helperRelevantResourceTypes, helperForcedResourceTypes))
                    {
                        bool includeVariablesFunctions = RequiresVariableFunctionsHelper(helperRelevantResourceTypes);
                        bool includeTextureHelpers = RequiresTextureMappingHelpers(helperRelevantResourceTypes);
                        ResourceExportService.ExportAssetOrder(
                            modifiedData,
                            helpersExportDir,
                            includeObjectEvents: RequiresObjectEventsHelper(helperRelevantResourceTypes),
                            includeVariablesFunctions: includeVariablesFunctions,
                            includeTextureHelpers: includeTextureHelpers);
                    }
                    LogService.Log($"[Timing] Modified load: {loadTime.TotalSeconds:F1}s, hash: {hashTime.TotalSeconds:F1}s, export: {exportTime.TotalSeconds:F1}s, helpers: {phaseSw.Elapsed.TotalSeconds:F1}s, peak RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
                }
                phaseSw.Restart();
                GC.Collect();
                LogService.Log($"[Timing] GC after modified: {phaseSw.Elapsed.TotalMilliseconds:F0}ms, RAM after GC: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
            }
            LogService.Progress(60, 100);

            var manifest = new G3MPatchManifest
            {
                CreatedAt = DateTime.UtcNow.ToString("o"),
                Tool = new Models.ToolInfo { Name = "G3MTool", Version = AppVersionService.Version },
                Original = originalInfo,
                Modified = modifiedInfo,
                Resources = [],
                Statistics = new PatchStatistics()
            };

            try
            {

                // Compare resources using in-memory hashes (instant, no file I/O)
                phaseSw.Restart();
                LogService.Log("[PatchService] Comparing resources by in-memory hash...");

                foreach (var resourceType in ResourceTypeRegistry.AllTypes)
                {
                    var origTypeHashes = originalHashes.GetValueOrDefault(resourceType) ?? [];
                    var modTypeHashes = modifiedHashes.GetValueOrDefault(resourceType) ?? [];
                    var changes = BuildResourceTypeChangesFromKnownDifferences(
                        origTypeHashes,
                        modTypeHashes,
                        modifiedExportDir,
                        resourceType,
                        changedNamesPerType.GetValueOrDefault(resourceType));
                    if (changes.HasChanges)
                        manifest.Resources[resourceType] = changes;
                }
                LogService.Progress(65, 100);
                LogService.Log($"[Timing] Hash comparison: {phaseSw.Elapsed.TotalSeconds:F1}s");

                // Calculate statistics: resource counts + file-level counts
                foreach (var (_, changes) in manifest.Resources)
                {
                    manifest.Statistics.TotalChanged += changes.Changed?.Count ?? 0;
                    manifest.Statistics.TotalNew += changes.New?.Count ?? 0;
                    manifest.Statistics.TotalDeleted += changes.Deleted?.Count ?? 0;
                    foreach (var c in changes.Changed ?? Enumerable.Empty<ResourceChange>())
                        manifest.Statistics.TotalChangedFiles += c.Files?.Count ?? 0;
                    foreach (var n in changes.New ?? Enumerable.Empty<ResourceChange>())
                        manifest.Statistics.TotalNewFiles += n.Files?.Count ?? 0;
                }

                manifest.ApplyPlan = BuildPatchApplyPlan(manifest.Resources);

                // Create output directory
                var outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);

                // Write changed resources to the patch file
                phaseSw.Restart();
                LogService.Log("[PatchService] Creating .g3mpatch file...");
                using (var zipStream = new FileStream(outputPath, FileMode.Create))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    // Add manifest
                    var manifestEntry = archive.CreateEntry("g3mpatch.json", ArchiveCompressionUtil.GetLevel("g3mpatch.json"));
                    using (var writer = new StreamWriter(manifestEntry.Open()))
                    {
                        var json = JsonSerializer.Serialize(manifest, s_jsonOptions);
                        await writer.WriteAsync(json);
                    }

                    // Add changed and new resources from modified export
                    foreach (var (resourceType, changes) in manifest.Resources)
                    {
                        // CodeEntries: ALL entries added below for full recompilation
                        if (resourceType == "CodeEntries")
                            continue;

                        var modifiedResDir = Path.Combine(modifiedExportDir, resourceType);

                        // Include all files for each changed/new resource (metadata + data files)
                        foreach (var changed in changes.Changed ?? Enumerable.Empty<ResourceChange>())
                        {
                            await AddResourceToArchiveAsync(archive, modifiedResDir, resourceType, changed.Name!);
                        }

                        foreach (var newRes in changes.New ?? Enumerable.Empty<ResourceChange>())
                        {
                            await AddResourceToArchiveAsync(archive, modifiedResDir, resourceType, newRes.Name!);
                        }
                    }

                    // CodeEntries: write directly from memory.
                    if (codeEntriesInMemory.Count > 0)
                    {
                        foreach (var (archiveKey, entry) in codeEntriesInMemory)
                        {
                            if (entry.gml != null)
                            {
                                var gmlPath = $"CodeEntries/{archiveKey}/{archiveKey}.gml";
                                var gmlEntry = archive.CreateEntry(gmlPath, ArchiveCompressionUtil.GetLevel(gmlPath));
                                using var gmlStream = new StreamWriter(gmlEntry.Open(), Encoding.UTF8);
                                await gmlStream.WriteAsync(entry.gml);
                            }
                            if (entry.asm != null)
                            {
                                var asmPath = $"CodeEntries/{archiveKey}/{archiveKey}.asm";
                                var asmEntry = archive.CreateEntry(asmPath, ArchiveCompressionUtil.GetLevel(asmPath));
                                using var asmStream = new StreamWriter(asmEntry.Open(), Encoding.UTF8);
                                await asmStream.WriteAsync(entry.asm);
                            }
                            if (entry.childAsms != null)
                            {
                                foreach (var (childLeafKey, childEntry) in entry.childAsms)
                                {
                                    var childPath = $"CodeEntries/{archiveKey}/{childLeafKey}.asm";
                                    var childArchiveEntry = archive.CreateEntry(childPath, ArchiveCompressionUtil.GetLevel(childPath));
                                    using var childStream = new StreamWriter(childArchiveEntry.Open(), Encoding.UTF8);
                                    await childStream.WriteAsync(childEntry.Asm);
                                }
                            }
                        }
                        LogService.Log($"[PatchService] CodeEntries: {codeEntriesInMemory.Count} written directly to patch");
                    }

                    // Add exported helper files into Helpers/.
                    // (already exported by ResourceExportService.ExportAssetOrder during modified data phase)
                    var helpersDir = Path.Combine(tempDir, "Helpers");
                    if (Directory.Exists(helpersDir))
                    {
                        foreach (var file in Directory.GetFiles(helpersDir, "*", SearchOption.AllDirectories))
                        {
                            string relative = Path.GetRelativePath(helpersDir, file).Replace('\\', '/');
                            if (relative.StartsWith("EmbeddedTextures/", StringComparison.OrdinalIgnoreCase))
                                continue;
                            var helperEntryPath = $"Helpers/{relative}";
                            var entry = archive.CreateEntry(helperEntryPath, ArchiveCompressionUtil.GetLevel(helperEntryPath));
                            using var entryStream = entry.Open();
                            using var fileStream = File.OpenRead(file);
                            await fileStream.CopyToAsync(entryStream);
                        }
                        LogService.Log("[PatchService] Helpers data added to patch");
                    }

                    if (includeXdeltaFallback && exactPatchSourcePath == null)
                    {
                        var xdeltaService = new XDeltaService();
                        tempExactPatch = Path.Combine(
                            Path.GetTempPath(), $"g3mtool_xdelta_fallback_{Guid.NewGuid():N}.xdelta"
                        );
                        var exactResult = await xdeltaService.CreatePatchAsync(
                            originalPath, modifiedPath, tempExactPatch
                        );
                        if (exactResult.Success && File.Exists(tempExactPatch))
                        {
                            exactPatchSourcePath = tempExactPatch;
                            LogService.Log("[PatchService] Embedded xdelta fallback");
                        }
                        else
                        {
                            LogService.Warning(
                                $"[PatchService] Xdelta fallback was not created: {exactResult.Error}"
                            );
                        }
                    }

                    if (includeXdeltaFallback && exactPatchSourcePath != null && File.Exists(exactPatchSourcePath))
                    {
                        var exactEntry = archive.CreateEntry(
                            $"Xdelta/{Path.GetFileName(exactPatchSourcePath)}",
                            CompressionLevel.Fastest
                        );
                        using var exactStream = exactEntry.Open();
                        using var exactFile = File.OpenRead(exactPatchSourcePath);
                        await exactFile.CopyToAsync(exactStream);
                    }

                }

                LogService.Log($"[Timing] Patch write: {phaseSw.Elapsed.TotalSeconds:F1}s");
                LogService.Progress(100, 100);
                LogService.ProgressComplete();

                totalSw.Stop();
                LogService.Log($"[Timing] === PATCH CREATE TOTAL: {totalSw.Elapsed.TotalSeconds:F1}s === RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");

                return new PatchCreateResult
                {
                    Success = true,
                    Statistics = manifest.Statistics,
                    Message = FormatPatchStats(manifest.Statistics)
                };
            }
            finally
            {
                // Cleanup temp directories
                try { Directory.Delete(tempDir, true); } catch { }
                // Cleanup temp xdelta result file if created
                if (tempXdeltaResult != null)
                    try { File.Delete(tempXdeltaResult); } catch { }
                if (tempExactPatch != null)
                    try { File.Delete(tempExactPatch); } catch { }
            }
        }
        catch (Exception ex)
        {
            // Cleanup temp xdelta result file on error
            if (tempXdeltaResult != null)
                try { File.Delete(tempXdeltaResult); } catch { }
            if (tempExactPatch != null)
                try { File.Delete(tempExactPatch); } catch { }
            return new PatchCreateResult { Success = false, Error = $"Failed to create patch: {ex.Message}" };
        }
    }

    /// <summary>
    /// Forces full export of resource types whose binary layout depends on the GameMaker version.
    /// This prevents merged patches from leaving a small number of untouched resources
    /// in an older on-disk format when the patch upgrades the target game version.
    /// </summary>
    private static Dictionary<string, Dictionary<string, int>> ReadResourceNameCounts(string dataPath)
    {
        using var stream = OpenDataReadStream(dataPath);
        var data = UndertaleIO.Read(stream);
        return GetResourceNameCounts(data);
    }

    private static Dictionary<string, Dictionary<string, int>> GetResourceNameCounts(UndertaleData data)
    {
        var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
        {
            ["GameObjects"] = CountResourceNames(data.GameObjects),
            ["Sprites"] = CountResourceNames(data.Sprites),
            ["Sounds"] = CountResourceNames(data.Sounds),
            ["Rooms"] = CountResourceNames(data.Rooms),
            ["Scripts"] = CountResourceNames(data.Scripts),
            ["Fonts"] = CountResourceNames(data.Fonts),
            ["Backgrounds"] = CountResourceNames(data.Backgrounds),
            ["Paths"] = CountResourceNames(data.Paths),
            ["Timelines"] = CountResourceNames(data.Timelines),
            ["Shaders"] = CountResourceNames(data.Shaders),
            ["Extensions"] = CountResourceNames(data.Extensions),
            ["AudioGroups"] = CountResourceNames(data.AudioGroups)
        };
        return result;
    }

    private static Dictionary<string, int> CountResourceNames<T>(IList<T>? list)
        where T : UndertaleNamedResource
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (list == null)
            return counts;
        foreach (var item in list)
        {
            var name = item?.Name?.Content;
            if (string.IsNullOrEmpty(name))
                continue;
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        return counts;
    }

    private static void AddDuplicateCountChanges(
        Dictionary<string, Dictionary<string, int>> originalNameCounts,
        UndertaleData modifiedData,
        Dictionary<string, HashSet<string>> changedNamesPerType)
    {
        AddDuplicateCountChangesForType("GameObjects", originalNameCounts, modifiedData.GameObjects, changedNamesPerType);
        AddDuplicateCountChangesForType("Sprites", originalNameCounts, modifiedData.Sprites, changedNamesPerType);
        AddDuplicateCountChangesForType("Sounds", originalNameCounts, modifiedData.Sounds, changedNamesPerType);
        AddDuplicateCountChangesForType("Rooms", originalNameCounts, modifiedData.Rooms, changedNamesPerType);
        AddDuplicateCountChangesForType("Scripts", originalNameCounts, modifiedData.Scripts, changedNamesPerType);
        AddDuplicateCountChangesForType("Fonts", originalNameCounts, modifiedData.Fonts, changedNamesPerType);
        AddDuplicateCountChangesForType("Backgrounds", originalNameCounts, modifiedData.Backgrounds, changedNamesPerType);
        AddDuplicateCountChangesForType("Paths", originalNameCounts, modifiedData.Paths, changedNamesPerType);
        AddDuplicateCountChangesForType("Timelines", originalNameCounts, modifiedData.Timelines, changedNamesPerType);
        AddDuplicateCountChangesForType("Shaders", originalNameCounts, modifiedData.Shaders, changedNamesPerType);
        AddDuplicateCountChangesForType("Extensions", originalNameCounts, modifiedData.Extensions, changedNamesPerType);
        AddDuplicateCountChangesForType("AudioGroups", originalNameCounts, modifiedData.AudioGroups, changedNamesPerType);
    }

    private static void AddDuplicateCountChangesForType<T>(
        string resourceType,
        Dictionary<string, Dictionary<string, int>> originalNameCounts,
        IList<T>? modified,
        Dictionary<string, HashSet<string>> changedNamesPerType)
        where T : UndertaleNamedResource
    {
        if (modified == null)
            return;
        originalNameCounts.TryGetValue(resourceType, out var originalCounts);
        originalCounts ??= [];
        var modifiedCounts = CountResourceNames(modified);
        bool countChanged = modifiedCounts.Any(pair => originalCounts.GetValueOrDefault(pair.Key) != pair.Value);
        if (countChanged && resourceType.Equals("GameObjects", StringComparison.OrdinalIgnoreCase))
        {
            changedNamesPerType[resourceType] = [.. modifiedCounts.Keys];
            LogService.Log($"[PatchService] {resourceType}: duplicate/name count changed, exporting full type");
            return;
        }

        foreach (var (name, count) in modifiedCounts)
        {
            if (originalCounts.GetValueOrDefault(name) == count)
                continue;
            if (!changedNamesPerType.TryGetValue(resourceType, out var changedNames))
                changedNamesPerType[resourceType] = changedNames = [];
            changedNames.Add(name);
        }
    }

    private static void PromoteVersionSensitiveExportsIfNeeded(
        GeneralInfoData? originalInfo,
        GeneralInfoData? modifiedInfo,
        Dictionary<string, Dictionary<string, string>> modifiedHashes,
        Dictionary<string, HashSet<string>> changedNamesPerType)
    {
        if (originalInfo == null || modifiedInfo == null)
            return;

        bool versionChanged =
            originalInfo.Major != modifiedInfo.Major ||
            originalInfo.Minor != modifiedInfo.Minor ||
            originalInfo.Release != modifiedInfo.Release ||
            originalInfo.Build != modifiedInfo.Build;
        if (!versionChanged)
            return;

        PromoteResourceTypeToFullExport(changedNamesPerType, modifiedHashes, "Rooms");
        PromoteResourceTypeToFullExport(changedNamesPerType, modifiedHashes, "TextureGroupInfo");
    }

    private static void PromoteOrderSensitiveExportsIfNeeded(
        Dictionary<string, IReadOnlyList<string>> originalOrderedNames,
        UndertaleData modifiedData,
        Dictionary<string, Dictionary<string, string>> modifiedHashes,
        Dictionary<string, HashSet<string>> changedNamesPerType,
        HashSet<string> helperForcedResourceTypes)
    {
        PromoteOrderSensitiveExportIfNeeded(
            "Rooms",
            originalOrderedNames,
            modifiedData.Rooms?.Select(r => r?.Name?.Content ?? "").ToList(),
            modifiedHashes,
            changedNamesPerType,
            helperForcedResourceTypes,
            forceHelpers: true);

        PromoteOrderSensitiveExportIfNeeded(
            "Scripts",
            originalOrderedNames,
            modifiedData.Scripts?.Select(s => s?.Name?.Content ?? "").ToList(),
            modifiedHashes,
            changedNamesPerType,
            helperForcedResourceTypes,
            forceHelpers: false);
    }

    private static void PromoteOrderSensitiveExportIfNeeded(
        string resourceType,
        Dictionary<string, IReadOnlyList<string>> originalOrderedNames,
        IReadOnlyList<string>? modifiedOrderedNames,
        Dictionary<string, Dictionary<string, string>> modifiedHashes,
        Dictionary<string, HashSet<string>> changedNamesPerType,
        HashSet<string> helperForcedResourceTypes,
        bool forceHelpers)
    {
        if (!originalOrderedNames.TryGetValue(resourceType, out var originalNames) || modifiedOrderedNames == null)
            return;

        if (originalNames.SequenceEqual(modifiedOrderedNames, StringComparer.Ordinal))
            return;

        PromoteResourceTypeToFullExport(changedNamesPerType, modifiedHashes, resourceType);
        if (forceHelpers)
        {
            helperForcedResourceTypes.Add(resourceType);
            LogService.Log($"[PatchService] {resourceType}: order changed, exporting full type and forcing asset-order helpers");
        }
        else
        {
            LogService.Log($"[PatchService] {resourceType}: order changed, exporting full type");
        }
    }

    private static void PromoteResourceTypeToFullExport(
        Dictionary<string, HashSet<string>> changedNamesPerType,
        Dictionary<string, Dictionary<string, string>> modifiedHashes,
        string resourceType)
    {
        if (!modifiedHashes.TryGetValue(resourceType, out var typeHashes) || typeHashes.Count == 0)
            return;

        changedNamesPerType[resourceType] = [.. typeHashes.Keys];
        LogService.Log($"[PatchService] Version change detected, exporting all {resourceType} resources");
    }

    private static Dictionary<string, IReadOnlyList<string>> ReadOrderSensitiveResourceNames(string dataPath)
    {
        using var stream = OpenDataReadStream(dataPath);
        var data = UndertaleIO.Read(stream);
        return GetOrderSensitiveResourceNames(data);
    }

    private static Dictionary<string, IReadOnlyList<string>> GetOrderSensitiveResourceNames(UndertaleData data)
    {
        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Rooms"] = data.Rooms?.Select(r => r?.Name?.Content ?? "").ToList() ?? [],
            ["Scripts"] = data.Scripts?.Select(s => s?.Name?.Content ?? "").ToList() ?? []
        };
    }

    public static Dictionary<string, Dictionary<string, int>> GetResourceNameCountsForReuse(UndertaleData data) =>
        GetResourceNameCounts(data);

    public static Dictionary<string, IReadOnlyList<string>> GetOrderSensitiveResourceNamesForReuse(UndertaleData data) =>
        GetOrderSensitiveResourceNames(data);

    /// <summary>
    /// Compares two in-memory hash dictionaries (resourceName -> hash) and produces ResourceTypeChanges.
    /// Uses the exported modified directory to resolve folder names with __idx suffixes.
    /// </summary>
    private static ResourceTypeChanges CompareHashDictionaries(
        Dictionary<string, string> originalHashes,
        Dictionary<string, string> modifiedHashes,
        string modifiedExportDir,
        string resourceType,
        HashSet<string>? forcedExportNames = null)
    {
        var changes = new ResourceTypeChanges
        {
            Changed = [],
            New = [],
            Deleted = []
        };

        // Build folder name mapping from exported modified directory
        // This maps base names to actual folder names (which may include __idx suffixes)
        var modifiedResDir = Path.Combine(modifiedExportDir, resourceType);
        var folderMap = BuildBaseNameMap(modifiedResDir);

        var originalNames = originalHashes.Keys.ToHashSet();
        var modifiedNames = modifiedHashes.Keys.ToHashSet();

        // New resources (in modified but not in original)
        foreach (var name in modifiedNames.Except(originalNames))
        {
            List<string> folderNames = [];
            if (forcedExportNames?.Contains(name) == true)
            {
                if (folderMap.TryGetValue(name, out var mappedFolder))
                    folderNames.Add(mappedFolder);
                else
                    folderNames = GetFolderNamesForBaseName(modifiedResDir, name);
            }
            if (folderNames.Count == 0)
                folderNames.Add(folderMap.GetValueOrDefault(name) ?? name);
            foreach (var folderName in folderNames)
                changes.New.Add(new ResourceChange { Name = folderName });
        }

        // Deleted resources (in original but not in modified)
        foreach (var name in originalNames.Except(modifiedNames))
        {
            changes.Deleted.Add(name);
        }

        // Changed resources (different hashes)
        foreach (var name in originalNames.Intersect(modifiedNames))
        {
            bool forceInclude = forcedExportNames?.Contains(name) == true;
            if (forceInclude || originalHashes[name] != modifiedHashes[name])
            {
                List<string> folderNames = [];
                if (forceInclude)
                {
                    if (folderMap.TryGetValue(name, out var mappedFolder))
                        folderNames.Add(mappedFolder);
                    else
                        folderNames = GetFolderNamesForBaseName(modifiedResDir, name);
                }
                if (folderNames.Count == 0)
                    folderNames.Add(folderMap.GetValueOrDefault(name) ?? name);
                foreach (var folderName in folderNames)
                    changes.Changed.Add(new ResourceChange { Name = folderName });
            }
        }

        return changes;
    }

    private static ResourceTypeChanges BuildResourceTypeChangesFromKnownDifferences(
        Dictionary<string, string> originalHashes,
        Dictionary<string, string> modifiedHashes,
        string modifiedExportDir,
        string resourceType,
        HashSet<string>? knownChangedOrNewNames)
    {
        var changes = new ResourceTypeChanges
        {
            Changed = [],
            New = [],
            Deleted = []
        };

        var modifiedResDir = Path.Combine(modifiedExportDir, resourceType);
        var folderMap = BuildBaseNameMap(modifiedResDir);

        if (knownChangedOrNewNames != null)
        {
            foreach (var name in knownChangedOrNewNames)
            {
                List<string> folderNames = [];
                if (folderMap.TryGetValue(name, out var mappedFolder))
                    folderNames.Add(mappedFolder);
                else
                    folderNames = GetFolderNamesForBaseName(modifiedResDir, name);

                if (folderNames.Count == 0)
                    folderNames.Add(name);

                bool isNew = !originalHashes.ContainsKey(name) && modifiedHashes.ContainsKey(name);
                foreach (var folderName in folderNames)
                {
                    if (isNew)
                        changes.New!.Add(new ResourceChange { Name = folderName });
                    else
                        changes.Changed!.Add(new ResourceChange { Name = folderName });
                }
            }
        }

        foreach (var name in originalHashes.Keys.Intersect(modifiedHashes.Keys))
        {
            if (knownChangedOrNewNames?.Contains(name) == true || originalHashes[name] == modifiedHashes[name])
                continue;

            List<string> folderNames = [];
            if (folderMap.TryGetValue(name, out var mappedFolder))
                folderNames.Add(mappedFolder);
            else
                folderNames = GetFolderNamesForBaseName(modifiedResDir, name);

            if (folderNames.Count == 0)
                folderNames.Add(name);

            foreach (var folderName in folderNames)
                changes.Changed!.Add(new ResourceChange { Name = folderName });
        }

        foreach (var name in originalHashes.Keys.Except(modifiedHashes.Keys))
            changes.Deleted!.Add(name);

        return changes;
    }

    private static List<string> GetFolderNamesForBaseName(string directory, string baseName)
    {
        if (!Directory.Exists(directory))
            return [];

        var result = new List<string>();
        foreach (var dir in Directory.GetDirectories(directory))
        {
            var folderName = Path.GetFileName(dir)!;
            if (string.Equals(StripIdxSuffix(folderName), baseName, StringComparison.Ordinal))
                result.Add(folderName);
        }
        return result;
    }

    /// <summary>
    /// Builds a mapping from base name to folder name for resource comparison.
    /// Strips __idx suffixes so resources that changed index are matched by logical name.
    /// When duplicate base names exist (e.g. same object name at different indices),
    /// falls back to full folder names to keep both entries separate.
    /// </summary>
    private static Dictionary<string, string> BuildBaseNameMap(string directory)
    {
        Dictionary<string, string> map = [];
        if (!Directory.Exists(directory)) return map;

        Dictionary<string, int> baseNameCounts = [];
        List<(string folderName, string baseName)> entries = [];
        foreach (var dir in Directory.GetDirectories(directory))
        {
            var folderName = Path.GetFileName(dir)!;
            var baseName = StripIdxSuffix(folderName);
            entries.Add((folderName, baseName));
            baseNameCounts[baseName] = baseNameCounts.GetValueOrDefault(baseName) + 1;
        }

        foreach (var (folderName, baseName) in entries)
        {
            if (baseNameCounts[baseName] > 1)
                map[folderName] = folderName;
            else
                map[baseName] = folderName;
        }
        return map;
    }

    /// <summary>
    /// Strips __idx#### suffix from export folder names so resources are matched by logical name.
    /// </summary>
    private static string StripIdxSuffix(string folderName)
    {
        int idxPos = folderName.LastIndexOf("__idx");
        if (idxPos <= 0) return folderName;

        string after = folderName[(idxPos + 5)..];
        if (after.Length > 0 && after.All(char.IsDigit))
            return folderName[..idxPos];

        return folderName;
    }

    private static async Task AddResourceToArchiveAsync(ZipArchive archive, string sourceDir, string resourceType, string resourceName, Dictionary<string, string>? filesToInclude = null)
    {
        var resourcePath = Path.Combine(sourceDir, resourceName);

        // Handle file-based resources (like GeneralInfo) where files are directly in sourceDir
        // In this case resourceName equals the directory name itself
        if (!Directory.Exists(resourcePath) && resourceName == Path.GetFileName(sourceDir))
        {
            if (!Directory.Exists(sourceDir))
                return;
            // Files are directly in sourceDir, not in a subdirectory
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relativeToResource = Path.GetRelativePath(sourceDir, file);

                if (filesToInclude != null && !filesToInclude.ContainsKey(relativeToResource))
                    continue;

                var entryPath = Path.Combine(resourceType, relativeToResource).Replace('\\', '/');

                var entry = archive.CreateEntry(entryPath, ArchiveCompressionUtil.GetLevel(entryPath));
                using var entryStream = entry.Open();
                using var fileStream = File.OpenRead(file);
                await fileStream.CopyToAsync(entryStream);
            }
            return;
        }

        if (!Directory.Exists(resourcePath))
            return;

        foreach (var file in Directory.GetFiles(resourcePath, "*", SearchOption.AllDirectories))
        {
            var relativeToResource = Path.GetRelativePath(resourcePath, file);

            // If filesToInclude is specified, only add files that are in the list
            if (filesToInclude != null && !filesToInclude.ContainsKey(relativeToResource))
                continue;

            var entryPath = Path.Combine(resourceType, resourceName, relativeToResource).Replace('\\', '/');

            var entry = archive.CreateEntry(entryPath, ArchiveCompressionUtil.GetLevel(entryPath));
            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(file);
            await fileStream.CopyToAsync(entryStream);
        }
    }

    public static async Task<PatchApplyResult> ApplyPatchAsync(
        string dataPath,
        string patchPath,
        string outputPath,
        bool allowXdeltaFallback = false,
        bool verifyModifiedHash = true)
    {
        if (!File.Exists(dataPath))
            return new PatchApplyResult { Success = false, Error = $"Data file not found: {dataPath}" };

        if (!File.Exists(patchPath))
            return new PatchApplyResult { Success = false, Error = $"Patch file not found: {patchPath}" };

        PatchFileSystem? pfsForFallback = null;
        G3MPatchManifest? manifestForFallback = null;

        try
        {
            var totalSw = Stopwatch.StartNew();
            var phaseSw = new Stopwatch();
            var patchLoadSw = new Stopwatch();
            var dataLoadSw = new Stopwatch();
            var nonCodeImportSw = new Stopwatch();
            var codeImportSw = new Stopwatch();
            var finalizeSw = new Stopwatch();
            var saveSw = new Stopwatch();
            var verifyHashSw = new Stopwatch();
            var preCodeReorderSw = new Stopwatch();
            var postSpriteReorderSw = new Stopwatch();
            var finalReorderSw = new Stopwatch();
            var repairDuplicatesSw = new Stopwatch();
            var applyEventsSw = new Stopwatch();
            var repairDanglingCodeSw = new Stopwatch();
            var ensureScriptsSw = new Stopwatch();
            var integritySw = new Stopwatch();

            LogService.SetOperation("Applying patch");
            LogService.Progress(0, 100);

            patchLoadSw.Start();
            phaseSw.Restart();
            LogService.Log("[PatchService] Loading .g3mpatch file into memory...");
            var pfs = await Task.Run(() => PatchFileSystem.LoadFromZip(patchPath, loadExactPayloads: allowXdeltaFallback, skipAsmBackedGml: true));
            G3MPatchManifest? manifest = pfs.Manifest;
            patchLoadSw.Stop();

            pfsForFallback = pfs;
            manifestForFallback = manifest;

            if (allowXdeltaFallback &&
                await TryApplyXdeltaFallbackFromPatchAsync(dataPath, outputPath, patchPath, pfs, manifest))
            {
                LogService.Progress(100, 100);
                LogService.ProgressComplete();
                return new PatchApplyResult { Success = true };
            }

            var applyPlan = manifest?.ApplyPlan ?? BuildPatchApplyPlan(manifest?.Resources ?? new Dictionary<string, ResourceTypeChanges>(StringComparer.OrdinalIgnoreCase));

            dataLoadSw.Start();
            phaseSw.Restart();
            LogService.Log("[PatchService] Loading data file for .g3mpatch apply...");
            UndertaleData data;
            using (var stream = OpenDataReadStream(dataPath))
            {
                data = UndertaleIO.Read(stream);
            }
            dataLoadSw.Stop();

            LogService.Log($"[Timing] Data load: {phaseSw.Elapsed.TotalSeconds:F1}s, RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
            LogService.Progress(5, 100);

            LogService.Log($"[PatchService] Data loaded: {data.GeneralInfo?.DisplayName?.Content ?? "Unknown"}");

            // Set PatchFileSystem for all native importers
            ResourceImportService.SetPatchFileSystem(pfs);

            try
            {
                var resourceTypesToProcess = BuildResourceTypesToProcess(pfs, manifest);

                LogService.Log($"[PatchService] Resources to process: {string.Join(", ", resourceTypesToProcess)}");

                if (resourceTypesToProcess.Count == 0)
                {
                    LogService.Log("[PatchService] No resources to apply");
                    var noOpOutDir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(noOpOutDir) && !Directory.Exists(noOpOutDir))
                        Directory.CreateDirectory(noOpOutDir);
                    File.Copy(dataPath, outputPath, overwrite: true);
                    LogService.Progress(100, 100);
                    LogService.ProgressComplete();
                    return new PatchApplyResult { Success = true };
                }

                if (CanUseMinimalStandardApply(applyPlan, resourceTypesToProcess))
                {
                    LogService.Log($"[PatchService] Applying only required resource functions: {string.Join(", ", resourceTypesToProcess)}");
                    int minimalAppliedCount = 0;
                    int minimalFailedCount = 0;
                    foreach (var resourceType in resourceTypesToProcess)
                    {
                        if (!pfs.DirectoryExists(resourceType))
                            continue;

                        try
                        {
                            ResourceImportService.SetDataPaths(dataPath, outputPath);
                            if (!ResourceImportService.Import(resourceType, data, resourceType))
                                minimalFailedCount++;
                            else
                                minimalAppliedCount++;
                        }
                        catch (Exception ex)
                        {
                            LogService.Warning($"ERROR applying {resourceType}: {ex.Message}");
                            minimalFailedCount++;
                        }
                        finally
                        {
                            ResourceImportService.SetDataPaths(null, null);
                        }
                    }

                    if (minimalFailedCount > 0 || minimalAppliedCount == 0)
                    {
                        return new PatchApplyResult
                        {
                            Success = false,
                            Error = minimalFailedCount > 0
                                ? $"Patch apply failed: {minimalFailedCount} resource type(s) failed during import; output was not written"
                                : "No resources were applied successfully"
                        };
                    }

                    var minimalIntegrity = DataIntegrityService.ValidateOnly(data);
                    if (!minimalIntegrity.Success)
                    {
                        return new PatchApplyResult
                        {
                            Success = false,
                            Error = "Integrity validation failed: " + string.Join("; ", minimalIntegrity.Errors.Take(20))
                        };
                    }

                    phaseSw.Restart();
                    LogService.Log("[PatchService] Saving modified data file...");
                    saveSw.Start();
                    var minimalOutDir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(minimalOutDir) && !Directory.Exists(minimalOutDir))
                        Directory.CreateDirectory(minimalOutDir);
                    using (var outStream = OpenDataWriteStream(outputPath))
                        UndertaleIO.Write(outStream, data);
                    saveSw.Stop();

                    totalSw.Stop();
                    LogService.Log($"[Timing] Minimal apply total: {totalSw.Elapsed.TotalSeconds:F1}s");
                    return new PatchApplyResult { Success = true };
                }

                // Weighted progress ranges
                var resourceWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AudioGroups"] = 1,
                    ["TextureGroupInfo"] = 1,
                    ["Sprites"] = 5,
                    ["Fonts"] = 1,
                    ["Sounds"] = 1,
                    ["Paths"] = 1,
                    ["Shaders"] = 1,
                    ["GameObjects"] = 2,
                    ["Rooms"] = 4,
                    ["Tilesets"] = 1,
                    ["GeneralInfo"] = 1
                };
                int totalNonCodeWeight = 0;
                foreach (var rt in resourceTypesToProcess)
                {
                    if (rt != "CodeEntries")
                        totalNonCodeWeight += resourceWeights.GetValueOrDefault(rt, 1);
                }
                bool hasCodeEntries = resourceTypesToProcess.Contains("CodeEntries");
                int nonCodeRangeStart = 5;
                int nonCodeRangeEnd = hasCodeEntries ? 40 : 90;
                int nonCodeRange = nonCodeRangeEnd - nonCodeRangeStart;
                int nonCodeProgress = 0;

                LogService.Progress(5, 100);

                int appliedCount = 0;
                int failedCount = 0;
                bool requiresFinalAssetReorder = applyPlan.RequiresAssetReorder;
                bool requiresHeavyFinalize = applyPlan.RequiresHeavyFinalize;
                bool touchesTextureWorld = RequiresTextureMappingHelpers(resourceTypesToProcess)
                    || resourceTypesToProcess.Contains("EmbeddedTextures", StringComparer.OrdinalIgnoreCase)
                    || resourceTypesToProcess.Contains("TexturePageItems", StringComparer.OrdinalIgnoreCase);
                bool needsCodeRemapPreparation = hasCodeEntries && RequiresCodeRemapPreparation(resourceTypesToProcess);
                bool needsTexturePreservation = requiresFinalAssetReorder && touchesTextureWorld;
                bool needsFontTexturePreservation = needsTexturePreservation && HasFontTexturePayload(pfs);
                bool needsLocaleArtPayloadScan = requiresFinalAssetReorder && resourceTypesToProcess.Contains("Sprites", StringComparer.OrdinalIgnoreCase);

                var helpersDir = pfs.HelpersPrefix;
                var capturedAssetOrderPath = Path.Combine(helpersDir, "asset_order.txt");
                string[]? capturedAssetOrderLines = (hasCodeEntries || requiresFinalAssetReorder) && pfs.FileExists(capturedAssetOrderPath)
                    ? pfs.ReadAllLines(capturedAssetOrderPath)
                    : null;
                var capturedObjectEventsPath = Path.Combine(helpersDir, "object_events.json");
                byte[]? capturedObjectEventsBytes = (hasCodeEntries || requiresFinalAssetReorder) && pfs.FileExists(capturedObjectEventsPath)
                    ? pfs.ReadAllBytes(capturedObjectEventsPath)
                    : null;
                var capturedTexturePageItemsPath = Path.Combine(helpersDir, "texture_page_items.json");
                byte[]? capturedTexturePageItemsBytes = needsTexturePreservation && pfs.FileExists(capturedTexturePageItemsPath)
                    ? pfs.ReadAllBytes(capturedTexturePageItemsPath)
                    : null;
                var capturedSpriteFrameMapPath = Path.Combine(helpersDir, "sprite_frame_map.json");
                byte[]? capturedSpriteFrameMapBytes = needsTexturePreservation && pfs.FileExists(capturedSpriteFrameMapPath)
                    ? pfs.ReadAllBytes(capturedSpriteFrameMapPath)
                    : null;
                string? capturedVariablesFunctionsContent = hasCodeEntries && pfs.FileExists(Path.Combine(helpersDir, "variables_functions.json"))
                    ? pfs.ReadAllText(Path.Combine(helpersDir, "variables_functions.json"))
                    : null;
                Dictionary<string, List<string>> assetNamesAtCodeImportStart = [];
                var originalGmlForCodeRemap = needsCodeRemapPreparation ? CaptureOriginalCodeGmlForAssetRemap(data, resourceTypesToProcess) : [];
                var patchCodeEntryNames = needsCodeRemapPreparation ? CapturePatchCodeEntryNames(pfs) : [];
                var untouchedEmbeddedTextureSnapshot = needsTexturePreservation ? CaptureUntouchedEmbeddedTextures(data, pfs) : [];
                string? stagedEmbeddedTexturesDir = needsTexturePreservation ? StageEmbeddedTexturesForAssetOrder(data, pfs) : null;
                bool hasFontTexturePayload = needsFontTexturePreservation;
                var localeArtSpritePayloads = needsLocaleArtPayloadScan ? CapturePatchedLocaleArtSpriteNames(pfs) : [];
                string? codeMetadataForFinalCleanup = capturedVariablesFunctionsContent;

                nonCodeImportSw.Start();
                foreach (var resourceType in resourceTypesToProcess)
                {
                    if (ApplyDeletedResources(data, manifest, resourceType) > 0)
                        appliedCount++;

                    // Reorder assets to match TARGET indices before code compilation.
                    if (resourceType == "CodeEntries")
                    {
                        var assetOrderFile = Path.Combine(helpersDir, "asset_order.txt");
                        if (pfs.FileExists(assetOrderFile))
                        {
                            // Create missing GameObjects so TARGET indices resolve correctly.
                            var aoLines = capturedAssetOrderLines ?? pfs.ReadAllLines(assetOrderFile);
                            bool inObjects = false;
                            var orderNameCounts = new Dictionary<string, int>();
                            foreach (var aoLine in aoLines)
                            {
                                if (aoLine.StartsWith("@@"))
                                {
                                    inObjects = aoLine == "@@objects@@";
                                    continue;
                                }
                                if (!inObjects || string.IsNullOrWhiteSpace(aoLine)) continue;
                                string objName = aoLine.Trim();
                                if (objName == "(null)" || int.TryParse(objName, out _)) continue;
                                orderNameCounts[objName] = orderNameCounts.GetValueOrDefault(objName) + 1;
                            }
                            // Count existing objects per name
                            var existingCounts = new Dictionary<string, int>();
                            foreach (var obj in data.GameObjects)
                            {
                                if (obj?.Name?.Content != null)
                                    existingCounts[obj.Name.Content] = existingCounts.GetValueOrDefault(obj.Name.Content) + 1;
                            }
                            int createdObjects = 0;
                            foreach (var (objName, needed) in orderNameCounts)
                            {
                                int have = existingCounts.GetValueOrDefault(objName);
                                for (int ci = have; ci < needed; ci++)
                                {
                                    var newObj = new UndertaleGameObject
                                    {
                                        Name = data.Strings.MakeString(objName)
                                    };
                                    data.GameObjects.Add(newObj);
                                    createdObjects++;
                                }
                            }
                            if (createdObjects > 0)
                                LogService.Log($"[PatchService] Created {createdObjects} missing GameObjects from TARGET order");

                            LogService.Log("[PatchService] Reordering assets to match TARGET order...");
                            try
                            {
                                preCodeReorderSw.Start();
                                // Reorder with asset_order.txt only (exclude TPI/frame data).
                                var reorderDir = Path.Combine(Path.GetTempPath(), $"g3mtool_aoreorder_{Guid.NewGuid():N}");
                                Directory.CreateDirectory(reorderDir);
                                var reorderKeepSections = BuildRelevantAssetOrderSections(resourceTypesToProcess, includeScriptsForCodeEntries: true);
                                reorderKeepSections.Add("counts");
                                var filteredAoLines = SelectAssetOrderSections(
                                    aoLines,
                                    reorderKeepSections,
                                    skipSections: ["TexturePageItems", "EmbeddedTextures"]);
                                File.WriteAllLines(Path.Combine(reorderDir, "asset_order.txt"), filteredAoLines);
                                if (capturedObjectEventsBytes != null)
                                    File.WriteAllBytes(Path.Combine(reorderDir, "object_events.json"), capturedObjectEventsBytes);

                                ResourceImportService.SetPatchFileSystem(null);
                                ResourceImportService.ImportAssetOrder(data, reorderDir);
                                ResourceImportService.SetPatchFileSystem(pfs);
                                appliedCount++;
                                try { Directory.Delete(reorderDir, true); } catch { }
                                preCodeReorderSw.Stop();
                            }
                            catch (Exception ex)
                            {
                                if (preCodeReorderSw.IsRunning) preCodeReorderSw.Stop();
                                LogService.Log($"[PatchService] Asset reorder warning: {ex.Message}");
                            }
                        }

                        if (needsCodeRemapPreparation)
                            assetNamesAtCodeImportStart = CaptureAssetNamesForCodeRemap(data);

                        LogService.Progress(45, 100);
                    }

                    // Import CodeEntries: GML compilation + ASM reassembly
                    if (resourceType == "CodeEntries")
                    {
                        nonCodeImportSw.Stop();
                        // Read variables_functions.json from PFS before releasing file data
                        string? vfContent = null;
                        var vfPath = Path.Combine(helpersDir, "variables_functions.json");
                        if (pfs.FileExists(vfPath))
                            vfContent = capturedVariablesFunctionsContent ?? pfs.ReadAllText(vfPath);
                        codeMetadataForFinalCleanup ??= vfContent;

                        string? objectEventsContent = null;
                        var objectEventsPath = Path.Combine(helpersDir, "object_events.json");
                        if (pfs.FileExists(objectEventsPath))
                            objectEventsContent = pfs.ReadAllText(objectEventsPath);

                        string? deferredGlobalScriptsContent = null;
                        var globalScriptsPath = "GlobalScripts/global_scripts.json";
                        if (pfs.FileExists(globalScriptsPath))
                            deferredGlobalScriptsContent = pfs.ReadAllText(globalScriptsPath);
                        bool hasDeferredScripts = pfs.DirectoryExists("Scripts");

                        LogService.Log($"[PatchService] Applying CodeEntries via hybrid import (in-memory)...");
                        phaseSw.Restart();
                        codeImportSw.Start();

                        try
                        {
                            LogService.Log($"[PatchService] Using {pfs.GmlEntries.Count} GML + {pfs.AsmEntries.Count} ASM entries from PFS");
                            ImportCodeEntriesDirect(data, pfs.GmlEntries, pfs.AsmEntries, pfs.AsmEntryPaths, pfs.CodeEntryLogicalNames, helpersDir, vfContent, objectEventsContent);
                            if (hasDeferredScripts)
                            {
                                ResourceImportService.Import("Scripts", data, "Scripts");
                                LogService.Log("[PatchService] Re-applied Scripts after CodeEntries");
                            }
                            if (deferredGlobalScriptsContent != null)
                            {
                                ApplyDeferredGlobalScripts(data, deferredGlobalScriptsContent);
                                LogService.Log("[PatchService] Re-applied GlobalScripts after CodeEntries");
                            }
                            appliedCount++;
                        }
                        catch (Exception ex)
                        {
                            LogService.Warning($"ERROR applying CodeEntries: {ex.Message}");
                            failedCount++;
                        }

                        // Release code entries after import
                        ResourceImportService.SetPatchFileSystem(null);
                        pfs.ReleaseFileData();
                        pfs.ReleaseCodeEntries();
                        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                        codeImportSw.Stop();
                        LogService.Log($"[PatchService] Released PFS file data, RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");

                        LogService.Log($"[Timing] Import CodeEntries: {phaseSw.Elapsed.TotalSeconds:F1}s, RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");

                        LogService.Progress(90, 100);
                        nonCodeImportSw.Start();
                        continue;
                    }

                    if (hasCodeEntries && resourceType == "Scripts")
                    {
                        LogService.Log("[PatchService] Deferring Scripts import until after CodeEntries...");
                        nonCodeProgress += resourceWeights.GetValueOrDefault(resourceType, 1);
                        LogService.Progress(nonCodeRangeStart + nonCodeRange * nonCodeProgress / Math.Max(totalNonCodeWeight, 1), 100);
                        continue;
                    }

                    var resourceDir = resourceType; // PFS uses resource type as virtual dir

                    if (!pfs.DirectoryExists(resourceDir))
                    {
                        nonCodeProgress += resourceWeights.GetValueOrDefault(resourceType, 1);
                        LogService.Progress(nonCodeRangeStart + nonCodeRange * nonCodeProgress / Math.Max(totalNonCodeWeight, 1), 100);
                        continue;
                    }

                    LogService.Log($"[PatchService] Applying {resourceType}...");
                    phaseSw.Restart();

                    try
                    {
                        ResourceImportService.SetDataPaths(dataPath, outputPath);
                        if (resourceType == "Sprites")
                            ResourceImportService.SetProgressRange(6, 24);
                        if (!ResourceImportService.Import(resourceType, data, resourceDir))
                        {
                            LogService.Warning($"No native importer for {resourceType}, skipping");
                            failedCount++;
                        }
                        else
                        {
                            appliedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.Warning($"ERROR applying {resourceType}: {ex.Message}");
                        failedCount++;
                    }
                    finally
                    {
                        ResourceImportService.SetProgressRange();
                        ResourceImportService.SetDataPaths(null, null);
                    }

                    LogService.Log($"[Timing] Import {resourceType}: {phaseSw.Elapsed.TotalSeconds:F1}s, RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");

                    // Re-run asset reordering after Sprites import to fix indices.
                    if (resourceType == "Sprites")
                    {
                        var reorderAssetOrderFile = Path.Combine(helpersDir, "asset_order.txt");
                        if (pfs.FileExists(reorderAssetOrderFile))
                        {
                            var reorderOnlyDir = Path.Combine(Path.GetTempPath(), $"g3mtool_reorder_{Guid.NewGuid():N}");
                            Directory.CreateDirectory(reorderOnlyDir);
                            var filteredLines = SelectAssetOrderSections(
                                pfs.ReadAllLines(reorderAssetOrderFile),
                                BuildRelevantAssetOrderSections(resourceTypesToProcess, includeScriptsForCodeEntries: false),
                                "TexturePageItems", "EmbeddedTextures");
                            File.WriteAllLines(Path.Combine(reorderOnlyDir, "asset_order.txt"), filteredLines);

                            LogService.Log("[PatchService] Re-ordering assets after sprite import...");
                            ResourceImportService.SetPatchFileSystem(null);
                            try
                            {
                                postSpriteReorderSw.Start();
                                ResourceImportService.ImportAssetOrder(data, reorderOnlyDir);
                                postSpriteReorderSw.Stop();
                            }
                            catch (Exception ex)
                            {
                                if (postSpriteReorderSw.IsRunning) postSpriteReorderSw.Stop();
                                LogService.Log($"[PatchService] Post-sprite reorder warning: {ex.Message}");
                            }
                            ResourceImportService.SetPatchFileSystem(pfs);
                            try { Directory.Delete(reorderOnlyDir, true); } catch { }
                        }
                    }

                    nonCodeProgress += resourceWeights.GetValueOrDefault(resourceType, 1);
                    LogService.Progress(nonCodeRangeStart + nonCodeRange * nonCodeProgress / Math.Max(totalNonCodeWeight, 1), 100);
                }
                nonCodeImportSw.Stop();

                finalizeSw.Start();
                if (capturedAssetOrderLines != null && requiresFinalAssetReorder)
                {
                    var finalReorderDir = Path.Combine(Path.GetTempPath(), $"g3mtool_final_reorder_{Guid.NewGuid():N}");
                    try
                    {
                        Directory.CreateDirectory(finalReorderDir);
                        var finalKeepSections = BuildRelevantAssetOrderSections(resourceTypesToProcess, includeScriptsForCodeEntries: true);
                        finalKeepSections.Add("counts");
                        var filteredLines = SelectAssetOrderSections(
                            capturedAssetOrderLines,
                            finalKeepSections,
                            skipSections: ["TexturePageItems", "EmbeddedTextures"]);
                        bool hasFinalTexturePayload =
                            capturedTexturePageItemsBytes != null ||
                            capturedSpriteFrameMapBytes != null ||
                            stagedEmbeddedTexturesDir != null;
                        if (!hasFinalTexturePayload && AssetOrderAlreadyMatches(data, filteredLines))
                        {
                            LogService.Log("[PatchService] Final asset order already matches target; skipping final reorder.");
                        }
                        else
                        {
                            File.WriteAllLines(Path.Combine(finalReorderDir, "asset_order.txt"), filteredLines);
                            if (capturedObjectEventsBytes != null)
                                File.WriteAllBytes(Path.Combine(finalReorderDir, "object_events.json"), capturedObjectEventsBytes);
                            if (capturedTexturePageItemsBytes != null)
                                File.WriteAllBytes(Path.Combine(finalReorderDir, "texture_page_items.json"), capturedTexturePageItemsBytes);
                            if (capturedSpriteFrameMapBytes != null)
                                File.WriteAllBytes(
                                    Path.Combine(finalReorderDir, "sprite_frame_map.json"),
                                    RemoveUnsafeRelinksFromSpriteFrameMap(capturedSpriteFrameMapBytes, localeArtSpritePayloads, removeFontRelinks: false));
                            if (stagedEmbeddedTexturesDir != null)
                                CopyStagedEmbeddedTexturesForAssetOrder(stagedEmbeddedTexturesDir, finalReorderDir);

                            LogService.Log("[PatchService] Enforcing final asset order after all imports...");
                            ResourceImportService.SetPatchFileSystem(null);
                            finalReorderSw.Start();
                            ResourceImportService.ImportAssetOrder(data, finalReorderDir);
                            finalReorderSw.Stop();
                            ResourceImportService.SetPatchFileSystem(pfs);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (finalReorderSw.IsRunning) finalReorderSw.Stop();
                        LogService.Log($"[PatchService] Final asset reorder warning: {ex.Message}");
                    }
                    finally
                    {
                        ResourceImportService.SetPatchFileSystem(pfs);
                        try { Directory.Delete(finalReorderDir, true); } catch { }
                        if (stagedEmbeddedTexturesDir != null)
                        {
                            try { Directory.Delete(stagedEmbeddedTexturesDir, true); } catch { }
                        }
                    }
                }

                if (capturedVariablesFunctionsContent != null && originalGmlForCodeRemap.Count > 0)
                {
                    RecompileUntouchedCodeForAssetOrderShifts(
                        data,
                        assetNamesAtCodeImportStart,
                        originalGmlForCodeRemap,
                        patchCodeEntryNames,
                        helpersDir,
                        capturedVariablesFunctionsContent);
                }

                RepairDetachedFontTexturePageItems(data);
                RestoreUntouchedEmbeddedTextures(data, untouchedEmbeddedTextureSnapshot);
                TrimUnreferencedTrailingEmbeddedTextures(data);

                repairDuplicatesSw.Start();
                if (RequiresDuplicateGameObjectRepair(resourceTypesToProcess, manifest))
                    RepairDuplicateGameObjectPlaceholders(data);
                repairDuplicatesSw.Stop();
                if (capturedObjectEventsBytes != null &&
                    ShouldApplyAuthoritativeObjectEvents(resourceTypesToProcess, manifest, capturedObjectEventsBytes))
                {
                    applyEventsSw.Start();
                    var objectEventCodeFilter = resourceTypesToProcess.Contains("GameObjects", StringComparer.OrdinalIgnoreCase)
                        ? null
                        : GetChangedCodeEntryNames(manifest);
                    ApplyAuthoritativeObjectEvents(data, Encoding.UTF8.GetString(capturedObjectEventsBytes), objectEventCodeFilter);
                    applyEventsSw.Stop();
                }
                PruneCodeEntriesOutsideTargetMetadata(
                    data,
                    codeMetadataForFinalCleanup ?? ReadVariablesFunctionsContent(pfs, helpersDir));
                PruneDeletedScriptOrphans(data, manifest);
                PruneMissingScriptCodeOrphans(data);
                DataIntegrityResult? integrity = null;
                if (requiresHeavyFinalize)
                {
                    repairDanglingCodeSw.Start();
                    RepairDanglingCodeReferences(data);
                    repairDanglingCodeSw.Stop();
                    if (RequiresScriptEntryEnsure(resourceTypesToProcess))
                    {
                        ensureScriptsSw.Start();
                        EnsureScriptEntriesForLiveScriptCode(data);
                        ensureScriptsSw.Stop();
                    }
                    integritySw.Start();
                    integrity = DataIntegrityService.RepairAndValidate(data);
                    integritySw.Stop();
                }
                else
                {
                    integritySw.Start();
                    integrity = DataIntegrityService.RepairAndValidate(data);
                    integritySw.Stop();
                }

                if (integrity is { Success: false })
                {
                    if (await TryApplyXdeltaFallbackFromPatchAsync(dataPath, outputPath, patchPath, pfs, manifest))
                        return new PatchApplyResult { Success = true };

                    return new PatchApplyResult
                    {
                        Success = false,
                        Error = "Integrity validation failed: " + string.Join("; ", integrity.Errors.Take(20))
                    };
                }

                RepairLocalChildFunctionReferencesFinal(data);

                if (failedCount > 0)
                {
                    finalizeSw.Stop();
                    if (await TryApplyXdeltaFallbackFromPatchAsync(dataPath, outputPath, patchPath, pfs, manifest))
                        return new PatchApplyResult { Success = true };

                    return new PatchApplyResult
                    {
                        Success = false,
                        Error = $"Patch apply failed: {failedCount} resource type(s) failed during import; output was not written"
                    };
                }

                // Save modified data
                phaseSw.Restart();
                LogService.Log("[PatchService] Saving modified data file...");
                saveSw.Start();
                var outDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                    Directory.CreateDirectory(outDir);
                using (var outStream = OpenDataWriteStream(outputPath))
                {
                    UndertaleIO.Write(outStream, data);
                }
                saveSw.Stop();
                finalizeSw.Stop();
                LogService.Log($"[Timing] Final save: {phaseSw.Elapsed.TotalSeconds:F1}s, RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
                LogService.Progress(100, 100);

                LogService.ProgressComplete();

                totalSw.Stop();
                LogService.Log($"[Timing] === PATCH APPLY TOTAL: {totalSw.Elapsed.TotalSeconds:F1}s === RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");

                string? expectedModifiedHash = manifest?.Modified?.Md5;
                if (verifyModifiedHash && allowXdeltaFallback && !string.IsNullOrWhiteSpace(expectedModifiedHash))
                {
                    verifyHashSw.Start();
                    string actualModifiedHash = await HashService.ComputeFileHashAsync(outputPath);
                    verifyHashSw.Stop();
                    if (!actualModifiedHash.Equals(expectedModifiedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        LogService.Warning("[PatchService] Output hash differs from the patch's modified file hash");
                        if (await TryApplyXdeltaFallbackFromPatchAsync(dataPath, outputPath, patchPath, pfs, manifest))
                            return new PatchApplyResult { Success = true };
                    }
                }

                if (appliedCount == 0)
                {
                    if (await TryApplyXdeltaFallbackFromPatchAsync(dataPath, outputPath, patchPath, pfs, manifest))
                        return new PatchApplyResult { Success = true };
                    return new PatchApplyResult { Success = false, Error = "No resources were applied successfully" };
                }

                if (failedCount > 0)
                    LogService.Warning($"Patch applied with warnings: {appliedCount} succeeded, {failedCount} failed");

                LogService.Log(
                    $"[Timing] Apply breakdown: patchLoad={patchLoadSw.Elapsed.TotalSeconds:F2}s, " +
                    $"dataLoad={dataLoadSw.Elapsed.TotalSeconds:F2}s, nonCode={nonCodeImportSw.Elapsed.TotalSeconds:F2}s, " +
                    $"code={codeImportSw.Elapsed.TotalSeconds:F2}s, finalize={finalizeSw.Elapsed.TotalSeconds:F2}s, " +
                    $"save={saveSw.Elapsed.TotalSeconds:F2}s, verifyHash={verifyHashSw.Elapsed.TotalSeconds:F2}s, " +
                    $"reorderPreCode={preCodeReorderSw.Elapsed.TotalSeconds:F2}s, reorderPostSprite={postSpriteReorderSw.Elapsed.TotalSeconds:F2}s, " +
                    $"reorderFinal={finalReorderSw.Elapsed.TotalSeconds:F2}s, repairDupes={repairDuplicatesSw.Elapsed.TotalSeconds:F2}s, " +
                    $"applyEvents={applyEventsSw.Elapsed.TotalSeconds:F2}s, repairDangling={repairDanglingCodeSw.Elapsed.TotalSeconds:F2}s, " +
                    $"ensureScripts={ensureScriptsSw.Elapsed.TotalSeconds:F2}s, integrity={integritySw.Elapsed.TotalSeconds:F2}s");
                LogService.Log("[PatchService] Patch applied successfully");
                return new PatchApplyResult { Success = true };
            }
            finally
            {
                ResourceImportService.SetPatchFileSystem(null);
            }
        }
        catch (Exception ex)
        {
            if (pfsForFallback != null &&
                await TryApplyXdeltaFallbackFromPatchAsync(dataPath, outputPath, patchPath, pfsForFallback, manifestForFallback))
            {
                return new PatchApplyResult { Success = true };
            }

            try
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
            }
            catch
            {
                // Best effort cleanup of partial output.
            }
            return new PatchApplyResult { Success = false, Error = $"Failed to apply patch: {ex.Message}" };
        }
    }

    internal static async Task<PatchApplyResult> ApplyPatchInMemoryAsync(
        UndertaleData data,
        string patchPath,
        string workingDataPath)
    {
        if (!File.Exists(patchPath))
            return new PatchApplyResult { Success = false, Error = $"Patch file not found: {patchPath}" };

        try
        {
            var totalSw = Stopwatch.StartNew();
            var phaseSw = new Stopwatch();
            var patchLoadSw = new Stopwatch();
            var nonCodeImportSw = new Stopwatch();
            var codeImportSw = new Stopwatch();
            var finalizeSw = new Stopwatch();
            var preCodeReorderSw = new Stopwatch();
            var postSpriteReorderSw = new Stopwatch();
            var finalReorderSw = new Stopwatch();
            var repairDuplicatesSw = new Stopwatch();
            var applyEventsSw = new Stopwatch();
            var repairDanglingCodeSw = new Stopwatch();
            var ensureScriptsSw = new Stopwatch();
            var integritySw = new Stopwatch();

            patchLoadSw.Start();
            phaseSw.Restart();
            LogService.Log("[PatchService] Loading .g3mpatch file into memory for in-memory apply...");
            var pfs = await Task.Run(() => PatchFileSystem.LoadFromZip(patchPath, loadExactPayloads: false, skipAsmBackedGml: true));
            G3MPatchManifest? manifest = pfs.Manifest;
            var applyPlan = manifest?.ApplyPlan ?? BuildPatchApplyPlan(manifest?.Resources ?? new Dictionary<string, ResourceTypeChanges>(StringComparer.OrdinalIgnoreCase));
            patchLoadSw.Stop();

            ResourceImportService.SetPatchFileSystem(pfs);

            try
            {
                var resourceTypesToProcess = BuildResourceTypesToProcess(pfs, manifest);

                LogService.Log($"[PatchService] Resources to process (in-memory): {string.Join(", ", resourceTypesToProcess)}");

                if (resourceTypesToProcess.Count == 0)
                    return new PatchApplyResult { Success = true };

                if (CanUseMinimalStandardApply(applyPlan, resourceTypesToProcess))
                {
                    LogService.Log($"[PatchService] Applying only required resource functions (in-memory): {string.Join(", ", resourceTypesToProcess)}");
                    int minimalAppliedCount = 0;
                    int minimalFailedCount = 0;
                    foreach (var resourceType in resourceTypesToProcess)
                    {
                        if (!pfs.DirectoryExists(resourceType))
                            continue;

                        try
                        {
                            ResourceImportService.SetDataPaths(workingDataPath, workingDataPath);
                            if (!ResourceImportService.Import(resourceType, data, resourceType))
                                minimalFailedCount++;
                            else
                                minimalAppliedCount++;
                        }
                        catch (Exception ex)
                        {
                            LogService.Warning($"ERROR applying {resourceType}: {ex.Message}");
                            minimalFailedCount++;
                        }
                        finally
                        {
                            ResourceImportService.SetDataPaths(null, null);
                        }
                    }

                    if (minimalFailedCount > 0 || minimalAppliedCount == 0)
                    {
                        return new PatchApplyResult
                        {
                            Success = false,
                            Error = minimalFailedCount > 0
                                ? $"Patch apply failed: {minimalFailedCount} resource type(s) failed during import; in-memory output was not committed"
                                : "No resources were applied successfully"
                        };
                    }

                    var minimalIntegrity = DataIntegrityService.ValidateOnly(data);
                    if (!minimalIntegrity.Success)
                    {
                        return new PatchApplyResult
                        {
                            Success = false,
                            Error = "Integrity validation failed: " + string.Join("; ", minimalIntegrity.Errors.Take(20))
                        };
                    }

                    totalSw.Stop();
                    LogService.Log($"[Timing] Minimal apply in-memory total: {totalSw.Elapsed.TotalSeconds:F1}s");
                    return new PatchApplyResult { Success = true };
                }

                var resourceWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AudioGroups"] = 1,
                    ["TextureGroupInfo"] = 1,
                    ["Sprites"] = 5,
                    ["Fonts"] = 1,
                    ["Sounds"] = 1,
                    ["Paths"] = 1,
                    ["Shaders"] = 1,
                    ["GameObjects"] = 2,
                    ["Rooms"] = 4,
                    ["Tilesets"] = 1,
                    ["GeneralInfo"] = 1
                };
                int totalNonCodeWeight = 0;
                foreach (var rt in resourceTypesToProcess)
                {
                    if (rt != "CodeEntries")
                        totalNonCodeWeight += resourceWeights.GetValueOrDefault(rt, 1);
                }
                bool hasCodeEntries = resourceTypesToProcess.Contains("CodeEntries");
                int nonCodeRangeStart = 5;
                int nonCodeRangeEnd = hasCodeEntries ? 40 : 90;
                int nonCodeRange = nonCodeRangeEnd - nonCodeRangeStart;
                int nonCodeProgress = 0;

                int appliedCount = 0;
                int failedCount = 0;
                bool requiresFinalAssetReorder = applyPlan.RequiresAssetReorder;
                bool requiresHeavyFinalize = applyPlan.RequiresHeavyFinalize;
                bool touchesTextureWorld = RequiresTextureMappingHelpers(resourceTypesToProcess)
                    || resourceTypesToProcess.Contains("EmbeddedTextures", StringComparer.OrdinalIgnoreCase)
                    || resourceTypesToProcess.Contains("TexturePageItems", StringComparer.OrdinalIgnoreCase);
                bool needsCodeRemapPreparation = hasCodeEntries && RequiresCodeRemapPreparation(resourceTypesToProcess);
                bool needsTexturePreservation = requiresFinalAssetReorder && touchesTextureWorld;
                bool needsFontTexturePreservation = needsTexturePreservation && HasFontTexturePayload(pfs);
                bool needsLocaleArtPayloadScan = requiresFinalAssetReorder && resourceTypesToProcess.Contains("Sprites", StringComparer.OrdinalIgnoreCase);

                var helperSnapshot = CapturePatchHelpers(
                    pfs,
                    includeAssetOrder: hasCodeEntries || requiresFinalAssetReorder,
                    includeObjectEvents: hasCodeEntries || requiresFinalAssetReorder,
                    includeTextureMappingHelpers: needsTexturePreservation);
                var helpersDir = helperSnapshot.HelpersDir;
                var capturedAssetOrderLines = helperSnapshot.AssetOrderLines;
                var capturedObjectEventsBytes = helperSnapshot.ObjectEventsBytes;
                var capturedTexturePageItemsBytes = helperSnapshot.TexturePageItemsBytes;
                var capturedSpriteFrameMapBytes = helperSnapshot.SpriteFrameMapBytes;
                string? capturedVariablesFunctionsContent = hasCodeEntries && pfs.FileExists(Path.Combine(helpersDir, "variables_functions.json"))
                    ? pfs.ReadAllText(Path.Combine(helpersDir, "variables_functions.json"))
                    : null;
                Dictionary<string, List<string>> assetNamesAtCodeImportStart = [];
                var originalGmlForCodeRemap = needsCodeRemapPreparation ? CaptureOriginalCodeGmlForAssetRemap(data, resourceTypesToProcess) : [];
                var patchCodeEntryNames = needsCodeRemapPreparation ? CapturePatchCodeEntryNames(pfs) : [];
                var untouchedEmbeddedTextureSnapshot = needsTexturePreservation ? CaptureUntouchedEmbeddedTextures(data, pfs) : [];
                string? stagedEmbeddedTexturesDir = needsTexturePreservation ? StageEmbeddedTexturesForAssetOrder(data, pfs) : null;
                bool hasFontTexturePayload = needsFontTexturePreservation;
                var localeArtSpritePayloads = needsLocaleArtPayloadScan ? CapturePatchedLocaleArtSpriteNames(pfs) : [];
                string? codeMetadataForFinalCleanup = capturedVariablesFunctionsContent;

                nonCodeImportSw.Start();
                foreach (var resourceType in resourceTypesToProcess)
                {
                    if (ApplyDeletedResources(data, manifest, resourceType) > 0)
                        appliedCount++;

                    if (resourceType == "CodeEntries")
                    {
                        var assetOrderFile = Path.Combine(helpersDir, "asset_order.txt");
                        if (pfs.FileExists(assetOrderFile))
                        {
                            var aoLines = capturedAssetOrderLines ?? pfs.ReadAllLines(assetOrderFile);
                            bool inObjects = false;
                            var orderNameCounts = new Dictionary<string, int>();
                            foreach (var aoLine in aoLines)
                            {
                                if (aoLine.StartsWith("@@"))
                                {
                                    inObjects = aoLine == "@@objects@@";
                                    continue;
                                }
                                if (!inObjects || string.IsNullOrWhiteSpace(aoLine)) continue;
                                string objName = aoLine.Trim();
                                if (objName == "(null)" || int.TryParse(objName, out _)) continue;
                                orderNameCounts[objName] = orderNameCounts.GetValueOrDefault(objName) + 1;
                            }

                            var existingCounts = new Dictionary<string, int>();
                            foreach (var obj in data.GameObjects)
                            {
                                if (obj?.Name?.Content != null)
                                    existingCounts[obj.Name.Content] = existingCounts.GetValueOrDefault(obj.Name.Content) + 1;
                            }

                            int createdObjects = 0;
                            foreach (var (objName, needed) in orderNameCounts)
                            {
                                int have = existingCounts.GetValueOrDefault(objName);
                                for (int ci = have; ci < needed; ci++)
                                {
                                    var newObj = new UndertaleGameObject
                                    {
                                        Name = data.Strings.MakeString(objName)
                                    };
                                    data.GameObjects.Add(newObj);
                                    createdObjects++;
                                }
                            }
                            if (createdObjects > 0)
                                LogService.Log($"[PatchService] Created {createdObjects} missing GameObjects from TARGET order");

                            LogService.Log("[PatchService] Reordering assets to match TARGET order...");
                            try
                            {
                                preCodeReorderSw.Start();
                                var reorderDir = Path.Combine(Path.GetTempPath(), $"g3mtool_aoreorder_{Guid.NewGuid():N}");
                                Directory.CreateDirectory(reorderDir);
                                var reorderKeepSections = BuildRelevantAssetOrderSections(resourceTypesToProcess, includeScriptsForCodeEntries: true);
                                reorderKeepSections.Add("counts");
                                var filteredAoLines = SelectAssetOrderSections(
                                    aoLines,
                                    reorderKeepSections,
                                    skipSections: ["TexturePageItems", "EmbeddedTextures"]);
                                File.WriteAllLines(Path.Combine(reorderDir, "asset_order.txt"), filteredAoLines);
                                if (capturedObjectEventsBytes != null)
                                    File.WriteAllBytes(Path.Combine(reorderDir, "object_events.json"), capturedObjectEventsBytes);

                                ResourceImportService.SetPatchFileSystem(null);
                                ResourceImportService.ImportAssetOrder(data, reorderDir);
                                ResourceImportService.SetPatchFileSystem(pfs);
                                appliedCount++;
                                try { Directory.Delete(reorderDir, true); } catch { }
                                preCodeReorderSw.Stop();
                            }
                            catch (Exception ex)
                            {
                                if (preCodeReorderSw.IsRunning) preCodeReorderSw.Stop();
                                LogService.Log($"[PatchService] Asset reorder warning: {ex.Message}");
                            }
                        }

                        if (needsCodeRemapPreparation)
                            assetNamesAtCodeImportStart = CaptureAssetNamesForCodeRemap(data);

                        nonCodeImportSw.Stop();
                        string? vfContent = null;
                        var vfPath = Path.Combine(helpersDir, "variables_functions.json");
                        if (pfs.FileExists(vfPath))
                            vfContent = capturedVariablesFunctionsContent ?? pfs.ReadAllText(vfPath);
                        codeMetadataForFinalCleanup ??= vfContent;

                        string? objectEventsContent = null;
                        var objectEventsPath = Path.Combine(helpersDir, "object_events.json");
                        if (pfs.FileExists(objectEventsPath))
                            objectEventsContent = pfs.ReadAllText(objectEventsPath);

                        string? deferredGlobalScriptsContent = null;
                        var globalScriptsPath = "GlobalScripts/global_scripts.json";
                        if (pfs.FileExists(globalScriptsPath))
                            deferredGlobalScriptsContent = pfs.ReadAllText(globalScriptsPath);
                        bool hasDeferredScripts = pfs.DirectoryExists("Scripts");

                        LogService.Log("[PatchService] Applying CodeEntries via hybrid import (in-memory data)...");
                        phaseSw.Restart();
                        codeImportSw.Start();

                        try
                        {
                            LogService.Log($"[PatchService] Using {pfs.GmlEntries.Count} GML + {pfs.AsmEntries.Count} ASM entries from PFS");
                            ImportCodeEntriesDirect(data, pfs.GmlEntries, pfs.AsmEntries, pfs.AsmEntryPaths, pfs.CodeEntryLogicalNames, helpersDir, vfContent, objectEventsContent);
                            if (hasDeferredScripts)
                            {
                                ResourceImportService.Import("Scripts", data, "Scripts");
                                LogService.Log("[PatchService] Re-applied Scripts after CodeEntries");
                            }
                            if (deferredGlobalScriptsContent != null)
                            {
                                ApplyDeferredGlobalScripts(data, deferredGlobalScriptsContent);
                                LogService.Log("[PatchService] Re-applied GlobalScripts after CodeEntries");
                            }
                            appliedCount++;
                        }
                        catch (Exception ex)
                        {
                            LogService.Warning($"ERROR applying CodeEntries: {ex.Message}");
                            failedCount++;
                        }

                        ResourceImportService.SetPatchFileSystem(null);
                        pfs.ReleaseFileData();
                        pfs.ReleaseCodeEntries();
                        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                        codeImportSw.Stop();
                        LogService.Progress(90, 100);
                        nonCodeImportSw.Start();
                        continue;
                    }

                    if (hasCodeEntries && resourceType == "Scripts")
                    {
                        LogService.Log("[PatchService] Deferring Scripts import until after CodeEntries...");
                        nonCodeProgress += resourceWeights.GetValueOrDefault(resourceType, 1);
                        LogService.Progress(nonCodeRangeStart + nonCodeRange * nonCodeProgress / Math.Max(totalNonCodeWeight, 1), 100);
                        continue;
                    }

                    var resourceDir = resourceType;
                    if (!pfs.DirectoryExists(resourceDir))
                    {
                        nonCodeProgress += resourceWeights.GetValueOrDefault(resourceType, 1);
                        LogService.Progress(nonCodeRangeStart + nonCodeRange * nonCodeProgress / Math.Max(totalNonCodeWeight, 1), 100);
                        continue;
                    }

                    LogService.Log($"[PatchService] Applying {resourceType}...");
                    phaseSw.Restart();

                    try
                    {
                        ResourceImportService.SetDataPaths(workingDataPath, workingDataPath);
                        if (resourceType == "Sprites")
                            ResourceImportService.SetProgressRange(6, 24);
                        if (!ResourceImportService.Import(resourceType, data, resourceDir))
                        {
                            LogService.Warning($"No native importer for {resourceType}, skipping");
                            failedCount++;
                        }
                        else
                        {
                            appliedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.Warning($"ERROR applying {resourceType}: {ex.Message}");
                        failedCount++;
                    }
                    finally
                    {
                        ResourceImportService.SetProgressRange();
                        ResourceImportService.SetDataPaths(null, null);
                    }

                    if (resourceType == "Sprites")
                    {
                        var reorderAssetOrderFile = Path.Combine(helpersDir, "asset_order.txt");
                        if (pfs.FileExists(reorderAssetOrderFile))
                        {
                            var reorderOnlyDir = Path.Combine(Path.GetTempPath(), $"g3mtool_reorder_{Guid.NewGuid():N}");
                            Directory.CreateDirectory(reorderOnlyDir);
                            var filteredLines = SelectAssetOrderSections(
                                pfs.ReadAllLines(reorderAssetOrderFile),
                                BuildRelevantAssetOrderSections(resourceTypesToProcess, includeScriptsForCodeEntries: false),
                                "TexturePageItems", "EmbeddedTextures");
                            File.WriteAllLines(Path.Combine(reorderOnlyDir, "asset_order.txt"), filteredLines);

                            LogService.Log("[PatchService] Re-ordering assets after sprite import...");
                            ResourceImportService.SetPatchFileSystem(null);
                            try
                            {
                                postSpriteReorderSw.Start();
                                ResourceImportService.ImportAssetOrder(data, reorderOnlyDir);
                                postSpriteReorderSw.Stop();
                            }
                            catch (Exception ex)
                            {
                                if (postSpriteReorderSw.IsRunning) postSpriteReorderSw.Stop();
                                LogService.Log($"[PatchService] Post-sprite reorder warning: {ex.Message}");
                            }
                            ResourceImportService.SetPatchFileSystem(pfs);
                            try { Directory.Delete(reorderOnlyDir, true); } catch { }
                        }
                    }

                    nonCodeProgress += resourceWeights.GetValueOrDefault(resourceType, 1);
                    LogService.Progress(nonCodeRangeStart + nonCodeRange * nonCodeProgress / Math.Max(totalNonCodeWeight, 1), 100);
                }
                nonCodeImportSw.Stop();

                finalizeSw.Start();
                if (capturedAssetOrderLines != null && requiresFinalAssetReorder)
                {
                    var finalReorderDir = Path.Combine(Path.GetTempPath(), $"g3mtool_final_reorder_{Guid.NewGuid():N}");
                    try
                    {
                        Directory.CreateDirectory(finalReorderDir);
                        var finalKeepSections = BuildRelevantAssetOrderSections(resourceTypesToProcess, includeScriptsForCodeEntries: true);
                        finalKeepSections.Add("counts");
                        var filteredLines = SelectAssetOrderSections(
                            capturedAssetOrderLines,
                            finalKeepSections,
                            skipSections: ["TexturePageItems", "EmbeddedTextures"]);
                        bool hasFinalTexturePayload =
                            capturedTexturePageItemsBytes != null ||
                            capturedSpriteFrameMapBytes != null ||
                            stagedEmbeddedTexturesDir != null;
                        if (!hasFinalTexturePayload && AssetOrderAlreadyMatches(data, filteredLines))
                        {
                            LogService.Log("[PatchService] Final asset order already matches target; skipping final reorder.");
                        }
                        else
                        {
                            File.WriteAllLines(Path.Combine(finalReorderDir, "asset_order.txt"), filteredLines);
                            if (capturedObjectEventsBytes != null)
                                File.WriteAllBytes(Path.Combine(finalReorderDir, "object_events.json"), capturedObjectEventsBytes);
                            if (capturedTexturePageItemsBytes != null)
                                File.WriteAllBytes(Path.Combine(finalReorderDir, "texture_page_items.json"), capturedTexturePageItemsBytes);
                            if (capturedSpriteFrameMapBytes != null)
                                File.WriteAllBytes(
                                    Path.Combine(finalReorderDir, "sprite_frame_map.json"),
                                    RemoveUnsafeRelinksFromSpriteFrameMap(capturedSpriteFrameMapBytes, localeArtSpritePayloads, removeFontRelinks: false));
                            if (stagedEmbeddedTexturesDir != null)
                                CopyStagedEmbeddedTexturesForAssetOrder(stagedEmbeddedTexturesDir, finalReorderDir);

                            LogService.Log("[PatchService] Enforcing final asset order after all imports...");
                            ResourceImportService.SetPatchFileSystem(null);
                            finalReorderSw.Start();
                            ResourceImportService.ImportAssetOrder(data, finalReorderDir);
                            finalReorderSw.Stop();
                            ResourceImportService.SetPatchFileSystem(pfs);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (finalReorderSw.IsRunning) finalReorderSw.Stop();
                        LogService.Log($"[PatchService] Final asset reorder warning: {ex.Message}");
                    }
                    finally
                    {
                        ResourceImportService.SetPatchFileSystem(pfs);
                        try { Directory.Delete(finalReorderDir, true); } catch { }
                        if (stagedEmbeddedTexturesDir != null)
                        {
                            try { Directory.Delete(stagedEmbeddedTexturesDir, true); } catch { }
                        }
                    }
                }

                RepairDetachedFontTexturePageItems(data);
                RestoreUntouchedEmbeddedTextures(data, untouchedEmbeddedTextureSnapshot);
                TrimUnreferencedTrailingEmbeddedTextures(data);

                repairDuplicatesSw.Start();
                if (RequiresDuplicateGameObjectRepair(resourceTypesToProcess, manifest))
                    RepairDuplicateGameObjectPlaceholders(data);
                repairDuplicatesSw.Stop();
                if (capturedObjectEventsBytes != null &&
                    ShouldApplyAuthoritativeObjectEvents(resourceTypesToProcess, manifest, capturedObjectEventsBytes))
                {
                    applyEventsSw.Start();
                    var objectEventCodeFilter = resourceTypesToProcess.Contains("GameObjects", StringComparer.OrdinalIgnoreCase)
                        ? null
                        : GetChangedCodeEntryNames(manifest);
                    ApplyAuthoritativeObjectEvents(data, Encoding.UTF8.GetString(capturedObjectEventsBytes), objectEventCodeFilter);
                    applyEventsSw.Stop();
                }
                PruneCodeEntriesOutsideTargetMetadata(
                    data,
                    codeMetadataForFinalCleanup ?? ReadVariablesFunctionsContent(pfs, helpersDir));
                PruneDeletedScriptOrphans(data, manifest);
                PruneMissingScriptCodeOrphans(data);

                if (capturedVariablesFunctionsContent != null && originalGmlForCodeRemap.Count > 0)
                {
                    RecompileUntouchedCodeForAssetOrderShifts(
                        data,
                        assetNamesAtCodeImportStart,
                        originalGmlForCodeRemap,
                        patchCodeEntryNames,
                        helpersDir,
                        capturedVariablesFunctionsContent);
                }

                DataIntegrityResult? integrity = null;
                if (requiresHeavyFinalize)
                {
                    repairDanglingCodeSw.Start();
                    RepairDanglingCodeReferences(data);
                    repairDanglingCodeSw.Stop();
                    if (RequiresScriptEntryEnsure(resourceTypesToProcess))
                    {
                        ensureScriptsSw.Start();
                        EnsureScriptEntriesForLiveScriptCode(data);
                        ensureScriptsSw.Stop();
                    }
                    integritySw.Start();
                    integrity = DataIntegrityService.RepairAndValidate(data);
                    integritySw.Stop();
                }
                else
                {
                    integritySw.Start();
                    integrity = DataIntegrityService.RepairAndValidate(data);
                    integritySw.Stop();
                }

                if (integrity is { Success: false })
                {
                    finalizeSw.Stop();
                    return new PatchApplyResult
                    {
                        Success = false,
                        Error = "Integrity validation failed: " + string.Join("; ", integrity.Errors.Take(20))
                    };
                }

                RepairLocalChildFunctionReferencesFinal(data);

                finalizeSw.Stop();

                if (failedCount > 0)
                {
                    return new PatchApplyResult
                    {
                        Success = false,
                        Error = $"Patch apply failed: {failedCount} resource type(s) failed during import; in-memory output was not committed"
                    };
                }

                totalSw.Stop();
                LogService.Log($"[Timing] === PATCH APPLY IN-MEMORY TOTAL: {totalSw.Elapsed.TotalSeconds:F1}s === RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
                LogService.Log(
                    $"[Timing] Apply in-memory breakdown: patchLoad={patchLoadSw.Elapsed.TotalSeconds:F2}s, " +
                    $"nonCode={nonCodeImportSw.Elapsed.TotalSeconds:F2}s, code={codeImportSw.Elapsed.TotalSeconds:F2}s, " +
                    $"finalize={finalizeSw.Elapsed.TotalSeconds:F2}s, reorderPreCode={preCodeReorderSw.Elapsed.TotalSeconds:F2}s, " +
                    $"reorderPostSprite={postSpriteReorderSw.Elapsed.TotalSeconds:F2}s, reorderFinal={finalReorderSw.Elapsed.TotalSeconds:F2}s, " +
                    $"repairDupes={repairDuplicatesSw.Elapsed.TotalSeconds:F2}s, applyEvents={applyEventsSw.Elapsed.TotalSeconds:F2}s, " +
                    $"repairDangling={repairDanglingCodeSw.Elapsed.TotalSeconds:F2}s, ensureScripts={ensureScriptsSw.Elapsed.TotalSeconds:F2}s, " +
                    $"integrity={integritySw.Elapsed.TotalSeconds:F2}s");
                return new PatchApplyResult { Success = true };
            }
            finally
            {
                ResourceImportService.SetPatchFileSystem(null);
            }
        }
        catch (Exception ex)
        {
            return new PatchApplyResult { Success = false, Error = $"Failed to apply patch in memory: {ex.Message}" };
        }
    }

    private static void RepairDuplicateGameObjectPlaceholders(UndertaleData data)
    {
        var byName = new Dictionary<string, List<UndertaleGameObject>>(StringComparer.Ordinal);
        foreach (var obj in data.GameObjects)
        {
            var name = obj?.Name?.Content;
            if (string.IsNullOrEmpty(name))
                continue;
            if (!byName.TryGetValue(name, out var list))
                byName[name] = list = [];
            list.Add(obj!);
        }

        int repaired = 0;
        foreach (var group in byName.Values)
        {
            if (group.Count < 2)
                continue;

            var source = group
                .Where(o => !IsGameObjectPlaceholder(o))
                .OrderByDescending(GameObjectCompletenessScore)
                .FirstOrDefault();
            if (source == null)
                continue;

            foreach (var target in group)
            {
                if (ReferenceEquals(source, target) || !IsGameObjectPlaceholder(target))
                    continue;

                CopyGameObjectProperties(source, target);
                repaired++;
            }
        }

        if (repaired > 0)
            LogService.Log($"[PatchService] Repaired {repaired} duplicate GameObject placeholder(s)");
    }

    private static bool IsGameObjectPlaceholder(UndertaleGameObject obj)
    {
        bool hasEvents = obj.Events.Any(e => e.Count > 0);
        return obj.Sprite == null &&
               obj.ParentId == null &&
               obj.TextureMaskId == null &&
               !obj.Solid &&
               obj.Depth == 0 &&
               !obj.Persistent &&
               !hasEvents;
    }

    private static int GameObjectCompletenessScore(UndertaleGameObject obj)
    {
        int score = 0;
        if (obj.Sprite != null) score += 4;
        if (obj.ParentId != null) score += 4;
        if (obj.TextureMaskId != null) score += 2;
        if (obj.Events.Any(e => e.Count > 0)) score += 4;
        if (obj.Solid) score++;
        if (obj.Depth != 0) score++;
        if (obj.Persistent) score++;
        if (obj.UsesPhysics) score++;
        return score;
    }

    private static void CopyGameObjectProperties(UndertaleGameObject source, UndertaleGameObject target)
    {
        target.Sprite = source.Sprite;
        target.Visible = source.Visible;
        target.Managed = source.Managed;
        target.Solid = source.Solid;
        target.Depth = source.Depth;
        target.Persistent = source.Persistent;
        target.ParentId = source.ParentId;
        target.TextureMaskId = source.TextureMaskId;
        target.UsesPhysics = source.UsesPhysics;
        target.IsSensor = source.IsSensor;
        target.CollisionShape = source.CollisionShape;
        target.Density = source.Density;
        target.Restitution = source.Restitution;
        target.Group = source.Group;
        target.LinearDamping = source.LinearDamping;
        target.AngularDamping = source.AngularDamping;
        target.Friction = source.Friction;
        target.Awake = source.Awake;
        target.Kinematic = source.Kinematic;

        for (int eventType = 0; eventType < Math.Min(source.Events.Count, target.Events.Count); eventType++)
        {
            target.Events[eventType].Clear();
            foreach (var sourceEvent in source.Events[eventType])
            {
                var targetEvent = new UndertaleGameObject.Event { EventSubtype = sourceEvent.EventSubtype };
                foreach (var sourceAction in sourceEvent.Actions)
                    targetEvent.Actions.Add(CloneEventAction(sourceAction));
                target.Events[eventType].Add(targetEvent);
            }
        }
    }

    private static UndertaleGameObject.EventAction CloneEventAction(UndertaleGameObject.EventAction source)
    {
        return new UndertaleGameObject.EventAction
        {
            LibID = source.LibID,
            ID = source.ID,
            Kind = source.Kind,
            UseRelative = source.UseRelative,
            IsQuestion = source.IsQuestion,
            UseApplyTo = source.UseApplyTo,
            ExeType = source.ExeType,
            ActionName = source.ActionName,
            CodeId = source.CodeId,
            ArgumentCount = source.ArgumentCount,
            Who = source.Who,
            Relative = source.Relative,
            IsNot = source.IsNot,
            UnknownAlwaysZero = source.UnknownAlwaysZero
        };
    }

    private static UndertaleGameObject.EventAction CreateEventActionFromJson(UndertaleData data, JsonElement actionElm)
    {
        var action = new UndertaleGameObject.EventAction();
        if (actionElm.TryGetProperty("libId", out var libIdElm))
            action.LibID = (uint)libIdElm.GetInt64();
        if (actionElm.TryGetProperty("id", out var idElm))
            action.ID = (uint)idElm.GetInt64();
        if (actionElm.TryGetProperty("kind", out var kindElm))
            action.Kind = (uint)kindElm.GetInt64();
        if (actionElm.TryGetProperty("useRelative", out var useRelativeElm))
            action.UseRelative = useRelativeElm.GetBoolean();
        if (actionElm.TryGetProperty("isQuestion", out var isQuestionElm))
            action.IsQuestion = isQuestionElm.GetBoolean();
        if (actionElm.TryGetProperty("useApplyTo", out var useApplyToElm))
            action.UseApplyTo = useApplyToElm.GetBoolean();
        if (actionElm.TryGetProperty("exeType", out var exeTypeElm))
            action.ExeType = (uint)exeTypeElm.GetInt64();
        if (actionElm.TryGetProperty("actionName", out var actionNameElm))
        {
            string? actionName = actionNameElm.GetString();
            action.ActionName = !string.IsNullOrEmpty(actionName) ? data.Strings.MakeString(actionName) : null;
        }
        if (actionElm.TryGetProperty("codeId", out var codeIdElm))
        {
            string? codeName = codeIdElm.GetString();
            if (!string.IsNullOrEmpty(codeName))
                action.CodeId = data.Code.ByName(codeName);
        }
        if (actionElm.TryGetProperty("argumentCount", out var argumentCountElm))
            action.ArgumentCount = (uint)argumentCountElm.GetInt64();
        if (actionElm.TryGetProperty("who", out var whoElm))
            action.Who = whoElm.GetInt32();
        if (actionElm.TryGetProperty("relative", out var relativeElm))
            action.Relative = relativeElm.GetBoolean();
        if (actionElm.TryGetProperty("isNot", out var isNotElm))
            action.IsNot = isNotElm.GetBoolean();
        return action;
    }

    private static void RepairDanglingCodeReferences(UndertaleData data)
    {
        var liveCodeEntries = new HashSet<UndertaleCode>();
        foreach (var code in data.Code)
        {
            if (code != null)
                liveCodeEntries.Add(code);
        }

        int repairedScripts = 0;
        int nulledScripts = 0;
        foreach (var script in data.Scripts)
        {
            if (script?.Code == null || liveCodeEntries.Contains(script.Code))
                continue;

            UndertaleCode? replacement = null;
            var currentCodeName = script.Code.Name?.Content;
            if (!string.IsNullOrEmpty(currentCodeName))
                replacement = ScriptCodeResolver.Resolve(data, script.Name?.Content, currentCodeName);

            if (replacement == null && script.Name?.Content is string scriptName && !string.IsNullOrEmpty(scriptName))
                replacement = ScriptCodeResolver.Resolve(data, scriptName);

            if (replacement != null)
            {
                script.Code = replacement;
                repairedScripts++;
            }
            else
            {
                script.Code = null;
                nulledScripts++;
            }
        }

        int removedGlobalInit = 0;
        for (int i = data.GlobalInitScripts.Count - 1; i >= 0; i--)
        {
            if (data.GlobalInitScripts[i]?.Code != null && !liveCodeEntries.Contains(data.GlobalInitScripts[i].Code))
            {
                data.GlobalInitScripts.RemoveAt(i);
                removedGlobalInit++;
            }
        }

        if (repairedScripts > 0 || nulledScripts > 0 || removedGlobalInit > 0)
        {
            LogService.Warning(
                $"[PatchService] Repaired dangling code references: {repairedScripts} relinked scripts, {nulledScripts} nulled scripts, {removedGlobalInit} removed global init entries"
            );
        }
    }

    private static void EnsureScriptEntriesForLiveScriptCode(UndertaleData data)
    {
        if (data.Scripts == null || data.Code == null)
            return;

        int relinked = 0;
        int created = 0;
        var scriptNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var script in data.Scripts)
        {
            if (script?.Name?.Content == null)
                continue;

            scriptNames.Add(script.Name.Content);
            if (script.Code != null)
                continue;

            script.Code = ScriptCodeResolver.Resolve(data, script.Name.Content);
            if (script.Code != null)
                relinked++;
        }

        foreach (var rootCode in data.Code)
        {
            foreach (var code in EnumerateCodeTreeRecursive(rootCode))
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
        }

        if (relinked > 0)
            LogService.Log($"[PatchService] Re-linked {relinked} Script entries to live code");
        if (created > 0)
            LogService.Log($"[PatchService] Created {created} missing Script entries for live gml_Script code");
    }

    internal static class ScriptCodeResolver
    {
        internal static UndertaleCode? Resolve(UndertaleData data, string? scriptName, string? explicitCodeName = null)
        {
            if (data.Code == null)
                return null;

            if (!string.IsNullOrWhiteSpace(explicitCodeName))
            {
                var byExplicit = FindCodeByName(data, explicitCodeName);
                if (byExplicit != null)
                    return byExplicit;
            }

            if (string.IsNullOrWhiteSpace(scriptName))
                return null;

            var byExact = FindCodeByName(data, scriptName);
            if (byExact != null)
                return byExact;

            bool alreadyScriptPrefixed = scriptName.StartsWith("gml_Script_", StringComparison.Ordinal);
            bool alreadyGlobalPrefixed = scriptName.StartsWith("gml_GlobalScript_", StringComparison.Ordinal);

            const string nestedGlobalScriptPrefix = "gml_Script_gml_GlobalScript_";
            if (scriptName.StartsWith(nestedGlobalScriptPrefix, StringComparison.Ordinal))
            {
                var byCanonicalScript = FindCodeByName(data, "gml_Script_" + scriptName[nestedGlobalScriptPrefix.Length..]);
                if (byCanonicalScript != null)
                    return byCanonicalScript;
            }

            if (!alreadyScriptPrefixed)
            {
                var byScriptPrefix = FindCodeByName(data, "gml_Script_" + scriptName);
                if (byScriptPrefix != null)
                    return byScriptPrefix;
            }

            if (!alreadyGlobalPrefixed)
            {
                var byGlobalPrefix = FindCodeByName(data, "gml_GlobalScript_" + scriptName);
                if (byGlobalPrefix != null)
                    return byGlobalPrefix;
            }

            if (alreadyScriptPrefixed)
                return FindCodeByName(data, "gml_GlobalScript_" + scriptName["gml_Script_".Length..]);

            if (alreadyGlobalPrefixed)
                return FindCodeByName(data, "gml_Script_" + scriptName["gml_GlobalScript_".Length..]);

            return null;
        }

        private static UndertaleCode? FindCodeByName(UndertaleData data, string? codeName)
        {
            if (string.IsNullOrWhiteSpace(codeName) || data.Code == null)
                return null;

            var topLevel = data.Code.ByName(codeName);
            if (topLevel != null)
                return topLevel;

            foreach (var code in data.Code)
            {
                var found = FindCodeByNameRecursive(code, codeName);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static UndertaleCode? FindCodeByNameRecursive(UndertaleCode? code, string codeName)
        {
            if (code?.Name?.Content == codeName)
                return code;

            if (code?.ChildEntries == null)
                return null;

            foreach (var child in code.ChildEntries)
            {
                var found = FindCodeByNameRecursive(child, codeName);
                if (found != null)
                    return found;
            }

            return null;
        }
    }

    private static string CanonicalizeFunctionName(string functionName)
    {
        const string nestedGlobalScriptPrefix = "gml_Script_gml_GlobalScript_";
        if (functionName.StartsWith(nestedGlobalScriptPrefix, StringComparison.Ordinal))
            return "gml_Script_" + functionName[nestedGlobalScriptPrefix.Length..];
        return functionName;
    }

    private static int ApplyDeletedResources(
        UndertaleData data,
        G3MPatchManifest? manifest,
        string resourceType)
    {
        if (manifest?.Resources == null ||
            !manifest.Resources.TryGetValue(resourceType, out var changes) ||
            changes.Deleted == null ||
            changes.Deleted.Count == 0)
        {
            return 0;
        }

        int handled = 0;
        foreach (var name in changes.Deleted)
        {
            if (RemoveResourceByName(data, resourceType, name))
                handled++;
        }

        if (handled > 0)
            LogService.Log($"[PatchService] Handled {handled} {resourceType} deletions from manifest");

        return handled;
    }

    private static bool RemoveResourceByName(UndertaleData data, string resourceType, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return resourceType switch
        {
            "Sprites" => RemoveReferencedOrNamed(data, data.Sprites, "Sprites", name, IsSpriteReferenced),
            "Backgrounds" => RemoveReferencedOrNamed(data, data.Backgrounds, "Backgrounds", name, IsBackgroundReferenced),
            "Fonts" => RemoveReferencedOrNamed(data, data.Fonts, "Fonts", name, IsFontReferenced),
            "Sounds" => RemoveReferencedOrNamed(data, data.Sounds, "Sounds", name, IsSoundReferenced),
            "Paths" => RemoveNamed(data.Paths, name),
            "Tilesets" => RemoveNamed(data.Backgrounds, name),
            "Shaders" => RemoveNamed(data.Shaders, name),
            "Timelines" => RemoveNamed(data.Timelines, name),
            "GameObjects" => RemoveReferencedOrNamed(data, data.GameObjects, "GameObjects", name, IsGameObjectReferenced),
            "Rooms" => RemoveNamed(data.Rooms, name),
            "TextureGroupInfo" => RemoveNamed(data.TextureGroupInfo, name),
            "Extensions" => RemoveExtension(data, name),
            "AudioGroups" => RemoveNamed(data.AudioGroups, name),
            "Scripts" => RemoveScriptOnlyWhenCodeIsGone(data, name),
            "EmbeddedImages" => data.EmbeddedImages != null && RemoveNamed(data.EmbeddedImages, name),
            "FilterEffects" => data.FilterEffects != null && RemoveNamed(data.FilterEffects, name),
            "AnimationCurves" => data.AnimationCurves != null && RemoveNamed(data.AnimationCurves, name),
            "ParticleSystemEmitters" => data.ParticleSystemEmitters != null && RemoveNamed(data.ParticleSystemEmitters, name),
            "ParticleSystems" => data.ParticleSystems != null && RemoveNamed(data.ParticleSystems, name),
            "Sequences" => data.Sequences != null && RemoveNamed(data.Sequences, name),
            "CodeEntries" => RemoveReferencedOrNamed(data, data.Code, "CodeEntries", name, IsCodeReferenced),
            _ => false
        };
    }

    private static bool RemoveReferencedOrNamed<T>(
        UndertaleData data,
        IList<T> list,
        string resourceType,
        string name,
        Func<UndertaleData, T, bool> isReferenced)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var item = list[i];
            var itemName = item?.GetType().GetProperty("Name")?.GetValue(item);
            var content = itemName?.GetType().GetProperty("Content")?.GetValue(itemName) as string;
            if (!string.Equals(content, name, StringComparison.Ordinal))
                continue;

            if (item != null && isReferenced(data, item))
            {
                LogService.Log($"[PatchService] Preserved {resourceType} deletion for {name}: resource is still referenced");
                return true;
            }

            list.RemoveAt(i);
            return true;
        }

        return false;
    }

    private static bool RemoveNamed<T>(IList<T> list, string name)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var item = list[i];
            var itemName = item?.GetType().GetProperty("Name")?.GetValue(item);
            var content = itemName?.GetType().GetProperty("Content")?.GetValue(itemName) as string;
            if (string.Equals(content, name, StringComparison.Ordinal))
            {
                list.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    private static bool RemoveExtension(UndertaleData data, string name)
    {
        for (int i = data.Extensions.Count - 1; i >= 0; i--)
        {
            var extension = data.Extensions[i];
            if (!string.Equals(extension?.Name?.Content, name, StringComparison.Ordinal))
                continue;

            data.Extensions.RemoveAt(i);
            var productIdData = data.FORM.EXTN?.productIdData;
            if (productIdData != null && i < productIdData.Count)
                productIdData.RemoveAt(i);
            return true;
        }

        return false;
    }

    private static bool RemoveScriptOnlyWhenCodeIsGone(UndertaleData data, string name)
    {
        for (int i = data.Scripts.Count - 1; i >= 0; i--)
        {
            var script = data.Scripts[i];
            if (!string.Equals(script?.Name?.Content, name, StringComparison.Ordinal))
                continue;

            var code = script!.Code;
            var codeName = code?.Name?.Content;
            if (!string.IsNullOrEmpty(codeName) && ScriptCodeResolver.Resolve(data, name, codeName) != null)
            {
                LogService.Log($"[PatchService] Preserved Script deletion for {name}: linked CodeEntry is still live");
                return true;
            }

            data.Scripts.RemoveAt(i);
            RemoveOrphanScriptFunction(data, codeName ?? name);
            return true;
        }

        RemoveOrphanScriptFunction(data, name);
        return false;
    }

    private static bool RemoveOrphanScriptFunction(UndertaleData data, string? codeName)
    {
        if (string.IsNullOrWhiteSpace(codeName) || data.Functions == null)
            return false;

        if (ScriptCodeResolver.Resolve(data, codeName, codeName) != null)
            return false;

        if (IsFunctionReferencedByCode(data, codeName))
            return false;

        for (int i = data.Functions.Count - 1; i >= 0; i--)
        {
            var function = data.Functions[i];
            if (!string.Equals(function?.Name?.Content, codeName, StringComparison.Ordinal))
                continue;

            data.Functions.RemoveAt(i);
            LogService.Log($"[PatchService] Removed orphan Function for deleted script code {codeName}");
            return true;
        }

        return false;
    }

    private static bool IsFunctionReferencedByCode(UndertaleData data, string functionName)
    {
        foreach (var code in data.Code)
        {
            foreach (var nested in EnumerateCodeTreeRecursive(code))
            {
                foreach (var instruction in nested.Instructions ?? [])
                {
                    if (string.Equals(instruction.ValueFunction?.Name?.Content, functionName, StringComparison.Ordinal))
                        return true;
                }
            }
        }

        return false;
    }

    private static void PruneMissingScriptCodeOrphans(UndertaleData data)
    {
        int removedScripts = 0;
        int removedFunctions = 0;
        for (int i = data.Scripts.Count - 1; i >= 0; i--)
        {
            var script = data.Scripts[i];
            var scriptName = script?.Name?.Content;
            if (string.IsNullOrWhiteSpace(scriptName))
                continue;

            var liveCode = ScriptCodeResolver.Resolve(data, scriptName, script?.Code?.Name?.Content);
            if (liveCode != null)
            {
                if (!ReferenceEquals(script!.Code, liveCode))
                    script.Code = liveCode;
                continue;
            }

            data.Scripts.RemoveAt(i);
            removedScripts++;
            if (RemoveOrphanScriptFunction(data, script?.Code?.Name?.Content ?? scriptName))
                removedFunctions++;
        }

        if (removedScripts > 0 || removedFunctions > 0)
            LogService.Log($"[PatchService] Pruned missing script-code orphans: {removedScripts} scripts, {removedFunctions} functions");
    }

    private static void PruneDeletedScriptOrphans(UndertaleData data, G3MPatchManifest? manifest)
    {
        if (manifest?.Resources == null ||
            !manifest.Resources.TryGetValue("Scripts", out var changes) ||
            changes.Deleted == null ||
            changes.Deleted.Count == 0)
        {
            return;
        }

        int removedScripts = 0;
        int removedFunctions = 0;
        foreach (var deletedScriptName in changes.Deleted)
        {
            if (string.IsNullOrWhiteSpace(deletedScriptName))
                continue;

            var liveCode = ScriptCodeResolver.Resolve(data, deletedScriptName, deletedScriptName);
            if (liveCode != null)
                continue;

            for (int i = data.Scripts.Count - 1; i >= 0; i--)
            {
                var script = data.Scripts[i];
                if (string.Equals(script?.Name?.Content, deletedScriptName, StringComparison.Ordinal) ||
                    string.Equals(script?.Code?.Name?.Content, deletedScriptName, StringComparison.Ordinal))
                {
                    data.Scripts.RemoveAt(i);
                    removedScripts++;
                }
            }

            if (RemoveOrphanScriptFunction(data, deletedScriptName))
                removedFunctions++;
        }

        if (removedScripts > 0 || removedFunctions > 0)
            LogService.Log($"[PatchService] Pruned deleted script orphans: {removedScripts} scripts, {removedFunctions} functions");
    }

    private static bool IsCodeReferenced(UndertaleData data, UndertaleCode code)
    {
        foreach (var script in data.Scripts)
            if (ReferenceEquals(script?.Code, code))
                return true;
        foreach (var global in data.GlobalInitScripts ?? [])
            if (ReferenceEquals(global?.Code, code))
                return true;
        foreach (var global in data.GameEndScripts ?? [])
            if (ReferenceEquals(global?.Code, code))
                return true;
        foreach (var obj in data.GameObjects)
            foreach (var evtList in obj?.Events ?? [])
                foreach (var evt in evtList ?? [])
                    foreach (var action in evt.Actions ?? [])
                        if (ReferenceEquals(action?.CodeId, code))
                            return true;
        foreach (var room in data.Rooms)
        {
            if (ReferenceEquals(room?.CreationCodeId, code))
                return true;
            foreach (var inst in room?.GameObjects ?? [])
                if (ReferenceEquals(inst?.CreationCode, code) || ReferenceEquals(inst?.PreCreateCode, code))
                    return true;
            foreach (var layer in room?.Layers ?? [])
                foreach (var inst in layer?.InstancesData?.Instances ?? [])
                    if (ReferenceEquals(inst?.CreationCode, code) || ReferenceEquals(inst?.PreCreateCode, code))
                        return true;
        }
        return false;
    }

    private static bool IsGameObjectReferenced(UndertaleData data, UndertaleGameObject gameObject)
    {
        foreach (var obj in data.GameObjects)
        {
            if (ReferenceEquals(obj?.ParentId, gameObject))
                return true;
            for (int eventType = 0; obj != null && eventType < obj.Events.Count; eventType++)
            {
                if (eventType != (int)EventType.Collision)
                    continue;
                foreach (var evt in obj.Events[eventType])
                    if (evt.EventSubtype < data.GameObjects.Count && ReferenceEquals(data.GameObjects[(int)evt.EventSubtype], gameObject))
                        return true;
            }
        }
        foreach (var room in data.Rooms)
        {
            foreach (var view in room?.Views ?? [])
                if (ReferenceEquals(view?.ObjectId, gameObject))
                    return true;
            foreach (var inst in room?.GameObjects ?? [])
                if (ReferenceEquals(inst?.ObjectDefinition, gameObject))
                    return true;
            foreach (var layer in room?.Layers ?? [])
                foreach (var inst in layer?.InstancesData?.Instances ?? [])
                    if (ReferenceEquals(inst?.ObjectDefinition, gameObject))
                        return true;
        }
        return false;
    }

    private static bool IsSpriteReferenced(UndertaleData data, UndertaleSprite sprite)
    {
        foreach (var obj in data.GameObjects)
            if (ReferenceEquals(obj?.Sprite, sprite) || ReferenceEquals(obj?.TextureMaskId, sprite))
                return true;
        foreach (var background in data.Backgrounds)
            if (ReferenceEquals(background?.GMS2ExportedSprite, sprite))
                return true;
        foreach (var emitter in data.ParticleSystemEmitters ?? [])
            if (ReferenceEquals(emitter?.Sprite, sprite))
                return true;
        foreach (var room in data.Rooms)
        {
            foreach (var tile in room?.Tiles ?? [])
                if (tile?.spriteMode == true && ReferenceEquals(tile.SpriteDefinition, sprite))
                    return true;
            foreach (var layer in room?.Layers ?? [])
            {
                if (ReferenceEquals(layer?.BackgroundData?.Sprite, sprite))
                    return true;
                foreach (var inst in layer?.AssetsData?.Sprites ?? [])
                    if (ReferenceEquals(inst?.Sprite, sprite))
                        return true;
                foreach (var tile in layer?.AssetsData?.LegacyTiles ?? [])
                    if (tile?.spriteMode == true && ReferenceEquals(tile.SpriteDefinition, sprite))
                        return true;
            }
        }
        return false;
    }

    private static bool IsBackgroundReferenced(UndertaleData data, UndertaleBackground background)
    {
        foreach (var room in data.Rooms)
        {
            foreach (var bg in room?.Backgrounds ?? [])
                if (ReferenceEquals(bg?.BackgroundDefinition, background))
                    return true;
            foreach (var tile in room?.Tiles ?? [])
                if (tile?.spriteMode == false && ReferenceEquals(tile.BackgroundDefinition, background))
                    return true;
            foreach (var layer in room?.Layers ?? [])
                if (ReferenceEquals(layer?.TilesData?.Background, background))
                    return true;
        }
        return false;
    }

    private static bool IsFontReferenced(UndertaleData data, UndertaleFont font)
    {
        foreach (var room in data.Rooms)
            foreach (var layer in room?.Layers ?? [])
                foreach (var text in layer?.AssetsData?.TextItems ?? [])
                    if (ReferenceEquals(text?.Font, font))
                        return true;
        return false;
    }

    private static bool IsSoundReferenced(UndertaleData data, UndertaleSound sound)
    {
        foreach (var sequence in data.Sequences ?? [])
        {
            if (SequenceReferencesSound(sequence, sound))
                return true;
        }
        return false;
    }

    private static bool SequenceReferencesSound(UndertaleSequence? sequence, UndertaleSound sound)
    {
        if (sequence == null)
            return false;
        // Sequence internals vary by GameMaker version; use reflection here to avoid
        // hard-coding one layout and missing another.
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return ObjectGraphReferences(sequence, sound, seen, depth: 0);
    }

    private static bool ObjectGraphReferences(object? node, object target, HashSet<object> seen, int depth)
    {
        if (node == null || depth > 8)
            return false;
        if (ReferenceEquals(node, target))
            return true;
        if (node is string || node.GetType().IsPrimitive || node.GetType().IsEnum)
            return false;
        if (!seen.Add(node))
            return false;
        if (node is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                if (ObjectGraphReferences(item, target, seen, depth + 1))
                    return true;
            return false;
        }
        foreach (var prop in node.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length != 0)
                continue;
            object? value;
            try { value = prop.GetValue(node); }
            catch { continue; }
            if (ObjectGraphReferences(value, target, seen, depth + 1))
                return true;
        }
        return false;
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private static async Task<bool> TryApplyXdeltaFallbackAsync(
        string dataPath,
        string outputPath,
        PatchFileSystem pfs,
        G3MPatchManifest? manifest)
    {
        if (pfs.ExactPatchBytes == null)
            return false;

        string currentHash = await HashService.ComputeFileHashAsync(dataPath);
        string? expectedOriginalHash = manifest?.Original?.Md5;
        if (!string.IsNullOrEmpty(expectedOriginalHash) &&
            !currentHash.Equals(expectedOriginalHash, StringComparison.OrdinalIgnoreCase))
        {
            LogService.Warning(
                "[PatchService] Xdelta fallback skipped because the original MD5 does not match"
            );
            return false;
        }

        string extension = Path.GetExtension(pfs.ExactPatchPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".xdelta";

        string tempExactPatch = Path.Combine(
            Path.GetTempPath(), $"g3mtool_exact_apply_{Guid.NewGuid():N}{extension}"
        );

        try
        {
            await File.WriteAllBytesAsync(tempExactPatch, pfs.ExactPatchBytes);
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            var xdeltaService = new XDeltaService();
            var exactResult = await xdeltaService.ApplyPatchAsync(dataPath, tempExactPatch, outputPath);
            if (!exactResult.Success)
            {
                LogService.Warning(
                    $"[PatchService] Xdelta fallback failed: {exactResult.Error}"
                );
                return false;
            }

            string? expectedModifiedHash = manifest?.Modified?.Md5;
            if (!string.IsNullOrEmpty(expectedModifiedHash))
            {
                string actualHash = await HashService.ComputeFileHashAsync(outputPath);
                if (!actualHash.Equals(expectedModifiedHash, StringComparison.OrdinalIgnoreCase))
                {
                    LogService.Warning(
                        "[PatchService] Xdelta fallback output hash mismatch"
                    );
                    try { File.Delete(outputPath); } catch { }
                    return false;
                }
            }

            LogService.Log("[PatchService] Patch applied via xdelta fallback");
            return true;
        }
        finally
        {
            try { File.Delete(tempExactPatch); } catch { }
        }
    }

    private static async Task<bool> TryApplyXdeltaFallbackFromPatchAsync(
        string dataPath,
        string outputPath,
        string patchPath,
        PatchFileSystem pfs,
        G3MPatchManifest? manifest)
    {
        if (pfs.ExactPatchBytes == null)
        {
            try
            {
                pfs = await Task.Run(() => PatchFileSystem.LoadFromZip(patchPath, loadExactPayloads: true, skipAsmBackedGml: true));
                manifest ??= pfs.Manifest;
            }
            catch (Exception ex)
            {
                LogService.Warning($"[PatchService] Xdelta fallback could not be loaded: {ex.Message}");
                return false;
            }
        }

        return await TryApplyXdeltaFallbackAsync(dataPath, outputPath, pfs, manifest);
    }

    private static void ApplyDeferredGlobalScripts(UndertaleData data, string globalScriptsJson)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"g3mtool_globals_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "global_scripts.json"), globalScriptsJson);
            ResourceImportService.SetPatchFileSystem(null);
            ResourceImportService.Import("GlobalScripts", data, tempDir);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static IEnumerable<UndertaleCode> EnumerateCodeTreeRecursive(UndertaleCode? code)
    {
        if (code == null)
            yield break;

        yield return code;

        if (code.ChildEntries == null)
            yield break;

        foreach (var child in code.ChildEntries)
        {
            foreach (var nested in EnumerateCodeTreeRecursive(child))
                yield return nested;
        }
    }

    private static Dictionary<string, UndertaleCode> BuildArchiveCodeLookup(UndertaleData data)
    {
        var lookup = new Dictionary<string, UndertaleCode>(StringComparer.Ordinal);
        var topLevelByName = new Dictionary<string, List<UndertaleCode>>(StringComparer.Ordinal);
        foreach (var code in data.Code)
        {
            var name = code?.Name?.Content;
            if (code == null || code.ParentEntry != null || string.IsNullOrEmpty(name))
                continue;

            if (!topLevelByName.TryGetValue(name, out var list))
            {
                list = [];
                topLevelByName[name] = list;
            }
            list.Add(code);
        }

        var childByParentAndName = new Dictionary<UndertaleCode, Dictionary<string, List<UndertaleCode>>>();
        foreach (var descriptor in CodeEntryArchiveIdentity.DescribeEntries(data))
        {
            UndertaleCode? code = null;

            if (descriptor.ParentArchiveKey != null)
            {
                if (!lookup.TryGetValue(descriptor.ParentArchiveKey, out var parent))
                    continue;

                if (!childByParentAndName.TryGetValue(parent, out var childrenByName))
                {
                    childrenByName = new Dictionary<string, List<UndertaleCode>>(StringComparer.Ordinal);
                    foreach (var child in parent.ChildEntries)
                    {
                        var childName = child?.Name?.Content;
                        if (child == null || string.IsNullOrEmpty(childName))
                            continue;

                        if (!childrenByName.TryGetValue(childName, out var children))
                        {
                            children = [];
                            childrenByName[childName] = children;
                        }
                        children.Add(child);
                    }
                    childByParentAndName[parent] = childrenByName;
                }

                if (childrenByName.TryGetValue(descriptor.LogicalName, out var matches) &&
                    descriptor.Occurrence > 0 &&
                    descriptor.Occurrence <= matches.Count)
                    code = matches[descriptor.Occurrence - 1];
            }
            else
            {
                if (topLevelByName.TryGetValue(descriptor.LogicalName, out var matches) &&
                    descriptor.Occurrence > 0 &&
                    descriptor.Occurrence <= matches.Count)
                    code = matches[descriptor.Occurrence - 1];
            }

            if (code != null)
                lookup[descriptor.ArchiveKey] = code;
        }

        return lookup;
    }

    private static readonly string[] s_codeTopologyWatchKeys =
    [
        "gml_GlobalScript_scr_flag_get/gml_Script_scr_flag_get",
        "gml_GlobalScript_scr_flag_get/gml_Script_scr_flag_name_get",
        "gml_GlobalScript_scr_flag_get/gml_Script_scr_getflag",
        "gml_GlobalScript_scr_rhythmgame_init/gml_Script_scr_rhythmgame_init",
        "gml_Object_obj_watercooler_enemy_Step_0"
    ];

    private static void LogWatchedArchiveCodeState(string phase, Dictionary<string, UndertaleCode> archiveCodeLookup)
    {
        if (!LogService.Verbose)
            return;

        try
        {
            var lines = new List<string>(s_codeTopologyWatchKeys.Length);
            foreach (var key in s_codeTopologyWatchKeys)
            {
                if (!archiveCodeLookup.TryGetValue(key, out var code) || code == null)
                {
                    lines.Add($"{key} => <missing>");
                    continue;
                }

                var parent = code.ParentEntry?.Name?.Content ?? "<top>";
                lines.Add($"{key} => {code.Name?.Content ?? "<null>"} | parent={parent} | children={code.ChildEntries?.Count ?? 0}");
            }

            LogService.Log($"[ImportCodeEntries][Watch:{phase}] {string.Join("; ", lines)}");
        }
        catch
        {
        }
    }

    private static Dictionary<string, (string LogicalName, int Occurrence, string? ParentArchiveKey)> ParseTargetCodeEntryMetadata(
        JsonElement root,
        Dictionary<string, string>? fallbackLogicalNames)
    {
        var map = new Dictionary<string, (string LogicalName, int Occurrence, string? ParentArchiveKey)>(StringComparer.Ordinal);
        if (!root.TryGetProperty("codeEntries", out var ceArray) || ceArray.ValueKind != JsonValueKind.Array)
            return map;

        foreach (var ce in ceArray.EnumerateArray())
        {
            if (ce.ValueKind == JsonValueKind.Object)
            {
                string? key = ce.TryGetProperty("key", out var keyElem) ? keyElem.GetString() : null;
                string? name = ce.TryGetProperty("name", out var nameElem) ? nameElem.GetString() : null;
                int occurrence = ce.TryGetProperty("occurrence", out var occElem) ? occElem.GetInt32() : 1;
                string? parentArchiveKey = ce.TryGetProperty("parent", out var parentElem) ? parentElem.GetString() : null;
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(name))
                    map[key] = (name, occurrence, parentArchiveKey);
            }
            else if (ce.ValueKind == JsonValueKind.String)
            {
                string? name = ce.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    string key = fallbackLogicalNames?.FirstOrDefault(x => x.Value == name).Key ?? name;
                    int occurrence = map.Values.Count(x => x.LogicalName == name) + 1;
                    map[key] = (name, occurrence, null);
                }
            }
        }

        return map;
    }

    private static void ReorderCodeEntriesFromTargetMetadata(
        UndertaleData data,
        Dictionary<string, (string LogicalName, int Occurrence, string? ParentArchiveKey)> targetCodeEntriesByKey)
    {
        if (targetCodeEntriesByKey.Count == 0)
            return;

        var archiveLookup = BuildArchiveCodeLookup(data);
        var used = new HashSet<UndertaleCode>(ReferenceEqualityComparer.Instance);
        var reordered = new List<UndertaleCode>(data.Code.Count);

        foreach (var entryKey in targetCodeEntriesByKey.Keys)
        {
            if (archiveLookup.TryGetValue(entryKey, out var code) && code != null && used.Add(code))
                reordered.Add(code);
        }

        foreach (var code in data.Code)
        {
            if (code != null && used.Add(code))
                reordered.Add(code);
        }

        if (reordered.Count != data.Code.Count)
            return;

        data.Code.Clear();
        foreach (var code in reordered)
            data.Code.Add(code);
    }

    private static void PruneCodeEntriesOutsideTargetMetadata(
        UndertaleData data,
        Dictionary<string, (string LogicalName, int Occurrence, string? ParentArchiveKey)> targetCodeEntriesByKey)
    {
        if (targetCodeEntriesByKey.Count == 0)
            return;

        var archiveLookup = BuildArchiveCodeLookup(data);
        var seen = new HashSet<UndertaleCode>(ReferenceEqualityComparer.Instance);
        var toRemove = new List<UndertaleCode>();
        foreach (var (entryKey, code) in archiveLookup)
        {
            if (!targetCodeEntriesByKey.ContainsKey(entryKey) && seen.Add(code))
                toRemove.Add(code);
        }

        if (toRemove.Count == 0)
            return;

        var removeSet = new HashSet<UndertaleCode>(toRemove, ReferenceEqualityComparer.Instance);
        foreach (var code in toRemove)
        {
            code.ParentEntry?.ChildEntries.Remove(code);
            code.ParentEntry = null;
            for (int i = code.ChildEntries.Count - 1; i >= 0; i--)
            {
                if (removeSet.Contains(code.ChildEntries[i]))
                    code.ChildEntries.RemoveAt(i);
            }

            for (int i = data.Scripts.Count - 1; i >= 0; i--)
            {
                var script = data.Scripts[i];
                if (ReferenceEquals(script?.Code, code) ||
                    string.Equals(script?.Name?.Content, code.Name?.Content, StringComparison.Ordinal))
                {
                    data.Scripts.RemoveAt(i);
                }
            }

            for (int i = data.CodeLocals.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(data.CodeLocals[i].Name, code.Name) ||
                    string.Equals(data.CodeLocals[i].Name?.Content, code.Name?.Content, StringComparison.Ordinal))
                {
                    data.CodeLocals.RemoveAt(i);
                }
            }

            data.Code.Remove(code);
        }

        LogService.Log($"[ImportCodeEntries] Pruned {toRemove.Count} code entrie(s) outside target metadata");
    }

    private static void PruneCodeEntriesOutsideTargetMetadata(UndertaleData data, string? variablesFunctionsJson)
    {
        if (string.IsNullOrWhiteSpace(variablesFunctionsJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(variablesFunctionsJson);
            var targetCodeEntriesByKey = ParseTargetCodeEntryMetadata(doc.RootElement, fallbackLogicalNames: null);
            PruneCodeEntriesOutsideTargetMetadata(data, targetCodeEntriesByKey);
            PruneExcessCodeAndScriptsByTargetNames(data, targetCodeEntriesByKey.Values.Select(x => x.LogicalName));
        }
        catch (Exception ex)
        {
            LogService.Log($"[PatchService] Code metadata cleanup warning: {ex.Message}");
        }
    }

    private static void PruneExcessCodeAndScriptsByTargetNames(UndertaleData data, IEnumerable<string> targetCodeNames)
    {
        var remainingByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in targetCodeNames)
        {
            if (string.IsNullOrEmpty(name))
                continue;
            remainingByName[name] = remainingByName.GetValueOrDefault(name) + 1;
        }

        if (remainingByName.Count == 0)
            return;

        var removeCodes = new HashSet<UndertaleCode>(ReferenceEqualityComparer.Instance);
        for (int i = data.Code.Count - 1; i >= 0; i--)
        {
            var code = data.Code[i];
            var name = code?.Name?.Content;
            if (code == null || string.IsNullOrEmpty(name))
                continue;

            int remaining = remainingByName.GetValueOrDefault(name);
            if (remaining > 0)
            {
                remainingByName[name] = remaining - 1;
                continue;
            }

            removeCodes.Add(code);
            data.Code.RemoveAt(i);
        }

        int removedScripts = 0;
        if (removeCodes.Count > 0)
        {
            for (int i = data.Scripts.Count - 1; i >= 0; i--)
            {
                var script = data.Scripts[i];
                var code = script?.Code;
                var scriptName = script?.Name?.Content;
                if ((code != null && removeCodes.Contains(code)) ||
                    (!string.IsNullOrEmpty(scriptName) && removeCodes.Any(c => string.Equals(c.Name?.Content, scriptName, StringComparison.Ordinal))))
                {
                    data.Scripts.RemoveAt(i);
                    removedScripts++;
                }
            }

            foreach (var code in removeCodes)
            {
                code.ParentEntry?.ChildEntries.Remove(code);
                code.ParentEntry = null;
            }

            LogService.Log($"[PatchService] Pruned excess code/script entries: code={removeCodes.Count}, scripts={removedScripts}");
        }
    }

    private static string? ReadVariablesFunctionsContent(PatchFileSystem pfs, string helpersDir)
    {
        var path = Path.Combine(helpersDir, "variables_functions.json");
        return pfs.FileExists(path) ? pfs.ReadAllText(path) : null;
    }

    private static Dictionary<string, List<string>> CaptureAssetNamesForCodeRemap(UndertaleData data) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sprites"] = [.. data.Sprites.Select(s => s?.Name?.Content ?? "")],
            ["objects"] = [.. data.GameObjects.Select(o => o?.Name?.Content ?? "")],
            ["sounds"] = [.. data.Sounds.Select(s => s?.Name?.Content ?? "")],
            ["backgrounds"] = [.. data.Backgrounds.Select(b => b?.Name?.Content ?? "")],
            ["paths"] = [.. data.Paths.Select(p => p?.Name?.Content ?? "")],
            ["scripts"] = [.. data.Scripts.Select(s => s?.Name?.Content ?? "")],
            ["fonts"] = [.. data.Fonts.Select(f => f?.Name?.Content ?? "")],
            ["rooms"] = [.. data.Rooms.Select(r => r?.Name?.Content ?? "")],
            ["timelines"] = [.. data.Timelines.Select(t => t?.Name?.Content ?? "")],
            ["shaders"] = [.. data.Shaders.Select(s => s?.Name?.Content ?? "")],
            ["audiogroups"] = [.. data.AudioGroups.Select(a => a?.Name?.Content ?? "")]
        };

    private static bool HasFontTexturePayload(PatchFileSystem pfs) =>
        pfs.GetAllFilePaths().Any(path =>
            path.StartsWith("Fonts/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/texture.png", StringComparison.OrdinalIgnoreCase));

    private static Dictionary<int, byte[]> CaptureUntouchedEmbeddedTextures(UndertaleData data, PatchFileSystem pfs)
    {
        var touched = GetTouchedEmbeddedTextureIndices(pfs);
        var result = new Dictionary<int, byte[]>();
        bool gm2022_5 = data.IsVersionAtLeast(2022, 5);
        for (int i = 0; i < data.EmbeddedTextures.Count; i++)
        {
            if (touched.Contains(i))
                continue;

            var image = data.EmbeddedTextures[i]?.TextureData?.Image;
            if (image == null)
                continue;

            try
            {
                result[i] = image.ToSpan(gm2022_5).ToArray();
            }
            catch
            {
            }
        }
        return result;
    }

    private static HashSet<int> GetTouchedEmbeddedTextureIndices(PatchFileSystem pfs)
    {
        var result = new HashSet<int>();
        foreach (var path in pfs.GetAllFilePaths())
        {
            if (!path.StartsWith("EmbeddedTextures/", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith($"{pfs.HelpersPrefix}/EmbeddedTextures/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("texture_", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(parts[i]["texture_".Length..], out int index))
                {
                    result.Add(index);
                    break;
                }
            }
        }
        return result;
    }

    private static void RestoreUntouchedEmbeddedTextures(UndertaleData data, Dictionary<int, byte[]> snapshot)
    {
        int restored = 0;
        bool gm2022_5 = data.IsVersionAtLeast(2022, 5);
        foreach (var (index, imageBytes) in snapshot)
        {
            if (index < 0 || index >= data.EmbeddedTextures.Count || data.EmbeddedTextures[index]?.TextureData == null)
                continue;

            try
            {
                using var stream = new MemoryStream(imageBytes, writable: false);
                using var reader = new FileBinaryReader(stream);
                data.EmbeddedTextures[index].TextureData.Image = GMImage.FromBinaryReader(
                    reader,
                    stream.Length,
                    gm2022_5);
                restored++;
            }
            catch
            {
            }
        }

        if (restored > 0)
            LogService.Log($"[PatchService] Restored {restored} untouched EmbeddedTextures");
    }

    private static void TrimUnreferencedTrailingEmbeddedTextures(UndertaleData data)
    {
        var referenced = new HashSet<UndertaleEmbeddedTexture>(ReferenceEqualityComparer.Instance);
        foreach (var tpi in data.TexturePageItems)
        {
            if (tpi?.TexturePage != null)
                referenced.Add(tpi.TexturePage);
        }

        int oldCount = data.EmbeddedTextures.Count;
        while (data.EmbeddedTextures.Count > 0)
        {
            var texture = data.EmbeddedTextures[^1];
            if (texture != null && referenced.Contains(texture))
                break;
            data.EmbeddedTextures.RemoveAt(data.EmbeddedTextures.Count - 1);
        }

        if (data.EmbeddedTextures.Count != oldCount)
            LogService.Log($"[PatchService] Trimmed unreferenced trailing EmbeddedTextures: {oldCount} -> {data.EmbeddedTextures.Count}");
    }

    private static byte[] RemoveUnsafeRelinksFromSpriteFrameMap(
        byte[] spriteFrameMapBytes,
        HashSet<string> localeArtSpritePayloads,
        bool removeFontRelinks)
    {
        try
        {
            using var doc = JsonDocument.Parse(spriteFrameMapBytes);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (removeFontRelinks && prop.NameEquals("fonts"))
                        continue;

                    if (prop.NameEquals("sprites"))
                    {
                        writer.WritePropertyName(prop.Name);
                        writer.WriteStartObject();
                        foreach (var spriteProp in prop.Value.EnumerateObject())
                        {
                            spriteProp.WriteTo(writer);
                        }
                        writer.WriteEndObject();
                        continue;
                    }

                    prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return stream.ToArray();
        }
        catch
        {
            return spriteFrameMapBytes;
        }
    }

    private static HashSet<string> CapturePatchedLocaleArtSpriteNames(PatchFileSystem pfs)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string prefix = "Sprites/";
        foreach (var path in pfs.GetAllFilePaths())
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                continue;

            string spriteName = parts[1];
            if (IsLocaleArtSpriteName(spriteName))
                result.Add(spriteName);
        }
        return result;
    }

    private static bool IsLocaleArtSpriteName(string spriteName) =>
        spriteName.StartsWith("spr_ja_", StringComparison.OrdinalIgnoreCase) ||
        spriteName.StartsWith("bg_lang_ja_", StringComparison.OrdinalIgnoreCase);

    private static void RepairDetachedFontTexturePageItems(UndertaleData data)
    {
        int repairedTpi = 0;
        int repairedTextures = 0;
        var tpiSet = new HashSet<UndertaleTexturePageItem>(data.TexturePageItems);
        var textureSet = new HashSet<UndertaleEmbeddedTexture>(data.EmbeddedTextures);

        foreach (var font in data.Fonts)
        {
            var tpi = font?.Texture;
            if (tpi == null)
                continue;

            if (!tpiSet.Contains(tpi))
            {
                data.TexturePageItems.Add(tpi);
                tpiSet.Add(tpi);
                repairedTpi++;
            }

            if (tpi.TexturePage != null && !textureSet.Contains(tpi.TexturePage))
            {
                data.EmbeddedTextures.Add(tpi.TexturePage);
                textureSet.Add(tpi.TexturePage);
                repairedTextures++;
            }
        }

        if (repairedTpi > 0 || repairedTextures > 0)
            LogService.Log($"[PatchService] Re-attached {repairedTpi} font TexturePageItems and {repairedTextures} font EmbeddedTextures");
    }

    private static void RepairLocalChildFunctionReferencesFinal(UndertaleData data)
    {
        if (data.Code == null || data.Functions == null)
            return;

        var codeByName = new Dictionary<string, UndertaleCode>(StringComparer.Ordinal);
        foreach (var code in data.Code)
        {
            var name = code?.Name?.Content;
            if (!string.IsNullOrEmpty(name) && code != null)
                codeByName[name] = code;
        }

        var functionByName = new Dictionary<string, UndertaleFunction>(StringComparer.Ordinal);
        foreach (var function in data.Functions)
        {
            var name = function?.Name?.Content;
            if (!string.IsNullOrEmpty(name) && function != null && !functionByName.ContainsKey(name))
                functionByName[name] = function;
        }

        var localMaps = new Dictionary<UndertaleCode, Dictionary<string, UndertaleFunction?>>();
        Dictionary<string, UndertaleFunction?> GetLocalMap(UndertaleCode owner)
        {
            if (localMaps.TryGetValue(owner, out var cached))
                return cached;

            var map = new Dictionary<string, UndertaleFunction?>(StringComparer.Ordinal);
            foreach (var child in owner.ChildEntries ?? [])
            {
                var childName = child?.Name?.Content;
                if (string.IsNullOrEmpty(childName) || !IsLocalStructFunctionName(childName))
                    continue;

                if (!functionByName.TryGetValue(childName, out var childFunction))
                    continue;

                string signature = NormalizeLocalStructFunctionName(childName);
                if (map.ContainsKey(signature))
                    map[signature] = null;
                else
                    map[signature] = childFunction;
            }

            localMaps[owner] = map;
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

            var localMap = GetLocalMap(owner);
            if (localMap.Count == 0)
                continue;

            foreach (var instruction in code.Instructions ?? [])
            {
                var currentFunction = instruction.ValueFunction;
                var currentName = currentFunction?.Name?.Content;
                if (string.IsNullOrEmpty(currentName) || !IsLocalStructFunctionName(currentName))
                    continue;

                if (codeByName.TryGetValue(currentName, out var referencedCode) &&
                    ReferenceEquals(referencedCode.ParentEntry, owner))
                {
                    continue;
                }

                if (!localMap.TryGetValue(NormalizeLocalStructFunctionName(currentName), out var replacement) ||
                    replacement == null ||
                    ReferenceEquals(replacement, currentFunction))
                {
                    continue;
                }

                instruction.ValueFunction = replacement;
                repaired++;
            }
        }

        if (repaired > 0)
            LogService.Log($"[PatchService] Re-linked {repaired} local child Function references to their owning CodeEntry");

        int pruned = PruneDetachedLocalStructFunctions(data, codeByName);
        if (pruned > 0)
            LogService.Log($"[PatchService] Removed {pruned} detached local child Function entries");
    }

    private static int PruneDetachedLocalStructFunctions(
        UndertaleData data,
        IReadOnlyDictionary<string, UndertaleCode> codeByName)
    {
        if (data.Functions == null)
            return 0;

        var referenced = new HashSet<UndertaleFunction>();
        foreach (var code in data.Code ?? [])
        {
            if (code == null)
                continue;

            foreach (var entry in new[] { code }.Concat(code.ChildEntries ?? []))
            {
                if (entry?.Instructions == null)
                    continue;

                foreach (var instruction in entry.Instructions)
                {
                    if (instruction.ValueFunction != null)
                        referenced.Add(instruction.ValueFunction);
                }
            }
        }

        var liveLocalSignatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in data.Code ?? [])
        {
            if (code?.ParentEntry == null || string.IsNullOrEmpty(code.Name?.Content) ||
                !IsLocalStructFunctionName(code.Name.Content))
            {
                continue;
            }

            liveLocalSignatures.Add(NormalizeLocalStructFunctionName(code.Name.Content));
        }

        int removed = 0;
        for (int i = data.Functions.Count - 1; i >= 0; i--)
        {
            var function = data.Functions[i];
            var name = function?.Name?.Content;
            if (function == null || string.IsNullOrEmpty(name) || !IsLocalStructFunctionName(name))
                continue;

            if (codeByName.ContainsKey(name) || referenced.Contains(function))
                continue;

            if (!liveLocalSignatures.Contains(NormalizeLocalStructFunctionName(name)))
                continue;

            data.Functions.RemoveAt(i);
            removed++;
        }

        return removed;
    }

    private static bool IsLocalStructFunctionName(string functionName) =>
        functionName.Contains("____struct___", StringComparison.Ordinal);

    private static string NormalizeLocalStructFunctionName(string functionName) =>
        LocalStructOrdinalRegex().Replace(functionName, "____struct___#");

    [GeneratedRegex("____struct___\\d+")]
    private static partial Regex LocalStructOrdinalRegex();

    private static Dictionary<string, string> CaptureOriginalCodeGmlForAssetRemap(
        UndertaleData data,
        IReadOnlyCollection<string> resourceTypesToProcess)
    {
        if (!RequiresCodeRemapPreparation(resourceTypesToProcess))
            return [];

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var ctx = new GlobalDecompileContext(data);
        foreach (var code in data.Code)
        {
            string? codeName = code?.Name?.Content;
            if (code == null || string.IsNullOrWhiteSpace(codeName))
                continue;
            try
            {
                result[codeName] = new DecompileContext(ctx, code, data.ToolInfo.DecompilerSettings).DecompileToString();
            }
            catch
            {
                // If a code entry cannot be decompiled, leave its bytecode untouched.
            }
        }
        return result;
    }

    private static HashSet<string> CapturePatchCodeEntryNames(PatchFileSystem pfs)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in pfs.GmlEntries.Keys.Concat(pfs.AsmEntries.Keys))
        {
            names.Add(key);
            if (pfs.CodeEntryLogicalNames.TryGetValue(key, out var logicalName))
                names.Add(logicalName);
        }
        return names;
    }

    private static void RecompileUntouchedCodeForAssetOrderShifts(
        UndertaleData data,
        Dictionary<string, List<string>> originalAssetNames,
        Dictionary<string, string> originalGmlByCodeName,
        HashSet<string> patchCodeEntryNames,
        string helpersDir,
        string variablesFunctionsContent)
    {
        var shiftedNames = GetShiftedAssetNames(originalAssetNames, CaptureAssetNamesForCodeRemap(data));
        if (shiftedNames.Count == 0)
            return;

        var gmlToRecompile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (codeName, gml) in originalGmlByCodeName)
        {
            if (patchCodeEntryNames.Contains(codeName) ||
                !ContainsAnyShiftedAssetName(gml, shiftedNames))
            {
                continue;
            }

            gmlToRecompile[codeName] = gml;
        }

        if (gmlToRecompile.Count == 0)
            return;

        LogService.Log($"[PatchService] Recompiling {gmlToRecompile.Count} untouched code entries for shifted asset indices ({shiftedNames.Count} shifted asset name(s))");
        ImportCodeEntriesDirect(
            data,
            gmlToRecompile,
            [],
            null,
            null,
            helpersDir,
            variablesFunctionsContent,
            null);
    }

    private static HashSet<string> GetShiftedAssetNames(
        Dictionary<string, List<string>> before,
        Dictionary<string, List<string>> after)
    {
        var shifted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (section, beforeNames) in before)
        {
            if (!after.TryGetValue(section, out var afterNames))
                continue;

            var afterIndexByName = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
            for (int i = 0; i < afterNames.Count; i++)
            {
                var name = afterNames[i];
                if (!IsSafeAssetNameForCodeRemap(name))
                    continue;
                if (!afterIndexByName.TryGetValue(name, out var queue))
                {
                    queue = new Queue<int>();
                    afterIndexByName[name] = queue;
                }
                queue.Enqueue(i);
            }

            for (int i = 0; i < beforeNames.Count; i++)
            {
                var name = beforeNames[i];
                if (!IsSafeAssetNameForCodeRemap(name) ||
                    !afterIndexByName.TryGetValue(name, out var queue) ||
                    queue.Count == 0)
                {
                    continue;
                }

                int afterIndex = queue.Dequeue();
                if (afterIndex != i)
                    shifted.Add(name);
            }
        }
        return shifted;
    }

    private static bool ContainsAnyShiftedAssetName(string gml, HashSet<string> shiftedNames)
    {
        foreach (var name in shiftedNames)
        {
            if (ContainsIdentifierReference(gml, name))
                return true;
        }
        return false;
    }

    private static bool ContainsIdentifierReference(string text, string identifier)
    {
        int searchIndex = 0;
        while (searchIndex < text.Length)
        {
            int matchIndex = text.IndexOf(identifier, searchIndex, StringComparison.Ordinal);
            if (matchIndex < 0)
                return false;

            int beforeIndex = matchIndex - 1;
            int afterIndex = matchIndex + identifier.Length;
            bool hasIdentifierBoundaryBefore = beforeIndex < 0 || !IsIdentifierChar(text[beforeIndex]);
            bool hasIdentifierBoundaryAfter = afterIndex >= text.Length || !IsIdentifierChar(text[afterIndex]);
            if (hasIdentifierBoundaryBefore && hasIdentifierBoundaryAfter)
                return true;

            searchIndex = matchIndex + identifier.Length;
        }

        return false;
    }

    private static bool IsIdentifierChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_';

    private static bool IsSafeAssetNameForCodeRemap(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (name.StartsWith("obj_", StringComparison.Ordinal) ||
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
         name.StartsWith("shd_", StringComparison.Ordinal));

    /// <summary>
    /// Import code entries: GML compilation for structures, ASM reassembly for byte-perfect bytecode.
    /// </summary>
    private static void ImportCodeEntriesDirect(
        UndertaleData data,
        Dictionary<string, string> gmlEntries,
        Dictionary<string, string> asmEntries,
        Dictionary<string, string>? asmEntryPaths,
        Dictionary<string, string>? codeEntryLogicalNames,
        string helpersDir,
        string? vfContentOverride = null,
        string? objectEventsContentOverride = null)
    {
        lock (s_codeImportLock)
        {
            ImportCodeEntriesDirectCore(data, gmlEntries, asmEntries, asmEntryPaths, codeEntryLogicalNames, helpersDir, vfContentOverride, objectEventsContentOverride);
        }
    }

    private static void ImportCodeEntriesDirectCore(
        UndertaleData data,
        Dictionary<string, string> gmlEntries,
        Dictionary<string, string> asmEntries,
        Dictionary<string, string>? asmEntryPaths,
        Dictionary<string, string>? codeEntryLogicalNames,
        string helpersDir,
        string? vfContentOverride = null,
        string? objectEventsContentOverride = null)
    {
        var sw = Stopwatch.StartNew();
        var phaseSw = new Stopwatch();

        LogService.Log($"[ImportCodeEntries] Starting hybrid import: {gmlEntries.Count} GML + {asmEntries.Count} ASM entries...");
        LogService.Log($"[ImportCodeEntries] Current state: {data.Code.Count} code, {data.Variables.Count} vars, {data.Functions.Count} funcs");

        // Load TARGET metadata
        string vfContent;
        if (vfContentOverride != null)
        {
            vfContent = vfContentOverride;
        }
        else
        {
            string varFuncPath = Path.Combine(helpersDir, "variables_functions.json");
            if (!File.Exists(varFuncPath))
                throw new Exception($"variables_functions.json not found in {helpersDir}");
            vfContent = File.ReadAllText(varFuncPath, Encoding.UTF8);
        }

        using var vfDoc = JsonDocument.Parse(vfContent);
        var root = vfDoc.RootElement;
        var targetCodeEntriesByKey = ParseTargetCodeEntryMetadata(root, codeEntryLogicalNames);
        var targetTopLevelNamesByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var targetEntryNames = new HashSet<string>(StringComparer.Ordinal);
        var targetEntryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var targetChildEntriesByParent = new Dictionary<string, List<(string EntryKey, string LogicalName)>>(StringComparer.Ordinal);
        bool isAssetShiftRecompileOnly =
            asmEntries.Count == 0 &&
            asmEntryPaths == null &&
            codeEntryLogicalNames == null &&
            objectEventsContentOverride == null;
        foreach (var (entryKey, meta) in targetCodeEntriesByKey)
        {
            if (meta.ParentArchiveKey != null)
            {
                if (!targetChildEntriesByParent.TryGetValue(meta.ParentArchiveKey, out var childList))
                {
                    childList = [];
                    targetChildEntriesByParent[meta.ParentArchiveKey] = childList;
                }
                childList.Add((entryKey, meta.LogicalName));
                continue;
            }
            targetTopLevelNamesByKey[entryKey] = meta.LogicalName;
            targetEntryNames.Add(meta.LogicalName);
            targetEntryCounts[meta.LogicalName] = targetEntryCounts.GetValueOrDefault(meta.LogicalName) + 1;
        }

        // === Phase 1: Queue all GML entries for compilation ===
        phaseSw.Restart();
        bool allGmlCoveredByAsm =
            !isAssetShiftRecompileOnly &&
            asmEntries.Count > 0 &&
            gmlEntries.Keys.All(asmEntries.ContainsKey);

        // Snapshot existing code entries to detect compiler side-effects later
        var preCompileEntries = new HashSet<string>(data.Code.Count);
        if (!allGmlCoveredByAsm)
        {
            foreach (var c in data.Code)
            {
                if (c?.Name?.Content != null)
                    preCompileEntries.Add(c.Name.Content);
            }
        }

        GlobalDecompileContext? ctx = null;
        CodeImportGroup? importGroup = null;
        Dictionary<string, UndertaleCode> codeLookup = [];
        Dictionary<string, UndertaleCode> archiveCodeLookup = [];
        Dictionary<string, (UndertaleGameObject obj, int idx)> goLookup = [];
        Dictionary<string, string> collisionTargetsByCode = [];

        if (!allGmlCoveredByAsm)
        {
            ApplyTargetFunctionVariableTables(data, root, "precompile");
            ctx = new GlobalDecompileContext(data);
            ctx.PrepareForCompilation(true);

            importGroup = new CodeImportGroup(data, ctx)
            {
                AutoCreateAssets = true
            };

            // O(1) lookups for code entries and game objects
            codeLookup = new Dictionary<string, UndertaleCode>(data.Code.Count);
            foreach (var c in data.Code)
            {
                foreach (var nested in EnumerateCodeTreeRecursive(c))
                {
                    if (nested?.Name?.Content != null)
                        codeLookup.TryAdd(nested.Name.Content, nested);
                }
            }
            archiveCodeLookup = BuildArchiveCodeLookup(data);
            goLookup = new Dictionary<string, (UndertaleGameObject obj, int idx)>(data.GameObjects.Count);
            for (int i = 0; i < data.GameObjects.Count; i++)
            {
                var go = data.GameObjects[i];
                if (go?.Name?.Content != null)
                    goLookup.TryAdd(go.Name.Content, (go, i));
            }
            collisionTargetsByCode = LoadCollisionTargetsByCode(helpersDir, objectEventsContentOverride);
        }

        int regularCount = 0;
        int collisionCount = 0;
        int asmBackedCompileSkippedCount = allGmlCoveredByAsm ? gmlEntries.Count : 0;
        var gmlOnlyEntries = new List<(string CodeName, string GmlCode, bool IsCollision)>();

        if (!allGmlCoveredByAsm)
        {
            foreach (var (entryKey, gmlCode) in gmlEntries)
            {
                string codeName = targetTopLevelNamesByKey.GetValueOrDefault(entryKey, codeEntryLogicalNames?.GetValueOrDefault(entryKey) ?? entryKey);
                if (!isAssetShiftRecompileOnly && asmEntries.ContainsKey(entryKey))
                {
                    asmBackedCompileSkippedCount++;
                    continue;
                }

                if (codeName.Contains("_Collision_"))
                {
                    try
                    {
                        ImportCollisionEvent(data, codeName, gmlCode, importGroup!, codeLookup, goLookup, collisionTargetsByCode);
                        if (!asmEntries.ContainsKey(entryKey))
                            gmlOnlyEntries.Add((codeName, gmlCode, true));
                        collisionCount++;
                    }
                    catch (Exception ex)
                    {
                        LogService.Log($"[ImportCodeEntries] Collision error {codeName}: {ex.Message}");
                    }
                }
                else
                {
                    try
                    {
                        EnsureObjectEventForCode(data, codeName, codeLookup, goLookup);
                        if (archiveCodeLookup.TryGetValue(entryKey, out var existing) && existing.ParentEntry == null)
                            importGroup!.QueueReplace(existing, gmlCode);
                        else if (codeLookup.TryGetValue(codeName, out existing) && existing.ParentEntry == null)
                            importGroup!.QueueReplace(existing, gmlCode);
                        else
                            importGroup!.QueueReplace(codeName, gmlCode);
                        if (!asmEntries.ContainsKey(entryKey))
                            gmlOnlyEntries.Add((codeName, gmlCode, false));
                        regularCount++;
                    }
                    catch (Exception ex)
                    {
                        LogService.Log($"[ImportCodeEntries] Skipping {codeName}: {ex.Message}");
                    }
                }
            }
        }

        if (asmBackedCompileSkippedCount > 0)
            LogService.Log($"[ImportCodeEntries] Skipped GML compile for {asmBackedCompileSkippedCount} ASM-backed entrie(s)");

        LogService.Log($"[ImportCodeEntries] Queued {regularCount} regular + {collisionCount} collision entries in {phaseSw.Elapsed.TotalSeconds:F1}s");

        // === Phase 2: Delete code entries not present in TARGET ===
        phaseSw.Restart();
        if (targetEntryNames.Count > 0)
        {
            var entriesToDelete = new List<UndertaleCode>();
            var seenCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in data.Code)
            {
                if (c?.Name?.Content == null || c.ParentEntry != null)
                    continue;

                if (!targetEntryNames.Contains(c.Name.Content))
                {
                    entriesToDelete.Add(c);
                    continue;
                }

                int seen = seenCounts.GetValueOrDefault(c.Name.Content) + 1;
                seenCounts[c.Name.Content] = seen;
                if (seen > targetEntryCounts.GetValueOrDefault(c.Name.Content))
                    entriesToDelete.Add(c);
            }

            if (entriesToDelete.Count > 0)
            {
                LogService.Log($"[ImportCodeEntries] Removing {entriesToDelete.Count} stale code entries...");
                int deletedEvents = 0;

                foreach (var code in entriesToDelete)
                {
                    // Remove children first
                    var children = new List<UndertaleCode>();
                    foreach (var c in data.Code)
                    {
                        if (c?.ParentEntry == code)
                            children.Add(c);
                    }
                    foreach (var child in children)
                    {
                        data.Code.Remove(child);
                        for (int i = data.CodeLocals.Count - 1; i >= 0; i--)
                            if (data.CodeLocals[i].Name == child.Name) { data.CodeLocals.RemoveAt(i); break; }
                    }

                    // Remove events referencing this code
                    foreach (var obj in data.GameObjects)
                    {
                        if (obj == null) continue;
                        for (int evtType = 0; evtType < obj.Events.Count; evtType++)
                        {
                            var evtList = obj.Events[evtType];
                            for (int i = evtList.Count - 1; i >= 0; i--)
                            {
                                if (evtList[i].Actions.Count > 0 && evtList[i].Actions[0].CodeId == code)
                                {
                                    evtList.RemoveAt(i);
                                    deletedEvents++;
                                }
                            }
                        }
                    }

                    // Remove Script/GlobalInit references
                    foreach (var script in data.Scripts)
                        if (script?.Code == code) script.Code = null;
                    for (int i = data.GlobalInitScripts.Count - 1; i >= 0; i--)
                        if (data.GlobalInitScripts[i].Code == code) data.GlobalInitScripts.RemoveAt(i);

                    // Remove CodeLocals
                    for (int i = data.CodeLocals.Count - 1; i >= 0; i--)
                        if (data.CodeLocals[i].Name == code.Name) { data.CodeLocals.RemoveAt(i); break; }

                    data.Code.Remove(code);
                }
                LogService.Log($"[ImportCodeEntries] Deleted {entriesToDelete.Count} entries + {deletedEvents} events in {phaseSw.Elapsed.TotalSeconds:F1}s");
            }
        }
        // === Phase 3: Snapshot events, compile, restore ===
        phaseSw.Restart();
        bool touchesObjectEventCode = false;
        foreach (var entryKey in gmlEntries.Keys)
        {
            string codeName = targetTopLevelNamesByKey.GetValueOrDefault(entryKey, codeEntryLogicalNames?.GetValueOrDefault(entryKey) ?? entryKey);
            if (codeName.Contains("_Collision_", StringComparison.Ordinal) ||
                codeName.StartsWith("gml_Object_", StringComparison.Ordinal))
            {
                touchesObjectEventCode = true;
                break;
            }
        }
        if (!touchesObjectEventCode)
        {
            foreach (var entryKey in asmEntries.Keys)
            {
                string codeName = codeEntryLogicalNames?.GetValueOrDefault(entryKey) ?? targetTopLevelNamesByKey.GetValueOrDefault(entryKey, entryKey);
                if (codeName.Contains("_Collision_", StringComparison.Ordinal) ||
                    codeName.StartsWith("gml_Object_", StringComparison.Ordinal))
                {
                    touchesObjectEventCode = true;
                    break;
                }
            }
        }

        bool hasQueuedGmlCompilation = (regularCount + collisionCount) > 0;
        if (touchesObjectEventCode && hasQueuedGmlCompilation)
            LogService.Log("[ImportCodeEntries] Taking event snapshot before compilation...");

        var topLevelEntrySnapshot = new Dictionary<string, (UndertaleCode code, UndertaleCodeLocals? locals, UndertaleScript? script)>(StringComparer.Ordinal);
        var childEntrySnapshot = new Dictionary<string, (UndertaleCode code, UndertaleCodeLocals? locals, UndertaleScript? script, string parentArchiveKey)>(StringComparer.Ordinal);
        var pendingChildReattachments = new List<(UndertaleCode child, string parentArchiveKey)>();
        if (hasQueuedGmlCompilation)
        {
            // Snapshot child entries and their Script references (compiler may destroy them).
            var scriptByCode = new Dictionary<UndertaleCode, UndertaleScript>(data.Scripts.Count);
            foreach (var s in data.Scripts)
            {
                if (s?.Code != null)
                    scriptByCode.TryAdd(s.Code, s);
            }
            var codeLocalsByName = new Dictionary<UndertaleString, UndertaleCodeLocals>(data.CodeLocals.Count);
            foreach (var cl in data.CodeLocals)
            {
                if (cl.Name != null)
                    codeLocalsByName.TryAdd(cl.Name, cl);
            }
            var snapshotArchiveLookup = BuildArchiveCodeLookup(data);
            foreach (var descriptor in CodeEntryArchiveIdentity.DescribeEntries(data))
            {
                if (descriptor.ParentArchiveKey != null)
                    continue;
                if (!snapshotArchiveLookup.TryGetValue(descriptor.ArchiveKey, out var top) ||
                    top?.Name?.Content == null)
                {
                    continue;
                }

                scriptByCode.TryGetValue(top, out var script);
                codeLocalsByName.TryGetValue(top.Name, out var locals);
                topLevelEntrySnapshot[descriptor.ArchiveKey] = (top, locals, script);
            }

            foreach (var descriptor in CodeEntryArchiveIdentity.DescribeEntries(data))
            {
                if (descriptor.ParentArchiveKey == null)
                    continue;
                if (!snapshotArchiveLookup.TryGetValue(descriptor.ArchiveKey, out var nested) ||
                    nested?.Name?.Content == null)
                {
                    continue;
                }

                scriptByCode.TryGetValue(nested, out var script);
                codeLocalsByName.TryGetValue(nested.Name, out var locals);
                childEntrySnapshot[descriptor.ArchiveKey] = (nested, locals, script, descriptor.ParentArchiveKey);
            }
        }

        var eventSnapshot = new Dictionary<string, List<(int evtType, uint subtype, string codeName)>>();
        if (touchesObjectEventCode && hasQueuedGmlCompilation)
        {
            var objectNameCountsForEvents = BuildObjectNameCounts(data);
            for (int objIndex = 0; objIndex < data.GameObjects.Count; objIndex++)
            {
                var obj = data.GameObjects[objIndex];
                if (obj?.Name?.Content == null) continue;
                var events = new List<(int, uint, string)>();
                for (int evtType = 0; evtType < obj.Events.Count; evtType++)
                {
                    foreach (var evt in obj.Events[evtType])
                    {
                        string evtCodeName = (evt.Actions.Count > 0 && evt.Actions[0].CodeId != null)
                            ? (evt.Actions[0].CodeId.Name?.Content ?? "") : "";
                        events.Add((evtType, evt.EventSubtype, evtCodeName));
                    }
                }
                eventSnapshot[GetObjectEventKey(obj.Name.Content, objIndex, objectNameCountsForEvents)] = events;
            }
        }

        // Compile everything
        if (hasQueuedGmlCompilation)
        {
            LogService.Log($"[ImportCodeEntries] Compiling {regularCount + collisionCount} code entries...");
            var compileResult = importGroup!.Import(throwOnFailedCompile: false);
            if (!compileResult.Successful)
            {
                LogService.Log($"[ImportCodeEntries] Compilation warning: {compileResult.PrintAllErrors(true)}");
                RetryGmlOnlyCodeEntriesIndividually(data, gmlOnlyEntries, collisionTargetsByCode);
            }
        }
        else
        {
            LogService.Log("[ImportCodeEntries] GML compilation skipped: ASM payload covers all queued entries");
        }
        LogService.Log($"[ImportCodeEntries] Compilation phase complete in {phaseSw.Elapsed.TotalSeconds:F2}s");

        // Restore child entries removed by the compiler. Detach from parent to make standalone.
        if (hasQueuedGmlCompilation)
        {
            var currentArchiveLookupAfterCompile = BuildArchiveCodeLookup(data);
            LogWatchedArchiveCodeState("post-compile", currentArchiveLookupAfterCompile);
            var currentEntries = new HashSet<string>(BuildArchiveCodeLookup(data).Keys, StringComparer.Ordinal);
            var currentCodeObjects = new HashSet<UndertaleCode>(data.Code);
            var currentScripts = new HashSet<string>(data.Scripts.Count);
            foreach (var s in data.Scripts)
            {
                if (s?.Name?.Content != null)
                    currentScripts.Add(s.Name.Content);
            }
            int restoredTopLevel = 0;
            foreach (var (entryKey, snapshot) in topLevelEntrySnapshot)
            {
                if (targetCodeEntriesByKey.Count > 0 && !targetCodeEntriesByKey.ContainsKey(entryKey))
                    continue;
                if (currentEntries.Contains(entryKey))
                    continue;

                var (snapCode, snapLocals, snapScript) = snapshot;
                snapCode.ParentEntry = null;
                if (!currentCodeObjects.Contains(snapCode))
                {
                    data.Code.Add(snapCode);
                    currentCodeObjects.Add(snapCode);
                }
                if (snapLocals != null)
                {
                    bool localsExist = false;
                    foreach (var cl in data.CodeLocals)
                    {
                        if (cl.Name == snapCode.Name) { localsExist = true; break; }
                    }
                    if (!localsExist)
                        data.CodeLocals.Add(snapLocals);
                }
                if (snapScript != null && !currentScripts.Contains(snapScript.Name?.Content ?? ""))
                {
                    snapScript.Code = snapCode;
                    data.Scripts.Add(snapScript);
                    currentScripts.Add(snapScript.Name?.Content ?? "");
                }
                restoredTopLevel++;
            }
            if (restoredTopLevel > 0)
                LogService.Log($"[ImportCodeEntries] Restored {restoredTopLevel} missing top-level code occurrence(s) from snapshot");

            currentArchiveLookupAfterCompile = BuildArchiveCodeLookup(data);
            currentEntries = new HashSet<string>(currentArchiveLookupAfterCompile.Keys, StringComparer.Ordinal);

            int restored = 0;
            int repairedParenting = 0;
            foreach (var (entryKey, snapshot) in childEntrySnapshot)
            {
                if (targetCodeEntriesByKey.Count > 0 && !targetCodeEntriesByKey.ContainsKey(entryKey))
                    continue;

                var (snapCode, snapLocals, snapScript, parentArchiveKey) = snapshot;
                if (currentCodeObjects.Contains(snapCode) &&
                    currentArchiveLookupAfterCompile.TryGetValue(parentArchiveKey, out var desiredParent) &&
                    snapCode.ParentEntry != desiredParent)
                {
                    snapCode.ParentEntry?.ChildEntries.Remove(snapCode);
                    snapCode.ParentEntry = desiredParent;
                    if (!desiredParent.ChildEntries.Contains(snapCode))
                        desiredParent.ChildEntries.Add(snapCode);
                    repairedParenting++;
                    currentArchiveLookupAfterCompile = BuildArchiveCodeLookup(data);
                    currentEntries = new HashSet<string>(currentArchiveLookupAfterCompile.Keys, StringComparer.Ordinal);
                }

                if (!currentEntries.Contains(entryKey))
                {
                    snapCode.ParentEntry = null;
                    pendingChildReattachments.Add((snapCode, parentArchiveKey));
                    data.Code.Add(snapCode);
                    if (snapLocals != null)
                    {
                        bool localsExist = false;
                        foreach (var cl in data.CodeLocals)
                        {
                            if (cl.Name == snapCode.Name) { localsExist = true; break; }
                        }
                        if (!localsExist)
                            data.CodeLocals.Add(snapLocals);
                    }
                    if (snapScript != null && !currentScripts.Contains(snapScript.Name?.Content ?? ""))
                    {
                        snapScript.Code = snapCode;
                        data.Scripts.Add(snapScript);
                        LogService.Log($"  Restored detached child + script: {snapCode.Name?.Content} [{entryKey}]");
                    }
                    else
                    {
                        LogService.Log($"  Restored detached child: {snapCode.Name?.Content} [{entryKey}]");
                    }
                    restored++;
                }
            }
            if (repairedParenting > 0)
                LogService.Log($"[ImportCodeEntries] Repaired parent linkage for {repairedParenting} child code entrie(s)");
            if (restored > 0)
                LogService.Log($"[ImportCodeEntries] Restored {restored} child entries as standalone (detached from recompiled parents)");
            currentArchiveLookupAfterCompile = BuildArchiveCodeLookup(data);
            LogWatchedArchiveCodeState("post-restore", currentArchiveLookupAfterCompile);
        }

        // Remove spurious entries created as compiler side effects.
        if (hasQueuedGmlCompilation)
        {
            var spurious = new List<UndertaleCode>();
            foreach (var c in data.Code)
            {
                if (c?.Name?.Content != null && c.ParentEntry == null &&
                    !preCompileEntries.Contains(c.Name.Content) &&
                    !gmlEntries.ContainsKey(c.Name.Content))
                    spurious.Add(c);
            }

            if (spurious.Count > 0)
            {
                LogService.Log($"[ImportCodeEntries] Removing {spurious.Count} compiler side-effect entries...");
                foreach (var code in spurious)
                {
                    var spuriousChildren = new List<UndertaleCode>();
                    foreach (var c in data.Code)
                    {
                        if (c?.ParentEntry == code)
                            spuriousChildren.Add(c);
                    }
                    foreach (var child in spuriousChildren)
                    {
                        data.Code.Remove(child);
                        for (int i = data.CodeLocals.Count - 1; i >= 0; i--)
                            if (data.CodeLocals[i].Name == child.Name) { data.CodeLocals.RemoveAt(i); break; }
                    }
                    for (int i = data.Scripts.Count - 1; i >= 0; i--)
                        if (data.Scripts[i]?.Code == code) data.Scripts.RemoveAt(i);
                    for (int i = data.CodeLocals.Count - 1; i >= 0; i--)
                        if (data.CodeLocals[i].Name == code.Name) { data.CodeLocals.RemoveAt(i); break; }
                    data.Code.Remove(code);
                    LogService.Log($"  Removed: {code.Name?.Content}");
                }
            }
        }

        // Link Script entries with Code=null to their matching code entries.
        if (hasQueuedGmlCompilation)
        {
            int linkedScripts = 0;
            foreach (var script in data.Scripts)
            {
                if (script?.Name?.Content != null && script.Code == null)
                {
                    var code = ScriptCodeResolver.Resolve(data, script.Name.Content);
                    if (code != null)
                    {
                        script.Code = code;
                        linkedScripts++;
                    }
                }
            }
            if (linkedScripts > 0)
                LogService.Log($"[ImportCodeEntries] Linked {linkedScripts} Script entries to their code (were Code=null)");
        }

        LogService.Log($"[ImportCodeEntries] Compilation done in {phaseSw.Elapsed.TotalSeconds:F1}s");
        // === Phase 4: Restore events to pre-compilation state ===
        phaseSw.Restart();
        int restoredEvents = 0;
        if (touchesObjectEventCode && hasQueuedGmlCompilation)
        {
            var objectNameCountsForEvents = BuildObjectNameCounts(data);
            for (int objIndex = 0; objIndex < data.GameObjects.Count; objIndex++)
            {
                var obj = data.GameObjects[objIndex];
                if (obj?.Name?.Content == null) continue;
                if (!eventSnapshot.TryGetValue(GetObjectEventKey(obj.Name.Content, objIndex, objectNameCountsForEvents), out var snapshot)) continue;

                var expectedPairs = new HashSet<(int, uint)>();
                foreach (var (evtType, subtype, _) in snapshot)
                    expectedPairs.Add((evtType, subtype));

                for (int evtType = 0; evtType < obj.Events.Count; evtType++)
                {
                    var evtList = obj.Events[evtType];
                    for (int i = evtList.Count - 1; i >= 0; i--)
                    {
                        if (!expectedPairs.Contains((evtType, evtList[i].EventSubtype)))
                        {
                            evtList.RemoveAt(i);
                            restoredEvents++;
                        }
                    }
                }
            }
        }
        if (restoredEvents > 0)
            LogService.Log($"[ImportCodeEntries] Removed {restoredEvents} spurious events added during compilation");
        LogService.Log($"[ImportCodeEntries] Event restore phase complete in {phaseSw.Elapsed.TotalSeconds:F2}s");

        // Phase 4b: Event cleanup from TARGET's object_events.json
        var eventCleanupSw = Stopwatch.StartNew();
        string? objEventsContent = objectEventsContentOverride;
        string objEventsPath = Path.Combine(helpersDir, "object_events.json");
        if (objEventsContent == null && File.Exists(objEventsPath))
            objEventsContent = File.ReadAllText(objEventsPath, Encoding.UTF8);
        if (touchesObjectEventCode && hasQueuedGmlCompilation && objEventsContent != null)
        {
            var targetEventsRoot = JsonSerializer.Deserialize<JsonElement>(objEventsContent);
            int authRemoved = 0;

            var objectNameCountsForCleanup = BuildObjectNameCounts(data);
            for (int objIndex = 0; objIndex < data.GameObjects.Count; objIndex++)
            {
                var obj = data.GameObjects[objIndex];
                if (obj?.Name?.Content == null) continue;
                var eventKey = ResolveObjectEventsKey(targetEventsRoot, data, obj.Name.Content, objIndex, objectNameCountsForCleanup);
                if (eventKey == null || !targetEventsRoot.TryGetProperty(eventKey, out var targetEvents))
                {
                    continue;
                }

                // Collision events (type=4) matched by object name
                var targetEventKeys = new HashSet<string>();
                var targetCollisionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var evt in targetEvents.EnumerateArray())
                {
                    int t = evt.GetProperty("t").GetInt32();
                    if (t == 4 && evt.TryGetProperty("cn", out var cnElem))
                    {
                        string? cn = cnElem.GetString();
                        if (!string.IsNullOrEmpty(cn))
                        {
                            targetCollisionNames.Add(cn);
                        }
                    }
                    uint subtype = evt.GetProperty("s").GetUInt32();
                    if (t == 4 && evt.TryGetProperty("cn", out var collNameElem))
                    {
                        var collName = collNameElem.GetString();
                        int occurrence = evt.TryGetProperty("co", out var coElem) ? coElem.GetInt32() : 0;
                        var collObj = !string.IsNullOrEmpty(collName)
                            ? ResolveGameObjectByNameOccurrence(data, collName, occurrence)
                            : null;
                        if (collObj == null)
                            continue;
                        subtype = (uint)data.GameObjects.IndexOf(collObj);
                    }

                    if (t < 0 || t >= obj.Events.Count)
                        continue;

                    string eventKeepKey = $"{t}_{subtype}";
                    UndertaleGameObject.Event? targetEvent = null;
                    foreach (var existingEvt in obj.Events[t])
                    {
                        if (existingEvt.EventSubtype == subtype)
                        {
                            targetEvent = existingEvt;
                            break;
                        }
                    }

                    if (targetEvent == null)
                    {
                        targetEvent = new UndertaleGameObject.Event { EventSubtype = subtype };
                        obj.Events[t].Add(targetEvent);
                    }

                    bool hasLiveAction = false;
                    if (evt.TryGetProperty("actions", out var actionsElem) && actionsElem.ValueKind == JsonValueKind.Array)
                    {
                        targetEvent.Actions.Clear();
                        foreach (var actionElm in actionsElem.EnumerateArray())
                        {
                            targetEvent.Actions.Add(CreateEventActionFromJson(data, actionElm));
                        }
                        hasLiveAction = targetEvent.Actions.Any(action => action?.CodeId != null);
                    }
                    else if (evt.TryGetProperty("c", out var codeNameElem))
                    {
                        var codeName = codeNameElem.GetString();
                        if (!string.IsNullOrEmpty(codeName))
                        {
                            var code = data.Code.ByName(codeName);
                            if (code != null)
                            {
                                if (targetEvent.Actions.Count == 0)
                                    targetEvent.Actions.Add(new UndertaleGameObject.EventAction { CodeId = code });
                                else if (targetEvent.Actions[0].CodeId == null || targetEvent.Actions[0].CodeId.Name?.Content != codeName)
                                    targetEvent.Actions[0].CodeId = code;
                                hasLiveAction = true;
                            }
                        }
                    }

                    if (hasLiveAction)
                    {
                        targetEventKeys.Add(eventKeepKey);
                    }
                    else
                    {
                        obj.Events[t].Remove(targetEvent);
                    }
                }

                for (int evtType = 0; evtType < obj.Events.Count; evtType++)
                {
                    for (int j = obj.Events[evtType].Count - 1; j >= 0; j--)
                    {
                        bool shouldKeep;
                        if (evtType == 4)
                        {
                            int collObjIdx = (int)obj.Events[evtType][j].EventSubtype;
                            string? collObjName = collObjIdx >= 0 && collObjIdx < data.GameObjects.Count
                                ? data.GameObjects[collObjIdx]?.Name?.Content : null;
                            shouldKeep = !string.IsNullOrEmpty(collObjName) && targetCollisionNames.Contains(collObjName);
                        }
                        else
                        {
                            shouldKeep = targetEventKeys.Contains($"{evtType}_{obj.Events[evtType][j].EventSubtype}");
                        }

                        if (!shouldKeep)
                        {
                            obj.Events[evtType].RemoveAt(j);
                            authRemoved++;
                        }
                    }
                }
            }

            if (authRemoved > 0)
                LogService.Log($"[ImportCodeEntries] Removed {authRemoved} events not in TARGET (authoritative cleanup)");
        }
        eventCleanupSw.Stop();
        LogService.Log($"[ImportCodeEntries] Event cleanup done in {eventCleanupSw.Elapsed.TotalSeconds:F2}s");

        if (isAssetShiftRecompileOnly)
        {
            LogService.Log("[ImportCodeEntries] Function/variable reconciliation skipped for asset-shift recompile");
            LogService.Log($"[ImportCodeEntries] Hybrid import done in {sw.Elapsed.TotalSeconds:F1}s");
            return;
        }

        // === Phase 5: Reconcile functions against TARGET ===
        var functionReconcileSw = Stopwatch.StartNew();
        ApplyTargetFunctionVariableTables(data, root, "postcompile");
        functionReconcileSw.Stop();
        LogService.Log($"[ImportCodeEntries] Function reconciliation phase complete in {functionReconcileSw.Elapsed.TotalSeconds:F2}s");
        Dictionary<string, UndertaleString>? codeImportStringLookup = null;
        // === Phase 5b: Add missing variables from TARGET ===
        var variableReconcileSw = Stopwatch.StartNew();
        variableReconcileSw.Stop();
        LogService.Log($"[ImportCodeEntries] Variable reconciliation phase complete in {variableReconcileSw.Elapsed.TotalSeconds:F2}s");

        if (asmEntries.Count == 0)
        {
            LogService.Log("[ImportCodeEntries] ASM reassembly skipped: no ASM entries supplied");
            LogService.Log($"[ImportCodeEntries] Hybrid import done in {sw.Elapsed.TotalSeconds:F1}s");
            return;
        }

        // === Phase 6: ASM reassembly ===
        phaseSw.Restart();
        LogService.Log("[ImportCodeEntries] Reassembling from ASM for byte-perfect bytecode...");

        // Build local variable index lookup
        var localVarLookupSw = Stopwatch.StartNew();
        var localVarLookup = new Dictionary<string, int>();
        for (int vi = 0; vi < data.Variables.Count; vi++)
        {
            var v = data.Variables[vi];
            if (v?.Name?.Content != null && v.InstanceType == UndertaleInstruction.InstanceType.Local)
            {
                if (!localVarLookup.ContainsKey(v.Name.Content))
                    localVarLookup[v.Name.Content] = vi;
            }
        }
        localVarLookupSw.Stop();

        var stripStringIdxRegex = StripStringIdxRegex();
        int reassembled = 0;
        int asmFailed = 0;

        var archiveLookupSw = Stopwatch.StartNew();
        archiveCodeLookup = BuildArchiveCodeLookup(data);
        LogWatchedArchiveCodeState("pre-asm", archiveCodeLookup);
        archiveLookupSw.Stop();

        var skeletonSw = Stopwatch.StartNew();
        codeImportStringLookup ??= BuildStringLookup(data);
        int createdAsmSkeletons = 0;
        foreach (var entryKey in asmEntries.Keys)
        {
            if (archiveCodeLookup.ContainsKey(entryKey))
                continue;

            string codeName = codeEntryLogicalNames?.GetValueOrDefault(entryKey)
                ?? targetTopLevelNamesByKey.GetValueOrDefault(entryKey)
                ?? Path.GetFileName(entryKey);

            var newCode = new UndertaleCode
            {
                Name = MakeStringCached(data, codeImportStringLookup, codeName)
            };

            if (asmEntryPaths != null &&
                asmEntryPaths.TryGetValue(entryKey, out var asmPath))
            {
                var parts = asmPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    string parentKey = parts[^2];
                    if (!parentKey.Equals(entryKey, StringComparison.OrdinalIgnoreCase) &&
                        archiveCodeLookup.TryGetValue(parentKey, out var parent))
                    {
                        newCode.ParentEntry = parent;
                        if (!parent.ChildEntries.Contains(newCode))
                            parent.ChildEntries.Add(newCode);
                    }
                }
            }

            data.Code.Add(newCode);
            archiveCodeLookup[entryKey] = newCode;
            createdAsmSkeletons++;
        }

        if (createdAsmSkeletons > 0)
            LogService.Log($"[ImportCodeEntries] Created {createdAsmSkeletons} missing ASM code skeletons from patch metadata");
        skeletonSw.Stop();

        // Enable lookup caches in Assembler
        var assemblerCacheSw = Stopwatch.StartNew();
        Assembler.SetLookupCaches(data);
        assemblerCacheSw.Stop();
        var asmLoopSw = Stopwatch.StartNew();
        var childDirectiveSw = new Stopwatch();
        var asmPreprocessSw = new Stopwatch();
        var assembleOnlySw = new Stopwatch();
        var replaceInstructionsSw = new Stopwatch();
        var currentCodeSet = new HashSet<UndertaleCode>(data.Code);
        var scriptsByCode = new Dictionary<UndertaleCode, List<UndertaleScript>>();
        foreach (var script in data.Scripts)
        {
            if (script?.Code == null)
                continue;
            if (!scriptsByCode.TryGetValue(script.Code, out var scriptList))
            {
                scriptList = [];
                scriptsByCode[script.Code] = scriptList;
            }
            scriptList.Add(script);
        }
        int preprocessedAsmEntries = 0;
        int childDirectiveEntries = 0;
        try
        {

            foreach (var (entryKey, asmText) in asmEntries)
            {
                if (!archiveCodeLookup.TryGetValue(entryKey, out var code)) continue;
                string codeName = codeEntryLogicalNames?.GetValueOrDefault(entryKey)
                    ?? targetTopLevelNamesByKey.GetValueOrDefault(entryKey)
                    ?? code.Name?.Content
                    ?? entryKey;

                try
                {
                    // Step 1: Extract TARGET child names from > directives
                    var targetChildNames = new List<string>();
                    bool mayContainChildDirectives = asmText.Contains("\n> ", StringComparison.Ordinal) || asmText.StartsWith("> ", StringComparison.Ordinal);
                    if (mayContainChildDirectives)
                    {
                        childDirectiveSw.Start();
                        using var sr = new StringReader(asmText);
                        string? ln;
                        while ((ln = sr.ReadLine()) != null)
                        {
                            ln = ln.Trim();
                            if (ln.StartsWith("> "))
                            {
                                string rest = ln[2..].Trim();
                                int sp = rest.IndexOf(' ');
                                if (sp > 0)
                                    targetChildNames.Add(rest[..sp]);
                            }
                        }
                        childDirectiveSw.Stop();
                    }

                    // Step 2: Build child name mapping (TARGET name -> OURS name)
                    var nameMap = new Dictionary<string, string>();
                    if (targetChildNames.Count > 0)
                    {
                        childDirectiveEntries++;
                        childDirectiveSw.Start();
                        targetChildEntriesByParent.TryGetValue(entryKey, out var explicitChildEntries);
                        bool hasExplicitChildEntries = false;
                        if (explicitChildEntries is { Count: > 0 } explicitChildren)
                        {
                            hasExplicitChildEntries = true;
                            var existingChildren = code.ChildEntries.ToList();
                            foreach (var existingChild in existingChildren)
                            {
                                code.ChildEntries.Remove(existingChild);
                                if (existingChild.ParentEntry == code)
                                    existingChild.ParentEntry = null;
                                data.Code.Remove(existingChild);
                                currentCodeSet.Remove(existingChild);
                                for (int i = data.CodeLocals.Count - 1; i >= 0; i--)
                                {
                                    if (data.CodeLocals[i].Name == existingChild.Name)
                                    {
                                        data.CodeLocals.RemoveAt(i);
                                        break;
                                    }
                                }
                                if (scriptsByCode.TryGetValue(existingChild, out var scriptsForChild))
                                    foreach (var script in scriptsForChild)
                                        script.Code = null;
                            }

                            foreach (var (childEntryKey, childLogicalName) in explicitChildren)
                            {
                                if (!archiveCodeLookup.TryGetValue(childEntryKey, out var desiredChild))
                                {
                                    desiredChild = new UndertaleCode
                                    {
                                        Name = MakeStringCached(data, codeImportStringLookup, childLogicalName),
                                        ParentEntry = code
                                    };
                                    data.Code.Add(desiredChild);
                                    currentCodeSet.Add(desiredChild);
                                    archiveCodeLookup[childEntryKey] = desiredChild;
                                }
                                else if (currentCodeSet.Add(desiredChild))
                                {
                                    data.Code.Add(desiredChild);
                                }
                                desiredChild.ParentEntry = code;
                                if (!code.ChildEntries.Contains(desiredChild))
                                    code.ChildEntries.Add(desiredChild);
                            }
                        }

                        var targetChildSet = new HashSet<string>(targetChildNames, StringComparer.Ordinal);
                        var compiledChildren = code.ChildEntries.ToList();

                        foreach (var compiledChild in compiledChildren)
                        {
                            string? compiledName = compiledChild.Name?.Content;
                            if (compiledName == null || targetChildSet.Contains(compiledName))
                                continue;

                            code.ChildEntries.Remove(compiledChild);
                            if (compiledChild.ParentEntry == code)
                                compiledChild.ParentEntry = null;
                            data.Code.Remove(compiledChild);
                            currentCodeSet.Remove(compiledChild);

                            for (int i = data.CodeLocals.Count - 1; i >= 0; i--)
                            {
                                if (data.CodeLocals[i].Name == compiledChild.Name)
                                {
                                    data.CodeLocals.RemoveAt(i);
                                    break;
                                }
                            }

                            if (scriptsByCode.TryGetValue(compiledChild, out var scriptsForChild))
                                foreach (var script in scriptsForChild)
                                    script.Code = null;
                        }

                        var oursChildren = code.ChildEntries.OrderBy(c => c.Offset).ToList();
                        if (targetChildNames.Count != oursChildren.Count)
                        {
                            LogService.Log(
                                $"[ImportCodeEntries] Child topology differs for {codeName}: ASM has {targetChildNames.Count}, compiled has {oursChildren.Count}; continuing with partial remap");
                        }

                        int remapCount = hasExplicitChildEntries ? 0 : Math.Min(targetChildNames.Count, oursChildren.Count);
                        for (int ci = 0; ci < remapCount; ci++)
                        {
                            string tName = targetChildNames[ci];
                            string? oName = oursChildren[ci].Name?.Content;
                            if (!string.IsNullOrEmpty(oName) && tName != oName)
                                nameMap[tName] = oName;
                        }
                        childDirectiveSw.Stop();
                    }

                    // Step 3: Preprocess assembly text
                    bool needsLocalVarRemap = asmText.Contains(".localvar", StringComparison.Ordinal);
                    bool needsStringSuffixStrip = asmText.Contains("push.s ", StringComparison.Ordinal);
                    bool needsChildNameRemap = nameMap.Count > 0;
                    string assemblySource = asmText;

                    if (needsLocalVarRemap || needsStringSuffixStrip || needsChildNameRemap)
                    {
                        preprocessedAsmEntries++;
                        asmPreprocessSw.Start();
                        var sb = new StringBuilder(asmText.Length);
                        using var sr = new StringReader(asmText);
                        string? ln;
                        while ((ln = sr.ReadLine()) != null)
                        {
                            string trimmed = ln.Trim();

                            // Remap .localvar VARI indices
                            if (needsLocalVarRemap && trimmed.StartsWith(".localvar", StringComparison.Ordinal))
                            {
                                var parts = trimmed.Split(' ');
                                if (parts.Length >= 4)
                                {
                                    string varName = parts[2];
                                    if (localVarLookup.TryGetValue(varName, out int newIdx))
                                        parts[3] = newIdx.ToString();
                                    ln = string.Join(" ", parts);
                                }
                            }

                            // Strip @N string index suffixes
                            if (needsStringSuffixStrip && trimmed.StartsWith("push.s ", StringComparison.Ordinal))
                                ln = stripStringIdxRegex.Replace(ln, "\"");

                            // Remap child entry names in > directives and function references
                            if (needsChildNameRemap)
                            {
                                foreach (var kvp in nameMap)
                                {
                                    if (ln.Contains(kvp.Key))
                                        ln = ln.Replace(kvp.Key, kvp.Value);
                                }
                            }

                            sb.AppendLine(ln);
                        }

                        assemblySource = sb.ToString();
                        asmPreprocessSw.Stop();
                    }

                    // Step 4: Assemble
                    assembleOnlySw.Start();
                    var newInstructions = Assembler.Assemble(assemblySource, data, null, code);
                    assembleOnlySw.Stop();

                    // Step 5: Replace instructions
                    replaceInstructionsSw.Start();
                    code.Instructions.Clear();
                    foreach (var instr in newInstructions)
                        code.Instructions.Add(instr);

                    // Update code length
                    uint totalWords = 0;
                    foreach (var instr in newInstructions)
                        totalWords += instr.CalculateInstructionSize();
                    code.Length = totalWords * 4;
                    replaceInstructionsSw.Stop();

                    reassembled++;
                }
                catch (Exception ex)
                {
                    asmFailed++;
                    if (asmFailed <= 5)
                        LogService.Log($"[ImportCodeEntries] ASM error for {codeName}: {ex.Message}");
                }
            }

        } // end try
        finally
        {
            Assembler.ClearLookupCaches();
        }
        asmLoopSw.Stop();

        var postAsmTopologySw = Stopwatch.StartNew();
        if (pendingChildReattachments.Count > 0)
        {
            int reattachedChildren = 0;
            archiveCodeLookup = BuildArchiveCodeLookup(data);
            foreach (var (child, parentArchiveKey) in pendingChildReattachments)
            {
                if (!archiveCodeLookup.TryGetValue(parentArchiveKey, out var parent))
                    continue;
                child.ParentEntry = parent;
                if (!parent.ChildEntries.Contains(child))
                    parent.ChildEntries.Add(child);
                reattachedChildren++;
            }

            if (reattachedChildren > 0)
                LogService.Log($"[ImportCodeEntries] Reattached {reattachedChildren} restored child entries after ASM reassembly");
        }

        if (targetCodeEntriesByKey.Count > 0)
        {
            int canonicalParents = 0;
            int canonicalChildren = 0;
            var allTrackedCodes = new Dictionary<string, UndertaleCode>(StringComparer.Ordinal);

            foreach (var (entryKey, snapshot) in topLevelEntrySnapshot)
                allTrackedCodes[entryKey] = snapshot.code;
            foreach (var (entryKey, snapshot) in childEntrySnapshot)
                allTrackedCodes[entryKey] = snapshot.code;

            archiveCodeLookup = BuildArchiveCodeLookup(data);
            foreach (var (entryKey, code) in archiveCodeLookup)
                allTrackedCodes[entryKey] = code;

            foreach (var (entryKey, meta) in targetCodeEntriesByKey)
            {
                if (!allTrackedCodes.TryGetValue(entryKey, out var code))
                    continue;

                if (currentCodeSet.Add(code))
                    data.Code.Add(code);

                if (meta.ParentArchiveKey == null)
                {
                    code.ParentEntry?.ChildEntries.Remove(code);
                    code.ParentEntry = null;
                    code.ChildEntries.Clear();
                    canonicalParents++;
                }
            }

            foreach (var (entryKey, meta) in targetCodeEntriesByKey)
            {
                if (meta.ParentArchiveKey == null)
                    continue;
                if (!allTrackedCodes.TryGetValue(entryKey, out var child))
                    continue;
                if (!allTrackedCodes.TryGetValue(meta.ParentArchiveKey, out var parent))
                    continue;

                if (currentCodeSet.Add(child))
                    data.Code.Add(child);
                if (currentCodeSet.Add(parent))
                    data.Code.Add(parent);

                if (child.ParentEntry != null && child.ParentEntry != parent)
                    child.ParentEntry.ChildEntries.Remove(child);
                child.ParentEntry = parent;
                if (!parent.ChildEntries.Contains(child))
                    parent.ChildEntries.Add(child);
                canonicalChildren++;
            }

            LogService.Log($"[ImportCodeEntries] Canonicalized code topology from helper metadata: {canonicalParents} parents, {canonicalChildren} child links");
        }

        ReorderCodeEntriesFromTargetMetadata(data, targetCodeEntriesByKey);
        PruneCodeEntriesOutsideTargetMetadata(data, targetCodeEntriesByKey);

        archiveCodeLookup = BuildArchiveCodeLookup(data);
        LogWatchedArchiveCodeState("post-asm", archiveCodeLookup);
        postAsmTopologySw.Stop();

        if (asmFailed > 5)
            LogService.Log($"[ImportCodeEntries] ... and {asmFailed - 5} more ASM errors (suppressed)");
        LogService.Log(
            $"[ImportCodeEntries] ASM timings: funcs={functionReconcileSw.Elapsed.TotalSeconds:F2}s, vars={variableReconcileSw.Elapsed.TotalSeconds:F2}s, localvars={localVarLookupSw.Elapsed.TotalSeconds:F2}s, codeLookup={archiveLookupSw.Elapsed.TotalSeconds:F2}s, skeletons={skeletonSw.Elapsed.TotalSeconds:F2}s, cache={assemblerCacheSw.Elapsed.TotalSeconds:F2}s, loop={asmLoopSw.Elapsed.TotalSeconds:F2}s, child={childDirectiveSw.Elapsed.TotalSeconds:F2}s/{childDirectiveEntries}, preprocess={asmPreprocessSw.Elapsed.TotalSeconds:F2}s/{preprocessedAsmEntries}, assemble={assembleOnlySw.Elapsed.TotalSeconds:F2}s, replace={replaceInstructionsSw.Elapsed.TotalSeconds:F2}s, topology={postAsmTopologySw.Elapsed.TotalSeconds:F2}s");
        LogService.Log($"[ImportCodeEntries] Reassembled {reassembled}/{reassembled + asmFailed} entries in {phaseSw.Elapsed.TotalSeconds:F1}s");

        sw.Stop();
        LogService.Log($"[ImportCodeEntries] Hybrid import complete in {sw.Elapsed.TotalSeconds:F1}s");
    }

    private static void RetryGmlOnlyCodeEntriesIndividually(
        UndertaleData data,
        List<(string CodeName, string GmlCode, bool IsCollision)> gmlOnlyEntries,
        Dictionary<string, string> collisionTargetsByCode)
    {
        if (gmlOnlyEntries.Count == 0)
            return;

        var phaseSw = Stopwatch.StartNew();
        LogService.Log($"[ImportCodeEntries] Retrying {gmlOnlyEntries.Count} GML-only entries individually after group compile failure...");

        var ctx = new GlobalDecompileContext(data);
        ctx.PrepareForCompilation(true);

        int applied = 0;
        int failed = 0;
        foreach (var (CodeName, GmlCode, IsCollision) in gmlOnlyEntries)
        {
            try
            {
                var codeLookup = new Dictionary<string, UndertaleCode>(data.Code.Count);
                foreach (var c in data.Code)
                {
                    foreach (var nested in EnumerateCodeTreeRecursive(c))
                    {
                        if (nested?.Name?.Content != null)
                            codeLookup.TryAdd(nested.Name.Content, nested);
                    }
                }

                var goLookup = new Dictionary<string, (UndertaleGameObject obj, int idx)>(data.GameObjects.Count);
                for (int i = 0; i < data.GameObjects.Count; i++)
                {
                    var go = data.GameObjects[i];
                    if (go?.Name?.Content != null)
                        goLookup.TryAdd(go.Name.Content, (go, i));
                }

                var singleGroup = new CodeImportGroup(data, ctx)
                {
                    AutoCreateAssets = true
                };

                if (IsCollision)
                {
                    ImportCollisionEvent(data, CodeName, GmlCode, singleGroup, codeLookup, goLookup, collisionTargetsByCode);
                }
                else
                {
                    EnsureObjectEventForCode(data, CodeName, codeLookup, goLookup);
                    if (codeLookup.TryGetValue(CodeName, out var existing) && existing.ParentEntry == null)
                        singleGroup.QueueReplace(existing, GmlCode);
                    else
                        singleGroup.QueueReplace(CodeName, GmlCode);
                }

                var result = singleGroup.Import(throwOnFailedCompile: false);
                if (result.Successful)
                {
                    applied++;
                }
                else
                {
                    failed++;
                    LogService.Log($"[ImportCodeEntries] GML-only retry failed for {CodeName}: {result.PrintAllErrors(true)}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                LogService.Log($"[ImportCodeEntries] GML-only retry exception for {CodeName}: {ex.Message}");
            }
        }

        LogService.Log($"[ImportCodeEntries] GML-only retry applied {applied}/{gmlOnlyEntries.Count}, failed {failed}, in {phaseSw.Elapsed.TotalSeconds:F1}s");
    }

    private static void ApplyTargetFunctionVariableTables(UndertaleData data, JsonElement root, string phase)
    {
        ApplyTargetFunctionsExact(data, root, phase);
        ApplyTargetVariablesExact(data, root, phase);
    }

    private static void ApplyTargetFunctionsExact(UndertaleData data, JsonElement root, string phase)
    {
        if (!root.TryGetProperty("functions", out var funcArray))
            return;

        var oldByName = new Dictionary<string, UndertaleFunction>(StringComparer.Ordinal);
        foreach (var function in data.Functions)
        {
            var name = function?.Name?.Content;
            if (!string.IsNullOrEmpty(name) && function != null)
                oldByName.TryAdd(name, function);
        }

        var stringLookup = BuildStringLookup(data);
        var newList = new List<UndertaleFunction>(funcArray.GetArrayLength());
        var newByName = new Dictionary<string, UndertaleFunction>(StringComparer.Ordinal);
        foreach (var f in funcArray.EnumerateArray())
        {
            var name = f.GetString();
            if (string.IsNullOrEmpty(name))
                continue;

            name = CanonicalizeFunctionName(name);
            var function = oldByName.TryGetValue(name, out var existing)
                ? existing
                : new UndertaleFunction { Name = MakeStringCached(data, stringLookup, name) };
            function.Name = MakeStringCached(data, stringLookup, name);
            newList.Add(function);
            newByName.TryAdd(name, function);
        }

        data.Functions.Clear();
        foreach (var function in newList)
            data.Functions.Add(function);

        int remapped = 0;
        foreach (var code in data.Code)
        {
            foreach (var nested in EnumerateCodeTreeRecursive(code))
            {
                foreach (var instruction in nested.Instructions ?? [])
                {
                    var name = instruction.ValueFunction?.Name?.Content;
                    if (!string.IsNullOrEmpty(name) && newByName.TryGetValue(CanonicalizeFunctionName(name), out var replacement) && !ReferenceEquals(instruction.ValueFunction, replacement))
                    {
                        instruction.ValueFunction = replacement;
                        remapped++;
                    }
                }
            }
        }

        LogService.Log($"[ImportCodeEntries] Functions {phase}: exact={data.Functions.Count}, remapped={remapped}");
    }

    private static void ApplyTargetVariablesExact(UndertaleData data, JsonElement root, string phase)
    {
        if (!root.TryGetProperty("variables", out var varArray))
            return;

        var newList = new List<UndertaleVariable>(varArray.GetArrayLength());
        var newExact = new Dictionary<(string Name, UndertaleInstruction.InstanceType InstanceType, int VarId), UndertaleVariable>();
        var newByNameType = new Dictionary<(string Name, UndertaleInstruction.InstanceType InstanceType), UndertaleVariable>();
        foreach (var vElem in varArray.EnumerateArray())
        {
            string name = vElem.GetProperty("n").GetString() ?? "";
            int instType = vElem.GetProperty("t").GetInt32();
            int varId = vElem.GetProperty("id").GetInt32();
            if (string.IsNullOrEmpty(name))
                continue;

            var instanceType = (UndertaleInstruction.InstanceType)instType;
            var variable = new UndertaleVariable
            {
                Name = MakeDistinctString(data, name),
                InstanceType = instanceType,
                VarID = varId
            };
            newList.Add(variable);
            newExact.TryAdd((name, instanceType, varId), variable);
            newByNameType.TryAdd((name, instanceType), variable);
        }

        data.Variables.Clear();
        foreach (var variable in newList)
            data.Variables.Add(variable);

        int remapped = 0;
        foreach (var code in data.Code)
        {
            foreach (var nested in EnumerateCodeTreeRecursive(code))
            {
                foreach (var instruction in nested.Instructions ?? [])
                {
                    var variable = instruction.ValueVariable;
                    var name = variable?.Name?.Content;
                    if (string.IsNullOrEmpty(name) || variable == null)
                        continue;

                    if (!newExact.TryGetValue((name, variable.InstanceType, variable.VarID), out UndertaleVariable? replacement))
                        newByNameType.TryGetValue((name, variable.InstanceType), out replacement);

                    if (replacement != null && !ReferenceEquals(variable, replacement))
                    {
                        instruction.ValueVariable = replacement;
                        remapped++;
                    }
                }
            }
        }

        LogService.Log($"[ImportCodeEntries] Variables {phase}: exact={data.Variables.Count}, remapped={remapped}");
    }

    private static UndertaleString MakeDistinctString(UndertaleData data, string content)
    {
        var created = new UndertaleString(content);
        if (data.Strings is UndertaleObservableList<UndertaleString> stringList)
            stringList.InternalAdd(created);
        else
            data.Strings.Add(created);
        return created;
    }

    private static UndertaleString MakeStringCached(
        UndertaleData data,
        Dictionary<string, UndertaleString> stringLookup,
        string content)
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

    private static Dictionary<string, UndertaleString> BuildStringLookup(UndertaleData data)
    {
        var stringLookup = new Dictionary<string, UndertaleString>(data.Strings.Count, StringComparer.Ordinal);
        foreach (var str in data.Strings)
        {
            if (str?.Content != null)
                stringLookup.TryAdd(str.Content, str);
        }
        return stringLookup;
    }

    private static void EnsureObjectEventForCode(
        UndertaleData data,
        string codeName,
        Dictionary<string, UndertaleCode> codeLookup,
        Dictionary<string, (UndertaleGameObject obj, int idx)> goLookup)
    {
        if (!TryParseObjectEventCodeName(codeName, out var objectName, out int eventType, out uint eventSubtype))
            return;
        if (!goLookup.TryGetValue(objectName, out var objEntry))
            return;

        var obj = objEntry.obj;
        if (eventType < 0 || eventType >= obj.Events.Count)
            return;

        if (!codeLookup.TryGetValue(codeName, out var codeEntry))
        {
            codeEntry = UndertaleCode.CreateEmptyEntry(data, codeName);
            codeLookup[codeName] = codeEntry;
        }

        var eventList = obj.Events[eventType];
        UndertaleGameObject.Event? targetEvent = null;
        foreach (var evt in eventList)
        {
            if (evt.EventSubtype == eventSubtype)
            {
                targetEvent = evt;
                break;
            }
        }

        if (targetEvent == null)
        {
            targetEvent = new UndertaleGameObject.Event { EventSubtype = eventSubtype };
            eventList.Add(targetEvent);
        }

        if (targetEvent.Actions.Count == 0)
            targetEvent.Actions.Add(new UndertaleGameObject.EventAction { CodeId = codeEntry });
        else if (targetEvent.Actions[0].CodeId == null || targetEvent.Actions[0].CodeId.Name?.Content != codeName)
            targetEvent.Actions[0].CodeId = codeEntry;
    }

    private static Dictionary<string, int> BuildObjectNameCounts(UndertaleData data)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var obj in data.GameObjects)
        {
            var name = obj?.Name?.Content;
            if (name != null)
                counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        return counts;
    }

    private static string GetObjectEventKey(string objectName, int objectIndex, Dictionary<string, int> objectNameCounts)
        => objectNameCounts.GetValueOrDefault(objectName) > 1
            ? $"{objectName}__idx{objectIndex:D4}"
            : objectName;

    private static string? ResolveObjectEventsKey(
        JsonElement targetEventsRoot,
        UndertaleData data,
        string objectName,
        int objectIndex,
        Dictionary<string, int> objectNameCounts)
    {
        if (objectNameCounts.GetValueOrDefault(objectName) <= 1)
        {
            var exactKey = GetObjectEventKey(objectName, objectIndex, objectNameCounts);
            if (targetEventsRoot.TryGetProperty(exactKey, out _))
                return exactKey;
            return targetEventsRoot.TryGetProperty(objectName, out _) ? objectName : null;
        }

        int occurrence = 0;
        for (int i = 0; i < objectIndex; i++)
        {
            if (string.Equals(data.GameObjects[i]?.Name?.Content, objectName, StringComparison.Ordinal))
                occurrence++;
        }

        var duplicateKeys = new List<(int idx, string key)>();
        var prefix = objectName + "__idx";
        foreach (var prop in targetEventsRoot.EnumerateObject())
        {
            if (!prop.Name.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            if (int.TryParse(prop.Name.AsSpan(prefix.Length), out int idx))
                duplicateKeys.Add((idx, prop.Name));
        }

        if (duplicateKeys.Count == 0)
            return null;

        duplicateKeys.Sort((a, b) => a.idx.CompareTo(b.idx));
        return occurrence >= 0 && occurrence < duplicateKeys.Count
            ? duplicateKeys[occurrence].key
            : null;
    }

    private static void ApplyAuthoritativeObjectEvents(
        UndertaleData data,
        string objectEventsJson,
        HashSet<string>? changedCodeNames = null)
    {
        using var doc = JsonDocument.Parse(objectEventsJson);
        var root = doc.RootElement;
        var objectNameCounts = BuildObjectNameCounts(data);

        for (int objIndex = 0; objIndex < data.GameObjects.Count; objIndex++)
        {
            var obj = data.GameObjects[objIndex];
            if (obj?.Name?.Content == null)
                continue;

            var eventKey = ResolveObjectEventsKey(root, data, obj.Name.Content, objIndex, objectNameCounts);
            if (eventKey == null || !root.TryGetProperty(eventKey, out var targetEvents))
                continue;

            var desired = new List<(int Type, uint Subtype, JsonElement? Actions, string CodeName, string? CollisionName)>();
            bool objectUsesChangedCode = changedCodeNames == null;
            foreach (var evt in targetEvents.EnumerateArray())
            {
                int type = evt.GetProperty("t").GetInt32();
                uint subtype = evt.GetProperty("s").GetUInt32();
                JsonElement? actions = evt.TryGetProperty("actions", out var actionsElem) && actionsElem.ValueKind == JsonValueKind.Array
                    ? actionsElem
                    : null;
                string codeName = evt.TryGetProperty("c", out var c) ? c.GetString() ?? "" : "";
                string? collisionName = evt.TryGetProperty("cn", out var cn) ? cn.GetString() : null;
                if (!objectUsesChangedCode)
                {
                    if (!string.IsNullOrEmpty(codeName) && changedCodeNames!.Contains(codeName))
                    {
                        objectUsesChangedCode = true;
                    }
                    else if (actions.HasValue)
                    {
                        foreach (var actionElm in actions.Value.EnumerateArray())
                        {
                            if (actionElm.TryGetProperty("codeId", out var codeIdElm) &&
                                changedCodeNames!.Contains(codeIdElm.GetString() ?? ""))
                            {
                                objectUsesChangedCode = true;
                                break;
                            }
                        }
                    }
                }
                int collisionOccurrence = evt.TryGetProperty("co", out var co) ? co.GetInt32() : 0;
                if (type == (int)EventType.Collision && !string.IsNullOrEmpty(collisionName))
                {
                    var collObj = ResolveGameObjectByNameOccurrence(data, collisionName, collisionOccurrence);
                    if (collObj == null)
                        continue;
                    subtype = (uint)data.GameObjects.IndexOf(collObj);
                }
                desired.Add((type, subtype, actions, codeName, collisionName));
            }

            if (!objectUsesChangedCode)
                continue;

            for (int eventType = 0; eventType < obj.Events.Count; eventType++)
                obj.Events[eventType].Clear();

            foreach (var evt in desired)
            {
                if (evt.Type < 0 || evt.Type >= obj.Events.Count)
                    continue;
                var targetEvent = new UndertaleGameObject.Event { EventSubtype = evt.Subtype };
                if (evt.Actions.HasValue)
                {
                    foreach (var actionElm in evt.Actions.Value.EnumerateArray())
                        targetEvent.Actions.Add(CreateEventActionFromJson(data, actionElm));
                }
                else if (!string.IsNullOrEmpty(evt.CodeName))
                {
                    var code = data.Code.ByName(evt.CodeName);
                    if (code != null)
                        targetEvent.Actions.Add(new UndertaleGameObject.EventAction { CodeId = code });
                }

                if (targetEvent.Actions.Any(action => action?.CodeId != null))
                    obj.Events[evt.Type].Add(targetEvent);
            }
        }
    }

    private static UndertaleGameObject? ResolveGameObjectByNameOccurrence(UndertaleData data, string objectName, int occurrence)
    {
        int targetOccurrence = Math.Max(occurrence, 0);
        int seen = 0;
        foreach (var obj in data.GameObjects)
        {
            if (!string.Equals(obj?.Name?.Content, objectName, StringComparison.Ordinal))
                continue;
            if (seen == targetOccurrence)
                return obj;
            seen++;
        }
        return data.GameObjects.ByName(objectName);
    }

    private static bool TryParseObjectEventCodeName(string codeName, out string objectName, out int eventType, out uint eventSubtype)
    {
        objectName = string.Empty;
        eventType = -1;
        eventSubtype = 0;

        const string prefix = "gml_Object_";
        if (!codeName.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        if (codeName.Contains("_Collision_", StringComparison.Ordinal))
            return false;

        var eventNames = new (string Suffix, int Type)[]
        {
            ("_Create_", (int)EventType.Create),
            ("_Destroy_", (int)EventType.Destroy),
            ("_Alarm_", (int)EventType.Alarm),
            ("_Step_", (int)EventType.Step),
            ("_Mouse_", (int)EventType.Mouse),
            ("_Other_", (int)EventType.Other),
            ("_Draw_", (int)EventType.Draw),
            ("_KeyPress_", (int)EventType.KeyPress),
            ("_KeyRelease_", (int)EventType.KeyRelease),
            ("_CleanUp_", (int)EventType.CleanUp),
            ("_PreCreate_", (int)EventType.PreCreate)
        };

        foreach (var (suffix, type) in eventNames)
        {
            int idx = codeName.LastIndexOf(suffix, StringComparison.Ordinal);
            if (idx <= prefix.Length)
                continue;

            var subtypeText = codeName[(idx + suffix.Length)..];
            if (!uint.TryParse(subtypeText, out eventSubtype))
                continue;

            objectName = codeName[prefix.Length..idx];
            eventType = type;
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"""@\d+")]
    private static partial Regex StripStringIdxRegex();

    /// <summary>
    /// Import a collision event: find or create the event, then queue GML for compilation.
    /// </summary>
    private static void ImportCollisionEvent(
        UndertaleData data, string codeName, string gmlCode, CodeImportGroup importGroup,
        Dictionary<string, UndertaleCode> codeLookup,
        Dictionary<string, (UndertaleGameObject obj, int idx)> goLookup,
        Dictionary<string, string> collisionTargetsByCode)
    {
        const string prefix = "gml_Object_";
        if (!codeName.StartsWith(prefix)) return;

        int collisionIdx = codeName.LastIndexOf("_Collision_");
        if (collisionIdx < 0) return;

        string objectName = codeName[prefix.Length..collisionIdx];
        string identifier = codeName[(collisionIdx + "_Collision_".Length)..];

        if (!goLookup.TryGetValue(objectName, out var objEntry)) return;
        var obj = objEntry.obj;

        uint collisionIndex;
        if (collisionTargetsByCode.TryGetValue(codeName, out var collisionObjectName))
        {
            if (!goLookup.TryGetValue(collisionObjectName, out var collisionEntry)) return;
            collisionIndex = (uint)collisionEntry.idx;
        }
        else if (!uint.TryParse(identifier, out collisionIndex))
        {
            if (!goLookup.TryGetValue(identifier, out var collisionEntry)) return;
            collisionIndex = (uint)collisionEntry.idx;
        }

        // Find or create collision event and its code entry
        var collisionEvents = obj.Events[(int)EventType.Collision];
        UndertaleCode? codeEntry = null;

        foreach (var evt in collisionEvents)
        {
            if (evt.EventSubtype == collisionIndex)
            {
                if (evt.Actions.Count > 0 && evt.Actions[0].CodeId != null)
                {
                    codeEntry = evt.Actions[0].CodeId;
                }
                else
                {
                    codeLookup.TryGetValue(codeName, out codeEntry);
                    codeEntry ??= UndertaleCode.CreateEmptyEntry(data, codeName);
                    if (evt.Actions.Count == 0)
                        evt.Actions.Add(new UndertaleGameObject.EventAction { CodeId = codeEntry });
                    else
                        evt.Actions[0].CodeId = codeEntry;
                }
                break;
            }
        }

        if (codeEntry == null)
        {
            codeLookup.TryGetValue(codeName, out codeEntry);
            codeEntry ??= UndertaleCode.CreateEmptyEntry(data, codeName);
            var newEvent = new UndertaleGameObject.Event { EventSubtype = collisionIndex };
            newEvent.Actions.Add(new UndertaleGameObject.EventAction { CodeId = codeEntry });
            collisionEvents.Add(newEvent);
        }

        importGroup.QueueReplace(codeEntry, gmlCode);
    }

    private static Dictionary<string, string> LoadCollisionTargetsByCode(string helpersDir, string? objectEventsContentOverride)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? objectEventsContent = objectEventsContentOverride;
        string objEventsPath = Path.Combine(helpersDir, "object_events.json");
        if (objectEventsContent == null && File.Exists(objEventsPath))
            objectEventsContent = File.ReadAllText(objEventsPath, Encoding.UTF8);
        if (objectEventsContent == null)
            return result;

        using var doc = JsonDocument.Parse(objectEventsContent);
        foreach (var objEvents in doc.RootElement.EnumerateObject())
        {
            foreach (var evt in objEvents.Value.EnumerateArray())
            {
                if (!evt.TryGetProperty("t", out var typeElem) || typeElem.GetInt32() != (int)EventType.Collision)
                    continue;
                if (!evt.TryGetProperty("c", out var codeElem) || codeElem.ValueKind != JsonValueKind.String)
                    continue;
                if (!evt.TryGetProperty("cn", out var collisionNameElem) || collisionNameElem.ValueKind != JsonValueKind.String)
                    continue;

                var codeName = codeElem.GetString();
                var collisionName = collisionNameElem.GetString();
                if (!string.IsNullOrEmpty(codeName) && !string.IsNullOrEmpty(collisionName))
                    result[codeName] = collisionName;
            }
        }
        return result;
    }

    /// <summary>
    /// Format patch statistics as "Changed X (Y), New X (Y), Deleted X"
    /// where X = resource count, Y = individual file changes.
    /// </summary>
    private static string FormatPatchStats(PatchStatistics s)
    {
        List<string> parts = [];
        if (s.TotalChanged > 0)
        {
            parts.Add(s.TotalChangedFiles > 0
                ? $"Changed: {s.TotalChanged} ({s.TotalChangedFiles} files)"
                : $"Changed: {s.TotalChanged}");
        }
        if (s.TotalNew > 0)
        {
            parts.Add(s.TotalNewFiles > 0
                ? $"New: {s.TotalNew} ({s.TotalNewFiles} files)"
                : $"New: {s.TotalNew}");
        }
        if (s.TotalDeleted > 0)
            parts.Add($"Deleted: {s.TotalDeleted}");
        return parts.Count > 0 ? string.Join(", ", parts) : "No changes";
    }

    public static async Task<PatchValidateResult> ValidatePatchAsync(string patchPath, string? dataPath = null, G3MCacheOptions? cacheOptions = null)
    {
        if (!File.Exists(patchPath))
            return new PatchValidateResult { Success = false, Error = $"Patch file not found: {patchPath}" };

        try
        {
            using var zipStream = new FileStream(patchPath, FileMode.Open, FileAccess.Read);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            var manifestEntry = archive.GetEntry("g3mpatch.json");
            if (manifestEntry == null)
                return new PatchValidateResult { Success = false, Error = "Invalid patch: g3mpatch.json not found" };

            using var reader = new StreamReader(manifestEntry.Open());
            var json = await reader.ReadToEndAsync();
            var manifest = JsonSerializer.Deserialize<G3MPatchManifest>(json);

            if (manifest == null)
                return new PatchValidateResult { Success = false, Error = "Invalid patch: failed to parse manifest" };

            // Check compatibility with data file if provided
            if (dataPath != null && File.Exists(dataPath))
            {
                var dataHash = G3MCacheService.TryReadDataFileInfo(dataPath, cacheOptions)?.Md5
                    ?? await HashService.ComputeFileHashAsync(dataPath);
                if (manifest.Original?.Md5 != null && manifest.Original.Md5 != dataHash)
                {
                    // Not exact match, but might still be compatible
                    LogService.Warning(" Data file hash doesn't match original - patch may not apply correctly");
                }
            }

            return new PatchValidateResult { Success = true, Manifest = manifest };
        }
        catch (Exception ex)
        {
            return new PatchValidateResult { Success = false, Error = $"Failed to read patch: {ex.Message}" };
        }
    }

    /// <summary>
    /// Ensures the input is a .g3mpatch file. Converts xdelta or data files if needed.
    /// Returns the original path if already .g3mpatch, or a temp path to the converted patch.
    /// </summary>
    public static async Task<string> EnsureG3MPatchAsync(
        string originalPath, string inputPath, string? tempDir = null,
        Dictionary<string, Dictionary<string, string>>? precomputedOriginalHashes = null,
        DataFileInfo? precomputedOriginalInfo = null,
        Dictionary<string, Dictionary<string, int>>? precomputedOriginalNameCounts = null,
        Dictionary<string, IReadOnlyList<string>>? precomputedOriginalOrderedNames = null,
        G3MCacheOptions? cacheOptions = null)
    {
        var ext = Path.GetExtension(inputPath).ToLowerInvariant();

        if (ext is ".g3mpatch" or ".zip")
            return inputPath;

        string dataFilePath;

        if (ext == ".xdelta")
        {
            LogService.Warning($"Input '{Path.GetFileName(inputPath)}' is xdelta, converting to .g3mpatch...");
            var xdelta = new XDeltaService();
            dataFilePath = Path.Combine(tempDir ?? Path.GetTempPath(), $"g3mtool_conv_{Guid.NewGuid():N}.win");
            var xResult = await xdelta.ApplyPatchAsync(originalPath, inputPath, dataFilePath);
            if (!xResult.Success)
                throw new Exception($"Failed to apply xdelta '{Path.GetFileName(inputPath)}': {xResult.Error}");
        }
        else if (DataFileExtensionUtil.IsDataFile(inputPath))
        {
            LogService.Warning($"Input '{Path.GetFileName(inputPath)}' is a data file, converting to .g3mpatch...");
            dataFilePath = inputPath;
        }
        else
        {
            return inputPath;
        }

        var outputZip = Path.Combine(tempDir ?? Path.GetTempPath(), $"g3mtool_conv_{Guid.NewGuid():N}.g3mpatch");
        var result = await CreatePatchAsync(
            originalPath,
            dataFilePath,
            outputZip,
            precomputedOriginalHashes,
            precomputedOriginalInfo,
            precomputedOriginalNameCounts,
            precomputedOriginalOrderedNames,
            includeXdeltaFallback: false,
            cacheOptions: cacheOptions
        );
        if (!result.Success)
            throw new Exception($"Failed to create .g3mpatch from '{Path.GetFileName(inputPath)}': {result.Error}");

        // Clean up temp data file if we created one from xdelta
        if (ext == ".xdelta" && dataFilePath != inputPath)
            try { File.Delete(dataFilePath); } catch { }

        return outputZip;
    }

    public static string? GetCreateCompatibilityError(DataFileInfo original, DataFileInfo modified)
    {
        if (original.BytecodeVersion != modified.BytecodeVersion)
        {
            return $"Incompatible data files: bytecode version differs ({original.BytecodeVersion} vs {modified.BytecodeVersion}). " +
                   "Use the exact original data file for this modified file.";
        }

        var originalInfo = original.GeneralInfo;
        var modifiedInfo = modified.GeneralInfo;
        if (originalInfo == null || modifiedInfo == null)
            return null;

        if (!SameText(originalInfo.DisplayName, modifiedInfo.DisplayName) &&
            IsLikelyChapterMismatch(originalInfo.DisplayName, modifiedInfo.DisplayName))
        {
            return $"Incompatible data files: display name differs ('{originalInfo.DisplayName}' vs '{modifiedInfo.DisplayName}'). " +
                   "This usually means a different GameMaker game/chapter/build was used as the base.";
        }

        if (originalInfo.RoomOrderCount > 0 && modifiedInfo.RoomOrderCount > 0)
        {
            var smaller = Math.Min(originalInfo.RoomOrderCount, modifiedInfo.RoomOrderCount);
            var larger = Math.Max(originalInfo.RoomOrderCount, modifiedInfo.RoomOrderCount);
            if (larger > smaller * 3)
            {
                return $"Incompatible data files: room order count differs too much ({originalInfo.RoomOrderCount} vs {modifiedInfo.RoomOrderCount}). " +
                       "Use the exact original data file for this modified file.";
            }
        }

        return null;
    }

    private static bool SameText(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsLikelyChapterMismatch(string? left, string? right)
    {
        static bool HasChapterMarker(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Contains("chapter", StringComparison.OrdinalIgnoreCase);

        return HasChapterMarker(left) || HasChapterMarker(right);
    }

    /// <summary>
    /// Filter asset_order.txt lines to exclude entire sections and their count lines.
    /// Sections are matched case-insensitively against @@section@@ headers.
    /// Count lines like "Sprites=5053" are also filtered if "sprites" is excluded.
    /// </summary>
    private static string[] FilterAssetOrderSections(string[] lines, params string[] skipSections)
    {
        var skip = new HashSet<string>(skipSections, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(lines.Length);
        bool inSkipped = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("@@") && line.EndsWith("@@") && line.Length > 4)
            {
                string sect = line[2..^2];
                inSkipped = skip.Contains(sect);
                if (!inSkipped) result.Add(line);
                continue;
            }
            if (inSkipped) continue;
            // Filter count lines like "Sprites=5053" in @@counts@@ section
            int eq = line.IndexOf('=');
            if (eq > 0 && skip.Contains(line[..eq]))
                continue;
            result.Add(line);
        }
        return [.. result];
    }

    private static HashSet<string> BuildRelevantAssetOrderSections(IEnumerable<string> resourceTypes, bool includeScriptsForCodeEntries)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourceType in resourceTypes)
        {
            switch (resourceType)
            {
                case "Sounds":
                    keep.Add("sounds");
                    break;
                case "Sprites":
                    keep.Add("sprites");
                    break;
                case "Backgrounds":
                case "Tilesets":
                    keep.Add("backgrounds");
                    break;
                case "Paths":
                    keep.Add("paths");
                    break;
                case "Scripts":
                    keep.Add("scripts");
                    break;
                case "Fonts":
                    keep.Add("fonts");
                    break;
                case "GameObjects":
                    keep.Add("objects");
                    break;
                case "Timelines":
                    keep.Add("timelines");
                    break;
                case "Rooms":
                    keep.Add("rooms");
                    break;
                case "Shaders":
                    keep.Add("shaders");
                    break;
                case "Extensions":
                    keep.Add("extensions");
                    break;
                case "AudioGroups":
                    keep.Add("audiogroups");
                    break;
                case "CodeEntries":
                    if (includeScriptsForCodeEntries)
                        keep.Add("scripts");
                    break;
            }
        }
        return keep;
    }

    private static bool RequiresHeavyFinalize(IEnumerable<string> resourceTypesToProcess)
    {
        bool sawAny = false;
        bool sawOnlyScriptMetadata = true;
        foreach (var resourceType in resourceTypesToProcess)
        {
            sawAny = true;
            switch (resourceType)
            {
                case "GeneralInfo":
                case "Options":
                case "Language":
                case "FeatureFlags":
                case "Tags":
                case "FilterEffects":
                    continue;
                case "Scripts":
                case "GlobalScripts":
                    continue;
                default:
                    sawOnlyScriptMetadata = false;
                    return true;
            }
        }

        if (!sawAny)
            return true;

        return !sawOnlyScriptMetadata;
    }

    private static bool RequiresScriptEntryEnsure(IEnumerable<string> resourceTypesToProcess)
    {
        foreach (var resourceType in resourceTypesToProcess)
        {
            if (resourceType.Equals("Scripts", StringComparison.OrdinalIgnoreCase) ||
                resourceType.Equals("GlobalScripts", StringComparison.OrdinalIgnoreCase) ||
                resourceType.Equals("CodeEntries", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool RequiresObjectEventsHelper(IEnumerable<string> changedResourceTypes)
    {
        foreach (var resourceType in changedResourceTypes)
        {
            if (resourceType.Equals("CodeEntries", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool ShouldApplyAuthoritativeObjectEvents(
        List<string> resourceTypesToProcess,
        G3MPatchManifest? manifest,
        byte[] objectEventsBytes)
    {
        if (resourceTypesToProcess.Contains("GameObjects", StringComparer.OrdinalIgnoreCase))
            return true;

        if (manifest?.Resources == null ||
            !manifest.Resources.TryGetValue("CodeEntries", out var codeChanges))
        {
            return false;
        }

        var changedCodeNames = GetChangedCodeEntryNames(codeChanges);

        if (changedCodeNames.Count == 0)
            return true;

        var objectEventsText = Encoding.UTF8.GetString(objectEventsBytes);
        foreach (var codeName in changedCodeNames)
        {
            if (objectEventsText.Contains(codeName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static HashSet<string> GetChangedCodeEntryNames(G3MPatchManifest? manifest)
    {
        if (manifest?.Resources == null ||
            !manifest.Resources.TryGetValue("CodeEntries", out var codeChanges))
        {
            return [];
        }

        return GetChangedCodeEntryNames(codeChanges);
    }

    private static HashSet<string> GetChangedCodeEntryNames(ResourceTypeChanges codeChanges)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (codeChanges.Changed != null)
        {
            foreach (var change in codeChanges.Changed)
            {
                if (!string.IsNullOrWhiteSpace(change?.Name))
                    names.Add(change.Name!);
            }
        }
        if (codeChanges.New != null)
        {
            foreach (var change in codeChanges.New)
            {
                if (!string.IsNullOrWhiteSpace(change?.Name))
                    names.Add(change.Name!);
            }
        }

        return names;
    }

    private static bool RequiresVariableFunctionsHelper(IEnumerable<string> changedResourceTypes)
    {
        foreach (var resourceType in changedResourceTypes)
        {
            if (resourceType.Equals("CodeEntries", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool RequiresTextureMappingHelpers(IEnumerable<string> changedResourceTypes)
    {
        foreach (var resourceType in changedResourceTypes)
        {
            if (resourceType.Equals("Sprites", StringComparison.OrdinalIgnoreCase)
                || resourceType.Equals("Backgrounds", StringComparison.OrdinalIgnoreCase)
                || resourceType.Equals("Fonts", StringComparison.OrdinalIgnoreCase)
                || resourceType.Equals("Tilesets", StringComparison.OrdinalIgnoreCase)
                || resourceType.Equals("EmbeddedImages", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDirectApplyFriendlyResourceType(string resourceType) =>
        resourceType switch
        {
            "GeneralInfo" or
            "Options" or
            "Language" or
            "FeatureFlags" or
            "Tags" or
            "FilterEffects" or
            "AudioGroups" or
            "EmbeddedAudio" or
            "TextureGroupInfo" or
            "EmbeddedImages" or
            "AnimationCurves" or
            "Shaders" or
            "Extensions" => true,
            _ => false
        };

    private static bool CanUseMinimalStandardApply(
        PatchApplyPlan applyPlan,
        IReadOnlyCollection<string> resourceTypesToProcess) =>
        applyPlan.SupportsDirectResourceApply &&
        !applyPlan.RequiresCodePipeline &&
        !applyPlan.RequiresTexturePipeline &&
        !applyPlan.RequiresAssetReorder &&
        resourceTypesToProcess.All(IsDirectApplyFriendlyResourceType);

    internal static PatchApplyPlan BuildPatchApplyPlan(IReadOnlyDictionary<string, ResourceTypeChanges> resources)
    {
        var resourceTypes = resources.Keys.ToArray();
        bool requiresCodePipeline = resourceTypes.Contains("CodeEntries", StringComparer.OrdinalIgnoreCase);
        bool requiresTexturePipeline =
            RequiresTextureMappingHelpers(resourceTypes) ||
            resourceTypes.Contains("EmbeddedTextures", StringComparer.OrdinalIgnoreCase) ||
            resourceTypes.Contains("TexturePageItems", StringComparer.OrdinalIgnoreCase);
        bool requiresAssetReorder = RequiresFinalAssetReorder(resourceTypes);
        bool requiresHeavyFinalize = RequiresHeavyFinalize(resourceTypes);

        var simpleTypes = resourceTypes
            .Where(IsDirectApplyFriendlyResourceType)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var heavyTypes = resourceTypes
            .Where(static x => !IsDirectApplyFriendlyResourceType(x))
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool supportsDirectResourceApply =
            heavyTypes.Count == 0 &&
            resources.Values.All(static changes => (changes.Deleted?.Count ?? 0) == 0);

        return new PatchApplyPlan
        {
            Mode = supportsDirectResourceApply ? "direct" : "standard",
            RequiresCodePipeline = requiresCodePipeline,
            RequiresTexturePipeline = requiresTexturePipeline,
            RequiresAssetReorder = requiresAssetReorder,
            RequiresHeavyFinalize = requiresHeavyFinalize,
            SupportsDirectResourceApply = supportsDirectResourceApply,
            SimpleResourceTypes = simpleTypes,
            HeavyResourceTypes = heavyTypes
        };
    }

    private static bool RequiresPatchHelpers(
        IEnumerable<string> changedResourceTypes,
        IEnumerable<string>? helperForcedResourceTypes = null)
    {
        if (helperForcedResourceTypes != null)
        {
            foreach (var forcedType in helperForcedResourceTypes)
            {
                if (!string.IsNullOrWhiteSpace(forcedType))
                    return true;
            }
        }

        bool sawAny = false;
        foreach (var resourceType in changedResourceTypes)
        {
            sawAny = true;
            if (!IsHelperFreeResourceType(resourceType))
                return true;
        }

        if (!sawAny)
            return true;

        return false;
    }

    private static bool IsHelperFreeResourceType(string resourceType)
    {
        return resourceType.Equals("GameObjects", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("Fonts", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("Sounds", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("Paths", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("Rooms", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("Options", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("FeatureFlags", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("Tags", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("Language", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("GeneralInfo", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("FilterEffects", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("EmbeddedImages", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("AnimationCurves", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("TextureGroupInfo", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("Shaders", StringComparison.OrdinalIgnoreCase)
            || resourceType.Equals("Extensions", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> GetHelperRelevantResourceTypes(
        Dictionary<string, Dictionary<string, string>> originalHashes,
        Dictionary<string, Dictionary<string, string>> modifiedHashes,
        Dictionary<string, HashSet<string>> changedNamesPerType)
    {
        var resourceTypes = new HashSet<string>(changedNamesPerType.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var resourceType in ResourceTypeRegistry.AllTypes)
        {
            var origTypeHashes = originalHashes.GetValueOrDefault(resourceType) ?? [];
            var modTypeHashes = modifiedHashes.GetValueOrDefault(resourceType) ?? [];

            foreach (var name in origTypeHashes.Keys)
            {
                if (!modTypeHashes.ContainsKey(name))
                {
                    resourceTypes.Add(resourceType);
                    break;
                }
            }
        }

        return resourceTypes;
    }

    private static bool RequiresDuplicateGameObjectRepair(IEnumerable<string> changedResourceTypes, G3MPatchManifest? manifest)
    {
        bool hasGameObjects = false;
        foreach (var resourceType in changedResourceTypes)
        {
            if (resourceType.Equals("GameObjects", StringComparison.OrdinalIgnoreCase))
            {
                hasGameObjects = true;
                break;
            }
        }

        if (!hasGameObjects)
            return false;

        var changes = manifest?.Resources?.GetValueOrDefault("GameObjects");
        if (changes == null)
            return true;

        var names = new List<string>();
        if (changes.Changed != null)
            names.AddRange(changes.Changed.Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n))!);
        if (changes.New != null)
            names.AddRange(changes.New.Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n))!);

        if (names.Count == 0)
            return true;

        var baseCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var baseName = StripIdxSuffix(name!);
            baseCounts[baseName] = baseCounts.GetValueOrDefault(baseName) + 1;
            if (baseCounts[baseName] > 1)
                return true;
        }

        return false;
    }

    private static bool RequiresFinalAssetReorder(IEnumerable<string> resourceTypesToProcess)
    {
        bool sawAny = false;
        foreach (var resourceType in resourceTypesToProcess)
        {
            sawAny = true;
            switch (resourceType)
            {
                case "GeneralInfo":
                case "Options":
                case "Language":
                case "FeatureFlags":
                case "Tags":
                case "FilterEffects":
                case "TextureGroupInfo":
                case "EmbeddedAudio":
                case "EmbeddedImages":
                case "CodeEntries":
                    break;
                default:
                    return true;
            }
        }

        if (!sawAny)
            return true;

        return false;
    }

    private static bool RequiresCodeRemapPreparation(IEnumerable<string> resourceTypesToProcess)
    {
        foreach (var resourceType in resourceTypesToProcess)
        {
            switch (resourceType)
            {
                case "Sprites":
                case "Backgrounds":
                case "Tilesets":
                case "Sounds":
                case "Paths":
                case "Fonts":
                case "GameObjects":
                case "Rooms":
                case "Timelines":
                case "Shaders":
                    return true;
            }
        }

        return false;
    }

    private static bool AssetOrderAlreadyMatches(UndertaleData data, string[] filteredAssetOrderLines)
    {
        var targetSections = ParseAssetOrderSections(filteredAssetOrderLines);
        foreach (var (section, targetNames) in targetSections)
        {
            if (section.Equals("counts", StringComparison.OrdinalIgnoreCase))
                continue;

            var currentNames = GetCurrentAssetOrderSection(data, section);
            if (currentNames == null || currentNames.Count != targetNames.Count)
                return false;

            for (int i = 0; i < targetNames.Count; i++)
            {
                if (!string.Equals(currentNames[i], targetNames[i], StringComparison.Ordinal))
                    return false;
            }
        }

        return true;
    }

    private static Dictionary<string, List<string>> ParseAssetOrderSections(string[] lines)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? currentSection = null;
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("@@") && line.EndsWith("@@") && line.Length > 4)
            {
                currentSection = line[2..^2];
                sections[currentSection] = [];
                continue;
            }

            if (currentSection == null || currentSection.Equals("counts", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(line))
                sections[currentSection].Add(line);
        }

        return sections;
    }

    private static List<string>? GetCurrentAssetOrderSection(UndertaleData data, string section) =>
        section.ToLowerInvariant() switch
        {
            "sounds" => [.. data.Sounds.Select(s => s?.Name?.Content ?? "")],
            "sprites" => [.. data.Sprites.Select(s => s?.Name?.Content ?? "")],
            "backgrounds" => [.. data.Backgrounds.Select(b => b?.Name?.Content ?? "")],
            "paths" => [.. data.Paths.Select(p => p?.Name?.Content ?? "")],
            "scripts" => [.. data.Scripts.Select(s => s?.Name?.Content ?? "")],
            "fonts" => [.. data.Fonts.Select(f => f?.Name?.Content ?? "")],
            "objects" => [.. data.GameObjects.Select(o => o?.Name?.Content ?? "")],
            "timelines" => [.. data.Timelines.Select(t => t?.Name?.Content ?? "")],
            "rooms" => [.. data.Rooms.Select(r => r?.Name?.Content ?? "")],
            "shaders" => [.. data.Shaders.Select(s => s?.Name?.Content ?? "")],
            "extensions" => [.. data.Extensions.Select(e => e?.Name?.Content ?? "")],
            "audiogroups" => [.. data.AudioGroups.Select(a => a?.Name?.Content ?? "")],
            _ => null
        };


    private static string[] SelectAssetOrderSections(
        string[] lines,
        HashSet<string> keepSections,
        params string[] skipSections)
    {
        var skip = new HashSet<string>(skipSections, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(lines.Length);
        bool inSelected = false;
        bool inCounts = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("@@") && line.EndsWith("@@") && line.Length > 4)
            {
                string sect = line[2..^2];
                inCounts = sect.Equals("counts", StringComparison.OrdinalIgnoreCase);
                inSelected = !skip.Contains(sect) && (inCounts || keepSections.Contains(sect));
                if (inSelected)
                    result.Add(line);
                continue;
            }

            if (!inSelected)
                continue;

            if (inCounts)
            {
                int eq = line.IndexOf('=');
                if (eq > 0)
                {
                    string countName = line[..eq];
                    if (skip.Contains(countName))
                        continue;
                    if (!keepSections.Contains(countName) &&
                        !countName.Equals("EmbeddedTextures", StringComparison.OrdinalIgnoreCase) &&
                        !countName.Equals("TexturePageItems", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
            }

            result.Add(line);
        }

        return [.. result];
    }

    private static void CopyEmbeddedTexturesForAssetOrder(PatchFileSystem pfs, string assetOrderDir)
    {
        string embeddedDir = Path.Combine(assetOrderDir, "EmbeddedTextures");
        foreach (var path in pfs.GetAllFilePaths())
        {
            string? sourcePrefix = null;
            if (path.StartsWith("EmbeddedTextures/", StringComparison.OrdinalIgnoreCase))
                sourcePrefix = "EmbeddedTextures/";
            else if (path.StartsWith($"{pfs.HelpersPrefix}/EmbeddedTextures/", StringComparison.OrdinalIgnoreCase))
                sourcePrefix = $"{pfs.HelpersPrefix}/EmbeddedTextures/";

            if (sourcePrefix == null)
                continue;

            string relative = path[sourcePrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relative))
                continue;

            string outputPath = Path.Combine(embeddedDir, relative);
            string? outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);
            File.WriteAllBytes(outputPath, pfs.ReadAllBytes(path));
        }
    }

    private static string? StageEmbeddedTexturesForAssetOrder(UndertaleData data, PatchFileSystem pfs)
    {
        bool hasEmbeddedTextures = false;
        foreach (var path in pfs.GetAllFilePaths())
        {
            if (path.StartsWith("EmbeddedTextures/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith($"{pfs.HelpersPrefix}/EmbeddedTextures/", StringComparison.OrdinalIgnoreCase))
            {
                hasEmbeddedTextures = true;
                break;
            }
        }

        if (!hasEmbeddedTextures)
            return null;

        string stageDir = Path.Combine(Path.GetTempPath(), $"g3mtool_embedded_stage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageDir);
        CopyEmbeddedTexturesForAssetOrder(pfs, stageDir);
        ExportMissingEmbeddedTexturesForAssetOrder(data, pfs, stageDir);
        return stageDir;
    }

    private static void ExportMissingEmbeddedTexturesForAssetOrder(UndertaleData data, PatchFileSystem pfs, string stageDir)
    {
        string embeddedDir = Path.Combine(stageDir, "EmbeddedTextures");
        Directory.CreateDirectory(embeddedDir);
        var touchedIndices = GetTouchedEmbeddedTextureIndices(pfs);
        bool gm2022_5 = data.IsVersionAtLeast(2022, 5);

        for (int i = 0; i < data.EmbeddedTextures.Count; i++)
        {
            if (touchedIndices.Contains(i))
                continue;

            var texture = data.EmbeddedTextures[i];
            if (texture == null)
                continue;
            var image = texture.TextureData?.Image;
            if (image == null)
                continue;

            string textureName = $"texture_{i:D4}";
            string textureDir = Path.Combine(embeddedDir, textureName);
            string jsonPath = Path.Combine(textureDir, textureName + ".json");
            string binPath = Path.Combine(textureDir, textureName + ".bin");
            if (File.Exists(jsonPath) || File.Exists(binPath))
                continue;

            Directory.CreateDirectory(textureDir);
            File.WriteAllBytes(binPath, image.ToSpan(gm2022_5).ToArray());
            var meta = new Dictionary<string, object>
            {
                ["index"] = i,
                ["name"] = texture.Name?.Content ?? "",
                ["scaled"] = texture.Scaled,
                ["generatedMips"] = texture.GeneratedMips,
                ["format"] = image.Format.ToString()
            };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(meta, s_jsonOptions), Encoding.UTF8);
        }
    }

    private static void CopyStagedEmbeddedTexturesForAssetOrder(string stagedDir, string assetOrderDir)
    {
        string sourceRoot = Path.Combine(stagedDir, "EmbeddedTextures");
        if (!Directory.Exists(sourceRoot))
            return;

        string targetRoot = Path.Combine(assetOrderDir, "EmbeddedTextures");
        foreach (var file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceRoot, file);
            string outputPath = Path.Combine(targetRoot, relative);
            string? outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);
            File.Copy(file, outputPath, true);
        }
    }
}

public class PatchCreateResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public PatchStatistics? Statistics { get; set; }
}

public class PatchApplyResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class PatchValidateResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public G3MPatchManifest? Manifest { get; set; }
}
