using System.IO.Compression;
using System.Text.Json;
using G3MToolCLI.Models;
using G3MToolCLI.Utils;
using UndertaleModLib;

namespace G3MToolCLI.Services;

public class PatchService
{
    private readonly HashService _hashService = new();

    public async Task<PatchCreateResult> CreatePatchAsync(
        string originalPath, 
        string modifiedPath, 
        string outputPath)
    {
        if (!File.Exists(originalPath))
            return new PatchCreateResult { Success = false, Error = $"Original file not found: {originalPath}" };
        
        if (!File.Exists(modifiedPath))
            return new PatchCreateResult { Success = false, Error = $"Modified file not found: {modifiedPath}" };

        // If modifiedPath is an xdelta file, apply it first to get the actual modified .win
        string? tempXdeltaResult = null;
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
            LogService.Log("[PatchService] Loading original data file...");
            UndertaleData originalData;
            using (var stream = new FileStream(originalPath, FileMode.Open, FileAccess.Read))
            {
                originalData = UndertaleIO.Read(stream);
            }

            LogService.Log("[PatchService] Loading modified data file...");
            UndertaleData modifiedData;
            using (var stream = new FileStream(modifiedPath, FileMode.Open, FileAccess.Read))
            {
                modifiedData = UndertaleIO.Read(stream);
            }

            LogService.Log($"[PatchService] Original: {originalData.GeneralInfo?.DisplayName?.Content ?? "Unknown"}");
            LogService.Log($"[PatchService] Modified: {modifiedData.GeneralInfo?.DisplayName?.Content ?? "Unknown"}");

            var manifest = new G3MPatchManifest
            {
                Version = "1.0",
                CreatedAt = DateTime.UtcNow.ToString("o"),
                Tool = new Models.ToolInfo { Name = "G3MTool", Version = "1.0.0" },
                Original = new DataFileInfo
                {
                    Filename = Path.GetFileName(originalPath),
                    Size = new FileInfo(originalPath).Length,
                    Sha256 = await _hashService.ComputeFileHashAsync(originalPath),
                    BytecodeVersion = originalData.GeneralInfo?.BytecodeVersion ?? 0,
                    GmsVersion = GeneralInfoHelper.GetVersionDisplay(originalData.GeneralInfo),
                    GeneralInfo = GeneralInfoHelper.ExtractGeneralInfo(originalData)
                },
                Modified = new DataFileInfo
                {
                    Filename = Path.GetFileName(modifiedPath),
                    Size = new FileInfo(modifiedPath).Length,
                    Sha256 = await _hashService.ComputeFileHashAsync(modifiedPath),
                    BytecodeVersion = modifiedData.GeneralInfo?.BytecodeVersion ?? 0,
                    GmsVersion = GeneralInfoHelper.GetVersionDisplay(modifiedData.GeneralInfo),
                    GeneralInfo = GeneralInfoHelper.ExtractGeneralInfo(modifiedData)
                },
                Resources = new Dictionary<string, ResourceTypeChanges>(),
                Statistics = new PatchStatistics()
            };

            // Create temp directories for export
            var tempDir = Path.Combine(Path.GetTempPath(), $"g3mtool_{Guid.NewGuid():N}");
            var originalExportDir = Path.Combine(tempDir, "original");
            var modifiedExportDir = Path.Combine(tempDir, "modified");
            Directory.CreateDirectory(originalExportDir);
            Directory.CreateDirectory(modifiedExportDir);

            try
            {
                // Total steps: export original (1) + export modified (1) + compare (9 types) + archive (1) = 12
                const int totalSteps = 12;
                int currentStep = 0;
                
                LogService.SetOperation("Creating patch");
                LogService.Progress(0, totalSteps); // Show 0% immediately
                
                // Export resources from both files using CSX scripts
                LogService.Log("[PatchService] Exporting original resources...");
                await ExportAllResourcesAsync(originalPath, originalExportDir);
                currentStep++;
                LogService.Progress(currentStep, totalSteps);
                
                LogService.Log("[PatchService] Exporting modified resources...");
                await ExportAllResourcesAsync(modifiedPath, modifiedExportDir);
                currentStep++;
                LogService.Progress(currentStep, totalSteps);

                // Compare resources by SHA hash
                LogService.Log("[PatchService] Comparing resources by hash...");
                
                var resourceTypes = new[] { "Sprites", "CodeEntries", "Sounds", "Backgrounds", "Fonts", "GameObjects", "Rooms", "Paths", "Shaders" };
                
                foreach (var resourceType in resourceTypes)
                {
                    var originalResDir = Path.Combine(originalExportDir, resourceType);
                    var modifiedResDir = Path.Combine(modifiedExportDir, resourceType);
                    
                    if (Directory.Exists(modifiedResDir))
                    {
                        var changes = await CompareResourcesByHashAsync(originalResDir, modifiedResDir);
                        if (changes.HasChanges)
                            manifest.Resources[resourceType] = changes;
                    }
                    
                    currentStep++;
                    LogService.Progress(currentStep, totalSteps);
                }

                // Calculate statistics
                foreach (var (_, changes) in manifest.Resources)
                {
                    manifest.Statistics.TotalChanged += changes.Changed?.Count ?? 0;
                    manifest.Statistics.TotalNew += changes.New?.Count ?? 0;
                    manifest.Statistics.TotalDeleted += changes.Deleted?.Count ?? 0;
                }

                // Create output directory
                var outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);

                // Create patch zip with changed resources
                LogService.Log("[PatchService] Creating patch archive...");
                using (var zipStream = new FileStream(outputPath, FileMode.Create))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    // Add manifest
                    var manifestEntry = archive.CreateEntry("g3mpatch.json", CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(manifestEntry.Open()))
                    {
                        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
                        await writer.WriteAsync(json);
                    }

                    // Add changed and new resources from modified export
                    foreach (var (resourceType, changes) in manifest.Resources)
                    {
                        var modifiedResDir = Path.Combine(modifiedExportDir, resourceType);
                        
                        // Add changed resources - only include files that actually changed
                        foreach (var changed in changes.Changed ?? Enumerable.Empty<ResourceChange>())
                        {
                            await AddResourceToArchiveAsync(archive, modifiedResDir, resourceType, changed.Name!, changed.Files);
                        }
                        
                        // Add new resources - include all files
                        foreach (var newRes in changes.New ?? Enumerable.Empty<ResourceChange>())
                        {
                            await AddResourceToArchiveAsync(archive, modifiedResDir, resourceType, newRes.Name!, newRes.Files);
                        }
                    }
                }
                
