using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using G3MToolCLI.Models;
using G3MToolCLI.Utils;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace G3MToolCLI.Services;

public class DiffService
{
    private const int DataFileBufferSize = 1024 * 1024;

    private static FileStream OpenDataReadStream(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, DataFileBufferSize, FileOptions.SequentialScan);

    private static readonly string[] ResourceTypes = ResourceTypeRegistry.AllTypes;

    private static readonly string[] TextExtensions = [".gml", ".json", ".txt", ".xml"];
    private static readonly HashSet<string> BinaryHeavyResourceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sprites",
        "Fonts",
        "Sounds",
        "EmbeddedAudio",
        "EmbeddedTextures",
        "TexturePageItems",
        "TextureGroupInfo"
    };

    public static async Task<DiffResult> CompareAsync(string file1Path, string file2Path, string outputPath, DiffReportMode reportMode = DiffReportMode.Standard, G3MCacheOptions? cacheOptions = null)
    {
        if (!File.Exists(file1Path))
            return new DiffResult { Success = false, Error = $"File not found: {file1Path}" };

        if (!File.Exists(file2Path))
            return new DiffResult { Success = false, Error = $"File not found: {file2Path}" };

        var ext1 = Path.GetExtension(file1Path).ToLowerInvariant();
        var ext2 = Path.GetExtension(file2Path).ToLowerInvariant();

        var sb = new StringBuilder();
        sb.AppendLine("# Diff Report");
        sb.AppendLine();
        sb.AppendLine($"> Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        int differenceCount;
        ResourceDiffSummary? resourceSummary = null;

        var isData1 = DataFileExtensionUtil.IsValidDataExtension(ext1);
        var isData2 = DataFileExtensionUtil.IsValidDataExtension(ext2);

        if ((ext1 == ".g3mpatch" || ext1 == ".zip") && isData2)
        {
            differenceCount = await ComparePatchWithDataAsync(file1Path, file2Path, sb, reportMode);
        }
        else if (isData1 && (ext2 == ".g3mpatch" || ext2 == ".zip"))
        {
            differenceCount = await ComparePatchWithDataAsync(file2Path, file1Path, sb, reportMode);
        }
        else if ((ext1 == ".g3mpatch" || ext1 == ".zip") && (ext2 == ".g3mpatch" || ext2 == ".zip"))
        {
            differenceCount = await ComparePatchesAsync(file1Path, file2Path, sb, reportMode);
        }
        else if (isData1 && isData2)
        {
            resourceSummary = await CompareDataFilesAsync(file1Path, file2Path, sb, reportMode, cacheOptions);
            differenceCount = resourceSummary.TotalChanged + resourceSummary.TotalNew + resourceSummary.TotalDeleted;
        }
        else
        {
            sb.AppendLine("## Unsupported file combination");
            sb.AppendLine();
            sb.AppendLine($"Cannot compare `{ext1}` with `{ext2}`.");
            sb.AppendLine($"Supported: {DataFileExtensionUtil.GetValidExtensionsDisplay()} vs same, `.g3mpatch` vs data file, `.g3mpatch` vs `.g3mpatch`");

            await WriteOutputAsync(outputPath, sb.ToString());
            return new DiffResult
            {
                Success = false,
                Error = $"Unsupported file combination: '{ext1}' vs '{ext2}'",
                OutputPath = outputPath
            };
        }

        await WriteOutputAsync(outputPath, sb.ToString());

        return new DiffResult
        {
            Success = true,
            DifferenceCount = differenceCount,
            OutputPath = outputPath,
            Mode = reportMode.ToString().ToLowerInvariant(),
            TotalChanged = resourceSummary?.TotalChanged,
            TotalNew = resourceSummary?.TotalNew,
            TotalDeleted = resourceSummary?.TotalDeleted,
            ByType = resourceSummary?.ByType
                .Where(kvp => kvp.Value.Changed.Count > 0 || kvp.Value.New.Count > 0 || kvp.Value.Deleted.Count > 0)
                .ToDictionary(
                kvp => kvp.Key,
                kvp => new DiffTypeSummary
                {
                    Changed = kvp.Value.Changed.Count,
                    New = kvp.Value.New.Count,
                    Deleted = kvp.Value.Deleted.Count
                },
                StringComparer.OrdinalIgnoreCase),
            TextDiffCount = resourceSummary?.TextDiffs.Count
        };
    }

    private static async Task WriteOutputAsync(string outputPath, string contents)
    {
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        await File.WriteAllTextAsync(outputPath, contents);
    }

    private static async Task<ResourceDiffSummary> CompareDataFilesAsync(string file1Path, string file2Path, StringBuilder sb, DiffReportMode reportMode, G3MCacheOptions? cacheOptions)
    {
        LogService.SetOperation("Comparing data files");
        LogService.Progress(0, 100);

        var hash1 = G3MCacheService.TryReadDataFileInfo(file1Path, cacheOptions)?.Md5
            ?? await HashService.ComputeFileHashAsync(file1Path);
        LogService.Progress(3, 100);
        var hash2 = G3MCacheService.TryReadDataFileInfo(file2Path, cacheOptions)?.Md5
            ?? await HashService.ComputeFileHashAsync(file2Path);
        LogService.Progress(6, 100);

        sb.AppendLine("## Files");
        sb.AppendLine();
        sb.AppendLine("| Property | File 1 | File 2 |");
        sb.AppendLine("|:---------|:-------|:-------|");
        sb.AppendLine($"| **Path** | `{Path.GetFileName(file1Path)}` | `{Path.GetFileName(file2Path)}` |");
        sb.AppendLine($"| **Size** | {new FileInfo(file1Path).Length:N0} bytes | {new FileInfo(file2Path).Length:N0} bytes |");
        sb.AppendLine($"| **MD5** | `{hash1}` | `{hash2}` |");
        sb.AppendLine();

        if (hash1 == hash2)
        {
            sb.AppendLine("Files are **identical** (same MD5 hash).");
            LogService.ProgressComplete();
            return new ResourceDiffSummary();
        }

        var resourceDiffs = await CompareWinFilesAsync(file1Path, file2Path, reportMode, cacheOptions);
        _ = resourceDiffs.TotalChanged + resourceDiffs.TotalNew + resourceDiffs.TotalDeleted;

        LogService.Progress(92, 100);
        AppendSummaryTable(sb, resourceDiffs);
        AppendResourceDetails(sb, resourceDiffs, reportMode);

        // Binary-level analysis: variables, functions, indices
        LogService.Log("[DiffService] Performing binary-level analysis...");
        AppendBinaryAnalysis(sb, file1Path, file2Path, reportMode);

        LogService.Progress(100, 100);
        LogService.ProgressComplete();

        return resourceDiffs;
    }

    private static async Task<int> ComparePatchWithDataAsync(string patchPath, string dataPath, StringBuilder sb, DiffReportMode reportMode)
    {
        LogService.SetOperation("Comparing patch with data");
        LogService.Progress(0, 100);

        sb.AppendLine("## Patch vs Data Comparison");
        sb.AppendLine();
        sb.AppendLine($"| | File |");
        sb.AppendLine("|:--|:-----|");
        sb.AppendLine($"| **Patch** | `{Path.GetFileName(patchPath)}` |");
        sb.AppendLine($"| **Data** | `{Path.GetFileName(dataPath)}` |");
        sb.AppendLine();

        var tempDir = Path.Combine(Path.GetTempPath(), $"g3mtool_diff_{Guid.NewGuid():N}");
        var patchExtractDir = Path.Combine(tempDir, "patch");
        var dataExportDir = Path.Combine(tempDir, "data");

        try
        {
            Directory.CreateDirectory(patchExtractDir);
            Directory.CreateDirectory(dataExportDir);

            // Extract patch
            LogService.Progress(10, 100);
            ZipFile.ExtractToDirectory(patchPath, patchExtractDir);
            LogService.Progress(20, 100);

            // Read manifest to know which resources to compare
            var manifestPath = Path.Combine(patchExtractDir, "g3mpatch.json");
            G3MPatchManifest? manifest = null;
            bool manifestValid = true;

            if (!File.Exists(manifestPath))
            {
                sb.AppendLine("## Warning");
                sb.AppendLine();
                sb.AppendLine("Patch does not contain `g3mpatch.json` manifest. Using folder-based detection.");
                sb.AppendLine();
                manifestValid = false;
            }
            else
            {
                try
                {
                    var manifestJson = await File.ReadAllTextAsync(manifestPath);
                    manifest = JsonSerializer.Deserialize<G3MPatchManifest>(manifestJson);

                    if (manifest?.Resources == null)
                    {
                        sb.AppendLine("## Warning");
                        sb.AppendLine();
                        sb.AppendLine("Manifest has no resources. Using folder-based detection.");
                        sb.AppendLine();
                        manifestValid = false;
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("## Warning");
                    sb.AppendLine();
                    sb.AppendLine($"Failed to parse manifest: {ex.Message}. Using folder-based detection.");
                    sb.AppendLine();
                    manifestValid = false;
                }
            }

            // Determine resource types to compare
            HashSet<string> resourceTypesInPatch;

            if (manifest?.Resources != null && manifestValid)
            {
                resourceTypesInPatch = [.. manifest.Resources.Keys];
            }
            else
            {
                // Fallback: detect folders in extracted patch
                var existingFolders = Directory.GetDirectories(patchExtractDir)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrEmpty(name) && ResourceTypes.Contains(name))
                    .ToHashSet()!;

                resourceTypesInPatch = existingFolders!;

                if (resourceTypesInPatch.Count == 0)
                {
                    sb.AppendLine("## Error");
                    sb.AppendLine();
                    sb.AppendLine("No valid resource folders found in patch.");
                    return 0;
                }

                sb.AppendLine($"Detected resource folders: {string.Join(", ", resourceTypesInPatch)}");
                sb.AppendLine();
            }
            // Export only the resource types present in the patch
            using (var stream = OpenDataReadStream(dataPath))
            {
                var dataObj = UndertaleIO.Read(stream);
                ResourceExportService.ExportTypes(dataObj, dataExportDir, dataPath, resourceTypesInPatch);
            }
            GC.Collect();
            LogService.Progress(60, 100);

            // Compare resources from patch with exported data
            var summary = new ResourceDiffSummary();
            List<TextFileDiff> textDiffs = [];

            if (manifest?.Resources != null && manifestValid)
            {
                // Use manifest-based comparison
                foreach (var (resourceType, changes) in manifest.Resources)
                {
                    var patchResDir = Path.Combine(patchExtractDir, resourceType);
                    var dataResDir = Path.Combine(dataExportDir, resourceType);
                    var typeChanges = new DiffResourceTypeChanges();

                    // Check changed resources
                    foreach (var changed in changes.Changed ?? Enumerable.Empty<ResourceChange>())
                    {
                        if (string.IsNullOrEmpty(changed.Name)) continue;

                        var patchResPath = Path.Combine(patchResDir, changed.Name);
                        var dataResPath = Path.Combine(dataResDir, changed.Name);

                        if (Directory.Exists(patchResPath) && Directory.Exists(dataResPath))
                        {
                            if (await AreDirectoriesDifferentAsync(patchResPath, dataResPath))
                            {
                                typeChanges.Changed.Add(changed.Name);
                                // Collect text diffs
                                await CollectTextDiffsAsync(patchResPath, dataResPath, changed.Name, resourceType, textDiffs, reportMode);
                            }
                        }
                        else
                        {
                            typeChanges.Changed.Add(changed.Name);
                        }
                    }

                    // New resources (in patch but not in data)
                    foreach (var newRes in changes.New ?? Enumerable.Empty<ResourceChange>())
                    {
                        if (!string.IsNullOrEmpty(newRes.Name))
                            typeChanges.New.Add(newRes.Name);
                    }

                    summary.ByType[resourceType] = typeChanges;
                    summary.TotalChanged += typeChanges.Changed.Count;
                    summary.TotalNew += typeChanges.New.Count;
                }
            }
            else
            {
                // Fallback: folder-based comparison
                foreach (var resourceType in resourceTypesInPatch)
                {
                    var patchResDir = Path.Combine(patchExtractDir, resourceType);
                    var dataResDir = Path.Combine(dataExportDir, resourceType);

                    var typeChanges = await CompareResourceTypeAsync(patchResDir, dataResDir);
                    summary.ByType[resourceType] = typeChanges;
                    summary.TotalChanged += typeChanges.Changed.Count;
                    summary.TotalNew += typeChanges.New.Count;
                    summary.TotalDeleted += typeChanges.Deleted.Count;

                    // Collect text diffs for changed resources
                    foreach (var changedName in typeChanges.Changed)
                    {
                        var patchResPath = Path.Combine(patchResDir, changedName);
                        var dataResPath = Path.Combine(dataResDir, changedName);
                        if (Directory.Exists(patchResPath) && Directory.Exists(dataResPath))
                        {
                            await CollectTextDiffsAsync(patchResPath, dataResPath, changedName, resourceType, textDiffs, reportMode);
                        }
                    }
                }
            }

            LogService.Progress(90, 100);
            AppendSummaryTable(sb, summary);
            AppendResourceList(sb, summary);
            AppendTextDiffs(sb, textDiffs);

            LogService.ProgressComplete();

            return summary.TotalChanged + summary.TotalNew;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static async Task<int> ComparePatchesAsync(string patch1Path, string patch2Path, StringBuilder sb, DiffReportMode reportMode)
    {
        LogService.SetOperation("Comparing patches");
        LogService.Progress(0, 100);

        sb.AppendLine("## Patches");
        sb.AppendLine();
        sb.AppendLine($"| | File |");
        sb.AppendLine("|:--|:-----|");
        sb.AppendLine($"| **Patch 1** | `{Path.GetFileName(patch1Path)}` |");
        sb.AppendLine($"| **Patch 2** | `{Path.GetFileName(patch2Path)}` |");
        sb.AppendLine();

        var tempDir = Path.Combine(Path.GetTempPath(), $"g3mtool_diff_{Guid.NewGuid():N}");
        var patch1Dir = Path.Combine(tempDir, "patch1");
        var patch2Dir = Path.Combine(tempDir, "patch2");

        try
        {
            Directory.CreateDirectory(patch1Dir);
            Directory.CreateDirectory(patch2Dir);

            LogService.Progress(10, 100);
            ZipFile.ExtractToDirectory(patch1Path, patch1Dir);
            LogService.Progress(20, 100);
            ZipFile.ExtractToDirectory(patch2Path, patch2Dir);
            LogService.Progress(30, 100);

            var summary = new ResourceDiffSummary();

            int typeIndex = 0;
            int totalTypes = ResourceTypes.Length;
            foreach (var resourceType in ResourceTypes)
            {
                var dir1 = Path.Combine(patch1Dir, resourceType);
                var dir2 = Path.Combine(patch2Dir, resourceType);

                var changes = await CompareResourceTypeAsync(dir1, dir2);
                summary.ByType[resourceType] = changes;
                summary.TotalChanged += changes.Changed.Count;
                summary.TotalNew += changes.New.Count;
                summary.TotalDeleted += changes.Deleted.Count;

                // Collect text diffs
                foreach (var changedName in changes.Changed)
                {
                    var resDir1 = Path.Combine(dir1, changedName);
                    var resDir2 = Path.Combine(dir2, changedName);
                    if (Directory.Exists(resDir1) && Directory.Exists(resDir2))
                    {
                        await CollectTextDiffsAsync(resDir1, resDir2, changedName, resourceType, summary.TextDiffs, reportMode);
                    }
                }

                typeIndex++;
                LogService.Progress(30 + (typeIndex * 60 / totalTypes), 100);
            }

            LogService.Progress(95, 100);
            AppendSummaryTable(sb, summary);
            AppendResourceList(sb, summary);
            AppendTextDiffs(sb, summary.TextDiffs);

            LogService.ProgressComplete();

            return summary.TotalChanged + summary.TotalNew + summary.TotalDeleted;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static void AppendSummaryTable(StringBuilder sb, ResourceDiffSummary summary)
    {
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("> Comparison: **File 2** relative to **File 1**");
        sb.AppendLine("> - **Changed** = exists in both, but content differs");
        sb.AppendLine("> - **New** = exists in File 2, not in File 1");
        sb.AppendLine("> - **Deleted** = exists in File 1, not in File 2");
        sb.AppendLine();
        sb.AppendLine("| Category | Count |");
        sb.AppendLine("|:---------|------:|");
        sb.AppendLine($"| Changed | {summary.TotalChanged} |");
        sb.AppendLine($"| New | {summary.TotalNew} |");
        sb.AppendLine($"| Deleted | {summary.TotalDeleted} |");
        sb.AppendLine($"| **Total** | **{summary.TotalChanged + summary.TotalNew + summary.TotalDeleted}** |");
        sb.AppendLine();
    }

    private static void AppendResourceList(StringBuilder sb, ResourceDiffSummary summary)
    {
        foreach (var (resourceType, changes) in summary.ByType)
        {
            if (changes.Changed.Count == 0 && changes.New.Count == 0 && changes.Deleted.Count == 0)
                continue;

            sb.AppendLine($"## {resourceType}");
            sb.AppendLine();

            if (changes.Changed.Count > 0)
            {
                sb.AppendLine($"### Changed ({changes.Changed.Count})");
                sb.AppendLine();
                foreach (var name in changes.Changed)
                    sb.AppendLine($"- `{name}`");
                sb.AppendLine();
            }

            if (changes.New.Count > 0)
            {
                sb.AppendLine($"### New ({changes.New.Count})");
                sb.AppendLine();
                foreach (var name in changes.New)
                    sb.AppendLine($"- `{name}`");
                sb.AppendLine();
            }

            if (changes.Deleted.Count > 0)
            {
                sb.AppendLine($"### Deleted ({changes.Deleted.Count})");
                sb.AppendLine();
                foreach (var name in changes.Deleted)
                    sb.AppendLine($"- `{name}`");
                sb.AppendLine();
            }
        }
    }

    private static void AppendResourceDetails(StringBuilder sb, ResourceDiffSummary summary, DiffReportMode reportMode)
    {
        AppendResourceList(sb, summary);

        if (summary.TextDiffs.Count > 0)
        {
            if (reportMode == DiffReportMode.Full)
                AppendTextDiffs(sb, summary.TextDiffs);
            else
                AppendTextDiffSummary(sb, summary.TextDiffs);
        }
    }

    private static void AppendTextDiffSummary(StringBuilder sb, List<TextFileDiff> textDiffs)
    {
        if (textDiffs.Count == 0) return;

        sb.AppendLine("## Text Difference Summary");
        sb.AppendLine();
        sb.AppendLine("Standard mode lists changed text files without unified hunks. Use `--full` to include exact text/code/JSON diffs.");
        sb.AppendLine();
        sb.AppendLine("| Resource | Changed text files |");
        sb.AppendLine("|:---------|-------------------:|");
        foreach (var group in textDiffs.GroupBy(d => $"{d.ResourceType}/{d.ResourceName}").OrderBy(g => g.Key, StringComparer.Ordinal).Take(120))
            sb.AppendLine($"| `{group.Key}` | {group.Count()} |");
        if (textDiffs.Select(d => $"{d.ResourceType}/{d.ResourceName}").Distinct(StringComparer.Ordinal).Count() > 120)
            sb.AppendLine("| *additional resources omitted from standard report* | |");
        sb.AppendLine();
    }

    private static void AppendTextDiffs(StringBuilder sb, List<TextFileDiff> textDiffs)
    {
        if (textDiffs.Count == 0) return;

        sb.AppendLine("## Text Differences");
        sb.AppendLine();

        foreach (var diff in textDiffs)
        {
            sb.AppendLine($"### `{diff.ResourceType}/{diff.ResourceName}/{diff.FileName}`");
            sb.AppendLine();
            sb.AppendLine("```diff");
            sb.AppendLine(diff.UnifiedDiff);
            sb.AppendLine("```");
            sb.AppendLine();
        }
    }

    private static void AppendBinaryAnalysis(StringBuilder sb, string file1Path, string file2Path, DiffReportMode reportMode)
    {
        UndertaleData data1, data2;
        using (var s = OpenDataReadStream(file1Path))
            data1 = UndertaleIO.Read(s);
        using (var s = OpenDataReadStream(file2Path))
            data2 = UndertaleIO.Read(s);

        sb.AppendLine("## Binary Analysis");
        sb.AppendLine();

        // Resource counts table
        sb.AppendLine("### Resource Counts");
        sb.AppendLine();
        sb.AppendLine("| Resource | File 1 | File 2 | Delta |");
        sb.AppendLine("|:---------|-------:|-------:|------:|");

        void Row(string name, int c1, int c2)
        {
            string delta = c1 == c2 ? "=" : $"{c2 - c1:+#;-#;0}";
            sb.AppendLine($"| {name} | {c1} | {c2} | {delta} |");
        }

        Row("Sprites", data1.Sprites?.Count ?? 0, data2.Sprites?.Count ?? 0);
        Row("Backgrounds", data1.Backgrounds?.Count ?? 0, data2.Backgrounds?.Count ?? 0);
        Row("Sounds", data1.Sounds?.Count ?? 0, data2.Sounds?.Count ?? 0);
        Row("Fonts", data1.Fonts?.Count ?? 0, data2.Fonts?.Count ?? 0);
        Row("Paths", data1.Paths?.Count ?? 0, data2.Paths?.Count ?? 0);
        Row("Scripts", data1.Scripts?.Count ?? 0, data2.Scripts?.Count ?? 0);
        Row("GameObjects", data1.GameObjects?.Count ?? 0, data2.GameObjects?.Count ?? 0);
        Row("Rooms", data1.Rooms?.Count ?? 0, data2.Rooms?.Count ?? 0);
        Row("Code", data1.Code?.Count ?? 0, data2.Code?.Count ?? 0);
        Row("Functions", data1.Functions?.Count ?? 0, data2.Functions?.Count ?? 0);
        Row("Variables", data1.Variables?.Count ?? 0, data2.Variables?.Count ?? 0);
        Row("Strings", data1.Strings?.Count ?? 0, data2.Strings?.Count ?? 0);
        Row("Timelines", data1.Timelines?.Count ?? 0, data2.Timelines?.Count ?? 0);
        Row("Shaders", data1.Shaders?.Count ?? 0, data2.Shaders?.Count ?? 0);
        Row("Extensions", data1.Extensions?.Count ?? 0, data2.Extensions?.Count ?? 0);
        Row("AudioGroups", data1.AudioGroups?.Count ?? 0, data2.AudioGroups?.Count ?? 0);
        Row("EmbeddedTextures", data1.EmbeddedTextures?.Count ?? 0, data2.EmbeddedTextures?.Count ?? 0);
        Row("TexturePageItems", data1.TexturePageItems?.Count ?? 0, data2.TexturePageItems?.Count ?? 0);
        sb.AppendLine();

        AppendSpriteFrameAnalysis(sb, data1, data2);

        // Helper to compare named lists and report mismatches
        void CompareNamedList<T>(
            string sectionName,
            IList<T>? list1, IList<T>? list2,
            Func<T, string?> getName,
            int maxItems = 20)
        {
            maxItems = reportMode == DiffReportMode.Full ? Math.Max(maxItems, 200) : maxItems;
            var names1 = (list1 ?? Array.Empty<T>()).Select(getName).Where(n => n != null).ToList();
            var names2 = (list2 ?? Array.Empty<T>()).Select(getName).Where(n => n != null).ToList();
            var index1 = new Dictionary<string, int>(StringComparer.Ordinal);
            var index2 = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < names1.Count; i++)
                index1.TryAdd(names1[i]!, i);
            for (int i = 0; i < names2.Count; i++)
                index2.TryAdd(names2[i]!, i);
            var set1 = index1.Keys.ToHashSet(StringComparer.Ordinal);
            var set2 = index2.Keys.ToHashSet(StringComparer.Ordinal);

            var missing = set1.Except(set2).ToList();
            var extra = set2.Except(set1).ToList();

            // Index mismatches
            var common = set1.Intersect(set2).ToList();
            List<(string name, int idx1, int idx2)> indexMismatches = [];
            foreach (var name in common)
            {
                int i1 = index1[name!];
                int i2 = index2[name!];
                if (i1 != i2)
                    indexMismatches.Add((name!, i1, i2));
            }

            if (missing.Count == 0 && extra.Count == 0 && indexMismatches.Count == 0)
                return;

            sb.AppendLine($"### {sectionName}");
            sb.AppendLine();

            if (missing.Count > 0)
            {
                sb.AppendLine($"**Missing in File 2 ({missing.Count}):** ");
                foreach (var n in missing.Take(maxItems))
                    sb.AppendLine($"- `{n}`");
                if (missing.Count > maxItems)
                    sb.AppendLine($"- *...and {missing.Count - maxItems} more*");
                sb.AppendLine();
            }

            if (extra.Count > 0)
            {
                sb.AppendLine($"**Extra in File 2 ({extra.Count}):** ");
                foreach (var n in extra.Take(maxItems))
                    sb.AppendLine($"- `{n}`");
                if (extra.Count > maxItems)
                    sb.AppendLine($"- *...and {extra.Count - maxItems} more*");
                sb.AppendLine();
            }

            if (indexMismatches.Count > 0)
            {
                sb.AppendLine($"**Index mismatches ({indexMismatches.Count}):**");
                sb.AppendLine();
                sb.AppendLine("| Name | File 1 idx | File 2 idx |");
                sb.AppendLine("|:-----|----------:|-----------:|");
                foreach (var (name, i1, i2) in indexMismatches.Take(maxItems))
                    sb.AppendLine($"| `{name}` | {i1} | {i2} |");
                if (indexMismatches.Count > maxItems)
                    sb.AppendLine($"| *...and {indexMismatches.Count - maxItems} more* | | |");
                sb.AppendLine();
            }
        }

        CompareNamedList("Variables", data1.Variables, data2.Variables, v => v?.Name?.Content);
        CompareNamedList("Functions", data1.Functions, data2.Functions, f => f?.Name?.Content);
        CompareNamedList("GameObjects", data1.GameObjects, data2.GameObjects, o => o?.Name?.Content);
        CompareNamedList("Sprites", data1.Sprites, data2.Sprites, s => s?.Name?.Content);
        CompareNamedList("Rooms", data1.Rooms, data2.Rooms, r => r?.Name?.Content);
        CompareNamedList("Scripts", data1.Scripts, data2.Scripts, s => s?.Name?.Content);
        CompareNamedList("Sounds", data1.Sounds, data2.Sounds, s => s?.Name?.Content);
        CompareNamedList("Code Entries", data1.Code, data2.Code, c => c?.Name?.Content);
        AppendTexturePageItemReferenceAnalysis(sb, data1, data2, reportMode == DiffReportMode.Full ? 200 : 40);

        // Room instance count diffs
        List<(string name, int c1, int c2)> roomDiffs = [];
        if (data1.Rooms != null && data2.Rooms != null)
        {
            var rooms2ByName = new Dictionary<string, UndertaleRoom>(StringComparer.Ordinal);
            foreach (var room in data2.Rooms)
            {
                var name = room?.Name?.Content;
                if (!string.IsNullOrEmpty(name) && room != null)
                    rooms2ByName.TryAdd(name, room);
            }

            foreach (var r1 in data1.Rooms)
            {
                if (r1?.Name?.Content == null) continue;
                if (!rooms2ByName.TryGetValue(r1.Name.Content, out var r2)) continue;

                int CountInstances(UndertaleRoom room)
                {
                    int count = 0;
                    foreach (var layer in room.Layers ?? Enumerable.Empty<UndertaleRoom.Layer>())
                        if (layer.InstancesData?.Instances != null)
                            count += layer.InstancesData.Instances.Count;
                    return count;
                }

                int ic1 = CountInstances(r1), ic2 = CountInstances(r2);
                if (ic1 != ic2)
                    roomDiffs.Add((r1.Name.Content, ic1, ic2));
            }
        }

        if (roomDiffs.Count > 0)
        {
            sb.AppendLine("### Room Instance Counts");
            sb.AppendLine();
            sb.AppendLine("| Room | File 1 | File 2 |");
            sb.AppendLine("|:-----|-------:|-------:|");
            foreach (var (name, c1, c2) in roomDiffs.Take(30))
                sb.AppendLine($"| `{name}` | {c1} | {c2} |");
            if (roomDiffs.Count > 30)
                sb.AppendLine($"| *...and {roomDiffs.Count - 30} more* | | |");
            sb.AppendLine();
        }
    }

    private static void AppendTexturePageItemReferenceAnalysis(StringBuilder sb, UndertaleData data1, UndertaleData data2, int maxRows)
    {
        var refs1 = BuildTexturePageItemReferenceMap(data1);
        var refs2 = BuildTexturePageItemReferenceMap(data2);
        var rows = new List<string>();
        foreach (var key in refs1.Keys.Union(refs2.Keys).OrderBy(k => k))
        {
            refs1.TryGetValue(key, out var left);
            refs2.TryGetValue(key, out var right);
            if (!string.Equals(left, right, StringComparison.Ordinal))
            {
                rows.Add($"| {key} | `{left ?? "<missing>"}` | `{right ?? "<missing>"}` |");
                if (rows.Count >= maxRows)
                    break;
            }
        }

        if (rows.Count == 0)
            return;

        sb.AppendLine("### TexturePageItem References");
        sb.AppendLine();
        sb.AppendLine("| TPI index | File 1 refs | File 2 refs |");
        sb.AppendLine("|----------:|:------------|:------------|");
        foreach (var row in rows)
            sb.AppendLine(row);
        if (refs1.Keys.Union(refs2.Keys).Count() > maxRows)
            sb.AppendLine("| *additional changed TPI refs omitted* | | |");
        sb.AppendLine();
    }

    private static Dictionary<int, string> BuildTexturePageItemReferenceMap(UndertaleData data)
    {
        var refs = new Dictionary<int, List<string>>();
        void Add(UndertaleTexturePageItem? tpi, string owner)
        {
            if (tpi == null || data.TexturePageItems == null)
                return;
            int index = data.TexturePageItems.IndexOf(tpi);
            if (index < 0)
                return;
            if (!refs.TryGetValue(index, out var owners))
            {
                owners = [];
                refs[index] = owners;
            }
            owners.Add(owner);
        }

        foreach (var sprite in data.Sprites ?? [])
        {
            var name = sprite?.Name?.Content;
            if (sprite?.Textures == null || string.IsNullOrWhiteSpace(name))
                continue;
            for (int i = 0; i < sprite.Textures.Count; i++)
                Add(sprite.Textures[i]?.Texture, $"sprite:{name}[{i}]");
        }

        foreach (var bg in data.Backgrounds ?? [])
            Add(bg?.Texture, $"background:{bg?.Name?.Content ?? "?"}");

        foreach (var font in data.Fonts ?? [])
            Add(font?.Texture, $"font:{font?.Name?.Content ?? "?"}");

        return refs.ToDictionary(kvp => kvp.Key, kvp => string.Join(", ", kvp.Value.OrderBy(v => v, StringComparer.Ordinal).Take(8)));
    }

    private static void AppendSpriteFrameAnalysis(StringBuilder sb, UndertaleData data1, UndertaleData data2)
    {
        if (data1.Sprites == null || data2.Sprites == null)
            return;

        var sprites1 = data1.Sprites
            .Where(s => s?.Name?.Content != null)
            .ToDictionary(s => s!.Name!.Content, StringComparer.Ordinal);
        var sprites2 = data2.Sprites
            .Where(s => s?.Name?.Content != null)
            .ToDictionary(s => s!.Name!.Content, StringComparer.Ordinal);

        var rows = new List<string>();
        using var worker = new TextureWorker();
        foreach (var name in sprites1.Keys.Intersect(sprites2.Keys, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
        {
            var left = sprites1[name];
            var right = sprites2[name];
            int minFrames = Math.Min(left.Textures.Count, right.Textures.Count);
            int changedFrames = 0;
            for (int i = 0; i < minFrames; i++)
            {
                if (SpriteFrameHash(worker, left, i) != SpriteFrameHash(worker, right, i))
                    changedFrames++;
            }

            int addedFrames = Math.Max(0, right.Textures.Count - left.Textures.Count);
            int removedFrames = Math.Max(0, left.Textures.Count - right.Textures.Count);
            bool metadataChanged =
                left.Width != right.Width ||
                left.Height != right.Height ||
                left.OriginX != right.OriginX ||
                left.OriginY != right.OriginY ||
                left.MarginLeft != right.MarginLeft ||
                left.MarginTop != right.MarginTop ||
                left.MarginRight != right.MarginRight ||
                left.MarginBottom != right.MarginBottom ||
                left.BBoxMode != right.BBoxMode ||
                left.Transparent != right.Transparent ||
                left.Smooth != right.Smooth ||
                left.Preload != right.Preload;

            if (changedFrames == 0 && addedFrames == 0 && removedFrames == 0 && !metadataChanged)
                continue;

            rows.Add($"| `{name}` | {left.Textures.Count} -> {right.Textures.Count} | {changedFrames} | {addedFrames} | {removedFrames} | {metadataChanged} |");
            if (rows.Count >= 80)
                break;
        }

        if (rows.Count == 0)
            return;

        sb.AppendLine("### Sprite Frame Differences");
        sb.AppendLine();
        sb.AppendLine("| Sprite | Frames | Changed Frames | Added Frames | Removed Frames | Metadata Changed |");
        sb.AppendLine("|:-------|:-------|---------------:|-------------:|---------------:|:-----------------|");
        foreach (var row in rows)
            sb.AppendLine(row);
        sb.AppendLine();
    }

    private static string SpriteFrameHash(TextureWorker worker, UndertaleSprite sprite, int frameIndex)
    {
        var tpi = sprite.Textures.ElementAtOrDefault(frameIndex)?.Texture;
        if (tpi == null)
            return "<null>";
        try
        {
            using var image = worker.GetTextureFor(tpi, $"{sprite.Name?.Content ?? "sprite"}_{frameIndex}", includePadding: false);
            return Convert.ToHexString(SHA256.HashData(image.ToByteArray(ImageMagick.MagickFormat.Png))).ToLowerInvariant();
        }
        catch
        {
            return "<decode-error>";
        }
    }

    private static async Task CollectTextDiffsAsync(string dir1, string dir2, string resourceName, string resourceType, List<TextFileDiff> textDiffs, DiffReportMode reportMode)
    {
        if (reportMode == DiffReportMode.Standard && BinaryHeavyResourceTypes.Contains(resourceType))
            return;

        var files1 = Directory.GetFiles(dir1, "*", SearchOption.AllDirectories);

        foreach (var file1 in files1)
        {
            var ext = Path.GetExtension(file1).ToLowerInvariant();
            if (!TextExtensions.Contains(ext)) continue;

            var relPath = Path.GetRelativePath(dir1, file1);
            var file2 = Path.Combine(dir2, relPath);

            if (!File.Exists(file2)) continue;

            var content1 = await File.ReadAllTextAsync(file1);
            var content2 = await File.ReadAllTextAsync(file2);

            if (content1 != content2)
            {
                textDiffs.Add(new TextFileDiff
                {
                    ResourceType = resourceType,
                    ResourceName = resourceName,
                    FileName = relPath,
                    UnifiedDiff = reportMode == DiffReportMode.Full
                        ? GenerateUnifiedDiff(content1, content2, relPath)
                        : ""
                });
            }
        }
    }

    private static string GenerateUnifiedDiff(string content1, string content2, string fileName)
    {
        var lines1 = content1.Split('\n');
        var lines2 = content2.Split('\n');

        var sb = new StringBuilder();
        sb.AppendLine($"--- a/{fileName}");
        sb.AppendLine($"+++ b/{fileName}");

        // Simple line-by-line diff (not a full LCS algorithm, but good enough for display)
        int i = 0, j = 0;
        int contextLines = 3;
        List<(int start1, int start2, List<string> lines)> hunks = [];
        List<string> currentHunk = [];
        int hunkStart1 = 0, hunkStart2 = 0;
        bool inHunk = false;

        while (i < lines1.Length || j < lines2.Length)
        {
            if (i < lines1.Length && j < lines2.Length && lines1[i].TrimEnd('\r') == lines2[j].TrimEnd('\r'))
            {
                if (inHunk)
                {
                    currentHunk.Add($" {lines1[i].TrimEnd('\r')}");
                }
                i++; j++;
            }
            else
            {
                if (!inHunk)
                {
                    inHunk = true;
                    hunkStart1 = Math.Max(0, i - contextLines);
                    hunkStart2 = Math.Max(0, j - contextLines);
                    // Add context before
                    for (int k = hunkStart1; k < i; k++)
                        currentHunk.Add($" {lines1[k].TrimEnd('\r')}");
                }

                // Find next matching line or end
                if (i < lines1.Length)
                {
                    currentHunk.Add($"-{lines1[i].TrimEnd('\r')}");
                    i++;
                }
                if (j < lines2.Length && (i >= lines1.Length || lines1[Math.Min(i, lines1.Length - 1)].TrimEnd('\r') != lines2[j].TrimEnd('\r')))
                {
                    currentHunk.Add($"+{lines2[j].TrimEnd('\r')}");
                    j++;
                }
            }

            // Close hunk if we've had enough context after changes
            if (inHunk && i < lines1.Length && j < lines2.Length &&
                lines1[i].TrimEnd('\r') == lines2[j].TrimEnd('\r'))
            {
                int matchCount = 0;
                int ti = i, tj = j;
                while (ti < lines1.Length && tj < lines2.Length &&
                       lines1[ti].TrimEnd('\r') == lines2[tj].TrimEnd('\r') && matchCount < contextLines * 2)
                {
                    matchCount++; ti++; tj++;
                }

                if (matchCount >= contextLines * 2 || (ti >= lines1.Length && tj >= lines2.Length))
                {
                    // Add context after
                    for (int k = 0; k < Math.Min(contextLines, matchCount); k++)
                    {
                        if (i + k < lines1.Length)
                            currentHunk.Add($" {lines1[i + k].TrimEnd('\r')}");
                    }
                    hunks.Add((hunkStart1, hunkStart2, [.. currentHunk]));
                    currentHunk.Clear();
                    inHunk = false;
                    i += Math.Min(contextLines, matchCount);
                    j += Math.Min(contextLines, matchCount);
                }
            }
        }

        if (currentHunk.Count > 0)
        {
            hunks.Add((hunkStart1, hunkStart2, currentHunk));
        }

        foreach (var (start1, start2, lines) in hunks)
        {
            int removed = lines.Count(l => l.StartsWith('-'));
            int added = lines.Count(l => l.StartsWith('+'));
            int context = lines.Count(l => l.StartsWith(' '));
            sb.AppendLine($"@@ -{start1 + 1},{removed + context} +{start2 + 1},{added + context} @@");
            foreach (var line in lines)
                sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    private static async Task<ResourceDiffSummary> CompareWinFilesAsync(string file1Path, string file2Path, DiffReportMode reportMode, G3MCacheOptions? cacheOptions)
    {
        var summary = new ResourceDiffSummary();
        var tempDir = Path.Combine(Path.GetTempPath(), $"g3mtool_diff_{Guid.NewGuid():N}");
        var export1Dir = Path.Combine(tempDir, "file1");
        var export2Dir = Path.Combine(tempDir, "file2");

        try
        {
            var diffSw = Stopwatch.StartNew();
            var diffPhaseSw = new Stopwatch();

            // Phase 1: Hash both files in memory for fast change detection
            LogService.Log("[DiffService] Hashing resources from both files...");
            LogService.Progress(10, 100);

            diffPhaseSw.Restart();
            Dictionary<string, Dictionary<string, string>> hashes1;
            var cached1 = G3MCacheService.TryReadDataCache(file1Path, cacheOptions);
            if (cached1 != null)
            {
                hashes1 = cached1.ResourceHashes;
                LogService.Log($"[Timing] File1 cache: {diffPhaseSw.Elapsed.TotalSeconds:F1}s, RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
            }
            else
            {
                using var stream = OpenDataReadStream(file1Path);
                var data1 = UndertaleIO.Read(stream);
                var loadTime = diffPhaseSw.Elapsed;
                diffPhaseSw.Restart();
                hashes1 = ResourceHashService.HashAll(data1);
                var info = new DataFileInfo
                {
                    Filename = Path.GetFileName(file1Path),
                    Size = new FileInfo(file1Path).Length,
                    Md5 = await HashService.ComputeFileHashAsync(file1Path),
                    BytecodeVersion = data1.GeneralInfo?.BytecodeVersion ?? 0,
                    GmsVersion = GeneralInfoUtil.GetVersionDisplay(data1.GeneralInfo),
                    GeneralInfo = GeneralInfoUtil.ExtractGeneralInfo(data1)
                };
                await G3MCacheService.WriteDataCacheAsync(file1Path, info, hashes1, PatchService.GetResourceNameCountsForReuse(data1), PatchService.GetOrderSensitiveResourceNamesForReuse(data1), cacheOptions);
                LogService.Log($"[Timing] File1 load: {loadTime.TotalSeconds:F1}s, hash: {diffPhaseSw.Elapsed.TotalSeconds:F1}s, RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
            }
            GC.Collect();
            LogService.Progress(25, 100);

            diffPhaseSw.Restart();
            Dictionary<string, Dictionary<string, string>> hashes2;
            var cached2 = G3MCacheService.TryReadDataCache(file2Path, cacheOptions);
            if (cached2 != null)
            {
                hashes2 = cached2.ResourceHashes;
                LogService.Log($"[Timing] File2 cache: {diffPhaseSw.Elapsed.TotalSeconds:F1}s, RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
            }
            else
            {
                using var stream = OpenDataReadStream(file2Path);
                var data2 = UndertaleIO.Read(stream);
                var loadTime = diffPhaseSw.Elapsed;
                diffPhaseSw.Restart();
                hashes2 = ResourceHashService.HashAll(data2);
                var info = new DataFileInfo
                {
                    Filename = Path.GetFileName(file2Path),
                    Size = new FileInfo(file2Path).Length,
                    Md5 = await HashService.ComputeFileHashAsync(file2Path),
                    BytecodeVersion = data2.GeneralInfo?.BytecodeVersion ?? 0,
                    GmsVersion = GeneralInfoUtil.GetVersionDisplay(data2.GeneralInfo),
                    GeneralInfo = GeneralInfoUtil.ExtractGeneralInfo(data2)
                };
                await G3MCacheService.WriteDataCacheAsync(file2Path, info, hashes2, PatchService.GetResourceNameCountsForReuse(data2), PatchService.GetOrderSensitiveResourceNamesForReuse(data2), cacheOptions);
                LogService.Log($"[Timing] File2 load: {loadTime.TotalSeconds:F1}s, hash: {diffPhaseSw.Elapsed.TotalSeconds:F1}s, RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
            }
            GC.Collect();
            LogService.Progress(40, 100);

            // Phase 2: Compare hashes to identify changed types and resources
            diffPhaseSw.Restart();
            var changedTypes = new List<string>();
            foreach (var resourceType in ResourceTypes)
            {
                var h1 = hashes1.GetValueOrDefault(resourceType) ?? [];
                var h2 = hashes2.GetValueOrDefault(resourceType) ?? [];

                var changes = new DiffResourceTypeChanges();
                var names1 = h1.Keys.ToHashSet();
                var names2 = h2.Keys.ToHashSet();

                foreach (var name in names2.Except(names1))
                    changes.New.Add(name);
                foreach (var name in names1.Except(names2))
                    changes.Deleted.Add(name);
                foreach (var name in names1.Intersect(names2))
                    if (h1[name] != h2[name])
                        changes.Changed.Add(name);

                summary.ByType[resourceType] = changes;
                summary.TotalChanged += changes.Changed.Count;
                summary.TotalNew += changes.New.Count;
                summary.TotalDeleted += changes.Deleted.Count;

                if (changes.Changed.Count > 0 || changes.New.Count > 0 || changes.Deleted.Count > 0)
                    changedTypes.Add(resourceType);
            }
            LogService.Log($"[Timing] Hash comparison: {diffPhaseSw.Elapsed.TotalSeconds:F1}s, changed types: {changedTypes.Count}/{ResourceTypes.Length}");
            LogService.Progress(50, 100);

            // Phase 3: Export text-oriented changed types for text diff generation.
            // Binary-heavy types are already compared by resource hashes above; exporting thousands of
            // sprite/texture/audio payloads only to search for small JSON diffs makes large reports unusably slow.
            var textDiffTypes = reportMode == DiffReportMode.Full
                ? [.. changedTypes]
                : changedTypes
                    .Where(type => !BinaryHeavyResourceTypes.Contains(type))
                    .ToList();
            if (textDiffTypes.Count > 0)
            {
                Directory.CreateDirectory(export1Dir);
                Directory.CreateDirectory(export2Dir);

                diffPhaseSw.Restart();
                LogService.Log($"[DiffService] Exporting {textDiffTypes.Count}/{changedTypes.Count} changed type(s) for text diffs...");

                using (var stream = OpenDataReadStream(file1Path))
                {
                    var data1 = UndertaleIO.Read(stream);
                    ResourceExportService.ExportTypes(data1, export1Dir, file1Path, textDiffTypes);
                }
                GC.Collect();

                using (var stream = OpenDataReadStream(file2Path))
                {
                    var data2 = UndertaleIO.Read(stream);
                    ResourceExportService.ExportTypes(data2, export2Dir, file2Path, textDiffTypes);
                }
                GC.Collect();
                LogService.Log($"[Timing] Selective export ({textDiffTypes.Count} text types): {diffPhaseSw.Elapsed.TotalSeconds:F1}s");
                LogService.Progress(80, 100);

                // Collect text diffs for changed resources
                diffPhaseSw.Restart();
                int typeIndex = 0;
                int totalTypes = textDiffTypes.Count;
                foreach (var resourceType in textDiffTypes)
                {
                    var dir1 = Path.Combine(export1Dir, resourceType);
                    var dir2 = Path.Combine(export2Dir, resourceType);
                    var changes = summary.ByType[resourceType];
                    foreach (var changedName in changes.Changed)
                    {
                        var resDir1 = Path.Combine(dir1, changedName);
                        var resDir2 = Path.Combine(dir2, changedName);
                        if (Directory.Exists(resDir1) && Directory.Exists(resDir2))
                            await CollectTextDiffsAsync(resDir1, resDir2, changedName, resourceType, summary.TextDiffs, reportMode);
                    }
                    typeIndex++;
                    LogService.Progress(80 + (typeIndex * 15 / totalTypes), 100);
                }
                LogService.Log($"[Timing] Text diff collection: {diffPhaseSw.Elapsed.TotalSeconds:F1}s");
            }
            else if (changedTypes.Count > 0)
            {
                LogService.Log($"[DiffService] Text diff export skipped; changed types are binary-heavy ({string.Join(", ", changedTypes)})");
            }

            diffSw.Stop();
            LogService.Log($"[Timing] === DIFF TOTAL: {diffSw.Elapsed.TotalSeconds:F1}s === RAM: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");

            return summary;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static async Task<DiffResourceTypeChanges> CompareResourceTypeAsync(string dir1, string dir2)
    {
        var changes = new DiffResourceTypeChanges();

        var resources1 = Directory.Exists(dir1)
            ? Directory.GetDirectories(dir1).Select(Path.GetFileName).Where(n => n != null).ToHashSet()
            : [];

        var resources2 = Directory.Exists(dir2)
            ? Directory.GetDirectories(dir2).Select(Path.GetFileName).Where(n => n != null).ToHashSet()
            : [];

        // New in file2
        foreach (var name in resources2.Except(resources1))
        {
            if (!string.IsNullOrEmpty(name))
                changes.New.Add(name);
        }

        // Deleted (in file1 only)
        foreach (var name in resources1.Except(resources2))
        {
            if (!string.IsNullOrEmpty(name))
                changes.Deleted.Add(name);
        }

        // Changed (exists in both)
        foreach (var name in resources1.Intersect(resources2))
        {
            if (string.IsNullOrEmpty(name)) continue;

            var path1 = Path.Combine(dir1, name);
            var path2 = Path.Combine(dir2, name);

            if (await AreDirectoriesDifferentAsync(path1, path2))
                changes.Changed.Add(name);
        }

        return changes;
    }

    private static async Task<bool> AreDirectoriesDifferentAsync(string dir1, string dir2)
    {
        var files1 = Directory.GetFiles(dir1, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir1, f)).ToHashSet();
        var files2 = Directory.GetFiles(dir2, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir2, f)).ToHashSet();

        if (!files1.SetEquals(files2))
            return true;

        foreach (var relPath in files1)
        {
            var f1 = Path.Combine(dir1, relPath);
            var f2 = Path.Combine(dir2, relPath);

            // Size-first: skip hash if sizes differ (definitely different)
            if (!HashService.FileSizesMatch(f1, f2))
                return true;

            var hash1 = await HashService.ComputeFileHashAsync(f1);
            var hash2 = await HashService.ComputeFileHashAsync(f2);
            if (hash1 != hash2)
                return true;
        }

        return false;
    }
}

public class ResourceDiffSummary
{
    public Dictionary<string, DiffResourceTypeChanges> ByType { get; } = [];
    public List<TextFileDiff> TextDiffs { get; } = [];
    public int TotalChanged { get; set; }
    public int TotalNew { get; set; }
    public int TotalDeleted { get; set; }
}

public class DiffResourceTypeChanges
{
    public List<string> Changed { get; } = [];
    public List<string> New { get; } = [];
    public List<string> Deleted { get; } = [];
}

public enum DiffReportMode
{
    Standard,
    Full
}

public class TextFileDiff
{
    public string ResourceType { get; set; } = "";
    public string ResourceName { get; set; } = "";
    public string FileName { get; set; } = "";
    public string UnifiedDiff { get; set; } = "";
}

public class DiffResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int DifferenceCount { get; set; }
    public string? OutputPath { get; set; }
    public string? Mode { get; set; }
    public int? TotalChanged { get; set; }
    public int? TotalNew { get; set; }
    public int? TotalDeleted { get; set; }
    public int? TextDiffCount { get; set; }
    public Dictionary<string, DiffTypeSummary>? ByType { get; set; }
}

public class DiffTypeSummary
{
    [JsonPropertyName("changed")]
    public int Changed { get; set; }

    [JsonPropertyName("new")]
    public int New { get; set; }

    [JsonPropertyName("deleted")]
    public int Deleted { get; set; }
}
