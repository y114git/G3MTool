using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using G3MToolCLI.Models;
using G3MToolCLI.Utils;
using UndertaleModLib;

namespace G3MToolCLI.Services;

public static class G3MCacheService
{
    private const int Schema = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static G3MDataAnalysisCache? TryReadDataCache(string sourcePath, G3MCacheOptions? options)
    {
        if (options?.CanRead != true)
            return null;

        var cachePath = GetCachePath(options.ReadDirectory!, sourcePath);
        if (!File.Exists(cachePath))
            return null;

        try
        {
            using var zip = ZipFile.OpenRead(cachePath);
            var manifest = ReadJson<G3MCacheManifest>(zip, "g3mcache.json");
            if (!IsValidForSource(manifest, sourcePath))
                return null;

            var analysis = ReadJson<G3MDataAnalysisCache>(zip, "data_analysis.json");
            if (analysis?.DataInfo == null || analysis.ResourceHashes.Count == 0)
                return null;

            LogService.Log($"[Cache] Using analysis cache for {Path.GetFileName(sourcePath)}");
            return analysis;
        }
        catch (Exception ex)
        {
            LogService.Log($"[Cache] Ignored invalid cache for {Path.GetFileName(sourcePath)}: {ex.Message}");
            return null;
        }
    }

    public static G3MDataInfoSnapshot? TryReadDataInfoSnapshot(string sourcePath, G3MCacheOptions? options)
    {
        if (options?.CanRead != true)
            return null;

        var cachePath = GetCachePath(options.ReadDirectory!, sourcePath);
        if (!File.Exists(cachePath))
            return null;

        try
        {
            using var zip = ZipFile.OpenRead(cachePath);
            var manifest = ReadJson<G3MCacheManifest>(zip, "g3mcache.json");
            if (!IsValidForSource(manifest, sourcePath))
                return null;

            var analysis = ReadJson<G3MDataAnalysisCache>(zip, "data_analysis.json");
            if (analysis?.InfoSnapshot == null)
                return null;

            LogService.Log($"[Cache] Using info cache for {Path.GetFileName(sourcePath)}");
            return analysis.InfoSnapshot;
        }
        catch (Exception ex)
        {
            LogService.Log($"[Cache] Ignored invalid info cache for {Path.GetFileName(sourcePath)}: {ex.Message}");
            return null;
        }
    }

    public static DataFileInfo? TryReadDataFileInfo(string sourcePath, G3MCacheOptions? options)
    {
        if (options?.CanRead != true)
            return null;

        var cachePath = GetCachePath(options.ReadDirectory!, sourcePath);
        if (!File.Exists(cachePath))
            return null;

        try
        {
            using var zip = ZipFile.OpenRead(cachePath);
            var manifest = ReadJson<G3MCacheManifest>(zip, "g3mcache.json");
            if (!IsValidForSource(manifest, sourcePath))
                return null;

            var analysis = ReadJson<G3MDataAnalysisCache>(zip, "data_analysis.json");
            return analysis?.DataInfo;
        }
        catch
        {
            return null;
        }
    }

    public static async Task WriteDataInfoCacheAsync(
        string sourcePath,
        G3MDataInfoSnapshot infoSnapshot,
        G3MCacheOptions? options)
    {
        if (options?.CanWrite != true)
            return;

        var existingAnalysis = TryReadDataCache(sourcePath, options);
        if (existingAnalysis != null)
        {
            await WriteDataCacheAsync(
                sourcePath,
                existingAnalysis.DataInfo,
                existingAnalysis.ResourceHashes,
                existingAnalysis.ResourceNameCounts,
                ToReadOnlyOrderNames(existingAnalysis.OrderSensitiveNames),
                options,
                infoSnapshot);
            return;
        }

        var dataInfo = new DataFileInfo
        {
            Filename = infoSnapshot.File,
            Size = infoSnapshot.Size,
            Md5 = HashService.ComputeFileHash(sourcePath),
            BytecodeVersion = infoSnapshot.BytecodeVersion,
            GmsVersion = infoSnapshot.Version,
            GeneralInfo = infoSnapshot.GeneralInfo
        };

        await WriteDataCacheAsync(
            sourcePath,
            dataInfo,
            [],
            [],
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            options,
            infoSnapshot);
    }