                LogService.ProgressComplete();

                return new PatchCreateResult 
                { 
                    Success = true, 
                    Statistics = manifest.Statistics,
                    Message = $"Patch created: {manifest.Statistics.TotalChanged} changed, {manifest.Statistics.TotalNew} new, {manifest.Statistics.TotalDeleted} deleted"
                };
            }
            finally
            {
                // Cleanup temp directories
                try { Directory.Delete(tempDir, true); } catch { }
                // Cleanup temp xdelta result file if created
                if (tempXdeltaResult != null)
                    try { File.Delete(tempXdeltaResult); } catch { }
            }
        }
        catch (Exception ex)
        {
            // Cleanup temp xdelta result file on error
            if (tempXdeltaResult != null)
                try { File.Delete(tempXdeltaResult); } catch { }
            return new PatchCreateResult { Success = false, Error = $"Failed to create patch: {ex.Message}" };
        }
    }

    private async Task ExportAllResourcesAsync(string dataPath, string outputDir)
    {
        var scriptExecutor = new ScriptExecutorService();
        var exportScripts = new[]
        {
            "ExportSprites.csx",
            "ExportCodeEntries.csx", 
            "ExportSounds.csx",
            "ExportBackgrounds.csx",
            "ExportFonts.csx",
            "ExportGameObjects.csx",
            "ExportRooms.csx",
            "ExportPaths.csx",
            "ExportShaders.csx"
        };

        foreach (var script in exportScripts)
        {
            var result = await scriptExecutor.ExecuteEmbeddedScriptAsync(script, dataPath, outputDir);
            if (!result.Success)
            {
                LogService.Log($"[PatchService] Warning: {script} failed: {result.Error}");
            }
        }
    }

    private async Task<ResourceTypeChanges> CompareResourcesByHashAsync(string originalDir, string modifiedDir)
    {
        var changes = new ResourceTypeChanges
        {
            Changed = new List<ResourceChange>(),
            New = new List<ResourceChange>(),
            Deleted = new List<string>()
        };

        // Get all resource directories from both sides
        var originalResources = Directory.Exists(originalDir) 
            ? Directory.GetDirectories(originalDir).Select(Path.GetFileName).ToHashSet()
            : new HashSet<string?>();
        
        var modifiedResources = Directory.Exists(modifiedDir)
            ? Directory.GetDirectories(modifiedDir).Select(Path.GetFileName).ToHashSet()
            : new HashSet<string?>();

        // New resources (in modified but not in original) - include all files
        foreach (var name in modifiedResources.Except(originalResources))
        {
            if (string.IsNullOrEmpty(name)) continue;
            var modifiedResPath = Path.Combine(modifiedDir, name);
            var files = await GetAllFileHashesAsync(modifiedResPath);
            changes.New.Add(new ResourceChange { Name = name, Files = files });
        }

        // Deleted resources (in original but not in modified)
        foreach (var name in originalResources.Except(modifiedResources))
        {
            if (!string.IsNullOrEmpty(name))
                changes.Deleted.Add(name);
        }

        // Changed resources - compare file by file, only include changed files
        foreach (var name in originalResources.Intersect(modifiedResources))
        {
            if (string.IsNullOrEmpty(name)) continue;
            
            var originalResPath = Path.Combine(originalDir, name);
            var modifiedResPath = Path.Combine(modifiedDir, name);
            
            var changedFiles = await CompareResourceFilesAsync(originalResPath, modifiedResPath);
            
            // Only add if there are actually changed files
            if (changedFiles.Count > 0)
            {
                changes.Changed.Add(new ResourceChange { Name = name, Files = changedFiles });
            }
        }

        return changes;
    }

    private async Task<Dictionary<string, string>> GetAllFileHashesAsync(string dirPath)
    {
        var result = new Dictionary<string, string>();
        if (!Directory.Exists(dirPath)) return result;
        
        foreach (var file in Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(dirPath, file);
            var hash = await _hashService.ComputeFileHashAsync(file);
            result[relativePath] = hash;
        }
        return result;
    }

    private async Task<Dictionary<string, string>> CompareResourceFilesAsync(string originalPath, string modifiedPath)
    {
        var changedFiles = new Dictionary<string, string>();
        
        // Get all files from modified resource
        var modifiedFiles = Directory.Exists(modifiedPath) 
            ? Directory.GetFiles(modifiedPath, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
        
        foreach (var modFile in modifiedFiles)
        {
            var relativePath = Path.GetRelativePath(modifiedPath, modFile);
            var origFile = Path.Combine(originalPath, relativePath);
            
            var modHash = await _hashService.ComputeFileHashAsync(modFile);
            
            if (!File.Exists(origFile))
            {
                // New file in resource
                changedFiles[relativePath] = modHash;
            }
            else
            {
                var origHash = await _hashService.ComputeFileHashAsync(origFile);
                if (origHash != modHash)
                {
                    // File changed
                    changedFiles[relativePath] = modHash;
                }
            }
        }
        
        return changedFiles;
    }

    private async Task<string> ComputeDirectoryHashAsync(string dirPath)
    {
        if (!Directory.Exists(dirPath))
            return "";

        var files = Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories).OrderBy(f => f).ToList();
        if (files.Count == 0)
            return "";

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var combinedHash = new List<byte>();

        foreach (var file in files)
        {
            var hash = await _hashService.ComputeFileHashAsync(file);
            combinedHash.AddRange(System.Text.Encoding.UTF8.GetBytes(hash));
        }

        var finalHash = sha256.ComputeHash(combinedHash.ToArray());
        return Convert.ToHexString(finalHash).ToLowerInvariant();
    }

    private async Task AddResourceToArchiveAsync(ZipArchive archive, string sourceDir, string resourceType, string resourceName, Dictionary<string, string>? filesToInclude = null)
    {
        var resourcePath = Path.Combine(sourceDir, resourceName);
        if (!Directory.Exists(resourcePath))
            return;

        foreach (var file in Directory.GetFiles(resourcePath, "*", SearchOption.AllDirectories))
        {
            var relativeToResource = Path.GetRelativePath(resourcePath, file);
            
            // If filesToInclude is specified, only add files that are in the list
            if (filesToInclude != null && !filesToInclude.ContainsKey(relativeToResource))
                continue;
            
            var entryPath = Path.Combine(resourceType, resourceName, relativeToResource).Replace('\\', '/');
            
            var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(file);
            await fileStream.CopyToAsync(entryStream);
        }
    }

    public async Task<PatchApplyResult> ApplyPatchAsync(
        string dataPath, 
        string patchPath, 
        string outputPath,
        bool skipValidation = false)
    {
        if (!File.Exists(dataPath))
            return new PatchApplyResult { Success = false, Error = $"Data file not found: {dataPath}" };
        
        if (!File.Exists(patchPath))
            return new PatchApplyResult { Success = false, Error = $"Patch file not found: {patchPath}" };

        try
        {
            // Validate patch first
            G3MPatchManifest? manifest = null;
            if (!skipValidation)
            {
                var validationResult = await ValidatePatchAsync(patchPath, dataPath);
                if (!validationResult.Success)
                    return new PatchApplyResult { Success = false, Error = $"Patch validation failed: {validationResult.Error}" };
                manifest = validationResult.Manifest;
            }

            LogService.Log("[PatchService] Loading data file...");
            UndertaleData data;
            using (var stream = new FileStream(dataPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            LogService.Log($"[PatchService] Data loaded: {data.GeneralInfo?.DisplayName?.Content ?? "Unknown"}");

            // Extract patch to temp directory
            var tempDir = Path.Combine(Path.GetTempPath(), $"g3mtool_apply_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                LogService.Log("[PatchService] Extracting patch...");
                ZipFile.ExtractToDirectory(patchPath, tempDir);

                // Load manifest if not already loaded
                if (manifest == null)
                {
                    var manifestPath = Path.Combine(tempDir, "g3mpatch.json");
                    if (File.Exists(manifestPath))
                    {
                        var json = await File.ReadAllTextAsync(manifestPath);
                        manifest = JsonSerializer.Deserialize<G3MPatchManifest>(json);
                    }
                }

                if (manifest?.Resources == null)
                {
                    LogService.Log("[PatchService] No resources to apply");
                }
                else
                {
                    // Calculate total resources for smooth progress
                    int totalResources = manifest.Resources.Values.Sum(c => 
                        (c.Changed?.Count ?? 0) + (c.New?.Count ?? 0));
                    // Add weight for each resource type processing + saving
                    int totalSteps = totalResources + manifest.Resources.Count + 1;
                    int currentStep = 0;
                    
                    LogService.SetOperation("Applying patch");
                    LogService.Progress(0, totalSteps); // Show 0% immediately
                    
                    // Apply resources from patch
                    var scriptExecutor = new ScriptExecutorService();
                    
                    // Track current data source - starts with original, then uses output after first import
                    string currentDataPath = dataPath;
                    bool isFirstImport = true;
                    
                    // Fixed import order - metadata first, then assets, code near end, extensions last
                    var importOrder = new[] 
                    { 
                        "GeneralInfo", "AudioGroups", "TextureGroupInfo", 
                        "Sprites", "Backgrounds", "Fonts", "Sounds", "Paths", 
                        "Tilesets", "Shaders", "Timelines", "GameObjects", 
                        "Rooms", "CodeEntries", "Extensions" 
                    };
                    
                    foreach (var resourceType in importOrder.Where(rt => manifest.Resources.ContainsKey(rt)))
                    {
                        var changes = manifest.Resources[resourceType];
                        var resourceDir = Path.Combine(tempDir, resourceType);
                        int resourceCount = (changes.Changed?.Count ?? 0) + (changes.New?.Count ?? 0);
                        
                        // Show progress at start of each resource type
                        LogService.Progress(currentStep, totalSteps);
                        
                        if (!Directory.Exists(resourceDir))
                        {
                            currentStep += resourceCount + 1;
                            continue;
                        }

                        LogService.Log($"[PatchService] Applying {resourceType}...");

                        // Use import script for this resource type
                        // After first import, read from output file to chain modifications
                        var importScript = $"Import{resourceType}.csx";
                        var result = await scriptExecutor.ExecuteEmbeddedScriptAsync(
                            importScript, currentDataPath, outputPath, resourceDir);

                        if (!result.Success)
                        {
                            LogService.Warning($"Failed to apply {resourceType}: {result.Error}");
                        }
                        else if (isFirstImport)
                        {
                            // After first successful import, use output as source for next imports
                            currentDataPath = outputPath;
                            isFirstImport = false;
                        }
                        
                        // Update progress after processing
                        currentStep += resourceCount + 1;
                        LogService.Progress(currentStep, totalSteps);
                    }
                    
                    LogService.ProgressComplete();
                }

                LogService.Log("[PatchService] Patch applied successfully");
                return new PatchApplyResult { Success = true };
            }
            finally
            {
                // Cleanup
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            return new PatchApplyResult { Success = false, Error = $"Failed to apply patch: {ex.Message}" };
        }
    }

    public async Task<PatchValidateResult> ValidatePatchAsync(string patchPath, string? dataPath = null)
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
                var dataHash = await _hashService.ComputeFileHashAsync(dataPath);
                if (manifest.Original?.Sha256 != null && manifest.Original.Sha256 != dataHash)
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
