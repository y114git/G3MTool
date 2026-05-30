


using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using ImageMagick;

// ============================================================================
// DETAILED LOGGING SYSTEM
// ============================================================================
static StreamWriter _logWriter = null;
static string _logPath = null;

void InitLog(string scriptName)
{
    if (!Verbose) return;
    string logDir = Path.Combine(Path.GetTempPath(), "g3mtool_logs");
    Directory.CreateDirectory(logDir);
    _logPath = Path.Combine(logDir, $"{scriptName}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
    _logWriter = new StreamWriter(_logPath, false, Encoding.UTF8);
    _logWriter.AutoFlush = true;
    Log($"=== {scriptName} Log Started at {DateTime.Now} ===");
    Console.WriteLine($"[{scriptName}] Detailed log: {_logPath}");
}

void Log(string message)
{
    if (_logWriter != null)
    {
        _logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }
}

void CloseLog()
{
    if (_logWriter != null)
    {
        Log("=== Log Ended ===");
        _logWriter.Close();
        _logWriter = null;
        if (!string.IsNullOrEmpty(_logPath))
        {
            try { File.Delete(_logPath); } catch { }
            try
            {
                string logDir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(logDir) &&
                    Directory.Exists(logDir) &&
                    !Directory.EnumerateFileSystemEntries(logDir).Any())
                {
                    Directory.Delete(logDir);
                }
            }
            catch { }
        }
    }
}

void PrintLine(string s) { if (Verbose) Console.WriteLine(s); Log(s); }

string GetInputDirectory()
{
    string inputDir = InputDir;
    if (string.IsNullOrEmpty(inputDir))
        throw new Exception("InputDir is not set.");
    if (!Directory.Exists(inputDir))
        throw new Exception($"INPUT_DIR directory does not exist: {inputDir}");
    return inputDir;
}




EnsureDataLoaded();

// Initialize detailed logging
InitLog("ImportSprites");

static List<MagickImage> imagesToCleanup = new();
Regex sprFrameRegex = new(@"^(.+?)(?:_(\d+))$", RegexOptions.Compiled);
bool noMasksForBasicRectangles = Data.IsVersionAtLeast(2022, 9);

string spritesPath = GetInputDirectory();
PrintLine($"[ImportSprites] Importing from: {spritesPath}");

// Log initial state
Log($"INITIAL STATE: Data.Sprites.Count = {Data.Sprites.Count}");
Log($"INITIAL STATE: Data.EmbeddedTextures.Count = {Data.EmbeddedTextures.Count}");
Log($"INITIAL STATE: Data.TexturePageItems.Count = {Data.TexturePageItems.Count}");

// Log first 20 sprites for reference
Log("INITIAL SPRITES (first 20):");
for (int i = 0; i < Math.Min(20, Data.Sprites.Count); i++)
{
    var spr = Data.Sprites[i];
    Log($"  [{i}] {spr?.Name?.Content ?? "(null)"} - Textures: {spr?.Textures?.Count ?? 0}");
}

var pngFiles = Directory.GetFiles(spritesPath, "*.png", SearchOption.AllDirectories);
var jsonFiles = Directory.GetFiles(spritesPath, "*.json", SearchOption.AllDirectories);
if (pngFiles.Length == 0 && jsonFiles.Length == 0)
{
    PrintLine("[ImportSprites] No sprite data found - nothing to import.");
    return;
}

PrintLine($"[ImportSprites] Found {pngFiles.Length} PNG files and {jsonFiles.Length} JSON metadata files to process.");

int newSpritesCreated = 0;
int existingSpritesUpdated = 0;

Dictionary<string, JsonElement?> spriteMetadataCache = new();
Dictionary<string, List<Dictionary<string, object>>> textureFrameCache = new();

JsonElement? TryLoadSpriteMetadata(string spritesFolder, string spriteName)
{
    if (spriteMetadataCache.TryGetValue(spriteName, out JsonElement? cached))
        return cached;

    string spriteFolder = Path.Combine(spritesFolder, spriteName);
    string metaFile = Path.Combine(spriteFolder, $"{spriteName}.json");


    if (!File.Exists(metaFile))
        metaFile = Path.Combine(spriteFolder, "sprite_meta.json");

    if (!File.Exists(metaFile))
    {
        spriteMetadataCache[spriteName] = null;
        return null;
    }

    try
    {
        string jsonContent = File.ReadAllText(metaFile, Encoding.UTF8);
        JsonDocument jsonDoc = JsonDocument.Parse(jsonContent);
        spriteMetadataCache[spriteName] = jsonDoc.RootElement.Clone();
        return spriteMetadataCache[spriteName];
    }
    catch
    {
        spriteMetadataCache[spriteName] = null;
        return null;
    }
}

void ApplySpriteMetadata(UndertaleSprite sprite, JsonElement meta)
{
    // Size - critical for rendering
    if (meta.TryGetProperty("width", out JsonElement width))
        sprite.Width = (uint)width.GetInt64();
    if (meta.TryGetProperty("height", out JsonElement height))
        sprite.Height = (uint)height.GetInt64();

    if (meta.TryGetProperty("originX", out JsonElement originX))
        sprite.OriginX = originX.GetInt32();
    if (meta.TryGetProperty("originY", out JsonElement originY))
        sprite.OriginY = originY.GetInt32();

    if (meta.TryGetProperty("marginLeft", out JsonElement marginLeft))
        sprite.MarginLeft = marginLeft.GetInt32();
    if (meta.TryGetProperty("marginRight", out JsonElement marginRight))
        sprite.MarginRight = marginRight.GetInt32();
    if (meta.TryGetProperty("marginTop", out JsonElement marginTop))
        sprite.MarginTop = marginTop.GetInt32();
    if (meta.TryGetProperty("marginBottom", out JsonElement marginBottom))
        sprite.MarginBottom = marginBottom.GetInt32();

    if (meta.TryGetProperty("transparent", out JsonElement transparent))
        sprite.Transparent = transparent.GetBoolean();
    if (meta.TryGetProperty("smooth", out JsonElement smooth))
        sprite.Smooth = smooth.GetBoolean();
    if (meta.TryGetProperty("preload", out JsonElement preload))
        sprite.Preload = preload.GetBoolean();

    if (meta.TryGetProperty("bboxMode", out JsonElement bboxMode))
        sprite.BBoxMode = (uint)bboxMode.GetInt64();
    if (meta.TryGetProperty("sepMasks", out JsonElement sepMasks))
        sprite.SepMasks = (UndertaleSprite.SepMaskType)sepMasks.GetInt32();

    if (Data.IsGameMaker2())
    {
        // GMS2+ specific properties
        if (meta.TryGetProperty("isSpecialType", out JsonElement isSpecialType))
            sprite.IsSpecialType = isSpecialType.GetBoolean();
        if (meta.TryGetProperty("sVersion", out JsonElement sVersion))
            sprite.SVersion = (uint)sVersion.GetInt64();
        if (meta.TryGetProperty("sSpriteType", out JsonElement sSpriteType))
            sprite.SSpriteType = (UndertaleSprite.SpriteType)sSpriteType.GetInt32();

        if (meta.TryGetProperty("gms2PlaybackSpeed", out JsonElement playbackSpeed))
            sprite.GMS2PlaybackSpeed = (float)playbackSpeed.GetDouble();
        if (meta.TryGetProperty("gms2PlaybackSpeedType", out JsonElement playbackSpeedType))
            sprite.GMS2PlaybackSpeedType = (AnimSpeedType)playbackSpeedType.GetInt32();
    }

    // Import collision masks from metadata
    if (meta.TryGetProperty("collisionMasks", out JsonElement collisionMasks) && collisionMasks.ValueKind == JsonValueKind.Array)
    {
        sprite.CollisionMasks.Clear();
        foreach (var maskElm in collisionMasks.EnumerateArray())
        {
            var mask = new UndertaleSprite.MaskEntry();
            if (maskElm.TryGetProperty("width", out JsonElement widthElm))
                mask.Width = (int)widthElm.GetInt64();
            if (maskElm.TryGetProperty("height", out JsonElement heightElm))
                mask.Height = (int)heightElm.GetInt64();
            if (maskElm.TryGetProperty("data", out JsonElement dataElm))
            {
                string base64 = dataElm.GetString();
                if (!string.IsNullOrEmpty(base64))
                    mask.Data = Convert.FromBase64String(base64);
            }
            sprite.CollisionMasks.Add(mask);
        }
    }

    if (meta.TryGetProperty("nineSlice", out JsonElement nineSlice) && Data.IsVersionAtLeast(2, 3, 2))
    {
        if (sprite.V3NineSlice == null)
            sprite.V3NineSlice = new UndertaleSprite.NineSlice();

        if (nineSlice.TryGetProperty("left", out JsonElement nsLeft))
            sprite.V3NineSlice.Left = nsLeft.GetInt32();
        if (nineSlice.TryGetProperty("top", out JsonElement nsTop))
            sprite.V3NineSlice.Top = nsTop.GetInt32();
        if (nineSlice.TryGetProperty("right", out JsonElement nsRight))
            sprite.V3NineSlice.Right = nsRight.GetInt32();
        if (nineSlice.TryGetProperty("bottom", out JsonElement nsBottom))
            sprite.V3NineSlice.Bottom = nsBottom.GetInt32();
        if (nineSlice.TryGetProperty("enabled", out JsonElement nsEnabled))
            sprite.V3NineSlice.Enabled = nsEnabled.GetBoolean();

        if (nineSlice.TryGetProperty("tileModes", out JsonElement tileModes) && tileModes.ValueKind == JsonValueKind.Array)
        {
            var modesArray = tileModes.EnumerateArray().ToArray();
            for (int i = 0; i < Math.Min(5, modesArray.Length); i++)
                sprite.V3NineSlice.TileModes[i] = (UndertaleSprite.NineSlice.TileMode)modesArray[i].GetInt32();
        }
    }
}

