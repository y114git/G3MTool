using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using G3MToolCLI.Models;

namespace G3MToolCLI.Services;

public sealed class BatchOptions
{
    public required string OriginalPath { get; init; }
    public string? OutDir { get; init; }
    public string? ApplyDir { get; init; }
    public G3MCacheOptions? CacheOptions { get; init; }
    public bool ContinueOnError { get; init; }
    public bool XdeltaFallback { get; init; }
    public bool IncludeXdeltaFallback { get; init; }
    public bool UseCodeMerge { get; init; }
    public bool UsePropertyMerge { get; init; }
    public bool WriteReports { get; init; }
}

public sealed class BatchResult
{
    public bool Success => Failed == 0;
    public int Total { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int Deduplicated { get; set; }
    public List<BatchItemResult> Items { get; } = [];
}

public sealed class BatchItemResult
{
    public int Index { get; init; }
    public required string Kind { get; init; }
    public required string Key { get; init; }
    public required string[] Inputs { get; init; }
    public string[] Outputs { get; init; } = [];
    public bool Success { get; init; }
    public bool Deduplicated { get; init; }
    public string? Error { get; init; }
    public double Seconds { get; init; }
}

public static class BatchPatchService
{
    public static async Task<BatchResult> ApplyBatchAsync(IReadOnlyList<string> patchPaths, BatchOptions options)
    {
        var outDir = RequireOutDir(options);
        Directory.CreateDirectory(outDir);
        ValidateOriginal(options.OriginalPath);
        var inputs = NormalizeExistingFiles(patchPaths, "patch");
        var hashes = await ComputeFileHashesAsync([options.OriginalPath, .. inputs]);
        var originalHash = hashes[Path.GetFullPath(options.OriginalPath)];
        var originalStem = Path.GetFileNameWithoutExtension(options.OriginalPath);
        var dataOutputExtension = GetDataOutputExtension(options.OriginalPath);
        var outputPaths = BuildUniqueOutputPaths(inputs.Select(path =>
            Path.Combine(outDir, $"{SanitizeFileName(originalStem)}_{SanitizeFileName(Path.GetFileNameWithoutExtension(path))}{dataOutputExtension}")));

        var jobs = new List<BatchJob>();
        for (int i = 0; i < inputs.Count; i++)
        {
            var inputHash = hashes[inputs[i]];
            string key = BuildKey("apply", originalHash, inputHash, options.XdeltaFallback.ToString());
            jobs.Add(new BatchJob(i + 1, "apply", key, [inputs[i]], [outputPaths[i]]));
        }

        return await RunDeduplicatedAsync(jobs, options, ExecuteApplyJobAsync);
    }

    public static async Task<BatchResult> CreateBatchAsync(IReadOnlyList<string> modifiedPaths, BatchOptions options)
    {
        var outDir = RequireOutDir(options);
        Directory.CreateDirectory(outDir);
        ValidateOriginal(options.OriginalPath);
        var inputs = NormalizeExistingFiles(modifiedPaths, "modified");
        var hashes = await ComputeFileHashesAsync([options.OriginalPath, .. inputs]);
        var originalHash = hashes[Path.GetFullPath(options.OriginalPath)];
        var outputPaths = BuildUniqueOutputPaths(inputs.Select(path =>
            Path.Combine(outDir, $"{SanitizeFileName(Path.GetFileNameWithoutExtension(path))}.g3mpatch")));

        var jobs = new List<BatchJob>();
        for (int i = 0; i < inputs.Count; i++)
        {
            var inputHash = hashes[inputs[i]];
            string key = BuildKey("create", originalHash, inputHash, options.IncludeXdeltaFallback.ToString());
            jobs.Add(new BatchJob(i + 1, "create", key, [inputs[i]], [outputPaths[i]]));
        }

        return await RunDeduplicatedAsync(jobs, options, ExecuteCreateJobAsync);
    }

