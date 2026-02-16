using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;

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
    public static Dictionary<string, Dictionary<string, string>> HashAll(UndertaleData data)
    {
        Dictionary<string, Dictionary<string, string>> result = [];

        result["GeneralInfo"] = HashGeneralInfo(data);
        result["AudioGroups"] = HashAudioGroups(data);
        result["TextureGroupInfo"] = HashTextureGroupInfo(data);
        result["Sprites"] = HashSprites(data);
        result["Backgrounds"] = HashBackgrounds(data);
        result["Fonts"] = HashFonts(data);
        result["Sounds"] = HashSounds(data);
        result["Paths"] = HashPaths(data);
        result["Tilesets"] = HashTilesets(data);
        result["Shaders"] = HashShaders(data);
        result["Timelines"] = HashTimelines(data);
        result["GameObjects"] = HashGameObjects(data);
        result["Rooms"] = HashRooms(data);
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
            using var pixels = image.GetPixels();
            byte[]? allPixels = pixels.ToByteArray("RGBA");
            if (allPixels == null)
            {
                foreach (var (srcX, srcY, srcW, srcH) in regions)
                    frameHashes[(pageIdx, srcX, srcY, srcW, srcH)] = "";
                return;
            }
            const int bpp = 4;

            foreach (var (srcX, srcY, srcW, srcH) in regions)
            {
                int regionSize = srcW * srcH * bpp;
                var regionBytes = new byte[regionSize];
                int dst = 0;
                for (int y = srcY; y < srcY + srcH; y++)
                {
                    Buffer.BlockCopy(allPixels, (y * pageW + srcX) * bpp, regionBytes, dst, srcW * bpp);
                    dst += srcW * bpp;
                }
                frameHashes[(pageIdx, srcX, srcY, srcW, srcH)] = HexHash(regionBytes);
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
                    w.WriteStartArray("maskLengths");
                    foreach (var mask in sprite.CollisionMasks)
                        w.WriteNumberValue(mask?.Data?.Length ?? 0);
                    w.WriteEndArray();
                }

                w.WriteEndObject();
            });
            result[sprite.Name.Content] = hash;
        });
        return new(result);
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
                w.WriteNumber("glyphCount", font.Glyphs?.Count ?? 0);
                var tpi = font.Texture;
                if (tpi != null)
                {
                    w.WriteNumber("texSrcX", tpi.SourceX);
                    w.WriteNumber("texSrcY", tpi.SourceY);
                    w.WriteNumber("texSrcW", tpi.SourceWidth);
                    w.WriteNumber("texSrcH", tpi.SourceHeight);
                }
                w.WriteEndObject();
            });
            result[font.Name.Content] = hash;
        });
        return new(result);
    }

    // ── Sounds ───────────────────────────────────────────────────────

    private static Dictionary<string, string> HashSounds(UndertaleData data)
    {
        if (data.Sounds == null) return [];
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
                w.WriteNumber("volume", snd.Volume);
                w.WriteNumber("pitch", snd.Pitch);
                w.WriteString("audioGroup", snd.AudioGroup?.Name?.Content ?? "");
                w.WriteNumber("audioID", snd.AudioID);
                w.WriteNumber("groupID", snd.GroupID);
                w.WriteEndObject();
            });
            result[snd.Name.Content] = hash;
        });
        return new(result);
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
        var result = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(data.GameObjects, obj =>
        {
            if (obj?.Name?.Content == null) return;
            var hash = JsonHash(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", obj.Name.Content);
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
            });
            result[obj.Name.Content] = hash;
        });
        return new(result);
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
        var result = new ConcurrentDictionary<string, string>();

        Parallel.ForEach(topLevel, entry =>
        {
            try
            {
                // Hash disassembly (parent + children) for name-stable comparison
                using var ms = new MemoryStream();
                var bytes = Encoding.UTF8.GetBytes(entry.Disassemble(data.Variables, data.CodeLocals.For(entry)));
                ms.Write(bytes, 0, bytes.Length);
                foreach (var child in entry.ChildEntries)
                {
                    if (child?.Name?.Content == null) continue;
                    try
                    {
                        ms.WriteByte(0); // separator
                        var childBytes = Encoding.UTF8.GetBytes(child.Disassemble(data.Variables, data.CodeLocals.For(child)));
                        ms.Write(childBytes, 0, childBytes.Length);
                    }
                    catch { }
                }
                result[entry.Name.Content] = HexHash(ms.GetBuffer().AsSpan(0, (int)ms.Length));
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
                result[entry.Name.Content] = HexHash(ms.GetBuffer().AsSpan(0, (int)ms.Length));
            }
        });
        return new(result);
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
                w.WriteStartArray("files");
                if (ext.Files != null)
                    foreach (var file in ext.Files)
                    {
                        w.WriteStartObject();
                        w.WriteString("filename", file?.Filename?.Content ?? "");
                        w.WriteString("cleanupScript", file?.CleanupScript?.Content ?? "");
                        w.WriteString("initScript", file?.InitScript?.Content ?? "");
                        w.WriteNumber("kind", (int)(file?.Kind ?? 0));
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