List<Dictionary<string, object>> TryGetTextureFrameData(string spritesFolder, string spriteName)
{
    if (textureFrameCache.TryGetValue(spriteName, out var cached))
        return cached;

    JsonElement? meta = TryLoadSpriteMetadata(spritesFolder, spriteName);
    if (meta.HasValue && meta.Value.TryGetProperty("textureFrames", out JsonElement framesElm) && framesElm.ValueKind == JsonValueKind.Array)
    {
        var frames = new List<Dictionary<string, object>>();
        foreach (var frameElm in framesElm.EnumerateArray())
        {
            var frame = new Dictionary<string, object>();
            if (frameElm.TryGetProperty("frameIndex", out JsonElement idxElm))
                frame["frameIndex"] = idxElm.GetInt32();
            if (frameElm.TryGetProperty("isNull", out JsonElement nullElm))
                frame["isNull"] = nullElm.GetBoolean();
            if (frameElm.TryGetProperty("texturePageIndex", out JsonElement tpIdxElm))
                frame["texturePageIndex"] = tpIdxElm.GetInt32();
            if (frameElm.TryGetProperty("sourceX", out JsonElement sxElm))
                frame["sourceX"] = (ushort)sxElm.GetInt32();
            if (frameElm.TryGetProperty("sourceY", out JsonElement syElm))
                frame["sourceY"] = (ushort)syElm.GetInt32();
            if (frameElm.TryGetProperty("sourceWidth", out JsonElement swElm))
                frame["sourceWidth"] = (ushort)swElm.GetInt32();
            if (frameElm.TryGetProperty("sourceHeight", out JsonElement shElm))
                frame["sourceHeight"] = (ushort)shElm.GetInt32();
            if (frameElm.TryGetProperty("targetX", out JsonElement txElm))
                frame["targetX"] = (ushort)txElm.GetInt32();
            if (frameElm.TryGetProperty("targetY", out JsonElement tyElm))
                frame["targetY"] = (ushort)tyElm.GetInt32();
            if (frameElm.TryGetProperty("targetWidth", out JsonElement twElm))
                frame["targetWidth"] = (ushort)twElm.GetInt32();
            if (frameElm.TryGetProperty("targetHeight", out JsonElement thElm))
                frame["targetHeight"] = (ushort)thElm.GetInt32();
            if (frameElm.TryGetProperty("boundingWidth", out JsonElement bwElm))
                frame["boundingWidth"] = (ushort)bwElm.GetInt32();
            if (frameElm.TryGetProperty("boundingHeight", out JsonElement bhElm))
                frame["boundingHeight"] = (ushort)bhElm.GetInt32();
            frames.Add(frame);
        }
        textureFrameCache[spriteName] = frames;
        return frames;
    }

    textureFrameCache[spriteName] = null;
    return null;
}

void ApplyTextureFrameProperties(UndertaleTexturePageItem tpi, string spriteName, int frameIndex, List<Dictionary<string, object>> frameData)
{
    if (frameData == null || frameIndex >= frameData.Count)
        return;

    var frame = frameData[frameIndex];
    if (frame.ContainsKey("isNull") && (bool)frame["isNull"])
        return;

    if (frame.ContainsKey("targetX"))
        tpi.TargetX = (ushort)frame["targetX"];
    if (frame.ContainsKey("targetY"))
        tpi.TargetY = (ushort)frame["targetY"];
    if (frame.ContainsKey("targetWidth"))
        tpi.TargetWidth = (ushort)frame["targetWidth"];
    if (frame.ContainsKey("targetHeight"))
        tpi.TargetHeight = (ushort)frame["targetHeight"];
}

// Helper class to hold sprite import data for two-phase processing
class SpriteImportData
{
    public int TargetIndex;
    public string Name;           // Actual sprite name (may be empty string)
    public string FolderName;     // Folder name used for loading files
    public string FolderPath;
    public JsonElement? Meta;
    public List<Dictionary<string, object>> FrameData;
    public bool IsNew;
    public bool HasValidTextureIndex;
}

