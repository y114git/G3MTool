using System.IO.Compression;
using System.Text;
using System.Text.Json;
using G3MToolCLI.Models;

namespace G3MToolCLI.Services;

public class DiffService
{
    private readonly HashService _hashService = new();
    private readonly ScriptExecutorService _scriptExecutor = new();

    private static readonly string[] ResourceTypes = 
    {
        "GeneralInfo", "AudioGroups", "TextureGroupInfo", 
        "Sprites", "Backgrounds", "Fonts", "Sounds", "Paths", 
        "Tilesets", "Shaders", "Timelines", "GameObjects", 
        "Rooms", "CodeEntries", "Extensions"
    };

    private static readonly string[] TextExtensions = { ".gml", ".json", ".txt", ".xml" };

    public async Task<DiffResult> CompareAsync(string file1Path, string file2Path, string outputPath)
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

        int differenceCount = 0;

        // Determine comparison type
        if (ext1 == ".zip" && ext2 == ".win")
        {
            // patch.zip vs data.win
            differenceCount = await ComparePatchWithDataAsync(file1Path, file2Path, sb);
        }
        else if (ext1 == ".win" && ext2 == ".zip")
        {
            // data.win vs patch.zip (swap order)
            differenceCount = await ComparePatchWithDataAsync(file2Path, file1Path, sb);
        }
        else if (ext1 == ".zip" && ext2 == ".zip")
        {
            // patch.zip vs patch.zip
            differenceCount = await ComparePatchesAsync(file1Path, file2Path, sb);
        }
        else if (ext1 == ".win" && ext2 == ".win")
        {
            // data.win vs data.win
            differenceCount = await CompareDataFilesAsync(file1Path, file2Path, sb);
        }
        else
        {
            sb.AppendLine("## Unsupported file combination");
            sb.AppendLine();
            sb.AppendLine($"Cannot compare `{ext1}` with `{ext2}`.");
            sb.AppendLine("Supported: `.win` vs `.win`, `.zip` vs `.win`, `.zip` vs `.zip`");
        }

        // Write output
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        await File.WriteAllTextAsync(outputPath, sb.ToString());

