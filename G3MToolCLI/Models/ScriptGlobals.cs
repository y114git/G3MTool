using G3MToolCLI.Services;
using UndertaleModLib;

namespace G3MToolCLI.Models;

// ScriptGlobals is the API surface for CSX import/export scripts.
// Methods must remain instance-based with their exact signatures - scripts call them via Roslyn globals.
// Unused parameters and non-instance access are intentional (stub implementations for headless CLI).
#pragma warning disable CA1822 // Member does not access instance data - required for CSX script API
#pragma warning disable IDE0060 // Remove unused parameter - required for CSX script API
public class ScriptGlobals
{
    public UndertaleData Data { get; set; } = null!;
    public string FilePath { get; set; } = string.Empty;
    public string? ScriptPath { get; set; }

    // Path to the data.win file being processed
    public string DataFilePath { get; set; } = string.Empty;

    // Directories for export/import scripts - thread-safe, no environment variables
    public string OutputDir { get; set; } = string.Empty;
    public string InputDir { get; set; } = string.Empty;

    // Verbose mode for detailed output
    public bool Verbose => LogService.Verbose;

    private int _progressValue;
    private int _progressMax;
    private string? _progressStatus;
    private readonly Lock _progressLock = new();
    private CancellationTokenSource? _progressUpdaterCts;

    public void EnsureDataLoaded()
    {
        if (Data == null)
            throw new ScriptException("Data is not loaded. Call this after loading a data file.");
    }

    public void ScriptError(string message, string? title = null)
    {
        throw new ScriptException($"{title ?? "Script Error"}: {message}");
    }

    public void ScriptMessage(string message)
    {
        LogService.Log($"[Script] {message}");
    }

    public bool ScriptQuestion(string message)
    {
        LogService.Log($"[Script Question] {message}");
        return true;
    }

    public string? ScriptInputDialog(string title, string label, string defaultInput, bool allowMultiline, bool showDialog)
    {
        LogService.Log($"[Script Input] {title}: {label} (default: {defaultInput})");
        return defaultInput;
    }

    public string? SimpleTextInput(string title, string label, string defaultInput, bool allowMultiline)
    {
        return ScriptInputDialog(title, label, defaultInput, allowMultiline, true);
    }

    public string? PromptLoadFile(string filter, string defaultPath)
    {
        LogService.Log($"[Script] PromptLoadFile: {filter}");
        return null;
    }

    public string? PromptSaveFile(string filter, string defaultName)
    {
        LogService.Log($"[Script] PromptSaveFile: {filter}");
        return null;
    }

    public string? PromptChooseDirectory()
    {
        LogService.Log("[Script] PromptChooseDirectory");
        return null;
    }

    public void SetProgressBar(string? label, string status, int current, int max)
    {
        lock (_progressLock)
        {
            _progressStatus = status;
            _progressValue = current;
            _progressMax = max;
        }
    }

    public void UpdateProgressBar(string? label, string status, int current, int max)
    {
        SetProgressBar(label, status, current, max);
    }

    public void AddProgress(int amount)
    {
        lock (_progressLock)
        {
            _progressValue += amount;
        }
    }

    public void IncrementProgress()
    {
        AddProgress(1);
    }

    public void IncrementProgressParallel()
    {
        lock (_progressLock)
        {
            _progressValue++;
        }
    }

    public void StartProgressBarUpdater()
    {
        // Only show script progress in verbose mode
        if (!LogService.Verbose)
            return;

        _progressUpdaterCts = new CancellationTokenSource();
        var token = _progressUpdaterCts.Token;

        Task.Run(async () =>
        {
            int lastValue = -1;
            while (!token.IsCancellationRequested)
            {
                int current, max;
                string? status;
                lock (_progressLock)
                {
                    current = _progressValue;
                    max = _progressMax;
                    status = _progressStatus;
                }

                if (current != lastValue && max > 0)
                {
                    // Show simple percent progress
                    int percent = max > 0 ? (current * 100 / max) : 0;
                    Console.Write($"\r{status}: {percent}%   ");
                    lastValue = current;
                }

                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }, token);
    }

    public async Task StopProgressBarUpdater()
    {
        if (_progressUpdaterCts != null)
        {
            _progressUpdaterCts.Cancel();
            await Task.Delay(100);
            _progressUpdaterCts.Dispose();
            _progressUpdaterCts = null;
        }
    }

    public void HideProgressBar()
    {
        lock (_progressLock)
        {
            _progressValue = 0;
            _progressMax = 0;
            _progressStatus = null;
        }
    }

    public int GetProgress()
    {
        lock (_progressLock)
        {
            return _progressValue;
        }
    }

    public void SetFinishedMessage(bool enabled)
    {
    }

    public void ChangeSelection(object obj, bool isRecursive = false)
    {
    }

    // Sync binding stubs for import scripts
    public void SyncBinding(string name, bool value)
    {
    }

    public void SyncBinding(bool condition, bool value)
    {
    }

    public void DisableAllSyncBindings()
    {
    }
}

public class ScriptException : Exception
{
    public ScriptException(string message) : base(message) { }
    public ScriptException(string message, Exception inner) : base(message, inner) { }
}
