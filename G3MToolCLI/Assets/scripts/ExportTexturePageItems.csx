
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using UndertaleModLib;
using UndertaleModLib.Models;

void PrintLine(string s) { if (Verbose) Console.WriteLine(s); }

string GetOutputDirectory()
{
    string outputDir = OutputDir;
    if (string.IsNullOrEmpty(outputDir))
        throw new Exception("OutputDir is not set.");
    string typeDir = Path.Combine(outputDir, "TexturePageItems");
    if (!Directory.Exists(typeDir))
        Directory.CreateDirectory(typeDir);
    return typeDir;
}

EnsureDataLoaded();

string itemsOut = GetOutputDirectory();
PrintLine($"[ExportTexturePageItems] Exporting to: {itemsOut}");

var allItems = Data.TexturePageItems.ToList();
PrintLine($"[ExportTexturePageItems] Found {allItems.Count} texture page items to export.");

// Build texture page index lookup
var texturePageIndexMap = new Dictionary<UndertaleEmbeddedTexture, int>();
for (int i = 0; i < Data.EmbeddedTextures.Count; i++)
{
    texturePageIndexMap[Data.EmbeddedTextures[i]] = i;
}

// Export all items in a single JSON file for efficiency
var itemsData = new List<Dictionary<string, object>>();

for (int i = 0; i < allItems.Count; i++)
{
    var item = allItems[i];
    var itemData = new Dictionary<string, object>
    {
        ["index"] = i,
        ["name"] = item.Name?.Content ?? "",
        ["sourceX"] = item.SourceX,
        ["sourceY"] = item.SourceY,
        ["sourceWidth"] = item.SourceWidth,
        ["sourceHeight"] = item.SourceHeight,
        ["targetX"] = item.TargetX,
        ["targetY"] = item.TargetY,
        ["targetWidth"] = item.TargetWidth,
        ["targetHeight"] = item.TargetHeight,
        ["boundingWidth"] = item.BoundingWidth,
        ["boundingHeight"] = item.BoundingHeight,
        ["texturePageIndex"] = item.TexturePage != null && texturePageIndexMap.TryGetValue(item.TexturePage, out int idx) ? idx : -1
    };
    itemsData.Add(itemData);
}

var jsonOptions = new JsonSerializerOptions 
{ 
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

string jsonPath = Path.Combine(itemsOut, "texture_page_items.json");
string json = JsonSerializer.Serialize(itemsData, jsonOptions);
File.WriteAllText(jsonPath, json, Encoding.UTF8);

PrintLine($"[ExportTexturePageItems] Export complete. {allItems.Count} items exported to {jsonPath}");
