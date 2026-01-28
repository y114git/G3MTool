


using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using UndertaleModLib;
using UndertaleModLib.Models;




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




EnsureDataLoaded();

string audioGroupsIn = GetInputDirectory();
PrintLine($"[ImportAudioGroups] Importing from: {audioGroupsIn}");

string[] audioGroupDirs = Directory.GetDirectories(audioGroupsIn);
if (audioGroupDirs.Length == 0)
{
    PrintLine("[ImportAudioGroups] No audio group directories found, skipping import.");
    return;
}

PrintLine($"[ImportAudioGroups] Found {audioGroupDirs.Length} audio group(s) to import.");

SetProgressBar(null, "Importing Audio Groups", 0, audioGroupDirs.Length);
StartProgressBarUpdater();

SyncBinding("AudioGroups, Strings", true);

int created = 0;
int updated = 0;

foreach (string audioGroupDir in audioGroupDirs)
{
    try
    {
        string audioGroupName = Path.GetFileName(audioGroupDir);
        string audioGroupFile = Path.Combine(audioGroupDir, audioGroupName + ".json");
        
        if (!File.Exists(audioGroupFile))
        {
            PrintLine($"[ImportAudioGroups] Warning: No .json file found in {audioGroupName}");
            IncrementProgress();
            continue;
        }
        
        string jsonContent = File.ReadAllText(audioGroupFile, Encoding.UTF8);
        
        JsonDocument jsonDoc = JsonDocument.Parse(jsonContent);
        JsonElement root = jsonDoc.RootElement;
        
        UndertaleAudioGroup audioGroup = Data.AudioGroups?.ByName(audioGroupName);
        bool isNew = false;
        
        if (audioGroup == null)
        {
            
            audioGroup = new UndertaleAudioGroup();
            audioGroup.Name = Data.Strings.MakeString(audioGroupName);
            isNew = true;
            PrintLine($"[ImportAudioGroups] Creating NEW audio group: {audioGroupName}");
        }

        if (root.TryGetProperty("path", out JsonElement pathElm))
        {
            string path = pathElm.GetString() ?? "";
            if (!string.IsNullOrEmpty(path))
            {
                audioGroup.Path = Data.Strings.MakeString(path);
            }
        }

        if (isNew)
        {
            Data.AudioGroups.Add(audioGroup);
            created++;
            PrintLine($"[ImportAudioGroups] Created new audio group: {audioGroupName}");
        }
        else
        {
            updated++;
            PrintLine($"[ImportAudioGroups] Updated audio group: {audioGroupName}");
        }
        jsonDoc.Dispose();
        IncrementProgress();
    }
    catch (Exception ex)
    {
        PrintLine($"[ImportAudioGroups] Error importing audio group {audioGroupName}: {ex.Message}");
        IncrementProgress();
    }
}

await StopProgressBarUpdater();
HideProgressBar();
PrintLine($"[ImportAudioGroups] Done. Created: {created}, Updated: {updated}");







