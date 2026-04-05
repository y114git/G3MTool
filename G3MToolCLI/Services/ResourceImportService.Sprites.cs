using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace G3MToolCLI.Services;

public static partial class ResourceImportService
{
    // =========================================================================
    // Sprites
    // =========================================================================

    private class SpriteImportData
    {
        public int TargetIndex;
        public string Name = "";
        public string FolderName = "";
        public string FolderPath = "";
        public JsonElement? Meta;
        public List<Dictionary<string, object>>? FrameData;
        public bool IsNew;
        public bool HasValidTextureIndex;
    }

    private static void ImportSprites(UndertaleData data, string inputDir)
    {
        string spritesPath = inputDir;
        Log($"[ImportSprites] Importing from: {spritesPath}");

        var spriteMetadataCache = new Dictionary<string, JsonElement?>();
        var textureFrameCache = new Dictionary<string, List<Dictionary<string, object>>?>();

        // --- Local helpers (match CSX logic) ---
        JsonElement? LoadMeta(string folderName)
        {
            if (spriteMetadataCache.TryGetValue(folderName, out var cached)) return cached;
            string metaFile = Path.Combine(spritesPath, folderName, $"{folderName}.json");
            if (!FExists(metaFile)) metaFile = Path.Combine(spritesPath, folderName, "sprite_meta.json");
            if (!FExists(metaFile)) { spriteMetadataCache[folderName] = null; return null; }
            try
            {
                var doc = JsonDocument.Parse(FReadText(metaFile));
                spriteMetadataCache[folderName] = doc.RootElement.Clone();
                return spriteMetadataCache[folderName];
            }
            catch { spriteMetadataCache[folderName] = null; return null; }
        }

        List<Dictionary<string, object>>? LoadFrameData(string folderName)
        {
            if (textureFrameCache.TryGetValue(folderName, out var cached)) return cached;
            var meta = LoadMeta(folderName);
            if (meta.HasValue && meta.Value.TryGetProperty("textureFrames", out var framesElm) && framesElm.ValueKind == JsonValueKind.Array)
            {
                var frames = new List<Dictionary<string, object>>();
                foreach (var frameElm in framesElm.EnumerateArray())
                {
                    var frame = new Dictionary<string, object>();
                    if (frameElm.TryGetProperty("frameIndex", out var idxElm)) frame["frameIndex"] = idxElm.GetInt32();
                    if (frameElm.TryGetProperty("isNull", out var nullElm)) frame["isNull"] = nullElm.GetBoolean();
                    if (frameElm.TryGetProperty("texturePageIndex", out var tpIdxElm)) frame["texturePageIndex"] = tpIdxElm.GetInt32();
                    if (frameElm.TryGetProperty("sourceX", out var sxElm)) frame["sourceX"] = (ushort)sxElm.GetInt32();
                    if (frameElm.TryGetProperty("sourceY", out var syElm)) frame["sourceY"] = (ushort)syElm.GetInt32();
                    if (frameElm.TryGetProperty("sourceWidth", out var swElm)) frame["sourceWidth"] = (ushort)swElm.GetInt32();
                    if (frameElm.TryGetProperty("sourceHeight", out var shElm)) frame["sourceHeight"] = (ushort)shElm.GetInt32();
                    if (frameElm.TryGetProperty("targetX", out var txElm)) frame["targetX"] = (ushort)txElm.GetInt32();
                    if (frameElm.TryGetProperty("targetY", out var tyElm)) frame["targetY"] = (ushort)tyElm.GetInt32();
                    if (frameElm.TryGetProperty("targetWidth", out var twElm)) frame["targetWidth"] = (ushort)twElm.GetInt32();
                    if (frameElm.TryGetProperty("targetHeight", out var thElm)) frame["targetHeight"] = (ushort)thElm.GetInt32();
                    if (frameElm.TryGetProperty("boundingWidth", out var bwElm)) frame["boundingWidth"] = (ushort)bwElm.GetInt32();
                    if (frameElm.TryGetProperty("boundingHeight", out var bhElm)) frame["boundingHeight"] = (ushort)bhElm.GetInt32();
                    frames.Add(frame);
                }
                textureFrameCache[folderName] = frames;
                return frames;
            }
            textureFrameCache[folderName] = null;
            return null;
        }

        UndertaleTexturePageItem? FindExistingTPI(int texturePageIndex, ushort sourceX, ushort sourceY, ushort sourceWidth, ushort sourceHeight)
        {
            if (texturePageIndex < 0 || texturePageIndex >= data.EmbeddedTextures.Count) return null;
            var texturePage = data.EmbeddedTextures[texturePageIndex];
            foreach (var tpi in data.TexturePageItems)
                if (tpi.TexturePage == texturePage && tpi.SourceX == sourceX && tpi.SourceY == sourceY &&
                    tpi.SourceWidth == sourceWidth && tpi.SourceHeight == sourceHeight)
                    return tpi;
            return null;
        }

        void ApplyFrameProps(UndertaleTexturePageItem tpi, List<Dictionary<string, object>> frameData, int frameIndex)
        {
            if (frameIndex >= frameData.Count) return;
            var frame = frameData[frameIndex];
            if (frame.TryGetValue("isNull", out var isNullVal) && (bool)isNullVal) return;
            if (frame.TryGetValue("targetX", out var txVal)) tpi.TargetX = (ushort)txVal;
            if (frame.TryGetValue("targetY", out var tyVal)) tpi.TargetY = (ushort)tyVal;
            if (frame.TryGetValue("targetWidth", out var twVal)) tpi.TargetWidth = (ushort)twVal;
            if (frame.TryGetValue("targetHeight", out var thVal)) tpi.TargetHeight = (ushort)thVal;
            if (frame.TryGetValue("boundingWidth", out var bwVal)) tpi.BoundingWidth = (ushort)bwVal;
            if (frame.TryGetValue("boundingHeight", out var bhVal)) tpi.BoundingHeight = (ushort)bhVal;
        }

        void ApplyOptionalFrameFields(UndertaleTexturePageItem tpi, Dictionary<string, object> frame)
        {
            if (frame.TryGetValue("targetX", out var txv)) tpi.TargetX = (ushort)Convert.ToInt32(txv);
            if (frame.TryGetValue("targetY", out var tyv)) tpi.TargetY = (ushort)Convert.ToInt32(tyv);
            if (frame.TryGetValue("targetWidth", out var twv)) tpi.TargetWidth = (ushort)Convert.ToInt32(twv);
            if (frame.TryGetValue("targetHeight", out var thv)) tpi.TargetHeight = (ushort)Convert.ToInt32(thv);
            if (frame.TryGetValue("boundingWidth", out var bwv)) tpi.BoundingWidth = (ushort)Convert.ToInt32(bwv);
            if (frame.TryGetValue("boundingHeight", out var bhv)) tpi.BoundingHeight = (ushort)Convert.ToInt32(bhv);
        }

        // =====================================================================
        // PHASE 1: Direct metadata import - new sprites + existing sprites
        // =====================================================================
        var spriteFolders = GetDirs(spritesPath);
        var importDataList = new List<SpriteImportData>();

        foreach (string spriteFolder in spriteFolders)
        {
            string folderName = Path.GetFileName(spriteFolder);
            var meta = LoadMeta(folderName);
            if (!meta.HasValue) continue;

            string spriteName = folderName;
            if (meta.Value.TryGetProperty("name", out var nameElm))
            {
                string? jsonName = nameElm.GetString();
                spriteName = jsonName ?? folderName;
            }

            var frameData = LoadFrameData(folderName);
            bool isNew = data.Sprites.ByName(spriteName) == null;

            int targetIndex = -1;
            if (meta.Value.TryGetProperty("index", out var indexElm))
                targetIndex = indexElm.GetInt32();

            bool hasValidTextureIndex = (frameData != null && frameData.Count == 0) ||
                (frameData != null && frameData.Any(f =>
                    f.ContainsKey("texturePageIndex") &&
                    Convert.ToInt32(f["texturePageIndex"]) >= 0 &&
                    Convert.ToInt32(f["texturePageIndex"]) < data.EmbeddedTextures.Count));

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
        }

        Log($"[ImportSprites] Collected {importDataList.Count} sprites. New: {importDataList.Count(d => d.IsNew)}, Existing: {importDataList.Count(d => !d.IsNew)}");
        ReportProgress(5, 100);

        // Alias sprite names in metadata cache (folder names may have __idx suffixes)
        foreach (var importData in importDataList)
        {
            if (importData.Name != importData.FolderName)
            {
                if (spriteMetadataCache.TryGetValue(importData.FolderName, out var cachedMeta))
                    spriteMetadataCache[importData.Name] = cachedMeta;
            }
        }

        // --- Phase 1a: Create new sprites with valid texture indices ---
        int created = 0;
        foreach (var importData in importDataList.Where(d => d.IsNew && d.HasValidTextureIndex))
        {
            var sprite = CreateSpriteFromMetadata(data, importData, FindExistingTPI);
            if (sprite != null)
            {
                data.Sprites.Add(sprite);
                created++;
            }
        }
        Log($"[ImportSprites] Phase 1a: {created} new sprites created from metadata");
        ReportProgress(15, 100);

        // --- Phase 1b: Update existing sprites (metadata + rebuild texture frames) ---
        int updated = 0;
        foreach (var importData in importDataList.Where(d => !d.IsNew))
        {
            var sprite = data.Sprites.ByName(importData.Name);
            if (sprite == null || !importData.Meta.HasValue) continue;

            ApplySpriteMetadata(data, sprite, importData.Meta.Value);

            if (importData.FrameData != null)
            {
                var oldTPIs = new List<UndertaleTexturePageItem>();
                foreach (var texEntry in sprite.Textures)
                    if (texEntry?.Texture != null) oldTPIs.Add(texEntry.Texture);

                sprite.Textures.Clear();
                int oldTPIIndex = 0;

                foreach (var frame in importData.FrameData)
                {
                    if (frame.TryGetValue("isNull", out var isNull) && (bool)isNull)
                    {
                        sprite.Textures.Add(new UndertaleSprite.TextureEntry { Texture = null });
                        continue;
                    }

                    int texturePageIndex = frame.TryGetValue("texturePageIndex", out var tpIdx) ? Convert.ToInt32(tpIdx) : -1;
                    if (texturePageIndex < 0 || texturePageIndex >= data.EmbeddedTextures.Count)
                        break;

                    ushort sourceX = frame.TryGetValue("sourceX", out var sxv) ? (ushort)Convert.ToInt32(sxv) : (ushort)0;
                    ushort sourceY = frame.TryGetValue("sourceY", out var syv) ? (ushort)Convert.ToInt32(syv) : (ushort)0;
                    ushort sourceWidth = frame.TryGetValue("sourceWidth", out var swv) ? (ushort)Convert.ToInt32(swv) : (ushort)0;
                    ushort sourceHeight = frame.TryGetValue("sourceHeight", out var shv) ? (ushort)Convert.ToInt32(shv) : (ushort)0;

                    var tpi = FindExistingTPI(texturePageIndex, sourceX, sourceY, sourceWidth, sourceHeight);

                    if (tpi == null && oldTPIIndex < oldTPIs.Count)
                    {
                        tpi = oldTPIs[oldTPIIndex++];
                        tpi.TexturePage = data.EmbeddedTextures[texturePageIndex];
                        tpi.SourceX = sourceX; tpi.SourceY = sourceY;
                        tpi.SourceWidth = sourceWidth; tpi.SourceHeight = sourceHeight;
                        ApplyOptionalFrameFields(tpi, frame);
                    }
                    else if (tpi == null)
                    {
                        tpi = new UndertaleTexturePageItem
                        {
                            Name = new UndertaleString($"PageItem {data.TexturePageItems.Count}"),
                            TexturePage = data.EmbeddedTextures[texturePageIndex],
                            SourceX = sourceX,
                            SourceY = sourceY,
                            SourceWidth = sourceWidth,
                            SourceHeight = sourceHeight
                        };
                        ApplyOptionalFrameFields(tpi, frame);
                        data.TexturePageItems.Add(tpi);
                    }
                    else
                    {
                        ApplyFrameProps(tpi, importData.FrameData, sprite.Textures.Count);
                    }

                    sprite.Textures.Add(new UndertaleSprite.TextureEntry { Texture = tpi });
                }
            }
            updated++;
        }
        Log($"[ImportSprites] Phase 1b: {updated} existing sprites updated");
        ReportProgress(25, 100);

        // =====================================================================
        // PHASE 2: Texture repacking - for any folder with PNGs
        // (new sprites without valid TPI + existing sprites with changed images)
        // =====================================================================
        var foldersWithPngs = spriteFolders.Where(folder =>
            GetFilesIn(folder, "*.png").Length > 0
        ).ToList();

        Log($"[ImportSprites] {foldersWithPngs.Count} sprite folders with PNGs need texture repacking");
        ReportProgress(35, 100);

        if (foldersWithPngs.Count == 0)
        {
            Log("[ImportSprites] All sprites handled via direct metadata import (no repacking needed)");
            ReportProgress(100, 100);
        }
        else
        {
            DoTextureRepack(data, spritesPath, foldersWithPngs, LoadMeta, LoadFrameData, ApplyFrameProps);
        }
    }

