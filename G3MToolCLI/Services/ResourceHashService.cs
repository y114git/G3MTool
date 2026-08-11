using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Project;
using static G3MToolCLI.Utils.ResourceAssetUtil;

namespace G3MToolCLI.Services;

/// <summary>
/// Computes per-resource hashes directly from in-memory UndertaleData,
/// without writing any files to disk. Used during patch create to compare
/// original vs modified data without full export.
/// </summary>
public static class ResourceHashService
{
    /// <summary>
    /// Hash all resource types. Returns type -> (resourceName -> hashHex).
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> HashAll(UndertaleData data, string? dataFilePath = null)
    {
        Dictionary<string, Dictionary<string, string>> result = [];

        result["GeneralInfo"] = HashGeneralInfo(data);
        result["Options"] = HashOptions(data);
        result["GlobalScripts"] = HashGlobalScripts(data);
        result["Scripts"] = HashScripts(data);
        result["Language"] = HashLanguage(data);
        result["FeatureFlags"] = HashFeatureFlags(data);
        result["Tags"] = HashTags(data);
        result["FilterEffects"] = HashFilterEffects(data);
        result["AudioGroups"] = HashAudioGroups(data);
        result["EmbeddedAudio"] = HashEmbeddedAudio(data);
        result["TextureGroupInfo"] = HashTextureGroupInfo(data);
        result["EmbeddedTextures"] = HashEmbeddedTextures(data);
        result["TexturePageItems"] = HashTexturePageItems(data);
        result["EmbeddedImages"] = HashEmbeddedImages(data);
        result["Sprites"] = HashSprites(data);
        result["Backgrounds"] = HashBackgrounds(data);
        result["Fonts"] = HashFonts(data);
        result["Sounds"] = HashSounds(data, dataFilePath);
        result["Paths"] = HashPaths(data);
        result["Tilesets"] = HashTilesets(data);
        result["Shaders"] = HashShaders(data);
        result["Timelines"] = HashTimelines(data);
        result["GameObjects"] = HashGameObjects(data);
        result["Rooms"] = HashRooms(data);
        result["AnimationCurves"] = HashAnimationCurves(data);
        result["ParticleSystemEmitters"] = HashParticleSystemEmitters(data);
        result["ParticleSystems"] = HashParticleSystems(data);
        result["Sequences"] = HashSequences(data);
        result["CodeEntries"] = HashCodeEntries(data);
        result["Extensions"] = HashExtensions(data);

        return result;
    }

