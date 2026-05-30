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
        "GeneralInfo", "Options", "GlobalScripts", "Scripts", "Language", "FeatureFlags", "Tags", "FilterEffects", "AudioGroups", "EmbeddedAudio", "TextureGroupInfo", "EmbeddedTextures", "TexturePageItems", "EmbeddedImages",
        "Sprites", "Backgrounds", "Fonts", "Sounds", "Paths",
        "Tilesets", "Shaders", "Timelines", "GameObjects",
        "AnimationCurves", "ParticleSystemEmitters", "ParticleSystems", "Sequences", "Rooms", "CodeEntries", "Extensions"
    ];

    /// <summary>
    /// Import order - GameObjects MUST be before CodeEntries.
    /// CodeEntries MUST be last (PFS file data is released before it starts).
    /// </summary>
    public static readonly string[] ImportOrder =
    [
        "Options", "GlobalScripts", "Scripts", "Language", "FeatureFlags", "Tags", "FilterEffects", "AudioGroups", "EmbeddedAudio", "TextureGroupInfo", "EmbeddedTextures", "TexturePageItems", "EmbeddedImages",
        "Sprites", "Backgrounds", "Fonts", "Sounds", "Paths",
        "Tilesets", "Shaders", "Timelines", "GameObjects",
        "AnimationCurves", "ParticleSystemEmitters", "ParticleSystems", "Sequences", "Rooms", "Extensions", "GeneralInfo", "CodeEntries"
    ];

}