    private static UndertaleSprite? CreateSpriteFromMetadata(UndertaleData data, SpriteImportData importData,
        Func<int, ushort, ushort, ushort, ushort, UndertaleTexturePageItem?> findExistingTPI)
    {
        if (importData.FrameData == null) return null;

        var sprite = new UndertaleSprite
        {
            Name = data.Strings.MakeString(importData.Name)
        };

        if (importData.Meta.HasValue)
            ApplySpriteMetadata(data, sprite, importData.Meta.Value);

        foreach (var frame in importData.FrameData)
        {
            if (frame.TryGetValue("isNull", out var isNull) && (bool)isNull)
            {
                sprite.Textures.Add(new UndertaleSprite.TextureEntry { Texture = null });
                continue;
            }

            int texturePageIndex = frame.TryGetValue("texturePageIndex", out var tpIdx) ? Convert.ToInt32(tpIdx) : -1;
            if (texturePageIndex < 0 || texturePageIndex >= data.EmbeddedTextures.Count)
                return null; // Invalid texture ref - sprite can't be created from metadata

            ushort sourceX = frame.TryGetValue("sourceX", out var sxv) ? (ushort)Convert.ToInt32(sxv) : (ushort)0;
            ushort sourceY = frame.TryGetValue("sourceY", out var syv) ? (ushort)Convert.ToInt32(syv) : (ushort)0;
            ushort sourceWidth = frame.TryGetValue("sourceWidth", out var swv) ? (ushort)Convert.ToInt32(swv) : (ushort)0;
            ushort sourceHeight = frame.TryGetValue("sourceHeight", out var shv) ? (ushort)Convert.ToInt32(shv) : (ushort)0;

            var tpi = findExistingTPI(texturePageIndex, sourceX, sourceY, sourceWidth, sourceHeight);
            if (tpi == null)
            {
                tpi = new UndertaleTexturePageItem
                {
                    Name = new UndertaleString($"PageItem {data.TexturePageItems.Count}"),
                    TexturePage = data.EmbeddedTextures[texturePageIndex],
                    SourceX = sourceX,
                    SourceY = sourceY,
                    SourceWidth = sourceWidth,
                    SourceHeight = sourceHeight
                };
                if (frame.TryGetValue("targetX", out var txv)) tpi.TargetX = (ushort)Convert.ToInt32(txv);
                if (frame.TryGetValue("targetY", out var tyv)) tpi.TargetY = (ushort)Convert.ToInt32(tyv);
                if (frame.TryGetValue("targetWidth", out var twv)) tpi.TargetWidth = (ushort)Convert.ToInt32(twv);
                if (frame.TryGetValue("targetHeight", out var thv)) tpi.TargetHeight = (ushort)Convert.ToInt32(thv);
                if (frame.TryGetValue("boundingWidth", out var bwv)) tpi.BoundingWidth = (ushort)Convert.ToInt32(bwv);
                if (frame.TryGetValue("boundingHeight", out var bhv)) tpi.BoundingHeight = (ushort)Convert.ToInt32(bhv);
                data.TexturePageItems.Add(tpi);
            }

            sprite.Textures.Add(new UndertaleSprite.TextureEntry { Texture = tpi });
        }

        return sprite;
    }

