


using System;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using UndertaleModLib;
using UndertaleModLib.Util;




void PrintLine(string s) { if (Verbose) Console.WriteLine(s); }

string GetInputDirectory()
{
    string inputDir = InputDir;
    if (string.IsNullOrEmpty(inputDir))
        throw new Exception("InputDir is not set.");
    if (!Directory.Exists(inputDir))
        throw new Exception($"INPUT_DIR directory does not exist: {inputDir}");
    return inputDir;
}

string CorrectCodeEntryName(string filename)
{
    string corrected = filename;
    corrected = corrected.Replace("_object_", "_Object_");
    corrected = corrected.Replace("_create_", "_Create_");
    corrected = corrected.Replace("_destroy_", "_Destroy_");
    corrected = corrected.Replace("_step_", "_Step_");
    corrected = corrected.Replace("_draw_", "_Draw_");
    corrected = corrected.Replace("_alarm_", "_Alarm_");
    corrected = corrected.Replace("_collision_", "_Collision_");
    corrected = corrected.Replace("_other_", "_Other_");
    return corrected;
}




EnsureDataLoaded();

string importFolder = GetInputDirectory();
PrintLine($"[ImportCodeEntries] Importing from: {importFolder}");

string[] codeDirs = Directory.GetDirectories(importFolder);
if (codeDirs.Length == 0)
{
    PrintLine("[ImportCodeEntries] No code entry directories found - nothing to import.");
    return;
}

PrintLine($"[ImportCodeEntries] Found {codeDirs.Length} code entry(s) to import.");

SetProgressBar(null, "Importing GML", 0, codeDirs.Length);
StartProgressBarUpdater();

SyncBinding("Strings, Code, CodeLocals, Scripts, GlobalInitScripts, GameObjects, Functions, Variables", true);

await Task.Run(() =>
{
    UndertaleModLib.Compiler.CodeImportGroup importGroup = new(Data);
    
    foreach (string codeDir in codeDirs)
    {
        IncrementProgress();

        string originalCodeName = Path.GetFileName(codeDir);
        string gmlFile = Path.Combine(codeDir, originalCodeName + ".gml");
        
        if (!File.Exists(gmlFile))
        {
            PrintLine($"[ImportCodeEntries] Warning: No .gml file found in {originalCodeName}");
            continue;
        }

        string code = File.ReadAllText(gmlFile);
        string correctedCodeName = CorrectCodeEntryName(originalCodeName);
        
        var exactMatch = Data.Code.ByName(correctedCodeName);
        if (exactMatch == null)
            exactMatch = Data.Code.ByName(originalCodeName);
        if (exactMatch == null)
            exactMatch = Data.Code.FirstOrDefault(c => 
                c?.Name?.Content != null && 
                c.Name.Content.Equals(correctedCodeName, StringComparison.OrdinalIgnoreCase));
        
        string targetName = exactMatch?.Name?.Content ?? correctedCodeName;
        
        try
        {
            importGroup.QueueReplace(targetName, code);
        }
        catch (Exception ex)
        {
            PrintLine($"[ImportCodeEntries] ERROR: QueueReplace failed for '{targetName}': {ex.Message}");
            throw;
        }
    }
    
    SetProgressBar(null, "Compiling code...", codeDirs.Length, codeDirs.Length);
    importGroup.Import();
});

DisableAllSyncBindings();
await StopProgressBarUpdater();
HideProgressBar();

PrintLine($"[ImportCodeEntries] Successfully imported {codeDirs.Length} code entries.");





