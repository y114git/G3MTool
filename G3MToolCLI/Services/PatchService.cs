using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using G3MToolCLI.Models;
using G3MToolCLI.Utils;
using Underanalyzer.Decompiler;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;

namespace G3MToolCLI.Services;

public partial class PatchService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public static async Task<PatchCreateResult> CreatePatchAsync(
        string originalPath,
        string modifiedPath,
        string outputPath,
        Dictionary<string, Dictionary<string, string>>? precomputedOriginalHashes = null,
        DataFileInfo? precomputedOriginalInfo = null,
        bool includeXdeltaFallback = false)
    {
        if (!File.Exists(originalPath))
            return new PatchCreateResult { Success = false, Error = $"Original file not found: {originalPath}" };

        if (!File.Exists(modifiedPath))
            return new PatchCreateResult { Success = false, Error = $"Modified file not found: {modifiedPath}" };

        // If modifiedPath is an xdelta file, apply it first to get the actual modified .win
        string? tempXdeltaResult = null;
        string? tempExactPatch = null;
        string? exactPatchSourcePath = null;
        string originalModifiedInputPath = modifiedPath;
        if (Path.GetExtension(modifiedPath).Equals(".xdelta", StringComparison.OrdinalIgnoreCase))
        {
            LogService.Log("[PatchService] Detected xdelta file, applying to original first...");
            exactPatchSourcePath = originalModifiedInputPath;

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
            if (precomputedOriginalHashes != null && precomputedOriginalInfo != null)
            {
                originalHashes = precomputedOriginalHashes;
                originalInfo = precomputedOriginalInfo;
                LogService.Log("[PatchService] Using precomputed original hashes (shared)");
            }
            else
            {
                phaseSw.Restart();
                LogService.Log("[PatchService] Loading original data file...");
                using (var stream = new FileStream(originalPath, FileMode.Open, FileAccess.Read))
                {
                    var originalData = UndertaleIO.Read(stream);
                    var loadTime = phaseSw.Elapsed;
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
                    originalHashes = ResourceHashService.HashAll(originalData);
                    LogService.Log($"[Timing] Original load: {loadTime.TotalSeconds:F1}s, hash: {phaseSw.Elapsed.TotalSeconds:F1}s, peak RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
                }
                phaseSw.Restart();
                GC.Collect();
                LogService.Log($"[Timing] GC after original: {phaseSw.Elapsed.TotalMilliseconds:F0}ms, RAM after GC: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
            }
            LogService.Progress(20, 100);

            // --- Load modified, extract manifest info, hash + export, release ---
            phaseSw.Restart();
            LogService.Log("[PatchService] Loading modified data file...");
            DataFileInfo modifiedInfo;
            Dictionary<string, Dictionary<string, string>> modifiedHashes;
            Dictionary<string, HashSet<string>> changedNamesPerType = [];
            Dictionary<string, (string? gml, string? asm, Dictionary<string, string>? childAsms)> codeEntriesInMemory = [];
            using (var stream = new FileStream(modifiedPath, FileMode.Open, FileAccess.Read))
            {
                var modifiedData = UndertaleIO.Read(stream);
                var loadTime = phaseSw.Elapsed;
                LogService.Log($"[PatchService] Modified: {modifiedData.GeneralInfo?.DisplayName?.Content ?? "Unknown"}");
                modifiedInfo = new DataFileInfo
                {
                    Filename = Path.GetFileName(modifiedPath),
                    Size = new FileInfo(modifiedPath).Length,
                    Md5 = await HashService.ComputeFileHashAsync(modifiedPath),
                    BytecodeVersion = modifiedData.GeneralInfo?.BytecodeVersion ?? 0,
                    GmsVersion = GeneralInfoUtil.GetVersionDisplay(modifiedData.GeneralInfo),
                    GeneralInfo = GeneralInfoUtil.ExtractGeneralInfo(modifiedData)
                };

                var compatibilityError = GetCreateCompatibilityError(originalInfo, modifiedInfo);
                if (compatibilityError != null)
                    return new PatchCreateResult { Success = false, Error = compatibilityError };

                // Hash modified resources in memory (fast comparison)
                phaseSw.Restart();
                LogService.Log("[PatchService] Hashing modified resources in memory...");
                modifiedHashes = ResourceHashService.HashAll(modifiedData);
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

                // Export code entries to MEMORY (no disk I/O - direct to archive later)
                var changedCodeNames = changedNamesPerType.GetValueOrDefault("CodeEntries") ?? [];
                var codeExportSw = Stopwatch.StartNew();
                codeEntriesInMemory = ResourceExportService.ExportCodeEntriesToMemory(modifiedData, changedCodeNames);
                codeExportSw.Stop();
                LogService.Log($"[Export Timing] CodeEntries (in-memory {changedCodeNames.Count}): {codeExportSw.Elapsed.TotalSeconds:F1}s");
                var exportTime = phaseSw.Elapsed;

                // Export asset order + helpers while data is still in memory
                phaseSw.Restart();
                var helpersExportDir = Path.Combine(tempDir, "Helpers");
                ResourceExportService.ExportAssetOrder(modifiedData, helpersExportDir);
                LogService.Log($"[Timing] Modified load: {loadTime.TotalSeconds:F1}s, hash: {hashTime.TotalSeconds:F1}s, export: {exportTime.TotalSeconds:F1}s, helpers: {phaseSw.Elapsed.TotalSeconds:F1}s, peak RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
            }
            phaseSw.Restart();
            GC.Collect();
            LogService.Log($"[Timing] GC after modified: {phaseSw.Elapsed.TotalMilliseconds:F0}ms, RAM after GC: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
            LogService.Progress(60, 100);

            var manifest = new G3MPatchManifest
            {
                Version = "1.0",
                CreatedAt = DateTime.UtcNow.ToString("o"),
                Tool = new Models.ToolInfo { Name = "G3MTool", Version = "1.0.0" },
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

                    var forcedExportNames = changedNamesPerType.GetValueOrDefault(resourceType);
                    var changes = CompareHashDictionaries(
                        origTypeHashes,
                        modTypeHashes,
                        modifiedExportDir,
                        resourceType,
                        forcedExportNames);
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

                // Create output directory
                var outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);

                // Create patch zip with changed resources
                phaseSw.Restart();
                LogService.Log("[PatchService] Creating patch archive...");
                using (var zipStream = new FileStream(outputPath, FileMode.Create))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    // Add manifest
                    var manifestEntry = archive.CreateEntry("g3mpatch.json", CompressionLevel.NoCompression);
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

                    // CodeEntries: write directly from memory to archive (no disk I/O)
                    if (codeEntriesInMemory.Count > 0)
                    {
                        foreach (var (codeName, (gml, asm, childAsms)) in codeEntriesInMemory)
                        {
                            if (gml != null)
                            {
                                var gmlEntry = archive.CreateEntry($"CodeEntries/{codeName}/{codeName}.gml", CompressionLevel.NoCompression);
                                using var gmlStream = new StreamWriter(gmlEntry.Open(), Encoding.UTF8);
                                await gmlStream.WriteAsync(gml);
                            }
                            if (asm != null)
                            {
                                var asmEntry = archive.CreateEntry($"CodeEntries/{codeName}/{codeName}.asm", CompressionLevel.NoCompression);
                                using var asmStream = new StreamWriter(asmEntry.Open(), Encoding.UTF8);
                                await asmStream.WriteAsync(asm);
                            }
                            if (childAsms != null)
                            {
                                foreach (var (childName, childAsm) in childAsms)
                                {
                                    var childEntry = archive.CreateEntry($"CodeEntries/{codeName}/{childName}.asm", CompressionLevel.NoCompression);
                                    using var childStream = new StreamWriter(childEntry.Open(), Encoding.UTF8);
                                    await childStream.WriteAsync(childAsm);
                                }
                            }
                        }
                        LogService.Log($"[PatchService] CodeEntries: {codeEntriesInMemory.Count} written directly to archive (no disk I/O)");
                    }

                // Add all exported helper files into Helpers/ folder in ZIP
                // (already exported by ResourceExportService.ExportAssetOrder during modified data phase)
                var helpersDir = Path.Combine(tempDir, "Helpers");
                if (Directory.Exists(helpersDir))
                    {
                        foreach (var file in Directory.GetFiles(helpersDir))
                        {
                            string fileName = Path.GetFileName(file);
                            var entry = archive.CreateEntry($"Helpers/{fileName}", CompressionLevel.NoCompression);
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
                            CompressionLevel.NoCompression
                        );
                        using var exactStream = exactEntry.Open();
                        using var exactFile = File.OpenRead(exactPatchSourcePath);
                        await exactFile.CopyToAsync(exactStream);
                    }
                }

                LogService.Log($"[Timing] Archiving: {phaseSw.Elapsed.TotalSeconds:F1}s");
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
    /// This prevents merged semantic patches from leaving a small number of untouched resources
    /// in an older on-disk format when the patch upgrades the target game version.
    /// </summary>
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
            var folderName = folderMap.GetValueOrDefault(name) ?? name;
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
                var folderName = folderMap.GetValueOrDefault(name) ?? name;
                changes.Changed.Add(new ResourceChange { Name = folderName });
            }
        }

        return changes;
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

                var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
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

            var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(file);
            await fileStream.CopyToAsync(entryStream);
        }
    }

    public static async Task<PatchApplyResult> ApplyPatchAsync(
        string dataPath,
        string patchPath,
        string outputPath)
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

            LogService.SetOperation("Applying patch");
            LogService.Progress(0, 100);

            // Load patch ZIP first. Xdelta payloads are used only as a fallback after semantic apply fails.
            phaseSw.Restart();
            LogService.Log("[PatchService] Loading patch ZIP into memory...");
            var pfs = await Task.Run(() => PatchFileSystem.LoadFromZip(patchPath));
            G3MPatchManifest? manifest = pfs.Manifest;

            pfsForFallback = pfs;
            manifestForFallback = manifest;

            phaseSw.Restart();
            LogService.Log("[PatchService] Loading data file for semantic patch application...");
            UndertaleData data;
            using (var stream = new FileStream(dataPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            LogService.Log($"[Timing] Data load (semantic path): {phaseSw.Elapsed.TotalSeconds:F1}s, RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
            LogService.Progress(5, 100);

            LogService.Log($"[PatchService] Data loaded: {data.GeneralInfo?.DisplayName?.Content ?? "Unknown"}");

            // Set PatchFileSystem for all native importers
            ResourceImportService.SetPatchFileSystem(pfs);

            try
            {
                var importOrder = ResourceTypeRegistry.ImportOrder;
                var existingFolders = pfs.GetResourceTypes();

                var resourceTypesToProcess = new List<string>(importOrder.Length);
                foreach (var rt in importOrder)
                {
                    if (existingFolders.Contains(rt))
                        resourceTypesToProcess.Add(rt);
                }

                if (resourceTypesToProcess.Remove("GeneralInfo"))
                    resourceTypesToProcess.Insert(0, "GeneralInfo");

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

                var helpersDir = pfs.HelpersPrefix;

                foreach (var resourceType in resourceTypesToProcess)
                {
                    // Reorder assets to match TARGET indices before code compilation.
                    if (resourceType == "CodeEntries")
                    {
                        var assetOrderFile = Path.Combine(helpersDir, "asset_order.txt");
                        if (pfs.FileExists(assetOrderFile))
                        {
                            // Create missing GameObjects so TARGET indices resolve correctly.
                            var aoLines = pfs.ReadAllLines(assetOrderFile);
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
                                // Reorder with asset_order.txt only (exclude TPI/frame data).
                                var reorderDir = Path.Combine(Path.GetTempPath(), $"g3mtool_aoreorder_{Guid.NewGuid():N}");
                                Directory.CreateDirectory(reorderDir);
                                // Exclude TexturePageItems/EmbeddedTextures sections
                                var filteredAoLines = FilterAssetOrderSections(
                                    aoLines,
                                    "TexturePageItems", "EmbeddedTextures");
                                File.WriteAllLines(Path.Combine(reorderDir, "asset_order.txt"), filteredAoLines);
                                var objEventsPath = Path.Combine(helpersDir, "object_events.json");
                                if (pfs.FileExists(objEventsPath))
                                    File.WriteAllBytes(Path.Combine(reorderDir, "object_events.json"), pfs.ReadAllBytes(objEventsPath));

                                ResourceImportService.SetPatchFileSystem(null);
                                ResourceImportService.ImportAssetOrder(data, reorderDir);
                                ResourceImportService.SetPatchFileSystem(pfs);
                                appliedCount++;

                                try { Directory.Delete(reorderDir, true); } catch { }
                            }
                            catch (Exception ex)
                            {
                                LogService.Log($"[PatchService] Asset reorder warning: {ex.Message}");
                            }
                        }
                        LogService.Progress(45, 100);
                    }

                    // Import CodeEntries: GML compilation + ASM reassembly
                    if (resourceType == "CodeEntries")
                    {
                        // Read variables_functions.json from PFS before releasing file data
                        string? vfContent = null;
                        var vfPath = Path.Combine(helpersDir, "variables_functions.json");
                        if (pfs.FileExists(vfPath))
                            vfContent = pfs.ReadAllText(vfPath);

                        // Release PFS file data (no longer needed after non-code imports)
                        ResourceImportService.SetPatchFileSystem(null);
                        pfs.ReleaseFileData();
                        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                        LogService.Log($"[PatchService] Released PFS file data, RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");

                        LogService.Log($"[PatchService] Applying CodeEntries via hybrid import (in-memory)...");
                        phaseSw.Restart();

                        try
                        {
                            LogService.Log($"[PatchService] Using {pfs.GmlEntries.Count} GML + {pfs.AsmEntries.Count} ASM entries from PFS");
                            ImportCodeEntriesDirect(data, pfs.GmlEntries, pfs.AsmEntries, helpersDir, vfContent);
                            appliedCount++;
                        }
                        catch (Exception ex)
                        {
                            LogService.Warning($"ERROR applying CodeEntries: {ex.Message}");
                            failedCount++;
                        }

                        // Release code entries after import
                        pfs.ReleaseCodeEntries();
                        GC.Collect(2, GCCollectionMode.Aggressive, true, true);

                        LogService.Log($"[Timing] Import CodeEntries: {phaseSw.Elapsed.TotalSeconds:F1}s, RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");

                        LogService.Progress(90, 100);
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
                            var filteredLines = FilterAssetOrderSections(
                                pfs.ReadAllLines(reorderAssetOrderFile),
                                "TexturePageItems", "EmbeddedTextures");
                            File.WriteAllLines(Path.Combine(reorderOnlyDir, "asset_order.txt"), filteredLines);

                            LogService.Log("[PatchService] Re-ordering assets after sprite import...");
                            ResourceImportService.SetPatchFileSystem(null);
                            try
                            {
                                ResourceImportService.ImportAssetOrder(data, reorderOnlyDir);
                            }
                            catch (Exception ex)
                            {
                                LogService.Log($"[PatchService] Post-sprite reorder warning: {ex.Message}");
                            }
                            ResourceImportService.SetPatchFileSystem(pfs);

                            try { Directory.Delete(reorderOnlyDir, true); } catch { }
                        }
                    }

                    nonCodeProgress += resourceWeights.GetValueOrDefault(resourceType, 1);
                    LogService.Progress(nonCodeRangeStart + nonCodeRange * nonCodeProgress / Math.Max(totalNonCodeWeight, 1), 100);
                }

                RepairDanglingCodeReferences(data);

                // Save modified data
                phaseSw.Restart();
                LogService.Log("[PatchService] Saving modified data file...");
                var outDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                    Directory.CreateDirectory(outDir);
                using (var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    UndertaleIO.Write(outStream, data);
                }
                LogService.Log($"[Timing] Final save: {phaseSw.Elapsed.TotalSeconds:F1}s, RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
                LogService.Progress(100, 100);

                LogService.ProgressComplete();

                totalSw.Stop();
                LogService.Log($"[Timing] === PATCH APPLY TOTAL: {totalSw.Elapsed.TotalSeconds:F1}s === RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");

                string? expectedModifiedHash = manifest?.Modified?.Md5;
                if (!string.IsNullOrWhiteSpace(expectedModifiedHash))
                {
                    string actualModifiedHash = await HashService.ComputeFileHashAsync(outputPath);
                    if (!actualModifiedHash.Equals(expectedModifiedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        LogService.Warning("[PatchService] Semantic output hash differs from the patch's modified file hash");
                        if (await TryApplyXdeltaFallbackAsync(dataPath, outputPath, pfs, manifest))
                            return new PatchApplyResult { Success = true };
                    }
                }

                if (appliedCount == 0)
                {
                    if (await TryApplyXdeltaFallbackAsync(dataPath, outputPath, pfs, manifest))
                        return new PatchApplyResult { Success = true };
                    return new PatchApplyResult { Success = false, Error = "No resources were applied successfully" };
                }

                if (failedCount > 0)
                    LogService.Warning($"Patch applied with warnings: {appliedCount} succeeded, {failedCount} failed");

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
                await TryApplyXdeltaFallbackAsync(dataPath, outputPath, pfsForFallback, manifestForFallback))
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
                replacement = data.Code.ByName(currentCodeName);

            if (replacement == null && script.Name?.Content is string scriptName && !string.IsNullOrEmpty(scriptName))
            {
                replacement = data.Code.ByName("gml_Script_" + scriptName)
                    ?? data.Code.ByName("gml_GlobalScript_" + scriptName);
            }

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

    /// <summary>
    /// Import code entries: GML compilation for structures, ASM reassembly for byte-perfect bytecode.
    /// </summary>
    private static void ImportCodeEntriesDirect(
        UndertaleData data,
        Dictionary<string, string> gmlEntries,
        Dictionary<string, string> asmEntries,
        string helpersDir,
        string? vfContentOverride = null)
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

        // === Phase 1: Queue all GML entries for compilation ===
        phaseSw.Restart();

        // Snapshot existing code entries to detect compiler side-effects later
        var preCompileEntries = new HashSet<string>(data.Code.Count);
        foreach (var c in data.Code)
        {
            if (c?.Name?.Content != null)
                preCompileEntries.Add(c.Name.Content);
        }

        var ctx = new GlobalDecompileContext(data);
        ctx.PrepareForCompilation(true);

        var importGroup = new CodeImportGroup(data, ctx)
        {
            AutoCreateAssets = true
        };

        // O(1) lookups for code entries and game objects
        var codeLookup = new Dictionary<string, UndertaleCode>(data.Code.Count);
        foreach (var c in data.Code)
        {
            if (c?.Name?.Content != null)
                codeLookup.TryAdd(c.Name.Content, c);
        }
        var goLookup = new Dictionary<string, (UndertaleGameObject obj, int idx)>(data.GameObjects.Count);
        for (int i = 0; i < data.GameObjects.Count; i++)
        {
            var go = data.GameObjects[i];
            if (go?.Name?.Content != null)
                goLookup.TryAdd(go.Name.Content, (go, i));
        }

        int regularCount = 0;
        int collisionCount = 0;

        foreach (var (codeName, gmlCode) in gmlEntries)
        {
            if (codeName.Contains("_Collision_"))
            {
                try
                {
                    ImportCollisionEvent(data, codeName, gmlCode, importGroup, codeLookup, goLookup);
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
                    if (codeLookup.TryGetValue(codeName, out var existing) && existing.ParentEntry == null)
                        importGroup.QueueReplace(existing, gmlCode);
                    else
                        importGroup.QueueReplace(codeName, gmlCode);
                    regularCount++;
                }
                catch (Exception ex)
                {
                    LogService.Log($"[ImportCodeEntries] Skipping {codeName}: {ex.Message}");
                }
            }
        }

        LogService.Log($"[ImportCodeEntries] Queued {regularCount} regular + {collisionCount} collision entries in {phaseSw.Elapsed.TotalSeconds:F1}s");

        // === Phase 2: Delete code entries not present in TARGET ===
        phaseSw.Restart();
        var targetEntryNames = new HashSet<string>();
        if (root.TryGetProperty("codeEntries", out var ceArray))
        {
            foreach (var ce in ceArray.EnumerateArray())
            {
                string? ceName = ce.GetString();
                if (!string.IsNullOrEmpty(ceName))
                    targetEntryNames.Add(ceName);
            }
        }

        if (targetEntryNames.Count > 0)
        {
            var entriesToDelete = new List<UndertaleCode>();
            foreach (var c in data.Code)
            {
                if (c?.Name?.Content != null && c.ParentEntry == null && !targetEntryNames.Contains(c.Name.Content))
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
        LogService.Log("[ImportCodeEntries] Taking event snapshot before compilation...");

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
        var childEntrySnapshot = new Dictionary<string, (UndertaleCode code, UndertaleCodeLocals? locals, UndertaleScript? script)>();
        foreach (var c in data.Code)
        {
            if (c?.Name?.Content != null && c.ParentEntry != null)
            {
                scriptByCode.TryGetValue(c, out var script);
                codeLocalsByName.TryGetValue(c.Name, out var locals);
                childEntrySnapshot[c.Name.Content] = (c, locals, script);
            }
        }

        var eventSnapshot = new Dictionary<string, List<(int evtType, uint subtype, string codeName)>>();
        foreach (var obj in data.GameObjects)
        {
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
            eventSnapshot[obj.Name.Content] = events;
        }

        // Compile everything
        LogService.Log($"[ImportCodeEntries] Compiling {regularCount + collisionCount} code entries...");
        try
        {
            importGroup.Import();
        }
        catch (Exception ex)
        {
            LogService.Log($"[ImportCodeEntries] Compilation warning: {ex.Message}");
        }

        // Restore child entries removed by the compiler. Detach from parent to make standalone.
        {
            var currentEntries = new HashSet<string>(data.Code.Count);
            foreach (var c in data.Code)
            {
                if (c?.Name?.Content != null)
                    currentEntries.Add(c.Name.Content);
            }
            var currentScripts = new HashSet<string>(data.Scripts.Count);
            foreach (var s in data.Scripts)
            {
                if (s?.Name?.Content != null)
                    currentScripts.Add(s.Name.Content);
            }
            int restored = 0;
            foreach (var (name, (snapCode, snapLocals, snapScript)) in childEntrySnapshot)
            {
                if (!currentEntries.Contains(name))
                {
                    snapCode.ParentEntry = null;
                    snapCode.Offset = 0;
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
                        LogService.Log($"  Restored detached child + script: {name}");
                    }
                    else
                    {
                        LogService.Log($"  Restored detached child: {name}");
                    }
                    restored++;
                }
            }
            if (restored > 0)
                LogService.Log($"[ImportCodeEntries] Restored {restored} child entries as standalone (detached from recompiled parents)");
        }

        // Remove spurious entries created as compiler side effects.
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
        {
            int linkedScripts = 0;
            foreach (var script in data.Scripts)
            {
                if (script?.Name?.Content != null && script.Code == null)
                {
                    var code = data.Code.ByName("gml_Script_" + script.Name.Content)
                            ?? data.Code.ByName("gml_GlobalScript_" + script.Name.Content);
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
        foreach (var obj in data.GameObjects)
        {
            if (obj?.Name?.Content == null) continue;
            if (!eventSnapshot.TryGetValue(obj.Name.Content, out var snapshot)) continue;

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
        if (restoredEvents > 0)
            LogService.Log($"[ImportCodeEntries] Removed {restoredEvents} spurious events added during compilation");

        // Phase 4b: Event cleanup from TARGET's object_events.json
        string objEventsPath = Path.Combine(helpersDir, "object_events.json");
        if (File.Exists(objEventsPath))
        {
            var targetEventsRoot = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(objEventsPath));
            int authRemoved = 0;

            foreach (var obj in data.GameObjects)
            {
                if (obj?.Name?.Content == null) continue;
                if (!targetEventsRoot.TryGetProperty(obj.Name.Content, out var targetEvents)) continue;

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
                            continue;
                        }
                    }
                    targetEventKeys.Add($"{t}_{evt.GetProperty("s").GetUInt32()}");
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
        LogService.Log($"[ImportCodeEntries] Event cleanup done in {phaseSw.Elapsed.TotalSeconds:F1}s");

        // === Phase 5: Remove functions not in TARGET ===
        if (root.TryGetProperty("functions", out var cleanupFuncArray))
        {
            var targetFunctions = new HashSet<string>();
            foreach (var f in cleanupFuncArray.EnumerateArray())
            {
                string? fname = f.GetString();
                if (!string.IsNullOrEmpty(fname))
                    targetFunctions.Add(fname);
            }

            var oursCodeNames = new HashSet<string>(data.Code.Count);
            foreach (var c in data.Code)
            {
                if (c?.Name?.Content != null)
                    oursCodeNames.Add(c.Name.Content);
            }

            int removedFuncs = 0;
            for (int i = data.Functions.Count - 1; i >= 0; i--)
            {
                string? funcName = data.Functions[i].Name?.Content;
                if (!string.IsNullOrEmpty(funcName) && !targetFunctions.Contains(funcName) && !oursCodeNames.Contains(funcName))
                {
                    LogService.Log($"  Removing function: {funcName}");
                    data.Functions.RemoveAt(i);
                    removedFuncs++;
                }
            }
            LogService.Log($"[ImportCodeEntries] Functions cleanup: removed {removedFuncs}");
        }
        // === Phase 5b: Add missing variables from TARGET ===
        if (root.TryGetProperty("variables", out var varArray))
        {
            var existingVars = new HashSet<(string, int)>();
            foreach (var v in data.Variables)
            {
                if (v?.Name?.Content != null)
                    existingVars.Add((v.Name.Content, (int)v.InstanceType));
            }

            int addedVars = 0;
            foreach (var vElem in varArray.EnumerateArray())
            {
                string name = vElem.GetProperty("n").GetString() ?? "";
                int instType = vElem.GetProperty("t").GetInt32();
                int varId = vElem.GetProperty("id").GetInt32();
                if (string.IsNullOrEmpty(name)) continue;

                if (!existingVars.Contains((name, instType)))
                {
                    data.Variables.Add(new UndertaleVariable
                    {
                        Name = data.Strings.MakeString(name),
                        InstanceType = (UndertaleInstruction.InstanceType)instType,
                        VarID = varId
                    });
                    existingVars.Add((name, instType));
                    addedVars++;
                }
            }
            if (addedVars > 0)
                LogService.Log($"[ImportCodeEntries] Added {addedVars} missing variables from TARGET export");
        }

        // === Phase 6: ASM reassembly ===
        phaseSw.Restart();
        LogService.Log("[ImportCodeEntries] Reassembling from ASM for byte-perfect bytecode...");

        // Build local variable index lookup
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

        var stripStringIdxRegex = StripStringIdxRegex();
        int reassembled = 0;
        int asmFailed = 0;

        // Rebuild code lookup (data.Code changed during compilation)
        codeLookup.Clear();
        foreach (var c in data.Code)
        {
            if (c?.Name?.Content != null)
                codeLookup.TryAdd(c.Name.Content, c);
        }

        // Enable lookup caches in Assembler
        Assembler.SetLookupCaches(data);
        try
        {

            foreach (var (codeName, asmText) in asmEntries)
            {
                if (!codeLookup.TryGetValue(codeName, out var code)) continue;

                try
                {
                    // Step 1: Extract TARGET child names from > directives
                    var targetChildNames = new List<string>();
                    using (var sr = new StringReader(asmText))
                    {
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
                    }

                    // Step 2: Build child name mapping (TARGET name -> OURS name)
                    var nameMap = new Dictionary<string, string>();
                    if (targetChildNames.Count > 0)
                    {
                        var oursChildren = code.ChildEntries.OrderBy(c => c.Offset).ToList();
                        if (targetChildNames.Count != oursChildren.Count)
                            throw new Exception($"Child count mismatch: ASM has {targetChildNames.Count}, compiled has {oursChildren.Count}");

                        for (int ci = 0; ci < targetChildNames.Count; ci++)
                        {
                            string tName = targetChildNames[ci];
                            string? oName = oursChildren[ci].Name?.Content;
                            if (!string.IsNullOrEmpty(oName) && tName != oName)
                                nameMap[tName] = oName;
                        }
                    }

                    // Step 3: Preprocess assembly text
                    var sb = new StringBuilder(asmText.Length);
                    using (var sr = new StringReader(asmText))
                    {
                        string? ln;
                        while ((ln = sr.ReadLine()) != null)
                        {
                            string trimmed = ln.Trim();

                            // Remap .localvar VARI indices
                            if (trimmed.StartsWith(".localvar"))
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
                            if (trimmed.StartsWith("push.s "))
                                ln = stripStringIdxRegex.Replace(ln, "\"");

                            // Remap child entry names in > directives and function references
                            if (nameMap.Count > 0)
                            {
                                foreach (var kvp in nameMap)
                                {
                                    if (ln.Contains(kvp.Key))
                                        ln = ln.Replace(kvp.Key, kvp.Value);
                                }
                            }

                            sb.AppendLine(ln);
                        }
                    }

                    // Step 4: Assemble
                    var newInstructions = Assembler.Assemble(sb.ToString(), data);

                    // Step 5: Replace instructions
                    code.Instructions.Clear();
                    foreach (var instr in newInstructions)
                        code.Instructions.Add(instr);

                    // Update code length
                    uint totalWords = 0;
                    foreach (var instr in newInstructions)
                        totalWords += instr.CalculateInstructionSize();
                    code.Length = totalWords * 4;

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

        if (asmFailed > 5)
            LogService.Log($"[ImportCodeEntries] ... and {asmFailed - 5} more ASM errors (suppressed)");
        LogService.Log($"[ImportCodeEntries] Reassembled {reassembled}/{reassembled + asmFailed} entries in {phaseSw.Elapsed.TotalSeconds:F1}s");

        sw.Stop();
        LogService.Log($"[ImportCodeEntries] Hybrid import complete in {sw.Elapsed.TotalSeconds:F1}s");
    }

    [GeneratedRegex(@"""@\d+")]
    private static partial Regex StripStringIdxRegex();

    /// <summary>
    /// Import a collision event: find or create the event, then queue GML for compilation.
    /// </summary>
    private static void ImportCollisionEvent(
        UndertaleData data, string codeName, string gmlCode, CodeImportGroup importGroup,
        Dictionary<string, UndertaleCode> codeLookup,
        Dictionary<string, (UndertaleGameObject obj, int idx)> goLookup)
    {
        const string prefix = "gml_Object_";
        if (!codeName.StartsWith(prefix)) return;

        int collisionIdx = codeName.LastIndexOf("_Collision_");
        if (collisionIdx < 0) return;

        string objectName = codeName[prefix.Length..collisionIdx];
        string identifier = codeName[(collisionIdx + "_Collision_".Length)..];

        if (!goLookup.TryGetValue(objectName, out var objEntry)) return;
        var obj = objEntry.obj;

        if (!uint.TryParse(identifier, out uint collisionIndex))
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

    public static async Task<PatchValidateResult> ValidatePatchAsync(string patchPath, string? dataPath = null)
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
                var dataHash = await HashService.ComputeFileHashAsync(dataPath);
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
        DataFileInfo? precomputedOriginalInfo = null)
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
            includeXdeltaFallback: false
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
