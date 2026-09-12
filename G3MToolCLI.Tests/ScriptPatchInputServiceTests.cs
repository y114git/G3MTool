using System;
using System.IO;
using System.Threading.Tasks;
using G3MLib.DataFile;
using G3MToolCLI.Services.Patching;
using Xunit;

namespace G3MToolCLI.Tests;

public sealed class ScriptPatchInputServiceTests
{
    [Fact("ScriptPatchInputServiceTests.cs", 12)]
    public async Task MaterializeDataAcceptsValidDataAndRejectsCorruptData()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "source.win");
            string corruptPath = Path.Combine(directory, "corrupt.win");
            using (GameMakerData data = GameMakerData.CreateNew2026LTS())
            using (FileStream stream = File.Create(sourcePath))
            {
                GameMakerIO.Write(stream, data);
            }
            await File.WriteAllTextAsync(corruptPath, "not a GameMaker data file", TestContext.Current.CancellationToken);

            Assert.Equal(sourcePath, await ScriptPatchInputService.MaterializeDataAsync(sourcePath, sourcePath, directory));
            await Assert.ThrowsAsync<InvalidDataException>(() => ScriptPatchInputService.MaterializeDataAsync(sourcePath, corruptPath, directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact("ScriptPatchInputServiceTests.cs", 39)]
    public async Task MaterializeDataRejectsUnsupportedInputsWithoutProducingData()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "source.win");
            string unsupportedPath = Path.Combine(directory, "input.txt");
            using (GameMakerData data = GameMakerData.CreateNew2026LTS())
            using (FileStream stream = File.Create(sourcePath))
            {
                GameMakerIO.Write(stream, data);
            }
            await File.WriteAllTextAsync(unsupportedPath, "input", TestContext.Current.CancellationToken);

            Assert.False(ScriptPatchInputService.IsXDelta(unsupportedPath));
            await Assert.ThrowsAsync<NotSupportedException>(() => ScriptPatchInputService.MaterializeDataAsync(sourcePath, unsupportedPath, Path.Combine(directory, "output")));
            Assert.Empty(Directory.GetFiles(Path.Combine(directory, "output")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact("ScriptPatchInputServiceTests.cs", 66)]
    public async Task MaterializeScriptAppliesChangesWithoutChangingTheSource()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "source.win");
            string scriptPath = Path.Combine(directory, "change.csx");
            using (GameMakerData data = GameMakerData.CreateNew2026LTS())
            using (FileStream stream = File.Create(sourcePath))
            {
                GameMakerIO.Write(stream, data);
            }
            await File.WriteAllTextAsync(scriptPath, "Data.GeneralInfo!.LastObj = 777;", TestContext.Current.CancellationToken);

            string outputPath = await ScriptPatchInputService.MaterializeDataAsync(sourcePath, scriptPath, Path.Combine(directory, "output"));

            using FileStream source = File.OpenRead(sourcePath);
            using GameMakerData sourceData = GameMakerIO.Read(source);
            using FileStream output = File.OpenRead(outputPath);
            using GameMakerData outputData = GameMakerIO.Read(output);
            Assert.NotEqual(777u, sourceData.GeneralInfo.LastObj);
            Assert.Equal(777u, outputData.GeneralInfo.LastObj);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
