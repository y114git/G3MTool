using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UndertaleModLib.Models;

namespace UndertaleModLib.Project;

/// <summary>
/// Public bridge for tools that need the built-in project asset serializers
/// without referencing internal Serializable* implementations directly.
/// </summary>
public static class SerializableProjectAssetBridge
{
    public static void Export(ProjectContext projectContext, IProjectAsset asset, string destinationFile)
    {
        ISerializableProjectAsset serializable = asset.GenerateSerializableProjectAsset(projectContext);
        serializable.Serialize(projectContext, destinationFile);
    }

    public static IProjectAsset Import(ProjectContext projectContext, string sourceFile)
    {
        ISerializableProjectAsset serializable = Load(sourceFile);
        serializable.PreImport(projectContext);
        return serializable.Import(projectContext);
    }

    public static IReadOnlyList<IProjectAsset> ImportMany(ProjectContext projectContext, IEnumerable<string> sourceFiles)
    {
        List<ISerializableProjectAsset> serializableAssets = [];
        foreach (string sourceFile in sourceFiles)
            serializableAssets.Add(Load(sourceFile));

        foreach (ISerializableProjectAsset asset in serializableAssets)
            asset.PreImport(projectContext);

        List<IProjectAsset> imported = new(serializableAssets.Count);
        foreach (ISerializableProjectAsset asset in serializableAssets)
            imported.Add(asset.Import(projectContext));

        return imported;
    }

    private static ISerializableProjectAsset Load(string sourceFile)
    {
        using FileStream fs = new(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<ISerializableProjectAsset>(fs, ProjectContext.JsonOptions)
            ?? throw new InvalidDataException($"Failed to deserialize project asset \"{sourceFile}\".");
    }
}
