using System;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UndertaleModLib;
using UndertaleModLib.Util;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Compiler;
using Underanalyzer.Decompiler;

// ============================================================================
// ImportCodeEntries.csx - Selective compilation approach
// ============================================================================
// Only CHANGED/NEW code entries are in the patch. After ImportAssetOrder
// reorders resources to match TARGET, we compile only those entries.
// Unchanged entries keep their original bytecode — UndertaleModLib stores
// object references (not raw indices), so the serializer auto-resolves
// correct indices at save time. Phases:
//   1. Queue patch code entries for compilation (incl. collision events)
//   2. Delete ORIGINAL-only entries (not in TARGET)
//   3. Event snapshot → compile → restore events (undo FindOrCreateCodeEntry)
//   4. Restore ALL objects to pre-compilation event state
//   5. Clean up Functions table (remove compiler-added spurious entries)
//   6. Assembly reassembly for byte-perfect bytecode from .asm files
// ============================================================================

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

// Check if code entry is a collision event
bool IsCollisionEvent(string codeName)
{
    return codeName.Contains("_Collision_");
}

// Parse collision event name
// Format: gml_Object_<objectName>_Collision_<indexOrObjectName>
(string objectName, string collisionIdentifier)? ParseCollisionEventName(string codeName)
{
    const string prefix = "gml_Object_";
    if (!codeName.StartsWith(prefix)) return null;

    int collisionIdx = codeName.LastIndexOf("_Collision_");
    if (collisionIdx < 0) return null;

    string objectName = codeName.Substring(prefix.Length, collisionIdx - prefix.Length);
    string identifier = codeName.Substring(collisionIdx + "_Collision_".Length);

    return (objectName, identifier);
}

// Find or create collision event for an object
// Returns the UndertaleCode entry for this collision event
UndertaleCode GetOrCreateCollisionEvent(UndertaleGameObject obj, uint collisionObjectIndex, string objectName)
{
    var collisionEvents = obj.Events[(int)EventType.Collision];

    // First check: find existing event with matching subtype
    foreach (var evt in collisionEvents)
    {
        if (evt.EventSubtype == collisionObjectIndex)
        {
            // Event exists - return its code entry or create one and link it
            if (evt.Actions.Count > 0 && evt.Actions[0].CodeId != null)
            {
                return evt.Actions[0].CodeId;
            }

            // Event exists but has no code entry (e.g. ImportGameObjects created event
            // but code entry didn't exist yet) - create code entry and link it
            string codeName = $"gml_Object_{objectName}_Collision_{collisionObjectIndex}";
            UndertaleCode codeEntry = Data.Code.ByName(codeName);
            if (codeEntry == null)
            {
                codeEntry = new UndertaleCode();
                codeEntry.Name = Data.Strings.MakeString(codeName);
                Data.Code.Add(codeEntry);

                UndertaleCodeLocals locals = new UndertaleCodeLocals();
                locals.Name = codeEntry.Name;
                Data.CodeLocals.Add(locals);
            }

            if (evt.Actions.Count == 0)
            {
                var action = new UndertaleGameObject.EventAction();
                action.CodeId = codeEntry;
                evt.Actions.Add(action);
            }
            else
            {
                evt.Actions[0].CodeId = codeEntry;
            }

            return codeEntry;
        }
    }

    // Collision event doesn't exist at all - create new event
    string newCodeName = $"gml_Object_{objectName}_Collision_{collisionObjectIndex}";

    UndertaleCode newCodeEntry = Data.Code.ByName(newCodeName);
    if (newCodeEntry == null)
    {
        newCodeEntry = new UndertaleCode();
        newCodeEntry.Name = Data.Strings.MakeString(newCodeName);
        Data.Code.Add(newCodeEntry);

        UndertaleCodeLocals locals = new UndertaleCodeLocals();
        locals.Name = newCodeEntry.Name;
        Data.CodeLocals.Add(locals);
    }

    var newEvent = new UndertaleGameObject.Event();
    newEvent.EventSubtype = collisionObjectIndex;

    var newAction = new UndertaleGameObject.EventAction();
    newAction.CodeId = newCodeEntry;
    newEvent.Actions.Add(newAction);

    collisionEvents.Add(newEvent);

    return newCodeEntry;
}

