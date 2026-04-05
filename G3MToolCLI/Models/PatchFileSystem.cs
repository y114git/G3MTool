using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using G3MToolCLI.Services;

namespace G3MToolCLI.Models;

/// <summary>
/// In-memory file system loaded from a ZIP archive.
/// All file access goes through memory, eliminating disk extraction overhead.
/// </summary>
public sealed class PatchFileSystem
{
    // key = normalized path (forward slashes, case-insensitive)
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    // Pre-built directory index: parent path -> sorted child directory paths
    private readonly Dictionary<string, string[]> _childDirs = new(StringComparer.OrdinalIgnoreCase);

    // Pre-built file index: parent path -> sorted child file paths
    private readonly Dictionary<string, string[]> _childFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Code entries read during loading (GML source by entry name).</summary>
    public Dictionary<string, string> GmlEntries { get; } = [];

    /// <summary>Code entries read during loading (ASM source by entry name).</summary>
    public Dictionary<string, string> AsmEntries { get; } = [];

    /// <summary>Parsed manifest from g3mpatch.json.</summary>
    public G3MPatchManifest? Manifest { get; private set; }

    /// <summary>Helpers directory prefix ("Helpers" or "AssetOrder").</summary>
    public string HelpersPrefix { get; private set; } = "Helpers";

    /// <summary>Optional exact xdelta payload for byte-perfect patch application.</summary>
    public byte[]? ExactPatchBytes { get; private set; }

    /// <summary>Archive path of the optional exact payload.</summary>
    public string? ExactPatchPath { get; private set; }

    private static string Norm(string path) => path.Replace('\\', '/').TrimEnd('/');

    /// <summary>Decode bytes to UTF-8 string, stripping BOM if present.</summary>
    private static string DecodeUtf8(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Load entire ZIP into memory in a single pass.
    /// Parses manifest and code entries during loading.
    /// </summary>
    public static PatchFileSystem LoadFromZip(string zipPath)
    {
        var sw = Stopwatch.StartNew();
        var pfs = new PatchFileSystem();

        // Single sequential read of entire ZIP into memory
        var zipBytes = File.ReadAllBytes(zipPath);
        using var ms = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var fullName = Norm(entry.FullName);

            // Read entry bytes
            byte[] bytes;
            using (var stream = entry.Open())
            {
                if (entry.Length > 0)
                {
                    bytes = new byte[entry.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = stream.Read(bytes, read, bytes.Length - read);
                        if (n == 0) break;
                        read += n;
                    }
                    if (read < bytes.Length)
                        Array.Resize(ref bytes, read);
                }
                else
                {
                    using var buf = new MemoryStream();
                    stream.CopyTo(buf);
                    bytes = buf.ToArray();
                }
            }

            // Parse manifest
            if (fullName.Equals("g3mpatch.json", StringComparison.OrdinalIgnoreCase))
            {
                try { pfs.Manifest = JsonSerializer.Deserialize<G3MPatchManifest>(bytes); }
                catch { }
                pfs._files[fullName] = bytes;
                continue;
            }

            // Parse code entries into separate dictionaries
            if (fullName.StartsWith("CodeEntries/", StringComparison.OrdinalIgnoreCase))
            {
                string codeName = Path.GetFileNameWithoutExtension(entry.Name);
                string content = DecodeUtf8(bytes);

                if (entry.Name.EndsWith(".gml", StringComparison.OrdinalIgnoreCase))
                    pfs.GmlEntries[codeName] = content;
                else if (entry.Name.EndsWith(".asm", StringComparison.OrdinalIgnoreCase))
                    pfs.AsmEntries[codeName] = content;

                continue; // Don't store code entry bytes (already parsed to strings)
            }

            if (fullName.StartsWith("Exact/", StringComparison.OrdinalIgnoreCase))
            {
                if (pfs.ExactPatchBytes == null &&
                    entry.Name.EndsWith(".xdelta", StringComparison.OrdinalIgnoreCase))
                {
                    pfs.ExactPatchBytes = bytes;
                    pfs.ExactPatchPath = fullName;
                }
                else if (!entry.Name.EndsWith(".xdelta", StringComparison.OrdinalIgnoreCase))
                {
                    LogService.Warning(
                        $"[PatchFileSystem] Ignoring non-xdelta Exact entry '{entry.Name}' at '{fullName}'");
                }
                continue;
            }

            pfs._files[fullName] = bytes;
        }

        // Detect helpers prefix (new "Helpers" vs legacy "AssetOrder")
        bool hasHelpers = false, hasAssetOrder = false;
        foreach (var key in pfs._files.Keys)
        {
            if (!hasHelpers && key.StartsWith("Helpers/", StringComparison.OrdinalIgnoreCase))
                hasHelpers = true;
            else if (!hasAssetOrder && key.StartsWith("AssetOrder/", StringComparison.OrdinalIgnoreCase))
                hasAssetOrder = true;
            if (hasHelpers) break;
        }
        pfs.HelpersPrefix = hasHelpers ? "Helpers" : (hasAssetOrder ? "AssetOrder" : "Helpers");

        // Build directory index
        pfs.BuildDirectoryIndex();

        long totalBytes = pfs._files.Values.Sum(b => (long)b.Length);
        LogService.Log($"[PatchFileSystem] Loaded {pfs._files.Count} files + {pfs.GmlEntries.Count} GML + {pfs.AsmEntries.Count} ASM in {sw.Elapsed.TotalSeconds:F1}s ({zipBytes.Length / 1024 / 1024}MB ZIP -> {totalBytes / 1024 / 1024}MB memory)");

        return pfs;
    }

