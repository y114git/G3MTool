using System.Text.Json;
using G3MLib.DataFile;
using G3MLib.DataFile.Legacy;
using G3MLib.DataFile.Models;
using G3MLib.DataFile.Scripting;
using G3MLib.DataFile.Decompiler;
using G3MLib.Modding.Logging;
using ImageMagick;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace G3MToolGUI.Services;

public sealed record ScriptExecutionResult(bool Success, string? Error = null);

public static class ScriptExecutionService
{
    private static readonly System.Threading.Lock s_scriptLock = new();
    private static (string Content, string? Path, Script<object> Script)? s_lastScript;
    private static readonly string[] s_imports =
    [
        "System", "System.IO", "System.Text", "System.Text.Json", "System.Text.RegularExpressions", "System.Linq", "System.Threading.Tasks", "System.Collections.Generic",
        "G3MLib.DataFile", "G3MLib.DataFile.Models", "G3MLib.DataFile.Util", "G3MLib.DataFile.Decompiler", "G3MLib.DataFile.Compiler", "G3MLib.DataFile.Scripting", "ImageMagick"
    ];

    public static async Task<ScriptExecutionResult> RunAsync(string scriptPath, string? dataPath, string? outputPath, IReadOnlyList<string> arguments, ScriptInteraction? interaction = null)
    {
        if (!File.Exists(scriptPath)) return new(false, "Script was not found.");
        if (dataPath is not null && !File.Exists(dataPath)) return new(false, "Data file was not found.");
        if (dataPath is not null && string.IsNullOrWhiteSpace(outputPath)) return new(false, "Choose an output data path.");

        GameMakerData? data = null;
        ScriptGlobals? globals = null;
        try
        {
            if (dataPath is not null)
            {
                LogService.Info("Loading data file: " + dataPath);
                using FileStream input = new(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                data = GameMakerIO.Read(input);
            }

            string fullScriptPath = Path.GetFullPath(scriptPath);
            string source = UTMTScriptTranslator.Translate(await File.ReadAllTextAsync(fullScriptPath));
            globals = new(data, dataPath ?? fullScriptPath, fullScriptPath, outputPath, arguments.FirstOrDefault()) { Interaction = interaction };
            globals.ScriptRunner = path => RunNestedAsync(path, globals);
            Script<object> script;
            lock (s_scriptLock)
            {
                if (s_lastScript is { } cached && cached.Content == source && cached.Path == fullScriptPath)
                    script = cached.Script;
                else
                {
                    script = CSharpScript.Create(source, Options(fullScriptPath), typeof(ScriptGlobals));
                    script.Compile();
                    s_lastScript = (source, fullScriptPath, script);
                }
            }
            await script.RunAsync(globals);

            if (globals.Data is not null && outputPath is not null)
            {
                string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                G3MLib.Modding.Patching.PatchInputService.WriteDataFile(globals.Data, outputPath, dataPath);
            }

            return new(true);
        }
        catch (CompilationErrorException exception)
        {
            return new(false, "Compilation error:" + Environment.NewLine + string.Join(Environment.NewLine, exception.Diagnostics));
        }
        catch (Exception exception)
        {
            return new(false, exception is ScriptException ? exception.Message : "Script execution failed: " + exception.Message);
        }
        finally
        {
            if (!ReferenceEquals(data, globals?.Data)) globals?.Data?.Dispose();
            data?.Dispose();
        }
    }

    private static async Task<bool> RunNestedAsync(string path, ScriptGlobals globals)
    {
        string scriptDirectory = Path.GetDirectoryName(globals.ScriptPath) ?? globals.ScriptRootPath;
        string fullPath = Path.GetFullPath(path, scriptDirectory);
        string relative = Path.GetRelativePath(globals.ScriptRootPath, fullPath);
        if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ScriptException("Nested script path escapes the script directory.");
        if (!File.Exists(fullPath)) throw new ScriptException("Script was not found: " + fullPath);

        string? previousPath = globals.ScriptPath;
        try
        {
            globals.ScriptPath = fullPath;
            await CSharpScript.EvaluateAsync(UTMTScriptTranslator.Translate(await File.ReadAllTextAsync(fullPath)), Options(fullPath), globals, typeof(ScriptGlobals));
            return true;
        }
        finally
        {
            globals.ScriptPath = previousPath;
        }
    }

    private static ScriptOptions Options(string scriptPath)
    {
        string directory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory;
        return ScriptOptions.Default
            .AddImports(s_imports)
            .AddReferences(typeof(GameMakerData).Assembly, typeof(MagickImage).Assembly, typeof(JsonSerializer).Assembly)
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithFilePath(scriptPath)
            .WithSourceResolver(ScriptSourceResolver.Default.WithBaseDirectory(directory))
            .WithMetadataResolver(ScriptMetadataResolver.Default.WithBaseDirectory(directory));
    }
}

public sealed class ScriptGlobals(GameMakerData? data, string filePath, string scriptPath, string? outputPath, string? inputDirectory)
{
    private int _progress;
    private int _progressMaximum = 100;
    public ScriptInteraction? Interaction { get; init; }
    public GameMakerData? Data { get; set; } = data;
    public string FilePath { get; } = filePath;
    public Action<Action> MainThreadAction { get; } = static action => action();
    public string? ScriptPath { get; internal set; } = scriptPath;
    internal string ScriptRootPath { get; } = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory;
    public string DataFilePath { get; } = data is null ? string.Empty : filePath;
    public string OutputDir { get; } = string.IsNullOrWhiteSpace(outputPath) ? string.Empty : Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? string.Empty;
    public string InputDir { get; } = inputDirectory ?? string.Empty;
    public string ExePath => Path.GetDirectoryName(ScriptPath) ?? Environment.CurrentDirectory;
    internal Func<string, Task<bool>>? ScriptRunner { get; set; }
    public static bool Verbose => true;

