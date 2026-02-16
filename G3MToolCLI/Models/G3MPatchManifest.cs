using System.Text.Json.Serialization;

namespace G3MToolCLI.Models;

public class G3MPatchManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("tool")]
    public ToolInfo? Tool { get; set; }

    [JsonPropertyName("original")]
    public DataFileInfo? Original { get; set; }

    [JsonPropertyName("modified")]
    public DataFileInfo? Modified { get; set; }

    [JsonPropertyName("resources")]
    public Dictionary<string, ResourceTypeChanges>? Resources { get; set; }

    [JsonPropertyName("statistics")]
    public PatchStatistics? Statistics { get; set; }
}

public class ToolInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

public class DataFileInfo
{
    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }

    [JsonPropertyName("bytecodeVersion")]
    public int BytecodeVersion { get; set; }

    [JsonPropertyName("gmsVersion")]
    public string? GmsVersion { get; set; }

    [JsonPropertyName("generalInfo")]
    public GeneralInfoData? GeneralInfo { get; set; }
}

public class GeneralInfoData
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }

    [JsonPropertyName("config")]
    public string? Config { get; set; }

    [JsonPropertyName("gameID")]
    public uint GameID { get; set; }

    [JsonPropertyName("directPlayGuid")]
    public string? DirectPlayGuid { get; set; }

    [JsonPropertyName("major")]
    public uint Major { get; set; }

    [JsonPropertyName("minor")]
    public uint Minor { get; set; }

    [JsonPropertyName("release")]
    public uint Release { get; set; }

    [JsonPropertyName("build")]
    public uint Build { get; set; }

    [JsonPropertyName("defaultWindowWidth")]
    public uint DefaultWindowWidth { get; set; }

    [JsonPropertyName("defaultWindowHeight")]
    public uint DefaultWindowHeight { get; set; }

    [JsonPropertyName("infoFlags")]
    public uint InfoFlags { get; set; }

    [JsonPropertyName("licenseCRC32")]
    public int LicenseCRC32 { get; set; }

    [JsonPropertyName("timestamp")]
    public ulong Timestamp { get; set; }

    [JsonPropertyName("activeTargets")]
    public ulong ActiveTargets { get; set; }

    [JsonPropertyName("functionClassifications")]
    public ulong FunctionClassifications { get; set; }

    [JsonPropertyName("steamAppID")]
    public int SteamAppID { get; set; }

    [JsonPropertyName("debuggerPort")]
    public int DebuggerPort { get; set; }

    [JsonPropertyName("gms2FPS")]
    public float GMS2FPS { get; set; }

    [JsonPropertyName("gms2AllowStatistics")]
    public bool GMS2AllowStatistics { get; set; }

    [JsonPropertyName("roomOrderCount")]
    public int RoomOrderCount { get; set; }
}

public class ResourceTypeChanges
{
    [JsonPropertyName("changed")]
    public List<ResourceChange>? Changed { get; set; }

    [JsonPropertyName("new")]
    public List<ResourceChange>? New { get; set; }

    [JsonPropertyName("deleted")]
    public List<string>? Deleted { get; set; }

    [JsonIgnore]
    public bool HasChanges => (Changed?.Count ?? 0) + (New?.Count ?? 0) + (Deleted?.Count ?? 0) > 0;
}

public class ResourceChange
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("files")]
    public Dictionary<string, string>? Files { get; set; }
}

public class PatchStatistics
{
    [JsonPropertyName("totalChanged")]
    public int TotalChanged { get; set; }

    [JsonPropertyName("totalNew")]
    public int TotalNew { get; set; }

    [JsonPropertyName("totalDeleted")]
    public int TotalDeleted { get; set; }

    [JsonPropertyName("totalChangedFiles")]
    public int TotalChangedFiles { get; set; }

    [JsonPropertyName("totalNewFiles")]
    public int TotalNewFiles { get; set; }
}
