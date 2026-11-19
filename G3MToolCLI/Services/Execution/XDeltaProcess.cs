namespace G3MToolCLI.Services.Execution;

/// <summary>Result of an xdelta run. <see cref="Output"/> is standard output and standard error together.</summary>
internal sealed record XDeltaRun(bool Success, string Output, string? Error);

/// <summary>
/// Runs xdelta as a child process that a token can kill with its children, which the library's service cannot do.
/// This file is shared by the CLI and the GUI project (linked), so both build the same arguments and apply the same checks and timeout.
/// </summary>
internal static class XDeltaProcess
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public static Task<XDeltaRun> CreateAsync(string executable, string originalPath, string modifiedPath, string outputPath, CancellationToken cancellationToken) =>
        RunFilesAsync(executable, "-e", originalPath, "Original", modifiedPath, "Modified", outputPath, cancellationToken);

    public static Task<XDeltaRun> ApplyAsync(string executable, string originalPath, string patchPath, string outputPath, CancellationToken cancellationToken) =>
        RunFilesAsync(executable, "-d", originalPath, "Original", patchPath, "Patch", outputPath, cancellationToken);

    /// <summary>Runs xdelta with the given arguments. Cancelling kills the process tree and throws <see cref="OperationCanceledException"/>.</summary>
    public static async Task<XDeltaRun> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (!File.Exists(executable)) return new(false, string.Empty, "xdelta executable not found: " + executable);
        var limit = timeout ?? DefaultTimeout;
        System.Diagnostics.ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null) return new(false, string.Empty, "Failed to start xdelta process");
        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutSource = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // the process ended on its own right before the kill
            }
            try { await Task.WhenAll(standardOutput, standardError).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); }
            catch (TimeoutException)
            {
                // a grandchild still holds the pipes; the result below does not need their text
            }
            if (cancellationToken.IsCancellationRequested) throw;
            return new(false, string.Empty, $"xdelta timed out after {limit.TotalSeconds:F0}s and was terminated");
        }
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode == 0) return new(true, output + error, null);
        var exitMessage = $"xdelta exited with code {process.ExitCode}";
        return new(false, output, string.IsNullOrEmpty(error) ? exitMessage : error.TrimEnd() + Environment.NewLine + exitMessage);
    }

    private static async Task<XDeltaRun> RunFilesAsync(string executable, string mode, string originalPath, string originalName, string inputPath, string inputName, string outputPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(originalPath)) return new(false, string.Empty, $"{originalName} file not found: {originalPath}");
        if (!File.Exists(inputPath)) return new(false, string.Empty, $"{inputName} file not found: {inputPath}");
        var fullOutput = Path.GetFullPath(outputPath);
        var existedBefore = File.Exists(fullOutput);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
            var run = await RunAsync(executable, [mode, "-s", Path.GetFullPath(originalPath), Path.GetFullPath(inputPath), fullOutput], cancellationToken);
            if (!run.Success) DeleteNewFile(fullOutput, existedBefore);
            return run;
        }
        catch (OperationCanceledException)
        {
            DeleteNewFile(fullOutput, existedBefore);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(false, string.Empty, exception.Message);
        }
    }

    // A half-written output that this run created is removed; a file that existed before is xdelta's to refuse or replace.
    private static void DeleteNewFile(string path, bool existedBefore)
    {
        try { if (!existedBefore && File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // best-effort cleanup of a partial output
        }
    }
}
