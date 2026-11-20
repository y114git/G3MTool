using G3MLib.Modding.Logging;

namespace G3MToolGUI.Services;

public sealed partial class ModdingOperationService
{
    private async Task<ModdingOperationResult> BatchCreateAsync(ModdingOperationRequest request, CancellationToken cancellationToken)
    {
        var error = RequireFile(request.OriginalPath, "Original data file") ?? RequireAtLeast(request.InputPaths, 1, "inputs") ?? RequireDirectory(request.OutputPath) ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);
        if (request.IncludeXDeltaFallback && request.CreateXDelta) return ModdingOperationResult.Failure("XDelta output cannot include an XDelta fallback.");
        LogService.SetOperation("Creating patch batch");
        Directory.CreateDirectory(request.OutputPath);
        List<string> outputs = [];
        var completed = 0;
        var failed = 0;
        foreach ((var input, var index) in request.InputPaths.Select((path, index) => (path, index + 1)))
        {
            if (cancellationToken.IsCancellationRequested) return Cancelled(completed, request.InputPaths.Count, outputs, failed);
            var output = UniquePath(Path.Combine(request.OutputPath, Path.GetFileNameWithoutExtension(input) + (request.CreateXDelta ? ".xdelta" : ".g3mpatch")));
            ModdingOperationResult item;
            try { item = await CreatePatchFileAsync(request, input, output, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Cancelled(completed, request.InputPaths.Count, outputs, failed); }
            if (item.Success) { completed++; outputs.Add(output); }
            else if (cancellationToken.IsCancellationRequested) return Cancelled(completed, request.InputPaths.Count, outputs, failed);
            else { failed++; LogService.Error(item.Summary); if (!request.ContinueOnError) break; }
            LogService.Progress(index, request.InputPaths.Count);
        }
        return BatchSummary(completed, failed, request.InputPaths.Count, outputs);
    }

    private async Task<ModdingOperationResult> BatchApplyAsync(ModdingOperationRequest request, CancellationToken cancellationToken)
    {
        var error = RequireFile(request.OriginalPath, "Original data file") ?? RequireAtLeast(request.InputPaths, 1, "patches") ?? RequireDirectory(request.OutputPath) ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);
        LogService.SetOperation("Applying patch batch");
        Directory.CreateDirectory(request.OutputPath);
        List<string> outputs = [];
        var completed = 0;
        var failed = 0;
        var stem = Path.GetFileNameWithoutExtension(request.OriginalPath);
        var extension = Path.GetExtension(request.OriginalPath);
        foreach ((var input, var index) in request.InputPaths.Select((path, index) => (path, index + 1)))
        {
            if (cancellationToken.IsCancellationRequested) return Cancelled(completed, request.InputPaths.Count, outputs, failed);
            var output = UniquePath(Path.Combine(request.OutputPath, stem + "_" + Path.GetFileNameWithoutExtension(input) + extension));
            var item = await ExecuteAsync(new ModdingOperationRequest
            {
                Operation = ModdingOperation.ApplyPatch,
                OriginalPath = request.OriginalPath,
                PrimaryPath = input,
                OutputPath = output,
                CacheDirectory = request.CacheDirectory,
                IncludeXDeltaFallback = request.IncludeXDeltaFallback
            }, cancellationToken);
            if (item.Success) { completed++; outputs.Add(output); }
            else if (cancellationToken.IsCancellationRequested) return Cancelled(completed, request.InputPaths.Count, outputs, failed);
            else { failed++; LogService.Error(item.Summary); if (!request.ContinueOnError) break; }
            LogService.Progress(index, request.InputPaths.Count);
        }
        return BatchSummary(completed, failed, request.InputPaths.Count, outputs);
    }

    private async Task<ModdingOperationResult> BatchMergeAsync(ModdingOperationRequest request, CancellationToken cancellationToken)
    {
        var error = RequireFile(request.OriginalPath, "Original data file") ?? (request.InputPaths.Count == 0 ? "Enter at least one merge set." : null) ?? RequireDirectory(request.PrimaryPath) ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);
        LogService.SetOperation("Merging patch batches");
        Directory.CreateDirectory(request.PrimaryPath);
        if (!string.IsNullOrWhiteSpace(request.OutputPath)) Directory.CreateDirectory(request.OutputPath);
        List<string> outputs = [];
        var completed = 0;
        var failed = 0;
        for (var index = 0; index < request.InputPaths.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested) return Cancelled(completed, request.InputPaths.Count, outputs, failed);
            List<string> inputs = [.. request.InputPaths[index].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            var stem = "merge_" + (index + 1).ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
            string dataOutput;
            string patchOutput;
            for (var suffix = 1; ; suffix++)
            {
                var name = suffix == 1 ? stem : stem + "_" + suffix;
                dataOutput = Path.Combine(request.PrimaryPath, name + Path.GetExtension(request.OriginalPath));
                patchOutput = string.IsNullOrWhiteSpace(request.OutputPath) ? string.Empty : Path.Combine(request.OutputPath, name + ".g3mpatch");
                var report = request.FullReport ? ReportPath(patchOutput, dataOutput) : null;
                if (!new[] { dataOutput, patchOutput, report }.Any(path => File.Exists(path) || Directory.Exists(path))) break;
            }
            var item = await ExecuteAsync(new ModdingOperationRequest
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
            }, cancellationToken);
            if (item.Success) { completed++; outputs.AddRange(item.Outputs); }
            else if (cancellationToken.IsCancellationRequested) return Cancelled(completed, request.InputPaths.Count, outputs, failed);
            else { failed++; LogService.Error(item.Summary); if (!request.ContinueOnError) break; }
            LogService.Progress(index + 1, request.InputPaths.Count);
        }
        return BatchSummary(completed, failed, request.InputPaths.Count, outputs);
    }
    private static ModdingOperationResult Cancelled(int completed, int total, IReadOnlyList<string> outputs, int failed = 0) => new(false, failed == 0 ? $"Cancelled; {completed}/{total} completed." : $"Cancelled; {completed}/{total} completed; {failed} failed.", outputs);

    private static ModdingOperationResult BatchSummary(int completed, int failed, int total, IReadOnlyList<string> outputs) => new(failed == 0 && completed == total, $"{completed}/{total} completed; {failed} failed.", outputs);

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(directory, fileName + "_" + index + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}
