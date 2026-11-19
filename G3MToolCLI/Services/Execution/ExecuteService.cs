using G3MLib.DataFile;
using G3MLib.DataFile.Scripting;
using G3MToolCLI.Models.Scripting;
using G3MToolCLI.Services.Logging;

namespace G3MToolCLI.Services.Execution;

public static class ExecuteService
{
    /// <summary>
    /// Runs a .csx script. <paramref name="savePath"/> is a data file path: when set, the data is written there after the script ran.
    /// <paramref name="exportDirectory"/> is where scripts write exports; it defaults to the folder of <paramref name="savePath"/>, or the current folder.
    /// </summary>
    public static async Task<ScriptResult> ExecuteScriptAsync(string scriptPath, string? dataPath, string? savePath, string[] args, string? exportDirectory = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(scriptPath))
        {
            return new ScriptResult
            {
                Success = false,
                Error = "Script not found: " + scriptPath
            };
        }
        var hasData = !string.IsNullOrEmpty(dataPath) && File.Exists(dataPath);
        if (!string.IsNullOrEmpty(dataPath) && !hasData)
        {
            return new ScriptResult
            {
                Success = false,
                Error = "Data file not found: " + dataPath
            };
        }
        GameMakerData? data = null;
        CliScriptGlobals? globals = null;
        try
        {
            scriptPath = Path.GetFullPath(scriptPath);
            if (hasData)
            {
                LogService.Log("[ScriptExecutor] Loading data file: " + dataPath);
                using (var stream = new FileStream(dataPath!, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    data = await GameMakerIO.ReadAsync(stream, null, cancellationToken);
                }
                LogService.Log("[ScriptExecutor] Data loaded. Game: " + (data.GeneralInfo?.DisplayName?.Content ?? "Unknown"));
            }
            else
            {
                LogService.Log("[ScriptExecutor] No data file specified, running script without game data");
            }
            LogService.Log("[ScriptExecutor] Executing script: " + scriptPath);
            var outputDir = Path.GetFullPath(exportDirectory ?? (string.IsNullOrEmpty(savePath) ? "." : Path.GetDirectoryName(Path.GetFullPath(savePath))!));
            Directory.CreateDirectory(outputDir);
            globals = new CliScriptGlobals(data, dataPath ?? scriptPath, scriptPath)
            {
                DataFilePath = dataPath ?? string.Empty,
                OutputDir = outputDir,
                InputDir = args.FirstOrDefault() ?? string.Empty
            };
            var result = await RunAsync(scriptPath, globals, timeout, cancellationToken);
            if (!result.Success)
            {
                return result;
            }
            if (globals.Data is not null && !string.IsNullOrEmpty(savePath))
            {
                LogService.Log("[ScriptExecutor] Saving to: " + savePath);
                PatchInputService.WriteDataFile(globals.Data, savePath, dataPath);
            }
            LogService.Log("[ScriptExecutor] Script executed successfully");
            return new ScriptResult
            {
                Success = true
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ScriptResult
            {
                Success = false,
                Error = "Script execution failed: " + ex.Message
            };
        }
        finally
        {
            if (!ReferenceEquals(data, globals?.Data)) globals?.Data?.Dispose();
            data?.Dispose();
        }
    }

    private static readonly Lock s_consoleLock = new();
    private static int s_silenced;
    private static TextWriter? s_savedOut;

    // Counted, so overlapping runs never save the silent writer as the original or restore too early.
    private static void SilenceConsole()
    {
        using (s_consoleLock.EnterScope())
        {
            if (s_silenced++ != 0) return;
            s_savedOut = Console.Out;
            Console.SetOut(TextWriter.Null);
        }
    }

    private static void RestoreConsole()
    {
        using (s_consoleLock.EnterScope())
        {
            if (--s_silenced != 0 || s_savedOut is null) return;
            Console.SetOut(s_savedOut);
            s_savedOut = null;
        }
    }

    private static async Task<ScriptResult> RunAsync(string scriptPath, CliScriptGlobals globals, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        // Script output goes to the console only with --verbose. Scripts print through Console directly, so the writer is swapped for the whole process.
        var silenced = !LogService.Verbose;
        if (silenced) SilenceConsole();
        try
        {
            var result = await ScriptRunner.RunAsync(scriptPath, globals, cancellationToken, timeout);
            if (result.Cancelled && cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            return new ScriptResult { Success = result.Success, Error = result.Error };
        }
        finally
        {
            if (silenced) RestoreConsole();
        }
    }
}

public sealed class ScriptResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Output { get; set; }
}
