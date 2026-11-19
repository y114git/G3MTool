using G3MLib.DataFile;
using G3MLib.DataFile.Scripting;
using G3MLib.Modding.Logging;

namespace G3MToolGUI.Services;

public sealed record ScriptExecutionResult(bool Success, string? Error = null);

public static class ScriptExecutionService
{
    /// <summary>Runs a script. Cancelling the token stops it at its next library call and throws <see cref="OperationCanceledException"/>.</summary>
    public static async Task<ScriptExecutionResult> RunAsync(string scriptPath, string? dataPath, string? outputPath, IReadOnlyList<string> arguments, ScriptInteraction? interaction = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(scriptPath)) return new(false, "Script was not found.");
        if (dataPath is not null && !File.Exists(dataPath)) return new(false, "Data file was not found.");
        if (dataPath is not null && string.IsNullOrWhiteSpace(outputPath)) return new(false, "Choose an output data path.");

        GameMakerData? data = null;
        GuiScriptGlobals? globals = null;
        try
        {
            if (dataPath is not null)
            {
                LogService.Info("Loading data file: " + dataPath);
                using FileStream input = new(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                data = await GameMakerIO.ReadAsync(input, null, cancellationToken);
            }

            var fullScriptPath = Path.GetFullPath(scriptPath);
            globals = new(data, dataPath ?? fullScriptPath, fullScriptPath, outputPath, arguments is [var firstArgument, ..] ? firstArgument : null) { Interaction = interaction };
            var result = await ScriptRunner.RunAsync(fullScriptPath, globals, cancellationToken);
            if (result.Cancelled) throw new OperationCanceledException(cancellationToken);
            if (!result.Success) return new(false, result.Error);

            if (globals.Data is not null && outputPath is not null)
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                G3MLib.Modding.Patching.PatchInputService.WriteDataFile(globals.Data, outputPath, dataPath);
            }

            return new(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(false, "Script execution failed: " + exception.Message);
        }
        finally
        {
            if (!ReferenceEquals(data, globals?.Data)) globals?.Data?.Dispose();
            data?.Dispose();
        }
    }
}

/// <summary>Script globals of the window: dialogs and pickers go through <see cref="ScriptInteraction"/>, and without one they throw.</summary>
public sealed class GuiScriptGlobals : ScriptGlobals
{
    public GuiScriptGlobals(GameMakerData? data, string filePath, string scriptPath, string? outputPath, string? inputDirectory)
        : base(data, filePath, scriptPath)
    {
        ExePath = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory;
        DataFilePath = data is null ? string.Empty : filePath;
        OutputDir = string.IsNullOrWhiteSpace(outputPath) ? string.Empty : Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? string.Empty;
        InputDir = inputDirectory ?? string.Empty;
        MessageHandler = message => LogService.Info("[Script] " + message);
        WarningHandler = message => LogService.Warning("[Script] " + message);
    }

    public ScriptInteraction? Interaction { get; init; }

    public override bool Verbose => true;

    public override bool ScriptQuestion(string message) => Wait(RequireInteraction().Question(message))!;

    public override string ScriptInputDialog(string title, string label, string defaultInput, string cancelText, string submitText, bool isMultiline, bool preventClose) =>
        Wait(RequireInteraction().Input(title, label, defaultInput, isMultiline))!;

    public override string SimpleTextInput(string title, string label, string defaultValue, bool allowMultiline, bool showDialog = true) =>
        showDialog ? Wait(RequireInteraction().Input(title, label, defaultValue, allowMultiline))! : defaultValue;

    public override string PromptLoadFile(string defaultExt, string filter) => Wait(RequireInteraction().OpenFile(filter, string.Empty))!;

    public override string PromptSaveFile(string defaultExt, string filter) => Wait(RequireInteraction().SaveFile(filter, string.Empty))!;

    public override string PromptChooseDirectory() => Wait(RequireInteraction().ChooseDirectory())!;

    protected override void ReportProgress(string label, string status, int value, double maximum)
    {
        if (!string.IsNullOrEmpty(status)) LogService.SetOperation(status);
        LogService.Progress(value, (int)maximum);
    }

    // A script waiting for the user must still end when the operation is cancelled.
    private T Wait<T>(Task<T> task) => task.WaitAsync(CancellationToken).GetAwaiter().GetResult();

    private ScriptInteraction RequireInteraction() => Interaction ?? throw new ScriptException("Interactive script input requires a window host.");
}
