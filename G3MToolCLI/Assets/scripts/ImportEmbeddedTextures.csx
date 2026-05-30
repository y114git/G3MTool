
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
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
        _logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
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

T GetJsonValue<T>(JsonElement root, string propertyName, T defaultValue)
{
    if (root.TryGetProperty(propertyName, out JsonElement elm))
    {
        try
        {
            if (typeof(T) == typeof(uint))
                return (T)(object)(uint)elm.GetInt64();
            if (typeof(T) == typeof(int))
                return (T)(object)elm.GetInt32();
            if (typeof(T) == typeof(bool))
            {
                // Handle both boolean and number values
                if (elm.ValueKind == JsonValueKind.True) return (T)(object)true;
                if (elm.ValueKind == JsonValueKind.False) return (T)(object)false;
                if (elm.ValueKind == JsonValueKind.Number) return (T)(object)(elm.GetInt32() != 0);
                return (T)(object)elm.GetBoolean();
            }
            if (typeof(T) == typeof(string))
                return (T)(object)(elm.GetString() ?? "");
        }
        catch { }
    }
    return defaultValue;
}

EnsureDataLoaded();

// Initialize detailed logging
InitLog("ImportEmbeddedTextures");

string texturesDir = GetInputDirectory();
Console.WriteLine($"[ImportEmbeddedTextures] Importing from: {texturesDir}");

// Log initial state
Log($"INITIAL STATE: Data.EmbeddedTextures.Count = {Data.EmbeddedTextures.Count}");
Log("INITIAL TEXTURES (first 10):");
for (int i = 0; i < Math.Min(10, Data.EmbeddedTextures.Count); i++)
{
    var tex = Data.EmbeddedTextures[i];
    Log($"  [{i}] {tex?.Name?.Content ?? "(null)"}");
}

var textureDirs = Directory.GetDirectories(texturesDir)
    .OrderBy(d => Path.GetFileName(d))
    .ToArray();

if (textureDirs.Length == 0)
{
    Console.WriteLine("[ImportEmbeddedTextures] No texture directories found - nothing to import.");
    return;
}

Console.WriteLine($"[ImportEmbeddedTextures] Found {textureDirs.Length} texture(s) to process.");

int imported = 0;
int created = 0;
int updated = 0;

foreach (string textureSubDir in textureDirs)
{
    string textureName = Path.GetFileName(textureSubDir);
    string pngPath = Path.Combine(textureSubDir, $"{textureName}.png");
    string jsonPath = Path.Combine(textureSubDir, $"{textureName}.json");

    if (!File.Exists(pngPath))
    {
        PrintLine($"[ImportEmbeddedTextures] Skipping {textureName}: PNG not found");
        continue;
    }

    try
    {
        int textureIndex = -1;
        uint scaled = 0;
        uint generatedMips = 0;
        string originalName = "";
        string originalFormat = "Png";

        // Load metadata if exists
        if (File.Exists(jsonPath))
        {
            string jsonContent = File.ReadAllText(jsonPath, Encoding.UTF8);
            using JsonDocument jsonDoc = JsonDocument.Parse(jsonContent);
            JsonElement root = jsonDoc.RootElement;

            textureIndex = GetJsonValue<int>(root, "index", -1);
            originalName = GetJsonValue<string>(root, "name", "");
            scaled = GetJsonValue<uint>(root, "scaled", 0);
            generatedMips = GetJsonValue<uint>(root, "generatedMips", 0);
            originalFormat = GetJsonValue<string>(root, "format", "Png");
        }
        else
        {
            // No JSON - try to extract index from folder name (texture_XXXX format)
            if (textureName.StartsWith("texture_") && textureName.Length >= 12)
            {
                if (int.TryParse(textureName.Substring(8, 4), out int parsedIndex))
                {
                    textureIndex = parsedIndex;
                    originalName = $"Texture {parsedIndex}";
                    Log($"EXTRACTED INDEX from folder name: {textureName} -> index={textureIndex}");
                }
            }
        }

        // Load image from PNG, then convert to original format to preserve encoding
        byte[] pngBytes = File.ReadAllBytes(pngPath);
        var gmImage = GMImage.FromPng(pngBytes);
        Log($"LOADED IMAGE: {pngPath} - Size: {gmImage.Width}x{gmImage.Height}, converting to {originalFormat}");
        
        // Convert to original format (preserves BZ2QOI/QOI encoding instead of storing as larger PNG)
        if (Enum.TryParse<GMImage.ImageFormat>(originalFormat, out var targetFormat) && targetFormat != GMImage.ImageFormat.Png)
        {
            gmImage = gmImage.ConvertToFormat(targetFormat);
            Log($"CONVERTED to {targetFormat} format");
        }

        if (textureIndex >= 0)
        {
            // Expand EmbeddedTextures array if needed to accommodate this index
            while (Data.EmbeddedTextures.Count <= textureIndex)
            {
                var placeholder = new UndertaleEmbeddedTexture();
                placeholder.Name = new UndertaleString($"Texture {Data.EmbeddedTextures.Count}");
                Data.EmbeddedTextures.Add(placeholder);
                created++;
            }
            
            // Update texture at the specified index
            var texture = Data.EmbeddedTextures[textureIndex];
            texture.Name = new UndertaleString(originalName.Length > 0 ? originalName : $"Texture {textureIndex}");
            texture.TextureData.Image = gmImage;
            texture.Scaled = scaled;
            texture.GeneratedMips = generatedMips;
            updated++;
            Log($"UPDATED TEXTURE: index={textureIndex}, name='{textureName}', originalName='{originalName}'");
            PrintLine($"[ImportEmbeddedTextures] Updated texture at index {textureIndex}");
        }
        else
        {
            // No index specified - skip this texture (it's a vanilla texture that doesn't need updating)
            // Vanilla textures without JSON metadata should be left as-is in the original file
            PrintLine($"[ImportEmbeddedTextures] Skipping {textureName}: no index in metadata (vanilla texture)");
            continue;
        }

        imported++;
    }
    catch (Exception ex)
    {
        PrintLine($"[ImportEmbeddedTextures] Failed to import {textureName}: {ex.Message}");
    }
}

PrintLine($"[ImportEmbeddedTextures] Import complete. {imported} textures processed ({updated} updated, {created} new).");

// Log final state
Log($"FINAL STATE: Data.EmbeddedTextures.Count = {Data.EmbeddedTextures.Count}");
Log("FINAL TEXTURES (first 10):");
for (int i = 0; i < Math.Min(10, Data.EmbeddedTextures.Count); i++)
{
    var tex = Data.EmbeddedTextures[i];
    Log($"  [{i}] {tex?.Name?.Content ?? "(null)"}");
}

CloseLog();
