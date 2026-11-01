using System.Diagnostics;

namespace G3MToolCLI.Services.Patching;

internal sealed class BatchOptions
{
    public required string OriginalPath { get; init; }
    public string? OutDir { get; init; }
    public string? ApplyDir { get; init; }
    public G3MCacheOptions? CacheOptions { get; init; }
    public bool ContinueOnError { get; init; }
    public bool XdeltaFallback { get; init; }
    public bool IncludeXdeltaFallback { get; init; }
    public bool CreateXdelta { get; init; }
    public bool UseCodeMerge { get; init; }
    public bool UsePropertyMerge { get; init; }
    public bool WriteReports { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

internal sealed record BatchItemResult
{
    public required int Index { get; init; }
    public required string Kind { get; init; }
    public required string[] Inputs { get; init; }
    public string[] Outputs { get; init; } = [];
    public bool Success { get; init; }
    public bool Deduplicated { get; init; }
    public string? Error { get; init; }
    public double Seconds { get; init; }
}

internal sealed class BatchResult
{
    public bool Success => Failed == 0;
    public int Total { get; init; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int Deduplicated { get; set; }
    public List<BatchItemResult> Items { get; } = [];
}

internal static class BatchPatchService
{
    private sealed record Job(int Index, string Kind, string Key, string[] Inputs, string[] Outputs);

    public static Task<BatchResult> ApplyBatchAsync(IReadOnlyList<string> patchPaths, BatchOptions options)
    {
        var outputDirectory = RequireOutputDirectory(options);
        var inputs = NormalizeFiles(patchPaths, "patch");
        return RunAsync(CreateJobsAsync(inputs, options.OriginalPath, input => Path.Combine(outputDirectory, $"{Name(options.OriginalPath)}_{Name(input)}{Extension(options.OriginalPath)}"), "apply", options.XdeltaFallback), options, ApplyAsync);
    }

    public static Task<BatchResult> CreateBatchAsync(IReadOnlyList<string> modifiedPaths, BatchOptions options)
    {
        var outputDirectory = RequireOutputDirectory(options);
        var inputs = NormalizeFiles(modifiedPaths, "modified");
        var extension = options.CreateXdelta ? ".xdelta" : ".g3mpatch";
        return RunAsync(CreateJobsAsync(inputs, options.OriginalPath, input => Path.Combine(outputDirectory, Name(input) + extension), "create", options.CreateXdelta, options.IncludeXdeltaFallback), options, CreateAsync);
    }

    public static async Task<BatchResult> MergeBatchAsync(IReadOnlyList<string> setSpecs, BatchOptions options)
    {
        ValidateOriginal(options.OriginalPath);
        var applyDirectory = string.IsNullOrWhiteSpace(options.ApplyDir) ? Directory.GetCurrentDirectory() : Path.GetFullPath(options.ApplyDir);
        Directory.CreateDirectory(applyDirectory);
        var patchDirectory = string.IsNullOrWhiteSpace(options.OutDir) ? null : Path.GetFullPath(options.OutDir);
        if (patchDirectory is not null) Directory.CreateDirectory(patchDirectory);

        List<string[]> sets = [.. ParseSets(setSpecs).Select(paths => NormalizeFiles(paths, "merge patch").ToArray())];
        var hashes = await HashAsync([options.OriginalPath, .. sets.SelectMany(set => set)]);
        var originalHash = hashes[Path.GetFullPath(options.OriginalPath)];
        var jobs = new List<Job>(sets.Count);
        var usedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < sets.Count; index++)
        {
            var stem = $"merge_{index + 1:000}_{string.Join("_", sets[index].Select(Name).Take(5))}";
            string[] outputs;
            for (var suffix = 1; ; suffix++)
            {
                var name = suffix == 1 ? stem : $"{stem}_{suffix}";
                var dataOutput = Path.Combine(applyDirectory, name + Extension(options.OriginalPath));
                outputs = patchDirectory is null
                    ? [dataOutput]
                    : [dataOutput, Path.Combine(patchDirectory, name + ".g3mpatch")];
                if (options.WriteReports) outputs = [.. outputs, Path.ChangeExtension(outputs[^1], ".merge_log.md")];
                if (outputs.Any(path => File.Exists(path) || Directory.Exists(path) || usedOutputs.Contains(path))) continue;
                foreach (var path in outputs) usedOutputs.Add(path);
                break;
            }
            jobs.Add(new(index + 1, "merge", Key("merge", originalHash, string.Join(">", sets[index].Select(path => hashes[path])), options.UseCodeMerge, options.UsePropertyMerge), sets[index], outputs));
        }
        return await RunAsync(jobs, options, MergeAsync);
    }

    private static async Task<List<Job>> CreateJobsAsync(IEnumerable<string> inputs, string originalPath, Func<string, string> output, string kind, params object[] keyParts)
    {
        ValidateOriginal(originalPath);
        List<string> inputList = [.. inputs];
        var hashes = await HashAsync([originalPath, .. inputList]);
        var originalHash = hashes[Path.GetFullPath(originalPath)];
        var usedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jobs = new List<Job>(inputList.Count);
        for (var index = 0; index < inputList.Count; index++)
        {
            var input = inputList[index];
            var candidate = output(input);
            var uniqueOutput = UniquePath(candidate, usedOutputs);
            jobs.Add(new(index + 1, kind, Key([.. keyParts, originalHash, hashes[input]]), [input], [uniqueOutput]));
        }
        return jobs;
    }

    private static async Task<BatchResult> RunAsync(Task<List<Job>> jobsTask, BatchOptions options, Func<Job, BatchOptions, Task<BatchItemResult>> execute) => await RunAsync(await jobsTask, options, execute);

    private static async Task<BatchResult> RunAsync(IReadOnlyList<Job> jobs, BatchOptions options, Func<Job, BatchOptions, Task<BatchItemResult>> execute)
    {
        var result = new BatchResult { Total = jobs.Count };
        var completed = new Dictionary<string, BatchItemResult>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            BatchItemResult item;
            var canReuse = job.Inputs.All(path => Path.GetExtension(path).Equals(".g3mpatch", StringComparison.OrdinalIgnoreCase));
            if (canReuse && completed.TryGetValue(job.Key, out var previous))
            {
                try
                {
                    item = previous with { Index = job.Index, Inputs = job.Inputs, Outputs = CopyOutputs(previous.Outputs, job.Outputs), Deduplicated = true, Seconds = 0 };
                    result.Deduplicated++;
                }
                catch (Exception exception)
                {
                    item = Failed(job, exception.Message, 0, deduplicated: true);
                }
            }
            else
            {
                item = await execute(job, options);
                if (item.Success && canReuse) completed.TryAdd(job.Key, item);
            }

            result.Items.Add(item);
            if (item.Success) result.Completed++; else result.Failed++;
            if (!item.Success && !options.ContinueOnError) break;
        }
        return result;
    }

    private static async Task<BatchItemResult> ApplyAsync(Job job, BatchOptions options)
    {
        var timer = Stopwatch.StartNew();
        string? temporaryDirectory = null;
        try
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), $"g3mtool_batch_apply_{Guid.NewGuid():N}");
            if (Path.GetExtension(job.Inputs[0]).Equals(".g3mpatch", StringComparison.OrdinalIgnoreCase))
            {
                var result = await PatchService.ApplyPatchAsync(options.OriginalPath, job.Inputs[0], job.Outputs[0], options.XdeltaFallback, cancellationToken: options.CancellationToken);
                return result.Success ? Succeeded(job, timer.Elapsed.TotalSeconds) : Failed(job, result.Error, timer.Elapsed.TotalSeconds);
            }
            PatchInputService.CopyDataFile(await ScriptPatchInputService.MaterializeDataAsync(options.OriginalPath, job.Inputs[0], temporaryDirectory), job.Outputs[0]);
            return Succeeded(job, timer.Elapsed.TotalSeconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { return Failed(job, exception.Message, timer.Elapsed.TotalSeconds); }
        finally { DeleteDirectory(temporaryDirectory); }
    }