// Import collision event - find or create, then compile code
// After ImportAssetOrder, numeric indices in collision code names match TARGET order.
void ImportCollisionEvent(string codeName, string gmlCode, CodeImportGroup importGroup)
{
    var parsed = ParseCollisionEventName(codeName);
    if (parsed == null)
    {
        PrintLine($"[ImportCodeEntries] Failed to parse collision event: {codeName}");
        return;
    }

    string objectName = parsed.Value.objectName;
    string identifier = parsed.Value.collisionIdentifier;

    UndertaleGameObject obj = Data.GameObjects.ByName(objectName);
    if (obj == null)
    {
        PrintLine($"[ImportCodeEntries] Object not found: {objectName}");
        return;
    }

    // Resolve collision object index:
    // - Numeric identifier: use directly (correct after ImportAssetOrder)
    // - String identifier: look up by name
    uint collisionIndex;
    if (uint.TryParse(identifier, out collisionIndex))
    {
        // Numeric - already correct after asset reorder
    }
    else
    {
        var collisionObj = Data.GameObjects.ByName(identifier);
        if (collisionObj == null)
        {
            PrintLine($"[ImportCodeEntries] Collision object not found: {identifier}");
            return;
        }
        collisionIndex = (uint)Data.GameObjects.IndexOf(collisionObj);
    }

    UndertaleCode codeEntry = GetOrCreateCollisionEvent(obj, collisionIndex, objectName);
    importGroup.QueueReplace(codeEntry, gmlCode);

    PrintLine($"[ImportCodeEntries] Collision: {objectName} + idx {collisionIndex}");
}

EnsureDataLoaded();

string importFolder = GetInputDirectory();

string[] codeDirs = Directory.GetDirectories(importFolder);
if (codeDirs.Length == 0)
{
    Console.WriteLine("[ImportCodeEntries] No code entry directories found.");
    return;
}

Console.WriteLine($"[ImportCodeEntries] Found {codeDirs.Length} changed code entry(s) to compile (selective mode).");
Console.WriteLine($"[ImportCodeEntries] Current state: {Data.Code.Count} code entries, {Data.Sprites.Count} sprites, {Data.GameObjects.Count} objects");

SetProgressBar(null, "Importing GML", 0, codeDirs.Length);
StartProgressBarUpdater();

SyncBinding("Strings, Code, CodeLocals, Scripts, GlobalInitScripts, GameObjects, Functions, Variables, Sprites", true);

GlobalDecompileContext globalDecompileContext = new(Data);
IDecompileSettings decompilerSettings = Data.ToolInfo.DecompilerSettings;

int collisionCount = 0;
int regularCount = 0;
int recompiledCount = 0;
var eventSnapshot = new Dictionary<string, List<(int evtType, uint subtype, string codeName)>>();
HashSet<string> targetEntryNames = new HashSet<string>();

