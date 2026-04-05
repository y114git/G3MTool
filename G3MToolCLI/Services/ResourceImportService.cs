using System.Collections;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using G3MToolCLI.Models;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using static UndertaleModLib.Models.UndertaleSound;

namespace G3MToolCLI.Services;

/// <summary>
/// Native C# resource importers for the patch apply pipeline.
/// Uses PatchFileSystem for in-memory ZIP access (no disk extraction).
/// Falls back to disk File/Directory when PFS is not set.
/// </summary>
public static partial class ResourceImportService
{
    // === PatchFileSystem integration ===
    // Thread-static so concurrent usage is safe
    [ThreadStatic]
    private static PatchFileSystem? _pfs;
    [ThreadStatic]
    private static (int Start, int End)? _progressRange;

    /// <summary>Set in-memory file system for imports. Pass null to revert to disk.</summary>
    public static void SetPatchFileSystem(PatchFileSystem? pfs) => _pfs = pfs;

    /// <summary>Get current PatchFileSystem (may be null if using disk).</summary>
    public static PatchFileSystem? GetPatchFileSystem() => _pfs;

    public static void SetProgressRange(int? startPercent = null, int? endPercent = null)
    {
        _progressRange = startPercent.HasValue && endPercent.HasValue
            ? (startPercent.Value, endPercent.Value)
            : null;
    }

    private static void ReportProgress(int current, int total)
    {
        if (_progressRange is { } range)
            LogService.ProgressRange(current, total, range.Start, range.End);
    }

    // File-system wrappers: use PFS when available, disk otherwise
    private static string[] GetDirs(string path) =>
        _pfs != null ? _pfs.GetDirectories(path) : Directory.GetDirectories(path);
    private static string[] GetFilesIn(string path, string pattern = "*") =>
        _pfs != null ? _pfs.GetFiles(path, pattern) : Directory.GetFiles(path, pattern);
    private static bool FExists(string path) =>
        _pfs != null ? _pfs.FileExists(path) : File.Exists(path);
    private static string FReadText(string path) =>
        _pfs != null ? _pfs.ReadAllText(path) : File.ReadAllText(path, Encoding.UTF8);
    private static byte[] FReadBytes(string path) =>
        _pfs != null ? _pfs.ReadAllBytes(path) : File.ReadAllBytes(path);
    private static string[] FReadLines(string path) =>
        _pfs != null ? _pfs.ReadAllLines(path) : File.ReadAllLines(path);
    private static bool DirExists(string path) =>
        _pfs != null ? _pfs.DirectoryExists(path) : Directory.Exists(path);

    /// <summary>
    /// Import a resource type using native C# code.
    /// Returns true if the resource type was handled natively.
    /// </summary>
    public static bool Import(string resourceType, UndertaleData data, string inputDir)
    {
        switch (resourceType)
        {
            case "AudioGroups": ImportAudioGroups(data, inputDir); return true;
            case "TextureGroupInfo": ImportTextureGroupInfo(data, inputDir); return true;
            case "Sprites": ImportSprites(data, inputDir); return true;
            case "Fonts": ImportFonts(data, inputDir); return true;
            case "Sounds": ImportSounds(data, inputDir); return true;
            case "Paths": ImportPaths(data, inputDir); return true;
            case "Shaders": ImportShaders(data, inputDir); return true;
            case "GameObjects": ImportGameObjects(data, inputDir); return true;
            case "Rooms": ImportRooms(data, inputDir); return true;
            case "Tilesets": ImportTilesets(data, inputDir); return true;
            case "Backgrounds": ImportBackgrounds(data, inputDir); return true;
            case "Extensions": ImportExtensions(data, inputDir); return true;
            case "Timelines": ImportTimelines(data, inputDir); return true;
            case "GeneralInfo": ImportGeneralInfo(data, inputDir); return true;
            case "TexturePageItems": ImportTexturePageItems(data, inputDir); return true;
            default: return false;
        }
    }

    /// <summary>
    /// Import asset order (reorder assets to match TARGET indices).
    /// </summary>
    public static void ImportAssetOrder(UndertaleData data, string inputDir)
    {
        ImportAssetOrderInternal(data, inputDir);
    }

    private static void Log(string msg) => LogService.Log(msg);