    private void BuildDirectoryIndex()
    {
        var dirSets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var fileSets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in _files.Keys)
        {
            var parts = path.Split('/');

            // Register file in its parent directory
            string fileParent = parts.Length >= 2 ? string.Join("/", parts[..^1]) : "";
            if (!fileSets.TryGetValue(fileParent, out var fileSet))
            {
                fileSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                fileSets[fileParent] = fileSet;
            }
            fileSet.Add(path);

            // Register directory chain
            string current = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string child = i == 0 ? parts[0] : current + "/" + parts[i];
                if (!dirSets.TryGetValue(current, out var dirSet))
                {
                    dirSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    dirSets[current] = dirSet;
                }
                dirSet.Add(child);
                current = child;
            }
        }

        // Convert to sorted arrays for stable iteration order
        foreach (var (parent, children) in dirSets)
            _childDirs[parent] = [.. children.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)];
        foreach (var (parent, files) in fileSets)
            _childFiles[parent] = [.. files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)];
    }

    // === File API ===

    public byte[] ReadAllBytes(string path)
    {
        var n = Norm(path);
        return _files.TryGetValue(n, out var bytes) ? bytes
            : throw new FileNotFoundException($"PatchFileSystem: file not found: {path}", path);
    }

    public string ReadAllText(string path) =>
        DecodeUtf8(ReadAllBytes(path));

    public string[] ReadAllLines(string path)
    {
        var text = ReadAllText(path);
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd('\r');
        return lines;
    }

    public bool FileExists(string path) =>
        _files.ContainsKey(Norm(path));

    // === Directory API ===

    public string[] GetDirectories(string dirPath)
    {
        var n = Norm(dirPath);
        return _childDirs.TryGetValue(n, out var dirs) ? dirs : [];
    }

    public string[] GetFiles(string dirPath, string pattern = "*")
    {
        var n = Norm(dirPath);
        if (!_childFiles.TryGetValue(n, out var files))
            return [];
        if (pattern is "*" or "*.*")
            return files;
        // Simple glob: "*.ext"
        if (pattern.StartsWith("*."))
        {
            string ext = pattern[1..]; // e.g., ".png"
            return [.. files.Where(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase))];
        }
        return files;
    }

    public bool DirectoryExists(string dirPath)
    {
        var n = Norm(dirPath);
        return _childDirs.ContainsKey(n) || _childFiles.ContainsKey(n);
    }

    // === Mutation API (for merge) ===

    /// <summary>Add a file to the in-memory file system. Overwrites if exists.</summary>
    public void AddFile(string path, byte[] data)
    {
        var n = Norm(path);
        _files[n] = data;
    }

    /// <summary>Add a text file (UTF-8 encoded) to the in-memory file system.</summary>
    public void AddTextFile(string path, string content)
    {
        AddFile(path, Encoding.UTF8.GetBytes(content));
    }

    /// <summary>Remove a file from the in-memory file system. Returns true if removed.</summary>
    public bool RemoveFile(string path)
    {
        return _files.Remove(Norm(path));
    }

    /// <summary>Get all file paths in the file system.</summary>
    public IEnumerable<string> GetAllFilePaths() => _files.Keys;

    /// <summary>Get all files as path->bytes pairs.</summary>
    public IReadOnlyDictionary<string, byte[]> GetAllFiles() => _files;

    /// <summary>Try to get file bytes. Returns false if not found.</summary>
    public bool TryGetFile(string path, out byte[] data)
    {
        return _files.TryGetValue(Norm(path), out data!);
    }

    /// <summary>Add a GML code entry.</summary>
    public void AddGmlEntry(string codeName, string gmlContent)
    {
        GmlEntries[codeName] = gmlContent;
    }

    /// <summary>Add an ASM code entry.</summary>
    public void AddAsmEntry(string codeName, string asmContent)
    {
        AsmEntries[codeName] = asmContent;
    }

    /// <summary>Remove a GML code entry. Returns true if removed.</summary>
    public bool RemoveGmlEntry(string codeName) => GmlEntries.Remove(codeName);

    /// <summary>Remove an ASM code entry. Returns true if removed.</summary>
    public bool RemoveAsmEntry(string codeName) => AsmEntries.Remove(codeName);

    /// <summary>Rebuild directory index after mutations. Must be called after Add/Remove operations before using directory APIs.</summary>
    public void RebuildDirectoryIndex()
    {
        _childDirs.Clear();
        _childFiles.Clear();
        BuildDirectoryIndex();
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Save the in-memory file system to a ZIP archive on disk.
    /// Includes all files, GML entries, and ASM entries.
    /// </summary>
    public void SaveToZip(string outputPath, G3MPatchManifest? manifest = null)
    {
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        using var zipStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        // Write manifest
        if (manifest != null)
        {
            var entry = archive.CreateEntry("g3mpatch.json", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            JsonSerializer.Serialize(stream, manifest, s_jsonOptions);
        }

        // Write all files
        foreach (var (path, data) in _files)
        {
            if (path.Equals("g3mpatch.json", StringComparison.OrdinalIgnoreCase) && manifest != null)
                continue; // Already written above
            var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(data, 0, data.Length);
        }

        // Write code entries
        foreach (var (codeName, gml) in GmlEntries)
        {
            var entryPath = $"CodeEntries/{codeName}/{codeName}.gml";
            var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(gml);
        }

        foreach (var (codeName, asm) in AsmEntries)
        {
            var entryPath = $"CodeEntries/{codeName}/{codeName}.asm";
            var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(asm);
        }
    }

    // === Resource helpers ===

    public bool HasResourceType(string resourceType) =>
        DirectoryExists(resourceType);

    public bool HasCodeEntries() =>
        GmlEntries.Count > 0 || AsmEntries.Count > 0;

    /// <summary>Get total number of files (excluding code entries).</summary>
    public int FileCount => _files.Count;

    /// <summary>Get total number of code entries (GML + ASM).</summary>
    public int CodeEntryCount => GmlEntries.Count + AsmEntries.Count;

    /// <summary>Release file data (keeps code entries). Call after all non-code imports are done.</summary>
    public void ReleaseFileData()
    {
        _files.Clear();
        _childDirs.Clear();
        _childFiles.Clear();
    }

    /// <summary>Release code entries. Call after CodeEntries import is done.</summary>
    public void ReleaseCodeEntries()
    {
        GmlEntries.Clear();
        AsmEntries.Clear();
    }

    /// <summary>Get all top-level resource type folders present in this archive.</summary>
    public HashSet<string> GetResourceTypes()
    {
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_childDirs.TryGetValue("", out var rootDirs))
        {
            foreach (var dir in rootDirs)
                types.Add(dir);
        }
        if (HasCodeEntries())
            types.Add("CodeEntries");
        return types;
    }
}
