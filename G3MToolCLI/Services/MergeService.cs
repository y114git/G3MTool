using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using G3MToolCLI.Models;
using G3MToolCLI.Utils;
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
    private sealed record ConflictEntry(
        string File, string Status, string Strategy, string Winner, string? Details = null, string? Diff = null);

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
        //           65-75 helpers, 75-80 manifest, 80-88 save ZIP, 88-98 apply, 98-100 report
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
            using (var stream = new FileStream(originalPath, FileMode.Open, FileAccess.Read))
                originalData = UndertaleIO.Read(stream);

            var originalAssetOrder = ExtractAssetOrder(originalData);
            int originalTpiCount = originalData.TexturePageItems?.Count ?? 0;

            // Pre-compute original hashes + info ONCE (shared across all normalizations)
            var origHashSw = Stopwatch.StartNew();
            var sharedOriginalHashes = ResourceHashService.HashAll(originalData);
            var sharedOriginalInfo = new DataFileInfo
            {
                Filename = Path.GetFileName(originalPath),
                Size = new FileInfo(originalPath).Length,
                Md5 = await HashService.ComputeFileHashAsync(originalPath),
                BytecodeVersion = originalData.GeneralInfo?.BytecodeVersion ?? 0,
                GmsVersion = GeneralInfoUtil.GetVersionDisplay(originalData.GeneralInfo),
                GeneralInfo = GeneralInfoUtil.ExtractGeneralInfo(originalData)
            };

            LogService.Log($"[Bench] Load + hash original: {stepSw.Elapsed.TotalSeconds:F2}s " +
                $"(hash: {origHashSw.Elapsed.TotalSeconds:F2}s, {originalData.GeneralInfo?.DisplayName?.Content ?? "?"})");
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
                    try { return await PatchService.EnsureG3MPatchAsync(originalPath, path, null, sharedOriginalHashes, sharedOriginalInfo); }
                    finally { normSemaphore.Release(); }
                });
            }

            var zipPaths = await Task.WhenAll(normalizeTasks);

            LogService.Suppress = false;
            LogService.SetOperation("Merging");

            for (int i = 0; i < patchPaths.Count; i++)
            {
                if (zipPaths[i] != patchPaths[i]) tempFiles.Add(zipPaths[i]);
                patchFileSystems[i] = PatchFileSystem.LoadFromZip(zipPaths[i]);
                LogService.Log($"[Bench] Patch {i + 1}/{patchPaths.Count} ({patchNames[i]}): " +
                    $"{patchFileSystems[i].FileCount} files + {patchFileSystems[i].GmlEntries.Count} GML + {patchFileSystems[i].AsmEntries.Count} ASM");
            }

            LogService.Log($"[Bench] All patches normalized (parallel): {stepSw.Elapsed.TotalSeconds:F2}s");
            LogService.Progress(15, 100);

            // Prepare decompiler for 3-way code merge
            GlobalDecompileContext? decompileCtx = null;
            var decompileCache = new Dictionary<string, string?>();
            if (options.UseCodeMerge)
                decompileCtx = new GlobalDecompileContext(originalData);

            // ══════════════════════════════════════════════════════════
            // Pre-compute sprite index remaps (patch order → merged order)
            // so hardcoded sprite indices in GML/ASM code are corrected.
            // ══════════════════════════════════════════════════════════
            var originalSpriteNames = originalAssetOrder.GetValueOrDefault("sprites") ?? [];

            // Build merged sprite order: original + new from each patch (mirrors MergeAssetOrders)
            var mergedSpriteNames = new List<string>(originalSpriteNames);
            var mergedSpriteSet = new HashSet<string>(originalSpriteNames);
            var patchSpriteLists = new List<string>[patchFileSystems.Length];

            for (int pi = 0; pi < patchFileSystems.Length; pi++)
            {
                var pfs = patchFileSystems[pi];
                var aoPath = $"{pfs.HelpersPrefix}/asset_order.txt";
                if (!pfs.FileExists(aoPath)) { patchSpriteLists[pi] = []; continue; }
                var (sections, _) = ParseAssetOrderText(pfs.ReadAllText(aoPath));
                patchSpriteLists[pi] = sections.GetValueOrDefault("sprites") ?? [];

                foreach (var name in patchSpriteLists[pi])
                {
                    if (!string.IsNullOrWhiteSpace(name) && name != "(null)" && mergedSpriteSet.Add(name))
                        mergedSpriteNames.Add(name);
                }
            }

            // Build merged sprite index lookup
            var mergedSpriteIndex = new Dictionary<string, int>(mergedSpriteNames.Count, StringComparer.Ordinal);
            for (int i = 0; i < mergedSpriteNames.Count; i++)
                mergedSpriteIndex[mergedSpriteNames[i]] = i;

            // Build per-patch remaps (only entries where patchIndex != mergedIndex)
            var patchSpriteRemaps = new Dictionary<int, int>[patchFileSystems.Length];
            for (int pi = 0; pi < patchFileSystems.Length; pi++)
            {
                var remap = new Dictionary<int, int>();
                var patchList = patchSpriteLists[pi];
                for (int i = 0; i < patchList.Count; i++)
                {
                    var name = patchList[i];
                    if (string.IsNullOrWhiteSpace(name) || name == "(null)") continue;
                    // Skip if same sprite at same position as original (no shift)
                    if (i < originalSpriteNames.Count && name == originalSpriteNames[i])
                        continue;
                    if (mergedSpriteIndex.TryGetValue(name, out int mergedIdx) && mergedIdx != i)
                        remap[i] = mergedIdx;
                }
                patchSpriteRemaps[pi] = remap;
                if (remap.Count > 0)
                    LogService.Log($"[MergeService] Sprite index remap for '{patchNames[pi]}': {remap.Count} indices shifted");
            }

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

                    if (existingData.Length == fileData.Length &&
                        MD5.HashData(existingData).AsSpan().SequenceEqual(MD5.HashData(fileData)))
                        continue;

                    if (options.UsePropertyMerge && filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var merged = JsonDeepMerge(existingData, fileData);
                            finalPfs.AddFile(filePath, merged);
                            var prevOwner = fileOwner.GetValueOrDefault(filePath, "?");
                            fileOwner[filePath] = $"{prevOwner} + {patchName}";
                            conflicts.Add(new ConflictEntry(filePath, "Resolved", "Properties Merge",
                                $"{prevOwner} + {patchName}"));
                            continue;
                        }
                        catch { /* fall through to overwrite */ }
                    }

                    var prevFileOwner = fileOwner.GetValueOrDefault(filePath, "Original");
                    finalPfs.AddFile(filePath, fileData);
                    fileOwner[filePath] = patchName;
                    filesConflict++;
                    conflicts.Add(new ConflictEntry(filePath, "Conflict", "Overwrite",
                        $"{patchName} over {prevFileOwner}"));
                }

                int midProgress = (patchProgressStart + patchProgressEnd) / 2;
                LogService.Progress(midProgress, 100);

                // ─── Merge code entries (GML + ASM) ───
                int codeIdx = 0;
                int totalCode = pfs.GmlEntries.Count;
                int codeAdded = 0, codeConflict = 0, codeMerged = 0;
                var spriteRemap = patchSpriteRemaps[pi];

                foreach (var (codeName, rawGmlCode) in pfs.GmlEntries)
                {
                    var gmlCode = rawGmlCode;
                    codeIdx++;
                    if (codeIdx % 100 == 0)
                    {
                        int subPct = midProgress + codeIdx * (patchProgressEnd - midProgress) / 2 / Math.Max(totalCode, 1);
                        LogService.Progress(subPct, 100);
                    }

                    if (!finalPfs.GmlEntries.TryGetValue(codeName, out var existingGml))
                    {
                        finalPfs.AddGmlEntry(codeName, gmlCode);
                        if (pfs.AsmEntries.TryGetValue(codeName, out var asmCode))
                            finalPfs.AddAsmEntry(codeName, RemapSpriteIndicesAsm(asmCode, spriteRemap));
                        codeOwner[codeName] = patchName;
                        codeAdded++;
                        continue;
                    }

                    if (existingGml == gmlCode) continue;

                    if (options.UseCodeMerge && decompileCtx != null)
                    {
                        var baseGml = GetOriginalGml(originalData, decompileCtx, decompileCache, codeName);
                        if (baseGml != null)
                        {
                            var (merged, hasConflicts) = ThreeWayMerge(baseGml, existingGml, gmlCode);
                            if (merged != null)
                            {
                                finalPfs.AddGmlEntry(codeName, merged);
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
                    finalPfs.AddGmlEntry(codeName, gmlCode);
                    if (pfs.AsmEntries.TryGetValue(codeName, out var newAsm))
                        finalPfs.AddAsmEntry(codeName, RemapSpriteIndicesAsm(newAsm, spriteRemap));
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

                // ASM-only entries
                foreach (var (codeName, asmCode) in pfs.AsmEntries)
                {
                    if (!pfs.GmlEntries.ContainsKey(codeName) &&
                        !finalPfs.GmlEntries.ContainsKey(codeName) &&
                        !finalPfs.AsmEntries.ContainsKey(codeName))
                    {
                        finalPfs.AddAsmEntry(codeName, RemapSpriteIndicesAsm(asmCode, spriteRemap));
                    }
                }

                // NOTE: Manifest deletions are intentionally NOT propagated during merge.
                // Merged patches are purely additive - resources that exist in the
                // original game are never deleted, even if individual mods don't include them.

                LogService.Progress(patchProgressEnd, 100);
                LogService.Log($"[Bench] Patch {patchPriority}/{patchFileSystems.Length} ({patchName}): {patchStepSw.Elapsed.TotalSeconds:F2}s " +
                    $"(+{filesAdded} files, {filesConflict} file conflicts, +{codeAdded} code, {codeConflict} code conflicts, {codeMerged} code merged)");
            }

            LogService.Log($"[Bench] Merge loop total: {stepSw.Elapsed.TotalSeconds:F2}s, {conflicts.Count} conflict entries");
            LogService.Progress(65, 100);

            // ══════════════════════════════════════════════════════════
            // Phase 4: Merge helper files
            // ══════════════════════════════════════════════════════════
            stepSw.Restart();

            var mergedAssetOrder = MergeAssetOrders(originalAssetOrder, patchFileSystems);
            finalPfs.AddTextFile("Helpers/asset_order.txt", mergedAssetOrder);
            LogService.Progress(67, 100);

            var mergedVarFuncs = MergeVariablesFunctions(patchFileSystems);
            if (mergedVarFuncs != null)
                finalPfs.AddTextFile("Helpers/variables_functions.json", mergedVarFuncs);
            LogService.Progress(69, 100);

            var mergedObjEvents = MergeObjectEvents(patchFileSystems);
            if (mergedObjEvents != null)
                finalPfs.AddTextFile("Helpers/object_events.json", mergedObjEvents);
            LogService.Progress(71, 100);

            MergeTextureHelpers(finalPfs, patchFileSystems, originalTpiCount);
            LogService.Progress(73, 100);

            LogService.Log($"[Bench] Helper files merge: {stepSw.Elapsed.TotalSeconds:F2}s");

            // Release original data
            originalData = null!;
            decompileCtx = null;
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
                LogService.Log($"[Bench] Save ZIP: {stepSw.Elapsed.TotalSeconds:F2}s ({zipOutputPath})");
            }
            LogService.Progress(88, 100);

            // ══════════════════════════════════════════════════════════
            // Phase 6: Apply if requested
            // ══════════════════════════════════════════════════════════
            if (applyData)
            {
                stepSw.Restart();

                // If ZIP wasn't saved yet, save to temp
                string applyZipPath = zipOutputPath;
                if (!saveZip)
                {
                    applyZipPath = Path.Combine(Path.GetTempPath(), $"g3m_merge_{Guid.NewGuid():N}.g3mpatch");
                    finalPfs.SaveToZip(applyZipPath, manifest);
                    tempFiles.Add(applyZipPath);
                }

                if (!LogService.Verbose) LogService.Suppress = true;
                PatchApplyResult applyResult;
                try
                {
                    applyResult = await PatchService.ApplyPatchAsync(originalPath, applyZipPath, options.ApplyPath!);
                }
                finally
                {
                    LogService.Suppress = false;
                    LogService.SetOperation("Merging");
                }
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
        PatchFileSystem[] patches)
    {
        // Parse each patch's asset_order.txt
        var patchOrders = new List<Dictionary<string, List<string>>>();
        Dictionary<string, string>? latestCounts = null;

        foreach (var pfs in patches)
        {
            var aoPath = $"{pfs.HelpersPrefix}/asset_order.txt";
            if (!pfs.FileExists(aoPath)) continue;
            var text = pfs.ReadAllText(aoPath);
            var (sections, counts) = ParseAssetOrderText(text);
            patchOrders.Add(sections);
            if (counts.Count > 0) latestCounts = counts;
        }

        if (patchOrders.Count == 0)
            return ""; // No asset orders found

        // For each section: start with original order, append new items from each patch
        var mergedOrder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var sectionNames = new[] { "sounds", "sprites", "backgrounds", "paths", "scripts",
            "fonts", "objects", "timelines", "rooms", "shaders", "extensions", "audiogroups" };

        foreach (var section in sectionNames)
        {
            var origList = originalOrder.GetValueOrDefault(section) ?? [];
            var merged = new List<string>(origList);
            var mergedSet = new HashSet<string>(origList); // For fast lookup

            // For (null) entries and numeric indices, track by position not name
            // But for unique named entries, use name-based dedup
            foreach (var patchOrder in patchOrders)
            {
                if (!patchOrder.TryGetValue(section, out var patchList)) continue;

                foreach (var entry in patchList)
                {
                    if (entry == "(null)" || string.IsNullOrWhiteSpace(entry)) continue;

                    // Add only truly new entries (not in original)
                    if (!mergedSet.Contains(entry))
                    {
                        merged.Add(entry);
                        mergedSet.Add(entry);
                    }
                }
            }

            mergedOrder[section] = merged;
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
            foreach (var (key, value) in latestCounts)
                sb.AppendLine($"{key}={value}");
        }

        return sb.ToString();
    }

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

    private static string? MergeVariablesFunctions(PatchFileSystem[] patches)
    {
        // Collect from all patches: variables, functions, codeEntries, varCounts, codeMetadata
        var variables = new Dictionary<(string name, int instType), (string name, int instType, int varId)>();
        var functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codeEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, object>? varCounts = null;
        var codeMetadata = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        bool foundAny = false;

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
                foreach (var v in varArray.EnumerateArray())
                {
                    var name = v.GetProperty("n").GetString() ?? "";
                    int instType = v.GetProperty("t").GetInt32();
                    int varId = v.GetProperty("id").GetInt32();
                    if (!string.IsNullOrEmpty(name))
                        variables[(name, instType)] = (name, instType, varId);
                }
            }

            // Functions: union
            if (root.TryGetProperty("functions", out var funcArray))
            {
                foreach (var f in funcArray.EnumerateArray())
                {
                    var fname = f.GetString();
                    if (!string.IsNullOrEmpty(fname)) functions.Add(fname);
                }
            }

            // CodeEntries: union
            if (root.TryGetProperty("codeEntries", out var ceArray))
            {
                foreach (var ce in ceArray.EnumerateArray())
                {
                    var ceName = ce.GetString();
                    if (!string.IsNullOrEmpty(ceName)) codeEntries.Add(ceName);
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
        var varList = variables.Values
            .Select(v => new Dictionary<string, object> { ["n"] = v.name, ["t"] = v.instType, ["id"] = v.varId })
            .ToList();

        var result = new Dictionary<string, object>
        {
            ["variables"] = varList,
            ["functions"] = functions.OrderBy(f => f).ToList(),
            ["codeEntries"] = codeEntries.OrderBy(c => c).ToList()
        };

        if (varCounts != null)
            result["varCounts"] = varCounts;

        if (codeMetadata.Count > 0)
            result["codeMetadata"] = codeMetadata;

        return JsonSerializer.Serialize(result);
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
                            ? $"{et}_cn_{ecnStr}"
                            : $"{et}_{existingEvents[i]["s"]}";
                        existingKeys[key] = i;
                    }

                    foreach (var evt in events)
                    {
                        int et = Convert.ToInt32(evt["t"]);
                        string key = et == 4 && evt.TryGetValue("cn", out var ecn) && ecn is string ecnStr && ecnStr != ""
                            ? $"{et}_cn_{ecnStr}"
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
        int originalTpiCount)
    {
        // texture_page_items.json: merge by appending new TPIs from each patch
        // Original TPIs (indices 0..origCount-1) are the same across all patches.
        // Each patch may add new TPIs at indices >= origCount.
        // Merged: [original TPIs] + [patch1 new TPIs] + [patch2 new TPIs] + ...
        var tpiOffsets = new int[patches.Length]; // TPI index offset for each patch's new entries

        // Collect all patches' TPI data
        var allTpis = new List<int[][]>();
        foreach (var pfs in patches)
        {
            var tpiPath = $"{pfs.HelpersPrefix}/texture_page_items.json";
            if (!pfs.FileExists(tpiPath))
            {
                allTpis.Add([]);
                continue;
            }
            var tpiData = JsonSerializer.Deserialize<int[][]>(pfs.ReadAllText(tpiPath));
            allTpis.Add(tpiData ?? []);
        }

        // Build merged TPI list
        var tpiList = new List<int[]>();

        // Take original TPIs from first patch that has them
        int[][]? baseTpis = allTpis.FirstOrDefault(t => t.Length >= originalTpiCount);
        if (baseTpis != null)
        {
            for (int i = 0; i < Math.Min(originalTpiCount, baseTpis.Length); i++)
                tpiList.Add(baseTpis[i]);
        }

        // Append new TPIs from each patch, tracking offsets
        for (int pi = 0; pi < patches.Length; pi++)
        {
            tpiOffsets[pi] = tpiList.Count - originalTpiCount;
            var patchTpis = allTpis[pi];
            for (int i = originalTpiCount; i < patchTpis.Length; i++)
                tpiList.Add(patchTpis[i]);
        }

        if (tpiList.Count > 0)
            finalPfs.AddTextFile("Helpers/texture_page_items.json", JsonSerializer.Serialize(tpiList.ToArray()));

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
                    int idx = bg.Value.GetInt32();
                    mergedBgs[bg.Name] = idx >= originalTpiCount ? idx + offset : idx;
                }
            }

            if (doc.RootElement.TryGetProperty("fonts", out var fonts))
            {
                foreach (var font in fonts.EnumerateObject())
                {
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

        if (hasConflicts)
        {
            // Fall back to theirs (higher priority) for the entire file
            return (theirsText, true);
        }

        // No overlapping non-identical edits → merge cleanly
        var allEdits = new List<(int BaseStart, int BaseEnd, string[] Replacement)>();
        var editKeys = new HashSet<(int, int)>();

        foreach (var e in oursEdits)
        {
            allEdits.Add(e);
            editKeys.Add((e.BaseStart, e.BaseEnd));
        }
        foreach (var e in theirsEdits)
        {
            if (!editKeys.Contains((e.BaseStart, e.BaseEnd)))
                allEdits.Add(e);
        }

        // Stable sort by base start position (ours before theirs at same position)
        allEdits = [.. allEdits.OrderBy(e => e.BaseStart)];

        var result = new List<string>();
        int pos = 0;
        foreach (var (start, end, replacement) in allEdits)
        {
            // Add unchanged base lines before this edit
            for (int i = pos; i < start; i++)
                result.Add(baseLines[i]);
            result.AddRange(replacement);
            pos = end;
        }
        // Add remaining base lines
        for (int i = pos; i < baseLines.Length; i++)
            result.Add(baseLines[i]);

        return (string.Join("\n", result), false);
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
            else
            {
                // Overwrite (arrays and primitives)
                target[prop.Key] = prop.Value.DeepClone();
            }
        }
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

                // Merge changed/new (deduplicate by name)
                var existingNames = new HashSet<string>(
                    (merged.Changed ?? []).Select(c => c.Name ?? "")
                    .Concat((merged.New ?? []).Select(n => n.Name ?? "")),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var c in changes.Changed ?? [])
                    if (!string.IsNullOrEmpty(c.Name) && existingNames.Add(c.Name))
                        merged.Changed!.Add(c);

                foreach (var n in changes.New ?? [])
                    if (!string.IsNullOrEmpty(n.Name) && existingNames.Add(n.Name))
                        merged.New!.Add(n);

                // NOTE: Deletions are NOT propagated - merged patches are purely additive
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
            Version = "1.0",
            CreatedAt = DateTime.UtcNow.ToString("o"),
            Tool = new Models.ToolInfo { Name = "G3MTool", Version = "1.0.0-merge" },
            Original = originalInfo,
            Resources = mergedResources,
            Statistics = stats
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
            sb.AppendLine("| File | Status | Strategy | Winner |");
            sb.AppendLine("|------|--------|----------|--------|");

            foreach (var c in group.OrderBy(c => c.File))
            {
                // Use raw name without backtick-wrapping to avoid markdown escaping underscores
                sb.AppendLine($"| {ShortName(c.File)} | **{c.Status}** | {c.Strategy} | {c.Winner} |");
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
    // Sprite index remapping helpers
    // ═══════════════════════════════════════════════════════════════════

    [GeneratedRegex(@"(pushi\.e )(-?\d+)")]
    private static partial Regex AsmPushiRegex();
    [GeneratedRegex(@"(push\.i )(-?\d+)")]
    private static partial Regex AsmPushIntRegex();

    private static string RemapSpriteIndicesAsm(string asm, Dictionary<int, int> remap)
    {
        if (remap.Count == 0) return asm;
        // Remap pushi.e <value> (compact 16-bit immediate)
        asm = AsmPushiRegex().Replace(asm, m =>
        {
            if (int.TryParse(m.Groups[2].Value, out int val) && remap.TryGetValue(val, out int newVal))
                return m.Groups[1].Value + newVal.ToString();
            return m.Value;
        });
        // Remap push.i <value> (32-bit immediate, NOT [type]name resource refs)
        asm = AsmPushIntRegex().Replace(asm, m =>
        {
            if (int.TryParse(m.Groups[2].Value, out int val) && remap.TryGetValue(val, out int newVal))
                return m.Groups[1].Value + newVal.ToString();
            return m.Value;
        });
        return asm;
    }
}
