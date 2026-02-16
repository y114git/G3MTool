using System.Text;
using System.Text.Json;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using static UndertaleModLib.Models.UndertaleSound;

namespace G3MToolCLI.Services;

public static partial class ResourceImportService
{
    // =========================================================================
    // TextureGroupInfo
    // =========================================================================
    private static void ImportTextureGroupInfo(UndertaleData data, string inputDir)
    {
        if (data.TextureGroupInfo == null) return;

        var dirs = GetDirs(inputDir);
        if (dirs.Length == 0) return;

        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            string jsonFile = Path.Combine(dir, name + ".json");
            if (!FExists(jsonFile)) continue;

            try
            {
                using var jsonDoc = JsonDocument.Parse(FReadText(jsonFile));
                var root = jsonDoc.RootElement;

                var tg = data.TextureGroupInfo.FirstOrDefault(t => t.Name?.Content == name);
                if (tg == null)
                {
                    tg = new UndertaleTextureGroupInfo { Name = data.Strings.MakeString(name) };
                    data.TextureGroupInfo.Add(tg);
                }

                if (root.TryGetProperty("name", out JsonElement nmElm) && nmElm.ValueKind == JsonValueKind.String)
                    tg.Name = data.Strings.MakeString(nmElm.GetString()!);

                if (data.IsVersionAtLeast(2022, 9))
                {
                    if (root.TryGetProperty("directory", out JsonElement dirElm) && dirElm.ValueKind == JsonValueKind.String)
                        tg.Directory = data.Strings.MakeString(dirElm.GetString()!);
                    if (root.TryGetProperty("extension", out JsonElement extElm) && extElm.ValueKind == JsonValueKind.String)
                        tg.Extension = data.Strings.MakeString(extElm.GetString()!);
                    if (root.TryGetProperty("loadType", out JsonElement ltElm) && ltElm.ValueKind == JsonValueKind.Number)
                        tg.LoadType = (UndertaleTextureGroupInfo.TextureGroupLoadType)ltElm.GetInt32();
                }

                if (root.TryGetProperty("texturePages", out JsonElement tpElm) && tpElm.ValueKind == JsonValueKind.Array)
                {
                    tg.TexturePages.Clear();
                    foreach (var e in tpElm.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.String) continue;
                        string tpName = e.GetString() ?? "";
                        if (string.IsNullOrEmpty(tpName)) continue;
                        var tp = data.EmbeddedTextures.FirstOrDefault(t => t.Name?.Content == tpName);
                        if (tp != null)
                            tg.TexturePages.Add(new UndertaleResourceById<UndertaleEmbeddedTexture, UndertaleChunkTXTR>(tp));
                    }
                }

                if (root.TryGetProperty("sprites", out JsonElement spElm) && spElm.ValueKind == JsonValueKind.Array)
                {
                    tg.Sprites.Clear();
                    foreach (var e in spElm.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.String) continue;
                        string sn = e.GetString() ?? "";
                        var spr = data.Sprites.ByName(sn);
                        if (spr != null)
                            tg.Sprites.Add(new UndertaleResourceById<UndertaleSprite, UndertaleChunkSPRT>(spr));
                    }
                }

