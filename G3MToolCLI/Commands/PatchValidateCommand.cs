using System.CommandLine;

namespace G3MToolCLI.Commands;

internal static class PatchValidateCommand
{
    public static Command Create()
    {
        var command = new Command("validate", "Validate a G3M patch file and optionally check compatibility with a data file.\n  Usage: patch validate <patch> [--data <data-file>] [--cache <dir>]");
        var patchArg = new Argument<FileInfo>("patch") { Description = "Path to G3M patch file (.g3mpatch)" };
        var dataOption = new Option<FileInfo?>("--data", ["-d"]) { Description = "Optional data file (.win/.ios/.droid/.unx) to check compatibility" };
        var cacheOption = new Option<DirectoryInfo?>("--cache") { Description = "Read reusable .g3mcache analysis when checking --data." };
        command.Add(patchArg);
        command.Add(dataOption);
        command.Add(cacheOption);
        command.SetAction(async parseResult =>
        {
            var patch = parseResult.GetRequiredValue(patchArg);
            var data = parseResult.GetValue(dataOption);
            var cacheDir = parseResult.GetValue(cacheOption);
            if (!Program.JsonOutput)
            {
                Console.WriteLine("Validating G3M patch: " + patch.FullName);
            }
            var result = await PatchService.ValidatePatchAsync(patch.FullName, data?.FullName, G3MCacheOptions.FromDirectory(cacheDir?.FullName), Program.Cancellation);
            if (!result.Success)
            {
                PatchCommand.WriteErrorJsonOrText("patch validate", result.Error);
                Environment.ExitCode = 1;
                return;
            }
            if (Program.JsonOutput)
            {
                WriteJson(patch, data, result.Manifest);
                return;
            }
            Console.WriteLine("Patch is valid.");
            if (result.Manifest != null)
            {
                Console.WriteLine("  Tool: " + result.Manifest.Tool?.Name + " v" + result.Manifest.Tool?.Version);
                Console.WriteLine("  Created: " + result.Manifest.CreatedAt);
                Console.WriteLine($"  Resources: {result.Manifest.Statistics?.TotalChanged ?? 0} changed, {result.Manifest.Statistics?.TotalNew ?? 0} new, {result.Manifest.Statistics?.TotalDeleted ?? 0} deleted");
            }
        });
        return command;
    }

    private static void WriteJson(FileInfo patch, FileInfo? data, G3MPatchManifest? manifest)
    {
        PatchCommand.WriteJson(new
        {
            success = true,
            command = "patch validate",
            patch = patch.FullName,
            data = data?.FullName,
            tool = manifest?.Tool,
            createdAt = manifest?.CreatedAt,
            original = manifest?.Original,
            modified = manifest?.Modified,
            statistics = manifest?.Statistics,
            applyPlan = manifest?.ApplyPlan,
            resourceTypes = manifest?.Resources?.Where(kvp => (kvp.Value.Changed?.Count ?? 0) > 0 || (kvp.Value.New?.Count ?? 0) > 0 || (kvp.Value.Deleted?.Count ?? 0) > 0).ToDictionary(kvp => kvp.Key, kvp => new
            {
                changed = kvp.Value.Changed?.Count ?? 0,
                @new = kvp.Value.New?.Count ?? 0,
                deleted = kvp.Value.Deleted?.Count ?? 0
            }, StringComparer.OrdinalIgnoreCase),
            warnings = Array.Empty<string>()
        });
    }
}
