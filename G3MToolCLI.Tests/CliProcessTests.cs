using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using G3MLib.DataFile;
using G3MLib.DataFile.Models;
using Xunit;

namespace G3MToolCLI.Tests;

public sealed class CliProcessTests
{
    [Fact]
    public async Task RelativeScriptPathsSupportNestedScriptsAndRejectEscapes()
    {
        string directory = Directory.CreateTempSubdirectory("g3mtool-nested-").FullName;
        try
        {
            string parent = Path.Combine(directory, "parent.csx");
            string child = Path.Combine(directory, "child.csx");
            string marker = Path.Combine(directory, "marker.txt");
            await File.WriteAllTextAsync(parent, "RunUMTScript(\"child.csx\");", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(child, "File.WriteAllText(Path.Combine(Path.GetDirectoryName(ScriptPath), \"marker.txt\"), \"ok\");", TestContext.Current.CancellationToken);
            CliResult result = await RunToolInDirectoryAsync(directory, "execute", "parent.csx");
            Assert.True(result.ExitCode == 0, result.StandardError);
            Assert.Equal("ok", await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken));
            await File.WriteAllTextAsync(parent, "RunUMTScript(\"../outside.csx\");", TestContext.Current.CancellationToken);
            result = await RunToolInDirectoryAsync(directory, "execute", "parent.csx");
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("escapes the mod directory", result.StandardError);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchMergeReservesDataPatchAndReportPaths(bool keepPatch)
    {
        string directory = Directory.CreateTempSubdirectory("g3mtool-merge-outputs-").FullName;
        try
        {
            string original = Path.Combine(directory, "original.win");
            string first = Path.Combine(directory, "first.win");
            string second = Path.Combine(directory, "second.win");
            string patch = Path.Combine(directory, "change.g3mpatch");
            string dataDirectory = Path.Combine(directory, "data");
            string patchDirectory = Path.Combine(directory, "patches");
            Directory.CreateDirectory(dataDirectory);
            Directory.CreateDirectory(patchDirectory);
            CreateRoomDataFile(original);
            ModifyDataFile(original, first, data => data.Rooms[1].Width = 123);
            ModifyDataFile(original, second, data => data.Rooms[2].Height = 456);
            Assert.Equal(0, (await RunToolAsync("patch", "create", original, first, patch)).ExitCode);
            string secondPatch = Path.Combine(directory, "other.g3mpatch");
            Assert.Equal(0, (await RunToolAsync("patch", "create", original, second, secondPatch)).ExitCode);
            string reportDirectory = keepPatch ? patchDirectory : dataDirectory;
            string reservedReport = Path.Combine(reportDirectory, "merge_001_change_other.merge_log.md");
            await File.WriteAllTextAsync(reservedReport, "previous report", TestContext.Current.CancellationToken);
            string reservedData = Path.Combine(dataDirectory, "merge_001_change_other_2.win");
            Directory.CreateDirectory(reservedData);
            string[] args = ["--json", "patch", "batch", "merge", original, patch + "," + secondPatch, patch + "," + secondPatch, "--apply", dataDirectory, "--report"];
            if (keepPatch) args = [.. args, "--out", patchDirectory];
            var saved = new System.Collections.Generic.Dictionary<string, byte[]>();
            for (int run = 0; run < 2; run++)
            {
                CliResult result = await RunToolAsync(args);
                Assert.True(result.ExitCode == 0, result.StandardError + result.StandardOutput);
                using var json = JsonDocument.Parse(result.StandardOutput);
                Assert.Equal(1, json.RootElement.GetProperty("deduplicated").GetInt32());
                foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
                {
                    string[] outputs = item.GetProperty("outputs").EnumerateArray().Select(value => value.GetString()!).ToArray();
                    Assert.Equal(keepPatch ? 3 : 2, outputs.Length);
                    using var data = ReadDataFile(outputs[0]);
                    Assert.Equal(123u, data.Rooms[1].Width);
                    Assert.Equal(456u, data.Rooms[2].Height);
                    Assert.Equal(reportDirectory, Path.GetDirectoryName(outputs[^1]));
                    foreach (string output in outputs)
                    {
                        Assert.False(saved.ContainsKey(output), "Previous output was reused: " + output);
                        saved.Add(output, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
                    }
                }
                foreach (var entry in saved) Assert.Equal(entry.Value, await File.ReadAllBytesAsync(entry.Key, TestContext.Current.CancellationToken));
            }
            Assert.Equal("previous report", await File.ReadAllTextAsync(reservedReport, TestContext.Current.CancellationToken));
            Assert.True(Directory.Exists(reservedData));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task BatchScriptsUseTheirOwnFilesAndPreservePreviousOutputs()
    {
        string directory = Directory.CreateTempSubdirectory("g3mtool-batch-scripts-").FullName;
        try
        {
            string original = Path.Combine(directory, "original.win");
            string output = Path.Combine(directory, "output");
            CreateRoomDataFile(original);
            string[] scripts = new string[2];
            for (int i = 0; i < 2; i++)
            {
                string folder = Path.Combine(directory, i.ToString());
                Directory.CreateDirectory(folder);
                scripts[i] = Path.Combine(folder, "change.csx");
                await File.WriteAllTextAsync(scripts[i], "Data.Rooms[1].Width = uint.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(ScriptPath), \"width.txt\")));", TestContext.Current.CancellationToken);
                await File.WriteAllTextAsync(Path.Combine(folder, "width.txt"), (100 + i).ToString(), TestContext.Current.CancellationToken);
            }
            for (int i = 0; i < 2; i++)
            {
                CliResult result = await RunToolAsync("patch", "batch", "apply", original, scripts[0], scripts[1], "--out-dir", output);
                Assert.True(result.ExitCode == 0, result.StandardError + result.StandardOutput);
            }
            string[] outputs = Directory.GetFiles(output, "*.win");
            Assert.Equal(4, outputs.Length);
            var widths = outputs.Select(path => { using var data = ReadDataFile(path); return data.Rooms[1].Width; }).Order().ToArray();
            Assert.Equal(new uint[] { 100, 100, 101, 101 }, widths);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task InteractiveModeExitsWhenInputCloses()
    {
        ProcessStartInfo startInfo = new(ToolPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using Process process = Process.Start(startInfo)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("G3MTool", await output);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task ScriptInputsWorkInPatchAndBatchCommands()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string original = Path.Combine(directory, "original.win");
            string script = Path.Combine(directory, "change.csx");
            string output = Path.Combine(directory, "output.win");
            using (GameMakerData data = GameMakerData.CreateNew2026LTS())
            using (FileStream stream = File.Create(original))
                GameMakerIO.Write(stream, data);
            await File.WriteAllTextAsync(script, "Data.GeneralInfo.LastObj = 321;", TestContext.Current.CancellationToken);
            string[][] commands =
            [
                ["patch", "apply", original, script, output],
                ["patch", "batch", "create", original, script, "--out-dir", Path.Combine(directory, "create")],
                ["patch", "batch", "apply", original, script, "--out-dir", Path.Combine(directory, "apply")],
                ["patch", "merge", original, script, output, "--apply", Path.Combine(directory, "merged.win")],
                ["patch", "batch", "merge", original, script + "," + output, "--apply", Path.Combine(directory, "merge")]
            ];
            foreach (string[] command in commands)
            {
                CliResult result = await RunToolAsync(command);
                Assert.True(result.ExitCode == 0, result.StandardError + result.StandardOutput);
            }
            using GameMakerData applied = ReadDataFile(output);
            Assert.Equal(321u, applied.GeneralInfo.LastObj);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact("CliProcessTests.cs", 13)]
    public async Task CliVersionInfoAndPatchCommandsRoundTripData()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string originalPath = Path.Combine(directory, "original.win");
            string modifiedPath = Path.Combine(directory, "modified.win");
            string patchPath = Path.Combine(directory, "change.g3mpatch");
            string outputPath = Path.Combine(directory, "output.win");
            using (GameMakerData original = GameMakerData.CreateNew2026LTS())
            using (FileStream stream = File.Create(originalPath))
            {
                GameMakerIO.Write(stream, original);
            }
            using (FileStream stream = File.OpenRead(originalPath))
            using (GameMakerData modified = GameMakerIO.Read(stream))
            {
                modified.GeneralInfo.DisplayName = modified.Strings.MakeString("CLI coverage");
                using FileStream output = File.Create(modifiedPath);
                GameMakerIO.Write(output, modified);
            }

            CliResult version = await RunToolAsync("--version");
            Assert.Equal(0, version.ExitCode);
            Assert.NotEmpty(version.StandardOutput.Trim());

            CliResult info = await RunToolAsync("--json", "info", originalPath);
            Assert.Equal(0, info.ExitCode);
            using (JsonDocument document = JsonDocument.Parse(info.StandardOutput))
            {
                Assert.Equal(Path.GetFileName(originalPath), document.RootElement.GetProperty("File").GetString());
                Assert.Equal(16, document.RootElement.GetProperty("BytecodeVersion").GetInt32());
            }

            Assert.Equal(0, (await RunToolAsync("patch", "create", originalPath, modifiedPath, patchPath)).ExitCode);
            Assert.True(File.Exists(patchPath));
            Assert.Equal(0, (await RunToolAsync("patch", "validate", patchPath, "--data", originalPath)).ExitCode);
            Assert.Equal(0, (await RunToolAsync("patch", "apply", originalPath, patchPath, outputPath)).ExitCode);

            using FileStream resultStream = File.OpenRead(outputPath);
            using GameMakerData result = GameMakerIO.Read(resultStream);
            Assert.Equal("CLI coverage", result.GeneralInfo.DisplayName.Content);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact("CliProcessTests.cs", 68)]
    public async Task CliReportsMissingDataFilesWithFailureExitCode()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.win");

        CliResult result = await RunToolAsync("info", missingPath);

        Assert.NotEqual(0, result.ExitCode);
        string output = result.StandardOutput + result.StandardError;
        Assert.Contains("Error reading data file", output, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(missingPath), output, StringComparison.Ordinal);
    }

    [Fact("CliProcessTests.cs", 80)]
    public async Task CliMergeAndBatchCommandsCombineIndependentChanges()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string originalPath = Path.Combine(directory, "original.win");
            string firstPath = Path.Combine(directory, "first.win");
            string secondPath = Path.Combine(directory, "second.win");
            string firstPatchPath = Path.Combine(directory, "first.g3mpatch");
            string secondPatchPath = Path.Combine(directory, "second.g3mpatch");
            string mergedPath = Path.Combine(directory, "merged.win");
            string mergedPatchPath = Path.Combine(directory, "merged.g3mpatch");
            string mergeReportPath = Path.Combine(directory, "merged.md");
            string batchCreateDirectory = Path.Combine(directory, "batch-create");
            string batchApplyDirectory = Path.Combine(directory, "batch-apply");
            string batchMergeDataDirectory = Path.Combine(directory, "batch-merge-data");
            string batchMergePatchDirectory = Path.Combine(directory, "batch-merge-patches");
            CreateRoomDataFile(originalPath);
            ModifyDataFile(originalPath, firstPath, data => data.Rooms.ByName("room_first").Width = 111u);
            ModifyDataFile(originalPath, secondPath, data => data.Rooms.ByName("room_second").Height = 222u);

            Assert.Equal(0, (await RunToolAsync("patch", "create", originalPath, firstPath, firstPatchPath)).ExitCode);
            Assert.Equal(0, (await RunToolAsync("patch", "create", originalPath, secondPath, secondPatchPath)).ExitCode);
            Assert.Equal(0, (await RunToolAsync("patch", "merge", originalPath, firstPatchPath, secondPatchPath, "--apply", mergedPath, "--out", mergedPatchPath, "--report", mergeReportPath)).ExitCode);
            Assert.True(File.Exists(mergedPatchPath));
            Assert.True(File.Exists(mergeReportPath));
            using (GameMakerData merged = ReadDataFile(mergedPath))
            {
                Assert.Equal(111u, merged.Rooms.ByName("room_first").Width);
                Assert.Equal(222u, merged.Rooms.ByName("room_second").Height);
            }

            Assert.Equal(0, (await RunToolAsync("patch", "batch", "create", originalPath, firstPath, secondPath, "--out-dir", batchCreateDirectory)).ExitCode);
            string[] batchPatches = [.. Directory.EnumerateFiles(batchCreateDirectory, "*.g3mpatch").OrderBy(path => path, StringComparer.Ordinal)];
            Assert.Equal(2, batchPatches.Length);
            Assert.Equal(0, (await RunToolAsync("patch", "batch", "apply", originalPath, batchPatches[0], batchPatches[1], "--out-dir", batchApplyDirectory)).ExitCode);
            Assert.Equal(2, Directory.EnumerateFiles(batchApplyDirectory, "*.win").Count());

            string mergeSet = string.Join(',', firstPatchPath, secondPatchPath);
            Assert.Equal(0, (await RunToolAsync("patch", "batch", "merge", originalPath, mergeSet, "--apply", batchMergeDataDirectory, "--out", batchMergePatchDirectory, "--report")).ExitCode);
            Assert.Single(Directory.EnumerateFiles(batchMergeDataDirectory, "*.win"));
            Assert.Single(Directory.EnumerateFiles(batchMergePatchDirectory, "*.g3mpatch"));
            Assert.Single(Directory.EnumerateFiles(batchMergePatchDirectory, "*.merge_log.md"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact("CliProcessTests.cs", 134)]
    public async Task CliDiffAndExecuteCommandsExposeProcessResults()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string originalPath = Path.Combine(directory, "original.win");
            string modifiedPath = Path.Combine(directory, "modified.win");
            string diffDirectory = Path.Combine(directory, "diff");
            CreateRoomDataFile(originalPath);
            ModifyDataFile(originalPath, modifiedPath, data => data.Rooms.ByName("room_first").Width = 333u);

            CliResult diff = await RunToolAsync("--json", "diff", originalPath, modifiedPath, diffDirectory, "--full");
            Assert.Equal(0, diff.ExitCode);
            using (JsonDocument document = JsonDocument.Parse(diff.StandardOutput))
            {
                Assert.True(File.Exists(document.RootElement.GetProperty("output").GetString()));
                Assert.True(document.RootElement.GetProperty("differences").GetInt32() > 0);
            }

            CliResult execute = await RunToolAsync("execute", ToolPath, "--", "--version");
            Assert.Equal(0, execute.ExitCode);
            Assert.NotEmpty(execute.StandardOutput.Trim());
            CliResult xdelta = await RunToolAsync("execute", "xdelta", "--", "-V");
            Assert.Equal(0, xdelta.ExitCode);
            Assert.Contains("Xdelta version", xdelta.StandardOutput);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact("CliProcessTests.cs", 171)]
    public async Task CliXpatchRoundTripsArbitraryFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"G3MToolTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string originalPath = Path.Combine(directory, "original.bin");
            string modifiedPath = Path.Combine(directory, "modified.bin");
            string patchPath = Path.Combine(directory, "change.xdelta");
            string outputPath = Path.Combine(directory, "output.bin");
            await File.WriteAllBytesAsync(originalPath, [1, 2, 3, 4, 5], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(modifiedPath, [1, 2, 8, 4, 5, 6, 7], TestContext.Current.CancellationToken);

            Assert.Equal(0, (await RunToolAsync("xpatch", "create", originalPath, modifiedPath, patchPath)).ExitCode);
            Assert.Equal(0, (await RunToolAsync("xpatch", "apply", originalPath, patchPath, outputPath)).ExitCode);
            Assert.Equal(await File.ReadAllBytesAsync(modifiedPath, TestContext.Current.CancellationToken), await File.ReadAllBytesAsync(outputPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void CreateRoomDataFile(string path)
    {
        using GameMakerData data = GameMakerData.CreateNew2026LTS();
        data.Rooms.Add(new GameMakerRoom { Name = data.Strings.MakeString("room_first") });
        data.Rooms.Add(new GameMakerRoom { Name = data.Strings.MakeString("room_second") });
        using FileStream stream = File.Create(path);
        GameMakerIO.Write(stream, data);
    }

    private static void ModifyDataFile(string sourcePath, string outputPath, Action<GameMakerData> modify)
    {
        using FileStream source = File.OpenRead(sourcePath);
        using GameMakerData data = GameMakerIO.Read(source);
        modify(data);
        using FileStream output = File.Create(outputPath);
        GameMakerIO.Write(output, data);
    }

    private static GameMakerData ReadDataFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return GameMakerIO.Read(stream);
    }

    private static string ToolPath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "G3MTool.exe" : "G3MTool");

    private static Task<CliResult> RunToolAsync(params string[] arguments) => RunToolInDirectoryAsync(Environment.CurrentDirectory, arguments);

    private static async Task<CliResult> RunToolInDirectoryAsync(string directory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = ToolPath,
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start G3MTool CLI.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new CliResult(process.ExitCode, await standardOutput, await standardError);
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}
