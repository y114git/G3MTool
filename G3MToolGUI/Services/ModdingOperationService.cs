using System.Diagnostics;
using System.Text;
using G3MLib.DataFile;
using G3MLib.Modding.Caching;
using G3MLib.Modding.Diffing;
using G3MLib.Modding.Logging;
using G3MLib.Modding.Merging;
using G3MLib.Modding.Patching;
using G3MLib.Modding.XDelta;

namespace G3MToolGUI.Services;

public enum ModdingOperation
{
    CreatePatch,
    ApplyPatch,
    ValidatePatch,
    MergePatches,
    Compare,
    Inspect,
    BatchCreate,
    BatchApply,
    BatchMerge,
    CreateXDelta,
    ApplyXDelta,
    RunXDelta,
    RunScript,
    RunProgram
}

public sealed class ModdingOperationRequest
{
    public required ModdingOperation Operation { get; init; }
    public string OriginalPath { get; init; } = string.Empty;
    public string PrimaryPath { get; init; } = string.Empty;
    public IReadOnlyList<string> InputPaths { get; init; } = [];
    public string OutputPath { get; init; } = string.Empty;
    public string CacheDirectory { get; init; } = string.Empty;
    public bool UseCodeMerge { get; init; }
    public bool UsePropertyMerge { get; init; }
    public bool UseSequentialMerge { get; init; }
    public bool IncludeXDeltaFallback { get; init; }
    public bool CreateXDelta { get; init; }
    public bool ContinueOnError { get; init; }
    public bool FullReport { get; init; }
}

public sealed record ModdingOperationResult(bool Success, string Summary, IReadOnlyList<string> Outputs)
{
    public static ModdingOperationResult Failure(string message) => new(false, message, []);
}

public sealed class ModdingOperationService : IDisposable
{
    public event Action<ModdingLogMessage>? LogReceived;
    public event Action<ModdingProgress>? ProgressChanged;

    private readonly ScriptInteraction? _interaction;

    public ModdingOperationService(ScriptInteraction? interaction = null)
    {
        _interaction = interaction;
    }

    public async Task<ModdingOperationResult> RunAsync(ModdingOperationRequest request)
    {
        using var logging = LogService.BeginScope(OnLogMessage, OnProgress, verbose: true);
        try
        {
            return request.Operation switch
            {
                ModdingOperation.CreatePatch => await CreatePatchAsync(request),
                ModdingOperation.ApplyPatch => await ApplyPatchAsync(request),
                ModdingOperation.ValidatePatch => await ValidatePatchAsync(request),
                ModdingOperation.MergePatches => await MergeAsync(request),
                ModdingOperation.Compare => await CompareAsync(request),
                ModdingOperation.Inspect => await InspectAsync(request),
                ModdingOperation.BatchCreate => await BatchCreateAsync(request),
                ModdingOperation.BatchApply => await BatchApplyAsync(request),
                ModdingOperation.BatchMerge => await BatchMergeAsync(request),
                ModdingOperation.CreateXDelta => await CreateXDeltaAsync(request),
                ModdingOperation.ApplyXDelta => await ApplyXDeltaAsync(request),
                ModdingOperation.RunXDelta => await RunXDeltaAsync(request),
                ModdingOperation.RunScript => await RunScriptAsync(request),
                ModdingOperation.RunProgram => await RunProgramAsync(request),
                _ => ModdingOperationResult.Failure("Unsupported operation.")
            };
        }
        catch (Exception exception)
        {
            LogService.Error(exception.Message);
            return ModdingOperationResult.Failure(exception.Message);
        }
        finally
        {
            LogService.ProgressComplete();
        }
    }

    public void Dispose()
    {
        LogReceived = null;
        ProgressChanged = null;
    }

    private async Task<ModdingOperationResult> CreatePatchAsync(ModdingOperationRequest request)
    {
        string error = RequireFiles(request.OriginalPath, request.PrimaryPath) ?? RequireOutput(request.OutputPath) ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);

