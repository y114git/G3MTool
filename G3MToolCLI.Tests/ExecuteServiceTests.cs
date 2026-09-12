using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using G3MLib.DataFile;
using G3MLib.DataFile.Models;
using G3MToolCLI.Services.Execution;
using Xunit;

namespace G3MToolCLI.Tests;

public class ExecuteServiceTests
{
    [Fact]
    public async Task BundledScriptsRestorePopulatedRoomsAndPaths()
    {
        string directory = Directory.CreateTempSubdirectory("g3mtool-resource-roundtrip-").FullName;
        try
        {
            string source = Path.Combine(directory, "source.win");
            string changed = Path.Combine(directory, "changed.win");
            string exportDirectory = Path.Combine(directory, "export");
            using (var data = GameMakerData.CreateNew2026LTS())
            {
                data.Rooms[0].Width = 1234;
                data.Rooms[0].Height = 567;
                data.Paths.Add(new GameMakerPath
                {
                    Name = data.Strings.MakeString("route"),
                    IsClosed = true,
                    Precision = 7,
                    Points = [new() { X = 12.5f, Y = -7.25f, Speed = 80 }, new() { X = 42, Y = 19, Speed = 120 }]
                });
                using (var stream = File.Create(source)) GameMakerIO.Write(stream, data);
                data.Rooms[0].Width = 1;
                data.Rooms[0].Height = 1;
                data.Paths.Clear();
                using (var stream = File.Create(changed)) GameMakerIO.Write(stream, data);
            }
            foreach (string type in new[] { "Rooms", "Paths" })
            {
                string resultPath = Path.Combine(directory, "restored-" + type + ".win");
                foreach (string operation in new[] { "Export", "Import" })
                {
                    string scriptName = operation + type + ".csx";
                    string script = Path.Combine(directory, scriptName);
                    using var stream = typeof(ExecuteService).Assembly.GetManifestResourceStream("G3MToolCLI.Assets.scripts." + scriptName)!;
                    using var reader = new StreamReader(stream);
                    await File.WriteAllTextAsync(script, await reader.ReadToEndAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
                    bool importing = operation == "Import";
                    ScriptResult result = await ExecuteService.ExecuteScriptAsync(script, importing ? changed : source,
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
        string directory = Directory.CreateTempSubdirectory("g3mtool-script-data-").FullName;
        try
        {
            string script = Path.Combine(directory, "create.csx");
            string output = Path.Combine(directory, "output.win");
            if (withInput)
            {
                using var original = GameMakerData.CreateNew2026LTS();
                using var stream = File.Create(output);
                GameMakerIO.Write(stream, original);
            }
            await File.WriteAllTextAsync(script, "Data = GameMakerData.CreateNew2026LTS(); Data.GeneralInfo.LastObj = 456;", TestContext.Current.CancellationToken);
            ScriptResult result = await ExecuteService.ExecuteScriptAsync(script, withInput ? output : null, output, []);
            Assert.True(result.Success, result.Error);
            using var saved = File.OpenRead(output);
            using var data = GameMakerIO.Read(saved);
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
        string directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "input.win");
            string script = Path.Combine(directory, "invalid.csx");
            using (GameMakerData data = GameMakerData.CreateNew2026LTS())
            using (FileStream stream = File.Create(path))
                GameMakerIO.Write(stream, data);
            byte[] original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(script, "Data.GeneralInfo.DisplayName.Content = null;", TestContext.Current.CancellationToken);

            ScriptResult result = await ExecuteService.ExecuteScriptAsync(script, path, path, []);

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
        string directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string inputPath = Path.Combine(directory, "input.win");
            string outputPath = Path.Combine(directory, "output.win");
            string scriptPath = Path.Combine(directory, "legacy.csx");
            using (GameMakerData data = GameMakerData.CreateNew2026LTS())
            {
                using FileStream stream = File.Create(inputPath);
                GameMakerIO.Write(stream, data);
            }
            await File.WriteAllTextAsync(scriptPath, "using UndertaleModLib;\nusing UndertaleModLib.Models;\nData.GeneralInfo!.LastObj = 777;", TestContext.Current.CancellationToken);
            ScriptResult result = await ExecuteService.ExecuteScriptAsync(scriptPath, inputPath, outputPath, []);
            Assert.True(result.Success, result.Error);
            using GameMakerData output = Read(outputPath);
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
        string directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string inputPath = Path.Combine(directory, "input.win");
            string outputPath = Path.Combine(directory, "output.win");
            string scriptPath = Path.Combine(directory, "legacy-main-thread.csx");
            using (GameMakerData data = GameMakerData.CreateNew2026LTS())
            using (FileStream stream = File.Create(inputPath))
            {
                GameMakerIO.Write(stream, data);
            }
            await File.WriteAllTextAsync(scriptPath, "using UndertaleModLib.Compiler;\nCodeImportGroup group = new(Data) { MainThreadAction = MainThreadAction };\nMainThreadAction(() => Data.GeneralInfo!.LastObj = 778);", TestContext.Current.CancellationToken);
            ScriptResult result = await ExecuteService.ExecuteScriptAsync(scriptPath, inputPath, outputPath, []);
            Assert.True(result.Success, result.Error);
            using GameMakerData output = Read(outputPath);
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
        string directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string inputPath = Path.Combine(directory, "input.win");
            string outputPath = Path.Combine(directory, "output.win");
            string scriptPath = Path.Combine(directory, "change.csx");
            using (GameMakerData data = GameMakerData.CreateNew2026LTS())
            using (FileStream stream = File.Create(inputPath))
            {
                GameMakerIO.Write(stream, data);
            }

            await File.WriteAllTextAsync(scriptPath, "Data.GeneralInfo!.LastObj = 101;", TestContext.Current.CancellationToken);
            Assert.True((await ExecuteService.ExecuteScriptAsync(scriptPath, inputPath, outputPath, [])).Success);
            using (GameMakerData firstOutput = Read(outputPath))
            {
                Assert.Equal(101u, firstOutput.GeneralInfo.LastObj);
            }

            await File.WriteAllTextAsync(scriptPath, "Data.GeneralInfo!.LastObj = 202;", TestContext.Current.CancellationToken);
            Assert.True((await ExecuteService.ExecuteScriptAsync(scriptPath, inputPath, outputPath, [])).Success);
            using GameMakerData secondOutput = Read(outputPath);
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
        string directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string inputPath = Path.Combine(directory, "input.win");
            string outputPath = Path.Combine(directory, "output.win");
            string inputDirectory = Path.Combine(directory, "extensions");
            string extensionDirectory = Path.Combine(inputDirectory, "test_extension");
            string scriptPath = Path.Combine(directory, "ImportExtensions.csx");
            Directory.CreateDirectory(extensionDirectory);
            using (GameMakerData data = GameMakerData.CreateNew2026LTS())
            using (FileStream stream = File.Create(inputPath))
            {
                GameMakerIO.Write(stream, data);
            }
            await File.WriteAllTextAsync(Path.Combine(extensionDirectory, "test_extension.json"), "{\"name\":\"test_extension\"}", TestContext.Current.CancellationToken);
            using Stream scriptStream = typeof(ExecuteService).Assembly.GetManifestResourceStream("G3MToolCLI.Assets.scripts.ImportExtensions.csx")!;
            using StreamReader reader = new(scriptStream);
            await File.WriteAllTextAsync(scriptPath, await reader.ReadToEndAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            ScriptResult result = await ExecuteService.ExecuteScriptAsync(scriptPath, inputPath, outputPath, [inputDirectory]);
            Assert.True(result.Success, result.Error);
            using GameMakerData output = Read(outputPath);
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
        string directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string inputPath = Path.Combine(directory, "input.win");
            string outputPath = Path.Combine(directory, "output.win");
            string scriptPath = Path.Combine(directory, "ExportGeneralInfo.csx");
            using (GameMakerData data = GameMakerData.CreateNew2026LTS())
            using (FileStream stream = File.Create(inputPath))
            {
                GameMakerIO.Write(stream, data);
            }
            Assembly assembly = typeof(ExecuteService).Assembly;
            using Stream scriptStream = assembly.GetManifestResourceStream("G3MToolCLI.Assets.scripts.ExportGeneralInfo.csx")!;
            using StreamReader reader = new(scriptStream);
            await File.WriteAllTextAsync(scriptPath, await reader.ReadToEndAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            ScriptResult result = await ExecuteService.ExecuteScriptAsync(scriptPath, inputPath, outputPath, []);
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
        string directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string inputPath = Path.Combine(directory, "input.win");
            using (GameMakerData data = GameMakerData.CreateNew2026LTS())
            using (FileStream stream = File.Create(inputPath))
            {
                GameMakerIO.Write(stream, data);
            }

            Assembly assembly = typeof(ExecuteService).Assembly;
            string[] resources = [.. assembly.GetManifestResourceNames().Where(name => name.StartsWith("G3MToolCLI.Assets.scripts.Export", StringComparison.Ordinal) && name.EndsWith(".csx", StringComparison.Ordinal))];
            Assert.NotEmpty(resources);
            foreach (string resource in resources)
            {
                string name = Path.GetFileNameWithoutExtension(resource);
                string scriptPath = Path.Combine(directory, name + ".csx");
                string outputPath = Path.Combine(directory, name, "output.win");
                using Stream scriptStream = assembly.GetManifestResourceStream(resource)!;
                using StreamReader reader = new(scriptStream);
                await File.WriteAllTextAsync(scriptPath, await reader.ReadToEndAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

                ScriptResult result = await ExecuteService.ExecuteScriptAsync(scriptPath, inputPath, outputPath, []);
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
        string directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "source.win");
            string exportDirectory = Path.Combine(directory, "export");
            using (GameMakerData data = GameMakerData.CreateNew2026LTS())
            using (FileStream stream = File.Create(sourcePath))
            {
                GameMakerIO.Write(stream, data);
            }

            Assembly assembly = typeof(ExecuteService).Assembly;
            string[] resources = [.. assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith("G3MToolCLI.Assets.scripts.", StringComparison.Ordinal) && name.EndsWith(".csx", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)];
            foreach (string resource in resources)
            {
                string name = Path.GetFileNameWithoutExtension(resource);
                string scriptPath = Path.Combine(directory, name + ".csx");
                using Stream scriptStream = assembly.GetManifestResourceStream(resource)!;
                using StreamReader reader = new(scriptStream);
                await File.WriteAllTextAsync(scriptPath, await reader.ReadToEndAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

                bool isImport = name.Contains(".Import", StringComparison.Ordinal);
                int importIndex = name.LastIndexOf(".Import", StringComparison.Ordinal);
                string inputDirectory = isImport && name is not "G3MToolCLI.Assets.scripts.ImportAssetOrder" and not "G3MToolCLI.Assets.scripts.ImportTexturePageItems"
                    ? Path.Combine(exportDirectory, name[(importIndex + ".Import".Length)..])
                    : exportDirectory;
                if (isImport)
                {
                    Directory.CreateDirectory(inputDirectory);
                }
                string outputPath = isImport
                    ? Path.Combine(directory, "import", name + ".win")
                    : Path.Combine(exportDirectory, "data.win");
                ScriptResult result = await ExecuteService.ExecuteScriptAsync(scriptPath, sourcePath, outputPath, isImport ? [inputDirectory] : []);
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
        using FileStream stream = File.OpenRead(path);
        return GameMakerIO.Read(stream);
    }
}