// Try direct import using JSON metadata (no texture repacking)
// This mode updates existing sprites and creates new sprites using texturePageIndex from metadata
bool TryDirectImportFromMetadata()
{
    // Get all sprite folders that have JSON metadata
    var spriteFolders = Directory.GetDirectories(spritesPath);
    if (spriteFolders.Length == 0) return false;

    // ============================================================================
    // PHASE 1: Collect all sprite data and determine which are new vs existing
    // ============================================================================
    var importDataList = new List<SpriteImportData>();
    int maxTargetIndex = -1;

    foreach (string spriteFolder in spriteFolders)
    {
        string folderName = Path.GetFileName(spriteFolder);
        JsonElement? meta = TryLoadSpriteMetadata(spritesPath, folderName);
        if (!meta.HasValue) continue;

        // Get the actual sprite name from metadata (may be empty string for unnamed sprites)
        string spriteName = folderName;
        if (meta.Value.TryGetProperty("name", out JsonElement nameElm))
        {
            string jsonName = nameElm.GetString();
            // For empty string names, keep the empty string (don't fall back to folderName)
            spriteName = jsonName ?? folderName;
        }

        var frameData = TryGetTextureFrameData(spritesPath, folderName);
        bool isNew = Data.Sprites.ByName(spriteName) == null;

        int targetIndex = -1;
        if (meta.Value.TryGetProperty("index", out JsonElement indexElm))
        {
            targetIndex = indexElm.GetInt32();
        }

        // Sprites with 0 textures are valid (e.g., spr_notasprite) - they just have empty textureFrames
        bool hasValidTextureIndex = (frameData != null && frameData.Count == 0) || // Empty sprite - valid
            (frameData != null && frameData.Any(f =>
                f.ContainsKey("texturePageIndex") && Convert.ToInt32(f["texturePageIndex"]) >= 0 && Convert.ToInt32(f["texturePageIndex"]) < Data.EmbeddedTextures.Count));

        importDataList.Add(new SpriteImportData
        {
            TargetIndex = targetIndex,
            Name = spriteName,
            FolderName = folderName,
            FolderPath = spriteFolder,
            Meta = meta,
            FrameData = frameData,
            IsNew = isNew,
            HasValidTextureIndex = hasValidTextureIndex
        });

        if (isNew && targetIndex > maxTargetIndex)
        {
            maxTargetIndex = targetIndex;
        }
    }

    Console.WriteLine($"[ImportSprites] Collected {importDataList.Count} sprites. New: {importDataList.Count(d => d.IsNew)}, Existing: {importDataList.Count(d => !d.IsNew)}");

    // ============================================================================
    // PHASE 2: Append new sprites (ImportAssetOrder will reorder later)
    // ============================================================================
    var newSpritesWithValidTextures = importDataList
        .Where(d => d.IsNew && d.HasValidTextureIndex)
        .ToList();

    int originalCount = Data.Sprites.Count;
    Console.WriteLine($"[ImportSprites] Original count: {originalCount}, New sprites to add: {newSpritesWithValidTextures.Count}");

    int updated = 0;
    int created = 0;

    foreach (var importData in newSpritesWithValidTextures)
    {
        var sprite = CreateSpriteFromMetadata(importData);
        if (sprite != null)
        {
            Data.Sprites.Add(sprite);
            newSpritesCreated++;
            created++;
            Log($"APPENDED SPRITE: '{importData.Name}' at index {Data.Sprites.Count - 1}");
        }
    }

    Console.WriteLine($"[ImportSprites] After additions: {Data.Sprites.Count} sprites");

    // ============================================================================
    // PHASE 3: Update existing sprites (metadata + texture frame references)
    // ============================================================================
    foreach (var importData in importDataList.Where(d => !d.IsNew))
    {
        UndertaleSprite sprite = Data.Sprites.ByName(importData.Name);
        if (sprite != null && importData.Meta.HasValue)
        {
            ApplySpriteMetadata(sprite, importData.Meta.Value);

            // Rebuild texture frame references from patch metadata
            // Save old TPIs so we can reuse them (avoids changing TPI count)
            if (importData.FrameData != null)
            {
                var oldTPIs = new List<UndertaleTexturePageItem>();
                foreach (var texEntry in sprite.Textures)
                {
                    if (texEntry?.Texture != null)
                        oldTPIs.Add(texEntry.Texture);
                }

                sprite.Textures.Clear();
                bool hasInvalidTexture = false;
                int oldTPIIndex = 0;

                foreach (var frame in importData.FrameData)
                {
                    if (frame.ContainsKey("isNull") && (bool)frame["isNull"])
                    {
                        var nullEntry = new UndertaleSprite.TextureEntry();
                        nullEntry.Texture = null;
                        sprite.Textures.Add(nullEntry);
                        continue;
                    }

                    int texturePageIndex = frame.ContainsKey("texturePageIndex") ? Convert.ToInt32(frame["texturePageIndex"]) : -1;

                    if (texturePageIndex < 0 || texturePageIndex >= Data.EmbeddedTextures.Count)
                    {
                        Log($"  INVALID texturePageIndex {texturePageIndex} for existing sprite {importData.Name} (max: {Data.EmbeddedTextures.Count - 1})");
                        hasInvalidTexture = true;
                        break;
                    }

                    ushort sourceX = frame.ContainsKey("sourceX") ? (ushort)Convert.ToInt32(frame["sourceX"]) : (ushort)0;
                    ushort sourceY = frame.ContainsKey("sourceY") ? (ushort)Convert.ToInt32(frame["sourceY"]) : (ushort)0;
                    ushort sourceWidth = frame.ContainsKey("sourceWidth") ? (ushort)Convert.ToInt32(frame["sourceWidth"]) : (ushort)0;
                    ushort sourceHeight = frame.ContainsKey("sourceHeight") ? (ushort)Convert.ToInt32(frame["sourceHeight"]) : (ushort)0;

                    // First try to find an existing TPI with matching coordinates
                    var tpi = FindExistingTPI(texturePageIndex, sourceX, sourceY, sourceWidth, sourceHeight);

                    if (tpi == null && oldTPIIndex < oldTPIs.Count)
                    {
                        // Reuse old TPI and update its properties to match TARGET
                        tpi = oldTPIs[oldTPIIndex++];
                        tpi.TexturePage = Data.EmbeddedTextures[texturePageIndex];
                        tpi.SourceX = sourceX;
                        tpi.SourceY = sourceY;
                        tpi.SourceWidth = sourceWidth;
                        tpi.SourceHeight = sourceHeight;
                        if (frame.ContainsKey("targetX")) tpi.TargetX = (ushort)Convert.ToInt32(frame["targetX"]);
                        if (frame.ContainsKey("targetY")) tpi.TargetY = (ushort)Convert.ToInt32(frame["targetY"]);
                        if (frame.ContainsKey("targetWidth")) tpi.TargetWidth = (ushort)Convert.ToInt32(frame["targetWidth"]);
                        if (frame.ContainsKey("targetHeight")) tpi.TargetHeight = (ushort)Convert.ToInt32(frame["targetHeight"]);
                        if (frame.ContainsKey("boundingWidth")) tpi.BoundingWidth = (ushort)Convert.ToInt32(frame["boundingWidth"]);
                        if (frame.ContainsKey("boundingHeight")) tpi.BoundingHeight = (ushort)Convert.ToInt32(frame["boundingHeight"]);
                        Log($"  Reused OLD TPI for existing sprite {importData.Name}: TexPage={texturePageIndex}, Src=({sourceX},{sourceY})");
                    }
                    else if (tpi == null)
                    {
                        // No old TPI to reuse and no existing match — create new
                        tpi = new UndertaleTexturePageItem();
                        tpi.Name = new UndertaleString($"PageItem {Data.TexturePageItems.Count}");
                        tpi.TexturePage = Data.EmbeddedTextures[texturePageIndex];
                        tpi.SourceX = sourceX;
                        tpi.SourceY = sourceY;
                        tpi.SourceWidth = sourceWidth;
                        tpi.SourceHeight = sourceHeight;
                        if (frame.ContainsKey("targetX")) tpi.TargetX = (ushort)Convert.ToInt32(frame["targetX"]);
                        if (frame.ContainsKey("targetY")) tpi.TargetY = (ushort)Convert.ToInt32(frame["targetY"]);
                        if (frame.ContainsKey("targetWidth")) tpi.TargetWidth = (ushort)Convert.ToInt32(frame["targetWidth"]);
                        if (frame.ContainsKey("targetHeight")) tpi.TargetHeight = (ushort)Convert.ToInt32(frame["targetHeight"]);
                        if (frame.ContainsKey("boundingWidth")) tpi.BoundingWidth = (ushort)Convert.ToInt32(frame["boundingWidth"]);
                        if (frame.ContainsKey("boundingHeight")) tpi.BoundingHeight = (ushort)Convert.ToInt32(frame["boundingHeight"]);
                        Data.TexturePageItems.Add(tpi);
                        Log($"  Created NEW TPI for existing sprite {importData.Name}: TexPage={texturePageIndex}, Src=({sourceX},{sourceY})");
                    }
                    else
                    {
                        // Found existing match — update its properties to match TARGET
                        ApplyTextureFrameProperties(tpi, importData.Name, sprite.Textures.Count, importData.FrameData);
                    }

                    var texEntry = new UndertaleSprite.TextureEntry();
                    texEntry.Texture = tpi;
                    sprite.Textures.Add(texEntry);
                }

                if (hasInvalidTexture)
                {
                    Log($"  WARNING: Existing sprite '{importData.Name}' has invalid texture references, texture frames may be incomplete");
                }
            }

            existingSpritesUpdated++;
            updated++;
        }
    }

    // Log sprites skipped due to invalid texture indices
    var skippedSprites = importDataList.Where(d => d.IsNew && !d.HasValidTextureIndex).ToList();
    if (skippedSprites.Count > 0)
    {
        Console.WriteLine($"[ImportSprites] Skipped {skippedSprites.Count} new sprites (no valid texturePageIndex - will try texture repacking)");
    }

    if (updated > 0 || created > 0)
    {
        Console.WriteLine($"[ImportSprites] Direct import: {updated} updated, {created} new sprites created");
        return true;
    }

    return false;
}

