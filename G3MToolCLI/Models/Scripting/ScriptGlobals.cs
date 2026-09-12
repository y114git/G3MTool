using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using G3MLib.DataFile;
using G3MLib.DataFile.Decompiler;
using G3MLib.DataFile.Models;
using G3MToolCLI.Services.Logging;

namespace G3MToolCLI.Models.Scripting;

public class ScriptGlobals
{
    private int _progressValue;

    private int _progressMax;

    private string? _progressStatus;

    private readonly Lock _progressLock = new Lock();

    private CancellationTokenSource? _progressUpdaterCts;

    public GameMakerData? Data { get; set; }

    public string FilePath { get; set; } = string.Empty;

    public Action<Action> MainThreadAction { get; } = static action => action();

    public string? ScriptPath { get; set; }

    internal string ScriptRootPath { get; set; } = Environment.CurrentDirectory;

    public string DataFilePath { get; set; } = string.Empty;

    public string OutputDir { get; set; } = string.Empty;

    public string InputDir { get; set; } = string.Empty;

    public string ExePath => Path.GetDirectoryName(ScriptPath) ?? Environment.CurrentDirectory;

    internal Func<string, bool>? ScriptRunner { get; set; }

    public static bool Verbose => LogService.Verbose;

    public void EnsureDataLoaded()
    {
        if (Data == null)
        {
            throw new ScriptException("Data is not loaded. Call this after loading a data file.");
        }
    }

    public static void ScriptError(string message, string? title = null, bool setConsoleText = true)
    {
        throw new ScriptException((title ?? "Script Error") + ": " + message);
    }

    public static void ScriptMessage(string message)
    {
        LogService.Log("[Script] " + message);
    }

    public static void ScriptWarning(string message)
    {
        LogService.Log("[Script Warning] " + message);
    }

    public bool RunUMTScript(string path)
    {
        return (ScriptRunner ?? throw new ScriptException("Nested script execution is unavailable."))(path);
    }

    public string GetDisassemblyText(string codeName)
    {
        EnsureDataLoaded();
        return GetDisassemblyText(Data!.Code.ByName(codeName));
    }

    public string GetDisassemblyText(GameMakerCode code)
    {
        EnsureDataLoaded();
        if (code?.ParentEntry != null)
        {
            return "; This code entry belongs to \"" + code.ParentEntry.Name.Content + "\".";
        }
        try
        {
            return code?.Disassemble(Data!.Variables, Data.CodeLocals?.For(code), Data.CodeLocals == null) ?? "";
        }
        catch (Exception)
        {
            LogService.Log("[Script Warning] Disassembly failed.");
            return "/* DISASSEMBLY FAILED */";
        }
    }

    public static bool ScriptQuestion(string message)
    {
        LogService.Log("[Script Question] " + message);
        return true;
    }

    public static string? ScriptInputDialog(string title, string label, string defaultInput, bool allowMultiline, bool showDialog)
    {
        LogService.Log($"[Script Input] {title}: {label} (default: {defaultInput})");
        return defaultInput;
    }

    public string? SimpleTextInput(string title, string label, string defaultInput, bool allowMultiline)
    {
        return ScriptInputDialog(title, label, defaultInput, allowMultiline, showDialog: true);
    }

    public static string? PromptLoadFile(string filter, string defaultPath)
    {
        LogService.Log("[Script] PromptLoadFile: " + filter);
        return null;
    }

    public static string? PromptSaveFile(string filter, string defaultName)
    {
        LogService.Log("[Script] PromptSaveFile: " + filter);
        return null;
    }

    public static string? PromptChooseDirectory()
    {
        LogService.Log("[Script] PromptChooseDirectory");
        return null;
    }

    public void SetProgressBar(string? label, string status, int current, int max)
    {
        using (_progressLock.EnterScope())
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
        using (_progressLock.EnterScope())
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
        using (_progressLock.EnterScope())
        {
            _progressValue++;
        }
    }

    public void StartProgressBarUpdater()
    {
        if (!LogService.Verbose)
        {
            return;
        }
        CancellationTokenSource updaterCts = new CancellationTokenSource();
        CancellationTokenSource? cancellationTokenSource = Interlocked.Exchange(ref _progressUpdaterCts, updaterCts);
        cancellationTokenSource?.Cancel();
        cancellationTokenSource?.Dispose();
        CancellationToken token = updaterCts.Token;
        Task.Run(async delegate
        {
            try
            {
                int lastValue = -1;
                while (!token.IsCancellationRequested)
                {
                    int current;
                    int max;
                    string? status;
                    using (_progressLock.EnterScope())
                    {
                        current = _progressValue;
                        max = _progressMax;
                        status = _progressStatus;
                    }
                    if (current != lastValue && max > 0)
                    {
                        int percent = current * 100 / max;
                        Console.Write($"\r{status}: {percent}%   ");
                        lastValue = current;
                    }
                    await Task.Delay(500, token).ConfigureAwait(continueOnCapturedContext: false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }, token);
    }

    public async Task StopProgressBarUpdater()
    {
        CancellationTokenSource? updaterCts = Interlocked.Exchange(ref _progressUpdaterCts, null);
        if (updaterCts != null)
        {
            await updaterCts.CancelAsync();
            updaterCts.Dispose();
        }
    }

    public void HideProgressBar()
    {
        using (_progressLock.EnterScope())
        {
            _progressValue = 0;
            _progressMax = 0;
            _progressStatus = null;
        }
    }

    public int GetProgress()
    {
        using (_progressLock.EnterScope())
        {
            return _progressValue;
        }
    }

    // Compatibility shims for GUI-only UndertaleModTool script APIs are safe no-ops in CLI scripts.
    public static void SetFinishedMessage(bool enabled)
    {
    }

    public static void ChangeSelection(object obj, bool isRecursive = false)
    {
    }

    public static void SyncBinding(string name, bool value)
    {
    }

    public static void SyncBinding(bool condition, bool value)
    {
    }

    public static void DisableAllSyncBindings()
    {
    }
}