    public static async Task<BatchResult> MergeBatchAsync(IReadOnlyList<string> setSpecs, BatchOptions options)
    {
        var applyDir = string.IsNullOrWhiteSpace(options.ApplyDir)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(options.ApplyDir);
        var patchDir = string.IsNullOrWhiteSpace(options.OutDir)
            ? null
            : Path.GetFullPath(options.OutDir);
        Directory.CreateDirectory(applyDir);
        if (patchDir is not null)
            Directory.CreateDirectory(patchDir);
        ValidateOriginal(options.OriginalPath);
        var sets = ParseMergeSets(setSpecs);
        var normalizedSets = sets.Select(set => NormalizeExistingFiles(set, "merge patch")).ToArray();
        var hashes = await ComputeFileHashesAsync([options.OriginalPath, .. normalizedSets.SelectMany(set => set)]);
        var originalHash = hashes[Path.GetFullPath(options.OriginalPath)];
        var dataOutputExtension = GetDataOutputExtension(options.OriginalPath);
        var jobs = new List<BatchJob>();
        var patchOutputCandidates = new List<string>();
        var dataOutputCandidates = new List<string>();

        for (int i = 0; i < normalizedSets.Length; i++)
        {
            var set = normalizedSets[i];
            var setName = BuildMergeSetName(i + 1, set);
            dataOutputCandidates.Add(Path.Combine(applyDir, $"{setName}{dataOutputExtension}"));
            if (patchDir is not null)
                patchOutputCandidates.Add(Path.Combine(patchDir, $"{setName}.g3mpatch"));

            var inputHashes = set.Select(path => hashes[path]);

            string key = BuildKey(
                "merge",
                originalHash,
                string.Join(">", inputHashes),
                options.UseCodeMerge.ToString(),
                options.UsePropertyMerge.ToString());
            jobs.Add(new BatchJob(i + 1, "merge", key, [.. set], []));
        }

        var patchOutputs = BuildUniqueOutputPaths(patchOutputCandidates);
        var dataOutputs = BuildUniqueOutputPaths(dataOutputCandidates);
        for (int i = 0; i < jobs.Count; i++)
        {
            var outputs = patchDir is null
                ? new[] { dataOutputs[i] }
                : new[] { dataOutputs[i], patchOutputs[i] };
            jobs[i] = jobs[i] with { Outputs = outputs };
        }

        return await RunDeduplicatedAsync(jobs, options, ExecuteMergeJobAsync);
    }

    private static async Task<BatchResult> RunDeduplicatedAsync(
        IReadOnlyList<BatchJob> jobs,
        BatchOptions options,
        Func<BatchJob, BatchOptions, Task<BatchItemResult>> executor)
    {
        int concurrency = GetSafeConcurrency();
        if (concurrency > 1 && jobs.Count > 1)
            return await RunIsolatedParallelAsync(jobs, options, concurrency);

        var result = new BatchResult { Total = jobs.Count };
        var completedByKey = new Dictionary<string, BatchItemResult>(StringComparer.Ordinal);

        foreach (var job in jobs)
        {
            if (completedByKey.TryGetValue(job.Key, out var completed))
            {
                try
                {
                    var copiedOutputs = CopyOutputs(completed.Outputs, job.Outputs);
                    var duplicate = new BatchItemResult
                    {
                        Index = job.Index,
                        Kind = job.Kind,
                        Key = job.Key,
                        Inputs = job.Inputs,
                        Outputs = copiedOutputs,
                        Success = true,
                        Deduplicated = true,
                        Seconds = 0
                    };
                    result.Items.Add(duplicate);
                    result.Completed++;
                    result.Deduplicated++;
                    LogService.Info($"[Batch] {job.Kind} #{job.Index}: duplicated from earlier identical job");
                    continue;
                }
                catch (Exception ex)
                {
                    var failedDuplicate = Failed(job, ex.Message, 0, deduplicated: true);
                    result.Items.Add(failedDuplicate);
                    result.Failed++;
                    if (!options.ContinueOnError)
                        break;
                    continue;
                }
            }

            var item = await executor(job, options);
            result.Items.Add(item);
            if (item.Success)
            {
                completedByKey[job.Key] = item;
                result.Completed++;
            }
            else
            {
                result.Failed++;
                if (!options.ContinueOnError)
                    break;
            }
        }

        return result;
    }