    private static async Task<BatchItemResult> CreateAsync(Job job, BatchOptions options)
    {
        var timer = Stopwatch.StartNew();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"g3mtool_batch_create_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var input = await ScriptPatchInputService.MaterializeDataAsync(options.OriginalPath, job.Inputs[0], temporaryDirectory);
            if (options.CreateXdelta)
            {
                var xdelta = await new XDeltaService().CreatePatchAsync(options.OriginalPath, input, job.Outputs[0]);
                return xdelta.Success ? Succeeded(job, timer.Elapsed.TotalSeconds) : Failed(job, xdelta.Error, timer.Elapsed.TotalSeconds);
            }
            var patch = await PatchService.CreatePatchAsync(options.OriginalPath, input, job.Outputs[0], null, null, null, null, null, options.IncludeXdeltaFallback, options.CacheOptions);
            return patch.Success ? Succeeded(job, timer.Elapsed.TotalSeconds) : Failed(job, patch.Error, timer.Elapsed.TotalSeconds);
        }
        catch (Exception exception) { return Failed(job, exception.Message, timer.Elapsed.TotalSeconds); }
        finally { DeleteDirectory(temporaryDirectory); }
    }

    private static async Task<BatchItemResult> MergeAsync(Job job, BatchOptions options)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var patchOutput = job.Outputs.FirstOrDefault(path => path.EndsWith(".g3mpatch", StringComparison.OrdinalIgnoreCase));
            var dataOutput = job.Outputs[0];
            var report = options.WriteReports ? job.Outputs[^1] : null;
            var merge = await ScriptPatchInputService.MergeAsync(options.OriginalPath, [.. job.Inputs], new MergeOptions
            {
                OutputPath = patchOutput,
                ApplyPath = dataOutput,
                UseCodeMerge = options.UseCodeMerge,
                UsePropertyMerge = options.UsePropertyMerge,
                ReportPath = report,
                CacheOptions = options.CacheOptions
            }, options.CancellationToken);
            if (!merge.Success) return Failed(job, merge.Error, timer.Elapsed.TotalSeconds);
            return Succeeded(job, timer.Elapsed.TotalSeconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { return Failed(job, exception.Message, timer.Elapsed.TotalSeconds); }
    }

    private static async Task<Dictionary<string, string>> HashAsync(IEnumerable<string> paths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)) result.Add(path, await HashService.ComputeFileHashAsync(path));
        return result;
    }

    private static List<string[]> ParseSets(IReadOnlyList<string> specifications)
    {
        if (specifications.Count == 0) throw new ArgumentException("At least one merge set is required.");
        return [.. specifications.Select(specification =>
        {
            if (specification.Contains(';', StringComparison.Ordinal)) throw new ArgumentException("Merge sets use comma separators only; semicolons are not supported.");
            var paths = specification.Split(',', StringSplitOptions.TrimEntries);
            if (paths.Length < 2 || paths.Any(string.IsNullOrEmpty)) throw new ArgumentException("Each merge set must contain at least two paths.");
            return paths;
        })];
    }

    private static List<string> NormalizeFiles(IEnumerable<string> paths, string label)
    {
        var result = new List<string>();
        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path.Trim());
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(fullPath)) throw new FileNotFoundException($"{label} file not found: {fullPath}", fullPath);
            result.Add(fullPath);
        }
        return result;
    }

    private static string RequireOutputDirectory(BatchOptions options)
    {
        ValidateOriginal(options.OriginalPath);
        if (string.IsNullOrWhiteSpace(options.OutDir)) throw new ArgumentException("--out-dir is required for this batch command.");
        var directory = Path.GetFullPath(options.OutDir);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void ValidateOriginal(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Original data file not found: " + path, path);
    }

    private static BatchItemResult Succeeded(Job job, double seconds) => new() { Index = job.Index, Kind = job.Kind, Inputs = job.Inputs, Outputs = job.Outputs, Success = true, Seconds = seconds };
    private static BatchItemResult Failed(Job job, string? error, double seconds, bool deduplicated = false) => new() { Index = job.Index, Kind = job.Kind, Inputs = job.Inputs, Outputs = job.Outputs, Error = error, Deduplicated = deduplicated, Seconds = seconds };
    private static string Name(string path) => Sanitize(Path.GetFileNameWithoutExtension(path));
    private static string Extension(string path) => string.IsNullOrEmpty(Path.GetExtension(path)) ? ".win" : Path.GetExtension(path);
    private static string Key(params object[] values) => string.Join("|", values.Select(value => value?.ToString()?.Replace("|", "||", StringComparison.Ordinal) ?? string.Empty));
    private static string UniquePath(string candidate, ISet<string> used) => UniqueName(candidate, used);
    private static string UniqueName(string candidate, ISet<string> used)
    {
        var directory = Path.GetDirectoryName(candidate) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(candidate);
        var extension = Path.GetExtension(candidate);
        var value = candidate;
        for (var suffix = 2; File.Exists(value) || Directory.Exists(value) || !used.Add(value); suffix++) value = Path.Combine(directory, $"{stem}_{suffix}{extension}");
        return value;
    }
    private static string Sanitize(string value) => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim(' ', '.', '_');
    private static string[] CopyOutputs(string[] source, string[] target)
    {
        if (source.Length != target.Length) throw new InvalidOperationException("Cannot duplicate batch result: source output count mismatch.");
        var copied = new List<string>(source.Length);
        for (var index = 0; index < source.Length; index++)
        {
            var destination = target[index];
            File.Copy(source[index], destination, false);
            copied.Add(destination);
        }
        return [.. copied];
    }
    private static void DeleteDirectory(string? path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Directory.Delete(path, true); }
        catch { }
    }
}
