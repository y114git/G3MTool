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
        string directory = Directory.CreateTempSubdirectory("g3mtool-gui-merge-outputs-").FullName;
        try
        {
            string original = Path.Combine(directory, "original.win");
            CreateRoomData(original);
            string first = Path.Combine(directory, "first.csx");
            string second = Path.Combine(directory, "second.csx");
            await File.WriteAllTextAsync(first, "Data.Rooms[1].Width = 123;", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(second, "Data.Rooms[1].Height = 456;", TestContext.Current.CancellationToken);
            string dataDirectory = Path.Combine(directory, "data");
            string patchDirectory = Path.Combine(directory, "patches");
            Directory.CreateDirectory(patchDirectory);
            string report = Path.Combine(patchDirectory, "merge_01.merge.md");
            await File.WriteAllTextAsync(report, "previous report", TestContext.Current.CancellationToken);
            var request = new ModdingOperationRequest
            {
                Operation = ModdingOperation.BatchMerge, OriginalPath = original,
                InputPaths = [first + "," + second], PrimaryPath = dataDirectory,
                OutputPath = patchDirectory, FullReport = true
            };
            using var service = new ModdingOperationService();
            var saved = new Dictionary<string, byte[]>();
            for (int run = 0; run < 2; run++)
            {
                var result = await service.RunAsync(request);
                Assert.True(result.Success, result.Summary);
                foreach (string output in result.Outputs)
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
            ScriptExecutionResult result = await ScriptExecutionService.RunAsync(script, withInput ? output : null, output, []);
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
        string directory = Directory.CreateTempSubdirectory("g3mtool-dialogs-").FullName;
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            string script = Path.Combine(directory, "dialogs.csx");
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
            Task<ScriptExecutionResult> run = Task.Run(() => ScriptExecutionService.RunAsync(script, null, null, [], interaction), TestContext.Current.CancellationToken);
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
        string directory = Path.Combine(Path.GetTempPath(), "g3mtool-gui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string original = Path.Combine(directory, "original.win");
            string script = Path.Combine(directory, "change.csx");
            string invalid = Path.Combine(directory, "invalid.csx");
            string applied = Path.Combine(directory, "applied.win");
            string merged = Path.Combine(directory, "merged.win");
            CreateRoomData(original);
            await File.WriteAllTextAsync(script, "Data.Rooms[1].Width = 123;", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(invalid, "throw new Exception(\"invalid input\");", TestContext.Current.CancellationToken);
            using ModdingOperationService service = new();

            var result = await service.RunAsync(new ModdingOperationRequest { Operation = ModdingOperation.ApplyPatch, OriginalPath = original, PrimaryPath = script, OutputPath = applied });
            Assert.True(result.Success, result.Summary);
            using (FileStream stream = File.OpenRead(applied))
            using (GameMakerData data = GameMakerIO.Read(stream))
                Assert.Equal(123u, data.Rooms[1].Width);

            result = await service.RunAsync(new ModdingOperationRequest { Operation = ModdingOperation.MergePatches, OriginalPath = original, InputPaths = [script, applied], PrimaryPath = merged });
            Assert.True(result.Success, result.Summary);
            Assert.True(File.Exists(merged));

            result = await service.RunAsync(new ModdingOperationRequest { Operation = ModdingOperation.BatchCreate, OriginalPath = original, InputPaths = [invalid, script], OutputPath = Path.Combine(directory, "batch"), ContinueOnError = true });
            Assert.False(result.Success);
            Assert.Contains("1/2 completed; 1 failed", result.Summary);
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
        string directory = Path.Combine(Path.GetTempPath(), "g3mtool-gui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "input.win");
            string script = Path.Combine(directory, "invalid.csx");
            CreateRoomData(path);
            byte[] original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(script, "Data.GeneralInfo.DisplayName.Content = null;", TestContext.Current.CancellationToken);

            ScriptExecutionResult result = await ScriptExecutionService.RunAsync(script, path, path, []);

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
        string output = Path.Combine(Path.GetTempPath(), "g3mtool-gui-tests", Guid.NewGuid() + ".g3mpatch");
        using ModdingOperationService service = new();

        ModdingOperationResult result = await service.RunAsync(new ModdingOperationRequest
        {
            Operation = ModdingOperation.CreatePatch,
            OriginalPath = "missing-original.win",
            PrimaryPath = "missing-modified.win",
            OutputPath = output
        });

        Assert.False(result.Success);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ScriptExecutionRunsWithoutDataFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "g3mtool-gui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, "script.csx");
        await File.WriteAllTextAsync(script, "ScriptMessage(\"GUI script test\");", TestContext.Current.CancellationToken);
        try
        {
            using ModdingOperationService service = new();
            ModdingOperationResult result = await service.RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.RunScript,
                PrimaryPath = script
            });

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
        string directory = Path.Combine(Path.GetTempPath(), "g3mtool-gui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "input.win");
            string output = Path.Combine(directory, "output.win");
            string script = Path.Combine(directory, "script.csx");
            using (GameMakerData data = GameMakerData.CreateNew2026LTS())
            using (FileStream stream = File.Create(input))
            {
                GameMakerIO.Write(stream, data);
            }
            await File.WriteAllTextAsync(script, "using UndertaleModLib;\nData.GeneralInfo!.LastObj = 777;", TestContext.Current.CancellationToken);

            using ModdingOperationService service = new();
            ModdingOperationResult result = await service.RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.RunScript,
                OriginalPath = input,
                PrimaryPath = script,
                OutputPath = output
            });

            Assert.True(result.Success, result.Summary);
            using FileStream resultStream = File.OpenRead(output);
            using GameMakerData resultData = GameMakerIO.Read(resultStream);
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
        string executable = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec")! : "/bin/sh";
        string[] arguments = OperatingSystem.IsWindows() ? ["/c", "echo stdout& echo stderr 1>&2"] : ["-c", "echo stdout; echo stderr >&2"];
        using ModdingOperationService service = new();
        var messages = new System.Collections.Concurrent.ConcurrentBag<string>();
        service.LogReceived += message => messages.Add(message.Message.Trim());

        ModdingOperationResult result = await service.RunAsync(new ModdingOperationRequest
        {
            Operation = ModdingOperation.RunProgram,
            PrimaryPath = executable,
            InputPaths = arguments
        });

        Assert.True(result.Success, result.Summary);
        Assert.Contains("stdout", messages);
        Assert.Contains("stderr", messages);
    }

    [Fact]
    public async Task PatchMergeCompareAndBatchOperationsRoundTripData()
    {
        string directory = Path.Combine(Path.GetTempPath(), "g3mtool-gui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string original = Path.Combine(directory, "original.win");
            string first = Path.Combine(directory, "first.win");
            string second = Path.Combine(directory, "second.win");
            string firstPatch = Path.Combine(directory, "first.g3mpatch");
            string secondPatch = Path.Combine(directory, "second.g3mpatch");
            string mergedData = Path.Combine(directory, "merged.win");
            string mergedPatch = Path.Combine(directory, "merged.g3mpatch");
            string report = Path.Combine(directory, "diff.md");
            string batchDirectory = Path.Combine(directory, "batch-create");
            string batchApplyDirectory = Path.Combine(directory, "batch-apply");
            string batchMergeDataDirectory = Path.Combine(directory, "batch-merge-data");
            string batchMergePatchDirectory = Path.Combine(directory, "batch-merge-patches");
            CreateRoomData(original);
            ModifyData(original, first, data => data.Rooms.ByName("room_first").Width = 111u);
            ModifyData(original, second, data => data.Rooms.ByName("room_second").Height = 222u);

            using ModdingOperationService service = new();
            Assert.True((await service.RunAsync(CreateRequest(original, first, firstPatch))).Success);
            Assert.True((await service.RunAsync(CreateRequest(original, second, secondPatch))).Success);
            Assert.True((await service.RunAsync(new ModdingOperationRequest { Operation = ModdingOperation.ValidatePatch, OriginalPath = original, PrimaryPath = firstPatch })).Success);
            Assert.True((await service.RunAsync(new ModdingOperationRequest { Operation = ModdingOperation.Inspect, PrimaryPath = original })).Success);
            Assert.True((await service.RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.MergePatches,
                OriginalPath = original,
                PrimaryPath = mergedData,
                InputPaths = [firstPatch, secondPatch],
                OutputPath = mergedPatch,
                FullReport = true
            })).Success);
            Assert.True(File.Exists(mergedData));
            Assert.True(File.Exists(mergedPatch));
            Assert.True((await service.RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.Compare,
                OriginalPath = original,
                PrimaryPath = mergedData,
                OutputPath = report,
                FullReport = true
            })).Success);
            Assert.True(File.Exists(report));
            Assert.True((await service.RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.BatchCreate,
                OriginalPath = original,
                InputPaths = [first, second],
                OutputPath = batchDirectory
            })).Success);
            Assert.Equal(2, Directory.EnumerateFiles(batchDirectory, "*.g3mpatch").Count());
            Assert.True((await service.RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.BatchApply,
                OriginalPath = original,
                InputPaths = [firstPatch, secondPatch],
                OutputPath = batchApplyDirectory
            })).Success);
            Assert.Equal(2, Directory.EnumerateFiles(batchApplyDirectory, "*.win").Count());
            Assert.True((await service.RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.BatchMerge,
                OriginalPath = original,
                PrimaryPath = batchMergeDataDirectory,
                InputPaths = [firstPatch + "," + secondPatch],
                OutputPath = batchMergePatchDirectory,
                FullReport = true
            })).Success);
            Assert.Single(Directory.EnumerateFiles(batchMergeDataDirectory, "*.win"));
            Assert.Single(Directory.EnumerateFiles(batchMergePatchDirectory, "*.g3mpatch"));
            Assert.True((await service.RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.BatchMerge,
                OriginalPath = original,
                PrimaryPath = batchMergeDataDirectory,
                InputPaths = [firstPatch + "," + secondPatch],
                OutputPath = batchMergePatchDirectory
            })).Success);
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
        string directory = Path.Combine(Path.GetTempPath(), "g3mtool-gui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string original = Path.Combine(directory, "original.bin");
            string modified = Path.Combine(directory, "modified.bin");
            string patch = Path.Combine(directory, "patch.xdelta");
            string output = Path.Combine(directory, "output.bin");
            await File.WriteAllBytesAsync(original, [1, 2, 3, 4], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(modified, [1, 9, 3, 4, 5], TestContext.Current.CancellationToken);

            Type provider = typeof(ModdingOperationService).Assembly.GetType("G3MToolGUI.Services.EmbeddedXDeltaPathProvider", throwOnError: true)!;
            string executable = (string)provider.GetMethod("GetPath")!.Invoke(null, null)!;
            Assert.True(File.Exists(executable));
            if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(executable).HasFlag(UnixFileMode.UserExecute));
            XDeltaService.DefaultExecutablePath = executable;
            try
            {
                using ModdingOperationService service = new();
                Assert.True((await service.RunAsync(new ModdingOperationRequest { Operation = ModdingOperation.CreateXDelta, OriginalPath = original, PrimaryPath = modified, OutputPath = patch })).Success);
                Assert.True((await service.RunAsync(new ModdingOperationRequest { Operation = ModdingOperation.ApplyXDelta, OriginalPath = original, PrimaryPath = patch, OutputPath = output })).Success);
                Assert.Equal(await File.ReadAllBytesAsync(modified, TestContext.Current.CancellationToken), await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
                string originalData = Path.Combine(directory, "original.win");
                string modifiedData = Path.Combine(directory, "modified.win");
                string vcdiff = Path.Combine(directory, "data.vcdiff");
                string structuredPatch = Path.Combine(directory, "converted.g3mpatch");
                CreateRoomData(originalData);
                ModifyData(originalData, modifiedData, data => data.Rooms[1].Width = 987);
                Assert.True((await new XDeltaService().CreatePatchAsync(originalData, modifiedData, vcdiff)).Success);
                var converted = await G3MLib.Modding.Patching.PatchService.CreatePatchAsync(originalData, vcdiff, structuredPatch);
                Assert.True(converted.Success, converted.Error);
                string ensured = await G3MLib.Modding.Patching.PatchService.EnsureG3MPatchAsync(originalData, vcdiff, directory);
                var applied = await G3MLib.Modding.Patching.PatchService.ApplyPatchAsync(originalData, ensured, Path.Combine(directory, "applied.win"));
                Assert.True(applied.Success, applied.Error);
                using var appliedStream = File.OpenRead(Path.Combine(directory, "applied.win"));
                using var appliedData = GameMakerIO.Read(appliedStream);
                Assert.Equal(987u, appliedData.Rooms[1].Width);
                string script = Path.Combine(directory, "change.csx");
                await File.WriteAllTextAsync(script, "Data.Rooms[1].Width = 654;", TestContext.Current.CancellationToken);
                var batch = await service.RunAsync(new ModdingOperationRequest
                {
                    Operation = ModdingOperation.BatchCreate,
                    OriginalPath = originalData,
                    InputPaths = [script],
                    OutputPath = Path.Combine(directory, "batch-delta"),
                    CreateXDelta = true
                });
                Assert.True(batch.Success, batch.Summary);
                string batchOutput = Path.Combine(directory, "batch-applied.win");
                Assert.True((await new XDeltaService().ApplyPatchAsync(originalData, Assert.Single(batch.Outputs), batchOutput)).Success);
                using var batchStream = File.OpenRead(batchOutput);
                using var batchData = GameMakerIO.Read(batchStream);
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

    private static void CreateRoomData(string path)
    {
        using GameMakerData data = GameMakerData.CreateNew2026LTS();
        data.Rooms.Add(new GameMakerRoom { Name = data.Strings.MakeString("room_first") });
        data.Rooms.Add(new GameMakerRoom { Name = data.Strings.MakeString("room_second") });
        using FileStream stream = File.Create(path);
        GameMakerIO.Write(stream, data);
    }

    private static void ModifyData(string sourcePath, string outputPath, Action<GameMakerData> modify)
    {
        using FileStream source = File.OpenRead(sourcePath);
        using GameMakerData data = GameMakerIO.Read(source);
        modify(data);
        using FileStream output = File.Create(outputPath);
        GameMakerIO.Write(output, data);
    }
}