    private static int GetSafeConcurrency()
    {
        int cpuLimit = Math.Max(1, Environment.ProcessorCount / 2);
        long memoryBudget = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        int memoryLimit = Math.Max(1, (int)(memoryBudget / (4L * 1024 * 1024 * 1024)));
        return Math.Min(4, Math.Min(cpuLimit, memoryLimit));
    }

    private static async Task<BatchResult> RunIsolatedParallelAsync(
        IReadOnlyList<BatchJob> jobs,
        BatchOptions options,
        int concurrency)
    {
        var uniqueJobs = jobs
            .GroupBy(job => job.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var completed = new ConcurrentDictionary<int, BatchItemResult>();
        int nextIndex = -1;
        int stopRequested = 0;

        LogService.Info($"[Batch] Running up to {concurrency} isolated jobs in parallel");
        var workers = Enumerable.Range(0, Math.Min(concurrency, uniqueJobs.Length)).Select(async _ =>
        {
            while (options.ContinueOnError || Volatile.Read(ref stopRequested) == 0)
            {
                int index = Interlocked.Increment(ref nextIndex);
                if (index >= uniqueJobs.Length)
                    break;

                var job = uniqueJobs[index];
                var item = await ExecuteJobInChildProcessAsync(job, options);
                completed[job.Index] = item;
                if (!item.Success && !options.ContinueOnError)
                    Interlocked.Exchange(ref stopRequested, 1);
            }
        });
        await Task.WhenAll(workers);

        var result = new BatchResult { Total = jobs.Count };
        var completedByKey = new Dictionary<string, BatchItemResult>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (completedByKey.TryGetValue(job.Key, out var source))
            {
                try
                {
                    var outputs = CopyOutputs(source.Outputs, job.Outputs);
                    result.Items.Add(new BatchItemResult
                    {
                        Index = job.Index,
                        Kind = job.Kind,
                        Key = job.Key,
                        Inputs = job.Inputs,
                        Outputs = outputs,
                        Success = true,
                        Deduplicated = true
                    });
                    result.Completed++;
                    result.Deduplicated++;
                }
                catch (Exception ex)
                {
                    result.Items.Add(Failed(job, ex.Message, 0, deduplicated: true));
                    result.Failed++;
                    if (!options.ContinueOnError)
                        break;
                }
                continue;
            }

            if (!completed.TryGetValue(job.Index, out var item))
            {
                if (!options.ContinueOnError)
                    break;
                item = await ExecuteJobInChildProcessAsync(job, options);
            }

            result.Items.Add(item);
            if (item.Success)
            {
                completedByKey[job.Key] = item;
                result.Completed++;
            }
            else
            {
                result.Failed++;
                if (!options.ContinueOnError)
                    break;
            }
        }

        if (!options.ContinueOnError && result.Failed > 0)
        {
            var reported = result.Items.Select(item => item.Index).ToHashSet();
            foreach (var item in completed.Values.Where(item => !reported.Contains(item.Index)))
            {
                foreach (string output in item.Outputs)
                    try { File.Delete(output); } catch { }
            }
        }

        return result;
    }

    private static async Task<BatchItemResult> ExecuteJobInChildProcessAsync(BatchJob job, BatchOptions options)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var startInfo = CreateChildStartInfo();
            foreach (string argument in BuildChildArguments(job, options))
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start isolated G3MTool process");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string output = await outputTask;
            string error = await errorTask;

            if (process.ExitCode != 0)
            {
                string message = string.IsNullOrWhiteSpace(error) ? output : error;
                return Failed(job, message.Trim(), sw.Elapsed.TotalSeconds);
            }