// Helper to find existing TPI by texture page and source coordinates
UndertaleTexturePageItem FindExistingTPI(int texturePageIndex, ushort sourceX, ushort sourceY, ushort sourceWidth, ushort sourceHeight)
{
    if (texturePageIndex < 0 || texturePageIndex >= Data.EmbeddedTextures.Count)
        return null;

    var texturePage = Data.EmbeddedTextures[texturePageIndex];

    foreach (var tpi in Data.TexturePageItems)
    {
        if (tpi.TexturePage == texturePage &&
            tpi.SourceX == sourceX &&
            tpi.SourceY == sourceY &&
            tpi.SourceWidth == sourceWidth &&
            tpi.SourceHeight == sourceHeight)
        {
            return tpi;
        }
    }
    return null;
}

UndertaleSprite CreateSpriteFromMetadata(SpriteImportData importData)
{
    // Allow sprites with 0 textures (e.g., spr_notasprite) - they just have empty Textures list
    if (importData.FrameData == null)
        return null;

    var sprite = new UndertaleSprite();
    sprite.Name = Data.Strings.MakeString(importData.Name);

    if (importData.Meta.HasValue)
        ApplySpriteMetadata(sprite, importData.Meta.Value);

    // Create or find TexturePageItems for each frame using metadata
    bool hasInvalidTexture = false;
    foreach (var frame in importData.FrameData)
    {
        if (frame.ContainsKey("isNull") && (bool)frame["isNull"])
        {
            // CRITICAL FIX: Don't add null to Textures list - add TextureEntry with null Texture instead
            var nullEntry = new UndertaleSprite.TextureEntry();
            nullEntry.Texture = null;
            sprite.Textures.Add(nullEntry);
            continue;
        }

        int texturePageIndex = frame.ContainsKey("texturePageIndex") ? Convert.ToInt32(frame["texturePageIndex"]) : -1;

        if (texturePageIndex < 0 || texturePageIndex >= Data.EmbeddedTextures.Count)
        {
            // DON'T add null textures - this causes corruption!
            // Mark sprite as invalid and return null instead
            Log($"  INVALID texturePageIndex {texturePageIndex} for {importData.Name} (max: {Data.EmbeddedTextures.Count - 1})");
            hasInvalidTexture = true;
            break;
        }

        ushort sourceX = frame.ContainsKey("sourceX") ? (ushort)Convert.ToInt32(frame["sourceX"]) : (ushort)0;
        ushort sourceY = frame.ContainsKey("sourceY") ? (ushort)Convert.ToInt32(frame["sourceY"]) : (ushort)0;
        ushort sourceWidth = frame.ContainsKey("sourceWidth") ? (ushort)Convert.ToInt32(frame["sourceWidth"]) : (ushort)0;
        ushort sourceHeight = frame.ContainsKey("sourceHeight") ? (ushort)Convert.ToInt32(frame["sourceHeight"]) : (ushort)0;

        // Try to find existing TPI with matching coordinates
        var tpi = FindExistingTPI(texturePageIndex, sourceX, sourceY, sourceWidth, sourceHeight);

        if (tpi == null)
        {
            // Create new TPI only if no existing one found
            tpi = new UndertaleTexturePageItem();
            tpi.Name = new UndertaleString($"PageItem {Data.TexturePageItems.Count}");
            tpi.TexturePage = Data.EmbeddedTextures[texturePageIndex];
            tpi.SourceX = sourceX;
            tpi.SourceY = sourceY;
            tpi.SourceWidth = sourceWidth;
            tpi.SourceHeight = sourceHeight;

            if (frame.ContainsKey("targetX")) tpi.TargetX = (ushort)Convert.ToInt32(frame["targetX"]);
            if (frame.ContainsKey("targetY")) tpi.TargetY = (ushort)Convert.ToInt32(frame["targetY"]);
            if (frame.ContainsKey("targetWidth")) tpi.TargetWidth = (ushort)Convert.ToInt32(frame["targetWidth"]);
            if (frame.ContainsKey("targetHeight")) tpi.TargetHeight = (ushort)Convert.ToInt32(frame["targetHeight"]);
            if (frame.ContainsKey("boundingWidth")) tpi.BoundingWidth = (ushort)Convert.ToInt32(frame["boundingWidth"]);
            if (frame.ContainsKey("boundingHeight")) tpi.BoundingHeight = (ushort)Convert.ToInt32(frame["boundingHeight"]);

            Data.TexturePageItems.Add(tpi);
            Log($"  Created NEW TPI for {importData.Name}: TexPage={texturePageIndex}, Src=({sourceX},{sourceY})");
        }
        else
        {
            Log($"  Found EXISTING TPI for {importData.Name}: index={Data.TexturePageItems.IndexOf(tpi)}, TexPage={texturePageIndex}, Src=({sourceX},{sourceY})");
        }

        var texEntry = new UndertaleSprite.TextureEntry();
        texEntry.Texture = tpi;
        sprite.Textures.Add(texEntry);
    }

    // If any frame had invalid texture, don't create this sprite
    if (hasInvalidTexture)
    {
        Log($"  Skipping sprite '{importData.Name}' due to invalid texture references");
        return null;
    }

    return sprite;
}

// Try direct import first - update existing sprites using JSON metadata
TryDirectImportFromMetadata();

// Check if there are any new sprites that need texture repacking
// NOTE: We need to check the actual sprite name from JSON metadata, not the folder name
// because unnamed sprites use folder names like __unnamed_sprite__idx5890 but have empty actual names
var spriteFolders = Directory.GetDirectories(spritesPath);
var newSpriteFolders = spriteFolders.Where(folder =>
{
    string folderName = Path.GetFileName(folder);
    // Try to get actual sprite name from metadata
    JsonElement? meta = TryLoadSpriteMetadata(spritesPath, folderName);
    string actualName = folderName;
    if (meta.HasValue && meta.Value.TryGetProperty("name", out JsonElement nameElm))
    {
        actualName = nameElm.GetString() ?? folderName;
    }
    return Data.Sprites.ByName(actualName) == null;
}).ToList();

