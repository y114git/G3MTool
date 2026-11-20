using System.CommandLine;
using System.Text.Json;

namespace G3MToolCLI.Commands;

public static class PatchCommand
{

    private static readonly JsonSerializerOptions s_compactJsonOptions = new()
    {
        WriteIndented = false
    };

    public static Command Create()
    {
        return new Command("patch", "Create, apply, validate, or merge .g3mpatch files.")
        {
            PatchCreateCommand.Create(),
            PatchApplyCommand.Create(),
            PatchValidateCommand.Create(),
            PatchBatchCommand.Create(),
            PatchMergeCommand.Create()
        };
    }

    internal static void WriteSuccessJsonOrText<T>(string command, string outputPath, T details)
    {
        if (Program.JsonOutput)
        {
            WriteJson(new
            {
                success = true,
                command,
                output = outputPath,
                details,
                warnings = Array.Empty<string>()
            });
        }
        else
        {
            Console.WriteLine("Patch applied successfully: " + outputPath);
        }
    }

    internal static void WriteErrorJsonOrText(string command, string? error)
    {
        if (Program.JsonOutput)
        {
            WriteJson(new
            {
                success = false,
                command,
                error
            });
        }
        else
        {
            Console.Error.WriteLine("Error: " + error);
        }
    }

    internal static void DeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked temporary folder must not fail the command; say where it stayed.
            G3MLib.Modding.Logging.LogService.Warning($"Could not delete the temporary folder {path}.");
        }
    }

    internal static void WriteJson<T>(T value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, s_compactJsonOptions));
    }
}