            foreach (string path in job.Outputs)
            {
                if (!File.Exists(path))
                    return Failed(job, $"Expected output was not created: {path}", sw.Elapsed.TotalSeconds);
            }

            var outputs = job.Outputs;
            string? report = GetReportPath(job, options);
            if (report != null && File.Exists(report))
                outputs = [.. outputs, report];
            return Succeeded(job with { Outputs = outputs }, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            return Failed(job, ex.Message, sw.Elapsed.TotalSeconds);
        }
    }

    private static ProcessStartInfo CreateChildStartInfo()
    {
        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to locate the G3MTool executable");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string assemblyPath = Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("Unable to locate the G3MTool assembly");
            startInfo.ArgumentList.Add(assemblyPath);
        }
        return startInfo;
    }

    private static IEnumerable<string> BuildChildArguments(BatchJob job, BatchOptions options)
    {
        var args = new List<string> { "patch", job.Kind, options.OriginalPath };
        args.AddRange(job.Inputs);
        switch (job.Kind)
        {
            case "apply":
            case "create":
                args.Add(job.Outputs[0]);
                if ((job.Kind == "apply" && options.XdeltaFallback) ||
                    (job.Kind == "create" && options.IncludeXdeltaFallback))
                    args.Add("--xdelta-fallback");
                break;
            case "merge":
                string? patchOutput = job.Outputs.FirstOrDefault(IsG3MPatchPath);
                string? dataOutput = job.Outputs.FirstOrDefault(path => !IsG3MPatchPath(path));
                if (patchOutput != null)
                {
                    args.Add("--out");
                    args.Add(patchOutput);
                }
                if (dataOutput != null)
                {
                    args.Add("--apply");
                    args.Add(dataOutput);
                }
                if (options.UseCodeMerge) args.Add("--code");
                if (options.UsePropertyMerge) args.Add("--properties");
                string? report = GetReportPath(job, options);
                if (report != null)
                {
                    args.Add("--report");
                    args.Add(report);
                }
                break;
        }

        string? cacheDirectory = options.CacheOptions?.ReadDirectory;
        if (!string.IsNullOrWhiteSpace(cacheDirectory) &&
            string.Equals(cacheDirectory, options.CacheOptions?.WriteDirectory, StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--cache");
            args.Add(cacheDirectory);
        }
        return args;
    }

    private static string? GetReportPath(BatchJob job, BatchOptions options)
    {
        if (job.Kind != "merge" || !options.WriteReports)
            return null;
        string reportBase = job.Outputs.FirstOrDefault(IsG3MPatchPath)
            ?? job.Outputs.FirstOrDefault()
            ?? "merge";
        return Path.ChangeExtension(reportBase, ".merge_log.md");
    }

    private static async Task<BatchItemResult> ExecuteApplyJobAsync(BatchJob job, BatchOptions options)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string input = job.Inputs[0];
            string output = job.Outputs[0];
            string? tempDir = null;
            try
            {
                tempDir = Path.Combine(Path.GetTempPath(), $"g3mtool_batch_apply_{Guid.NewGuid():N}");
                var materialized = await PatchInputService.MaterializeDataAsync(options.OriginalPath, input, tempDir);
                File.Copy(materialized, output, true);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }

            return Succeeded(job, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            return Failed(job, ex.Message, sw.Elapsed.TotalSeconds);
        }
    }

    private static async Task<BatchItemResult> ExecuteCreateJobAsync(BatchJob job, BatchOptions options)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? tempDir = Path.Combine(Path.GetTempPath(), $"g3mtool_batch_create_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var materialized = await PatchInputService.MaterializeDataAsync(options.OriginalPath, job.Inputs[0], tempDir);
            var createResult = await PatchService.CreatePatchAsync(
                options.OriginalPath,
                materialized,
                job.Outputs[0],
                includeXdeltaFallback: options.IncludeXdeltaFallback,
                cacheOptions: options.CacheOptions);
            TryDeleteDirectory(tempDir);
            return createResult.Success
                ? Succeeded(job, sw.Elapsed.TotalSeconds)
                : Failed(job, createResult.Error ?? "patch create failed", sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            return Failed(job, ex.Message, sw.Elapsed.TotalSeconds);
        }
    }

    private static async Task<BatchItemResult> ExecuteMergeJobAsync(BatchJob job, BatchOptions options)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string? patchOutputPath = job.Outputs.FirstOrDefault(IsG3MPatchPath);
            string? dataOutputPath = job.Outputs.FirstOrDefault(path => !IsG3MPatchPath(path));
            string reportBasePath = patchOutputPath ?? dataOutputPath ?? job.Outputs[0];
            string? reportPath = options.WriteReports
                ? Path.ChangeExtension(reportBasePath, ".merge_log.md")
                : null;
            var mergeResult = await MergeService.MergePatchesAsync(
                options.OriginalPath,
                [.. job.Inputs],
                new MergeOptions
                {
                    OutputPath = patchOutputPath,
                    ApplyPath = dataOutputPath,
                    UseCodeMerge = options.UseCodeMerge,
                    UsePropertyMerge = options.UsePropertyMerge,
                    ReportPath = reportPath,
                    CacheOptions = options.CacheOptions
                });

            if (!mergeResult.Success)
                return Failed(job, mergeResult.Error ?? "patch merge failed", sw.Elapsed.TotalSeconds);

            var outputs = job.Outputs;
            var implicitReportPath = Path.ChangeExtension(reportBasePath, ".merge_log.md");
            if (File.Exists(implicitReportPath))
                outputs = [.. job.Outputs, implicitReportPath];
            return Succeeded(job with { Outputs = outputs }, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            return Failed(job, ex.Message, sw.Elapsed.TotalSeconds);
        }
    }

    private static BatchItemResult Succeeded(BatchJob job, double seconds) => new()
    {
        Index = job.Index,
        Kind = job.Kind,
        Key = job.Key,
        Inputs = job.Inputs,
        Outputs = job.Outputs,
        Success = true,
        Seconds = seconds
    };

    private static BatchItemResult Failed(BatchJob job, string? error, double seconds, bool deduplicated = false) => new()
    {
        Index = job.Index,
        Kind = job.Kind,
        Key = job.Key,
        Inputs = job.Inputs,
        Outputs = job.Outputs,
        Success = false,
        Deduplicated = deduplicated,
        Error = error,
        Seconds = seconds
    };

    private static void ValidateOriginal(string originalPath)
    {
        if (!File.Exists(originalPath))
            throw new FileNotFoundException($"Original data file not found: {originalPath}", originalPath);
    }

    private static string RequireOutDir(BatchOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OutDir))
            throw new ArgumentException("--out-dir is required for this batch command.");
        return Path.GetFullPath(options.OutDir);
    }

    private static bool IsG3MPatchPath(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".g3mpatch", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> NormalizeExistingFiles(IEnumerable<string> paths, string label)
    {
        var result = new List<string>();
        foreach (var path in paths)
        {
            var trimmed = path.Trim();
            if (trimmed.Length == 0)
                throw new ArgumentException($"Empty {label} path is not allowed.");
            var fullPath = Path.GetFullPath(trimmed);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"{label} file not found: {fullPath}", fullPath);
            result.Add(fullPath);
        }
        return result;
    }

    private static List<List<string>> ParseMergeSets(IReadOnlyList<string> setSpecs)
    {
        if (setSpecs.Count == 0)
            throw new ArgumentException("At least one merge set is required.");

        var sets = new List<List<string>>();
        foreach (var spec in setSpecs)
        {
            if (spec.Contains(';'))
                throw new ArgumentException("Merge sets use comma separators only; semicolons are not supported.");
            var parts = spec.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Any(part => part.Length == 0))
                throw new ArgumentException($"Merge set has an empty path: {spec}");
            if (parts.Length < 2)
                throw new ArgumentException($"Merge set must contain at least 2 patches: {spec}");
            sets.Add([.. parts]);
        }
        return sets;
    }

    private static List<string> BuildUniqueOutputPaths(IEnumerable<string> candidates)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var candidate in candidates)
        {
            string unique = candidate;
            var dir = Path.GetDirectoryName(candidate) ?? "";
            var stem = Path.GetFileNameWithoutExtension(candidate);
            var ext = Path.GetExtension(candidate);
            int suffix = 2;
            while (!used.Add(unique))
            {
                unique = Path.Combine(dir, $"{stem}_{suffix}{ext}");
                suffix++;
            }
            result.Add(unique);
        }
        return result;
    }

    private static string BuildMergeSetName(int index, IReadOnlyList<string> inputs)
    {
        var pieces = inputs
            .Select(path => SanitizeFileName(Path.GetFileNameWithoutExtension(path)))
            .Where(piece => piece.Length > 0)
            .Take(5);
        var suffix = string.Join("_", pieces);
        if (suffix.Length > 120)
            suffix = suffix[..120].TrimEnd('_', '.', ' ');
        return suffix.Length == 0
            ? $"merge_{index:000}"
            : $"merge_{index:000}_{suffix}";
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
            builder.Append(invalid.Contains(c) ? '_' : c);
        return builder.ToString().Trim(' ', '.', '_');
    }

    private static string GetDataOutputExtension(string originalPath)
    {
        var extension = Path.GetExtension(originalPath);
        return string.IsNullOrWhiteSpace(extension) ? ".win" : extension;
    }

    private static string BuildKey(params string[] parts) =>
        string.Join("|", parts.Select(part => part.Replace("|", "||", StringComparison.Ordinal)));

    private static async Task<Dictionary<string, string>> ComputeFileHashesAsync(IEnumerable<string> paths)
    {
        var uniquePaths = paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hashes = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
            uniquePaths,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2)) },
            async (path, _) => hashes[path] = await HashService.ComputeFileHashAsync(path));
        return new Dictionary<string, string>(hashes, StringComparer.OrdinalIgnoreCase);
    }

    private static string[] CopyOutputs(IReadOnlyList<string> sourceOutputs, IReadOnlyList<string> targetOutputs)
    {
        if (sourceOutputs.Count < targetOutputs.Count)
            throw new InvalidOperationException("Cannot duplicate batch result: source output count mismatch.");

        var copied = new List<string>();
        for (int i = 0; i < sourceOutputs.Count; i++)
        {
            var source = sourceOutputs[i];
            var target = i < targetOutputs.Count
                ? targetOutputs[i]
                : DeriveDuplicateExtraOutputPath(source, targetOutputs);
            if (!File.Exists(source))
                throw new FileNotFoundException($"Cached batch output missing: {source}", source);
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.Copy(source, target, overwrite: true);
            copied.Add(target);
        }

        return [.. copied];
    }

    private static string DeriveDuplicateExtraOutputPath(string sourceExtraOutput, IReadOnlyList<string> targetOutputs)
    {
        if (targetOutputs.Count == 0)
            throw new InvalidOperationException("Cannot derive duplicate output path without primary output.");

        var sourceName = Path.GetFileName(sourceExtraOutput);
        if (sourceName.EndsWith(".merge_log.md", StringComparison.OrdinalIgnoreCase))
            return Path.ChangeExtension(targetOutputs[0], ".merge_log.md");

        var targetDir = Path.GetDirectoryName(targetOutputs[0]) ?? "";
        return Path.Combine(targetDir, Path.GetFileName(sourceExtraOutput));
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Temporary conversion cleanup is best-effort.
        }
    }

    private sealed record BatchJob(int Index, string Kind, string Key, string[] Inputs, string[] Outputs);
}