        LogService.SetOperation("Creating patch");
        return await CreatePatchFileAsync(request.OriginalPath, request.PrimaryPath, request.OutputPath, request.IncludeXDeltaFallback, CacheOptions(request));
    }

    private async Task<ModdingOperationResult> ApplyPatchAsync(ModdingOperationRequest request)
    {
        string error = RequireFiles(request.OriginalPath, request.PrimaryPath) ?? RequireOutput(request.OutputPath) ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);

        LogService.SetOperation("Applying patch");
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "g3mtool-gui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            if (IsXDelta(request.PrimaryPath) || Path.GetExtension(request.PrimaryPath).Equals(".csx", StringComparison.OrdinalIgnoreCase))
            {
                string materialized = await MaterializeInputAsync(request.OriginalPath, request.PrimaryPath, temporaryDirectory);
                PatchInputService.CopyDataFile(materialized, request.OutputPath);
                return new(true, "Patch applied.", [request.OutputPath]);
            }
            string patchPath = await PatchService.EnsureG3MPatchAsync(request.OriginalPath, request.PrimaryPath, temporaryDirectory, cacheOptions: CacheOptions(request));
            PatchApplyResult result = await PatchService.ApplyPatchAsync(request.OriginalPath, patchPath, request.OutputPath, request.IncludeXDeltaFallback);
            return result.Success
                ? new(true, "Patch applied.", [request.OutputPath])
                : ModdingOperationResult.Failure(result.Error ?? "Patch application failed.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task<ModdingOperationResult> ValidatePatchAsync(ModdingOperationRequest request)
    {
        string error = RequireFile(request.PrimaryPath, "Patch") ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);
        if (!string.IsNullOrWhiteSpace(request.OriginalPath) && !File.Exists(request.OriginalPath))
            return ModdingOperationResult.Failure("Original data file was not found.");

        LogService.SetOperation("Validating patch");
        PatchValidateResult result = await PatchService.ValidatePatchAsync(request.PrimaryPath, EmptyToNull(request.OriginalPath), CacheOptions(request));
        if (!result.Success) return ModdingOperationResult.Failure(result.Error ?? "Patch validation failed.");

        PatchStatistics? statistics = result.Manifest?.Statistics;
        string summary = statistics is null
            ? "Patch is valid."
            : $"Patch is valid: {statistics.TotalChanged} changed, {statistics.TotalNew} new, {statistics.TotalDeleted} deleted.";
        return new(true, summary, []);
    }

    private async Task<ModdingOperationResult> MergeAsync(ModdingOperationRequest request)
    {
        string error = RequireFile(request.OriginalPath, "Original data file") ?? RequireAtLeast(request.InputPaths, 2, "patches") ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);

        LogService.SetOperation("Merging patches");
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "g3mtool-gui", Guid.NewGuid().ToString("N"));
        List<string> inputs = [];
        MergeResult result;
        try
        {
            foreach (string input in request.InputPaths)
                inputs.Add(Path.GetExtension(input).Equals(".csx", StringComparison.OrdinalIgnoreCase)
                    ? await MaterializeInputAsync(request.OriginalPath, input, Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N")))
                    : input);
            result = await MergeService.MergePatchesAsync(request.OriginalPath, inputs, new MergeOptions
            {
                OutputPath = EmptyToNull(request.OutputPath),
                ApplyPath = EmptyToNull(request.PrimaryPath),
                UseCodeMerge = request.UseCodeMerge,
                UsePropertyMerge = request.UsePropertyMerge,
                UseSequentialMerge = request.UseSequentialMerge,
                ReportPath = request.FullReport ? ReportPath(request.OutputPath, request.PrimaryPath) : null,
                CacheOptions = CacheOptions(request)
            });
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
        }
        if (!result.Success) return ModdingOperationResult.Failure(result.Error ?? "Patch merge failed.");

        List<string> outputs = [.. new[] { result.OutputPath, EmptyToNull(request.PrimaryPath) }.OfType<string>()];
        string summary = result.TotalConflicts == 0
            ? "Patches merged without conflicts."
            : $"Patches merged with {result.TotalConflicts} conflict(s); {result.AutoMerged} resolved automatically.";
        return new(true, summary, outputs);
    }

    private static async Task<ModdingOperationResult> CompareAsync(ModdingOperationRequest request)
    {
        string error = RequireFiles(request.OriginalPath, request.PrimaryPath) ?? RequireOutput(request.OutputPath) ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);

        LogService.SetOperation("Comparing files");
        DiffResult result = await DiffService.CompareAsync(
            request.OriginalPath, request.PrimaryPath, request.OutputPath,
            request.FullReport ? DiffReportMode.Full : DiffReportMode.Standard,
            CacheOptions(request));
        return result.Success
            ? new(true, $"Report created: {result.DifferenceCount} difference(s).", [request.OutputPath])
            : ModdingOperationResult.Failure(result.Error ?? "Comparison failed.");
    }

    private static async Task<ModdingOperationResult> InspectAsync(ModdingOperationRequest request)
    {
        string error = RequireFile(request.PrimaryPath, "File") ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);

        LogService.SetOperation("Inspecting file");
        if (Path.GetExtension(request.PrimaryPath).Equals(".g3mpatch", StringComparison.OrdinalIgnoreCase))
        {
            PatchValidateResult patch = await PatchService.ValidatePatchAsync(request.PrimaryPath, cacheOptions: CacheOptions(request));
            if (!patch.Success) return ModdingOperationResult.Failure(patch.Error ?? "Patch inspection failed.");
            PatchStatistics? stats = patch.Manifest?.Statistics;
            return new(true, stats is null ? "Patch is valid." : $"Patch: {stats.TotalChanged} changed, {stats.TotalNew} new, {stats.TotalDeleted} deleted.", []);
        }

        using FileStream stream = new(request.PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using GameMakerData data = GameMakerIO.Read(stream);
        G3MDataInfoSnapshot snapshot = G3MCacheService.BuildInfoSnapshot(request.PrimaryPath, data);
        await G3MCacheService.WriteDataInfoCacheAsync(request.PrimaryPath, snapshot, CacheOptions(request));
        LogService.Info($"Game: {snapshot.Game}");
        LogService.Info($"Bytecode: {snapshot.BytecodeVersion}");
        foreach ((string type, int count) in snapshot.ResourceCounts.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            LogService.Info($"{type}: {count:N0}");
        return new(true, $"{snapshot.Game} · bytecode {snapshot.BytecodeVersion} · {snapshot.ResourceCounts.Values.Sum():N0} resources.", []);
    }

    private async Task<ModdingOperationResult> BatchCreateAsync(ModdingOperationRequest request)
    {
        string error = RequireFile(request.OriginalPath, "Original data file") ?? RequireAtLeast(request.InputPaths, 1, "inputs") ?? RequireDirectory(request.OutputPath) ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);
        if (request.IncludeXDeltaFallback && request.CreateXDelta) return ModdingOperationResult.Failure("XDelta output cannot include an XDelta fallback.");
        LogService.SetOperation("Creating patch batch");
        Directory.CreateDirectory(request.OutputPath);
        List<string> outputs = [];
        int completed = 0;
        int failed = 0;
        foreach ((string input, int index) in request.InputPaths.Select((path, index) => (path, index + 1)))
        {
            string output = UniquePath(Path.Combine(request.OutputPath, Path.GetFileNameWithoutExtension(input) + (request.CreateXDelta ? ".xdelta" : ".g3mpatch")));
            ModdingOperationResult item = await CreatePatchFileAsync(request.OriginalPath, input, output, request.IncludeXDeltaFallback, CacheOptions(request), request.CreateXDelta);
            if (item.Success) { completed++; outputs.Add(output); }
            else { failed++; LogService.Error(item.Summary); if (!request.ContinueOnError) break; }
            LogService.Progress(index, request.InputPaths.Count);
        }
        return BatchSummary(completed, failed, request.InputPaths.Count, outputs);
    }

    private async Task<ModdingOperationResult> BatchApplyAsync(ModdingOperationRequest request)
    {
        string error = RequireFile(request.OriginalPath, "Original data file") ?? RequireAtLeast(request.InputPaths, 1, "patches") ?? RequireDirectory(request.OutputPath) ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);
        LogService.SetOperation("Applying patch batch");
        Directory.CreateDirectory(request.OutputPath);
        List<string> outputs = [];
        int completed = 0;
        int failed = 0;
        string stem = Path.GetFileNameWithoutExtension(request.OriginalPath);
        string extension = Path.GetExtension(request.OriginalPath);
        foreach ((string input, int index) in request.InputPaths.Select((path, index) => (path, index + 1)))
        {
            string output = UniquePath(Path.Combine(request.OutputPath, stem + "_" + Path.GetFileNameWithoutExtension(input) + extension));
            ModdingOperationResult item = await RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.ApplyPatch,
                OriginalPath = request.OriginalPath,
                PrimaryPath = input,
                OutputPath = output,
                CacheDirectory = request.CacheDirectory,
                IncludeXDeltaFallback = request.IncludeXDeltaFallback
            });
            if (item.Success) { completed++; outputs.Add(output); }
            else { failed++; LogService.Error(item.Summary); if (!request.ContinueOnError) break; }
            LogService.Progress(index, request.InputPaths.Count);
        }
        return BatchSummary(completed, failed, request.InputPaths.Count, outputs);
    }

    private async Task<ModdingOperationResult> BatchMergeAsync(ModdingOperationRequest request)
    {
        string error = RequireFile(request.OriginalPath, "Original data file") ?? (request.InputPaths.Count == 0 ? "Enter at least one merge set." : null) ?? RequireDirectory(request.PrimaryPath) ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);
        LogService.SetOperation("Merging patch batches");
        Directory.CreateDirectory(request.PrimaryPath);
        if (!string.IsNullOrWhiteSpace(request.OutputPath)) Directory.CreateDirectory(request.OutputPath);
        List<string> outputs = [];
        int completed = 0;
        int failed = 0;
        for (int index = 0; index < request.InputPaths.Count; index++)
        {
            List<string> inputs = request.InputPaths[index].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            string stem = "merge_" + (index + 1).ToString("D2");
            string dataOutput;
            string patchOutput;
            for (int suffix = 1; ; suffix++)
            {
                string name = suffix == 1 ? stem : stem + "_" + suffix;
                dataOutput = Path.Combine(request.PrimaryPath, name + Path.GetExtension(request.OriginalPath));
                patchOutput = string.IsNullOrWhiteSpace(request.OutputPath) ? string.Empty : Path.Combine(request.OutputPath, name + ".g3mpatch");
                string? report = request.FullReport ? ReportPath(patchOutput, dataOutput) : null;
                if (!new[] { dataOutput, patchOutput, report }.Any(path => File.Exists(path) || Directory.Exists(path))) break;
            }
            ModdingOperationResult item = await RunAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.MergePatches,
                OriginalPath = request.OriginalPath,
                PrimaryPath = dataOutput,
                InputPaths = inputs,
                OutputPath = patchOutput,
                CacheDirectory = request.CacheDirectory,
                UseCodeMerge = request.UseCodeMerge,
                UsePropertyMerge = request.UsePropertyMerge,
                UseSequentialMerge = request.UseSequentialMerge,
                FullReport = request.FullReport
            });
            if (item.Success) { completed++; outputs.AddRange(item.Outputs); }
            else { failed++; LogService.Error(item.Summary); if (!request.ContinueOnError) break; }
            LogService.Progress(index + 1, request.InputPaths.Count);
        }
        return BatchSummary(completed, failed, request.InputPaths.Count, outputs);
    }

    private static async Task<ModdingOperationResult> CreateXDeltaAsync(ModdingOperationRequest request)
    {
        string error = RequireFiles(request.OriginalPath, request.PrimaryPath) ?? RequireOutput(request.OutputPath) ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);
        LogService.SetOperation("Creating XDelta patch");
        XDeltaResult result = await new XDeltaService().CreatePatchAsync(request.OriginalPath, request.PrimaryPath, request.OutputPath);
        return result.Success ? new(true, "XDelta patch created.", [request.OutputPath]) : ModdingOperationResult.Failure(result.Error ?? "XDelta creation failed.");
    }

    private static async Task<ModdingOperationResult> ApplyXDeltaAsync(ModdingOperationRequest request)
    {
        string error = RequireFiles(request.OriginalPath, request.PrimaryPath) ?? RequireOutput(request.OutputPath) ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);
        LogService.SetOperation("Applying XDelta patch");
        XDeltaResult result = await new XDeltaService().ApplyPatchAsync(request.OriginalPath, request.PrimaryPath, request.OutputPath);
        return result.Success ? new(true, "XDelta patch applied.", [request.OutputPath]) : ModdingOperationResult.Failure(result.Error ?? "XDelta application failed.");
    }

    private static async Task<ModdingOperationResult> RunXDeltaAsync(ModdingOperationRequest request)
    {
        if (request.InputPaths.Count == 0) return ModdingOperationResult.Failure("Enter one XDelta argument per line.");
        LogService.SetOperation("Running XDelta");
        XDeltaResult result = await new XDeltaService().ExecuteRawAsync([.. request.InputPaths]);
        if (!string.IsNullOrWhiteSpace(result.Output)) LogService.Info(result.Output.TrimEnd());
        return result.Success ? new(true, "XDelta completed.", []) : ModdingOperationResult.Failure(result.Error ?? "XDelta failed.");
    }

    private async Task<ModdingOperationResult> RunScriptAsync(ModdingOperationRequest request)
    {
        string error = RequireFile(request.PrimaryPath, "Script") ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);
        LogService.SetOperation("Running script");
        ScriptExecutionResult result = await ScriptExecutionService.RunAsync(request.PrimaryPath, EmptyToNull(request.OriginalPath), EmptyToNull(request.OutputPath), request.InputPaths, _interaction);
        return result.Success
            ? new(true, "Script completed.", string.IsNullOrWhiteSpace(request.OutputPath) ? [] : [request.OutputPath])
            : ModdingOperationResult.Failure(result.Error ?? "Script execution failed.");
    }

    private static async Task<ModdingOperationResult> RunProgramAsync(ModdingOperationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PrimaryPath)) return ModdingOperationResult.Failure("Enter a program path or command name.");
        LogService.SetOperation("Running program");
        ExternalProcessResult result = await ExternalProcessService.RunAsync(request.PrimaryPath, request.InputPaths);
        return result.ExitCode == 0
            ? new(true, "Program completed.", [])
            : ModdingOperationResult.Failure($"Program exited with code {result.ExitCode}.");
    }

    private async Task<ModdingOperationResult> CreatePatchFileAsync(string originalPath, string inputPath, string outputPath, bool includeXDeltaFallback, G3MCacheOptions cacheOptions, bool createXDelta = false)
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "g3mtool-gui", Guid.NewGuid().ToString("N"));
        try
        {
            string materialized = await MaterializeInputAsync(originalPath, inputPath, temporaryDirectory);
            if (createXDelta)
            {
                XDeltaResult delta = await new XDeltaService().CreatePatchAsync(originalPath, materialized, outputPath);
                return delta.Success ? new(true, "XDelta patch created.", [outputPath]) : ModdingOperationResult.Failure(delta.Error ?? "XDelta creation failed.");
            }
            PatchCreateResult result = await PatchService.CreatePatchAsync(originalPath, materialized, outputPath, includeXdeltaFallback: includeXDeltaFallback, cacheOptions: cacheOptions);
            return result.Success
                ? new(true, DescribePatchCreated(result), [outputPath])
                : ModdingOperationResult.Failure(result.Error ?? "Patch creation failed.");
        }
        catch (Exception exception)
        {
            return ModdingOperationResult.Failure(exception.Message);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private async Task<string> MaterializeInputAsync(string originalPath, string inputPath, string temporaryDirectory)
    {
        Directory.CreateDirectory(temporaryDirectory);
        if (!Path.GetExtension(inputPath).Equals(".csx", StringComparison.OrdinalIgnoreCase))
            return await PatchInputService.MaterializeDataAsync(originalPath, inputPath, temporaryDirectory);

        string output = Path.Combine(temporaryDirectory, "script-output" + Path.GetExtension(originalPath));
        ScriptExecutionResult result = await ScriptExecutionService.RunAsync(inputPath, originalPath, output, [], _interaction);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "Script execution failed.");
        PatchInputService.CopyExternalAudioGroups(originalPath, output);
        return output;
    }

    private static ModdingOperationResult BatchSummary(int completed, int failed, int total, IReadOnlyList<string> outputs) => new(failed == 0 && completed == total, $"{completed}/{total} completed; {failed} failed.", outputs);

    private static G3MCacheOptions CacheOptions(ModdingOperationRequest request) => G3MCacheOptions.FromDirectory(EmptyToNull(request.CacheDirectory));

    private static string? ReportPath(string outputPath, string applyPath)
    {
        string? path = EmptyToNull(outputPath) ?? EmptyToNull(applyPath);
        return path is null ? null : Path.ChangeExtension(path, ".merge.md");
    }

    private static string DescribePatchCreated(PatchCreateResult result)
    {
        PatchStatistics? statistics = result.Statistics;
        return statistics is null
            ? "Patch created."
            : $"Patch created: {statistics.TotalChanged} changed, {statistics.TotalNew} new, {statistics.TotalDeleted} deleted.";
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string fileName = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int index = 2; ; index++)
        {
            string candidate = Path.Combine(directory, fileName + "_" + index + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static bool IsXDelta(string path) => Path.GetExtension(path).Equals(".xdelta", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".vcdiff", StringComparison.OrdinalIgnoreCase);
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string? RequireFile(string path, string name) => File.Exists(path) ? null : $"{name} was not found.";
    private static string? RequireFiles(string original, string input) => RequireFile(original, "Original data file") ?? RequireFile(input, "Input file");
    private static string? RequireOutput(string path) => string.IsNullOrWhiteSpace(path) ? "Choose an output path." : null;
    private static string? RequireDirectory(string path) => string.IsNullOrWhiteSpace(path) ? "Choose an output directory." : null;
    private static string? RequireAtLeast(IReadOnlyList<string> paths, int count, string name) => paths.Count >= count && paths.All(File.Exists) ? null : $"Choose at least {count} {name}.";
    private void OnLogMessage(ModdingLogMessage message) => LogReceived?.Invoke(message);
    private void OnProgress(ModdingProgress progress) => ProgressChanged?.Invoke(progress);
}
