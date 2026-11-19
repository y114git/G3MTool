using G3MLib.DataFile;
using G3MLib.DataFile.Scripting;
using G3MToolCLI.Services.Logging;

namespace G3MToolCLI.Models.Scripting;

/// <summary>Script globals for the command line: no dialogs, questions are answered yes and inputs keep their default.</summary>
internal sealed class CliScriptGlobals : ScriptGlobals
{
    public CliScriptGlobals(GameMakerData? data, string filePath, string scriptPath)
        : base(data, filePath, scriptPath)
    {
        ExePath = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory;
        MessageHandler = message => LogService.Log("[Script] " + message);
        WarningHandler = message => LogService.Log("[Script Warning] " + message);
    }

    public override bool Verbose => LogService.Verbose;

    public override bool ScriptQuestion(string message)
    {
        LogService.Log("[Script Question] " + message);
        return true;
    }

    public override string ScriptInputDialog(string title, string label, string defaultInput, string cancelText, string submitText, bool isMultiline, bool preventClose)
    {
        LogService.Log($"[Script Input] {title}: {label} (default: {defaultInput})");
        return defaultInput;
    }

    public override string SimpleTextInput(string title, string label, string defaultValue, bool allowMultiline, bool showDialog = true)
    {
        LogService.Log($"[Script Input] {title}: {label} (default: {defaultValue})");
        return defaultValue;
    }

    public override string PromptLoadFile(string defaultExt, string filter) => Unavailable("PromptLoadFile");

    public override string PromptSaveFile(string defaultExt, string filter) => Unavailable("PromptSaveFile");

    public override string PromptChooseDirectory() => Unavailable("PromptChooseDirectory");

    protected override void ReportProgress(string label, string status, int value, double maximum)
    {
        if (!LogService.Verbose || maximum <= 0) return;
        if (!string.IsNullOrEmpty(status)) LogService.SetOperation(status);
        LogService.Progress(value, (int)maximum);
    }

    private static string Unavailable(string name)
    {
        LogService.Log("[Script] " + name);
        return null!; // scripts test for null when the user cancels a prompt
    }
}
