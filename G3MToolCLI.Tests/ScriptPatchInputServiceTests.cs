using G3MLib.DataFile;
using G3MToolCLI.Services.Patching;

namespace G3MToolCLI.Tests;

public sealed class ScriptPatchInputServiceTests
{
    [Fact("ScriptPatchInputServiceTests.cs", 12)]
    public async Task MaterializeDataAcceptsValidDataAndRejectsCorruptData()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var sourcePath = Path.Combine(directory, "source.win");
            var corruptPath = Path.Combine(directory, "corrupt.win");
            using (var data = GameMakerData.CreateNew2026LTS())
            using (var stream = File.Create(sourcePath))
            {
                await GameMakerIO.WriteAsync(stream, data, cancellationToken: TestContext.Current.CancellationToken);
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
        var directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var sourcePath = Path.Combine(directory, "source.win");
            var unsupportedPath = Path.Combine(directory, "input.txt");
            using (var data = GameMakerData.CreateNew2026LTS())
            using (var stream = File.Create(sourcePath))
            {
                await GameMakerIO.WriteAsync(stream, data, cancellationToken: TestContext.Current.CancellationToken);
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
        var directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var sourcePath = Path.Combine(directory, "source.win");
            var scriptPath = Path.Combine(directory, "change.csx");
            using (var data = GameMakerData.CreateNew2026LTS())
            using (var stream = File.Create(sourcePath))
            {
                await GameMakerIO.WriteAsync(stream, data, cancellationToken: TestContext.Current.CancellationToken);
            }
            await File.WriteAllTextAsync(scriptPath, "Data.GeneralInfo!.LastObj = 777;", TestContext.Current.CancellationToken);

            var outputPath = await ScriptPatchInputService.MaterializeDataAsync(sourcePath, scriptPath, Path.Combine(directory, "output"));

            using var source = File.OpenRead(sourcePath);
            using var sourceData = await GameMakerIO.ReadAsync(source, null, cancellationToken: TestContext.Current.CancellationToken);
            using var output = File.OpenRead(outputPath);
            using var outputData = await GameMakerIO.ReadAsync(output, null, cancellationToken: TestContext.Current.CancellationToken);
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
