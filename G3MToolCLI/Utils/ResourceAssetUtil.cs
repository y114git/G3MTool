using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Project;

namespace G3MToolCLI.Utils;

internal static class ResourceAssetUtil
{
    public static string? GetAssetNameByTagType(UndertaleData data, ResourceType type, int index) => type switch
    {
        ResourceType.Object when index >= 0 && index < data.GameObjects.Count => data.GameObjects[index]?.Name?.Content,
        ResourceType.Sprite when index >= 0 && index < data.Sprites.Count => data.Sprites[index]?.Name?.Content,
        ResourceType.Sound when index >= 0 && index < data.Sounds.Count => data.Sounds[index]?.Name?.Content,
        ResourceType.Room when index >= 0 && index < data.Rooms.Count => data.Rooms[index]?.Name?.Content,
        ResourceType.Path when index >= 0 && index < data.Paths.Count => data.Paths[index]?.Name?.Content,
        ResourceType.Script when index >= 0 && index < data.Scripts.Count => data.Scripts[index]?.Name?.Content,
        ResourceType.Font when index >= 0 && index < data.Fonts.Count => data.Fonts[index]?.Name?.Content,
        ResourceType.Timeline when index >= 0 && index < data.Timelines.Count => data.Timelines[index]?.Name?.Content,
        ResourceType.Background when index >= 0 && index < data.Backgrounds.Count => data.Backgrounds[index]?.Name?.Content,
        ResourceType.Shader when index >= 0 && index < data.Shaders.Count => data.Shaders[index]?.Name?.Content,
        ResourceType.Sequence when data.Sequences != null && index >= 0 && index < data.Sequences.Count => data.Sequences[index]?.Name?.Content,
        ResourceType.AnimCurve when data.AnimationCurves != null && index >= 0 && index < data.AnimationCurves.Count => data.AnimationCurves[index]?.Name?.Content,
        ResourceType.ParticleSystem when data.ParticleSystems != null && index >= 0 && index < data.ParticleSystems.Count => data.ParticleSystems[index]?.Name?.Content,
        _ => null
    };

    public static string? ResolveAudioGroupPath(UndertaleData data, string dataDir, int groupId)
    {
        string relativePath = $"audiogroup{groupId}.dat";
        if (data.AudioGroups != null && groupId >= 0 && groupId < data.AudioGroups.Count &&
            !string.IsNullOrWhiteSpace(data.AudioGroups[groupId]?.Path?.Content))
        {
            relativePath = data.AudioGroups[groupId].Path.Content;
        }

        try
        {
            string baseDir = Path.GetFullPath(dataDir);
            string fullPath = Path.GetFullPath(Path.Combine(baseDir, relativePath));
            return fullPath.StartsWith(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || string.Equals(fullPath, baseDir, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? ResolveAudioGroupPathFromDataFile(UndertaleData data, string dataPath, int groupId)
    {
        string? baseDir = Path.GetDirectoryName(dataPath);
        return string.IsNullOrWhiteSpace(baseDir) ? null : ResolveAudioGroupPath(data, baseDir, groupId);
    }

    public static ProjectContext CreateProjectContext(UndertaleData data, string root)
    {
        Directory.CreateDirectory(root);
        string project = Path.Combine(root, "project", "project.yy");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        return new ProjectContext(data, Path.Combine(root, "load.win"), Path.Combine(root, "save.win"), project, "G3MTool");
    }

    public static bool IsLocaleArtSpriteName(string spriteName) =>
        spriteName.StartsWith("spr_ja_", StringComparison.OrdinalIgnoreCase) ||
        spriteName.StartsWith("bg_lang_ja_", StringComparison.OrdinalIgnoreCase);
}
