using G3MLib.DataFile;
using G3MLib.DataFile.Models;
using G3MToolCLI.Services.Execution;

namespace G3MToolCLI.Tests;

public class ExecuteServiceTests
{
    [Fact]
    public async Task BundledScriptsRestorePopulatedRoomsAndPaths()
    {
        var directory = Directory.CreateTempSubdirectory("g3mtool-resource-roundtrip-").FullName;
        try
        {
            var source = Path.Combine(directory, "source.win");
            var changed = Path.Combine(directory, "changed.win");
            var exportDirectory = Path.Combine(directory, "export");
            using (var data = GameMakerData.CreateNew2026LTS())
            {
                data.Rooms[0].Width = 1234;
                data.Rooms[0].Height = 567;
#pragma warning disable CA2000 // the data model owns what is added to it
                data.Paths.Add(new GameMakerPath
                {
                    Name = data.Strings.MakeString("route"),
                    IsClosed = true,
                    Precision = 7,
                    Points = [new() { X = 12.5f, Y = -7.25f, Speed = 80 }, new() { X = 42, Y = 19, Speed = 120 }]
                });
#pragma warning restore CA2000
                using (var stream = File.Create(source)) await GameMakerIO.WriteAsync(stream, data, cancellationToken: TestContext.Current.CancellationToken);
                data.Rooms[0].Width = 1;
                data.Rooms[0].Height = 1;
                data.Paths.Clear();
                using (var stream = File.Create(changed)) await GameMakerIO.WriteAsync(stream, data, cancellationToken: TestContext.Current.CancellationToken);
            }
            foreach (var type in new[] { "Rooms", "Paths" })
            {
                var resultPath = Path.Combine(directory, "restored-" + type + ".win");
                foreach (var operation in new[] { "Export", "Import" })
                {
                    var scriptName = operation + type + ".csx";
                    var script = Path.Combine(directory, scriptName);
                    using var stream = typeof(ExecuteService).Assembly.GetManifestResourceStream("G3MToolCLI.Assets.scripts." + scriptName)!;
                    using var reader = new StreamReader(stream);
                    await File.WriteAllTextAsync(script, await reader.ReadToEndAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
                    var importing = operation == "Import";
                    var result = await ExecuteService.ExecuteScriptAsync(script, importing ? changed : source,
                        importing ? resultPath : Path.Combine(exportDirectory, "data.win"), importing ? [Path.Combine(exportDirectory, type)] : []);
                    Assert.True(result.Success, result.Error);
                }
                using var restored = Read(resultPath);
                if (type == "Rooms")
                {
                    Assert.Equal(1234u, restored.Rooms[0].Width);
                    Assert.Equal(567u, restored.Rooms[0].Height);
                }
                else
                {
                    var route = Assert.Single(restored.Paths);
                    Assert.Equal("route", route.Name.Content);
                    Assert.True(route.IsClosed);
                    Assert.Equal(7u, route.Precision);
                    Assert.Equal(2, route.Points.Count);
                    Assert.Equal(12.5f, route.Points[0].X);
                    Assert.Equal(-7.25f, route.Points[0].Y);
                    Assert.Equal(120f, route.Points[1].Speed);
                }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavesDataCreatedOrReplacedByScript(bool withInput)
    {
        var directory = Directory.CreateTempSubdirectory("g3mtool-script-data-").FullName;
        try
        {
            var script = Path.Combine(directory, "create.csx");
            var output = Path.Combine(directory, "output.win");
            if (withInput)
            {
                using var original = GameMakerData.CreateNew2026LTS();
                using var stream = File.Create(output);
                await GameMakerIO.WriteAsync(stream, original, cancellationToken: TestContext.Current.CancellationToken);
            }
            await File.WriteAllTextAsync(script, "Data = GameMakerData.CreateNew2026LTS(); Data.GeneralInfo.LastObj = 456;", TestContext.Current.CancellationToken);
            var result = await ExecuteService.ExecuteScriptAsync(script, withInput ? output : null, output, []);
            Assert.True(result.Success, result.Error);
            using var saved = File.OpenRead(output);
            using var data = await GameMakerIO.ReadAsync(saved, null, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(456u, data.GeneralInfo.LastObj);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedSavePreservesOriginalData()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "input.win");
            var script = Path.Combine(directory, "invalid.csx");
            using (var data = GameMakerData.CreateNew2026LTS())
            using (var stream = File.Create(path))
                await GameMakerIO.WriteAsync(stream, data, cancellationToken: TestContext.Current.CancellationToken);
            var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(script, "Data.GeneralInfo.DisplayName.Content = null;", TestContext.Current.CancellationToken);

            var result = await ExecuteService.ExecuteScriptAsync(script, path, path, []);

            Assert.False(result.Success);
            Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(2, Directory.GetFiles(directory).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact("ExecuteServiceTests.cs", 13)]
    public async Task ExecutesLegacyNamespaceScriptThroughG3MLib()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, "input.win");
            var outputPath = Path.Combine(directory, "output.win");
            var scriptPath = Path.Combine(directory, "legacy.csx");
            using (var data = GameMakerData.CreateNew2026LTS())
            {
                using var stream = File.Create(inputPath);
                await GameMakerIO.WriteAsync(stream, data, cancellationToken: TestContext.Current.CancellationToken);
            }
            await File.WriteAllTextAsync(scriptPath, "using UndertaleModLib;\nusing UndertaleModLib.Models;\nData.GeneralInfo!.LastObj = 777;", TestContext.Current.CancellationToken);
            var result = await ExecuteService.ExecuteScriptAsync(scriptPath, inputPath, outputPath, []);
            Assert.True(result.Success, result.Error);
            using var output = Read(outputPath);
            Assert.Equal(777u, output.GeneralInfo.LastObj);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact("ExecuteServiceTests.cs", 43)]
    public async Task ExecutesLegacyMainThreadActionThroughG3MLib()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, "input.win");
            var outputPath = Path.Combine(directory, "output.win");
            var scriptPath = Path.Combine(directory, "legacy-main-thread.csx");
            using (var data = GameMakerData.CreateNew2026LTS())
            using (var stream = File.Create(inputPath))
            {
                await GameMakerIO.WriteAsync(stream, data, cancellationToken: TestContext.Current.CancellationToken);
            }
            await File.WriteAllTextAsync(scriptPath, "using UndertaleModLib.Compiler;\nCodeImportGroup group = new(Data) { MainThreadAction = MainThreadAction };\nMainThreadAction(() => Data.GeneralInfo!.LastObj = 778);", TestContext.Current.CancellationToken);
            var result = await ExecuteService.ExecuteScriptAsync(scriptPath, inputPath, outputPath, []);
            Assert.True(result.Success, result.Error);
            using var output = Read(outputPath);
            Assert.Equal(778u, output.GeneralInfo.LastObj);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact("ExecuteServiceTests.cs", 73)]
    public async Task RecompilesChangedScriptsAtTheSamePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, "input.win");
            var outputPath = Path.Combine(directory, "output.win");
            var scriptPath = Path.Combine(directory, "change.csx");
            using (var data = GameMakerData.CreateNew2026LTS())
            using (var stream = File.Create(inputPath))
            {
                await GameMakerIO.WriteAsync(stream, data, cancellationToken: TestContext.Current.CancellationToken);
            }

            await File.WriteAllTextAsync(scriptPath, "Data.GeneralInfo!.LastObj = 101;", TestContext.Current.CancellationToken);
            Assert.True((await ExecuteService.ExecuteScriptAsync(scriptPath, inputPath, outputPath, [])).Success);
            using (var firstOutput = Read(outputPath))
            {
                Assert.Equal(101u, firstOutput.GeneralInfo.LastObj);
            }

            await File.WriteAllTextAsync(scriptPath, "Data.GeneralInfo!.LastObj = 202;", TestContext.Current.CancellationToken);
            Assert.True((await ExecuteService.ExecuteScriptAsync(scriptPath, inputPath, outputPath, [])).Success);
            using var secondOutput = Read(outputPath);
            Assert.Equal(202u, secondOutput.GeneralInfo.LastObj);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact("ExecuteServiceTests.cs", 75)]
    public async Task ImportsExtensionWithoutFolderNameIntoValidData()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, "input.win");
            var outputPath = Path.Combine(directory, "output.win");
            var inputDirectory = Path.Combine(directory, "extensions");
            var extensionDirectory = Path.Combine(inputDirectory, "test_extension");
            var scriptPath = Path.Combine(directory, "ImportExtensions.csx");
            Directory.CreateDirectory(extensionDirectory);
            using (var data = GameMakerData.CreateNew2026LTS())
            using (var stream = File.Create(inputPath))
            {
                await GameMakerIO.WriteAsync(stream, data, cancellationToken: TestContext.Current.CancellationToken);
            }
            await File.WriteAllTextAsync(Path.Combine(extensionDirectory, "test_extension.json"), /*lang=json,strict*/ "{\"name\":\"test_extension\"}", TestContext.Current.CancellationToken);
            using var scriptStream = typeof(ExecuteService).Assembly.GetManifestResourceStream("G3MToolCLI.Assets.scripts.ImportExtensions.csx")!;
            using StreamReader reader = new(scriptStream);
            await File.WriteAllTextAsync(scriptPath, await reader.ReadToEndAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            var result = await ExecuteService.ExecuteScriptAsync(scriptPath, inputPath, outputPath, [inputDirectory]);
            Assert.True(result.Success, result.Error);
            using var output = Read(outputPath);
            Assert.Equal(string.Empty, Assert.Single(output.Extensions).FolderName.Content);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact("ExecuteServiceTests.cs", 80)]
    public async Task ExecutesEmbeddedLegacyExportScriptThroughG3MLib()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, "input.win");
            var outputPath = Path.Combine(directory, "output.win");
            var scriptPath = Path.Combine(directory, "ExportGeneralInfo.csx");
            using (var data = GameMakerData.CreateNew2026LTS())
            using (var stream = File.Create(inputPath))
            {
                await GameMakerIO.WriteAsync(stream, data, cancellationToken: TestContext.Current.CancellationToken);
            }
            var assembly = typeof(ExecuteService).Assembly;
            using var scriptStream = assembly.GetManifestResourceStream("G3MToolCLI.Assets.scripts.ExportGeneralInfo.csx")!;
            using StreamReader reader = new(scriptStream);
            await File.WriteAllTextAsync(scriptPath, await reader.ReadToEndAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            var result = await ExecuteService.ExecuteScriptAsync(scriptPath, inputPath, outputPath, []);
            Assert.True(result.Success, result.Error);
            Assert.True(File.Exists(outputPath));
            Assert.True(File.Exists(Path.Combine(directory, "GeneralInfo", "GeneralInfo.json")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact("ExecuteServiceTests.cs", 114)]
    public async Task ExecutesAllEmbeddedExportScriptsThroughG3MLib()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, "input.win");
            using (var data = GameMakerData.CreateNew2026LTS())
            using (var stream = File.Create(inputPath))
            {
                await GameMakerIO.WriteAsync(stream, data, cancellationToken: TestContext.Current.CancellationToken);
            }

            var assembly = typeof(ExecuteService).Assembly;
            string[] resources = [.. assembly.GetManifestResourceNames().Where(name => name.StartsWith("G3MToolCLI.Assets.scripts.Export", StringComparison.Ordinal) && name.EndsWith(".csx", StringComparison.Ordinal))];
            Assert.NotEmpty(resources);
            foreach (var resource in resources)
            {
                var name = Path.GetFileNameWithoutExtension(resource);
                var scriptPath = Path.Combine(directory, name + ".csx");
                var outputPath = Path.Combine(directory, name, "output.win");
                using var scriptStream = assembly.GetManifestResourceStream(resource)!;
                using StreamReader reader = new(scriptStream);
                await File.WriteAllTextAsync(scriptPath, await reader.ReadToEndAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

                var result = await ExecuteService.ExecuteScriptAsync(scriptPath, inputPath, outputPath, []);
                Assert.True(result.Success, $"{name}: {result.Error}");
                Assert.True(File.Exists(outputPath), name);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact("ExecuteServiceTests.cs", 156)]
    public async Task RoundTripsAllEmbeddedScriptsThroughG3MLib()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var sourcePath = Path.Combine(directory, "source.win");
            var exportDirectory = Path.Combine(directory, "export");
            using (var data = GameMakerData.CreateNew2026LTS())
            using (var stream = File.Create(sourcePath))
            {
                await GameMakerIO.WriteAsync(stream, data, cancellationToken: TestContext.Current.CancellationToken);
            }

            var assembly = typeof(ExecuteService).Assembly;
            string[] resources = [.. assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith("G3MToolCLI.Assets.scripts.", StringComparison.Ordinal) && name.EndsWith(".csx", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)];
            foreach (var resource in resources)
            {
                var name = Path.GetFileNameWithoutExtension(resource);
                var scriptPath = Path.Combine(directory, name + ".csx");
                using var scriptStream = assembly.GetManifestResourceStream(resource)!;
                using StreamReader reader = new(scriptStream);
                await File.WriteAllTextAsync(scriptPath, await reader.ReadToEndAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

                var isImport = name.Contains(".Import", StringComparison.Ordinal);
                var importIndex = name.LastIndexOf(".Import", StringComparison.Ordinal);
                var inputDirectory = isImport && name is not "G3MToolCLI.Assets.scripts.ImportAssetOrder" and not "G3MToolCLI.Assets.scripts.ImportTexturePageItems"
                    ? Path.Combine(exportDirectory, name[(importIndex + ".Import".Length)..])
                    : exportDirectory;
                if (isImport)
                {
                    Directory.CreateDirectory(inputDirectory);
                }
                var outputPath = isImport
                    ? Path.Combine(directory, "import", name + ".win")
                    : Path.Combine(exportDirectory, "data.win");
                var result = await ExecuteService.ExecuteScriptAsync(scriptPath, sourcePath, outputPath, isImport ? [inputDirectory] : []);
                Assert.True(result.Success, $"{name}: {result.Error}");
                Assert.True(File.Exists(outputPath), name);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static GameMakerData Read(string path)
    {
        using var stream = File.OpenRead(path);
        return GameMakerIO.Read(stream);
    }
}
