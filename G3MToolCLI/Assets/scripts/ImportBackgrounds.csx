



using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;




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
            if (typeof(T) == typeof(long))
                return (T)(object)elm.GetInt64();
            if (typeof(T) == typeof(bool))
                return (T)(object)elm.GetBoolean();
            if (typeof(T) == typeof(float))
                return (T)(object)(float)elm.GetDouble();
            if (typeof(T) == typeof(string))
                return (T)(object)(elm.GetString() ?? "");
        }
        catch { }
    }
    return defaultValue;
}




EnsureDataLoaded();

string bgDir = GetInputDirectory();
PrintLine($"[ImportBackgrounds] Importing from: {bgDir}");


var bgDirs = Directory.GetDirectories(bgDir);

if (bgDirs.Length == 0)
{
    PrintLine("[ImportBackgrounds] No background directories found - nothing to import.");
    return;
}

PrintLine($"[ImportBackgrounds] Found {bgDirs.Length} background(s) to process.");

int imported = 0;
int created = 0;

using (TextureWorker worker = new TextureWorker())
{
    foreach (string bgSubDir in bgDirs)
    {
        string bgName = Path.GetFileName(bgSubDir);
        string pngPath = Path.Combine(bgSubDir, bgName + ".png");
        string jsonPath = Path.Combine(bgSubDir, bgName + ".json");

        if (!File.Exists(pngPath) && !File.Exists(jsonPath))
            continue;

        try
        {
            UndertaleBackground bg = Data.Backgrounds.ByName(bgName);
            bool isNew = false;

            if (bg == null)
            {
                bg = new UndertaleBackground();
                bg.Name = Data.Strings.MakeString(bgName);
                bg.Transparent = false;
                bg.Smooth = false;
                bg.Preload = false;
                // GMS2: zero out tile properties to prevent serialization errors
                // (default values may be non-zero, causing "Bad tile list length" on save)
                if (Data.IsGameMaker2())
                {
                    bg.GMS2TileWidth = 0;
                    bg.GMS2TileHeight = 0;
                    bg.GMS2TileColumns = 0;
                    bg.GMS2TileCount = 0;
                    bg.GMS2OutputBorderX = 0;
                    bg.GMS2OutputBorderY = 0;
                    bg.GMS2FrameLength = 0;
                }
                isNew = true;
                created++;
                PrintLine($"[ImportBackgrounds] Creating new background: {bgName}");
            }


            if (File.Exists(pngPath))
            {
                using (var img = TextureWorker.ReadBGRAImageFromFile(pngPath))
                {
                    int lastTextPage = Data.EmbeddedTextures.Count - 1;
                    int lastTextPageItem = Data.TexturePageItems.Count - 1;

                    UndertaleEmbeddedTexture newEmbeddedTexture = new UndertaleEmbeddedTexture();
                    newEmbeddedTexture.Name = new UndertaleString($"Texture {++lastTextPage}");
                    newEmbeddedTexture.TextureData.Image = GMImage.FromMagickImage(img).ConvertToPng();
                    Data.EmbeddedTextures.Add(newEmbeddedTexture);

                    UndertaleTexturePageItem newTexturePageItem = new UndertaleTexturePageItem();
                    newTexturePageItem.Name = new UndertaleString($"PageItem {++lastTextPageItem}");
                    newTexturePageItem.SourceX = 0;
                    newTexturePageItem.SourceY = 0;
                    newTexturePageItem.SourceWidth = (ushort)img.Width;
                    newTexturePageItem.SourceHeight = (ushort)img.Height;
                    newTexturePageItem.TargetX = 0;
                    newTexturePageItem.TargetY = 0;
                    newTexturePageItem.TargetWidth = (ushort)img.Width;
                    newTexturePageItem.TargetHeight = (ushort)img.Height;
                    newTexturePageItem.BoundingWidth = (ushort)img.Width;
                    newTexturePageItem.BoundingHeight = (ushort)img.Height;
                    newTexturePageItem.TexturePage = newEmbeddedTexture;
                    Data.TexturePageItems.Add(newTexturePageItem);

                    bg.Texture = newTexturePageItem;
                }
            }


            if (File.Exists(jsonPath))
            {
                string jsonContent = File.ReadAllText(jsonPath, Encoding.UTF8);
                JsonDocument jsonDoc = JsonDocument.Parse(jsonContent);
                JsonElement root = jsonDoc.RootElement;

                bg.Transparent = GetJsonValue<bool>(root, "transparent", bg.Transparent);
                bg.Smooth = GetJsonValue<bool>(root, "smooth", bg.Smooth);
                bg.Preload = GetJsonValue<bool>(root, "preload", bg.Preload);


                if (Data.IsGameMaker2())
                {
                    if (root.TryGetProperty("gms2UnknownAlways2", out _))
                        bg.GMS2UnknownAlways2 = GetJsonValue<uint>(root, "gms2UnknownAlways2", bg.GMS2UnknownAlways2);
                }

                jsonDoc.Dispose();
            }

            if (isNew)
            {
                Data.Backgrounds.Add(bg);
            }

            PrintLine($"[ImportBackgrounds] {(isNew ? "Created" : "Updated")} background: {bgName}");
            imported++;
        }
        catch (Exception ex)
        {
            PrintLine($"[ImportBackgrounds] Failed to import {bgName}: {ex.Message}");
        }
    }
}

PrintLine($"[ImportBackgrounds] Import complete. {imported} backgrounds processed ({created} new).");