    private static void ApplySpriteMetadata(UndertaleData data, UndertaleSprite sprite, JsonElement root)
    {
        if (root.TryGetProperty("width", out var wElm)) sprite.Width = (uint)wElm.GetInt64();
        if (root.TryGetProperty("height", out var hElm)) sprite.Height = (uint)hElm.GetInt64();
        if (root.TryGetProperty("originX", out var oxElm)) sprite.OriginX = oxElm.GetInt32();
        if (root.TryGetProperty("originY", out var oyElm)) sprite.OriginY = oyElm.GetInt32();
        if (root.TryGetProperty("marginLeft", out var mlElm)) sprite.MarginLeft = mlElm.GetInt32();
        if (root.TryGetProperty("marginRight", out var mrElm)) sprite.MarginRight = mrElm.GetInt32();
        if (root.TryGetProperty("marginTop", out var mtElm)) sprite.MarginTop = mtElm.GetInt32();
        if (root.TryGetProperty("marginBottom", out var mbElm)) sprite.MarginBottom = mbElm.GetInt32();
        if (root.TryGetProperty("transparent", out var trElm)) sprite.Transparent = trElm.GetBoolean();
        if (root.TryGetProperty("smooth", out var smthElm)) sprite.Smooth = smthElm.GetBoolean();
        if (root.TryGetProperty("preload", out var plElm)) sprite.Preload = plElm.GetBoolean();
        if (root.TryGetProperty("bboxMode", out var bmElm)) sprite.BBoxMode = (uint)bmElm.GetInt64();
        if (root.TryGetProperty("sepMasks", out var smElm))
            sprite.SepMasks = (UndertaleSprite.SepMaskType)smElm.GetInt32();

        // GMS2 properties (match export property names exactly)
        if (data.IsGameMaker2())
        {
            if (root.TryGetProperty("isSpecialType", out var istElm)) sprite.IsSpecialType = istElm.GetBoolean();
            if (root.TryGetProperty("sVersion", out var svElm)) sprite.SVersion = (uint)svElm.GetInt64();
            if (root.TryGetProperty("sSpriteType", out var stElm))
                sprite.SSpriteType = (UndertaleSprite.SpriteType)stElm.GetInt32();
            if (root.TryGetProperty("gms2PlaybackSpeed", out var psElm))
                sprite.GMS2PlaybackSpeed = (float)psElm.GetDouble();
            if (root.TryGetProperty("gms2PlaybackSpeedType", out var pstElm))
                sprite.GMS2PlaybackSpeedType = (AnimSpeedType)pstElm.GetInt32();
        }

        // Collision masks
        if (root.TryGetProperty("collisionMasks", out var cmElm) && cmElm.ValueKind == JsonValueKind.Array)
        {
            sprite.CollisionMasks.Clear();
            foreach (var maskElm in cmElm.EnumerateArray())
            {
                var mask = new UndertaleSprite.MaskEntry();
                if (maskElm.TryGetProperty("width", out var mwElm)) mask.Width = (int)mwElm.GetInt64();
                if (maskElm.TryGetProperty("height", out var mhElm)) mask.Height = (int)mhElm.GetInt64();
                if (maskElm.TryGetProperty("data", out var dataElm))
                {
                    string? base64 = dataElm.GetString();
                    if (!string.IsNullOrEmpty(base64))
                        mask.Data = Convert.FromBase64String(base64);
                }
                sprite.CollisionMasks.Add(mask);
            }
        }

        // Nine slice
        if (root.TryGetProperty("nineSlice", out var nsElm) && nsElm.ValueKind == JsonValueKind.Object)
        {
            var ns = new UndertaleSprite.NineSlice();
            sprite.V3NineSlice = ns;
            if (nsElm.TryGetProperty("left", out var nlElm)) ns.Left = nlElm.GetInt32();
            if (nsElm.TryGetProperty("top", out var ntElm)) ns.Top = ntElm.GetInt32();
            if (nsElm.TryGetProperty("right", out var nrElm)) ns.Right = nrElm.GetInt32();
            if (nsElm.TryGetProperty("bottom", out var nbElm)) ns.Bottom = nbElm.GetInt32();
            if (nsElm.TryGetProperty("enabled", out var neElm)) ns.Enabled = neElm.GetBoolean();
            if (nsElm.TryGetProperty("tileModes", out var nmElm) && nmElm.ValueKind == JsonValueKind.Array)
            {
                var modesArray = nmElm.EnumerateArray().ToArray();
                for (int i = 0; i < Math.Min(5, modesArray.Length); i++)
                    ns.TileModes[i] = (UndertaleSprite.NineSlice.TileMode)modesArray[i].GetInt32();
            }
        }
    }