Console.WriteLine($"[ImportSprites] Found {spriteFolders.Length} sprite folders, {newSpriteFolders.Count} are new sprites");

if (newSpriteFolders.Count == 0)
{
    PrintLine("[ImportSprites] All sprites updated via direct import (no new sprites to create)");
    return;
}

PrintLine("[ImportSprites] Creating new sprites via texture repacking...");
Log($"[REPACK] Starting texture repacking for {newSpriteFolders.Count} sprites");

// Use temp directory for Packager to avoid polluting data.win directory
string packDir = Path.Combine(Path.GetTempPath(), $"g3mtool_packager_{Guid.NewGuid():N}");
Log($"[REPACK] Temp directory: {packDir}");

try
{
    Log($"[REPACK] Creating temp directories...");
    Directory.CreateDirectory(packDir);

    // Copy only PNG files for NEW sprites to temp directory for repacking
    string newSpritesDir = Path.Combine(packDir, "new_sprites");
    Directory.CreateDirectory(newSpritesDir);
    Log($"[REPACK] Created: {newSpritesDir}");

    var newSpriteNames = new HashSet<string>(newSpriteFolders.Select(f => Path.GetFileName(f)));
    int copiedFiles = 0;
    int processedFolders = 0;

    Log($"[REPACK] Copying PNG files from {newSpriteFolders.Count} folders...");
    foreach (string folder in newSpriteFolders)
    {
        string spriteName = Path.GetFileName(folder);
        var pngFiles = Directory.GetFiles(folder, "*.png");
        foreach (string pngFile in pngFiles)
        {
            string destFile = Path.Combine(newSpritesDir, Path.GetFileName(pngFile));
            File.Copy(pngFile, destFile, true);
            copiedFiles++;
        }
        processedFolders++;
        if (processedFolders % 1000 == 0)
        {
            Log($"[REPACK] Copied {copiedFiles} files from {processedFolders}/{newSpriteFolders.Count} folders");
            Console.WriteLine($"[ImportSprites] Progress: {processedFolders}/{newSpriteFolders.Count} folders, {copiedFiles} files");
        }
    }

    Console.WriteLine($"[ImportSprites] Copied {copiedFiles} PNG files for {newSpriteNames.Count} new sprites");
    Log($"[REPACK] File copy complete: {copiedFiles} PNG files");

    string outName = Path.Combine(packDir, "atlas.txt");
    int textureSize = 2048;
    int PaddingValue = 2;
    bool debug = false;

    Log($"[REPACK] Initializing Packer (textureSize={textureSize}, padding={PaddingValue})");
    Console.WriteLine($"[ImportSprites] Starting texture packing...");
    Packer packer = new Packer();

    Log($"[REPACK] Processing {copiedFiles} PNG files...");
    packer.Process(newSpritesDir, "*.png", textureSize, PaddingValue, debug);
    Log($"[REPACK] Packer.Process complete");

    packer.SaveAtlasses(outName);
    Log($"[REPACK] Atlasses saved to {outName}");
    Log($"[REPACK] Generated {packer.Atlasses.Count} atlas(es)");
    Console.WriteLine($"[ImportSprites] Created {packer.Atlasses.Count} texture atlas(es)");

    int lastTextPage = Data.EmbeddedTextures.Count - 1;
    int lastTextPageItem = Data.TexturePageItems.Count - 1;
    Log($"[REPACK] Initial state: {lastTextPage + 1} texture pages, {lastTextPageItem + 1} texture page items");

    bool bboxMasks = Data.IsVersionAtLeast(2024, 6);
    Dictionary<UndertaleSprite, Node> maskNodes = new();

    string prefix = outName.Replace(Path.GetExtension(outName), "");
    int atlasCount = 0;
    Log($"[REPACK] Processing atlases...");
    foreach (Atlas atlas in packer.Atlasses)
    {
        Log($"[REPACK] Processing atlas {atlasCount}/{packer.Atlasses.Count} with {atlas.Nodes.Count} nodes");
        string atlasName = Path.Combine(packDir, $"{prefix}{atlasCount:000}.png");

        Log($"[REPACK] Reading atlas image: {atlasName}");
        using MagickImage atlasImage = TextureWorker.ReadBGRAImageFromFile(atlasName);
        IPixelCollection<byte> atlasPixels = atlasImage.GetPixels();
        Log($"[REPACK] Atlas image loaded: {atlasImage.Width}x{atlasImage.Height}");

        Log($"[REPACK] Creating EmbeddedTexture {lastTextPage + 1}");
        UndertaleEmbeddedTexture texture = new();
        texture.Name = new UndertaleString($"Texture {++lastTextPage}");
        texture.TextureData.Image = GMImage.FromMagickImage(atlasImage).ConvertToPng();
        Data.EmbeddedTextures.Add(texture);
        Log($"[REPACK] EmbeddedTexture added to Data");

        int nodeCount = 0;
        Log($"[REPACK] Processing {atlas.Nodes.Count} nodes in atlas {atlasCount}");
        foreach (Node n in atlas.Nodes)
        {
            nodeCount++;
            if (n.Texture != null)
            {
                string sourceFileName = Path.GetFileName(n.Texture.Source);
                string stripped = Path.GetFileNameWithoutExtension(sourceFileName);

                string spriteName;
                int frame = 0;
                try
                {
                    var spriteParts = sprFrameRegex.Match(stripped);
                    spriteName = spriteParts.Groups[1].Value;
                    Int32.TryParse(spriteParts.Groups[2].Value, out frame);
                }
                catch (Exception e)
                {
                    Log($"[REPACK] ERROR parsing sprite name: {stripped} - {e.Message}");
                    PrintLine($"[ImportSprites] Error: Image {stripped} has an invalid name. Skipping...");
                    continue;
                }

                if (nodeCount % 500 == 0)
                {
                    Log($"[REPACK] Processed {nodeCount}/{atlas.Nodes.Count} nodes in atlas {atlasCount}");
                }

                // Check if sprite exists - will update existing or create new
                UndertaleSprite existingSprite = Data.Sprites.ByName(spriteName);

                // Create TexturePageItem for this frame
                UndertaleTexturePageItem texturePageItem = new();
                texturePageItem.Name = new UndertaleString($"PageItem {++lastTextPageItem}");
                texturePageItem.SourceX = (ushort)n.Bounds.X;
                texturePageItem.SourceY = (ushort)n.Bounds.Y;
                texturePageItem.SourceWidth = (ushort)n.Bounds.Width;
                texturePageItem.SourceHeight = (ushort)n.Bounds.Height;
                texturePageItem.TargetX = (ushort)n.Texture.TargetX;
                texturePageItem.TargetY = (ushort)n.Texture.TargetY;
                texturePageItem.TargetWidth = (ushort)n.Bounds.Width;
                texturePageItem.TargetHeight = (ushort)n.Bounds.Height;
                texturePageItem.BoundingWidth = (ushort)n.Texture.BoundingWidth;
                texturePageItem.BoundingHeight = (ushort)n.Texture.BoundingHeight;
                texturePageItem.TexturePage = texture;

                Data.TexturePageItems.Add(texturePageItem);

                UndertaleSprite.TextureEntry texentry = new();
                texentry.Texture = texturePageItem;

                var frameData = TryGetTextureFrameData(spritesPath, spriteName);
                if (frameData != null)
                    ApplyTextureFrameProperties(texturePageItem, spriteName, frame, frameData);

                UndertaleSprite sprite;

                if (existingSprite == null)
                {
                    // Create new sprite
                    PrintLine($"[ImportSprites] Creating NEW sprite: {spriteName}");
                    sprite = new UndertaleSprite();
                    sprite.Name = Data.Strings.MakeString(spriteName);
                    sprite.Width = (uint)n.Texture.BoundingWidth;
                    sprite.Height = (uint)n.Texture.BoundingHeight;
                    sprite.MarginLeft = n.Texture.TargetX;
                    sprite.MarginRight = n.Texture.TargetX + n.Bounds.Width - 1;
                    sprite.MarginTop = n.Texture.TargetY;
                    sprite.MarginBottom = n.Texture.TargetY + n.Bounds.Height - 1;
                    sprite.OriginX = 0;
                    sprite.OriginY = 0;

                    JsonElement? spriteMeta = TryLoadSpriteMetadata(spritesPath, spriteName);
                    if (spriteMeta.HasValue)
                        ApplySpriteMetadata(sprite, spriteMeta.Value);

                    // Fill preceding frames with valid empty TextureEntry objects
                    // CRITICAL: Never use null - UndertaleSimpleList.Serialize skips null entries
                    // but still writes the count, causing read/write mismatch and data corruption
                    if (frame > 0)
                    {
                        Log($"  INFO: Sprite '{spriteName}' starts at frame {frame}, filling {frame} empty frames");
                        for (int i = 0; i < frame; i++)
                        {
                            var emptyEntry = new UndertaleSprite.TextureEntry();
                            emptyEntry.Texture = null;
                            sprite.Textures.Add(emptyEntry);
                        }
                    }

                    // Generate mask for new sprites if needed
                    if (!noMasksForBasicRectangles ||
                        sprite.SepMasks is not (UndertaleSprite.SepMaskType.AxisAlignedRect or UndertaleSprite.SepMaskType.RotatedRect))
                    {
                        if (sprite.CollisionMasks.Count == 0)
                            maskNodes[sprite] = n;
                    }

                    sprite.Textures.Add(texentry);
                    Data.Sprites.Add(sprite);
                    newSpritesCreated++;
                }
                else
                {
                    // Update existing sprite
                    sprite = existingSprite;

                    // Extend Textures list with valid empty TextureEntry objects if needed
                    // CRITICAL: Never use null - causes serialization corruption
                    if (frame >= sprite.Textures.Count)
                    {
                        Log($"  INFO: Extending '{spriteName}' Textures from {sprite.Textures.Count} to {frame + 1}");
                        while (frame >= sprite.Textures.Count)
                        {
                            var emptyEntry = new UndertaleSprite.TextureEntry();
                            emptyEntry.Texture = null;
                            sprite.Textures.Add(emptyEntry);
                        }
                    }

                    // Preserve texture properties from existing texture
                    UndertaleTexturePageItem oldTex =
                        (sprite.Textures[frame]?.Texture)
                        ?? sprite.Textures.FirstOrDefault(te => te != null)?.Texture;

                    if (oldTex != null)
                    {
                        texturePageItem.TargetX = oldTex.TargetX;
                        texturePageItem.TargetY = oldTex.TargetY;
                        texturePageItem.TargetWidth = oldTex.TargetWidth;
                        texturePageItem.TargetHeight = oldTex.TargetHeight;
                        texturePageItem.BoundingWidth = oldTex.BoundingWidth;
                        texturePageItem.BoundingHeight = oldTex.BoundingHeight;
                    }

                    if (frameData != null)
                        ApplyTextureFrameProperties(texturePageItem, spriteName, frame, frameData);

                    // Apply metadata including masks from patch
                    JsonElement? existingSpriteMeta = TryLoadSpriteMetadata(spritesPath, spriteName);
                    if (existingSpriteMeta.HasValue)
                        ApplySpriteMetadata(sprite, existingSpriteMeta.Value);

                    sprite.Textures[frame] = texentry;
                    existingSpritesUpdated++;
                }

                // Generate mask only if sprite needs precise collision and has no masks
                bool needMaskInit =
                    sprite.SepMasks is UndertaleSprite.SepMaskType.Precise &&
                    sprite.CollisionMasks.Count == 0;

                if (needMaskInit)
                    maskNodes[sprite] = n;
            }
        }


        // Match UTMT mask generation logic - wrap in try-catch to handle edge cases
        foreach ((UndertaleSprite maskSpr, Node maskNode) in maskNodes)
        {
            try
            {
                maskSpr.CollisionMasks.Clear();
                maskSpr.CollisionMasks.Add(maskSpr.NewMaskEntry(Data));
                (int maskWidth, int maskHeight) = maskSpr.CalculateMaskDimensions(Data);

                if (maskWidth <= 0 || maskHeight <= 0)
                {
                    // Invalid dimensions - clear masks to avoid issues
                    maskSpr.CollisionMasks.Clear();
                    continue;
                }

                int maskStride = ((maskWidth + 7) / 8) * 8;

                BitArray maskingBitArray = new BitArray(maskStride * maskHeight);
                for (int y = 0; y < maskHeight && y < maskNode.Bounds.Height; y++)
                {
                    for (int x = 0; x < maskWidth && x < maskNode.Bounds.Width; x++)
                    {
                        IMagickColor<byte> pixelColor = atlasPixels.GetPixel(x + maskNode.Bounds.X, y + maskNode.Bounds.Y).ToColor();
                        if (bboxMasks)
                        {
                            maskingBitArray[(y * maskStride) + x] = (pixelColor.A > 0);
                        }
                        else
                        {
                            int idx = ((y + maskNode.Texture.TargetY) * maskStride) + x + maskNode.Texture.TargetX;
                            if (idx >= 0 && idx < maskingBitArray.Length)
                                maskingBitArray[idx] = (pixelColor.A > 0);
                        }
                    }
                }
                BitArray tempBitArray = new BitArray(maskingBitArray.Length);
                for (int i = 0; i < maskingBitArray.Length; i += 8)
                {
                    for (int j = 0; j < 8; j++)
                    {
                        tempBitArray[j + i] = maskingBitArray[-(j - 7) + i];
                    }
                }

                int numBytes = maskingBitArray.Length / 8;
                byte[] bytes = new byte[numBytes];
                tempBitArray.CopyTo(bytes, 0);

                // Verify mask data size matches expected
                if (maskSpr.CollisionMasks[0].Data.Length == bytes.Length)
                {
                    for (int i = 0; i < bytes.Length; i++)
                        maskSpr.CollisionMasks[0].Data[i] = bytes[i];
                }
                else
                {
                    // Size mismatch - clear masks to avoid assertion failure
                    maskSpr.CollisionMasks.Clear();
                }
            }
            catch
            {
                // On any error, clear masks for this sprite to allow saving
                maskSpr.CollisionMasks.Clear();
            }
        }
        maskNodes.Clear();
        Log($"[REPACK] Atlas {atlasCount} complete");

        atlasCount++;
    }

    Log($"[REPACK] All {atlasCount} atlases processed successfully");
    Console.WriteLine($"[ImportSprites] Texture repacking complete - all atlases processed");

    PrintLine($"[ImportSprites] Import complete! Processed {pngFiles.Length} PNG files. New: {newSpritesCreated}, Updated: {existingSpritesUpdated}");
    Log($"[ImportSprites] Repacking finished - New: {newSpritesCreated}, Updated: {existingSpritesUpdated}");

    // =========================================================================
    // VALIDATION: Ensure ALL sprites are safe to serialize
    // =========================================================================
    Console.WriteLine("[ImportSprites] Validating all sprites before save...");
    int validationFixed = 0;
    int validationNullTexEntries = 0;
    int validationNullSprites = 0;
    bool isGMS2 = Data.IsGameMaker2();

    for (int si = 0; si < Data.Sprites.Count; si++)
    {
        var spr = Data.Sprites[si];
        if (spr == null)
        {
            validationNullSprites++;
            Log($"  VALIDATION ERROR: Sprite at index {si} is NULL!");
            continue;
        }

        string sprName = spr.Name?.Content ?? $"(unnamed_{si})";

        // Fix 1: Ensure IsSpecialType is set for GMS2 games
        if (isGMS2 && !spr.IsSpecialType)
        {
            Log($"  VALIDATION FIX: {sprName} - setting IsSpecialType=true (was false in GMS2 game)");
            spr.IsSpecialType = true;
            if (spr.SVersion == 0) spr.SVersion = 3;
            spr.SSpriteType = UndertaleSprite.SpriteType.Normal;
            if (spr.GMS2PlaybackSpeed == 0) spr.GMS2PlaybackSpeed = 15.0f;
            validationFixed++;
        }

        // Fix 2: Replace any null entries in Textures list with valid TextureEntry objects
        if (spr.Textures != null)
        {
            for (int ti = 0; ti < spr.Textures.Count; ti++)
            {
                if (spr.Textures[ti] == null)
                {
                    Log($"  VALIDATION FIX: {sprName} - replacing NULL Textures[{ti}] with empty TextureEntry");
                    spr.Textures[ti] = new UndertaleSprite.TextureEntry() { Texture = null };
                    validationNullTexEntries++;
                    validationFixed++;
                }
            }
        }
    }

    if (validationFixed > 0 || validationNullSprites > 0)
    {
        Console.WriteLine($"[ImportSprites] Validation: fixed {validationFixed} issues ({validationNullTexEntries} null TextureEntries, {validationNullSprites} null sprites)");
    }
    else
    {
        Console.WriteLine("[ImportSprites] Validation: all sprites OK");
    }
    Log($"VALIDATION COMPLETE: fixed={validationFixed}, nullTexEntries={validationNullTexEntries}, nullSprites={validationNullSprites}");

    // Log final state
    Log($"FINAL STATE: Data.Sprites.Count = {Data.Sprites.Count}");
    Log($"FINAL STATE: Data.EmbeddedTextures.Count = {Data.EmbeddedTextures.Count}");
    Log($"FINAL STATE: Data.TexturePageItems.Count = {Data.TexturePageItems.Count}");

    // Log first 20 sprites after import
    Log("FINAL SPRITES (first 20):");
    for (int i = 0; i < Math.Min(20, Data.Sprites.Count); i++)
    {
        var spr = Data.Sprites[i];
        Log($"  [{i}] {spr?.Name?.Content ?? "(null)"} - Textures: {spr?.Textures?.Count ?? 0}");
    }

    // Log spr_heart specifically
    var sprHeart = Data.Sprites.ByName("spr_heart");
    if (sprHeart != null)
    {
        int heartIdx = Data.Sprites.IndexOf(sprHeart);
        Log($"SPECIFIC CHECK: spr_heart at index {heartIdx}, Textures: {sprHeart.Textures?.Count ?? 0}");
        if (sprHeart.Textures != null && sprHeart.Textures.Count > 0 && sprHeart.Textures[0]?.Texture != null)
        {
            var tex = sprHeart.Textures[0].Texture;
            int tpIdx = Data.EmbeddedTextures.IndexOf(tex.TexturePage);
            Log($"  -> TexturePage index: {tpIdx}, SourceX: {tex.SourceX}, SourceY: {tex.SourceY}, SourceW: {tex.SourceWidth}, SourceH: {tex.SourceHeight}");
        }
    }
    else
    {
        Log("SPECIFIC CHECK: spr_heart NOT FOUND!");
    }
}
catch (Exception ex)
{
    Log($"[REPACK] EXCEPTION during texture repacking: {ex.GetType().Name}: {ex.Message}");
    Log($"[REPACK] Stack trace: {ex.StackTrace}");
    Console.WriteLine($"[ImportSprites] ERROR: {ex.Message}");
    throw;
}
finally
{
    Log($"[REPACK] Entering finally block - cleaning up resources");
    foreach (MagickImage img in imagesToCleanup)
        img.Dispose();

    // Cleanup temp packager directory
    try
    {
        Log($"[REPACK] Deleting temp directory: {packDir}");
        Directory.Delete(packDir, true);
    }
    catch (Exception ex)
    {
        Log($"[REPACK] Failed to delete temp dir: {ex.Message}");
    }

    // Close log file
    Log($"[REPACK] Cleanup complete, closing log");
    CloseLog();
}