                if (!data.IsNonLTSVersionAtLeast(2023, 1) &&
                    root.TryGetProperty("spineSprites", out JsonElement ssElm) && ssElm.ValueKind == JsonValueKind.Array)
                {
                    tg.SpineSprites.Clear();
                    foreach (var e in ssElm.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.String) continue;
                        string sn = e.GetString() ?? "";
                        var spr = data.Sprites.ByName(sn);
                        if (spr != null)
                            tg.SpineSprites.Add(new UndertaleResourceById<UndertaleSprite, UndertaleChunkSPRT>(spr));
                    }
                }

                if (root.TryGetProperty("fonts", out JsonElement fnElm) && fnElm.ValueKind == JsonValueKind.Array)
                {
                    tg.Fonts.Clear();
                    foreach (var e in fnElm.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.String) continue;
                        string fn = e.GetString() ?? "";
                        var font = data.Fonts.ByName(fn);
                        if (font != null)
                            tg.Fonts.Add(new UndertaleResourceById<UndertaleFont, UndertaleChunkFONT>(font));
                    }
                }

                if (root.TryGetProperty("tilesets", out JsonElement tsElm) && tsElm.ValueKind == JsonValueKind.Array)
                {
                    tg.Tilesets.Clear();
                    foreach (var e in tsElm.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.String) continue;
                        string tn = e.GetString() ?? "";
                        var tileset = data.Backgrounds.ByName(tn);
                        if (tileset != null)
                            tg.Tilesets.Add(new UndertaleResourceById<UndertaleBackground, UndertaleChunkBGND>(tileset));
                    }
                }
            }
            catch (Exception ex) { Log($"[ImportTextureGroupInfo] Error: {name}: {ex.Message}"); }
        }
        Log("[ImportTextureGroupInfo] Done.");
    }

    // =========================================================================
    // Fonts
    // =========================================================================
    private static void ImportFonts(UndertaleData data, string inputDir)
    {
        var dirs = GetDirs(inputDir);
        if (dirs.Length == 0) return;

        int imported = 0, created = 0, skipped = 0;
        foreach (string dir in dirs)
        {
            string safeName = Path.GetFileName(dir);
            string jsonPath = Path.Combine(dir, "font.json");
            string pngPath = Path.Combine(dir, "texture.png");
            if (!FExists(jsonPath)) { skipped++; continue; }

            string fontName = safeName;
            try
            {
                using var jsonDoc = JsonDocument.Parse(FReadText(jsonPath));
                var root = jsonDoc.RootElement;
                if (root.TryGetProperty("name", out JsonElement nmElm))
                    fontName = nmElm.GetString() ?? safeName;

                var font = data.Fonts.ByName(fontName);
                bool isNew = font == null;
                if (isNew)
                {
                    font = new UndertaleFont
                    {
                        Name = data.Strings.MakeString(fontName),
                        DisplayName = data.Strings.MakeString(fontName),
                        Glyphs = [],
                        EmSize = 12,
                        Bold = false,
                        Italic = false,
                        RangeStart = 32,
                        RangeEnd = 127,
                        Charset = 1,
                        AntiAliasing = 1,
                        ScaleX = 1.0f,
                        ScaleY = 1.0f
                    };
                    created++;
                }

                if (FExists(pngPath))
                {
                    using var img = new MagickImage(FReadBytes(pngPath));
                    if (!isNew && font!.Texture?.TexturePage != null)
                    {
                        font.Texture.SourceWidth = (ushort)img.Width;
                        font.Texture.SourceHeight = (ushort)img.Height;
                        font.Texture.TargetWidth = (ushort)img.Width;
                        font.Texture.TargetHeight = (ushort)img.Height;
                    }
                    else
                    {
                        int lastTP = data.EmbeddedTextures.Count - 1;
                        int lastTPI = data.TexturePageItems.Count - 1;
                        var newTex = new UndertaleEmbeddedTexture
                        {
                            Name = new UndertaleString($"Texture {++lastTP}")
                        };
                        newTex.TextureData.Image = GMImage.FromMagickImage(img).ConvertToPng();
                        data.EmbeddedTextures.Add(newTex);

                        ushort origTX = font!.Texture?.TargetX ?? 0;
                        ushort origTY = font.Texture?.TargetY ?? 0;
                        ushort origBW = font.Texture?.BoundingWidth ?? (ushort)img.Width;
                        ushort origBH = font.Texture?.BoundingHeight ?? (ushort)img.Height;

                        var newTPI = new UndertaleTexturePageItem
                        {
                            Name = new UndertaleString($"PageItem {++lastTPI}"),
                            SourceX = 0,
                            SourceY = 0,
                            SourceWidth = (ushort)img.Width,
                            SourceHeight = (ushort)img.Height,
                            TargetX = origTX,
                            TargetY = origTY,
                            TargetWidth = (ushort)img.Width,
                            TargetHeight = (ushort)img.Height,
                            BoundingWidth = origBW,
                            BoundingHeight = origBH,
                            TexturePage = newTex
                        };
                        data.TexturePageItems.Add(newTPI);
                        font.Texture = newTPI;
                    }
                }

                if (root.TryGetProperty("displayName", out JsonElement dnElm))
                {
                    string dn = dnElm.GetString() ?? "";
                    if (dn.Length > 0) font!.DisplayName = data.Strings.MakeString(dn);
                }

                if (root.TryGetProperty("emSize", out JsonElement esElm)) font!.EmSize = (float)esElm.GetDouble();
                if (root.TryGetProperty("emSizeIsFloat", out JsonElement esfElm)) font!.EmSizeIsFloat = esfElm.GetBoolean();
                if (root.TryGetProperty("bold", out JsonElement bElm)) font!.Bold = bElm.GetBoolean();
                if (root.TryGetProperty("italic", out JsonElement iElm)) font!.Italic = iElm.GetBoolean();
                if (root.TryGetProperty("rangeStart", out JsonElement rsElm)) font!.RangeStart = (ushort)rsElm.GetInt32();
                if (root.TryGetProperty("rangeEnd", out JsonElement reElm)) font!.RangeEnd = (uint)reElm.GetInt64();
                if (root.TryGetProperty("charset", out JsonElement csElm)) font!.Charset = (byte)csElm.GetInt32();
                if (root.TryGetProperty("antiAliasing", out JsonElement aaElm)) font!.AntiAliasing = (byte)aaElm.GetInt32();
                if (root.TryGetProperty("scaleX", out JsonElement sxElm)) font!.ScaleX = (float)sxElm.GetDouble();
                if (root.TryGetProperty("scaleY", out JsonElement syElm)) font!.ScaleY = (float)syElm.GetDouble();

                if (data.GeneralInfo?.BytecodeVersion >= 17 && root.TryGetProperty("ascenderOffset", out JsonElement aoElm))
                    font!.AscenderOffset = aoElm.GetInt32();
                if (data.IsVersionAtLeast(2022, 2) && root.TryGetProperty("ascender", out JsonElement ascElm))
                    font!.Ascender = (uint)ascElm.GetInt64();
                if (data.IsVersionAtLeast(2023, 2) && root.TryGetProperty("sdfSpread", out JsonElement sdfElm))
                    font!.SDFSpread = (uint)sdfElm.GetInt64();
                if (data.IsVersionAtLeast(2023, 6) && root.TryGetProperty("lineHeight", out JsonElement lhElm))
                    font!.LineHeight = (uint)lhElm.GetInt64();

                if (root.TryGetProperty("glyphs", out JsonElement gElm) && gElm.ValueKind == JsonValueKind.Array)
                {
                    font!.Glyphs.Clear();
                    foreach (var ge in gElm.EnumerateArray())
                    {
                        var glyph = new UndertaleFont.Glyph();
                        if (ge.TryGetProperty("character", out JsonElement cElm2)) glyph.Character = (ushort)cElm2.GetInt32();
                        if (ge.TryGetProperty("sourceX", out JsonElement sx2)) glyph.SourceX = (ushort)sx2.GetInt32();
                        if (ge.TryGetProperty("sourceY", out JsonElement sy2)) glyph.SourceY = (ushort)sy2.GetInt32();
                        if (ge.TryGetProperty("sourceWidth", out JsonElement sw2)) glyph.SourceWidth = (ushort)sw2.GetInt32();
                        if (ge.TryGetProperty("sourceHeight", out JsonElement sh2)) glyph.SourceHeight = (ushort)sh2.GetInt32();
                        if (ge.TryGetProperty("shift", out JsonElement shElm)) glyph.Shift = (short)shElm.GetInt32();
                        if (ge.TryGetProperty("offset", out JsonElement ofElm)) glyph.Offset = (short)ofElm.GetInt32();
                        if (ge.TryGetProperty("kerning", out JsonElement kElm) && kElm.ValueKind == JsonValueKind.Array)
                        {
                            glyph.Kerning = [];
                            foreach (var ke in kElm.EnumerateArray())
                            {
                                var kern = new UndertaleFont.Glyph.GlyphKerning();
                                if (ke.TryGetProperty("character", out JsonElement kc)) kern.Character = (short)kc.GetInt32();
                                if (ke.TryGetProperty("shiftModifier", out JsonElement ks)) kern.ShiftModifier = (short)ks.GetInt32();
                                glyph.Kerning.Add(kern);
                            }
                        }
                        font.Glyphs.Add(glyph);
                    }
                }

                if (isNew) data.Fonts.Add(font!);
                imported++;
            }
            catch (Exception ex) { Log($"[ImportFonts] Failed: {fontName}: {ex.Message}"); skipped++; }
        }
        Log($"[ImportFonts] Done. {imported} imported ({created} new), {skipped} skipped.");
    }

    // =========================================================================
    // Sounds
    // =========================================================================
    private static void ImportSounds(UndertaleData data, string inputDir)
    {
        var soundDirs = GetDirs(inputDir).Select(Path.GetFileName).ToList();
        if (soundDirs.Count == 0) return;

        int imported = 0, created = 0, skipped = 0, metadataApplied = 0;

        foreach (string? soundName in soundDirs)
        {
            if (soundName == null) continue;
            string soundDir = Path.Combine(inputDir, soundName);
            string oggFile = Path.Combine(soundDir, soundName + ".ogg");
            string wavFile = Path.Combine(soundDir, soundName + ".wav");
            string metaFile = Path.Combine(soundDir, soundName + ".json");

            string? audioFile = null;
            bool isOGG = false;
            if (FExists(oggFile)) { audioFile = oggFile; isOGG = true; }
            else if (FExists(wavFile)) { audioFile = wavFile; }

            if (audioFile == null && !FExists(metaFile)) { skipped++; continue; }

            if (audioFile == null && FExists(metaFile))
            {
                var existing = data.Sounds.ByName(soundName);
                if (existing == null) { skipped++; continue; }
                try { ApplySoundMetadata(data, existing, metaFile); metadataApplied++; }
                catch (Exception ex) { Log($"[ImportSounds] Metadata error: {soundName}: {ex.Message}"); }
                continue;
            }

            try
            {
                byte[] audioData = FReadBytes(audioFile!);
                if (audioData.Length == 0) { skipped++; continue; }

                var sound = data.Sounds.ByName(soundName);
                bool isNew = sound == null;
                if (isNew)
                {
                    sound = new UndertaleSound
                    {
                        Name = data.Strings.MakeString(soundName),
                        File = data.Strings.MakeString(soundName + (isOGG ? ".ogg" : ".wav")),
                        Type = data.Strings.MakeString(isOGG ? ".ogg" : ".wav"),
                        Volume = 1.0f,
                        Pitch = 1.0f,
                        Preload = true,
                        Flags = AudioEntryFlags.IsEmbedded
                    };
                    if (isOGG) sound.Flags |= AudioEntryFlags.IsCompressed;
                    sound.AudioFile = new UndertaleEmbeddedAudio { Data = audioData };
                    data.EmbeddedAudio.Add(sound.AudioFile);
                    sound.AudioID = data.EmbeddedAudio.Count - 1;
                    if (data.AudioGroups?.Count > 0)
                    {
                        sound.AudioGroup = data.AudioGroups[0];
                        sound.GroupID = 0;
                    }
                    created++;
                }
                else
                {
                    if (sound!.AudioFile == null)
                    {
                        sound.AudioFile = new UndertaleEmbeddedAudio();
                        data.EmbeddedAudio.Add(sound.AudioFile);
                        sound.AudioID = data.EmbeddedAudio.Count - 1;
                    }
                    sound.AudioFile.Data = audioData;
                    sound.Flags |= AudioEntryFlags.IsEmbedded;
                    if (isOGG) sound.Flags |= AudioEntryFlags.IsCompressed;
                    else sound.Flags &= ~AudioEntryFlags.IsCompressed;
                }

                if (FExists(metaFile))
                {
                    try { ApplySoundMetadata(data, sound!, metaFile); metadataApplied++; }
                    catch { }
                }

                if (isNew) data.Sounds.Add(sound!);
                imported++;
            }
            catch (Exception ex) { Log($"[ImportSounds] Failed: {soundName}: {ex.Message}"); skipped++; }
        }
        Log($"[ImportSounds] Done. {imported} imported ({created} new, {metadataApplied} with metadata), {skipped} skipped.");
    }

    private static void ApplySoundMetadata(UndertaleData data, UndertaleSound sound, string metaFile)
    {
        using var jsonDoc = JsonDocument.Parse(FReadText(metaFile));
        var root = jsonDoc.RootElement;

        if (root.TryGetProperty("volume", out JsonElement vElm)) sound.Volume = (float)vElm.GetDouble();
        if (root.TryGetProperty("pitch", out JsonElement pElm)) sound.Pitch = (float)pElm.GetDouble();
        if (root.TryGetProperty("preload", out JsonElement prElm)) sound.Preload = prElm.GetBoolean();
        if (root.TryGetProperty("effects", out JsonElement eElm)) sound.Effects = (uint)eElm.GetInt32();
        if (root.TryGetProperty("flags", out JsonElement fElm)) sound.Flags = (AudioEntryFlags)(uint)fElm.GetInt32();
        if (root.TryGetProperty("audioGroupName", out JsonElement agElm))
        {
            string agName = agElm.GetString() ?? "";
            if (!string.IsNullOrEmpty(agName) && data.AudioGroups != null)
            {
                var ag = data.AudioGroups.ByName(agName);
                if (ag != null) { sound.AudioGroup = ag; sound.GroupID = data.AudioGroups.IndexOf(ag); }
            }
        }
        if (root.TryGetProperty("audioLength", out JsonElement alElm) && data.IsVersionAtLeast(2024, 6))
            sound.AudioLength = (float)alElm.GetDouble();
    }

    // =========================================================================
    // Tilesets
    // =========================================================================
    private static void ImportTilesets(UndertaleData data, string inputDir)
    {
        if (!data.IsGameMaker2()) return;

        var names = new HashSet<string>();
        foreach (var f in GetFilesIn(inputDir, "*.png"))
            names.Add(Path.GetFileNameWithoutExtension(f));
        foreach (var f in GetFilesIn(inputDir, "*.json"))
        {
            string n = Path.GetFileNameWithoutExtension(f);
            if (!n.Equals("config", StringComparison.OrdinalIgnoreCase))
                names.Add(n);
        }
        if (names.Count == 0) return;

        int imported = 0, created = 0;
        using TextureWorker worker = new();

        foreach (string tsName in names)
        {
            string pngPath = Path.Combine(inputDir, tsName + ".png");
            string jsonPath = Path.Combine(inputDir, tsName + ".json");
            if (!FExists(pngPath) && !FExists(jsonPath)) continue;

            try
            {
                var ts = data.Backgrounds.ByName(tsName);
                bool isNew = ts == null;
                if (isNew)
                {
                    ts = new UndertaleBackground
                    {
                        Name = data.Strings.MakeString(tsName),
                        Transparent = false,
                        Smooth = false,
                        Preload = false
                    };
                    created++;
                }

                if (FExists(pngPath))
                {
                    using var img = new MagickImage(FReadBytes(pngPath));
                    int lastTP = data.EmbeddedTextures.Count - 1;
                    int lastTPI = data.TexturePageItems.Count - 1;

                    var newTex = new UndertaleEmbeddedTexture
                    {
                        Name = new UndertaleString($"Texture {++lastTP}")
                    };
                    newTex.TextureData.Image = GMImage.FromMagickImage(img).ConvertToPng();
                    data.EmbeddedTextures.Add(newTex);

                    var newTPI = new UndertaleTexturePageItem
                    {
                        Name = new UndertaleString($"PageItem {++lastTPI}"),
                        SourceX = 0,
                        SourceY = 0,
                        SourceWidth = (ushort)img.Width,
                        SourceHeight = (ushort)img.Height,
                        TargetX = 0,
                        TargetY = 0,
                        TargetWidth = (ushort)img.Width,
                        TargetHeight = (ushort)img.Height,
                        BoundingWidth = (ushort)img.Width,
                        BoundingHeight = (ushort)img.Height,
                        TexturePage = newTex
                    };
                    data.TexturePageItems.Add(newTPI);
                    ts!.Texture = newTPI;
                }

                if (FExists(jsonPath))
                {
                    using var jsonDoc = JsonDocument.Parse(FReadText(jsonPath));
                    var root = jsonDoc.RootElement;

                    ts!.Transparent = GetJsonValue(root, "transparent", ts.Transparent);
                    ts.Smooth = GetJsonValue(root, "smooth", ts.Smooth);
                    ts.Preload = GetJsonValue(root, "preload", ts.Preload);
                    if (root.TryGetProperty("gms2UnknownAlways2", out _))
                        ts.GMS2UnknownAlways2 = GetJsonValue(root, "gms2UnknownAlways2", ts.GMS2UnknownAlways2);
                    ts.GMS2TileWidth = GetJsonValue(root, "gms2TileWidth", ts.GMS2TileWidth);
                    ts.GMS2TileHeight = GetJsonValue(root, "gms2TileHeight", ts.GMS2TileHeight);
                    ts.GMS2OutputBorderX = GetJsonValue(root, "gms2OutputBorderX", ts.GMS2OutputBorderX);
                    ts.GMS2OutputBorderY = GetJsonValue(root, "gms2OutputBorderY", ts.GMS2OutputBorderY);
                    ts.GMS2TileColumns = GetJsonValue(root, "gms2TileColumns", ts.GMS2TileColumns);
                    ts.GMS2ItemsPerTileCount = GetJsonValue(root, "gms2ItemsPerTileCount", ts.GMS2ItemsPerTileCount);
                    ts.GMS2TileCount = GetJsonValue(root, "gms2TileCount", ts.GMS2TileCount);
                    if (root.TryGetProperty("gms2ExportedSpriteIndex", out _))
                        ts.GMS2ExportedSpriteIndex = GetJsonValue(root, "gms2ExportedSpriteIndex", ts.GMS2ExportedSpriteIndex);
                    ts.GMS2FrameLength = GetJsonValue(root, "gms2FrameLength", ts.GMS2FrameLength);

                    if (data.IsVersionAtLeast(2024, 14, 1))
                    {
                        if (root.TryGetProperty("gms2TileSeparationX", out _))
                            ts.GMS2TileSeparationX = GetJsonValue(root, "gms2TileSeparationX", ts.GMS2TileSeparationX);
                        if (root.TryGetProperty("gms2TileSeparationY", out _))
                            ts.GMS2TileSeparationY = GetJsonValue(root, "gms2TileSeparationY", ts.GMS2TileSeparationY);
                    }

                    if (root.TryGetProperty("gms2TileIds", out JsonElement tileIdsElm) && tileIdsElm.ValueKind == JsonValueKind.Array)
                    {
                        int expected = (int)(ts.GMS2TileCount * ts.GMS2ItemsPerTileCount);
                        var ids = tileIdsElm.EnumerateArray().ToList();
                        if (ids.Count == expected)
                        {
                            ts.GMS2TileIds.Clear();
                            foreach (var id in ids)
                                ts.GMS2TileIds.Add(new UndertaleBackground.TileID { ID = (uint)id.GetInt64() });
                        }
                    }
                }

                if (isNew) data.Backgrounds.Add(ts!);
                imported++;
            }
            catch (Exception ex) { Log($"[ImportTilesets] Failed: {tsName}: {ex.Message}"); }
        }
        Log($"[ImportTilesets] Done. {imported} processed ({created} new).");
    }

    // =========================================================================
    // Extensions
    // =========================================================================
    private static void ImportExtensions(UndertaleData data, string inputDir)
    {
        var dirs = GetDirs(inputDir);
        if (dirs.Length == 0) return;

        int created = 0, updated = 0;
        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            string jsonFile = Path.Combine(dir, name + ".json");
            if (!FExists(jsonFile)) continue;

            try
            {
                using var jsonDoc = JsonDocument.Parse(FReadText(jsonFile));
                var root = jsonDoc.RootElement;
                if (root.TryGetProperty("name", out JsonElement nmElm))
                    name = nmElm.GetString() ?? name;

                var ext = data.Extensions?.ByName(name);
                bool isNew = ext == null;
                if (isNew)
                {
                    ext = new UndertaleExtension
                    {
                        Name = data.Strings.MakeString(name),
                        Files = [],
                        Options = []
                    };
                    created++;
                }
                else updated++;

                if (root.TryGetProperty("folderName", out JsonElement fnElm))
                {
                    string fn = fnElm.GetString() ?? "";
                    if (fn.Length > 0) ext!.FolderName = data.Strings.MakeString(fn);
                }
                if (root.TryGetProperty("version", out JsonElement verElm))
                {
                    string ver = verElm.GetString() ?? "";
                    if (ver.Length > 0) ext!.Version = data.Strings.MakeString(ver);
                }
                if (root.TryGetProperty("className", out JsonElement cnElm))
                {
                    string cn = cnElm.GetString() ?? "";
                    if (cn.Length > 0) ext!.ClassName = data.Strings.MakeString(cn);
                }

                if (root.TryGetProperty("files", out JsonElement filesElm) && filesElm.ValueKind == JsonValueKind.Array)
                {
                    var arr = filesElm.EnumerateArray().ToArray();
                    for (int fi = 0; fi < arr.Length; fi++)
                    {
                        var fElm = arr[fi];
                        UndertaleExtensionFile file;
                        if (fi < ext!.Files.Count) file = ext.Files[fi];
                        else
                        {
                            file = new UndertaleExtensionFile
                            {
                                Functions = []
                            };
                            ext.Files.Add(file);
                        }

                        if (fElm.TryGetProperty("filename", out JsonElement ffnElm))
                        {
                            string ffn = ffnElm.GetString() ?? "";
                            if (ffn.Length > 0) file.Filename = data.Strings.MakeString(ffn);
                        }
                        if (fElm.TryGetProperty("kind", out JsonElement kElm))
                            file.Kind = (UndertaleExtensionKind)kElm.GetInt32();
                        if (fElm.TryGetProperty("initScript", out JsonElement isElm))
                        {
                            string s = isElm.GetString() ?? "";
                            file.InitScript = s.Length > 0 ? data.Strings.MakeString(s) : null;
                        }
                        if (fElm.TryGetProperty("cleanupScript", out JsonElement csElm))
                        {
                            string s = csElm.GetString() ?? "";
                            file.CleanupScript = s.Length > 0 ? data.Strings.MakeString(s) : null;
                        }

                        if (fElm.TryGetProperty("functions", out JsonElement funsElm) && funsElm.ValueKind == JsonValueKind.Array)
                        {
                            var funArr = funsElm.EnumerateArray().ToArray();
                            for (int fui = 0; fui < funArr.Length; fui++)
                            {
                                var fuElm = funArr[fui];
                                UndertaleExtensionFunction func;
                                if (fui < file.Functions.Count) func = file.Functions[fui];
                                else
                                {
                                    func = new UndertaleExtensionFunction
                                    {
                                        Arguments = []
                                    };
                                    file.Functions.Add(func);
                                }

                                if (fuElm.TryGetProperty("name", out JsonElement funcNm))
                                {
                                    string n = funcNm.GetString() ?? "";
                                    if (n.Length > 0) func.Name = data.Strings.MakeString(n);
                                }
                                if (fuElm.TryGetProperty("extName", out JsonElement enElm))
                                {
                                    string n = enElm.GetString() ?? "";
                                    if (n.Length > 0) func.ExtName = data.Strings.MakeString(n);
                                }
                                if (fuElm.TryGetProperty("id", out JsonElement idElm))
                                    func.ID = (uint)idElm.GetInt64();
                                if (fuElm.TryGetProperty("kind", out JsonElement fkElm))
                                    func.Kind = (uint)fkElm.GetInt64();
                                if (fuElm.TryGetProperty("retType", out JsonElement rtElm))
                                    func.RetType = (UndertaleExtensionVarType)rtElm.GetInt32();

                                if (fuElm.TryGetProperty("arguments", out JsonElement argsElm) && argsElm.ValueKind == JsonValueKind.Array)
                                {
                                    var argArr = argsElm.EnumerateArray().ToArray();
                                    for (int ai = 0; ai < argArr.Length; ai++)
                                    {
                                        var aElm = argArr[ai];
                                        UndertaleExtensionFunctionArg arg;
                                        if (ai < func.Arguments.Count) arg = func.Arguments[ai];
                                        else { arg = new UndertaleExtensionFunctionArg(); func.Arguments.Add(arg); }
                                        if (aElm.TryGetProperty("type", out JsonElement atElm))
                                            arg.Type = (UndertaleExtensionVarType)atElm.GetInt32();
                                    }
                                }
                            }
                        }
                    }
                }

                if (root.TryGetProperty("options", out JsonElement optsElm) && optsElm.ValueKind == JsonValueKind.Array)
                {
                    var optArr = optsElm.EnumerateArray().ToArray();
                    for (int oi = 0; oi < optArr.Length; oi++)
                    {
                        var oElm = optArr[oi];
                        UndertaleExtensionOption opt;
                        if (oi < ext!.Options.Count) opt = ext.Options[oi];
                        else { opt = new UndertaleExtensionOption(); ext.Options.Add(opt); }

                        if (oElm.TryGetProperty("name", out JsonElement onElm))
                        {
                            string on = onElm.GetString() ?? "";
                            if (on.Length > 0) opt.Name = data.Strings.MakeString(on);
                        }
                        if (oElm.TryGetProperty("value", out JsonElement ovElm))
                            opt.Value = data.Strings.MakeString(ovElm.GetString() ?? "");
                        if (oElm.TryGetProperty("kind", out JsonElement okElm))
                            opt.Kind = (UndertaleExtensionOption.OptionKind)okElm.GetInt32();
                    }
                }

                if (isNew) data.Extensions!.Add(ext!);
            }
            catch (Exception ex) { Log($"[ImportExtensions] Error: {name}: {ex.Message}"); }
        }
        Log($"[ImportExtensions] Done. Created: {created}, Updated: {updated}");
    }
}
