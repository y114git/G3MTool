using System.Text.Json.Serialization;
using G3MToolCLI.Services;

namespace G3MToolCLI.Models;

public sealed class G3MCacheOptions
{
    public static readonly G3MCacheOptions None = new();

    public string? ReadDirectory { get; init; }
    public string? WriteDirectory { get; init; }

    public bool CanRead => !string.IsNullOrWhiteSpace(ReadDirectory);
    public bool CanWrite => !string.IsNullOrWhiteSpace(WriteDirectory);

    public static G3MCacheOptions FromDirectory(string? directory) =>
        string.IsNullOrWhiteSpace(directory)
            ? None
            : new G3MCacheOptions { ReadDirectory = directory, WriteDirectory = directory };

    public static G3MCacheOptions FromDirectories(string? readDirectory, string? writeDirectory)
    {
        if (string.IsNullOrWhiteSpace(readDirectory) && string.IsNullOrWhiteSpace(writeDirectory))
            return None;

        return new G3MCacheOptions
        {
            ReadDirectory = string.IsNullOrWhiteSpace(readDirectory) ? null : readDirectory,
            WriteDirectory = string.IsNullOrWhiteSpace(writeDirectory) ? null : writeDirectory
        };
    }
}

public sealed class G3MDataAnalysisCache
{
    [JsonPropertyName("dataInfo")]
    public DataFileInfo DataInfo { get; set; } = new();

    [JsonPropertyName("resourceHashes")]
    public Dictionary<string, Dictionary<string, string>> ResourceHashes { get; set; } = [];

    [JsonPropertyName("resourceNameCounts")]
    public Dictionary<string, Dictionary<string, int>> ResourceNameCounts { get; set; } = [];

    [JsonPropertyName("orderSensitiveNames")]
    public Dictionary<string, List<string>> OrderSensitiveNames { get; set; } = [];

    [JsonPropertyName("infoSnapshot")]
    public G3MDataInfoSnapshot? InfoSnapshot { get; set; }
}

public sealed class G3MDataInfoSnapshot
{
    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("game")]
    public string Game { get; set; } = "Unknown";

    [JsonPropertyName("bytecodeVersion")]
    public int BytecodeVersion { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("generalInfo")]
    public GeneralInfoData? GeneralInfo { get; set; }

    [JsonPropertyName("resourceCounts")]
    public Dictionary<string, int> ResourceCounts { get; set; } = [];

    [JsonPropertyName("variablesByInstanceType")]
    public Dictionary<string, int> VariablesByInstanceType { get; set; } = [];

    [JsonPropertyName("firstFunction")]
    public string? FirstFunction { get; set; }

    [JsonPropertyName("lastFunction")]
    public string? LastFunction { get; set; }

    [JsonPropertyName("topLevelCodeCount")]
    public int TopLevelCodeCount { get; set; }

    [JsonPropertyName("childCodeCount")]
    public int ChildCodeCount { get; set; }

    [JsonPropertyName("audioGroups")]
    public List<string> AudioGroups { get; set; } = [];

    [JsonPropertyName("extensions")]
    public List<string> Extensions { get; set; } = [];

    [JsonPropertyName("roomOrderPreview")]
    public List<string> RoomOrderPreview { get; set; } = [];

    [JsonPropertyName("roomOrderCount")]
    public int RoomOrderCount { get; set; }
}

public sealed class G3MCacheManifest
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("O");

    [JsonPropertyName("tool")]
    public ToolInfo Tool { get; set; } = new() { Name = "G3MTool", Version = AppVersionService.Version };

    [JsonPropertyName("sourceKind")]
    public string SourceKind { get; set; } = "data";

    [JsonPropertyName("sourceFile")]
    public string SourceFile { get; set; } = "";

    [JsonPropertyName("sourceSize")]
    public long SourceSize { get; set; }

    [JsonPropertyName("sourceLastWriteUtcTicks")]
    public long SourceLastWriteUtcTicks { get; set; }

    [JsonPropertyName("sourceMd5")]
    public string? SourceMd5 { get; set; }
}
