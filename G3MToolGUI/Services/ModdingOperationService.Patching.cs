using G3MLib.DataFile;
using G3MLib.Modding.Caching;
using G3MLib.Modding.Diffing;
using G3MLib.Modding.Logging;
using G3MLib.Modding.Merging;
using G3MLib.Modding.Patching;

namespace G3MToolGUI.Services;

public sealed partial class ModdingOperationService
{
    private async Task<ModdingOperationResult> CreatePatchAsync(ModdingOperationRequest request, CancellationToken cancellationToken)
    {
        var error = RequireFiles(request.OriginalPath, request.PrimaryPath) ?? RequireOutput(request.OutputPath) ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);

        LogService.SetOperation("Creating patch");
        return await CreatePatchFileAsync(request, request.PrimaryPath, request.OutputPath, cancellationToken);
    }

    private async Task<ModdingOperationResult> ApplyPatchAsync(ModdingOperationRequest request, CancellationToken cancellationToken)
    {
        var error = RequireFiles(request.OriginalPath, request.PrimaryPath) ?? RequireOutput(request.OutputPath) ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);

        LogService.SetOperation("Applying patch");
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "g3mtool-gui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            if (IsXDelta(request.PrimaryPath) || Path.GetExtension(request.PrimaryPath).Equals(".csx", StringComparison.OrdinalIgnoreCase))
            {
                var materialized = await MaterializeInputAsync(request.OriginalPath, request.PrimaryPath, temporaryDirectory, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                PatchInputService.CopyDataFile(materialized, request.OutputPath);
                return new(true, "Patch applied.", [request.OutputPath]);
            }
            var patchPath = await PatchService.EnsureG3MPatchAsync(request.OriginalPath, request.PrimaryPath, temporaryDirectory, cacheOptions: CacheOptions(request));
            var result = await PatchService.ApplyPatchAsync(request.OriginalPath, patchPath, request.OutputPath, request.IncludeXDeltaFallback, cancellationToken: cancellationToken);
            return result.Success
                ? new(true, "Patch applied.", [request.OutputPath])
                : ModdingOperationResult.Failure(result.Error ?? "Patch application failed.");
        }
        finally
        {
            DeleteDirectory(temporaryDirectory);
        }
    }

    private static async Task<ModdingOperationResult> ValidatePatchAsync(ModdingOperationRequest request, CancellationToken cancellationToken)
    {
        var error = RequireFile(request.PrimaryPath, "Patch") ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);
        if (!string.IsNullOrWhiteSpace(request.OriginalPath) && !File.Exists(request.OriginalPath))
            return ModdingOperationResult.Failure("Original data file was not found.");

        LogService.SetOperation("Validating patch");
        var result = await PatchService.ValidatePatchAsync(request.PrimaryPath, EmptyToNull(request.OriginalPath), CacheOptions(request), cancellationToken);
        if (!result.Success) return ModdingOperationResult.Failure(result.Error ?? "Patch validation failed.");

        var statistics = result.Manifest?.Statistics;
        var summary = statistics is null
            ? "Patch is valid."
            : $"Patch is valid: {statistics.TotalChanged} changed, {statistics.TotalNew} new, {statistics.TotalDeleted} deleted.";
        return new(true, summary, []);
    }

    private async Task<ModdingOperationResult> MergeAsync(ModdingOperationRequest request, CancellationToken cancellationToken)
    {
        var error = RequireFile(request.OriginalPath, "Original data file") ?? RequireAtLeast(request.InputPaths, 2, "patches") ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);

        LogService.SetOperation("Merging patches");
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "g3mtool-gui", Guid.NewGuid().ToString("N"));
        List<string> inputs = [];
        MergeResult result;
        try
        {
            foreach (var input in request.InputPaths)
                inputs.Add(Path.GetExtension(input).Equals(".csx", StringComparison.OrdinalIgnoreCase)
                    ? await MaterializeInputAsync(request.OriginalPath, input, Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N")), cancellationToken)
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
            }, cancellationToken);
        }
        finally
        {
            DeleteDirectory(temporaryDirectory);
        }
        if (!result.Success) return ModdingOperationResult.Failure(result.Error ?? "Patch merge failed.");

        List<string> outputs = [.. new[] { result.OutputPath, EmptyToNull(request.PrimaryPath) }.OfType<string>()];
        var summary = result.TotalConflicts == 0
            ? "Patches merged without conflicts."
            : $"Patches merged with {result.TotalConflicts} conflict(s); {result.AutoMerged} resolved automatically.";
        return new(true, summary, outputs);
    }

    private static async Task<ModdingOperationResult> CompareAsync(ModdingOperationRequest request, CancellationToken cancellationToken)
    {
        var error = RequireFiles(request.OriginalPath, request.PrimaryPath) ?? RequireOutput(request.OutputPath) ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);

        LogService.SetOperation("Comparing files");
        var result = await DiffService.CompareAsync(
            request.OriginalPath, request.PrimaryPath, request.OutputPath,
            request.FullReport ? DiffReportMode.Full : DiffReportMode.Standard,
            CacheOptions(request), cancellationToken);
        return result.Success
            ? new(true, $"Report created: {result.DifferenceCount} difference(s).", [request.OutputPath])
            : ModdingOperationResult.Failure(result.Error ?? "Comparison failed.");
    }

    private static async Task<ModdingOperationResult> InspectAsync(ModdingOperationRequest request, CancellationToken cancellationToken)
    {
        var error = RequireFile(request.PrimaryPath, "File") ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);

        LogService.SetOperation("Inspecting file");
        if (IsPatchExtension(Path.GetExtension(request.PrimaryPath)))
        {
            var patch = await PatchService.ValidatePatchAsync(request.PrimaryPath, cacheOptions: CacheOptions(request), cancellationToken: cancellationToken);
            if (!patch.Success) return ModdingOperationResult.Failure(patch.Error ?? "Patch inspection failed.");
            var stats = patch.Manifest?.Statistics;
            return new(true, stats is null ? "Patch is valid." : $"Patch: {stats.TotalChanged} changed, {stats.TotalNew} new, {stats.TotalDeleted} deleted.", []);
        }

        var snapshot = G3MCacheService.TryReadDataInfoSnapshot(request.PrimaryPath, CacheOptions(request));
        if (snapshot is null)
        {
            using FileStream stream = new(request.PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            using var data = await GameMakerIO.ReadAsync(stream, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            snapshot = G3MCacheService.BuildInfoSnapshot(request.PrimaryPath, data);
            cancellationToken.ThrowIfCancellationRequested();
            await G3MCacheService.WriteDataInfoCacheAsync(request.PrimaryPath, snapshot, CacheOptions(request));
        }
        LogService.Info($"Game: {snapshot.Game}");
        LogService.Info($"Bytecode: {snapshot.BytecodeVersion}");
        foreach ((var type, var count) in snapshot.ResourceCounts.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            LogService.Info($"{type}: {count:N0}");
        return new(true, $"{snapshot.Game} · bytecode {snapshot.BytecodeVersion} · {snapshot.ResourceCounts.Values.Sum():N0} resources.", []);
    }
    // The original, cache and fallback choices come from the request; a batch passes each input and output of its own.
    private async Task<ModdingOperationResult> CreatePatchFileAsync(ModdingOperationRequest request, string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "g3mtool-gui", Guid.NewGuid().ToString("N"));
        try
        {
            var materialized = await MaterializeInputAsync(request.OriginalPath, inputPath, temporaryDirectory, cancellationToken);
            if (request.CreateXDelta)
            {
                return await CreateXDeltaFileAsync(request.OriginalPath, materialized, outputPath, cancellationToken);
            }
            var result = await PatchService.CreatePatchAsync(request.OriginalPath, materialized, outputPath, includeXdeltaFallback: request.IncludeXDeltaFallback, cacheOptions: CacheOptions(request), cancellationToken: cancellationToken);
            return result.Success
                ? new(true, DescribePatchCreated(result), [outputPath])
                : ModdingOperationResult.Failure(result.Error ?? "Patch creation failed.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ModdingOperationResult.Failure(exception.Message);
        }
        finally
        {
            DeleteDirectory(temporaryDirectory);
        }
    }

    private async Task<string> MaterializeInputAsync(string originalPath, string inputPath, string temporaryDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(temporaryDirectory);
        if (!Path.GetExtension(inputPath).Equals(".csx", StringComparison.OrdinalIgnoreCase))
            return await PatchInputService.MaterializeDataAsync(originalPath, inputPath, temporaryDirectory);

        var output = Path.Combine(temporaryDirectory, "script-output" + Path.GetExtension(originalPath));
        var result = await ScriptExecutionService.RunAsync(inputPath, originalPath, output, [], _interaction, cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "Script execution failed.");
        PatchInputService.CopyExternalAudioGroups(originalPath, output);
        return output;
    }

    private static string DescribePatchCreated(PatchCreateResult result)
    {
        var statistics = result.Statistics;
        return statistics is null
            ? "Patch created."
            : $"Patch created: {statistics.TotalChanged} changed, {statistics.TotalNew} new, {statistics.TotalDeleted} deleted.";
    }


    private static bool IsPatchExtension(string extension) => extension.Equals(".g3mpatch", StringComparison.OrdinalIgnoreCase) || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);

    private static bool IsXDelta(string path) => Path.GetExtension(path).Equals(".xdelta", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".vcdiff", StringComparison.OrdinalIgnoreCase);
}