        return new DiffResult 
        { 
            Success = true, 
            DifferenceCount = differenceCount,
            OutputPath = outputPath
        };
    }

    private async Task<int> CompareDataFilesAsync(string file1Path, string file2Path, StringBuilder sb)
    {
        LogService.SetOperation("Comparing data files");
        LogService.Progress(0, 100);
        
        var hash1 = await _hashService.ComputeFileHashAsync(file1Path);
        var hash2 = await _hashService.ComputeFileHashAsync(file2Path);
        
        LogService.Progress(10, 100);

        sb.AppendLine("## Files");
        sb.AppendLine();
        sb.AppendLine("| Property | File 1 | File 2 |");
        sb.AppendLine("|:---------|:-------|:-------||");
        sb.AppendLine($"| **Path** | `{Path.GetFileName(file1Path)}` | `{Path.GetFileName(file2Path)}` |");
        sb.AppendLine($"| **Size** | {new FileInfo(file1Path).Length:N0} bytes | {new FileInfo(file2Path).Length:N0} bytes |");
        sb.AppendLine($"| **SHA-256** | `{hash1}` | `{hash2}` |");
        sb.AppendLine();

        if (hash1 == hash2)
        {
            sb.AppendLine("Files are **identical** (same SHA-256 hash).");
            LogService.ProgressComplete();
            return 0;
        }

        var resourceDiffs = await CompareWinFilesAsync(file1Path, file2Path);
        var totalDiff = resourceDiffs.TotalChanged + resourceDiffs.TotalNew + resourceDiffs.TotalDeleted;

        AppendSummaryTable(sb, resourceDiffs);
        await AppendResourceDetailsAsync(sb, resourceDiffs);
        
        LogService.ProgressComplete();

        return totalDiff;
    }

    private async Task<int> ComparePatchWithDataAsync(string patchPath, string dataPath, StringBuilder sb)
    {
        LogService.SetOperation("Comparing patch with data");
        LogService.Progress(0, 100);
        
        sb.AppendLine("## Patch vs Data Comparison");
        sb.AppendLine();
        sb.AppendLine($"| | File |");
        sb.AppendLine("|:--|:-----||");
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
            if (!File.Exists(manifestPath))
            {
                sb.AppendLine("## Error");
                sb.AppendLine();
                sb.AppendLine("Patch does not contain `g3mpatch.json` manifest.");
                return 0;
            }

            var manifestJson = await File.ReadAllTextAsync(manifestPath);
            var manifest = JsonSerializer.Deserialize<G3MPatchManifest>(manifestJson);

            if (manifest?.Resources == null)
            {
                sb.AppendLine("## Error");
                sb.AppendLine();
                sb.AppendLine("Manifest has no resources.");
                return 0;
            }

            // Export only the resource types that are in the patch
            var resourceTypesInPatch = manifest.Resources.Keys.ToHashSet();
            int exportStep = 0;
            int totalExports = resourceTypesInPatch.Count;
            foreach (var resourceType in resourceTypesInPatch)
            {
                var script = $"Export{resourceType}.csx";
                await _scriptExecutor.ExecuteEmbeddedScriptAsync(script, dataPath, dataExportDir);
                exportStep++;
                LogService.Progress(20 + (exportStep * 40 / totalExports), 100);
            }

            // Compare resources from patch with exported data
            var summary = new ResourceDiffSummary();
            var textDiffs = new List<TextFileDiff>();

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
                            await CollectTextDiffsAsync(patchResPath, dataResPath, changed.Name, resourceType, textDiffs);
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

    private async Task<int> ComparePatchesAsync(string patch1Path, string patch2Path, StringBuilder sb)
    {
        LogService.SetOperation("Comparing patches");
        LogService.Progress(0, 100);
        
        sb.AppendLine("## Patches");
        sb.AppendLine();
        sb.AppendLine($"| | File |");
        sb.AppendLine("|:--|:-----||");
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
                        await CollectTextDiffsAsync(resDir1, resDir2, changedName, resourceType, summary.TextDiffs);
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

    private void AppendSummaryTable(StringBuilder sb, ResourceDiffSummary summary)
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

    private void AppendResourceList(StringBuilder sb, ResourceDiffSummary summary)
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

    private async Task AppendResourceDetailsAsync(StringBuilder sb, ResourceDiffSummary summary)
    {
        AppendResourceList(sb, summary);
        
        // Text diffs are collected during comparison for .win vs .win
        if (summary.TextDiffs.Count > 0)
        {
            AppendTextDiffs(sb, summary.TextDiffs);
        }
    }

    private void AppendTextDiffs(StringBuilder sb, List<TextFileDiff> textDiffs)
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

    private async Task CollectTextDiffsAsync(string dir1, string dir2, string resourceName, string resourceType, List<TextFileDiff> textDiffs)
    {
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
                var unifiedDiff = GenerateUnifiedDiff(content1, content2, relPath);
                textDiffs.Add(new TextFileDiff
                {
                    ResourceType = resourceType,
                    ResourceName = resourceName,
                    FileName = relPath,
                    UnifiedDiff = unifiedDiff
                });
            }
        }
    }

    private string GenerateUnifiedDiff(string content1, string content2, string fileName)
    {
        var lines1 = content1.Split('\n');
        var lines2 = content2.Split('\n');

        var sb = new StringBuilder();
        sb.AppendLine($"--- a/{fileName}");
        sb.AppendLine($"+++ b/{fileName}");

        // Simple line-by-line diff (not a full LCS algorithm, but good enough for display)
        int i = 0, j = 0;
        int contextLines = 3;
        var hunks = new List<(int start1, int start2, List<string> lines)>();
        var currentHunk = new List<string>();
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
                    hunks.Add((hunkStart1, hunkStart2, new List<string>(currentHunk)));
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
            int removed = lines.Count(l => l.StartsWith("-"));
            int added = lines.Count(l => l.StartsWith("+"));
            int context = lines.Count(l => l.StartsWith(" "));
            sb.AppendLine($"@@ -{start1 + 1},{removed + context} +{start2 + 1},{added + context} @@");
            foreach (var line in lines)
                sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<ResourceDiffSummary> CompareWinFilesAsync(string file1Path, string file2Path)
    {
        var summary = new ResourceDiffSummary();
        var tempDir = Path.Combine(Path.GetTempPath(), $"g3mtool_diff_{Guid.NewGuid():N}");
        var export1Dir = Path.Combine(tempDir, "file1");
        var export2Dir = Path.Combine(tempDir, "file2");

        try
        {
            Directory.CreateDirectory(export1Dir);
            Directory.CreateDirectory(export2Dir);

            LogService.Log("[DiffService] Exporting resources from both files...");
            LogService.Progress(10, 100);
            
            // Export resources from both files
            await ExportResourcesAsync(file1Path, export1Dir);
            LogService.Progress(40, 100);
            await ExportResourcesAsync(file2Path, export2Dir);
            LogService.Progress(70, 100);

            // Compare each resource type
            int typeIndex = 0;
            int totalTypes = ResourceTypes.Length;
            foreach (var resourceType in ResourceTypes)
            {
                var dir1 = Path.Combine(export1Dir, resourceType);
                var dir2 = Path.Combine(export2Dir, resourceType);

                var changes = await CompareResourceTypeAsync(dir1, dir2);
                summary.ByType[resourceType] = changes;
                summary.TotalChanged += changes.Changed.Count;
                summary.TotalNew += changes.New.Count;
                summary.TotalDeleted += changes.Deleted.Count;

                // Collect text diffs for changed resources
                foreach (var changedName in changes.Changed)
                {
                    var resDir1 = Path.Combine(dir1, changedName);
                    var resDir2 = Path.Combine(dir2, changedName);
                    if (Directory.Exists(resDir1) && Directory.Exists(resDir2))
                    {
                        await CollectTextDiffsAsync(resDir1, resDir2, changedName, resourceType, summary.TextDiffs);
                    }
                }
                
                typeIndex++;
                LogService.Progress(70 + (typeIndex * 25 / totalTypes), 100);
            }

            return summary;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private async Task ExportResourcesAsync(string dataPath, string outputDir)
    {
        var exportScripts = new[]
        {
            "ExportGeneralInfo.csx", "ExportAudioGroups.csx", "ExportTextureGroupInfo.csx",
            "ExportSprites.csx", "ExportBackgrounds.csx", "ExportFonts.csx", 
            "ExportSounds.csx", "ExportPaths.csx", "ExportTilesets.csx",
            "ExportShaders.csx", "ExportTimelines.csx", "ExportGameObjects.csx", 
            "ExportRooms.csx", "ExportCodeEntries.csx", "ExportExtensions.csx"
        };

        foreach (var script in exportScripts)
        {
            await _scriptExecutor.ExecuteEmbeddedScriptAsync(script, dataPath, outputDir);
        }
    }

    private async Task<DiffResourceTypeChanges> CompareResourceTypeAsync(string dir1, string dir2)
    {
        var changes = new DiffResourceTypeChanges();

        var resources1 = Directory.Exists(dir1)
            ? Directory.GetDirectories(dir1).Select(Path.GetFileName).Where(n => n != null).ToHashSet()
            : new HashSet<string?>();

        var resources2 = Directory.Exists(dir2)
            ? Directory.GetDirectories(dir2).Select(Path.GetFileName).Where(n => n != null).ToHashSet()
            : new HashSet<string?>();

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

    private async Task<bool> AreDirectoriesDifferentAsync(string dir1, string dir2)
    {
        var files1 = Directory.GetFiles(dir1, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir1, f)).ToHashSet();
        var files2 = Directory.GetFiles(dir2, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir2, f)).ToHashSet();

        if (!files1.SetEquals(files2))
            return true;

        foreach (var relPath in files1)
        {
            var hash1 = await _hashService.ComputeFileHashAsync(Path.Combine(dir1, relPath));
            var hash2 = await _hashService.ComputeFileHashAsync(Path.Combine(dir2, relPath));
            if (hash1 != hash2)
                return true;
        }

        return false;
    }
}

public class ResourceDiffSummary
{
    public Dictionary<string, DiffResourceTypeChanges> ByType { get; } = new();
    public List<TextFileDiff> TextDiffs { get; } = new();
    public int TotalChanged { get; set; }
    public int TotalNew { get; set; }
    public int TotalDeleted { get; set; }
}

public class DiffResourceTypeChanges
{
    public List<string> Changed { get; } = new();
    public List<string> New { get; } = new();
    public List<string> Deleted { get; } = new();
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
}