    // =========================================================================
    // Phase 2: Texture Repacking (only for sprites not created via metadata)
    // =========================================================================
    [GeneratedRegex(@"^(.+?)(?:_(\d+))$", RegexOptions.Compiled)]
    private static partial Regex SprFrameRegex();

#pragma warning disable IDE0060
    private static void DoTextureRepack(UndertaleData data, string spritesPath,
#pragma warning restore IDE0060
        List<string> newSpriteFolders,
        Func<string, JsonElement?> loadMeta,
        Func<string, List<Dictionary<string, object>>?> loadFrameData,
        Action<UndertaleTexturePageItem, List<Dictionary<string, object>>, int> applyFrameProps)
    {
        bool bboxMasks = data.IsVersionAtLeast(2024, 6);
        bool noMasksForBasicRectangles = data.IsVersionAtLeast(2022, 9);
        int atlasSize = 2048;
        int lastTextPage = data.EmbeddedTextures.Count - 1;
        int lastTextPageItem = data.TexturePageItems.Count - 1;

        var imagesToCleanup = new List<MagickImage>();
        var maskNodes = new Dictionary<UndertaleSprite, PackerNode>();

        try
        {
            var pngFiles = newSpriteFolders
                .SelectMany(folder => GetFilesIn(folder, "*.png"))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Log($"[ImportSprites] Prepared {pngFiles.Length} PNG files from {newSpriteFolders.Count} sprite folders for repacking");
            ReportProgress(5, 100);

            if (pngFiles.Length == 0)
            {
                Log("[ImportSprites] No PNGs to repack");
                return;
            }

            var packer = new SpritePacker();
            packer.ProcessFiles(
                pngFiles,
                atlasSize,
                2,
                false,
                imagesToCleanup,
                ReportProgress
            );
            ReportProgress(50, 100);

            int newCreated = 0;
            int atlasIndex = 0;
            int atlasTotal = Math.Max(packer.Atlasses.Count, 1);

            foreach (var atlas in packer.Atlasses)
            {
                using MagickImage atlasImage = SpritePacker.CreateAtlasImage(atlas);
                IPixelCollection<byte> atlasPixels = atlasImage.GetPixels();

                var texture = new UndertaleEmbeddedTexture
                {
                    Name = new UndertaleString($"Texture {++lastTextPage}")
                };
                texture.TextureData.Image = GMImage.FromMagickImage(atlasImage).ConvertToPng();
                data.EmbeddedTextures.Add(texture);

                foreach (var n in atlas.Nodes)
                {
                    if (n.Texture == null) continue;

                    string stripped = Path.GetFileNameWithoutExtension(Path.GetFileName(n.Texture.Source));
                    string spriteName;
                    int frame = 0;
                    try
                    {
                        var match = SprFrameRegex().Match(stripped);
                        spriteName = match.Groups[1].Value;
                        if (!int.TryParse(match.Groups[2].Value, out frame))
                            frame = 0;
                    }
                    catch { continue; }

                    var existingSprite = data.Sprites.ByName(spriteName);

                    var tpi = new UndertaleTexturePageItem
                    {
                        Name = new UndertaleString($"PageItem {++lastTextPageItem}"),
                        SourceX = (ushort)n.Bounds.X,
                        SourceY = (ushort)n.Bounds.Y,
                        SourceWidth = (ushort)n.Bounds.Width,
                        SourceHeight = (ushort)n.Bounds.Height,
                        TargetX = (ushort)n.Texture.TargetX,
                        TargetY = (ushort)n.Texture.TargetY,
                        TargetWidth = (ushort)n.Bounds.Width,
                        TargetHeight = (ushort)n.Bounds.Height,
                        BoundingWidth = (ushort)n.Texture.BoundingWidth,
                        BoundingHeight = (ushort)n.Texture.BoundingHeight,
                        TexturePage = texture
                    };
                    data.TexturePageItems.Add(tpi);

                    var texEntry = new UndertaleSprite.TextureEntry { Texture = tpi };
                    var frameData = loadFrameData(spriteName);
                    if (frameData != null)
                        applyFrameProps(tpi, frameData, frame);

                    UndertaleSprite sprite;
                    if (existingSprite == null)
                    {
                        sprite = new UndertaleSprite
                        {
                            Name = data.Strings.MakeString(spriteName),
                            Width = (uint)n.Texture.BoundingWidth,
                            Height = (uint)n.Texture.BoundingHeight,
                            MarginLeft = n.Texture.TargetX,
                            MarginRight = n.Texture.TargetX + n.Bounds.Width - 1,
                            MarginTop = n.Texture.TargetY,
                            MarginBottom = n.Texture.TargetY + n.Bounds.Height - 1,
                            OriginX = 0,
                            OriginY = 0
                        };

                        var meta = loadMeta(spriteName);
                        if (meta.HasValue) ApplySpriteMetadata(data, sprite, meta.Value);

                        for (int i = 0; i < frame; i++)
                            sprite.Textures.Add(new UndertaleSprite.TextureEntry { Texture = null });

                        if (!noMasksForBasicRectangles ||
                            sprite.SepMasks is not (UndertaleSprite.SepMaskType.AxisAlignedRect or UndertaleSprite.SepMaskType.RotatedRect))
                        {
                            if (sprite.CollisionMasks.Count == 0)
                                maskNodes[sprite] = n;
                        }

                        sprite.Textures.Add(texEntry);
                        data.Sprites.Add(sprite);
                        newCreated++;
                    }
                    else
                    {
                        sprite = existingSprite;
                        if (frame >= sprite.Textures.Count)
                        {
                            while (frame >= sprite.Textures.Count)
                                sprite.Textures.Add(new UndertaleSprite.TextureEntry { Texture = null });
                        }

                        var oldTex = sprite.Textures[frame]?.Texture
                            ?? sprite.Textures.FirstOrDefault(te => te != null)?.Texture;
                        if (oldTex != null)
                        {
                            tpi.TargetX = oldTex.TargetX; tpi.TargetY = oldTex.TargetY;
                            tpi.TargetWidth = oldTex.TargetWidth; tpi.TargetHeight = oldTex.TargetHeight;
                            tpi.BoundingWidth = oldTex.BoundingWidth; tpi.BoundingHeight = oldTex.BoundingHeight;
                        }

                        if (frameData != null)
                            applyFrameProps(tpi, frameData, frame);

                        var existMeta = loadMeta(spriteName);
                        if (existMeta.HasValue) ApplySpriteMetadata(data, sprite, existMeta.Value);

                        sprite.Textures[frame] = texEntry;
                    }

                    if (sprite.SepMasks == UndertaleSprite.SepMaskType.Precise && sprite.CollisionMasks.Count == 0)
                        maskNodes[sprite] = n;
                }

                // Generate masks
                foreach (var (maskSpr, maskNode) in maskNodes)
                {
                    try
                    {
                        maskSpr.CollisionMasks.Clear();
                        maskSpr.CollisionMasks.Add(maskSpr.NewMaskEntry(data));
                        (int mw, int mh) = maskSpr.CalculateMaskDimensions(data);
                        if (mw <= 0 || mh <= 0) { maskSpr.CollisionMasks.Clear(); continue; }

                        int stride = ((mw + 7) / 8) * 8;
                        var bits = new BitArray(stride * mh);
                        for (int y = 0; y < mh && y < maskNode.Bounds.Height; y++)
                        {
                            for (int x = 0; x < mw && x < maskNode.Bounds.Width; x++)
                            {
                                var px = atlasPixels.GetPixel(x + maskNode.Bounds.X, y + maskNode.Bounds.Y).ToColor();
                                if (bboxMasks)
                                    bits[(y * stride) + x] = (px!.A > 0);
                                else if (maskNode.Texture != null)
                                {
                                    int idx = ((y + maskNode.Texture.TargetY) * stride) + x + maskNode.Texture.TargetX;
                                    if (idx >= 0 && idx < bits.Length) bits[idx] = (px!.A > 0);
                                }
                            }
                        }

                        var temp = new BitArray(bits.Length);
                        for (int i = 0; i < bits.Length; i += 8)
                            for (int j = 0; j < 8; j++)
                                temp[j + i] = bits[-(j - 7) + i];

                        byte[] bytes = new byte[bits.Length / 8];
                        temp.CopyTo(bytes, 0);

                        if (maskSpr.CollisionMasks[0].Data.Length == bytes.Length)
                            Array.Copy(bytes, maskSpr.CollisionMasks[0].Data, bytes.Length);
                        else
                            maskSpr.CollisionMasks.Clear();
                    }
                    catch { maskSpr.CollisionMasks.Clear(); }
                }
                maskNodes.Clear();
                atlasIndex++;
                ReportProgress(50 + (atlasIndex * 45 / atlasTotal), 100);
            }

            ReportProgress(100, 100);
            Log($"[ImportSprites] Repack done. New from repack: {newCreated}");
        }
        catch (Exception ex) { Log($"[ImportSprites] REPACK ERROR: {ex.Message}"); throw; }
        finally
        {
            foreach (var img in imagesToCleanup) img.Dispose();
        }
    }