public class TextureInfo
{
    public string Source;
    public int Width;
    public int Height;
    public int TargetX;
    public int TargetY;
    public int BoundingWidth;
    public int BoundingHeight;
    public MagickImage Image;
}

public enum SplitType { Horizontal, Vertical }
public enum BestFitHeuristic { Area, MaxOneAxis }

public struct Rect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public class Node
{
    public Rect Bounds;
    public TextureInfo Texture;
    public SplitType SplitType;
}

public class Atlas
{
    public int Width;
    public int Height;
    public List<Node> Nodes;
}

public class Packer
{
    public List<TextureInfo> SourceTextures;
    public StringWriter Log;
    public StringWriter Error;
    public int Padding;
    public int AtlasSize;
    public bool DebugMode;
    public BestFitHeuristic FitHeuristic;
    public List<Atlas> Atlasses;

    public Packer()
    {
        SourceTextures = new List<TextureInfo>();
        Log = new StringWriter();
        Error = new StringWriter();
    }

    public void Process(string _SourceDir, string _Pattern, int _AtlasSize, int _Padding, bool _DebugMode)
    {
        Padding = _Padding;
        AtlasSize = _AtlasSize;
        DebugMode = _DebugMode;

        ScanForTextures(_SourceDir, _Pattern);
        List<TextureInfo> textures = SourceTextures.ToList();

        Atlasses = new List<Atlas>();
        while (textures.Count > 0)
        {
            Atlas atlas = new Atlas();
            atlas.Width = _AtlasSize;
            atlas.Height = _AtlasSize;
            List<TextureInfo> leftovers = LayoutAtlas(textures, atlas);
            if (leftovers.Count == 0)
            {
                while (leftovers.Count == 0)
                {
                    atlas.Width /= 2;
                    atlas.Height /= 2;
                    leftovers = LayoutAtlas(textures, atlas);
                }

                atlas.Width = atlas.Width == 0 ? 1 : atlas.Width * 2;
                atlas.Height = atlas.Height == 0 ? 1 : atlas.Height * 2;
                leftovers = LayoutAtlas(textures, atlas);
            }
            Atlasses.Add(atlas);
            textures = leftovers;
        }
    }

