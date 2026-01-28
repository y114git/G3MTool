namespace G3MToolCLI.Models;

public class ResourceInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public long Size { get; set; }
    public List<string> Files { get; set; } = new();
}

public enum ResourceState
{
    Unchanged,
    Changed,
    New,
    Deleted
}
