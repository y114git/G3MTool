
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using UndertaleModLib;
using UndertaleModLib.Models;

void PrintLine(string s) { if (Verbose) Console.WriteLine(s); }

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

string itemsDir = GetInputDirectory();
PrintLine($"[ImportTexturePageItems] Importing from: {itemsDir}");

string jsonPath = Path.Combine(itemsDir, "texture_page_items.json");
if (!File.Exists(jsonPath))
{
    PrintLine("[ImportTexturePageItems] texture_page_items.json not found - nothing to import.");
    return;
}

string jsonContent = File.ReadAllText(jsonPath, Encoding.UTF8);
using JsonDocument jsonDoc = JsonDocument.Parse(jsonContent);

if (jsonDoc.RootElement.ValueKind != JsonValueKind.Array)
{
    PrintLine("[ImportTexturePageItems] Invalid JSON format - expected array.");
    return;
}

var items = jsonDoc.RootElement.EnumerateArray().ToList();
PrintLine($"[ImportTexturePageItems] Found {items.Count} items to process.");

int updated = 0;
int created = 0;

foreach (var itemElm in items)
{
    try
    {
        int index = itemElm.TryGetProperty("index", out var idxElm) ? idxElm.GetInt32() : -1;
        string name = itemElm.TryGetProperty("name", out var nameElm) ? nameElm.GetString() ?? "" : "";
        ushort sourceX = itemElm.TryGetProperty("sourceX", out var sxElm) ? (ushort)sxElm.GetInt32() : (ushort)0;
        ushort sourceY = itemElm.TryGetProperty("sourceY", out var syElm) ? (ushort)syElm.GetInt32() : (ushort)0;
        ushort sourceWidth = itemElm.TryGetProperty("sourceWidth", out var swElm) ? (ushort)swElm.GetInt32() : (ushort)0;
        ushort sourceHeight = itemElm.TryGetProperty("sourceHeight", out var shElm) ? (ushort)shElm.GetInt32() : (ushort)0;
        ushort targetX = itemElm.TryGetProperty("targetX", out var txElm) ? (ushort)txElm.GetInt32() : (ushort)0;
        ushort targetY = itemElm.TryGetProperty("targetY", out var tyElm) ? (ushort)tyElm.GetInt32() : (ushort)0;
        ushort targetWidth = itemElm.TryGetProperty("targetWidth", out var twElm) ? (ushort)twElm.GetInt32() : (ushort)0;
        ushort targetHeight = itemElm.TryGetProperty("targetHeight", out var thElm) ? (ushort)thElm.GetInt32() : (ushort)0;
        ushort boundingWidth = itemElm.TryGetProperty("boundingWidth", out var bwElm) ? (ushort)bwElm.GetInt32() : (ushort)0;
        ushort boundingHeight = itemElm.TryGetProperty("boundingHeight", out var bhElm) ? (ushort)bhElm.GetInt32() : (ushort)0;
        int texturePageIndex = itemElm.TryGetProperty("texturePageIndex", out var tpElm) ? tpElm.GetInt32() : -1;

        UndertaleTexturePageItem item;
        
        if (index >= 0 && index < Data.TexturePageItems.Count)
        {
            // Update existing item
            item = Data.TexturePageItems[index];
            updated++;
        }
        else
        {
            // Create new item
            item = new UndertaleTexturePageItem();
            item.Name = new UndertaleString(name.Length > 0 ? name : $"PageItem {Data.TexturePageItems.Count}");
            Data.TexturePageItems.Add(item);
            created++;
        }

        item.SourceX = sourceX;
        item.SourceY = sourceY;
        item.SourceWidth = sourceWidth;
        item.SourceHeight = sourceHeight;
        item.TargetX = targetX;
        item.TargetY = targetY;
        item.TargetWidth = targetWidth;
        item.TargetHeight = targetHeight;
        item.BoundingWidth = boundingWidth;
        item.BoundingHeight = boundingHeight;

        if (texturePageIndex >= 0 && texturePageIndex < Data.EmbeddedTextures.Count)
        {
            item.TexturePage = Data.EmbeddedTextures[texturePageIndex];
        }
    }
    catch (Exception ex)
    {
        PrintLine($"[ImportTexturePageItems] Error processing item: {ex.Message}");
    }
}

PrintLine($"[ImportTexturePageItems] Import complete. {updated} updated, {created} created.");