await Task.Run(() =>
{
    var ctx = new GlobalDecompileContext(Data);
    ctx.PrepareForCompilation(true);

    CodeImportGroup importGroup = new(Data, ctx)
    {
        AutoCreateAssets = true
    };

    // Track which code entries are from the patch
    HashSet<string> patchedEntries = new HashSet<string>();

    // Phase 1: Queue all patch code entries for compilation
    foreach (string codeDir in codeDirs)
    {
        IncrementProgress();

        string originalCodeName = Path.GetFileName(codeDir);
        string gmlFile = Path.Combine(codeDir, originalCodeName + ".gml");

        if (!File.Exists(gmlFile))
            continue;

        string gmlCode = File.ReadAllText(gmlFile);

        // Handle collision events separately
        if (IsCollisionEvent(originalCodeName))
        {
            try
            {
                ImportCollisionEvent(originalCodeName, gmlCode, importGroup);
                collisionCount++;
                patchedEntries.Add(originalCodeName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ImportCodeEntries] Collision error {originalCodeName}: {ex.Message}");
            }
            continue;
        }

        // Regular code entries - queue for compilation
        try
        {
            importGroup.QueueReplace(originalCodeName, gmlCode);
            regularCount++;
            patchedEntries.Add(originalCodeName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ImportCodeEntries] Skipping {originalCodeName}: {ex.Message}");
        }
    }

    Console.WriteLine($"[ImportCodeEntries] Queued {regularCount} regular + {collisionCount} collision entries from patch");

    // Phase 2: Delete code entries that exist in ORIGINAL but should not be in TARGET.
    // Build TARGET entry list from helpers/variables_functions.json if available,
    // otherwise use patch directories as the source of truth for what's changed.
    // Entries NOT in patchedEntries are UNCHANGED — they keep their original bytecode.
    string varFuncPath2 = Path.Combine(Path.GetDirectoryName(importFolder), "Helpers", "variables_functions.json");
    if (!File.Exists(varFuncPath2))
        varFuncPath2 = Path.Combine(Path.GetDirectoryName(importFolder), "AssetOrder", "variables_functions.json");

    // Load target code entry names from helpers if available
    if (File.Exists(varFuncPath2))
    {
        using var vfDoc2 = JsonDocument.Parse(File.ReadAllText(varFuncPath2, Encoding.UTF8));
        if (vfDoc2.RootElement.TryGetProperty("codeEntries", out var codeArray))
        {
            foreach (var ce in codeArray.EnumerateArray())
            {
                string ceName = ce.GetString();
                if (!string.IsNullOrEmpty(ceName))
                    targetEntryNames.Add(ceName);
            }
        }
    }

    // If we have a target list, delete entries not in it
    if (targetEntryNames.Count > 0)
    {
        var entriesToDelete = Data.Code
            .Where(c => c?.Name?.Content != null && c.ParentEntry == null && !targetEntryNames.Contains(c.Name.Content))
            .ToList();

        if (entriesToDelete.Count > 0)
        {
            Console.WriteLine($"[ImportCodeEntries] Removing {entriesToDelete.Count} code entries not in TARGET...");
            int deletedEvents = 0;

            foreach (var code in entriesToDelete)
            {
                // Remove child code entries first
                var children = Data.Code.Where(c => c?.ParentEntry == code).ToList();
                foreach (var child in children)
                {
                    Data.Code.Remove(child);
                    for (int i = Data.CodeLocals.Count - 1; i >= 0; i--)
                    {
                        if (Data.CodeLocals[i].Name == child.Name)
                        {
                            Data.CodeLocals.RemoveAt(i);
                            break;
                        }
                    }
                }

                // Remove events referencing this code entry from game objects
                foreach (var obj in Data.GameObjects)
                {
                    if (obj == null) continue;
                    for (int evtType = 0; evtType < obj.Events.Count; evtType++)
                    {
                        var evtList = obj.Events[evtType];
                        for (int i = evtList.Count - 1; i >= 0; i--)
                        {
                            if (evtList[i].Actions.Any(a => a.CodeId == code))
                            {
                                evtList.RemoveAt(i);
                                deletedEvents++;
                            }
                        }
                    }
                }

                // Remove Script references
                foreach (var script in Data.Scripts)
                {
                    if (script?.Code == code)
                        script.Code = null;
                }

                // Remove GlobalInit references
                for (int i = Data.GlobalInitScripts.Count - 1; i >= 0; i--)
                {
                    if (Data.GlobalInitScripts[i].Code == code)
                        Data.GlobalInitScripts.RemoveAt(i);
                }

                // Remove CodeLocals
                for (int i = Data.CodeLocals.Count - 1; i >= 0; i--)
                {
                    if (Data.CodeLocals[i].Name == code.Name)
                    {
                        Data.CodeLocals.RemoveAt(i);
                        break;
                    }
                }

                // Remove the code entry itself
                Data.Code.Remove(code);
            }

            Console.WriteLine($"[ImportCodeEntries] Deleted {entriesToDelete.Count} entries and {deletedEvents} associated events");
        }
    }

    // Phase 3: Pre-compilation event snapshot
    // Save event state for ALL objects so we can restore non-patched objects after compilation
    // (FindOrCreateCodeEntry may create unwanted events on non-patched objects)
    Console.WriteLine("[ImportCodeEntries] Taking event snapshot before compilation...");
    eventSnapshot.Clear();
    foreach (var obj in Data.GameObjects)
    {
        if (obj?.Name?.Content == null) continue;
        var events = new List<(int, uint, string)>();
        for (int evtType = 0; evtType < obj.Events.Count; evtType++)
        {
            foreach (var evt in obj.Events[evtType])
            {
                string codeName = (evt.Actions.Count > 0 && evt.Actions[0].CodeId != null)
                    ? (evt.Actions[0].CodeId.Name?.Content ?? "") : "";
                events.Add((evtType, evt.EventSubtype, codeName));
            }
        }
        eventSnapshot[obj.Name.Content] = events;
    }

    // Compile everything
    Console.WriteLine($"[ImportCodeEntries] Compiling all {regularCount + collisionCount + recompiledCount} code entries...");
    SetProgressBar(null, "Compiling all code...", codeDirs.Length, codeDirs.Length);
    try
    {
        importGroup.Import();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ImportCodeEntries] Compilation warning: {ex.Message}");
    }
});

