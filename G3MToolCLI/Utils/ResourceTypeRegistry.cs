namespace G3MToolCLI.Utils;

/// <summary>
/// Single source of truth for all resource type names and import ordering.
/// </summary>
public static class ResourceTypeRegistry
{
    /// <summary>
    /// All resource types in standard export/comparison order.
    /// </summary>
    public static readonly string[] AllTypes =
    [
        "GeneralInfo", "AudioGroups", "TextureGroupInfo",
        "Sprites", "Backgrounds", "Fonts", "Sounds", "Paths",
        "Tilesets", "Shaders", "Timelines", "GameObjects",
        "Rooms", "CodeEntries", "Extensions"
    ];

    /// <summary>
    /// Import order - GameObjects MUST be before CodeEntries.
    /// CodeEntries MUST be last (PFS file data is released before it starts).
    /// </summary>
    public static readonly string[] ImportOrder =
    [
        "AudioGroups", "TextureGroupInfo",
        "Sprites", "Backgrounds", "Fonts", "Sounds", "Paths",
        "Tilesets", "Shaders", "Timelines", "GameObjects",
        "Rooms", "Extensions", "GeneralInfo", "CodeEntries"
    ];

}