    private static string HexHash(byte[] data) =>
        Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    private static string HexHash(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    private static string JsonHash(Action<Utf8JsonWriter> writeAction)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            writeAction(w);
        return HexHash(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    // ── GeneralInfo ──────────────────────────────────────────────────

    private static Dictionary<string, string> HashEmbeddedAudio(UndertaleData data)
    {
        if (data.EmbeddedAudio == null) return [];
        var result = new ConcurrentDictionary<string, string>();

        Parallel.For(0, data.EmbeddedAudio.Count, i =>
        {
            var audio = data.EmbeddedAudio[i];
            string name = $"audio_{i:D4}";
            var bytes = audio?.Data ?? [];
            result[name] = HexHash(bytes);
        });

        return new Dictionary<string, string>(result, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> HashEmbeddedTextures(UndertaleData data)
    {
        if (data.EmbeddedTextures == null) return [];
        var result = new ConcurrentDictionary<string, string>();

        Parallel.For(0, data.EmbeddedTextures.Count, i =>
        {
            var texture = data.EmbeddedTextures[i];
            if (texture == null)
                return;

            string name = $"texture_{i:D4}";
            string hash = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", texture.Name?.Content ?? "");
                w.WriteNumber("scaled", texture.Scaled);
                w.WriteNumber("generatedMips", texture.GeneratedMips);
                w.WriteString("format", texture.TextureData?.Image?.Format.ToString() ?? "");
                var imageData = texture.TextureData?.Image?.GetData();
                w.WriteString("image", imageData != null ? Convert.ToBase64String(imageData) : "");
                w.WriteEndObject();
            });
            result[name] = hash;
        });

        return new Dictionary<string, string>(result, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> HashGeneralInfo(UndertaleData data)
    {
        if (data.GeneralInfo == null) return [];
        var gi = data.GeneralInfo;
        var hash = JsonHash(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("isDebuggerDisabled", gi.IsDebuggerDisabled);
            w.WriteNumber("bytecodeVersion", gi.BytecodeVersion);
            w.WriteString("fileName", gi.FileName?.Content ?? "");
            w.WriteString("config", gi.Config?.Content ?? "");
            w.WriteNumber("lastObj", gi.LastObj);
            w.WriteNumber("lastTile", gi.LastTile);
            w.WriteNumber("gameID", gi.GameID);
            w.WriteString("name", gi.Name?.Content ?? "");
            w.WriteNumber("major", gi.Major);
            w.WriteNumber("minor", gi.Minor);
            w.WriteNumber("release", gi.Release);
            w.WriteNumber("build", gi.Build);
            w.WriteNumber("defaultWindowWidth", gi.DefaultWindowWidth);
            w.WriteNumber("defaultWindowHeight", gi.DefaultWindowHeight);
            w.WriteNumber("infoFlags", (uint)gi.Info);
            w.WriteNumber("licenseCRC32", gi.LicenseCRC32);
            w.WriteNumber("timestamp", gi.Timestamp);
            w.WriteString("displayName", gi.DisplayName?.Content ?? "");
            w.WriteNumber("activeTargets", gi.ActiveTargets);
            w.WriteNumber("steamAppID", gi.SteamAppID);
            w.WritePropertyName("roomOrder");
            w.WriteStartArray();
            foreach (var r in gi.RoomOrder)
                w.WriteStringValue(r?.Resource?.Name?.Content ?? "");
            w.WriteEndArray();
            if (gi.Major >= 2) w.WriteNumber("gms2FPS", gi.GMS2FPS);
            w.WriteEndObject();
        });
        return new() { ["GeneralInfo"] = hash };
    }

    // ── AudioGroups ──────────────────────────────────────────────────

    private static Dictionary<string, string> HashAudioGroups(UndertaleData data)
    {
        if (data.AudioGroups == null) return [];
        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(data.AudioGroups, ag =>
        {
            if (ag?.Name?.Content == null) return;
            var hash = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", ag.Name.Content);
                if (ag.Path != null) w.WriteString("path", ag.Path.Content ?? "");
                w.WriteEndObject();
            });
            result[ag.Name.Content] = hash;
        });
        return new(result);
    }

    // ── TextureGroupInfo ─────────────────────────────────────────────

    private static Dictionary<string, string> HashTextureGroupInfo(UndertaleData data)
    {
        if (data.TextureGroupInfo == null) return [];
        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(data.TextureGroupInfo, tg =>
        {
            if (tg?.Name?.Content == null) return;
            var hash = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", tg.Name.Content);
                if (data.IsVersionAtLeast(2022, 9))
                {
                    if (tg.Directory != null) w.WriteString("directory", tg.Directory.Content ?? "");
                    if (tg.Extension != null) w.WriteString("extension", tg.Extension.Content ?? "");
                    w.WriteNumber("loadType", (int)tg.LoadType);
                }
                w.WriteStartArray("texturePages");
                if (tg.TexturePages != null)
                    foreach (var r in tg.TexturePages)
                        if (r.Resource?.Name?.Content != null) w.WriteStringValue(r.Resource.Name.Content);
                w.WriteEndArray();
                w.WriteStartArray("sprites");
                if (tg.Sprites != null)
                    foreach (var r in tg.Sprites)
                        if (r.Resource?.Name?.Content != null) w.WriteStringValue(r.Resource.Name.Content);
                w.WriteEndArray();
                w.WriteStartArray("fonts");
                if (tg.Fonts != null)
                    foreach (var r in tg.Fonts)
                        if (r.Resource?.Name?.Content != null) w.WriteStringValue(r.Resource.Name.Content);
                w.WriteEndArray();
                w.WriteStartArray("tilesets");
                if (tg.Tilesets != null)
                    foreach (var r in tg.Tilesets)
                        if (r.Resource?.Name?.Content != null) w.WriteStringValue(r.Resource.Name.Content);
                w.WriteEndArray();
                w.WriteEndObject();
            });
            result[tg.Name.Content] = hash;
        });
        return new(result);
    }

    private static Dictionary<string, string> HashFeatureFlags(UndertaleData data)
    {
        if (data.FeatureFlags?.List == null) return [];

        var hash = JsonHash(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("flags");
            foreach (var flag in data.FeatureFlags.List.Select(f => f?.Content ?? "").OrderBy(f => f, StringComparer.Ordinal))
                w.WriteStringValue(flag);
            w.WriteEndArray();
            w.WriteEndObject();
        });

        return new Dictionary<string, string> { ["FeatureFlags"] = hash };
    }

    private static Dictionary<string, string> HashLanguage(UndertaleData data)
    {
        if (data.Language == null) return [];

        var hash = JsonHash(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("unknown1", data.Language.Unknown1);
            w.WriteStartArray("entryIds");
            foreach (var entryId in data.Language.EntryIDs ?? [])
                w.WriteStringValue(entryId?.Content ?? "");
            w.WriteEndArray();
            w.WriteStartArray("languages");
            foreach (var language in data.Language.Languages ?? [])
            {
                if (language == null) continue;
                w.WriteStartObject();
                w.WriteString("name", language.Name?.Content ?? "");
                w.WriteString("region", language.Region?.Content ?? "");
                w.WriteStartArray("entries");
                foreach (var entry in language.Entries ?? [])
                    w.WriteStringValue(entry?.Content ?? "");
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });

        return new Dictionary<string, string> { ["Language"] = hash };
    }

    private static Dictionary<string, string> HashTags(UndertaleData data)
    {
        if (data.Tags == null) return [];

        var hash = JsonHash(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("tags");
            if (data.Tags.Tags != null)
            {
                foreach (var tag in data.Tags.Tags.Select(t => t?.Content ?? "").OrderBy(t => t, StringComparer.Ordinal))
                    w.WriteStringValue(tag);
            }
            w.WriteEndArray();

            w.WriteStartArray("assetTags");
            if (data.Tags.AssetTags != null)
            {
                foreach (var (AssetId, Tags, Decoded) in data.Tags.AssetTags
                    .Select(kvp => (AssetId: kvp.Key, Tags: kvp.Value, Decoded: DecodeAssetTagId(data, kvp.Key)))
                    .OrderBy(e => e.Decoded.Type, StringComparer.Ordinal)
                    .ThenBy(e => e.Decoded.Name, StringComparer.Ordinal)
                    .ThenBy(e => e.AssetId))
                {
                    w.WriteStartObject();
                    w.WriteString("assetType", Decoded.Type);
                    w.WriteString("assetName", Decoded.Name);
                    if (string.IsNullOrEmpty(Decoded.Name))
                        w.WriteNumber("assetId", AssetId);
                    w.WriteStartArray("tags");
                    foreach (var tag in (Tags ?? []).Select(t => t?.Content ?? "").OrderBy(t => t, StringComparer.Ordinal))
                        w.WriteStringValue(tag);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });

        return new Dictionary<string, string> { ["Tags"] = hash };
    }

    private static Dictionary<string, string> HashFilterEffects(UndertaleData data)
    {
        if (data.FilterEffects == null) return [];

        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(data.FilterEffects, effect =>
        {
            if (effect?.Name?.Content == null) return;
            var hash = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", effect.Name.Content);
                w.WriteString("value", effect.Value?.Content ?? "");
                w.WriteEndObject();
            });
            result[effect.Name.Content] = hash;
        });
        return new(result);
    }

    private static (string Type, string Name) DecodeAssetTagId(UndertaleData data, int assetId)
    {
        var type = (ResourceType)(assetId >> 24);
        int rawIndex = assetId & 0xFFFFFF;
        int index = type == ResourceType.Script ? rawIndex - 100000 : rawIndex;
        return (type.ToString(), GetAssetNameByTagType(data, type, index) ?? "");
    }

    private static Dictionary<string, string> HashTexturePageItems(UndertaleData data)
    {
        if (data.TexturePageItems == null) return [];

        var embeddedTextureIndices = new Dictionary<UndertaleEmbeddedTexture, int>();
        if (data.EmbeddedTextures != null)
            for (int i = 0; i < data.EmbeddedTextures.Count; i++)
                embeddedTextureIndices[data.EmbeddedTextures[i]] = i;

        var hash = JsonHash(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("items");
            for (int i = 0; i < data.TexturePageItems.Count; i++)
            {
                var tpi = data.TexturePageItems[i];
                if (tpi == null)
                {
                    w.WriteNullValue();
                    continue;
                }

                w.WriteStartObject();
                w.WriteNumber("index", i);
                w.WriteString("name", tpi.Name?.Content ?? "");
                w.WriteNumber("texturePageIndex", tpi.TexturePage != null && embeddedTextureIndices.TryGetValue(tpi.TexturePage, out var texIdx) ? texIdx : -1);
                w.WriteNumber("sourceX", tpi.SourceX);
                w.WriteNumber("sourceY", tpi.SourceY);
                w.WriteNumber("sourceWidth", tpi.SourceWidth);
                w.WriteNumber("sourceHeight", tpi.SourceHeight);
                w.WriteNumber("targetX", tpi.TargetX);
                w.WriteNumber("targetY", tpi.TargetY);
                w.WriteNumber("targetWidth", tpi.TargetWidth);
                w.WriteNumber("targetHeight", tpi.TargetHeight);
                w.WriteNumber("boundingWidth", tpi.BoundingWidth);
                w.WriteNumber("boundingHeight", tpi.BoundingHeight);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });

        return new Dictionary<string, string> { ["TexturePageItems"] = hash };
    }

    // ── Sprites ──────────────────────────────────────────────────────

    private static Dictionary<string, string> HashSprites(UndertaleData data)
    {
        if (data.Sprites == null) return [];

        // Texture page index lookup
        var pageIndexMap = new Dictionary<UndertaleEmbeddedTexture, int>();
        if (data.EmbeddedTextures != null)
            for (int i = 0; i < data.EmbeddedTextures.Count; i++)
                pageIndexMap[data.EmbeddedTextures[i]] = i;

        // Collect unique source regions per texture page
        var regionsPerPage = new Dictionary<int, HashSet<(ushort srcX, ushort srcY, ushort srcW, ushort srcH)>>();
        foreach (var sprite in data.Sprites)
        {
            if (sprite?.Textures == null) continue;
            foreach (var texEntry in sprite.Textures)
            {
                var tpi = texEntry?.Texture;
                if (tpi?.TexturePage == null || !pageIndexMap.TryGetValue(tpi.TexturePage, out int pageIdx)) continue;
                if (!regionsPerPage.TryGetValue(pageIdx, out var regions))
                {
                    regions = [];
                    regionsPerPage[pageIdx] = regions;
                }
                regions.Add((tpi.SourceX, tpi.SourceY, tpi.SourceWidth, tpi.SourceHeight));
            }
        }

        // Decode texture pages and hash each source rectangle independently.
        var frameHashes = new ConcurrentDictionary<(int, ushort, ushort, ushort, ushort), string>();
        Parallel.ForEach(regionsPerPage, new ParallelOptions { MaxDegreeOfParallelism = 4 }, kvp =>
        {
            var (pageIdx, regions) = kvp;
            var tex = data.EmbeddedTextures![pageIdx];
            if (tex?.TextureData?.Image == null)
            {
                foreach (var (srcX, srcY, srcW, srcH) in regions)
                    frameHashes[(pageIdx, srcX, srcY, srcW, srcH)] = "";
                return;
            }

            using var image = tex.TextureData.Image.GetMagickImage();
            int pageW = (int)image.Width;
            int pageH = (int)image.Height;
            using var pixels = image.GetPixels();
            byte[]? allPixels = pixels.ToByteArray("RGBA");
            if (allPixels == null)
            {
                foreach (var (srcX, srcY, srcW, srcH) in regions)
                    frameHashes[(pageIdx, srcX, srcY, srcW, srcH)] = "";
                return;
            }
            foreach (var (srcX, srcY, srcW, srcH) in regions)
            {
                if (!TryHashTextureRegionPixels(
                        allPixels,
                        pageIdx,
                        pageW,
                        pageH,
                        srcX,
                        srcY,
                        srcW,
                        srcH,
                        out var regionHash))
                {
                    frameHashes[(pageIdx, srcX, srcY, srcW, srcH)] = regionHash;
                }
                else
                {
                    frameHashes[(pageIdx, srcX, srcY, srcW, srcH)] = regionHash;
                }
            }
        });

        // Hash sprites (metadata + per-frame pixel content)
        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(data.Sprites, sprite =>
        {
            if (sprite?.Name?.Content == null) return;
            var hash = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", sprite.Name.Content);
                w.WriteNumber("width", sprite.Width);
                w.WriteNumber("height", sprite.Height);
                w.WriteBoolean("transparent", sprite.Transparent);
                w.WriteBoolean("smooth", sprite.Smooth);
                w.WriteBoolean("preload", sprite.Preload);
                w.WriteNumber("marginLeft", sprite.MarginLeft);
                w.WriteNumber("marginRight", sprite.MarginRight);
                w.WriteNumber("marginTop", sprite.MarginTop);
                w.WriteNumber("marginBottom", sprite.MarginBottom);
                w.WriteNumber("originX", sprite.OriginX);
                w.WriteNumber("originY", sprite.OriginY);
                w.WriteNumber("sepMasks", (int)sprite.SepMasks);
                w.WriteNumber("bboxMode", sprite.BBoxMode);
                w.WriteNumber("specialOrGMS2PlaybackSpeed", sprite.GMS2PlaybackSpeed);
                w.WriteNumber("specialOrGMS2PlaybackSpeedType", (int)sprite.GMS2PlaybackSpeedType);
                w.WriteBoolean("isSpecialType", sprite.IsSpecialType);
                w.WriteNumber("spineVersion", sprite.SpineVersion);
                if (sprite.SpineJSON != null) w.WriteString("spineJSON", sprite.SpineJSON);
                if (sprite.SpineAtlas != null) w.WriteString("spineAtlas", sprite.SpineAtlas);
                w.WriteNumber("sWFVersion", sprite.SWFVersion);
                if (sprite.V3NineSlice != null)
                {
                    w.WriteStartObject("nineSlice");
                    w.WriteNumber("left", sprite.V3NineSlice.Left);
                    w.WriteNumber("top", sprite.V3NineSlice.Top);
                    w.WriteNumber("right", sprite.V3NineSlice.Right);
                    w.WriteNumber("bottom", sprite.V3NineSlice.Bottom);
                    w.WriteBoolean("enabled", sprite.V3NineSlice.Enabled);
                    w.WriteStartArray("tileModes");
                    foreach (var mode in sprite.V3NineSlice.TileModes)
                        w.WriteNumberValue((int)mode);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                else
                {
                    w.WriteNull("nineSlice");
                }

                // Hash texture page item references + per-frame pixel hash
                w.WriteStartArray("textures");
                if (sprite.Textures != null)
                {
                    foreach (var texEntry in sprite.Textures)
                    {
                        var tpi = texEntry?.Texture;
                        if (tpi != null)
                        {
                            w.WriteStartObject();
                            w.WriteNumber("srcX", tpi.SourceX);
                            w.WriteNumber("srcY", tpi.SourceY);
                            w.WriteNumber("srcW", tpi.SourceWidth);
                            w.WriteNumber("srcH", tpi.SourceHeight);
                            w.WriteNumber("tgtX", tpi.TargetX);
                            w.WriteNumber("tgtY", tpi.TargetY);
                            w.WriteNumber("tgtW", tpi.TargetWidth);
                            w.WriteNumber("tgtH", tpi.TargetHeight);
                            w.WriteNumber("bndX", tpi.BoundingWidth);
                            w.WriteNumber("bndY", tpi.BoundingHeight);
                            int pageIdx = tpi.TexturePage != null && pageIndexMap.TryGetValue(tpi.TexturePage, out var pi)
                                ? pi : -1;
                            w.WriteNumber("texPage", pageIdx);
                            // Per-frame pixel hash (only this sprite's source rectangle, not the whole page)
                            string? fh = "";
                            if (pageIdx >= 0)
                                frameHashes.TryGetValue((pageIdx, tpi.SourceX, tpi.SourceY, tpi.SourceWidth, tpi.SourceHeight), out fh);
                            w.WriteString("frameHash", fh ?? "");
                            w.WriteEndObject();
                        }
                        else w.WriteNullValue();
                    }
                }
                w.WriteEndArray();

                // Collision masks
                if (sprite.CollisionMasks != null)
                {
                    w.WriteNumber("maskCount", sprite.CollisionMasks.Count);
                    w.WriteStartArray("masks");
                    foreach (var mask in sprite.CollisionMasks)
                    {
                        if (mask == null)
                        {
                            w.WriteNullValue();
                            continue;
                        }

                        w.WriteStartObject();
                        w.WriteNumber("width", mask.Width);
                        w.WriteNumber("height", mask.Height);
                        w.WriteNumber("length", mask.Data?.Length ?? 0);
                        w.WriteString("dataHash", mask.Data != null ? HexHash(mask.Data) : "");
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                w.WriteEndObject();
            });
            result[sprite.Name.Content] = hash;
        });
        return new(result);
    }

    private static string HashTextureRegion(UndertaleTexturePageItem? tpi)
    {
        if (tpi?.TexturePage?.TextureData?.Image == null) return "";

        using var image = tpi.TexturePage.TextureData.Image.GetMagickImage();
        int pageW = (int)image.Width;
        int pageH = (int)image.Height;
        if (tpi.SourceWidth == 0 || tpi.SourceHeight == 0 ||
            tpi.SourceX >= pageW || tpi.SourceY >= pageH ||
            tpi.SourceX + tpi.SourceWidth > pageW || tpi.SourceY + tpi.SourceHeight > pageH)
        {
            return $"invalid:{tpi.SourceX}:{tpi.SourceY}:{tpi.SourceWidth}:{tpi.SourceHeight}:{pageW}:{pageH}";
        }

        using var pixels = image.GetPixels();
        byte[]? allPixels = pixels.ToByteArray("RGBA");
        if (allPixels == null) return "";

        TryHashTextureRegionPixels(
            allPixels,
            -1,
            pageW,
            pageH,
            tpi.SourceX,
            tpi.SourceY,
            tpi.SourceWidth,
            tpi.SourceHeight,
            out var regionHash);
        return regionHash;
    }

    private static bool TryHashTextureRegionPixels(
        byte[] allPixels,
        int pageIdx,
        int pageW,
        int pageH,
        int srcX,
        int srcY,
        int srcW,
        int srcH,
        out string hash)
    {
        const int bpp = 4;
        string invalidPrefix = pageIdx >= 0 ? $"invalid:{pageIdx}" : "invalid";

        if (srcW <= 0 || srcH <= 0 ||
            srcX < 0 || srcY < 0 ||
            srcX >= pageW || srcY >= pageH ||
            srcX > pageW - srcW || srcY > pageH - srcH)
        {
            hash = $"{invalidPrefix}:{srcX}:{srcY}:{srcW}:{srcH}:{pageW}:{pageH}:rect";
            return false;
        }

        long expectedPixelsLength = (long)pageW * pageH * bpp;
        if (allPixels.LongLength < expectedPixelsLength)
        {
            hash = $"{invalidPrefix}:{srcX}:{srcY}:{srcW}:{srcH}:{pageW}:{pageH}:buffer:{allPixels.LongLength}:{expectedPixelsLength}";
            return false;
        }

        long regionSizeLong = (long)srcW * srcH * bpp;
        if (regionSizeLong > int.MaxValue)
        {
            hash = $"{invalidPrefix}:{srcX}:{srcY}:{srcW}:{srcH}:{pageW}:{pageH}:region-too-large";
            return false;
        }

        long rowBytesLong = (long)srcW * bpp;
        if (rowBytesLong > int.MaxValue)
        {
            hash = $"{invalidPrefix}:{srcX}:{srcY}:{srcW}:{srcH}:{pageW}:{pageH}:row-too-large";
            return false;
        }

        int rowBytes = (int)rowBytesLong;
        var regionBytes = new byte[(int)regionSizeLong];
        int dst = 0;
        for (int y = srcY; y < srcY + srcH; y++)
        {
            long sourceOffsetLong = ((long)y * pageW + srcX) * bpp;
            if (sourceOffsetLong < 0 || sourceOffsetLong > allPixels.LongLength - rowBytes)
            {
                hash = $"{invalidPrefix}:{srcX}:{srcY}:{srcW}:{srcH}:{pageW}:{pageH}:offset:{sourceOffsetLong}:{allPixels.LongLength}:{rowBytes}";
                return false;
            }

            Buffer.BlockCopy(allPixels, (int)sourceOffsetLong, regionBytes, dst, rowBytes);
            dst += rowBytes;
        }

        hash = HexHash(regionBytes);
        return true;
    }

    // ── Backgrounds ──────────────────────────────────────────────────

    private static Dictionary<string, string> HashBackgrounds(UndertaleData data)
    {
        if (data.Backgrounds == null) return [];
        var pageIndexMap = new Dictionary<UndertaleEmbeddedTexture, int>();
        if (data.EmbeddedTextures != null)
            for (int i = 0; i < data.EmbeddedTextures.Count; i++)
                pageIndexMap[data.EmbeddedTextures[i]] = i;
        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(data.Backgrounds, bg =>
        {
            if (bg?.Name?.Content == null) return;
            var hash = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", bg.Name.Content);
                w.WriteBoolean("transparent", bg.Transparent);
                w.WriteBoolean("smooth", bg.Smooth);
                w.WriteBoolean("preload", bg.Preload);
                w.WriteNumber("gmsWidth", bg.GMS2OutputBorderX);
                w.WriteNumber("gmsHeight", bg.GMS2OutputBorderY);
                w.WriteNumber("tileWidth", bg.GMS2TileWidth);
                w.WriteNumber("tileHeight", bg.GMS2TileHeight);
                w.WriteNumber("tileColumns", bg.GMS2TileColumns);
                w.WriteNumber("tileCount", bg.GMS2TileCount);
                w.WriteNumber("frameLength", bg.GMS2FrameLength);
                var tpi = bg.Texture;
                if (tpi != null)
                {
                    w.WriteNumber("texSrcX", tpi.SourceX);
                    w.WriteNumber("texSrcY", tpi.SourceY);
                    w.WriteNumber("texSrcW", tpi.SourceWidth);
                    w.WriteNumber("texSrcH", tpi.SourceHeight);
                    w.WriteNumber("texPage", tpi.TexturePage != null && pageIndexMap.TryGetValue(tpi.TexturePage, out var bgPi)
                        ? bgPi : -1);
                }
                w.WriteEndObject();
            });
            result[bg.Name.Content] = hash;
        });
        return new(result);
    }

    // ── Fonts ────────────────────────────────────────────────────────

    private static Dictionary<string, string> HashFonts(UndertaleData data)
    {
        if (data.Fonts == null) return [];
        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(data.Fonts, font =>
        {
            if (font?.Name?.Content == null) return;
            var hash = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", font.Name.Content);
                w.WriteString("displayName", font.DisplayName?.Content ?? "");
                w.WriteBoolean("emSizeIsFloat", font.EmSizeIsFloat);
                w.WriteNumber("emSize", font.EmSize);
                w.WriteBoolean("bold", font.Bold);
                w.WriteBoolean("italic", font.Italic);
                w.WriteNumber("charset", font.Charset);
                w.WriteNumber("antiAliasing", font.AntiAliasing);
                w.WriteNumber("rangeStart", font.RangeStart);
                w.WriteNumber("rangeEnd", font.RangeEnd);
                w.WriteNumber("scaleX", font.ScaleX);
                w.WriteNumber("scaleY", font.ScaleY);
                w.WriteNumber("ascenderOffset", font.AscenderOffset);
                w.WriteNumber("ascender", font.Ascender);
                w.WriteNumber("sdfSpread", font.SDFSpread);
                w.WriteNumber("lineHeight", font.LineHeight);
                w.WriteNumber("glyphCount", font.Glyphs?.Count ?? 0);
                var tpi = font.Texture;
                if (tpi != null)
                {
                    w.WriteNumber("texSrcX", tpi.SourceX);
                    w.WriteNumber("texSrcY", tpi.SourceY);
                    w.WriteNumber("texSrcW", tpi.SourceWidth);
                    w.WriteNumber("texSrcH", tpi.SourceHeight);
                    w.WriteString("textureHash", HashTextureRegion(tpi));
                }
                w.WriteStartArray("glyphs");
                if (font.Glyphs != null)
                {
                    foreach (var glyph in font.Glyphs.OrderBy(g => g.Character))
                    {
                        if (glyph == null)
                        {
                            w.WriteNullValue();
                            continue;
                        }

                        w.WriteStartObject();
                        w.WriteNumber("character", glyph.Character);
                        w.WriteNumber("sourceX", glyph.SourceX);
                        w.WriteNumber("sourceY", glyph.SourceY);
                        w.WriteNumber("sourceWidth", glyph.SourceWidth);
                        w.WriteNumber("sourceHeight", glyph.SourceHeight);
                        w.WriteNumber("shift", glyph.Shift);
                        w.WriteNumber("offset", glyph.Offset);
                        w.WriteNumber("unknownAlwaysZero", glyph.UnknownAlwaysZero);
                        w.WriteStartArray("kerning");
                        if (glyph.Kerning != null)
                        {
                            foreach (var kern in glyph.Kerning.OrderBy(k => k.Character))
                            {
                                w.WriteStartObject();
                                w.WriteNumber("character", kern.Character);
                                w.WriteNumber("shiftModifier", kern.ShiftModifier);
                                w.WriteEndObject();
                            }
                        }
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
            result[font.Name.Content] = hash;
        });
        return new(result);
    }

    // ── Sounds ───────────────────────────────────────────────────────

    private static Dictionary<string, string> HashSounds(UndertaleData data, string? dataFilePath = null)
    {
        if (data.Sounds == null) return [];
        var externalAudioHashes = LoadExternalAudioHashes(data, dataFilePath);
        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(data.Sounds, snd =>
        {
            if (snd?.Name?.Content == null) return;
            var hash = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", snd.Name.Content);
                w.WriteNumber("flags", (uint)snd.Flags);
                w.WriteString("type", snd.Type?.Content ?? "");
                w.WriteString("file", snd.File?.Content ?? "");
                w.WriteNumber("effects", snd.Effects);
                w.WriteNumber("volume", snd.Volume);
                w.WriteBoolean("preload", snd.Preload);
                w.WriteNumber("pitch", snd.Pitch);
                w.WriteString("audioGroup", snd.AudioGroup?.Name?.Content ?? "");
                w.WriteNumber("audioID", snd.AudioID);
                w.WriteNumber("groupID", snd.GroupID);
                w.WriteNumber("audioLength", snd.AudioLength);
                w.WriteString("audioHash", GetSoundAudioHash(snd, externalAudioHashes));
                w.WriteEndObject();
            });
            result[snd.Name.Content] = hash;
        });
        return new(result);
    }

    private static string GetSoundAudioHash(UndertaleSound sound, Dictionary<int, Dictionary<int, string>> externalAudioHashes)
    {
        if (sound.AudioFile?.Data != null)
            return HexHash(sound.AudioFile.Data);

        if (sound.GroupID > 0 &&
            sound.AudioID >= 0 &&
            externalAudioHashes.TryGetValue(sound.GroupID, out var groupHashes) &&
            groupHashes.TryGetValue(sound.AudioID, out var hash))
        {
            return hash;
        }

        return "";
    }

    private static Dictionary<int, Dictionary<int, string>> LoadExternalAudioHashes(UndertaleData data, string? dataFilePath)
    {
        if (data.Sounds == null || string.IsNullOrWhiteSpace(dataFilePath))
            return [];

        var dataDir = Path.GetDirectoryName(dataFilePath);
        if (string.IsNullOrWhiteSpace(dataDir))
            return [];

        var groupIds = data.Sounds
            .Where(sound => sound != null && sound.GroupID > 0 && sound.AudioID >= 0)
            .Select(sound => sound.GroupID)
            .Distinct()
            .ToArray();

        var result = new Dictionary<int, Dictionary<int, string>>();
        foreach (int groupId in groupIds)
        {
            string? audioGroupPath = ResolveAudioGroupPath(data, dataDir, groupId);
            if (audioGroupPath == null)
                continue;
            if (!File.Exists(audioGroupPath))
                continue;

            try
            {
                using var stream = new FileStream(audioGroupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var audioGroupData = UndertaleIO.Read(stream);
                if (audioGroupData.EmbeddedAudio == null)
                    continue;

                var hashes = new Dictionary<int, string>();
                for (int i = 0; i < audioGroupData.EmbeddedAudio.Count; i++)
                {
                    var audio = audioGroupData.EmbeddedAudio[i];
                    if (audio?.Data != null)
                        hashes[i] = HexHash(audio.Data);
                }

                if (hashes.Count > 0)
                    result[groupId] = hashes;
            }
            catch
            {
                // Missing or incompatible external audio should not make hashing fail.
            }
        }

        return result;
    }

    private static Dictionary<string, string> HashOptions(UndertaleData data)
    {
        if (data.Options == null) return [];
        return new Dictionary<string, string>
        {
            ["Options"] = JsonHash(w =>
            {
                WriteOptionsJson(w, data);
            })
        };
    }

    private static Dictionary<string, string> HashGlobalScripts(UndertaleData data)
    {
        return new Dictionary<string, string>
        {
            ["GlobalScripts"] = JsonHash(w =>
            {
                WriteGlobalScriptsJson(w, data);
            })
        };
    }

    private static Dictionary<string, string> HashScripts(UndertaleData data)
    {
        if (data.Scripts == null) return [];
        var scripts = data.Scripts
            .Where(script => script?.Name?.Content != null)
            .Select(script => script!)
            .ToList();
        var keyedScripts = BuildUniqueResourceKeys(scripts, script => script.Name!.Content);

        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(keyedScripts, entry =>
        {
            var script = entry.Item;
            result[entry.Key] = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", script.Name.Content);
                w.WriteString("code", script.Code?.Name?.Content ?? "");
                w.WriteBoolean("isConstructor", script.IsConstructor);
                w.WriteEndObject();
            });
        });
        return new(result);
    }

    private static List<(T Item, string Key)> BuildUniqueResourceKeys<T>(
        List<T> items,
        Func<T, string> getName)
    {
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(T Item, string Key)>(items.Count);

        foreach (var item in items)
        {
            var baseName = getName(item);
            if (!used.TryGetValue(baseName, out var count))
            {
                used[baseName] = 1;
                result.Add((item, baseName));
                continue;
            }

            string uniqueName;
            do
            {
                count++;
                uniqueName = $"{baseName}__{count}";
            }
            while (used.ContainsKey(uniqueName));

            used[baseName] = count;
            used[uniqueName] = 1;
            result.Add((item, uniqueName));
        }

        return result;
    }

    private static Dictionary<string, string> HashEmbeddedImages(UndertaleData data)
    {
        if (data.EmbeddedImages == null) return [];
        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(data.EmbeddedImages, image =>
        {
            if (image?.Name?.Content == null) return;
            result[image.Name.Content] = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", image.Name.Content);
                WriteTexturePageItemRef(w, data, image.TextureEntry);
                w.WriteEndObject();
            });
        });
        return new(result);
    }

    private static void WriteTexturePageItemRef(Utf8JsonWriter w, UndertaleData data, UndertaleTexturePageItem? tpi)
    {
        w.WriteNumber("texturePageItemIndex", tpi != null && data.TexturePageItems != null ? data.TexturePageItems.IndexOf(tpi) : -1);
        if (tpi == null)
            return;

        w.WriteString("texturePageItemName", tpi.Name?.Content ?? "");
        w.WriteNumber("sourceX", tpi.SourceX);
        w.WriteNumber("sourceY", tpi.SourceY);
        w.WriteNumber("sourceWidth", tpi.SourceWidth);
        w.WriteNumber("sourceHeight", tpi.SourceHeight);
        w.WriteNumber("targetX", tpi.TargetX);
        w.WriteNumber("targetY", tpi.TargetY);
        w.WriteNumber("targetWidth", tpi.TargetWidth);
        w.WriteNumber("targetHeight", tpi.TargetHeight);
        w.WriteNumber("boundingWidth", tpi.BoundingWidth);
        w.WriteNumber("boundingHeight", tpi.BoundingHeight);
    }

    private static void WriteOptionsJson(Utf8JsonWriter w, UndertaleData data)
    {
        var options = data.Options;
        w.WriteStartObject();
        if (options == null)
        {
            w.WriteEndObject();
            return;
        }

        w.WriteBoolean("newFormat", options.NewFormat);
        w.WriteNumber("shaderExtensionFlag", options.ShaderExtensionFlag);
        w.WriteNumber("shaderExtensionVersion", options.ShaderExtensionVersion);
        w.WriteNumber("info", (ulong)options.Info);
        w.WriteNumber("scale", options.Scale);
        w.WriteNumber("windowColor", options.WindowColor);
        w.WriteNumber("colorDepth", options.ColorDepth);
        w.WriteNumber("resolution", options.Resolution);
        w.WriteNumber("frequency", options.Frequency);
        w.WriteNumber("vertexSync", options.VertexSync);
        w.WriteNumber("priority", options.Priority);
        w.WriteNumber("loadAlpha", options.LoadAlpha);
        w.WriteStartArray("constants");
        if (options.Constants != null)
        {
            foreach (var constant in options.Constants)
            {
                w.WriteStartObject();
                w.WriteString("name", constant?.Name?.Content ?? "");
                w.WriteString("value", constant?.Value?.Content ?? "");
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteGlobalScriptsJson(Utf8JsonWriter w, UndertaleData data)
    {
        w.WriteStartObject();
        WriteCodeRefArray(w, "globalInitScripts", data.GlobalInitScripts);
        WriteCodeRefArray(w, "gameEndScripts", data.GameEndScripts);
        w.WriteEndObject();
    }

    private static void WriteCodeRefArray(Utf8JsonWriter w, string propertyName, IList<UndertaleGlobalInit>? scripts)
    {
        w.WriteStartArray(propertyName);
        if (scripts != null)
        {
            foreach (var script in scripts)
                w.WriteStringValue(script?.Code?.Name?.Content ?? "");
        }
        w.WriteEndArray();
    }

    // ── AnimationCurves ─────────────────────────────────────────────

    private static Dictionary<string, string> HashAnimationCurves(UndertaleData data)
    {
        if (data.AnimationCurves == null) return [];

        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(data.AnimationCurves, curve =>
        {
            if (curve?.Name?.Content == null) return;
            var hash = JsonHash(w =>
            {
                w.WriteStartObject();
                WriteAnimationCurveJson(w, data, curve);
                w.WriteEndObject();
            });
            result[curve.Name.Content] = hash;
        });
        return new(result);
    }

    private static Dictionary<string, string> HashSequences(UndertaleData data)
    {
        if (data.Sequences == null) return [];

        var result = new ConcurrentDictionary<string, string>();
        string tempRoot = Path.Combine(Path.GetTempPath(), "g3mtool_seq_hash", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var context = CreateProjectContext(data, tempRoot);
            foreach (var sequence in data.Sequences)
            {
                if (sequence?.Name?.Content == null) continue;
                string file = Path.Combine(tempRoot, SafeFileName(sequence.Name.Content) + ".json");
                SerializableProjectAssetBridge.Export(context, sequence, file);
                result[sequence.Name.Content] = HexHash(File.ReadAllBytes(file));
            }
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
            try
            {
                string? parentDir = Path.GetDirectoryName(tempRoot);
                if (!string.IsNullOrWhiteSpace(parentDir) &&
                    Directory.Exists(parentDir) &&
                    !Directory.EnumerateFileSystemEntries(parentDir).Any())
                {
                    Directory.Delete(parentDir);
                }
            }
            catch { }
        }

        return new(result);
    }

    private static Dictionary<string, string> HashParticleSystems(UndertaleData data)
    {
        if (data.ParticleSystems == null) return [];
        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(data.ParticleSystems, ps =>
        {
            if (ps?.Name?.Content == null) return;
            result[ps.Name.Content] = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", ps.Name.Content);
                w.WriteNumber("originX", ps.OriginX);
                w.WriteNumber("originY", ps.OriginY);
                w.WriteNumber("drawOrder", (int)ps.DrawOrder);
                w.WriteBoolean("globalSpaceParticles", ps.GlobalSpaceParticles);
                w.WriteStartArray("emitters");
                foreach (var e in ps.Emitters ?? [])
                    w.WriteStringValue(e?.Resource?.Name?.Content ?? "");
                w.WriteEndArray();
                w.WriteEndObject();
            });
        });
        return new(result);
    }

    private static Dictionary<string, string> HashParticleSystemEmitters(UndertaleData data)
    {
        if (data.ParticleSystemEmitters == null) return [];
        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(data.ParticleSystemEmitters, e =>
        {
            if (e?.Name?.Content == null) return;
            result[e.Name.Content] = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", e.Name.Content);
                w.WriteString("sprite", e.Sprite?.Name?.Content ?? "");
                w.WriteString("spawnOnDeath", e.SpawnOnDeath?.Name?.Content ?? "");
                w.WriteString("spawnOnUpdate", e.SpawnOnUpdate?.Name?.Content ?? "");
                foreach (var prop in typeof(UndertaleParticleSystemEmitter).GetProperties().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!prop.CanRead || !prop.CanWrite) continue;
                    if (prop.Name is "Name" or "Sprite" or "SpawnOnDeath" or "SpawnOnUpdate" or
                        "SizeMin" or "SizeMax" or "SizeIncrease" or "SizeWiggle")
                        continue;
                    var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                    if (type.IsEnum)
                        w.WriteNumber(prop.Name, Convert.ToInt32(prop.GetValue(e)));
                    else if (type == typeof(bool))
                        w.WriteBoolean(prop.Name, (bool)(prop.GetValue(e) ?? false));
                    else if (type == typeof(int))
                        w.WriteNumber(prop.Name, (int)(prop.GetValue(e) ?? 0));
                    else if (type == typeof(uint))
                        w.WriteNumber(prop.Name, (uint)(prop.GetValue(e) ?? 0u));
                    else if (type == typeof(float))
                        w.WriteNumber(prop.Name, (float)(prop.GetValue(e) ?? 0f));
                }
                w.WriteEndObject();
            });
        });
        return new(result);
    }

    private static string SafeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "unnamed" : name;
    }

    private static void WriteAnimationCurveJson(Utf8JsonWriter w, UndertaleData data, UndertaleAnimationCurve curve)
    {
        w.WriteString("name", curve.Name?.Content ?? "");
        w.WriteNumber("graphType", (uint)curve.GraphType);
        w.WriteStartArray("channels");
        if (curve.Channels != null)
        {
            foreach (var channel in curve.Channels)
            {
                if (channel == null)
                    continue;

                w.WriteStartObject();
                w.WriteString("name", channel.Name?.Content ?? "");
                w.WriteNumber("curve", (uint)channel.Curve);
                w.WriteNumber("iterations", channel.Iterations);
                w.WriteStartArray("points");
                if (channel.Points != null)
                {
                    foreach (var point in channel.Points)
                    {
                        if (point == null)
                            continue;

                        w.WriteStartObject();
                        w.WriteNumber("x", point.X);
                        w.WriteNumber("value", point.Value);
                        if (data.IsVersionAtLeast(2, 3, 1))
                        {
                            w.WriteNumber("bezierX0", point.BezierX0);
                            w.WriteNumber("bezierY0", point.BezierY0);
                            w.WriteNumber("bezierX1", point.BezierX1);
                            w.WriteNumber("bezierY1", point.BezierY1);
                        }
                        w.WriteEndObject();
                    }
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();
    }

    // ── Paths ────────────────────────────────────────────────────────

    private static Dictionary<string, string> HashPaths(UndertaleData data)
    {
        if (data.Paths == null) return [];
        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(data.Paths, path =>
        {
            if (path?.Name?.Content == null) return;
            var hash = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", path.Name.Content);
                w.WriteBoolean("isSmooth", path.IsSmooth);
                w.WriteBoolean("isClosed", path.IsClosed);
                w.WriteNumber("precision", path.Precision);
                w.WriteNumber("pointCount", path.Points?.Count ?? 0);
                w.WriteStartArray("points");
                if (path.Points != null)
                    foreach (var p in path.Points)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("x", p.X);
                        w.WriteNumber("y", p.Y);
                        w.WriteNumber("speed", p.Speed);
                        w.WriteEndObject();
                    }
                w.WriteEndArray();
                w.WriteEndObject();
            });
            result[path.Name.Content] = hash;
        });
        return new(result);
    }

    // ── Tilesets ─────────────────────────────────────────────────────

    private static Dictionary<string, string> HashTilesets(UndertaleData data)
    {
        if (data.Backgrounds == null) return [];
        var tilesets = data.Backgrounds.Where(bg => bg?.Name?.Content != null
            && bg.GMS2TileWidth > 0 && bg.GMS2TileHeight > 0).ToList();
        if (tilesets.Count == 0) return [];

        // Tilesets are exported as files directly in Tilesets/ (no subdirectories),
        // so we produce a single combined hash for the entire type.
        var tsPageIndexMap = new Dictionary<UndertaleEmbeddedTexture, int>();
        if (data.EmbeddedTextures != null)
            for (int i = 0; i < data.EmbeddedTextures.Count; i++)
                tsPageIndexMap[data.EmbeddedTextures[i]] = i;

        var combinedHash = JsonHash(w =>
        {
            w.WriteStartArray();
            foreach (var ts in tilesets.OrderBy(t => t.Name.Content))
            {
                w.WriteStartObject();
                w.WriteString("name", ts.Name.Content);
                w.WriteNumber("tileWidth", ts.GMS2TileWidth);
                w.WriteNumber("tileHeight", ts.GMS2TileHeight);
                w.WriteNumber("tileColumns", ts.GMS2TileColumns);
                w.WriteNumber("tileCount", ts.GMS2TileCount);
                w.WriteNumber("outputBorderX", ts.GMS2OutputBorderX);
                w.WriteNumber("outputBorderY", ts.GMS2OutputBorderY);
                w.WriteNumber("frameLength", ts.GMS2FrameLength);
                var tpi = ts.Texture;
                if (tpi != null)
                {
                    w.WriteNumber("texSrcX", tpi.SourceX);
                    w.WriteNumber("texSrcY", tpi.SourceY);
                    w.WriteNumber("texSrcW", tpi.SourceWidth);
                    w.WriteNumber("texSrcH", tpi.SourceHeight);
                    w.WriteNumber("texPage", tpi.TexturePage != null && tsPageIndexMap.TryGetValue(tpi.TexturePage, out var tsPi)
                        ? tsPi : -1);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
        return new() { ["Tilesets"] = combinedHash };
    }

    // ── Shaders ──────────────────────────────────────────────────────

    private static Dictionary<string, string> HashShaders(UndertaleData data)
    {
        if (data.Shaders == null) return [];
        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(data.Shaders, shader =>
        {
            if (shader?.Name?.Content == null) return;
            var hash = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", shader.Name.Content);
                w.WriteString("type", shader.Type.ToString());
                w.WriteString("glslES_Vertex", shader.GLSL_ES_Vertex?.Content ?? "");
                w.WriteString("glslES_Fragment", shader.GLSL_ES_Fragment?.Content ?? "");
                w.WriteString("glsl_Vertex", shader.GLSL_Vertex?.Content ?? "");
                w.WriteString("glsl_Fragment", shader.GLSL_Fragment?.Content ?? "");
                w.WriteString("hlsl9_Vertex", shader.HLSL9_Vertex?.Content ?? "");
                w.WriteString("hlsl9_Fragment", shader.HLSL9_Fragment?.Content ?? "");
                w.WriteEndObject();
            });
            result[shader.Name.Content] = hash;
        });
        return new(result);
    }

    // ── Timelines ────────────────────────────────────────────────────

    private static Dictionary<string, string> HashTimelines(UndertaleData data)
    {
        if (data.Timelines == null) return [];
        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(data.Timelines, tl =>
        {
            if (tl?.Name?.Content == null) return;
            var hash = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", tl.Name.Content);
                w.WriteStartArray("moments");
                if (tl.Moments != null)
                    foreach (var moment in tl.Moments)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("step", moment.Step);
                        w.WriteStartArray("actions");
                        if (moment.Event != null)
                            foreach (var action in moment.Event)
                                w.WriteStringValue(action?.CodeId?.Name?.Content ?? "");
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                w.WriteEndArray();
                w.WriteEndObject();
            });
            result[tl.Name.Content] = hash;
        });
        return new(result);
    }

    // ── GameObjects ──────────────────────────────────────────────────

    private static Dictionary<string, string> HashGameObjects(UndertaleData data)
    {
        if (data.GameObjects == null) return [];
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var groups = data.GameObjects
            .Select((obj, index) => (obj, index))
            .Where(item => item.obj?.Name?.Content != null)
            .GroupBy(item => item.obj!.Name.Content, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var hash = JsonHash(w =>
            {
                w.WriteStartArray();
                foreach (var (obj, index) in group.OrderBy(item => item.index))
                {
                    w.WriteStartObject();
                    w.WriteNumber("index", index);
                    w.WriteString("name", obj!.Name.Content);
                    w.WriteString("spriteName", obj.Sprite?.Name?.Content ?? "");
                    w.WriteBoolean("visible", obj.Visible);
                    w.WriteBoolean("managed", obj.Managed);
                    w.WriteBoolean("solid", obj.Solid);
                    w.WriteNumber("depth", obj.Depth);
                    w.WriteBoolean("persistent", obj.Persistent);
                    w.WriteString("parentName", obj.ParentId?.Name?.Content ?? "");
                    w.WriteString("maskSpriteName", obj.TextureMaskId?.Name?.Content ?? "");
                    w.WriteBoolean("usesPhysics", obj.UsesPhysics);
                    w.WriteBoolean("isSensor", obj.IsSensor);
                    w.WriteNumber("collisionShape", (int)obj.CollisionShape);
                    w.WriteNumber("density", obj.Density);
                    w.WriteNumber("restitution", obj.Restitution);
                    w.WriteNumber("group", obj.Group);
                    w.WriteNumber("linearDamping", obj.LinearDamping);
                    w.WriteNumber("angularDamping", obj.AngularDamping);
                    w.WriteNumber("friction", obj.Friction);
                    w.WriteBoolean("kinematic", obj.Kinematic);
                    // Events
                    w.WriteStartArray("events");
                    for (int evtType = 0; evtType < obj.Events.Count; evtType++)
                    {
                        foreach (var evt in obj.Events[evtType])
                        {
                            w.WriteStartObject();
                            w.WriteNumber("type", evtType);
                            w.WriteNumber("subtype", evt.EventSubtype);
                            w.WriteStartArray("actions");
                            foreach (var action in evt.Actions)
                                w.WriteStringValue(action.CodeId?.Name?.Content ?? "");
                            w.WriteEndArray();
                            w.WriteEndObject();
                        }
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
            result[group.Key] = hash;
        }
        return result;
    }

    // ── Rooms ────────────────────────────────────────────────────────

    private static Dictionary<string, string> HashRooms(UndertaleData data)
    {
        if (data.Rooms == null) return [];
        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(data.Rooms, room =>
        {
            if (room?.Name?.Content == null) return;
            var hash = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", room.Name.Content);
                w.WriteString("caption", room.Caption?.Content ?? "");
                w.WriteNumber("width", room.Width);
                w.WriteNumber("height", room.Height);
                w.WriteNumber("speed", room.Speed);
                w.WriteBoolean("persistent", room.Persistent);
                w.WriteNumber("backgroundColor", room.BackgroundColor);
                w.WriteBoolean("drawBackgroundColor", room.DrawBackgroundColor);
                w.WriteString("creationCode", room.CreationCodeId?.Name?.Content ?? "");
                w.WriteNumber("flags", (uint)room.Flags);
                w.WriteBoolean("world", room.World);
                w.WriteNumber("top", room.Top);
                w.WriteNumber("left", room.Left);
                w.WriteNumber("right", room.Right);
                w.WriteNumber("bottom", room.Bottom);
                w.WriteNumber("gravityX", room.GravityX);
                w.WriteNumber("gravityY", room.GravityY);
                w.WriteNumber("metersPerPixel", room.MetersPerPixel);

                // Backgrounds
                w.WriteStartArray("backgrounds");
                if (room.Backgrounds != null)
                    foreach (var bg in room.Backgrounds)
                    {
                        w.WriteStartObject();
                        w.WriteBoolean("enabled", bg.Enabled);
                        w.WriteBoolean("foreground", bg.Foreground);
                        w.WriteString("bgDef", bg.BackgroundDefinition?.Name?.Content ?? "");
                        w.WriteNumber("x", bg.X); w.WriteNumber("y", bg.Y);
                        w.WriteBoolean("tiledH", bg.TiledHorizontally);
                        w.WriteBoolean("tiledV", bg.TiledVertically);
                        w.WriteNumber("speedX", bg.SpeedX); w.WriteNumber("speedY", bg.SpeedY);
                        w.WriteBoolean("stretch", bg.Stretch);
                        w.WriteEndObject();
                    }
                w.WriteEndArray();

                // Views
                w.WriteStartArray("views");
                if (room.Views != null)
                    foreach (var v in room.Views)
                    {
                        w.WriteStartObject();
                        w.WriteBoolean("enabled", v.Enabled);
                        w.WriteNumber("viewX", v.ViewX); w.WriteNumber("viewY", v.ViewY);
                        w.WriteNumber("viewW", v.ViewWidth); w.WriteNumber("viewH", v.ViewHeight);
                        w.WriteNumber("portX", v.PortX); w.WriteNumber("portY", v.PortY);
                        w.WriteNumber("portW", v.PortWidth); w.WriteNumber("portH", v.PortHeight);
                        w.WriteNumber("borderX", v.BorderX); w.WriteNumber("borderY", v.BorderY);
                        w.WriteNumber("speedX", v.SpeedX); w.WriteNumber("speedY", v.SpeedY);
                        w.WriteString("objectId", v.ObjectId?.Name?.Content ?? "");
                        w.WriteEndObject();
                    }
                w.WriteEndArray();

                // Instances
                w.WriteStartArray("instances");
                if (room.GameObjects != null)
                    foreach (var inst in room.GameObjects)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("id", inst.InstanceID);
                        w.WriteString("obj", inst.ObjectDefinition?.Name?.Content ?? "");
                        w.WriteNumber("x", inst.X); w.WriteNumber("y", inst.Y);
                        w.WriteString("cc", inst.CreationCode?.Name?.Content ?? "");
                        w.WriteNumber("scX", inst.ScaleX); w.WriteNumber("scY", inst.ScaleY);
                        w.WriteNumber("rot", inst.Rotation);
                        w.WriteNumber("col", inst.Color);
                        w.WriteNumber("imgSpd", inst.ImageSpeed);
                        w.WriteNumber("imgIdx", inst.ImageIndex);
                        w.WriteEndObject();
                    }
                w.WriteEndArray();

                // Tiles
                w.WriteStartArray("tiles");
                if (room.Tiles != null)
                    foreach (var t in room.Tiles)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("x", t.X); w.WriteNumber("y", t.Y);
                        w.WriteBoolean("sm", t.spriteMode);
                        if (t.spriteMode) w.WriteString("spr", t.SpriteDefinition?.Name?.Content ?? "");
                        else w.WriteString("bg", t.BackgroundDefinition?.Name?.Content ?? "");
                        w.WriteNumber("sx", t.SourceX); w.WriteNumber("sy", t.SourceY);
                        w.WriteNumber("w", t.Width); w.WriteNumber("h", t.Height);
                        w.WriteNumber("d", t.TileDepth); w.WriteNumber("id", t.InstanceID);
                        w.WriteNumber("scX", t.ScaleX); w.WriteNumber("scY", t.ScaleY);
                        w.WriteNumber("col", t.Color);
                        w.WriteEndObject();
                    }
                w.WriteEndArray();

                // Layers (GMS2)
                if (room.Layers != null)
                {
                    w.WriteStartArray("layers");
                    foreach (var layer in room.Layers)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", layer.LayerName?.Content ?? "");
                        w.WriteNumber("id", layer.LayerId);
                        w.WriteNumber("type", (int)layer.LayerType);
                        w.WriteNumber("depth", layer.LayerDepth);
                        w.WriteNumber("xOff", layer.XOffset); w.WriteNumber("yOff", layer.YOffset);
                        w.WriteNumber("hSpd", layer.HSpeed); w.WriteNumber("vSpd", layer.VSpeed);
                        w.WriteBoolean("vis", layer.IsVisible);

                        if (layer.LayerType == UndertaleRoom.LayerType.Instances && layer.InstancesData?.Instances != null)
                        {
                            w.WriteStartArray("instIds");
                            foreach (var inst in layer.InstancesData.Instances)
                                w.WriteNumberValue((int)inst.InstanceID);
                            w.WriteEndArray();
                        }
                        else if (layer.LayerType == UndertaleRoom.LayerType.Tiles && layer.TilesData != null)
                        {
                            w.WriteString("tilesBg", layer.TilesData.Background?.Name?.Content ?? "");
                            w.WriteNumber("tilesX", layer.TilesData.TilesX);
                            w.WriteNumber("tilesY", layer.TilesData.TilesY);
                            if (layer.TilesData.TileData != null)
                            {
                                w.WriteStartArray("td");
                                foreach (var row in layer.TilesData.TileData)
                                {
                                    w.WriteStartArray();
                                    if (row != null) foreach (var val in row) w.WriteNumberValue(val);
                                    w.WriteEndArray();
                                }
                                w.WriteEndArray();
                            }
                        }
                        else if (layer.LayerType == UndertaleRoom.LayerType.Background && layer.BackgroundData != null)
                        {
                            var bd = layer.BackgroundData;
                            w.WriteBoolean("bgVis", bd.Visible);
                            w.WriteString("bgSpr", bd.Sprite?.Name?.Content ?? "");
                            w.WriteBoolean("bgTH", bd.TiledHorizontally);
                            w.WriteBoolean("bgTV", bd.TiledVertically);
                            w.WriteBoolean("bgStr", bd.Stretch);
                            w.WriteNumber("bgCol", bd.Color);
                            w.WriteNumber("bgASpd", bd.AnimationSpeed);
                        }
                        else if (layer.LayerType == UndertaleRoom.LayerType.Assets && layer.AssetsData != null)
                        {
                            var ad = layer.AssetsData;
                            w.WriteNumber("ltCount", ad.LegacyTiles?.Count ?? 0);
                            w.WriteNumber("sprCount", ad.Sprites?.Count ?? 0);
                            if (ad.LegacyTiles != null)
                            {
                                w.WriteStartArray("lt");
                                foreach (var t in ad.LegacyTiles)
                                {
                                    w.WriteStartObject();
                                    w.WriteNumber("x", t.X); w.WriteNumber("y", t.Y);
                                    w.WriteNumber("sx", t.SourceX); w.WriteNumber("sy", t.SourceY);
                                    w.WriteNumber("w", t.Width); w.WriteNumber("h", t.Height);
                                    w.WriteNumber("d", t.TileDepth); w.WriteNumber("id", t.InstanceID);
                                    w.WriteString("bg", t.BackgroundDefinition?.Name?.Content ?? "");
                                    w.WriteEndObject();
                                }
                                w.WriteEndArray();
                            }
                            if (ad.Sprites != null)
                            {
                                w.WriteStartArray("spr");
                                foreach (var s in ad.Sprites)
                                {
                                    w.WriteStartObject();
                                    w.WriteString("n", s.Name?.Content ?? "");
                                    w.WriteString("s", s.Sprite?.Name?.Content ?? "");
                                    w.WriteNumber("x", s.X); w.WriteNumber("y", s.Y);
                                    w.WriteNumber("scX", s.ScaleX); w.WriteNumber("scY", s.ScaleY);
                                    w.WriteNumber("col", s.Color);
                                    w.WriteNumber("rot", s.Rotation);
                                    w.WriteEndObject();
                                }
                                w.WriteEndArray();
                            }
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                w.WriteEndObject();
            });
            result[room.Name.Content] = hash;
        });
        return new(result);
    }

    // ── CodeEntries ──────────────────────────────────────────────────

    private static Dictionary<string, string> HashCodeEntries(UndertaleData data)
    {
        if (data.Code == null) return [];
        var topLevel = new List<UndertaleCode>(data.Code.Count);
        foreach (var c in data.Code)
        {
            if (c?.Name?.Content != null && c.ParentEntry == null)
                topLevel.Add(c);
        }
        var result = new Dictionary<string, string>(topLevel.Count, StringComparer.Ordinal);
        var localsByName = data.CodeLocals?
            .Where(locals => locals?.Name?.Content != null)
            .GroupBy(locals => locals!.Name.Content, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var perName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var entry in topLevel)
        {
            try
            {
                // Hash disassembly (parent + children) for name-stable comparison
                using var ms = new MemoryStream();
                UndertaleCodeLocals? entryLocals = null;
                localsByName?.TryGetValue(entry.Name.Content, out entryLocals);
                var bytes = Encoding.UTF8.GetBytes(entry.Disassemble(data.Variables, entryLocals));
                ms.Write(bytes, 0, bytes.Length);
                foreach (var child in entry.ChildEntries)
                {
                    if (child?.Name?.Content == null) continue;
                    try
                    {
                        ms.WriteByte(0); // separator
                        UndertaleCodeLocals? childLocals = null;
                        localsByName?.TryGetValue(child.Name.Content, out childLocals);
                        var childBytes = Encoding.UTF8.GetBytes(child.Disassemble(data.Variables, childLocals));
                        ms.Write(childBytes, 0, childBytes.Length);
                    }
                    catch { }
                }
                if (!perName.TryGetValue(entry.Name.Content, out var hashes))
                {
                    hashes = [];
                    perName[entry.Name.Content] = hashes;
                }
                hashes.Add(HexHash(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
            }
            catch
            {
                // Fallback: structural hash
                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms);
                bw.Write(entry.Length);
                bw.Write(entry.Instructions.Count);
                foreach (var instr in entry.Instructions)
                {
                    bw.Write((byte)instr.Kind);
                    bw.Write((byte)instr.Type1);
                    bw.Write((byte)instr.Type2);
                }
                bw.Flush();
                if (!perName.TryGetValue(entry.Name.Content, out var hashes))
                {
                    hashes = [];
                    perName[entry.Name.Content] = hashes;
                }
                hashes.Add(HexHash(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
            }
        }
        foreach (var (name, hashes) in perName)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(hashes.Count);
            foreach (var hash in hashes)
                bw.Write(hash);
            bw.Flush();
            result[name] = HexHash(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        }
        return result;
    }

    // ── Extensions ───────────────────────────────────────────────────

    private static Dictionary<string, string> HashExtensions(UndertaleData data)
    {
        if (data.Extensions == null) return [];
        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(data.Extensions, ext =>
        {
            if (ext?.Name?.Content == null) return;
            var hash = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", ext.Name.Content);
                w.WriteString("className", ext.ClassName?.Content ?? "");
                w.WriteString("folderName", ext.FolderName?.Content ?? "");
                w.WriteString("version", ext.Version?.Content ?? "");
                w.WriteStartArray("files");
                if (ext.Files != null)
                    foreach (var file in ext.Files)
                    {
                        w.WriteStartObject();
                        w.WriteString("filename", file?.Filename?.Content ?? "");
                        w.WriteString("cleanupScript", file?.CleanupScript?.Content ?? "");
                        w.WriteString("initScript", file?.InitScript?.Content ?? "");
                        w.WriteNumber("kind", (int)(file?.Kind ?? 0));
                        w.WriteStartArray("functions");
                        if (file?.Functions != null)
                            foreach (var func in file.Functions)
                            {
                                w.WriteStartObject();
                                w.WriteString("name", func?.Name?.Content ?? "");
                                w.WriteString("extName", func?.ExtName?.Content ?? "");
                                w.WriteNumber("id", (int)(func?.ID ?? 0));
                                w.WriteNumber("kind", (int)(func?.Kind ?? 0));
                                w.WriteNumber("retType", (int)(func?.RetType ?? 0));
                                w.WriteStartArray("arguments");
                                if (func?.Arguments != null)
                                    foreach (var arg in func.Arguments)
                                    {
                                        w.WriteStartObject();
                                        w.WriteNumber("type", (int)(arg?.Type ?? 0));
                                        w.WriteEndObject();
                                    }
                                w.WriteEndArray();
                                w.WriteEndObject();
                            }
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                w.WriteEndArray();
                w.WriteStartArray("options");
                if (ext.Options != null)
                    foreach (var option in ext.Options)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", option?.Name?.Content ?? "");
                        w.WriteString("value", option?.Value?.Content ?? "");
                        w.WriteNumber("kind", (int)(option?.Kind ?? 0));
                        w.WriteEndObject();
                    }
                w.WriteEndArray();
                w.WriteEndObject();
            });
            result[ext.Name.Content] = hash;
        });
        return new(result);
    }
}