    private static T GetJsonValue<T>(JsonElement root, string propertyName, T defaultValue)
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
                {
                    if (elm.ValueKind == JsonValueKind.True) return (T)(object)true;
                    if (elm.ValueKind == JsonValueKind.False) return (T)(object)false;
                    if (elm.ValueKind == JsonValueKind.Number) return (T)(object)(elm.GetInt32() != 0);
                    return (T)(object)elm.GetBoolean();
                }
                if (typeof(T) == typeof(float))
                    return (T)(object)(float)elm.GetDouble();
                if (typeof(T) == typeof(string))
                    return (T)(object)(elm.GetString() ?? "");
            }
            catch { }
        }
        return defaultValue;
    }

    // =========================================================================
    // AudioGroups
    // =========================================================================
    private static void ImportAudioGroups(UndertaleData data, string inputDir)
    {
        var dirs = GetDirs(inputDir);
        if (dirs.Length == 0) return;

        Log($"[ImportAudioGroups] Found {dirs.Length} audio group(s) to import.");
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

                var ag = data.AudioGroups?.ByName(name);
                bool isNew = ag == null;
                if (isNew)
                {
                    ag = new UndertaleAudioGroup { Name = data.Strings.MakeString(name) };
                }

                if (root.TryGetProperty("path", out JsonElement pathElm))
                {
                    string path = pathElm.GetString() ?? "";
                    if (!string.IsNullOrEmpty(path))
                        ag!.Path = data.Strings.MakeString(path);
                }

                if (isNew) { data.AudioGroups!.Add(ag!); created++; } else updated++;
            }
            catch (Exception ex) { Log($"[ImportAudioGroups] Error: {name}: {ex.Message}"); }
        }
        Log($"[ImportAudioGroups] Done. Created: {created}, Updated: {updated}");
    }

    // =========================================================================
    // Paths
    // =========================================================================
    private static void ImportPaths(UndertaleData data, string inputDir)
    {
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

                var path = data.Paths?.ByName(name);
                bool isNew = path == null;
                if (isNew)
                {
                    path = new UndertalePath
                    {
                        Name = data.Strings.MakeString(name),
                        Points = [],
                        IsSmooth = false,
                        IsClosed = false,
                        Precision = 4
                    };
                }

                if (root.TryGetProperty("isSmooth", out JsonElement isSmoothElm))
                    path!.IsSmooth = isSmoothElm.GetBoolean();
                if (root.TryGetProperty("isClosed", out JsonElement isClosedElm))
                    path!.IsClosed = isClosedElm.GetBoolean();
                if (root.TryGetProperty("precision", out JsonElement precisionElm))
                    path!.Precision = (uint)precisionElm.GetInt32();

                if (root.TryGetProperty("points", out JsonElement pointsElm) && pointsElm.ValueKind == JsonValueKind.Array)
                {
                    path!.Points.Clear();
                    foreach (var ptElm in pointsElm.EnumerateArray())
                    {
                        var pt = new UndertalePath.PathPoint();
                        if (ptElm.TryGetProperty("x", out JsonElement xElm)) pt.X = (float)xElm.GetDouble();
                        if (ptElm.TryGetProperty("y", out JsonElement yElm)) pt.Y = (float)yElm.GetDouble();
                        if (ptElm.TryGetProperty("speed", out JsonElement sElm)) pt.Speed = (float)sElm.GetDouble();
                        path.Points.Add(pt);
                    }
                }

                if (isNew) data.Paths!.Add(path!);
            }
            catch (Exception ex) { Log($"[ImportPaths] Error: {name}: {ex.Message}"); }
        }
    }

    // =========================================================================
    // TexturePageItems
    // =========================================================================
    private static void ImportTexturePageItems(UndertaleData data, string inputDir)
    {
        string jsonPath = Path.Combine(inputDir, "texture_page_items.json");
        if (!FExists(jsonPath)) return;

        using var jsonDoc = JsonDocument.Parse(FReadText(jsonPath));
        if (jsonDoc.RootElement.ValueKind != JsonValueKind.Array) return;

        var items = jsonDoc.RootElement.EnumerateArray().ToList();
        int updated = 0, created = 0;

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
                if (index >= 0 && index < data.TexturePageItems.Count)
                {
                    item = data.TexturePageItems[index];
                    updated++;
                }
                else
                {
                    item = new UndertaleTexturePageItem
                    {
                        Name = new UndertaleString(name.Length > 0 ? name : $"PageItem {data.TexturePageItems.Count}")
                    };
                    data.TexturePageItems.Add(item);
                    created++;
                }

                item.SourceX = sourceX; item.SourceY = sourceY;
                item.SourceWidth = sourceWidth; item.SourceHeight = sourceHeight;
                item.TargetX = targetX; item.TargetY = targetY;
                item.TargetWidth = targetWidth; item.TargetHeight = targetHeight;
                item.BoundingWidth = boundingWidth; item.BoundingHeight = boundingHeight;

                if (texturePageIndex >= 0 && texturePageIndex < data.EmbeddedTextures.Count)
                    item.TexturePage = data.EmbeddedTextures[texturePageIndex];
            }
            catch (Exception ex) { Log($"[ImportTexturePageItems] Error: {ex.Message}"); }
        }
        Log($"[ImportTexturePageItems] Done. {updated} updated, {created} created.");
    }

    // =========================================================================
    // Backgrounds
    // =========================================================================
    private static void ImportBackgrounds(UndertaleData data, string inputDir)
    {
        var dirs = GetDirs(inputDir);
        if (dirs.Length == 0) return;

        int imported = 0, created = 0;
        using TextureWorker worker = new();

        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            string pngPath = Path.Combine(dir, name + ".png");
            string jsonPath = Path.Combine(dir, name + ".json");
            if (!FExists(pngPath) && !FExists(jsonPath)) continue;

            try
            {
                var bg = data.Backgrounds.ByName(name);
                bool isNew = bg == null;
                if (isNew)
                {
                    bg = new UndertaleBackground
                    {
                        Name = data.Strings.MakeString(name),
                        Transparent = false,
                        Smooth = false,
                        Preload = false
                    };
                    if (data.IsGameMaker2())
                    {
                        bg.GMS2TileWidth = 0; bg.GMS2TileHeight = 0;
                        bg.GMS2TileColumns = 0; bg.GMS2TileCount = 0;
                        bg.GMS2OutputBorderX = 0; bg.GMS2OutputBorderY = 0;
                        bg.GMS2FrameLength = 0;
                    }
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
                    bg!.Texture = newTPI;
                }

                if (FExists(jsonPath))
                {
                    using var jsonDoc = JsonDocument.Parse(FReadText(jsonPath));
                    var root = jsonDoc.RootElement;
                    bg!.Transparent = GetJsonValue(root, "transparent", bg.Transparent);
                    bg.Smooth = GetJsonValue(root, "smooth", bg.Smooth);
                    bg.Preload = GetJsonValue(root, "preload", bg.Preload);
                    if (data.IsGameMaker2() && root.TryGetProperty("gms2UnknownAlways2", out _))
                        bg.GMS2UnknownAlways2 = GetJsonValue(root, "gms2UnknownAlways2", bg.GMS2UnknownAlways2);
                }

                if (isNew) data.Backgrounds.Add(bg!);
                imported++;
            }
            catch (Exception ex) { Log($"[ImportBackgrounds] Failed: {name}: {ex.Message}"); }
        }
        Log($"[ImportBackgrounds] Done. {imported} processed ({created} new).");
    }

    // =========================================================================
    // Timelines
    // =========================================================================
    private static void ImportTimelines(UndertaleData data, string inputDir)
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
                if (root.TryGetProperty("name", out JsonElement nameElm))
                    name = nameElm.GetString() ?? name;

                var tl = data.Timelines?.ByName(name);
                bool isNew = tl == null;
                if (isNew)
                {
                    tl = new UndertaleTimeline
                    {
                        Name = data.Strings.MakeString(name),
                        Moments = []
                    };
                    created++;
                }
                else updated++;

                if (root.TryGetProperty("moments", out JsonElement momentsElm) && momentsElm.ValueKind == JsonValueKind.Array)
                {
                    var arr = momentsElm.EnumerateArray().ToArray();
                    for (int mi = 0; mi < arr.Length; mi++)
                    {
                        var mElm = arr[mi];
                        UndertaleTimeline.UndertaleTimelineMoment moment;
                        if (mi < tl!.Moments.Count) { moment = tl.Moments[mi]; }
                        else
                        {
                            moment = new UndertaleTimeline.UndertaleTimelineMoment
                            {
                                Event = []
                            };
                            tl.Moments.Add(moment);
                        }
                        if (mElm.TryGetProperty("step", out JsonElement stepElm))
                            moment.Step = (uint)stepElm.GetInt64();
                        if (mElm.TryGetProperty("actions", out JsonElement actionsElm) && actionsElm.ValueKind == JsonValueKind.Array)
                        {
                            moment.Event ??= [];
                            var actArr = actionsElm.EnumerateArray().ToArray();
                            for (int ai = 0; ai < actArr.Length; ai++)
                            {
                                var aElm = actArr[ai];
                                UndertaleGameObject.EventAction action;
                                if (ai < moment.Event.Count) action = moment.Event[ai];
                                else { action = new UndertaleGameObject.EventAction(); moment.Event.Add(action); }
                                ApplyEventAction(data, action, aElm);
                            }
                        }
                    }
                }
                if (isNew) data.Timelines!.Add(tl!);
            }
            catch (Exception ex) { Log($"[ImportTimelines] Error: {name}: {ex.Message}"); }
        }
        Log($"[ImportTimelines] Done. Created: {created}, Updated: {updated}");
    }

    // Shared helper for event action properties (used by Timelines and GameObjects)
    private static void ApplyEventAction(UndertaleData data, UndertaleGameObject.EventAction action, JsonElement elm)
    {
        if (elm.TryGetProperty("libId", out JsonElement libIdElm))
            action.LibID = (uint)libIdElm.GetInt64();
        if (elm.TryGetProperty("id", out JsonElement idElm))
            action.ID = (uint)idElm.GetInt64();
        if (elm.TryGetProperty("kind", out JsonElement kindElm))
            action.Kind = (uint)kindElm.GetInt64();
        if (elm.TryGetProperty("useRelative", out JsonElement useRelativeElm))
            action.UseRelative = useRelativeElm.GetBoolean();
        if (elm.TryGetProperty("isQuestion", out JsonElement isQuestionElm))
            action.IsQuestion = isQuestionElm.GetBoolean();
        if (elm.TryGetProperty("useApplyTo", out JsonElement useApplyToElm))
            action.UseApplyTo = useApplyToElm.GetBoolean();
        if (elm.TryGetProperty("exeType", out JsonElement exeTypeElm))
            action.ExeType = (uint)exeTypeElm.GetInt64();
        if (elm.TryGetProperty("actionName", out JsonElement actionNameElm))
        {
            string actionName = actionNameElm.GetString() ?? "";
            action.ActionName = !string.IsNullOrEmpty(actionName) ? data.Strings.MakeString(actionName) : null;
        }
        if (elm.TryGetProperty("codeId", out JsonElement codeIdElm))
        {
            if (codeIdElm.ValueKind == JsonValueKind.String)
            {
                string codeName = codeIdElm.GetString() ?? "";
                if (!string.IsNullOrEmpty(codeName))
                    action.CodeId = data.Code.ByName(codeName);
            }
            else if (codeIdElm.ValueKind == JsonValueKind.Null)
                action.CodeId = null;
        }
        if (elm.TryGetProperty("argumentCount", out JsonElement argumentCountElm))
            action.ArgumentCount = (uint)argumentCountElm.GetInt64();
        if (elm.TryGetProperty("who", out JsonElement whoElm))
            action.Who = whoElm.GetInt32();
        if (elm.TryGetProperty("relative", out JsonElement relativeElm))
            action.Relative = relativeElm.GetBoolean();
        if (elm.TryGetProperty("isNot", out JsonElement isNotElm))
            action.IsNot = isNotElm.GetBoolean();
    }

    // =========================================================================
    // Shaders
    // =========================================================================
    private static void ImportShaders(UndertaleData data, string inputDir)
    {
        var dirs = GetDirs(inputDir);
        int imported = 0, updated = 0;

        foreach (string dir in dirs)
        {
            string name = Path.GetFileName(dir);
            try
            {
                bool existed = data.Shaders.ByName(name) != null;
                ImportShaderSingle(data, dir);
                if (existed) updated++; else imported++;
            }
            catch (Exception ex) { Log($"[ImportShaders] Error: {name}: {ex.Message}"); }
        }
        Log($"[ImportShaders] Done. {imported} new, {updated} updated.");
    }

    private static void ImportShaderSingle(UndertaleData data, string shaderDir)
    {
        string name = Path.GetFileName(shaderDir);
        if (string.IsNullOrEmpty(name)) return;

        var shader = data.Shaders.ByName(name);
        if (shader == null)
        {
            shader = new UndertaleShader { Name = new UndertaleString(name) };
            data.Strings.Add(shader.Name);
            data.Shaders.Add(shader);
        }

        string typeFile = Path.Combine(shaderDir, "Type.txt");
        if (FExists(typeFile))
        {
            string typeStr = FReadText(typeFile);
            if (!string.IsNullOrEmpty(typeStr) && Enum.TryParse<UndertaleShader.ShaderType>(typeStr, out var st))
                shader.Type = st;
        }

        string[] textFiles = ["GLSL_ES_Fragment.txt", "GLSL_ES_Vertex.txt", "GLSL_Fragment.txt", "GLSL_Vertex.txt", "HLSL9_Fragment.txt", "HLSL9_Vertex.txt"];
        foreach (string fileName in textFiles)
        {
            string fp = Path.Combine(shaderDir, fileName);
            if (!FExists(fp)) continue;
            try
            {
                string code = FReadText(fp);
                UndertaleString? shaderString = null;
                switch (fileName)
                {
                    case "GLSL_ES_Fragment.txt": if (shader.GLSL_ES_Fragment == null) shader.GLSL_ES_Fragment = new UndertaleString(code); else shader.GLSL_ES_Fragment.Content = code; shaderString = shader.GLSL_ES_Fragment; break;
                    case "GLSL_ES_Vertex.txt": if (shader.GLSL_ES_Vertex == null) shader.GLSL_ES_Vertex = new UndertaleString(code); else shader.GLSL_ES_Vertex.Content = code; shaderString = shader.GLSL_ES_Vertex; break;
                    case "GLSL_Fragment.txt": if (shader.GLSL_Fragment == null) shader.GLSL_Fragment = new UndertaleString(code); else shader.GLSL_Fragment.Content = code; shaderString = shader.GLSL_Fragment; break;
                    case "GLSL_Vertex.txt": if (shader.GLSL_Vertex == null) shader.GLSL_Vertex = new UndertaleString(code); else shader.GLSL_Vertex.Content = code; shaderString = shader.GLSL_Vertex; break;
                    case "HLSL9_Fragment.txt": if (shader.HLSL9_Fragment == null) shader.HLSL9_Fragment = new UndertaleString(code); else shader.HLSL9_Fragment.Content = code; shaderString = shader.HLSL9_Fragment; break;
                    case "HLSL9_Vertex.txt": if (shader.HLSL9_Vertex == null) shader.HLSL9_Vertex = new UndertaleString(code); else shader.HLSL9_Vertex.Content = code; shaderString = shader.HLSL9_Vertex; break;
                }
                if (shaderString != null && !data.Strings.Any(s => s == shaderString))
                    data.Strings.Add(shaderString);
            }
            catch { }
        }

        string[] binaryFiles = ["HLSL11_VertexData.bin", "HLSL11_PixelData.bin", "PSSL_VertexData.bin", "PSSL_PixelData.bin",
                                  "Cg_PSVita_VertexData.bin", "Cg_PSVita_PixelData.bin", "Cg_PS3_VertexData.bin", "Cg_PS3_PixelData.bin"];
        foreach (string fileName in binaryFiles)
        {
            string fp = Path.Combine(shaderDir, fileName);
            if (!FExists(fp)) continue;
            try
            {
                byte[] bytes = FReadBytes(fp);
                if (bytes.Length == 0) continue;
                static UndertaleShader.UndertaleRawShaderData GetOrCreate(ref UndertaleShader.UndertaleRawShaderData? field)
                {
                    field ??= new UndertaleShader.UndertaleRawShaderData();
                    return field;
                }
                switch (fileName)
                {
                    case "HLSL11_VertexData.bin": var d1 = GetOrCreate(ref shader.HLSL11_VertexData); d1.Data = bytes; d1.IsNull = false; break;
                    case "HLSL11_PixelData.bin": var d2 = GetOrCreate(ref shader.HLSL11_PixelData); d2.Data = bytes; d2.IsNull = false; break;
                    case "PSSL_VertexData.bin": var d3 = GetOrCreate(ref shader.PSSL_VertexData); d3.Data = bytes; d3.IsNull = false; break;
                    case "PSSL_PixelData.bin": var d4 = GetOrCreate(ref shader.PSSL_PixelData); d4.Data = bytes; d4.IsNull = false; break;
                    case "Cg_PSVita_VertexData.bin": var d5 = GetOrCreate(ref shader.Cg_PSVita_VertexData); d5.Data = bytes; d5.IsNull = false; break;
                    case "Cg_PSVita_PixelData.bin": var d6 = GetOrCreate(ref shader.Cg_PSVita_PixelData); d6.Data = bytes; d6.IsNull = false; break;
                    case "Cg_PS3_VertexData.bin": var d7 = GetOrCreate(ref shader.Cg_PS3_VertexData); d7.Data = bytes; d7.IsNull = false; break;
                    case "Cg_PS3_PixelData.bin": var d8 = GetOrCreate(ref shader.Cg_PS3_PixelData); d8.Data = bytes; d8.IsNull = false; break;
                }
            }
            catch { }
        }

        string attrsFile = Path.Combine(shaderDir, "VertexShaderAttributes.txt");
        if (FExists(attrsFile))
        {
            try
            {
                string text = FReadText(attrsFile);
                if (!string.IsNullOrEmpty(text))
                {
                    shader.VertexShaderAttributes ??= [];
                    shader.VertexShaderAttributes.Clear();
                    foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var attrName = new UndertaleString(line.Trim());
                        data.Strings.Add(attrName);
                        shader.VertexShaderAttributes.Add(new UndertaleShader.VertexShaderAttribute { Name = attrName });
                    }
                }
            }
            catch { }
        }
    }

    // =========================================================================
    // GeneralInfo
    // =========================================================================
    private static void ImportGeneralInfo(UndertaleData data, string inputDir)
    {
        if (data.GeneralInfo == null) return;

        string jsonPath = Path.Combine(inputDir, "GeneralInfo.json");
        if (!FExists(jsonPath))
            jsonPath = Path.Combine(inputDir, "GeneralInfo", "GeneralInfo.json");
        if (!FExists(jsonPath)) return;

        try
        {
            using var jsonDoc = JsonDocument.Parse(FReadText(jsonPath));
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("isDebuggerDisabled", out JsonElement dbgElm)) data.GeneralInfo.IsDebuggerDisabled = dbgElm.GetBoolean();
            if (root.TryGetProperty("bytecodeVersion", out JsonElement bcElm)) data.GeneralInfo.BytecodeVersion = (byte)bcElm.GetInt32();
            if (root.TryGetProperty("padding", out JsonElement padElm)) data.GeneralInfo.Padding = (ushort)padElm.GetInt32();
            if (root.TryGetProperty("fileName", out JsonElement fnElm)) { string fn = fnElm.GetString() ?? ""; if (fn.Length > 0) data.GeneralInfo.FileName = data.Strings.MakeString(fn); }
            if (root.TryGetProperty("config", out JsonElement cfgElm)) { string cfg = cfgElm.GetString() ?? ""; if (cfg.Length > 0) data.GeneralInfo.Config = data.Strings.MakeString(cfg); }
            if (root.TryGetProperty("lastObj", out JsonElement loElm)) data.GeneralInfo.LastObj = (uint)loElm.GetInt64();
            if (root.TryGetProperty("lastTile", out JsonElement ltElm)) data.GeneralInfo.LastTile = (uint)ltElm.GetInt64();
            if (root.TryGetProperty("gameID", out JsonElement giElm)) data.GeneralInfo.GameID = (uint)giElm.GetInt64();
            if (root.TryGetProperty("directPlayGuid", out JsonElement dpElm)) { string gs = dpElm.GetString() ?? ""; if (Guid.TryParse(gs, out Guid g)) data.GeneralInfo.DirectPlayGuid = g; }
            if (root.TryGetProperty("name", out JsonElement nmElm)) { string nm = nmElm.GetString() ?? ""; if (nm.Length > 0) data.GeneralInfo.Name = data.Strings.MakeString(nm); }
            if (root.TryGetProperty("major", out JsonElement majElm)) data.GeneralInfo.Major = (uint)majElm.GetInt64();
            if (root.TryGetProperty("minor", out JsonElement minElm)) data.GeneralInfo.Minor = (uint)minElm.GetInt64();
            if (root.TryGetProperty("release", out JsonElement relElm)) data.GeneralInfo.Release = (uint)relElm.GetInt64();
            if (root.TryGetProperty("build", out JsonElement bldElm)) data.GeneralInfo.Build = (uint)bldElm.GetInt64();
            if (root.TryGetProperty("defaultWindowWidth", out JsonElement wwElm)) data.GeneralInfo.DefaultWindowWidth = (uint)wwElm.GetInt64();
            if (root.TryGetProperty("defaultWindowHeight", out JsonElement whElm)) data.GeneralInfo.DefaultWindowHeight = (uint)whElm.GetInt64();
            if (root.TryGetProperty("infoFlags", out JsonElement ifElm)) data.GeneralInfo.Info = (UndertaleGeneralInfo.InfoFlags)ifElm.GetUInt32();
            if (root.TryGetProperty("licenseCRC32", out JsonElement lcElm)) data.GeneralInfo.LicenseCRC32 = (uint)lcElm.GetInt64();
            if (root.TryGetProperty("licenseMD5", out JsonElement lmElm) && lmElm.ValueKind == JsonValueKind.Array)
                data.GeneralInfo.LicenseMD5 = [.. lmElm.EnumerateArray().Select(e => (byte)e.GetInt32())];
            if (root.TryGetProperty("timestamp", out JsonElement tsElm)) data.GeneralInfo.Timestamp = (ulong)tsElm.GetUInt64();
            if (root.TryGetProperty("displayName", out JsonElement dnElm)) { string dn = dnElm.GetString() ?? ""; if (dn.Length > 0) data.GeneralInfo.DisplayName = data.Strings.MakeString(dn); }
            if (root.TryGetProperty("activeTargets", out JsonElement atElm)) data.GeneralInfo.ActiveTargets = atElm.GetUInt64();
            if (root.TryGetProperty("functionClassifications", out JsonElement fcElm)) data.GeneralInfo.FunctionClassifications = (UndertaleGeneralInfo.FunctionClassification)fcElm.GetUInt64();
            if (root.TryGetProperty("steamAppID", out JsonElement saElm)) data.GeneralInfo.SteamAppID = saElm.GetInt32();
            if (data.GeneralInfo.BytecodeVersion >= 14 && root.TryGetProperty("debuggerPort", out JsonElement dprtElm))
                data.GeneralInfo.DebuggerPort = (uint)dprtElm.GetInt64();

            if (root.TryGetProperty("roomOrder", out JsonElement roElm) && roElm.ValueKind == JsonValueKind.Array)
            {
                data.GeneralInfo.RoomOrder.Clear();
                foreach (var rElm in roElm.EnumerateArray())
                {
                    if (rElm.ValueKind == JsonValueKind.Null) continue;
                    string rn = rElm.GetString() ?? "";
                    if (string.IsNullOrEmpty(rn)) continue;
                    var room = data.Rooms.ByName(rn);
                    if (room != null)
                        data.GeneralInfo.RoomOrder.Add(new UndertaleResourceById<UndertaleRoom, UndertaleChunkROOM> { Resource = room });
                }
            }

            if (data.GeneralInfo.Major >= 2)
            {
                if (root.TryGetProperty("gms2RandomUID", out JsonElement uidElm) && uidElm.ValueKind == JsonValueKind.Array)
                    data.GeneralInfo.GMS2RandomUID = [.. uidElm.EnumerateArray().Select(e => e.GetInt64())];
                if (root.TryGetProperty("gms2FPS", out JsonElement fpsElm))
                    data.GeneralInfo.GMS2FPS = (float)fpsElm.GetDouble();
                if (root.TryGetProperty("gms2AllowStatistics", out JsonElement asElm))
                    data.GeneralInfo.GMS2AllowStatistics = asElm.GetBoolean();
                if (root.TryGetProperty("gms2GameGUID", out JsonElement ggElm) && ggElm.ValueKind == JsonValueKind.Array)
                    data.GeneralInfo.GMS2GameGUID = [.. ggElm.EnumerateArray().Select(e => (byte)e.GetInt32())];
            }

            Log($"[ImportGeneralInfo] Done. Game: {data.GeneralInfo.DisplayName?.Content ?? "N/A"}");
        }
        catch (Exception ex) { Log($"[ImportGeneralInfo] Failed: {ex.Message}"); throw; }
    }
}