    public void SaveAtlasses(string _Destination)
    {
        int atlasCount = 0;
        string prefix = _Destination.Replace(Path.GetExtension(_Destination), "");

        StreamWriter tw = new StreamWriter(_Destination);
        tw.WriteLine("source_tex, atlas_tex, x, y, width, height");
        foreach (Atlas atlas in Atlasses)
        {
            string atlasName = $"{prefix}{atlasCount:000}.png";

            using (MagickImage img = CreateAtlasImage(atlas))
                TextureWorker.SaveImageToFile(img, atlasName);

            foreach (Node n in atlas.Nodes)
            {
                if (n.Texture != null)
                {
                    tw.Write(n.Texture.Source + ", ");
                    tw.Write(atlasName + ", ");
                    tw.Write((n.Bounds.X).ToString() + ", ");
                    tw.Write((n.Bounds.Y).ToString() + ", ");
                    tw.Write((n.Bounds.Width).ToString() + ", ");
                    tw.WriteLine((n.Bounds.Height).ToString());
                }
            }
            ++atlasCount;
        }
        tw.Close();
        tw = new StreamWriter(prefix + ".log");
        tw.WriteLine("--- LOG -------------------------------------------");
        tw.WriteLine(Log.ToString());
        tw.WriteLine("--- ERROR -----------------------------------------");
        tw.WriteLine(Error.ToString());
        tw.Close();
    }

