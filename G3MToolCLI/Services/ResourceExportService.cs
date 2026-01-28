using UndertaleModLib;

namespace G3MToolCLI.Services;

public class ResourceExportService
{
    private readonly HashService _hashService = new();

    public async Task<ExportResult> ExportAllResourcesAsync(
        UndertaleData data, 
        string outputDir,
        Action<string>? progressCallback = null)
    {
        try
        {
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            var result = new ExportResult { Success = true };

            // Export each resource type using embedded scripts
            var resourceTypes = new[]
            {
                ("CodeEntries", "ExportCodeEntries.csx"),
                ("Sprites", "ExportSprites.csx"),
                ("Sounds", "ExportSounds.csx"),
                ("Backgrounds", "ExportBackgrounds.csx"),
                ("Fonts", "ExportFonts.csx"),
                ("Shaders", "ExportShaders.csx"),
                ("Rooms", "ExportRooms.csx"),
                ("Tilesets", "ExportTilesets.csx"),
                ("Paths", "ExportPaths.csx"),
                ("Timelines", "ExportTimelines.csx"),
                ("GameObjects", "ExportGameObjects.csx"),
                ("Extensions", "ExportExtensions.csx"),
                ("AudioGroups", "ExportAudioGroups.csx"),
                ("TextureGroupInfo", "ExportTextureGroupInfo.csx"),
                ("GeneralInfo", "ExportGeneralInfo.csx"),
            };

            foreach (var (typeName, scriptName) in resourceTypes)
            {
                progressCallback?.Invoke($"Exporting {typeName}...");
                
                var typeOutputDir = Path.Combine(outputDir, typeName);
                if (!Directory.Exists(typeOutputDir))
                    Directory.CreateDirectory(typeOutputDir);

                // TODO: Execute embedded script with Roslyn
                // For now, just create the directory structure
            }

            result.OutputDirectory = outputDir;
            return result;
        }
        catch (Exception ex)
        {
            return new ExportResult { Success = false, Error = ex.Message };
        }
    }

    public async Task<ExportResult> ExportResourceTypeAsync(
        UndertaleData data,
        string resourceType,
        string outputDir)
    {
        try
        {
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            // TODO: Execute specific export script based on resourceType
            
            return new ExportResult 
            { 
                Success = true, 
                OutputDirectory = outputDir,
                Message = $"Exported {resourceType} to {outputDir}"
            };
        }
        catch (Exception ex)
        {
            return new ExportResult { Success = false, Error = ex.Message };
        }
    }
}

public class ExportResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public string? OutputDirectory { get; set; }
    public int ResourceCount { get; set; }
}
