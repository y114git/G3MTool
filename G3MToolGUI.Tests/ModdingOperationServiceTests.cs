using G3MToolGUI.Services;
using G3MLib.DataFile;
using G3MLib.DataFile.Models;
using G3MLib.Modding.XDelta;

namespace G3MToolGUI.Tests;

public sealed class ModdingOperationServiceTests
{
    [Fact]
    public async Task BatchMergePreservesExistingReportsAndRepeatedOutputs()
    {
        var directory = Directory.CreateTempSubdirectory("g3mtool-gui-merge-outputs-").FullName;
        try
        {
            var original = Path.Combine(directory, "original.win");
            CreateRoomData(original);
            var first = Path.Combine(directory, "first.csx");
            var second = Path.Combine(directory, "second.csx");
            await File.WriteAllTextAsync(first, "Data.Rooms[1].Width = 123;", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(second, "Data.Rooms[1].Height = 456;", TestContext.Current.CancellationToken);
            var dataDirectory = Path.Combine(directory, "data");
            var patchDirectory = Path.Combine(directory, "patches");
            Directory.CreateDirectory(patchDirectory);
            var report = Path.Combine(patchDirectory, "merge_01.merge.md");
            await File.WriteAllTextAsync(report, "previous report", TestContext.Current.CancellationToken);
            var request = new ModdingOperationRequest
            {
                Operation = ModdingOperation.BatchMerge,
                OriginalPath = original,
                InputPaths = [first + "," + second],
                PrimaryPath = dataDirectory,
                OutputPath = patchDirectory,
                FullReport = true
            };
            using var service = new ModdingOperationService();
            var saved = new Dictionary<string, byte[]>();
            for (var run = 0; run < 2; run++)
            {
                var result = await service.RunAsync(request, TestContext.Current.CancellationToken);
                Assert.True(result.Success, result.Summary);
                foreach (var output in result.Outputs)
                {
                    Assert.False(saved.ContainsKey(output));
                    saved.Add(output, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
                }
                foreach (var entry in saved) Assert.Equal(entry.Value, await File.ReadAllBytesAsync(entry.Key, TestContext.Current.CancellationToken));
            }
            Assert.Equal("previous report", await File.ReadAllTextAsync(report, TestContext.Current.CancellationToken));
            Assert.Equal(2, Directory.GetFiles(dataDirectory, "*.win").Length);
            Assert.Equal(3, Directory.GetFiles(patchDirectory, "*.md").Length);
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
            var result = await ScriptExecutionService.RunAsync(script, withInput ? output : null, output, []);
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
    public void ScriptQuestionsRequireUserInput()
    {
        var globals = new ScriptGlobals(null, "", "script.csx", null, null);
        Assert.Throws<G3MLib.DataFile.Scripting.ScriptException>(() => globals.ScriptQuestion("Overwrite files?"));
        Assert.Throws<G3MLib.DataFile.Scripting.ScriptException>(() => globals.ScriptInputDialog("Input", "Path", "default", false, true));
        globals.SetProgressBar(null, "test", 2, 100);
        Parallel.For(0, 20, _ => globals.IncrementProgressParallel());
        Assert.Equal(22, globals.GetProgress());
    }

    [Fact]
    public async Task ScriptDialogsWaitForTheHostAndReturnItsSelections()
    {
        var directory = Directory.CreateTempSubdirectory("g3mtool-dialogs-").FullName;
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var script = Path.Combine(directory, "dialogs.csx");
            await File.WriteAllTextAsync(script, """
                if (ScriptQuestion("Continue?")) throw new Exception("Expected No");
                if (ScriptInputDialog("Title", "Label", "Default", false, true) != "value") throw new Exception("Input");
                if (PromptLoadFile("All|*", "") != "input.win") throw new Exception("Open");
                if (PromptSaveFile("All|*", "") != "output.win") throw new Exception("Save");
                if (PromptChooseDirectory() != null) throw new Exception("Cancel");
                """, TestContext.Current.CancellationToken);
            var interaction = new ScriptInteraction
            {
                Question = _ => { requested.TrySetResult(); return response.Task; },
                Input = (_, _, _, _) => Task.FromResult<string?>("value"),
                OpenFile = (_, _) => Task.FromResult<string?>("input.win"),
                SaveFile = (_, _) => Task.FromResult<string?>("output.win"),
                ChooseDirectory = () => Task.FromResult<string?>(null)
            };
            var run = Task.Run(() => ScriptExecutionService.RunAsync(script, null, null, [], interaction), TestContext.Current.CancellationToken);
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.False(run.IsCompleted);
            response.SetResult(false);
            var result = await run;
            Assert.True(result.Success, result.Error);
        }
        finally { response.TrySetResult(false); Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ScriptPatchesWorkInApplyMergeAndContinueAfterBatchFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "g3mtool-gui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var original = Path.Combine(directory, "original.win");
            var script = Path.Combine(directory, "change.csx");
            var invalid = Path.Combine(directory, "invalid.csx");
            var applied = Path.Combine(directory, "applied.win");
            var merged = Path.Combine(directory, "merged.win");
            CreateRoomData(original);
            await File.WriteAllTextAsync(script, "Data.Rooms[1].Width = 123;", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(invalid, "throw new Exception(\"invalid input\");", TestContext.Current.CancellationToken);
            using ModdingOperationService service = new();

            var result = await service.RunAsync(new ModdingOperationRequest { Operation = ModdingOperation.ApplyPatch, OriginalPath = original, PrimaryPath = script, OutputPath = applied }, TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Summary);
            using (var stream = File.OpenRead(applied))
            using (var data = await GameMakerIO.ReadAsync(stream, null, cancellationToken: TestContext.Current.CancellationToken))
                Assert.Equal(123u, data.Rooms[1].Width);

            result = await service.RunAsync(new ModdingOperationRequest { Operation = ModdingOperation.MergePatches, OriginalPath = original, InputPaths = [script, applied], PrimaryPath = merged }, TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Summary);
            Assert.True(File.Exists(merged));

            result = await service.RunAsync(new ModdingOperationRequest { Operation = ModdingOperation.BatchCreate, OriginalPath = original, InputPaths = [invalid, script], OutputPath = Path.Combine(directory, "batch"), ContinueOnError = true }, TestContext.Current.CancellationToken);
            Assert.False(result.Success);
            Assert.Contains("1/2 completed; 1 failed", result.Summary, StringComparison.Ordinal);
            Assert.Single(result.Outputs);
            Assert.True(File.Exists(result.Outputs[0]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedSavePreservesOriginalData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "g3mtool-gui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "input.win");
            var script = Path.Combine(directory, "invalid.csx");
            CreateRoomData(path);
            var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(script, "Data.GeneralInfo.DisplayName.Content = null;", TestContext.Current.CancellationToken);

            var result = await ScriptExecutionService.RunAsync(script, path, path, []);

            Assert.False(result.Success);
            Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(2, Directory.GetFiles(directory).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CreatePatchRejectsMissingInputWithoutWritingOutput()
    {
        var output = Path.Combine(Path.GetTempPath(), "g3mtool-gui-tests", Guid.NewGuid() + ".g3mpatch");
        using ModdingOperationService service = new();

        var result = await service.RunAsync(new ModdingOperationRequest
        {
            Operation = ModdingOperation.CreatePatch,
            OriginalPath = "missing-original.win",
            PrimaryPath = "missing-modified.win",
            OutputPath = output
        }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ScriptExecutionRunsWithoutDataFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "g3mtool-gui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "script.csx");
        await File.WriteAllTextAsync(script, "ScriptMessage(\"GUI script test\");", TestContext.Current.CancellationToken);
        try
        {
            using ModdingOperationService service = new();
            var result = await service.RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.RunScript,
                PrimaryPath = script
            }, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Summary);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScriptExecutionSupportsLegacyDataScripts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "g3mtool-gui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "input.win");
            var output = Path.Combine(directory, "output.win");
            var script = Path.Combine(directory, "script.csx");
            using (var data = GameMakerData.CreateNew2026LTS())
            using (var stream = File.Create(input))
            {
                await GameMakerIO.WriteAsync(stream, data, cancellationToken: TestContext.Current.CancellationToken);
            }
            await File.WriteAllTextAsync(script, "using UndertaleModLib;\nData.GeneralInfo!.LastObj = 777;", TestContext.Current.CancellationToken);

            using ModdingOperationService service = new();
            var result = await service.RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.RunScript,
                OriginalPath = input,
                PrimaryPath = script,
                OutputPath = output
            }, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Summary);
            using var resultStream = File.OpenRead(output);
            using var resultData = await GameMakerIO.ReadAsync(resultStream, null, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(777u, resultData.GeneralInfo.LastObj);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExternalProgramReceivesArgumentsWithoutShellParsing()
    {
        var executable = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec")! : "/bin/sh";
        string[] arguments = OperatingSystem.IsWindows() ? ["/c", "echo stdout& echo stderr 1>&2"] : ["-c", "echo stdout; echo stderr >&2"];
        using ModdingOperationService service = new();
        var messages = new System.Collections.Concurrent.ConcurrentBag<string>();
        service.LogReceived += message => messages.Add(message.Message.Trim());

        var result = await service.RunAsync(new ModdingOperationRequest
        {
            Operation = ModdingOperation.RunProgram,
            PrimaryPath = executable,
            InputPaths = arguments
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Contains("stdout", messages);
        Assert.Contains("stderr", messages);
    }

    [Fact]
    public async Task PatchMergeCompareAndBatchOperationsRoundTripData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "g3mtool-gui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var original = Path.Combine(directory, "original.win");
            var first = Path.Combine(directory, "first.win");
            var second = Path.Combine(directory, "second.win");
            var firstPatch = Path.Combine(directory, "first.g3mpatch");
            var secondPatch = Path.Combine(directory, "second.g3mpatch");
            var mergedData = Path.Combine(directory, "merged.win");
            var mergedPatch = Path.Combine(directory, "merged.g3mpatch");
            var report = Path.Combine(directory, "diff.md");
            var batchDirectory = Path.Combine(directory, "batch-create");
            var batchApplyDirectory = Path.Combine(directory, "batch-apply");
            var batchMergeDataDirectory = Path.Combine(directory, "batch-merge-data");
            var batchMergePatchDirectory = Path.Combine(directory, "batch-merge-patches");
            CreateRoomData(original);
            ModifyData(original, first, data => data.Rooms.ByName("room_first").Width = 111u);
            ModifyData(original, second, data => data.Rooms.ByName("room_second").Height = 222u);

            using ModdingOperationService service = new();
            Assert.True((await service.RunAsync(CreateRequest(original, first, firstPatch), TestContext.Current.CancellationToken)).Success);
            Assert.True((await service.RunAsync(CreateRequest(original, second, secondPatch), TestContext.Current.CancellationToken)).Success);
            Assert.True((await service.RunAsync(new ModdingOperationRequest { Operation = ModdingOperation.ValidatePatch, OriginalPath = original, PrimaryPath = firstPatch }, TestContext.Current.CancellationToken)).Success);
            Assert.True((await service.RunAsync(new ModdingOperationRequest { Operation = ModdingOperation.Inspect, PrimaryPath = original }, TestContext.Current.CancellationToken)).Success);
            Assert.True((await service.RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.MergePatches,
                OriginalPath = original,
                PrimaryPath = mergedData,
                InputPaths = [firstPatch, secondPatch],
                OutputPath = mergedPatch,
                FullReport = true
            }, TestContext.Current.CancellationToken)).Success);
            Assert.True(File.Exists(mergedData));
            Assert.True(File.Exists(mergedPatch));
            Assert.True((await service.RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.Compare,
                OriginalPath = original,
                PrimaryPath = mergedData,
                OutputPath = report,
                FullReport = true
            }, TestContext.Current.CancellationToken)).Success);
            Assert.True(File.Exists(report));
            Assert.True((await service.RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.BatchCreate,
                OriginalPath = original,
                InputPaths = [first, second],
                OutputPath = batchDirectory
            }, TestContext.Current.CancellationToken)).Success);
            Assert.Equal(2, Directory.EnumerateFiles(batchDirectory, "*.g3mpatch").Count());
            Assert.True((await service.RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.BatchApply,
                OriginalPath = original,
                InputPaths = [firstPatch, secondPatch],
                OutputPath = batchApplyDirectory
            }, TestContext.Current.CancellationToken)).Success);
            Assert.Equal(2, Directory.EnumerateFiles(batchApplyDirectory, "*.win").Count());
            Assert.True((await service.RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.BatchMerge,
                OriginalPath = original,
                PrimaryPath = batchMergeDataDirectory,
                InputPaths = [firstPatch + "," + secondPatch],
                OutputPath = batchMergePatchDirectory,
                FullReport = true
            }, TestContext.Current.CancellationToken)).Success);
            Assert.Single(Directory.EnumerateFiles(batchMergeDataDirectory, "*.win"));
            Assert.Single(Directory.EnumerateFiles(batchMergePatchDirectory, "*.g3mpatch"));
            Assert.True((await service.RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.BatchMerge,
                OriginalPath = original,
                PrimaryPath = batchMergeDataDirectory,
                InputPaths = [firstPatch + "," + secondPatch],
                OutputPath = batchMergePatchDirectory
            }, TestContext.Current.CancellationToken)).Success);
            Assert.Equal(2, Directory.EnumerateFiles(batchMergeDataDirectory, "*.win").Count());
            Assert.Equal(2, Directory.EnumerateFiles(batchMergePatchDirectory, "*.g3mpatch").Count());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task XDeltaOperationsRoundTripArbitraryFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "g3mtool-gui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var original = Path.Combine(directory, "original.bin");
            var modified = Path.Combine(directory, "modified.bin");
            var patch = Path.Combine(directory, "patch.xdelta");
            var output = Path.Combine(directory, "output.bin");
            await File.WriteAllBytesAsync(original, [1, 2, 3, 4], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(modified, [1, 9, 3, 4, 5], TestContext.Current.CancellationToken);

            var provider = typeof(ModdingOperationService).Assembly.GetType("G3MToolGUI.Services.EmbeddedXDeltaPathProvider", throwOnError: true)!;
            var executable = (string)provider.GetMethod("GetPath")!.Invoke(null, null)!;
            Assert.True(File.Exists(executable));
            if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(executable).HasFlag(UnixFileMode.UserExecute));
            XDeltaService.DefaultExecutablePath = executable;
            try
            {
                using ModdingOperationService service = new();
                Assert.True((await service.RunAsync(new ModdingOperationRequest { Operation = ModdingOperation.CreateXDelta, OriginalPath = original, PrimaryPath = modified, OutputPath = patch }, TestContext.Current.CancellationToken)).Success);
                Assert.True((await service.RunAsync(new ModdingOperationRequest { Operation = ModdingOperation.ApplyXDelta, OriginalPath = original, PrimaryPath = patch, OutputPath = output }, TestContext.Current.CancellationToken)).Success);
                Assert.Equal(await File.ReadAllBytesAsync(modified, TestContext.Current.CancellationToken), await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
                var originalData = Path.Combine(directory, "original.win");
                var modifiedData = Path.Combine(directory, "modified.win");
                var vcdiff = Path.Combine(directory, "data.vcdiff");
                var structuredPatch = Path.Combine(directory, "converted.g3mpatch");
                CreateRoomData(originalData);
                ModifyData(originalData, modifiedData, data => data.Rooms[1].Width = 987);
                Assert.True((await new XDeltaService().CreatePatchAsync(originalData, modifiedData, vcdiff)).Success);
                var converted = await G3MLib.Modding.Patching.PatchService.CreatePatchAsync(originalData, vcdiff, structuredPatch, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(converted.Success, converted.Error);
                var ensured = await G3MLib.Modding.Patching.PatchService.EnsureG3MPatchAsync(originalData, vcdiff, directory);
                var applied = await G3MLib.Modding.Patching.PatchService.ApplyPatchAsync(originalData, ensured, Path.Combine(directory, "applied.win"), cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(applied.Success, applied.Error);
                using var appliedStream = File.OpenRead(Path.Combine(directory, "applied.win"));
                using var appliedData = await GameMakerIO.ReadAsync(appliedStream, null, cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(987u, appliedData.Rooms[1].Width);
                var script = Path.Combine(directory, "change.csx");
                await File.WriteAllTextAsync(script, "Data.Rooms[1].Width = 654;", TestContext.Current.CancellationToken);
                var batch = await service.RunAsync(new ModdingOperationRequest
                {
                    Operation = ModdingOperation.BatchCreate,
                    OriginalPath = originalData,
                    InputPaths = [script],
                    OutputPath = Path.Combine(directory, "batch-delta"),
                    CreateXDelta = true
                }, TestContext.Current.CancellationToken);
                Assert.True(batch.Success, batch.Summary);
                var batchOutput = Path.Combine(directory, "batch-applied.win");
                Assert.True((await new XDeltaService().ApplyPatchAsync(originalData, Assert.Single(batch.Outputs), batchOutput)).Success);
                using var batchStream = File.OpenRead(batchOutput);
                using var batchData = await GameMakerIO.ReadAsync(batchStream, null, cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(654u, batchData.Rooms[1].Width);
            }
            finally
            {
                XDeltaService.DefaultExecutablePath = null;
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ModdingOperationRequest CreateRequest(string original, string modified, string output) => new()
    {
        Operation = ModdingOperation.CreatePatch,
        OriginalPath = original,
        PrimaryPath = modified,
        OutputPath = output
    };

    [Fact]
    public async Task CancelledApplyPatchReportsCancelledAndWritesNothing()
    {
        var directory = Directory.CreateTempSubdirectory("g3mtool-cancel-").FullName;
        try
        {
            var original = Path.Combine(directory, "original.win");
            var modified = Path.Combine(directory, "modified.win");
            var patch = Path.Combine(directory, "change.g3mpatch");
            var output = Path.Combine(directory, "applied.win");
            CreateRoomData(original);
            ModifyData(original, modified, data => data.Rooms[1].Width = 123);
            using ModdingOperationService service = new();
            Assert.True((await service.RunAsync(new ModdingOperationRequest { Operation = ModdingOperation.CreatePatch, OriginalPath = original, PrimaryPath = modified, OutputPath = patch }, TestContext.Current.CancellationToken)).Success);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            var result = await service.RunAsync(new ModdingOperationRequest { Operation = ModdingOperation.ApplyPatch, OriginalPath = original, PrimaryPath = patch, OutputPath = output }, cancellation.Token);

            Assert.False(result.Success);
            Assert.Equal("Cancelled.", result.Summary);
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void CreateRoomData(string path)
    {
        using var data = GameMakerData.CreateNew2026LTS();
#pragma warning disable CA2000 // the data model owns what is added to it
        data.Rooms.Add(new GameMakerRoom { Name = data.Strings.MakeString("room_first") });
        data.Rooms.Add(new GameMakerRoom { Name = data.Strings.MakeString("room_second") });
#pragma warning restore CA2000
        using var stream = File.Create(path);
        GameMakerIO.Write(stream, data);
    }

    private static void ModifyData(string sourcePath, string outputPath, Action<GameMakerData> modify)
    {
        using var source = File.OpenRead(sourcePath);
        using var data = GameMakerIO.Read(source);
        modify(data);
        using var output = File.Create(outputPath);
        GameMakerIO.Write(output, data);
    }
}