    private void ScanForTextures(string _Path, string _Wildcard)
    {
        DirectoryInfo di = new(_Path);
        FileInfo[] files = di.GetFiles(_Wildcard, SearchOption.AllDirectories);
        foreach (FileInfo fi in files)
        {
            (int width, int height) = TextureWorker.GetImageSizeFromFile(fi.FullName);
            if (width == -1 || height == -1)
                continue;

            if (width <= AtlasSize && height <= AtlasSize)
            {
                TextureInfo ti = new();
                MagickImage img = new(fi.FullName);
                imagesToCleanup.Add(img);

                ti.Source = fi.FullName;
                ti.BoundingWidth = (int)img.Width;
                ti.BoundingHeight = (int)img.Height;

                ti.TargetX = 0;
                ti.TargetY = 0;

                img.BorderColor = MagickColors.Transparent;
                img.BackgroundColor = MagickColors.Transparent;
                img.Border(1);
                IMagickGeometry? bbox = img.BoundingBox;
                if (bbox is not null)
                {
                    ti.TargetX = bbox.X - 1;
                    ti.TargetY = bbox.Y - 1;
                    img.Trim();
                }
                else
                {
                    ti.TargetX = 0;
                    ti.TargetY = 0;
                    img.Crop(1, 1);
                }
                img.ResetPage();

                ti.Width = (int)img.Width;
                ti.Height = (int)img.Height;
                ti.Image = img;

                SourceTextures.Add(ti);
                Log.WriteLine($"Added {fi.FullName}");
            }
            else
            {
                Error.WriteLine($"{fi.FullName} is too large to fit in the atlas. Skipping!");
            }
        }
    }

    private void HorizontalSplit(Node _ToSplit, int _Width, int _Height, List<Node> _List)
    {
        Node n1 = new Node();
        n1.Bounds.X = _ToSplit.Bounds.X + _Width + Padding;
        n1.Bounds.Y = _ToSplit.Bounds.Y;
        n1.Bounds.Width = _ToSplit.Bounds.Width - _Width - Padding;
        n1.Bounds.Height = _Height;
        n1.SplitType = SplitType.Vertical;
        Node n2 = new Node();
        n2.Bounds.X = _ToSplit.Bounds.X;
        n2.Bounds.Y = _ToSplit.Bounds.Y + _Height + Padding;
        n2.Bounds.Width = _ToSplit.Bounds.Width;
        n2.Bounds.Height = _ToSplit.Bounds.Height - _Height - Padding;
        n2.SplitType = SplitType.Horizontal;
        if (n1.Bounds.Width > 0 && n1.Bounds.Height > 0)
            _List.Add(n1);
        if (n2.Bounds.Width > 0 && n2.Bounds.Height > 0)
            _List.Add(n2);
    }

    private void VerticalSplit(Node _ToSplit, int _Width, int _Height, List<Node> _List)
    {
        Node n1 = new Node();
        n1.Bounds.X = _ToSplit.Bounds.X + _Width + Padding;
        n1.Bounds.Y = _ToSplit.Bounds.Y;
        n1.Bounds.Width = _ToSplit.Bounds.Width - _Width - Padding;
        n1.Bounds.Height = _ToSplit.Bounds.Height;
        n1.SplitType = SplitType.Vertical;
        Node n2 = new Node();
        n2.Bounds.X = _ToSplit.Bounds.X;
        n2.Bounds.Y = _ToSplit.Bounds.Y + _Height + Padding;
        n2.Bounds.Width = _Width;
        n2.Bounds.Height = _ToSplit.Bounds.Height - _Height - Padding;
        n2.SplitType = SplitType.Horizontal;
        if (n1.Bounds.Width > 0 && n1.Bounds.Height > 0)
            _List.Add(n1);
        if (n2.Bounds.Width > 0 && n2.Bounds.Height > 0)
            _List.Add(n2);
    }

    private TextureInfo FindBestFitForNode(Node _Node, List<TextureInfo> _Textures)
    {
        TextureInfo bestFit = null;
        float nodeArea = _Node.Bounds.Width * _Node.Bounds.Height;
        float maxCriteria = 0.0f;
        foreach (TextureInfo ti in _Textures)
        {
            switch (FitHeuristic)
            {
                case BestFitHeuristic.MaxOneAxis:
                    if (ti.Width <= _Node.Bounds.Width && ti.Height <= _Node.Bounds.Height)
                    {
                        float wRatio = (float)ti.Width / (float)_Node.Bounds.Width;
                        float hRatio = (float)ti.Height / (float)_Node.Bounds.Height;
                        float ratio = wRatio > hRatio ? wRatio : hRatio;
                        if (ratio > maxCriteria)
                        {
                            maxCriteria = ratio;
                            bestFit = ti;
                        }
                    }
                    break;

                case BestFitHeuristic.Area:
                    if (ti.Width <= _Node.Bounds.Width && ti.Height <= _Node.Bounds.Height)
                    {
                        float textureArea = ti.Width * ti.Height;
                        float coverage = textureArea / nodeArea;
                        if (coverage > maxCriteria)
                        {
                            maxCriteria = coverage;
                            bestFit = ti;
                        }
                    }
                    break;
            }
        }
        return bestFit;
    }

    private List<TextureInfo> LayoutAtlas(List<TextureInfo> _Textures, Atlas _Atlas)
    {
        List<Node> freeList = new List<Node>();
        List<TextureInfo> textures = _Textures.ToList();
        _Atlas.Nodes = new List<Node>();
        Node root = new Node();
        root.Bounds.Width = _Atlas.Width;
        root.Bounds.Height = _Atlas.Height;
        root.SplitType = SplitType.Horizontal;
        freeList.Add(root);
        while (freeList.Count > 0 && textures.Count > 0)
        {
            Node node = freeList[0];
            freeList.RemoveAt(0);
            TextureInfo bestFit = FindBestFitForNode(node, textures);
            if (bestFit != null)
            {
                if (node.SplitType == SplitType.Horizontal)
                    HorizontalSplit(node, bestFit.Width, bestFit.Height, freeList);
                else
                    VerticalSplit(node, bestFit.Width, bestFit.Height, freeList);
                node.Texture = bestFit;
                node.Bounds.Width = bestFit.Width;
                node.Bounds.Height = bestFit.Height;
                textures.Remove(bestFit);
            }
            _Atlas.Nodes.Add(node);
        }
        return textures;
    }

    private MagickImage CreateAtlasImage(Atlas _Atlas)
    {
        MagickImage img = new(MagickColors.Transparent, (uint)_Atlas.Width, (uint)_Atlas.Height);

        foreach (Node n in _Atlas.Nodes)
        {
            if (n.Texture is not null)
            {
                MagickImage sourceImg = n.Texture.Image;
                using IMagickImage<byte> resizedSourceImg = TextureWorker.ResizeImage(sourceImg, n.Bounds.Width, n.Bounds.Height);
                img.Composite(resizedSourceImg, n.Bounds.X, n.Bounds.Y, CompositeOperator.Copy);
            }
        }

        return img;
    }
}





