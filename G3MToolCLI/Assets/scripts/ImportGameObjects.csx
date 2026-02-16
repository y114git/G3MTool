


using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Text.Json;
using UndertaleModLib;
using UndertaleModLib.Models;

// ============================================================================
// DETAILED LOGGING SYSTEM
// ============================================================================
static StreamWriter _logWriter = null;
static string _logPath = null;

void InitLog(string scriptName)
{
    if (!Verbose) return;
    string logDir = Path.Combine(Path.GetTempPath(), "g3mtool_logs");
    Directory.CreateDirectory(logDir);
    _logPath = Path.Combine(logDir, $"{scriptName}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
    _logWriter = new StreamWriter(_logPath, false, Encoding.UTF8);
    _logWriter.AutoFlush = true;
    Log($"=== {scriptName} Log Started at {DateTime.Now} ===");
    Console.WriteLine($"[{scriptName}] Detailed log: {_logPath}");
}

void Log(string message)
{
    if (_logWriter != null)
        _logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
}

void CloseLog()
{
    if (_logWriter != null)
    {
        Log("=== Log Ended ===");
        _logWriter.Close();
        _logWriter = null;
    }
}

void PrintLine(string s) { if (Verbose) Console.WriteLine(s); Log(s); }

string GetInputDirectory()
{
    string inputDir = InputDir;
    if (string.IsNullOrEmpty(inputDir))
        throw new Exception("InputDir is not set.");
    if (!Directory.Exists(inputDir))
        throw new Exception($"INPUT_DIR directory does not exist: {inputDir}");
    return inputDir;
}

// Helper class to hold parsed object data before placement
class GameObjectImportData
{
    public int TargetIndex;
    public string Name;
    public string JsonPath;
    public JsonElement Root;
    public bool IsNew;
    public int ActualIndex = -1; // Index where the object was placed after creation
}




EnsureDataLoaded();

// Initialize detailed logging
InitLog("ImportGameObjects");

string gameObjectsIn = GetInputDirectory();
PrintLine($"[ImportGameObjects] Importing from: {gameObjectsIn}");

// Log initial state
Log($"INITIAL STATE: Data.GameObjects.Count = {Data.GameObjects.Count}");
Log("INITIAL OBJECTS (first 20):");
for (int i = 0; i < Math.Min(20, Data.GameObjects.Count); i++)
{
    var obj = Data.GameObjects[i];
    string spriteName = obj?.Sprite?.Name?.Content ?? "(none)";
    Log($"  [{i}] {obj?.Name?.Content ?? "(null)"} - Sprite: {spriteName}");
}

int gameObjectsImported = 0;
int gameObjectsCreated = 0;
int gameObjectsUpdated = 0;

var objDirs = Directory.GetDirectories(gameObjectsIn);
Console.WriteLine($"[ImportGameObjects] Found {objDirs.Length} game object folders to import");

// ============================================================================
// PHASE 1: Collect all JSONs and determine which are new vs existing
// ============================================================================
var importDataList = new List<GameObjectImportData>();
int maxTargetIndex = -1;

foreach (string objDir in objDirs)
{
    string jsonFile = Path.Combine(objDir, "object.json");
    if (!File.Exists(jsonFile))
    {
        PrintLine($"[ImportGameObjects] Skipping {Path.GetFileName(objDir)}: object.json not found");
        continue;
    }
    
    try
    {
        string jsonContent = File.ReadAllText(jsonFile);
        JsonDocument jsonDoc = JsonDocument.Parse(jsonContent);
        JsonElement root = jsonDoc.RootElement.Clone();

        string objName = "";
        int targetIndex = -1;
        
        // Try to get index from JSON first
        if (root.TryGetProperty("index", out JsonElement indexElm))
        {
            targetIndex = indexElm.GetInt32();
        }
        
        // Also try to extract index from folder name (format: name__idxXXXX)
        string folderName = Path.GetFileName(objDir);
        int idxPos = folderName.LastIndexOf("__idx");
        if (idxPos > 0 && folderName.Length >= idxPos + 9)
        {
            string idxStr = folderName.Substring(idxPos + 5, 4);
            if (int.TryParse(idxStr, out int parsedIdx))
            {
                targetIndex = parsedIdx;
                Log($"EXTRACTED INDEX from folder name: {folderName} -> index={targetIndex}");
            }
        }
        
        if (root.TryGetProperty("name", out JsonElement nameElm))
        {
            objName = nameElm.GetString();
        }
        else
        {
            // Extract name from folder (remove __idxXXXX suffix if present)
            objName = idxPos > 0 ? folderName.Substring(0, idxPos) : folderName;
        }

        if (string.IsNullOrEmpty(objName)) continue;

        // Check if object with this name already exists in data
        // Track how many times each name appears in the import to handle duplicates
        // (e.g. TARGET may have two objects with the same name at different indices)
        int nameCount = importDataList.Count(d => d.Name == objName);
        bool existsInData = Data.GameObjects.ByName(objName) != null;
        
        // It's new if: not in data at all, OR it's a duplicate name that needs a second copy
        bool isNew = !existsInData || nameCount > 0;
        
        importDataList.Add(new GameObjectImportData
        {
            TargetIndex = targetIndex,
            Name = objName,
            JsonPath = jsonFile,
            Root = root,
            IsNew = isNew
        });
        
        if (isNew && targetIndex > maxTargetIndex)
        {
            maxTargetIndex = targetIndex;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ImportGameObjects] Error parsing {Path.GetFileName(jsonFile)}: {ex.Message}");
    }
}

Console.WriteLine($"[ImportGameObjects] Collected {importDataList.Count} objects. New: {importDataList.Count(d => d.IsNew)}, Existing: {importDataList.Count(d => !d.IsNew)}");

// ============================================================================
// PHASE 2: Append new objects (ImportAssetOrder will reorder later)
// ============================================================================
var newObjects = importDataList.Where(d => d.IsNew).ToList();

int originalCount = Data.GameObjects.Count;
Console.WriteLine($"[ImportGameObjects] Original count: {originalCount}, New objects to add: {newObjects.Count}");

foreach (var importData in newObjects)
{
    var gameObject = new UndertaleGameObject();
    gameObject.Name = Data.Strings.MakeString(importData.Name);
    Data.GameObjects.Add(gameObject);
    importData.ActualIndex = Data.GameObjects.Count - 1;
    gameObjectsCreated++;
    Log($"APPENDED '{importData.Name}' at index {importData.ActualIndex}");
}

Console.WriteLine($"[ImportGameObjects] After additions: {Data.GameObjects.Count} objects");

// ============================================================================
// PHASE 3: Apply properties to ALL objects (new and existing)
// ============================================================================

foreach (var importData in importDataList)
{
    try
    {
        JsonElement root = importData.Root;
        string objName = importData.Name;
        int targetIdx = importData.TargetIndex;
        
        // Find object: use ActualIndex for newly created objects, otherwise by index/name
        UndertaleGameObject gameObject = null;
        if (importData.IsNew && importData.ActualIndex >= 0 && importData.ActualIndex < Data.GameObjects.Count)
        {
            // For newly created objects (including duplicates), use the exact index where we placed them
            gameObject = Data.GameObjects[importData.ActualIndex];
        }
        else if (targetIdx >= 0 && targetIdx < Data.GameObjects.Count)
        {
            gameObject = Data.GameObjects[targetIdx];
            // Verify it's the right object
            if (gameObject?.Name?.Content != objName)
            {
                // Fall back to name search
                gameObject = Data.GameObjects.ByName(objName);
            }
        }
        else
        {
            gameObject = Data.GameObjects.ByName(objName);
        }
        
        if (gameObject == null)
        {
            Console.WriteLine($"[ImportGameObjects] ERROR: Object '{objName}' not found after placement!");
            continue;
        }
        
        bool isNew = importData.IsNew;

        // Set parent first
        if (root.TryGetProperty("parent", out JsonElement parentElm))
        {
            string parentName = parentElm.GetString();
            if (!string.IsNullOrEmpty(parentName))
            {
                gameObject.ParentId = Data.GameObjects.ByName(parentName);
            }
            else
            {
                gameObject.ParentId = null;
            }
        }

        // Set sprite from JSON
        // IMPORTANT: Empty string "" means "intentionally no sprite" (null)
        // This is different from missing property which means "don't change"
        if (root.TryGetProperty("sprite", out JsonElement spriteElm))
        {
            string spriteName = spriteElm.GetString();
            if (!string.IsNullOrEmpty(spriteName))
            {
                var sprite = Data.Sprites.ByName(spriteName);
                if (sprite != null)
                {
                    gameObject.Sprite = sprite;
                }
                else
                {
                    Log($"WARNING: Sprite '{spriteName}' not found for object '{objName}'");
                }
            }
            else
            {
                // Empty string means intentionally no sprite - set to null
                // Do NOT inherit from parent in this case
                gameObject.Sprite = null;
                Log($"SET SPRITE NULL: '{objName}' has empty sprite in JSON - intentionally no sprite");
            }
        }

        if (root.TryGetProperty("visible", out JsonElement visibleElm))
            gameObject.Visible = visibleElm.GetBoolean();
        if (root.TryGetProperty("solid", out JsonElement solidElm))
            gameObject.Solid = solidElm.GetBoolean();
        if (root.TryGetProperty("depth", out JsonElement depthElm))
            gameObject.Depth = depthElm.GetInt32();
        if (root.TryGetProperty("persistent", out JsonElement persistentElm))
            gameObject.Persistent = persistentElm.GetBoolean();

        if (root.TryGetProperty("textureMask", out JsonElement textureMaskElm))
        {
            string textureMaskName = textureMaskElm.GetString();
            if (!string.IsNullOrEmpty(textureMaskName))
            {
                gameObject.TextureMaskId = Data.Sprites.ByName(textureMaskName);
            }
            else
            {
                gameObject.TextureMaskId = null;
            }
        }

        if (Data.IsVersionAtLeast(2022, 5) && root.TryGetProperty("managed", out JsonElement managedElm))
        {
            gameObject.Managed = managedElm.GetBoolean();
        }

        if (root.TryGetProperty("usesPhysics", out JsonElement usesPhysicsElm))
            gameObject.UsesPhysics = usesPhysicsElm.GetBoolean();

        // Always import physics-related properties - they exist even when UsesPhysics is false
        if (root.TryGetProperty("isSensor", out JsonElement isSensorElm))
            gameObject.IsSensor = isSensorElm.GetBoolean();
        if (root.TryGetProperty("collisionShape", out JsonElement collisionShapeElm))
            gameObject.CollisionShape = (CollisionShapeFlags)collisionShapeElm.GetInt32();
        if (root.TryGetProperty("density", out JsonElement densityElm))
            gameObject.Density = (float)densityElm.GetDouble();
        if (root.TryGetProperty("restitution", out JsonElement restitutionElm))
            gameObject.Restitution = (float)restitutionElm.GetDouble();
        if (root.TryGetProperty("group", out JsonElement groupElm))
            gameObject.Group = (uint)groupElm.GetInt64();
        if (root.TryGetProperty("linearDamping", out JsonElement linearDampingElm))
            gameObject.LinearDamping = (float)linearDampingElm.GetDouble();
        if (root.TryGetProperty("angularDamping", out JsonElement angularDampingElm))
            gameObject.AngularDamping = (float)angularDampingElm.GetDouble();
        if (root.TryGetProperty("friction", out JsonElement frictionElm))
            gameObject.Friction = (float)frictionElm.GetDouble();
        if (root.TryGetProperty("awake", out JsonElement awakeElm))
            gameObject.Awake = awakeElm.GetBoolean();
        if (root.TryGetProperty("kinematic", out JsonElement kinematicElm))
            gameObject.Kinematic = kinematicElm.GetBoolean();

        if (root.TryGetProperty("physicsVertices", out JsonElement verticesElm) && verticesElm.ValueKind == JsonValueKind.Array)
        {
            gameObject.PhysicsVertices.Clear();
            var collection = gameObject.PhysicsVertices;
            var addMethod = collection.GetType().GetMethod("Add");
            var vertexType = collection.GetType().GetGenericArguments()[0];
            
            foreach (var vertexElm in verticesElm.EnumerateArray())
            {
                // Create vertex using reflection to avoid Roslyn type resolution issues
                var vertex = Activator.CreateInstance(vertexType);
                if (vertexElm.TryGetProperty("x", out JsonElement xElm))
                    vertexType.GetProperty("X").SetValue(vertex, (float)xElm.GetDouble());
                if (vertexElm.TryGetProperty("y", out JsonElement yElm))
                    vertexType.GetProperty("Y").SetValue(vertex, (float)yElm.GetDouble());
                addMethod.Invoke(collection, new[] { vertex });
            }
        }

        if (root.TryGetProperty("events", out JsonElement eventsElm) && eventsElm.ValueKind == JsonValueKind.Array)
        {
            // Clear existing events to prevent duplicates - JSON contains complete event list from TARGET
            for (int ei = 0; ei < gameObject.Events.Count; ei++)
            {
                gameObject.Events[ei].Clear();
            }
            
            foreach (var eventElm in eventsElm.EnumerateArray())
            {
                if (!eventElm.TryGetProperty("eventType", out JsonElement eventTypeElm)) continue;
                if (!eventElm.TryGetProperty("eventSubtype", out JsonElement eventSubtypeElm)) continue;

                int eventType = eventTypeElm.GetInt32();
                uint eventSubtype = (uint)eventSubtypeElm.GetInt64();

                // For collision events, resolve subtype from object NAME instead of raw index
                if (eventType == (int)EventType.Collision)
                {
                    if (eventElm.TryGetProperty("collisionObjectName", out JsonElement collisionObjElm))
                    {
                        string collisionObjName = collisionObjElm.GetString();
                        if (!string.IsNullOrEmpty(collisionObjName))
                        {
                            var collisionObj = Data.GameObjects.ByName(collisionObjName);
                            if (collisionObj != null)
                            {
                                // Get actual index of the collision object
                                eventSubtype = (uint)Data.GameObjects.IndexOf(collisionObj);
                            }
                            else
                            {
                                PrintLine($"[ImportGameObjects] Warning: Collision object '{collisionObjName}' not found for {objName}");
                                continue;
                            }
                        }
                    }
                }

                if (eventType < 0 || eventType >= gameObject.Events.Count) continue;

                UndertaleGameObject.Event existingEvent = null;
                foreach (var evt in gameObject.Events[eventType])
                {
                    if (evt.EventSubtype == eventSubtype)
                    {
                        existingEvent = evt;
                        break;
                    }
                }

                if (existingEvent == null)
                {
                    existingEvent = new UndertaleGameObject.Event();
                    existingEvent.EventSubtype = eventSubtype;
                    gameObject.Events[eventType].Add(existingEvent);
                }

                if (eventElm.TryGetProperty("actions", out JsonElement actionsElm) && actionsElm.ValueKind == JsonValueKind.Array)
                {
                    int actionIndex = 0;
                    foreach (var actionElm in actionsElm.EnumerateArray())
                    {
                        UndertaleGameObject.EventAction action;
                        if (actionIndex < existingEvent.Actions.Count)
                        {
                            action = existingEvent.Actions[actionIndex];
                        }
                        else
                        {
                            action = new UndertaleGameObject.EventAction();
                            existingEvent.Actions.Add(action);
                        }

                        if (actionElm.TryGetProperty("libId", out JsonElement libIdElm))
                            action.LibID = (uint)libIdElm.GetInt64();
                        if (actionElm.TryGetProperty("id", out JsonElement idElm))
                            action.ID = (uint)idElm.GetInt64();
                        if (actionElm.TryGetProperty("kind", out JsonElement kindElm))
                            action.Kind = (uint)kindElm.GetInt64();
                        if (actionElm.TryGetProperty("useRelative", out JsonElement useRelativeElm))
                            action.UseRelative = useRelativeElm.GetBoolean();
                        if (actionElm.TryGetProperty("isQuestion", out JsonElement isQuestionElm))
                            action.IsQuestion = isQuestionElm.GetBoolean();
                        if (actionElm.TryGetProperty("useApplyTo", out JsonElement useApplyToElm))
                            action.UseApplyTo = useApplyToElm.GetBoolean();
                        if (actionElm.TryGetProperty("exeType", out JsonElement exeTypeElm))
                            action.ExeType = (uint)exeTypeElm.GetInt64();
                        if (actionElm.TryGetProperty("actionName", out JsonElement actionNameElm))
                        {
                            string actionName = actionNameElm.GetString();
                            if (!string.IsNullOrEmpty(actionName))
                                action.ActionName = Data.Strings.MakeString(actionName);
                            else
                                action.ActionName = null; // Clear if empty in JSON
                        }
                        if (actionElm.TryGetProperty("codeId", out JsonElement codeIdElm))
                        {
                            string codeIdName = codeIdElm.GetString();
                            if (!string.IsNullOrEmpty(codeIdName))
                                action.CodeId = Data.Code.ByName(codeIdName);
                        }
                        if (actionElm.TryGetProperty("argumentCount", out JsonElement argumentCountElm))
                            action.ArgumentCount = (uint)argumentCountElm.GetInt64();
                        if (actionElm.TryGetProperty("who", out JsonElement whoElm))
                            action.Who = whoElm.GetInt32();
                        if (actionElm.TryGetProperty("relative", out JsonElement relativeElm))
                            action.Relative = relativeElm.GetBoolean();
                        if (actionElm.TryGetProperty("isNot", out JsonElement isNotElm))
                            action.IsNot = isNotElm.GetBoolean();

                        actionIndex++;
                    }
                }
            }
        }

        // Objects already placed in Phase 2, just track updates here
        if (!isNew)
            gameObjectsUpdated++;

        gameObjectsImported++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ImportGameObjects] Error importing {importData.Name}: {ex.Message}");
    }
}

Console.WriteLine($"[ImportGameObjects] Imported {gameObjectsImported} game objects ({gameObjectsCreated} new, {gameObjectsUpdated} updated)");

CloseLog();
