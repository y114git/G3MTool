using G3MToolCLI.Services.Execution;
using G3MLib.Modding.Logging;

namespace G3MToolGUI.Services;

public sealed partial class ModdingOperationService
{
    private static async Task<ModdingOperationResult> CreateXDeltaAsync(ModdingOperationRequest request, CancellationToken cancellationToken)
    {
        var error = RequireFiles(request.OriginalPath, request.PrimaryPath) ?? RequireOutput(request.OutputPath) ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);
        LogService.SetOperation("Creating XDelta patch");
        return await CreateXDeltaFileAsync(request.OriginalPath, request.PrimaryPath, request.OutputPath, cancellationToken);
    }

    private static Task<ModdingOperationResult> CreateXDeltaFileAsync(string originalPath, string modifiedPath, string outputPath, CancellationToken cancellationToken) =>
        RunXDeltaFileAsync(executable => XDeltaProcess.CreateAsync(executable, originalPath, modifiedPath, outputPath, cancellationToken), outputPath, "XDelta patch created.", "XDelta creation failed.");

    private static async Task<ModdingOperationResult> ApplyXDeltaAsync(ModdingOperationRequest request, CancellationToken cancellationToken)
    {
        var error = RequireFiles(request.OriginalPath, request.PrimaryPath) ?? RequireOutput(request.OutputPath) ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);
        LogService.SetOperation("Applying XDelta patch");
        return await RunXDeltaFileAsync(executable => XDeltaProcess.ApplyAsync(executable, request.OriginalPath, request.PrimaryPath, request.OutputPath, cancellationToken), request.OutputPath, "XDelta patch applied.", "XDelta application failed.");
    }

    // The library's xdelta service takes no token, so xdelta runs through the shared child-process helper that Cancel can kill.
    private static async Task<ModdingOperationResult> RunXDeltaFileAsync(Func<string, Task<XDeltaRun>> run, string outputPath, string success, string failure)
    {
        var result = await run(EmbeddedXDeltaPathProvider.Resolve());
        LogOutput(result.Output);
        return result.Success ? new(true, success, [outputPath]) : ModdingOperationResult.Failure($"{failure} {result.Error}");
    }

    private static async Task<ModdingOperationResult> RunXDeltaAsync(ModdingOperationRequest request, CancellationToken cancellationToken)
    {
        if (request.InputPaths.Count == 0) return ModdingOperationResult.Failure("Enter one XDelta argument per line.");
        LogService.SetOperation("Running XDelta");
        var result = await XDeltaProcess.RunAsync(EmbeddedXDeltaPathProvider.Resolve(), request.InputPaths, cancellationToken);
        LogOutput(result.Output);
        return result.Success ? new(true, "XDelta completed.", []) : ModdingOperationResult.Failure("XDelta failed. " + result.Error);
    }

    private static void LogOutput(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) LogService.Info(line);
    }

    private async Task<ModdingOperationResult> RunScriptAsync(ModdingOperationRequest request, CancellationToken cancellationToken)
    {
        var error = RequireFile(request.PrimaryPath, "Script") ?? string.Empty;
        if (error.Length != 0) return ModdingOperationResult.Failure(error);
        LogService.SetOperation("Running script");
        var result = await ScriptExecutionService.RunAsync(request.PrimaryPath, EmptyToNull(request.OriginalPath), EmptyToNull(request.OutputPath), request.InputPaths, _interaction, cancellationToken);
        return result.Success
            ? new(true, "Script completed.", string.IsNullOrWhiteSpace(request.OutputPath) ? [] : [request.OutputPath])
            : ModdingOperationResult.Failure(result.Error ?? "Script execution failed.");
    }

    private static async Task<ModdingOperationResult> RunProgramAsync(ModdingOperationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.PrimaryPath)) return ModdingOperationResult.Failure("Enter a program path or command name.");
        LogService.SetOperation("Running program");
        var result = await ExternalProcessService.RunAsync(request.PrimaryPath, request.InputPaths, cancellationToken: cancellationToken);
        return result.ExitCode == 0
            ? new(true, "Program completed.", [])
            : ModdingOperationResult.Failure($"Program exited with code {result.ExitCode}.");
    }
}
