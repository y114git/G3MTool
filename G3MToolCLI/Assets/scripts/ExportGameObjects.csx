


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
    string typeDir = Path.Combine(outputDir, "GameObjects");
    if (!Directory.Exists(typeDir))
        Directory.CreateDirectory(typeDir);
    return typeDir;
}




EnsureDataLoaded();

string objOut = GetOutputDirectory();
PrintLine($"[ExportGameObjects] Exporting to: {objOut}");

List<UndertaleGameObject> allGameObjects = Data.GameObjects?.ToList() ?? new List<UndertaleGameObject>();
PrintLine($"[ExportGameObjects] Found {allGameObjects.Count} game objects to export.");

SetProgressBar(null, "Exporting Game Objects", 0, allGameObjects.Count);
StartProgressBarUpdater();

await Task.Run(() => Parallel.ForEach(allGameObjects, obj => ExportGameObject(obj, objOut)));

void ExportGameObject(UndertaleGameObject gameObject, string outputDir)
{
    if (gameObject?.Name?.Content == null)
    {
        IncrementProgressParallel();
        return;
    }

    try
    {
        string objName = SafeName(gameObject.Name.Content);
        
        // Create subdirectory for this game object
        string objDir = Path.Combine(outputDir, objName);
        Directory.CreateDirectory(objDir);
        
        string objFile = Path.Combine(objDir, "object.json");

        using (var stream = new FileStream(objFile, FileMode.Create))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            writer.WriteString("name", gameObject.Name?.Content ?? "");
            writer.WriteString("sprite", gameObject.Sprite?.Name?.Content ?? "");
            writer.WriteBoolean("visible", gameObject.Visible);
            writer.WriteBoolean("solid", gameObject.Solid);
            writer.WriteNumber("depth", gameObject.Depth);
            writer.WriteBoolean("persistent", gameObject.Persistent);
            writer.WriteString("parent", gameObject.ParentId?.Name?.Content ?? "");
            writer.WriteString("textureMask", gameObject.TextureMaskId?.Name?.Content ?? "");
            
            if (Data.IsVersionAtLeast(2022, 5))
                writer.WriteBoolean("managed", gameObject.Managed);

            writer.WriteBoolean("usesPhysics", gameObject.UsesPhysics);
            if (gameObject.UsesPhysics)
            {
                writer.WriteBoolean("isSensor", gameObject.IsSensor);
                writer.WriteNumber("collisionShape", (int)gameObject.CollisionShape);
                writer.WriteString("collisionShapeDescription", gameObject.CollisionShape.ToString());
                writer.WriteNumber("density", gameObject.Density);
                writer.WriteNumber("restitution", gameObject.Restitution);
                writer.WriteNumber("group", gameObject.Group);
                writer.WriteNumber("linearDamping", gameObject.LinearDamping);
                writer.WriteNumber("angularDamping", gameObject.AngularDamping);
                writer.WriteNumber("friction", gameObject.Friction);
                writer.WriteBoolean("awake", gameObject.Awake);
                writer.WriteBoolean("kinematic", gameObject.Kinematic);

                if (gameObject.PhysicsVertices != null && gameObject.PhysicsVertices.Count > 0)
                {
                    writer.WritePropertyName("physicsVertices");
                    writer.WriteStartArray();
                    foreach (var vertex in gameObject.PhysicsVertices)
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("x", vertex.X);
                        writer.WriteNumber("y", vertex.Y);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
            }

            writer.WritePropertyName("events");
            writer.WriteStartArray();
            for (int eventTypeIdx = 0; eventTypeIdx < gameObject.Events.Count; eventTypeIdx++)
            {
                var eventList = gameObject.Events[eventTypeIdx];
                if (eventList == null || eventList.Count == 0) continue;

                foreach (var evt in eventList)
                {
                    if (evt == null) continue;

                    writer.WriteStartObject();
                    writer.WriteNumber("eventType", eventTypeIdx);
                    writer.WriteString("eventTypeName", ((EventType)eventTypeIdx).ToString());
                    writer.WriteNumber("eventSubtype", evt.EventSubtype);

                    if (eventTypeIdx == (int)EventType.Collision && evt.EventSubtype < Data.GameObjects.Count)
                    {
                        var collisionObject = Data.GameObjects[(int)evt.EventSubtype];
                        writer.WriteString("collisionObjectName", collisionObject?.Name?.Content ?? "");
                    }

                    writer.WritePropertyName("actions");
                    writer.WriteStartArray();
                    foreach (var action in evt.Actions)
                    {
                        if (action == null) continue;
                        writer.WriteStartObject();
                        writer.WriteNumber("libId", action.LibID);
                        writer.WriteNumber("id", action.ID);
                        writer.WriteNumber("kind", action.Kind);
                        writer.WriteBoolean("useRelative", action.UseRelative);
                        writer.WriteBoolean("isQuestion", action.IsQuestion);
                        writer.WriteBoolean("useApplyTo", action.UseApplyTo);
                        writer.WriteNumber("exeType", action.ExeType);
                        writer.WriteString("actionName", action.ActionName?.Content ?? "");
                        writer.WriteString("codeId", action.CodeId?.Name?.Content ?? "");
                        writer.WriteNumber("argumentCount", action.ArgumentCount);
                        writer.WriteNumber("who", action.Who);
                        writer.WriteBoolean("relative", action.Relative);
                        writer.WriteBoolean("isNot", action.IsNot);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();

                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
    }
    catch (Exception ex)
    {
        PrintLine($"[ExportGameObjects] Failed to export game object {gameObject.Name?.Content}: {ex.Message}");
    }

    IncrementProgressParallel();
}

await StopProgressBarUpdater();
HideProgressBar();

PrintLine($"[ExportGameObjects] Export complete. {allGameObjects.Count} game objects exported to {objOut}");




