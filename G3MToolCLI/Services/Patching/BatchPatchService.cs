using System.Diagnostics;
using System.Text;

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
        string outputDirectory = RequireOutputDirectory(options);
        List<string> inputs = NormalizeFiles(patchPaths, "patch");
        return RunAsync(CreateJobsAsync(inputs, options.OriginalPath, input => Path.Combine(outputDirectory, $"{Name(options.OriginalPath)}_{Name(input)}{Extension(options.OriginalPath)}"), "apply", options.XdeltaFallback), options, ApplyAsync);
    }

    public static Task<BatchResult> CreateBatchAsync(IReadOnlyList<string> modifiedPaths, BatchOptions options)
    {
        string outputDirectory = RequireOutputDirectory(options);
        List<string> inputs = NormalizeFiles(modifiedPaths, "modified");
        string extension = options.CreateXdelta ? ".xdelta" : ".g3mpatch";
        return RunAsync(CreateJobsAsync(inputs, options.OriginalPath, input => Path.Combine(outputDirectory, Name(input) + extension), "create", options.CreateXdelta, options.IncludeXdeltaFallback), options, CreateAsync);
    }

    public static async Task<BatchResult> MergeBatchAsync(IReadOnlyList<string> setSpecs, BatchOptions options)
    {
        ValidateOriginal(options.OriginalPath);
        string applyDirectory = string.IsNullOrWhiteSpace(options.ApplyDir) ? Directory.GetCurrentDirectory() : Path.GetFullPath(options.ApplyDir);
        Directory.CreateDirectory(applyDirectory);
        string? patchDirectory = string.IsNullOrWhiteSpace(options.OutDir) ? null : Path.GetFullPath(options.OutDir);
        if (patchDirectory is not null) Directory.CreateDirectory(patchDirectory);

        List<string[]> sets = ParseSets(setSpecs).Select(paths => NormalizeFiles(paths, "merge patch").ToArray()).ToList();
        Dictionary<string, string> hashes = await HashAsync([options.OriginalPath, .. sets.SelectMany(set => set)]);
        string originalHash = hashes[Path.GetFullPath(options.OriginalPath)];
        var jobs = new List<Job>(sets.Count);
        var usedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < sets.Count; index++)
        {
            string stem = $"merge_{index + 1:000}_{string.Join("_", sets[index].Select(Name).Take(5))}";
            string[] outputs;
            for (int suffix = 1; ; suffix++)
            {
                string name = suffix == 1 ? stem : $"{stem}_{suffix}";
                string dataOutput = Path.Combine(applyDirectory, name + Extension(options.OriginalPath));
                outputs = patchDirectory is null
                    ? [dataOutput]
                    : [dataOutput, Path.Combine(patchDirectory, name + ".g3mpatch")];
                if (options.WriteReports) outputs = [.. outputs, Path.ChangeExtension(outputs[^1], ".merge_log.md")];
                if (outputs.Any(path => File.Exists(path) || Directory.Exists(path) || usedOutputs.Contains(path))) continue;
                foreach (string path in outputs) usedOutputs.Add(path);
                break;
            }
            jobs.Add(new(index + 1, "merge", Key("merge", originalHash, string.Join(">", sets[index].Select(path => hashes[path])), options.UseCodeMerge, options.UsePropertyMerge), sets[index], outputs));
        }
        return await RunAsync(jobs, options, MergeAsync);
    }

    private static async Task<List<Job>> CreateJobsAsync(IEnumerable<string> inputs, string originalPath, Func<string, string> output, string kind, params object[] keyParts)
    {
        ValidateOriginal(originalPath);
        List<string> inputList = inputs.ToList();
        Dictionary<string, string> hashes = await HashAsync([originalPath, .. inputList]);
        string originalHash = hashes[Path.GetFullPath(originalPath)];
        var usedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jobs = new List<Job>(inputList.Count);
        for (int index = 0; index < inputList.Count; index++)
        {
            string input = inputList[index];
            string candidate = output(input);
            string uniqueOutput = UniquePath(candidate, usedOutputs);
            jobs.Add(new(index + 1, kind, Key([.. keyParts, originalHash, hashes[input]]), [input], [uniqueOutput]));
        }
        return jobs;
    }

    private static async Task<BatchResult> RunAsync(Task<List<Job>> jobsTask, BatchOptions options, Func<Job, BatchOptions, Task<BatchItemResult>> execute) => await RunAsync(await jobsTask, options, execute);

    private static async Task<BatchResult> RunAsync(IReadOnlyList<Job> jobs, BatchOptions options, Func<Job, BatchOptions, Task<BatchItemResult>> execute)
    {
        var result = new BatchResult { Total = jobs.Count };
        var completed = new Dictionary<string, BatchItemResult>(StringComparer.Ordinal);
        foreach (Job job in jobs)
        {
            BatchItemResult item;
            bool canReuse = job.Inputs.All(path => Path.GetExtension(path).Equals(".g3mpatch", StringComparison.OrdinalIgnoreCase));
            if (canReuse && completed.TryGetValue(job.Key, out BatchItemResult? previous))
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
        Stopwatch timer = Stopwatch.StartNew();
        string? temporaryDirectory = null;
        try
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), $"g3mtool_batch_apply_{Guid.NewGuid():N}");
            if (Path.GetExtension(job.Inputs[0]).Equals(".g3mpatch", StringComparison.OrdinalIgnoreCase))
            {
                PatchApplyResult result = await PatchService.ApplyPatchAsync(options.OriginalPath, job.Inputs[0], job.Outputs[0], options.XdeltaFallback);
                return result.Success ? Succeeded(job, timer.Elapsed.TotalSeconds) : Failed(job, result.Error, timer.Elapsed.TotalSeconds);
            }
            PatchInputService.CopyDataFile(await ScriptPatchInputService.MaterializeDataAsync(options.OriginalPath, job.Inputs[0], temporaryDirectory), job.Outputs[0]);
            return Succeeded(job, timer.Elapsed.TotalSeconds);
        }
        catch (Exception exception) { return Failed(job, exception.Message, timer.Elapsed.TotalSeconds); }
        finally { DeleteDirectory(temporaryDirectory); }
    }

    private static async Task<BatchItemResult> CreateAsync(Job job, BatchOptions options)
    {
        Stopwatch timer = Stopwatch.StartNew();
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"g3mtool_batch_create_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            string input = await ScriptPatchInputService.MaterializeDataAsync(options.OriginalPath, job.Inputs[0], temporaryDirectory);
            if (options.CreateXdelta)
            {
                XDeltaResult xdelta = await new XDeltaService().CreatePatchAsync(options.OriginalPath, input, job.Outputs[0]);
                return xdelta.Success ? Succeeded(job, timer.Elapsed.TotalSeconds) : Failed(job, xdelta.Error, timer.Elapsed.TotalSeconds);
            }
            PatchCreateResult patch = await PatchService.CreatePatchAsync(options.OriginalPath, input, job.Outputs[0], null, null, null, null, null, options.IncludeXdeltaFallback, options.CacheOptions);
            return patch.Success ? Succeeded(job, timer.Elapsed.TotalSeconds) : Failed(job, patch.Error, timer.Elapsed.TotalSeconds);
        }
        catch (Exception exception) { return Failed(job, exception.Message, timer.Elapsed.TotalSeconds); }
        finally { DeleteDirectory(temporaryDirectory); }
    }

    private static async Task<BatchItemResult> MergeAsync(Job job, BatchOptions options)
    {
        Stopwatch timer = Stopwatch.StartNew();
        try
        {
            string? patchOutput = job.Outputs.FirstOrDefault(path => path.EndsWith(".g3mpatch", StringComparison.OrdinalIgnoreCase));
            string dataOutput = job.Outputs[0];
            string? report = options.WriteReports ? job.Outputs[^1] : null;
            MergeResult merge = await ScriptPatchInputService.MergeAsync(options.OriginalPath, job.Inputs.ToList(), new MergeOptions
            {
                OutputPath = patchOutput,
                ApplyPath = dataOutput,
                UseCodeMerge = options.UseCodeMerge,
                UsePropertyMerge = options.UsePropertyMerge,
                ReportPath = report,
                CacheOptions = options.CacheOptions
            });
            if (!merge.Success) return Failed(job, merge.Error, timer.Elapsed.TotalSeconds);
            return Succeeded(job, timer.Elapsed.TotalSeconds);
        }
        catch (Exception exception) { return Failed(job, exception.Message, timer.Elapsed.TotalSeconds); }
    }

    private static async Task<Dictionary<string, string>> HashAsync(IEnumerable<string> paths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)) result.Add(path, await HashService.ComputeFileHashAsync(path));
        return result;
    }

    private static List<string[]> ParseSets(IReadOnlyList<string> specifications)
    {
        if (specifications.Count == 0) throw new ArgumentException("At least one merge set is required.");
        return specifications.Select(specification =>
        {
            if (specification.Contains(';')) throw new ArgumentException("Merge sets use comma separators only; semicolons are not supported.");
            string[] paths = specification.Split(',', StringSplitOptions.TrimEntries);
            if (paths.Length < 2 || paths.Any(string.IsNullOrEmpty)) throw new ArgumentException("Each merge set must contain at least two paths.");
            return paths;
        }).ToList();
    }

    private static List<string> NormalizeFiles(IEnumerable<string> paths, string label)
    {
        var result = new List<string>();
        foreach (string path in paths)
        {
            string fullPath = Path.GetFullPath(path.Trim());
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(fullPath)) throw new FileNotFoundException($"{label} file not found: {fullPath}", fullPath);
            result.Add(fullPath);
        }
        return result;
    }

    private static string RequireOutputDirectory(BatchOptions options)
    {
        ValidateOriginal(options.OriginalPath);
        if (string.IsNullOrWhiteSpace(options.OutDir)) throw new ArgumentException("--out-dir is required for this batch command.");
        string directory = Path.GetFullPath(options.OutDir);
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
        string directory = Path.GetDirectoryName(candidate) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(candidate);
        string extension = Path.GetExtension(candidate);
        string value = candidate;
        for (int suffix = 2; File.Exists(value) || Directory.Exists(value) || !used.Add(value); suffix++) value = Path.Combine(directory, $"{stem}_{suffix}{extension}");
        return value;
    }
    private static string Sanitize(string value) => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim(' ', '.', '_');
    private static string[] CopyOutputs(IReadOnlyList<string> source, IReadOnlyList<string> target)
    {
        if (source.Count != target.Count) throw new InvalidOperationException("Cannot duplicate batch result: source output count mismatch.");
        var copied = new List<string>(source.Count);
        for (int index = 0; index < source.Count; index++)
        {
            string destination = target[index];
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