    public void EnsureDataLoaded()
    {
        if (Data is null) throw new ScriptException("Data is not loaded.");
    }

    public static void ScriptError(string message, string? title = null, bool setConsoleText = true) => throw new ScriptException((title ?? "Script Error") + ": " + message);
    public static void ScriptMessage(string message) => LogService.Info("[Script] " + message);
    public static void ScriptWarning(string message) => LogService.Warning("[Script] " + message);
    public bool RunUMTScript(string path) => ScriptRunner?.Invoke(path).GetAwaiter().GetResult() ?? throw new ScriptException("Nested script execution is unavailable.");
    public string GetDisassemblyText(string codeName)
    {
        EnsureDataLoaded();
        return GetDisassemblyText(Data!.Code.ByName(codeName));
    }
    public string GetDisassemblyText(GameMakerCode code)
    {
        EnsureDataLoaded();
        if (code.ParentEntry is not null) return "; This code entry belongs to \"" + code.ParentEntry.Name.Content + "\".";
        try { return code.Disassemble(Data!.Variables, Data.CodeLocals?.For(code), Data.CodeLocals is null); }
        catch { return "/* DISASSEMBLY FAILED */"; }
    }
    public bool ScriptQuestion(string message) => RequireInteraction().Question(message).GetAwaiter().GetResult();
    public string? ScriptInputDialog(string title, string label, string defaultInput, bool allowMultiline, bool showDialog) => showDialog ? RequireInteraction().Input(title, label, defaultInput, allowMultiline).GetAwaiter().GetResult() : defaultInput;
    public string? SimpleTextInput(string title, string label, string defaultInput, bool allowMultiline) => ScriptInputDialog(title, label, defaultInput, allowMultiline, true);
    public string? PromptLoadFile(string filter, string defaultPath) => RequireInteraction().OpenFile(filter, defaultPath).GetAwaiter().GetResult();
    public string? PromptSaveFile(string filter, string defaultName) => RequireInteraction().SaveFile(filter, defaultName).GetAwaiter().GetResult();
    public string? PromptChooseDirectory() => RequireInteraction().ChooseDirectory().GetAwaiter().GetResult();
    private ScriptInteraction RequireInteraction() => Interaction ?? throw new ScriptException("Interactive script input requires a window host.");
    public void SetProgressBar(string? label, string status, int current, int max) { Interlocked.Exchange(ref _progress, current); Volatile.Write(ref _progressMaximum, max); LogService.SetOperation(status); LogService.Progress(current, max); }
    public void UpdateProgressBar(string? label, string status, int current, int max) => SetProgressBar(label, status, current, max);
    public void AddProgress(int amount) => LogService.Progress(Interlocked.Add(ref _progress, amount), Volatile.Read(ref _progressMaximum));
    public void IncrementProgress() => AddProgress(1);
    public void IncrementProgressParallel() => AddProgress(1);
    public void StartProgressBarUpdater() { }
    public Task StopProgressBarUpdater() => Task.CompletedTask;
    public void HideProgressBar() => LogService.ProgressComplete();
    public int GetProgress() => Volatile.Read(ref _progress);
    public static void SetFinishedMessage(bool enabled) { }
    public static void ChangeSelection(object obj, bool isRecursive = false) { }
    public static void SyncBinding(string name, bool value) { }
    public static void SyncBinding(bool condition, bool value) { }
    public static void DisableAllSyncBindings() { }
}
