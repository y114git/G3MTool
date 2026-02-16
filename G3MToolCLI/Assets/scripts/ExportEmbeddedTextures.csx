
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using ImageMagick;

void PrintLine(string s) { if (Verbose) Console.WriteLine(s); }

string GetOutputDirectory()
{
    string outputDir = OutputDir;
    if (string.IsNullOrEmpty(outputDir))
        throw new Exception("OutputDir is not set.");
    string typeDir = Path.Combine(outputDir, "EmbeddedTextures");
    if (!Directory.Exists(typeDir))
        Directory.CreateDirectory(typeDir);
    return typeDir;
}

EnsureDataLoaded();

string texturesOut = GetOutputDirectory();
PrintLine($"[ExportEmbeddedTextures] Exporting to: {texturesOut}");

var allTextures = Data.EmbeddedTextures.ToList();
PrintLine($"[ExportEmbeddedTextures] Found {allTextures.Count} embedded textures to export.");

JsonSerializerOptions jsonWriteOptions = new JsonSerializerOptions 
{ 
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

SetProgressBar(null, "Exporting Embedded Textures", 0, allTextures.Count);
StartProgressBarUpdater();

for (int i = 0; i < allTextures.Count; i++)
{
    var texture = allTextures[i];
    try
    {
        string textureName = $"texture_{i:D4}";
        string textureDir = Path.Combine(texturesOut, textureName);
        Directory.CreateDirectory(textureDir);

        // Export texture as PNG
        if (texture.TextureData?.Image != null)
        {
            string pngPath = Path.Combine(textureDir, $"{textureName}.png");
            using var img = texture.TextureData.Image.GetMagickImage();
            img.Write(pngPath, MagickFormat.Png32);
        }

        // Export metadata (including internal format for byte-perfect restoration)
        var textureMeta = new Dictionary<string, object>
        {
            ["index"] = i,
            ["name"] = texture.Name?.Content ?? "",
            ["scaled"] = texture.Scaled,
            ["generatedMips"] = texture.GeneratedMips,
            ["format"] = texture.TextureData?.Image?.Format.ToString() ?? "Png"
        };

        string metaJson = JsonSerializer.Serialize(textureMeta, jsonWriteOptions);
        string metaFile = Path.Combine(textureDir, $"{textureName}.json");
        File.WriteAllText(metaFile, metaJson, Encoding.UTF8);
    }
    catch (Exception ex)
    {
        PrintLine($"[ExportEmbeddedTextures] Failed to export texture {i}: {ex.Message}");
    }

    IncrementProgress();
}

await StopProgressBarUpdater();
HideProgressBar();

PrintLine($"[ExportEmbeddedTextures] Export complete. {allTextures.Count} textures exported to {texturesOut}");