DisableAllSyncBindings();
await StopProgressBarUpdater();
HideProgressBar();

Console.WriteLine($"[ImportCodeEntries] Done: {regularCount} from patch, {collisionCount} collisions, {recompiledCount} recompiled existing.");

// Phase 4: Restore ALL objects to pre-compilation event state
// FindOrCreateCodeEntry during compilation may add unwanted events to objects.
// Restore from snapshot to undo those additions. ImportGameObjects already set up
// all events correctly before compilation.
int restoredEvents = 0;
foreach (var obj in Data.GameObjects)
{
    if (obj?.Name?.Content == null) continue;
    if (!eventSnapshot.TryGetValue(obj.Name.Content, out var snapshot)) continue;

    var expectedPairs = new HashSet<(int, uint)>();
    foreach (var (evtType, subtype, _) in snapshot)
        expectedPairs.Add((evtType, subtype));

    for (int evtType = 0; evtType < obj.Events.Count; evtType++)
    {
        var evtList = obj.Events[evtType];
        for (int i = evtList.Count - 1; i >= 0; i--)
        {
            if (!expectedPairs.Contains((evtType, evtList[i].EventSubtype)))
            {
                evtList.RemoveAt(i);
                restoredEvents++;
            }
        }
    }
}
if (restoredEvents > 0)
    Console.WriteLine($"[ImportCodeEntries] Phase 4: removed {restoredEvents} spurious events added during compilation");

// Phase 4b: Authoritative event cleanup using TARGET's object_events.json
// Compilation can re-create events via code entry naming convention (e.g. gml_Object_obj_X_Draw_0
// auto-creates Draw event on obj_X). Use TARGET's event map as final authority.
string objEventsPath = Path.Combine(Path.GetDirectoryName(importFolder), "Helpers", "object_events.json");
if (!File.Exists(objEventsPath))
    objEventsPath = Path.Combine(Path.GetDirectoryName(importFolder), "AssetOrder", "object_events.json");