    public static async Task WriteDataCacheAsync(
        string sourcePath,
        DataFileInfo dataInfo,
        Dictionary<string, Dictionary<string, string>> resourceHashes,
        Dictionary<string, Dictionary<string, int>> resourceNameCounts,
        Dictionary<string, IReadOnlyList<string>> orderSensitiveNames,
        G3MCacheOptions? options,
        G3MDataInfoSnapshot? infoSnapshot = null)
    {
        if (options?.CanWrite != true)
            return;

        try
        {
            Directory.CreateDirectory(options.WriteDirectory!);
            var cachePath = GetCachePath(options.WriteDirectory!, sourcePath);
            infoSnapshot ??= TryReadDataInfoSnapshot(sourcePath, options);
            var tempPath = cachePath + ".tmp";
            if (File.Exists(tempPath))
                File.Delete(tempPath);

            var info = new FileInfo(sourcePath);
            var manifest = new G3MCacheManifest
            {
                Schema = Schema,
                CreatedAt = DateTime.UtcNow.ToString("O"),
                SourceFile = Path.GetFileName(sourcePath),
                SourceSize = info.Length,
                SourceLastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                SourceMd5 = dataInfo.Md5
            };
            var analysis = new G3MDataAnalysisCache
            {
                DataInfo = dataInfo,
                ResourceHashes = resourceHashes,
                ResourceNameCounts = resourceNameCounts,
                OrderSensitiveNames = orderSensitiveNames.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.ToList(),
                    StringComparer.OrdinalIgnoreCase),
                InfoSnapshot = infoSnapshot
            };

            using (var zip = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                WriteJson(zip, "g3mcache.json", manifest);
                WriteJson(zip, "data_analysis.json", analysis);
            }

            if (File.Exists(cachePath))
                File.Delete(cachePath);
            File.Move(tempPath, cachePath);
            await Task.CompletedTask;
            LogService.Log($"[Cache] Wrote analysis cache for {Path.GetFileName(sourcePath)}");
        }
        catch (Exception ex)
        {
            LogService.Log($"[Cache] Could not write cache for {Path.GetFileName(sourcePath)}: {ex.Message}");
        }
    }

    public static Dictionary<string, IReadOnlyList<string>> ToReadOnlyOrderNames(Dictionary<string, List<string>> names) =>
        names.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<string>)kvp.Value, StringComparer.OrdinalIgnoreCase);

    public static G3MDataInfoSnapshot BuildInfoSnapshot(string sourcePath, UndertaleData data)
    {
        var generalInfo = data.GeneralInfo;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Sprites"] = data.Sprites?.Count ?? 0,
            ["Sounds"] = data.Sounds?.Count ?? 0,
            ["Code"] = data.Code?.Count ?? 0,
            ["GameObjects"] = data.GameObjects?.Count ?? 0,
            ["Rooms"] = data.Rooms?.Count ?? 0,
            ["Backgrounds"] = data.Backgrounds?.Count ?? 0,
            ["Fonts"] = data.Fonts?.Count ?? 0,
            ["Scripts"] = data.Scripts?.Count ?? 0,
            ["Shaders"] = data.Shaders?.Count ?? 0,
            ["Paths"] = data.Paths?.Count ?? 0,
            ["Timelines"] = data.Timelines?.Count ?? 0,
            ["Extensions"] = data.Extensions?.Count ?? 0,
            ["Variables"] = data.Variables?.Count ?? 0,
            ["Functions"] = data.Functions?.Count ?? 0,
            ["Strings"] = data.Strings?.Count ?? 0,
            ["AudioGroups"] = data.AudioGroups?.Count ?? 0,
            ["EmbeddedTextures"] = data.EmbeddedTextures?.Count ?? 0,
            ["TexturePageItems"] = data.TexturePageItems?.Count ?? 0,
            ["TextureGroupInfo"] = data.TextureGroupInfo?.Count ?? 0,
            ["Tilesets"] = data.Backgrounds?.Count ?? 0
        };

        int topLevelCode = data.Code?.Count(c => c?.ParentEntry == null) ?? 0;
        return new G3MDataInfoSnapshot
        {
            File = Path.GetFileName(sourcePath),
            Size = new FileInfo(sourcePath).Length,
            Game = generalInfo?.DisplayName?.Content ?? "Unknown",
            BytecodeVersion = generalInfo?.BytecodeVersion ?? 0,
            Version = GeneralInfoUtil.GetVersionDisplay(generalInfo),
            GeneralInfo = GeneralInfoUtil.ExtractGeneralInfo(data),
            ResourceCounts = counts,
            VariablesByInstanceType = data.Variables?
                .GroupBy(v => v.InstanceType.ToString())
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal) ?? [],
            FirstFunction = data.Functions != null && data.Functions.Count > 0 ? data.Functions[0]?.Name?.Content : null,
            LastFunction = data.Functions != null && data.Functions.Count > 0 ? data.Functions[^1]?.Name?.Content : null,
            TopLevelCodeCount = topLevelCode,
            ChildCodeCount = (data.Code?.Count ?? 0) - topLevelCode,
            AudioGroups = data.AudioGroups?.Select(g => g?.Name?.Content ?? "?").ToList() ?? [],
            Extensions = data.Extensions?.Select(e => e?.Name?.Content ?? "?").ToList() ?? [],
            RoomOrderPreview = generalInfo?.RoomOrder?
                .Take(10)
                .Select(r => r?.Resource?.Name?.Content ?? "?")
                .ToList() ?? [],
            RoomOrderCount = generalInfo?.RoomOrder?.Count ?? 0
        };
    }

    private static bool IsValidForSource(G3MCacheManifest? manifest, string sourcePath)
    {
        if (manifest?.Schema != Schema || !string.Equals(manifest.SourceKind, "data", StringComparison.OrdinalIgnoreCase))
            return false;

        var info = new FileInfo(sourcePath);
        if (info.Length != manifest.SourceSize)
            return false;

        if (!string.IsNullOrWhiteSpace(manifest.SourceMd5))
            return string.Equals(HashService.ComputeFileHash(sourcePath), manifest.SourceMd5, StringComparison.OrdinalIgnoreCase);

        return info.LastWriteTimeUtc.Ticks == manifest.SourceLastWriteUtcTicks;
    }

    private static string GetCachePath(string directory, string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        var identity = $"{Path.GetFullPath(sourcePath).ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
        var safeName = Path.GetFileNameWithoutExtension(sourcePath);
        foreach (var c in Path.GetInvalidFileNameChars())
            safeName = safeName.Replace(c, '_');
        return Path.Combine(directory, $"{safeName}_{hash}.g3mcache");
    }

    private static T? ReadJson<T>(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName);
        if (entry == null)
            return default;
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, JsonOptions);
    }

    private static void WriteJson<T>(ZipArchive zip, string entryName, T value)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, JsonOptions);
    }
}
