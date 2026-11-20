using G3MLib.Modding.Caching;
using G3MLib.Modding.Logging;

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

public sealed partial class ModdingOperationService : IDisposable
{
    public event Action<ModdingLogMessage>? LogReceived;
    public event Action<ModdingProgress>? ProgressChanged;

    private readonly ScriptInteraction? _interaction;

    public ModdingOperationService(ScriptInteraction? interaction = null)
    {
        _interaction = interaction;
    }

    public async Task<ModdingOperationResult> RunAsync(ModdingOperationRequest request, CancellationToken cancellationToken = default)
    {
        using var logging = LogService.BeginScope(OnLogMessage, OnProgress, verbose: true);
        try
        {
            return await ExecuteAsync(request, cancellationToken);
        }
        finally
        {
            LogService.ProgressComplete();
        }
    }

    /// <summary>Runs one operation inside the caller's logging scope. Batches call it per item, so progress and the log stay in one scope.</summary>
    private async Task<ModdingOperationResult> ExecuteAsync(ModdingOperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.Operation is not (ModdingOperation.Inspect or ModdingOperation.ValidatePatch or ModdingOperation.Compare or ModdingOperation.RunProgram))
                EmbeddedXDeltaPathProvider.TryConfigure();
            return request.Operation switch
            {
                ModdingOperation.CreatePatch => await CreatePatchAsync(request, cancellationToken),
                ModdingOperation.ApplyPatch => await ApplyPatchAsync(request, cancellationToken),
                ModdingOperation.ValidatePatch => await ValidatePatchAsync(request, cancellationToken),
                ModdingOperation.MergePatches => await MergeAsync(request, cancellationToken),
                ModdingOperation.Compare => await CompareAsync(request, cancellationToken),
                ModdingOperation.Inspect => await InspectAsync(request, cancellationToken),
                ModdingOperation.BatchCreate => await BatchCreateAsync(request, cancellationToken),
                ModdingOperation.BatchApply => await BatchApplyAsync(request, cancellationToken),
                ModdingOperation.BatchMerge => await BatchMergeAsync(request, cancellationToken),
                ModdingOperation.CreateXDelta => await CreateXDeltaAsync(request, cancellationToken),
                ModdingOperation.ApplyXDelta => await ApplyXDeltaAsync(request, cancellationToken),
                ModdingOperation.RunXDelta => await RunXDeltaAsync(request, cancellationToken),
                ModdingOperation.RunScript => await RunScriptAsync(request, cancellationToken),
                ModdingOperation.RunProgram => await RunProgramAsync(request, cancellationToken),
                _ => ModdingOperationResult.Failure("Unsupported operation.")
            };
        }
        catch (OperationCanceledException)
        {
            LogService.Warning("Cancelled.");
            return ModdingOperationResult.Failure("Cancelled.");
        }
        catch (Exception exception)
        {
            LogService.Error(exception.Message);
            return ModdingOperationResult.Failure(exception.Message);
        }
    }

    public void Dispose()
    {
        LogReceived = null;
        ProgressChanged = null;
    }

    private static G3MCacheOptions CacheOptions(ModdingOperationRequest request) => G3MCacheOptions.FromDirectory(EmptyToNull(request.CacheDirectory));

    private static string? ReportPath(string outputPath, string applyPath)
    {
        var path = EmptyToNull(outputPath) ?? EmptyToNull(applyPath);
        return path is null ? null : Path.ChangeExtension(path, ".merge.md");
    }

    private static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { LogService.Warning($"Could not delete the temporary folder {path}."); }
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string? RequireFile(string path, string name) => File.Exists(path) ? null : $"{name} was not found.";
    private static string? RequireFiles(string original, string input) => RequireFile(original, "Original data file") ?? RequireFile(input, "Input file");
    private static string? RequireOutput(string path) => string.IsNullOrWhiteSpace(path) ? "Choose an output path." : null;
    private static string? RequireDirectory(string path) => string.IsNullOrWhiteSpace(path) ? "Choose an output directory." : null;
    private static string? RequireAtLeast(IReadOnlyList<string> paths, int count, string name) => paths.Count < count ? $"Choose at least {count} {name}." : paths.FirstOrDefault(path => !File.Exists(path)) is { } missing ? $"File was not found: {missing}" : null;
    private void OnLogMessage(ModdingLogMessage message) => LogReceived?.Invoke(message);
    private void OnProgress(ModdingProgress progress) => ProgressChanged?.Invoke(progress);
}