if (File.Exists(objEventsPath))
{
    var targetEventsRoot = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(objEventsPath));
    int authRemoved = 0;

    foreach (var obj in Data.GameObjects)
    {
        if (obj?.Name?.Content == null) continue;
        if (!targetEventsRoot.TryGetProperty(obj.Name.Content, out JsonElement targetEvents)) continue;

        var targetEventKeys = new HashSet<string>();
        foreach (var evt in targetEvents.EnumerateArray())
        {
            int t = evt.GetProperty("t").GetInt32();
            uint s = evt.GetProperty("s").GetUInt32();
            targetEventKeys.Add($"{t}_{s}");
        }

        for (int evtType = 0; evtType < obj.Events.Count; evtType++)
        {
            for (int j = obj.Events[evtType].Count - 1; j >= 0; j--)
            {
                string key = $"{evtType}_{obj.Events[evtType][j].EventSubtype}";
                if (!targetEventKeys.Contains(key))
                {
                    obj.Events[evtType].RemoveAt(j);
                    authRemoved++;
                }
            }
        }
    }

    if (authRemoved > 0)
        Console.WriteLine($"[ImportCodeEntries] Phase 4b: removed {authRemoved} events not in TARGET (authoritative cleanup)");
}

// Phase 5: Clean up Functions table to remove spurious entries created by recompiler
string varFuncPath = Path.Combine(Path.GetDirectoryName(importFolder), "Helpers", "variables_functions.json");
if (!File.Exists(varFuncPath))
    varFuncPath = Path.Combine(Path.GetDirectoryName(importFolder), "AssetOrder", "variables_functions.json");
if (File.Exists(varFuncPath))
{
    Console.WriteLine("[ImportCodeEntries] Phase 5: Cleaning up Functions table...");
    using var vfDoc = JsonDocument.Parse(File.ReadAllText(varFuncPath, Encoding.UTF8));
    var targetFunctions = new HashSet<string>();
    if (vfDoc.RootElement.TryGetProperty("functions", out var funcArray))
    {
        foreach (var f in funcArray.EnumerateArray())
        {
            string fname = f.GetString();
            if (!string.IsNullOrEmpty(fname))
                targetFunctions.Add(fname);
        }
    }

    // Build set of all code entry names in OURS for cross-reference
    var oursCodeNames = new HashSet<string>(
        Data.Code.Where(c => c.Name?.Content != null).Select(c => c.Name.Content));

    // Only remove functions that are BOTH not in TARGET AND have no backing code entry in OURS
    // Functions with backing code (like recompiled struct constructors) must be kept
    // even if their names differ from TARGET (e.g. __struct___25 vs __struct___31)
    int removedFuncs = 0;
    for (int i = Data.Functions.Count - 1; i >= 0; i--)
    {
        string funcName = Data.Functions[i].Name?.Content;
        if (!string.IsNullOrEmpty(funcName) && !targetFunctions.Contains(funcName) && !oursCodeNames.Contains(funcName))
        {
            Data.Functions.RemoveAt(i);
            removedFuncs++;
        }
    }
    Console.WriteLine($"[ImportCodeEntries] Phase 5: removed {removedFuncs} dangling functions (OURS had {Data.Functions.Count + removedFuncs}, TARGET has {targetFunctions.Count})");
}
else
{
    Console.WriteLine("[ImportCodeEntries] Phase 5: No variables_functions.json found - skipping Functions cleanup");
}

// Phase 6: Assembly reassembly for byte-perfect bytecode
// GML round-tripping is lossy (different VarIDs, structural differences, function ref differences).
// Now that compilation has built the Variables/Functions/Strings tables,
// reassemble from TARGET's .asm files to get exact bytecode.
Console.WriteLine("[ImportCodeEntries] Phase 6: Reassembling from assembly for byte-perfect bytecode...");

// Build local variable index lookup: name -> first matching Local-typed variable index
var localVarLookup = new Dictionary<string, int>();
for (int vi = 0; vi < Data.Variables.Count; vi++)
{
    var v = Data.Variables[vi];
    if (v?.Name?.Content != null && v.InstanceType == UndertaleInstruction.InstanceType.Local)
    {
        if (!localVarLookup.ContainsKey(v.Name.Content))
            localVarLookup[v.Name.Content] = vi;
    }
}

int reassembledCount = 0;
int asmFallbackCount = 0;