    // =========================================================================
    // Texture Packer
    // =========================================================================
    private class PackerTextureInfo
    {
        public string Source = "";
        public int Width, Height, TargetX, TargetY, BoundingWidth, BoundingHeight;
        public MagickImage? Image;
    }

    private enum PackerSplitType { Horizontal, Vertical }
    private enum PackerBestFitHeuristic { Area, MaxOneAxis }

    private struct PackerRect
    {
        public int X, Y, Width, Height;
    }

    private class PackerNode
    {
        public PackerRect Bounds;
        public PackerTextureInfo? Texture;
        public PackerSplitType SplitType;
    }

    private class PackerAtlas
    {
        public int Width, Height;
        public List<PackerNode> Nodes = [];
    }

    private class SpritePacker
    {
        public List<PackerTextureInfo> SourceTextures = [];
        public StringWriter LogWriter = new();
        public StringWriter ErrorWriter = new();
        public int Padding;
        public int AtlasSize;
        public PackerBestFitHeuristic FitHeuristic = PackerBestFitHeuristic.Area;
        public List<PackerAtlas> Atlasses = [];

#pragma warning disable IDE0060
        public void ProcessFiles(
            IReadOnlyList<string> sourceFiles,
            int atlasSize,
            int padding,
            bool debugMode,
            List<MagickImage> cleanup,
            Action<int, int>? progressCallback = null)
#pragma warning restore IDE0060
        {
            Padding = padding;
            AtlasSize = atlasSize;
            ScanForTextures(sourceFiles, cleanup, progressCallback);
            var textures = SourceTextures.ToList();
            Atlasses = [];
            while (textures.Count > 0)
            {
                var atlas = new PackerAtlas { Width = atlasSize, Height = atlasSize };
                var leftovers = LayoutAtlas(textures, atlas);
                if (leftovers.Count == 0)
                {
                    while (leftovers.Count == 0)
                    {
                        atlas.Width /= 2; atlas.Height /= 2;
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

        private void ScanForTextures(
            IReadOnlyList<string> sourceFiles,
            List<MagickImage> cleanup,
            Action<int, int>? progressCallback)
        {
            SourceTextures = new List<PackerTextureInfo>(sourceFiles.Count);
            for (int i = 0; i < sourceFiles.Count; i++)
            {
                string sourceFile = sourceFiles[i];
                var img = GetPatchFileSystem() != null
                    ? new MagickImage(FReadBytes(sourceFile))
                    : new MagickImage(sourceFile);
                int w = (int)img.Width;
                int h = (int)img.Height;
                if (w > AtlasSize || h > AtlasSize)
                {
                    img.Dispose();
                    continue;
                }

                cleanup.Add(img);

                // Exported PNGs are already cropped TPI source regions.
                // Do NOT trim - trimming would compute wrong TargetX/Y offsets.
                // Actual TargetX/Y and BoundingWidth/Height come from sprite metadata.
                var ti = new PackerTextureInfo
                {
                    Source = sourceFile,
                    Width = w,
                    Height = h,
                    BoundingWidth = w,
                    BoundingHeight = h,
                    TargetX = 0,
                    TargetY = 0,
                    Image = img
                };
                SourceTextures.Add(ti);
                progressCallback?.Invoke(i + 1, sourceFiles.Count);
            }
        }

        private List<PackerTextureInfo> LayoutAtlas(List<PackerTextureInfo> textures, PackerAtlas atlas)
        {
            var freeList = new List<PackerNode>();
            var remaining = textures.ToList();
            atlas.Nodes = [];
            var root = new PackerNode
            {
                Bounds = new PackerRect { Width = atlas.Width, Height = atlas.Height },
                SplitType = PackerSplitType.Horizontal
            };
            freeList.Add(root);
            while (freeList.Count > 0 && remaining.Count > 0)
            {
                var node = freeList[0];
                freeList.RemoveAt(0);
                var best = FindBestFit(node, remaining);
                if (best != null)
                {
                    if (node.SplitType == PackerSplitType.Horizontal)
                        HorizontalSplit(node, best.Width, best.Height, freeList);
                    else
                        VerticalSplit(node, best.Width, best.Height, freeList);
                    node.Texture = best;
                    node.Bounds = new PackerRect { X = node.Bounds.X, Y = node.Bounds.Y, Width = best.Width, Height = best.Height };
                    remaining.Remove(best);
                }
                atlas.Nodes.Add(node);
            }
            return remaining;
        }

        private void HorizontalSplit(PackerNode toSplit, int w, int h, List<PackerNode> list)
        {
            var n1 = new PackerNode
            {
                Bounds = new PackerRect { X = toSplit.Bounds.X + w + Padding, Y = toSplit.Bounds.Y, Width = toSplit.Bounds.Width - w - Padding, Height = h },
                SplitType = PackerSplitType.Vertical
            };
            var n2 = new PackerNode
            {
                Bounds = new PackerRect { X = toSplit.Bounds.X, Y = toSplit.Bounds.Y + h + Padding, Width = toSplit.Bounds.Width, Height = toSplit.Bounds.Height - h - Padding },
                SplitType = PackerSplitType.Horizontal
            };
            if (n1.Bounds.Width > 0 && n1.Bounds.Height > 0) list.Add(n1);
            if (n2.Bounds.Width > 0 && n2.Bounds.Height > 0) list.Add(n2);
        }

        private void VerticalSplit(PackerNode toSplit, int w, int h, List<PackerNode> list)
        {
            var n1 = new PackerNode
            {
                Bounds = new PackerRect { X = toSplit.Bounds.X + w + Padding, Y = toSplit.Bounds.Y, Width = toSplit.Bounds.Width - w - Padding, Height = toSplit.Bounds.Height },
                SplitType = PackerSplitType.Vertical
            };
            var n2 = new PackerNode
            {
                Bounds = new PackerRect { X = toSplit.Bounds.X, Y = toSplit.Bounds.Y + h + Padding, Width = w, Height = toSplit.Bounds.Height - h - Padding },
                SplitType = PackerSplitType.Horizontal
            };
            if (n1.Bounds.Width > 0 && n1.Bounds.Height > 0) list.Add(n1);
            if (n2.Bounds.Width > 0 && n2.Bounds.Height > 0) list.Add(n2);
        }

        private static PackerTextureInfo? FindBestFit(PackerNode node, List<PackerTextureInfo> textures)
        {
            PackerTextureInfo? best = null;
            float maxCrit = 0;
            foreach (var ti in textures)
            {
                if (ti.Width > node.Bounds.Width || ti.Height > node.Bounds.Height) continue;
                float wR = (float)ti.Width / node.Bounds.Width;
                float hR = (float)ti.Height / node.Bounds.Height;
                float ratio = wR > hR ? wR : hR;
                if (ratio > maxCrit) { maxCrit = ratio; best = ti; }
            }
            return best;
        }

        internal static MagickImage CreateAtlasImage(PackerAtlas atlas)
        {
            var img = new MagickImage(MagickColors.Transparent, (uint)atlas.Width, (uint)atlas.Height);
            foreach (var n in atlas.Nodes)
            {
                if (n.Texture?.Image is not null)
                {
                    using var resized = TextureWorker.ResizeImage(n.Texture.Image, n.Bounds.Width, n.Bounds.Height);
                    img.Composite(resized, n.Bounds.X, n.Bounds.Y, CompositeOperator.Copy);
                }
            }
            return img;
        }
    }
}
