


using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using UndertaleModLib;
using UndertaleModLib.Models;




void PrintLine(string s) { if (Verbose) Console.WriteLine(s); }

string SafeName(string name)
{
    var invalid = Path.GetInvalidFileNameChars();
    var sb = new StringBuilder(name.Length);
    foreach (var ch in name) sb.Append(invalid.Contains(ch) ? '_' : ch);
    return sb.ToString();
}

string GetOutputDirectory()
{
    string outputDir = OutputDir;
    if (string.IsNullOrEmpty(outputDir))
        throw new Exception("OutputDir is not set.");
    string typeDir = Path.Combine(outputDir, "CodeEntries");
    if (!Directory.Exists(typeDir))
        Directory.CreateDirectory(typeDir);
    return typeDir;
}




EnsureDataLoaded();

if (Data.IsYYC())
{
    PrintLine("[ExportCodeEntries] YYC build detected - code export not available.");
    return;
}

string codeOut = GetOutputDirectory();
PrintLine($"[ExportCodeEntries] Exporting to: {codeOut}");

List<UndertaleCode> allCode = Data.Code.Where(c => c.ParentEntry is null).ToList();
PrintLine($"[ExportCodeEntries] Found {allCode.Count} code entries to export.");

GlobalDecompileContext globalDecompileContext = new(Data);
Underanalyzer.Decompiler.IDecompileSettings decompilerSettings = Data.ToolInfo.DecompilerSettings;

SetProgressBar(null, "Exporting Code Entries", 0, allCode.Count);
StartProgressBarUpdater();

await Task.Run(() => Parallel.ForEach(allCode, code => ExportCode(code, codeOut)));

void ExportCode(UndertaleCode code, string outputDir)
{
    if (code?.Name?.Content == null)
    {
        IncrementProgressParallel();
        return;
    }

    string codeName = SafeName(code.Name.Content);
    string resourceDir = Path.Combine(outputDir, codeName);
    Directory.CreateDirectory(resourceDir);
    string gmlPath = Path.Combine(resourceDir, codeName + ".gml");

    try
    {
        string decompiled = new Underanalyzer.Decompiler.DecompileContext(globalDecompileContext, code, decompilerSettings).DecompileToString();
        File.WriteAllText(gmlPath, decompiled, Encoding.UTF8);
    }
    catch (Exception e)
    {
        File.WriteAllText(gmlPath, "/*\nDECOMPILER FAILED!\n\n" + e.ToString() + "\n*/", Encoding.UTF8);
    }

    IncrementProgressParallel();
}

await StopProgressBarUpdater();
HideProgressBar();

PrintLine($"[ExportCodeEntries] Export complete. {allCode.Count} code entries exported to {codeOut}");