foreach (string codeDir in codeDirs)
{
    string codeName = Path.GetFileName(codeDir);
    string asmFile = Path.Combine(codeDir, codeName + ".asm");
    if (!File.Exists(asmFile)) continue;

    var code = Data.Code.ByName(codeName);
    if (code == null) continue;

    try
    {
        string asmText = File.ReadAllText(asmFile, Encoding.UTF8);

        // Step 1: Extract TARGET child names from > directives
        var targetChildNames = new List<string>();
        using (var sr = new StringReader(asmText))
        {
            string ln;
            while ((ln = sr.ReadLine()) != null)
            {
                ln = ln.Trim();
                if (ln.StartsWith("> "))
                {
                    string rest = ln.Substring(2).Trim();
                    int sp = rest.IndexOf(' ');
                    if (sp > 0)
                        targetChildNames.Add(rest.Substring(0, sp));
                }
            }
        }

        // Step 2: Build child name mapping (TARGET name -> OURS name)
        var nameMap = new Dictionary<string, string>();
        if (targetChildNames.Count > 0)
        {
            var oursChildren = code.ChildEntries.OrderBy(c => c.Offset).ToList();
            if (targetChildNames.Count != oursChildren.Count)
                throw new Exception($"Child count mismatch: ASM has {targetChildNames.Count}, compiled has {oursChildren.Count}");

            for (int ci = 0; ci < targetChildNames.Count; ci++)
            {
                string tName = targetChildNames[ci];
                string oName = oursChildren[ci].Name?.Content;
                if (!string.IsNullOrEmpty(oName) && tName != oName)
                    nameMap[tName] = oName;
            }
        }

        // Step 3: Preprocess assembly text
        var sb7 = new StringBuilder();
        using (var sr = new StringReader(asmText))
        {
            string ln;
            while ((ln = sr.ReadLine()) != null)
            {
                string trimmed = ln.Trim();

                // Remap .localvar VARI indices to match OURS' Variables table
                if (trimmed.StartsWith(".localvar"))
                {
                    var parts = trimmed.Split(' ');
                    if (parts.Length >= 4)
                    {
                        string varName = parts[2];
                        if (localVarLookup.TryGetValue(varName, out int newIdx))
                            parts[3] = newIdx.ToString();
                        ln = string.Join(" ", parts);
                    }
                }

                // Strip @N string index suffixes to prevent cross-file index corruption
                // Format: push.s "string content"@12345 -> push.s "string content"
                if (trimmed.StartsWith("push.s "))
                {
                    ln = Regex.Replace(ln, @"""@\d+", "\"");
                }

                // Remap child entry names in > directives and function references
                if (nameMap.Count > 0)
                {
                    foreach (var kvp in nameMap)
                    {
                        if (ln.Contains(kvp.Key))
                            ln = ln.Replace(kvp.Key, kvp.Value);
                    }
                }

                sb7.AppendLine(ln);
            }
        }

        // Step 4: Assemble
        var newInstructions = Assembler.Assemble(sb7.ToString(), Data);

        // Step 5: Replace instructions on the code entry
        code.Instructions.Clear();
        foreach (var instr in newInstructions)
            code.Instructions.Add(instr);

        // Update code length (instructions size is in 32-bit words, Length is in bytes)
        uint totalWords = 0;
        foreach (var instr in newInstructions)
            totalWords += instr.CalculateInstructionSize();
        code.Length = totalWords * 4;

        reassembledCount++;
    }
    catch (Exception ex)
    {
        asmFallbackCount++;
        if (asmFallbackCount <= 5)
            PrintLine($"[ImportCodeEntries] Phase 6 fallback for {codeName}: {ex.Message}");
    }
}

if (asmFallbackCount > 5)
    PrintLine($"[ImportCodeEntries] Phase 6: ... and {asmFallbackCount - 5} more fallbacks (suppressed)");
Console.WriteLine($"[ImportCodeEntries] Phase 6: reassembled {reassembledCount}/{reassembledCount + asmFallbackCount} entries ({asmFallbackCount} fell back to GML)");
