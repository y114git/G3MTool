using System.CommandLine;
using System.IO.Compression;
using System.Text.Json;
using G3MLib.DataFile;

namespace G3MToolCLI.Commands;

public static class InfoCommand
{

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true
    };

    private static bool IsPatchExtension(string? extension)
    {
        var text = extension?.ToLowerInvariant();
        if (text is ".g3mpatch" or ".zip")
        {
            return true;
        }
        return false;
    }

    public static Command Create()
    {
        var command = new Command("info", "Show metadata for a data file or .g3mpatch.\n  Usage: info <target> [--cache <dir>]\n  Without -v: counts, GeneralInfo, and short breakdowns\n  With -v: full per-resource listing");
        var targetArg = new Argument<FileInfo>("target") { Description = "Path to data file (.win/.ios/.droid/.unx) or .g3mpatch" };
        var verboseOption = new Option<bool>("--verbose", ["-v"]) { Description = "Show full per-resource listing" };
        var cacheOption = new Option<DirectoryInfo?>("--cache") { Description = "Read and write reusable .g3mcache info analysis in this directory." };
        command.Add(targetArg);
        command.Add(verboseOption);
        command.Add(cacheOption);
        command.SetAction(async parseResult =>
        {
            var target = parseResult.GetRequiredValue(targetArg);
            var verbose = parseResult.GetValue(verboseOption);
            var cacheDir = parseResult.GetValue(cacheOption);
            var jsonOutput = Program.JsonOutput;
            var cacheOptions = G3MCacheOptions.FromDirectory(cacheDir?.FullName);
            if (IsPatchExtension(Path.GetExtension(target.FullName)))
            {
                await ShowPatchInfoAsync(target.FullName, verbose, jsonOutput);
            }
            else
            {
                await ShowDataFileInfoAsync(target.FullName, verbose, jsonOutput, cacheOptions);
            }
        });
        return command;
    }

    private static async Task ShowDataFileInfoAsync(string path, bool verbose, bool jsonOutput, G3MCacheOptions? cacheOptions)
    {
        try
        {
            if (!verbose)
            {
                var cached = G3MCacheService.TryReadDataInfoSnapshot(path, cacheOptions);
                if (cached != null)
                {
                    InfoPrinter.PrintSnapshot(cached, jsonOutput);
                    return;
                }
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
            using var data = await GameMakerIO.ReadAsync(stream, cancellationToken: Program.Cancellation);
            if (!verbose)
            {
                await G3MCacheService.WriteDataInfoCacheAsync(path, G3MCacheService.BuildInfoSnapshot(path, data), cacheOptions);
            }
            InfoPrinter.PrintDataFile(path, data, verbose, jsonOutput);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Error reading data file: " + ex.Message);
            Environment.ExitCode = 1;
        }
    }

    private static async Task ShowPatchInfoAsync(string path, bool verbose, bool jsonOutput)
    {
        var result = await PatchService.ValidatePatchAsync(path, null, null, Program.Cancellation);
        if (!result.Success || result.Manifest == null)
        {
            if (jsonOutput)
            {
                // Plain text would break a JSON consumer, so a patch without a readable manifest is an error here.
                PatchCommand.WriteErrorJsonOrText("info", result.Error);
                Environment.ExitCode = 1;
                return;
            }
            await ShowArchiveInfoAsync(path, result.Error, verbose);
            return;
        }
        var manifest = result.Manifest;
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(manifest, s_jsonOptions));
            return;
        }
        Console.WriteLine("G3M Patch: " + Path.GetFileName(path));
        Console.WriteLine("Created: " + manifest.CreatedAt);
        Console.WriteLine("Tool: " + manifest.Tool?.Name + " v" + manifest.Tool?.Version);
        Console.WriteLine();
        Console.WriteLine("Original:");
        Console.WriteLine("  File: " + manifest.Original?.Filename);
        Console.WriteLine($"  Size: {manifest.Original?.Size:N0} bytes");
        Console.WriteLine("  GMS: " + manifest.Original?.GmsVersion);
        Console.WriteLine();
        Console.WriteLine("Statistics:");
        Console.WriteLine($"  Changed: {manifest.Statistics?.TotalChanged ?? 0}");
        Console.WriteLine($"  New: {manifest.Statistics?.TotalNew ?? 0}");
        Console.WriteLine($"  Deleted: {manifest.Statistics?.TotalDeleted ?? 0}");
        if (verbose && manifest.Resources != null)
        {
            PrintResourcesByType(manifest.Resources);
        }
    }

    private static void PrintResourcesByType(Dictionary<string, ResourceTypeChanges> resources)
    {
        Console.WriteLine();
        Console.WriteLine("Resources by type:");
        foreach (var (type, changes) in resources)
        {
            var changed = changes.Changed?.Count ?? 0;
            var newRes = changes.New?.Count ?? 0;
            var deleted = changes.Deleted?.Count ?? 0;
            if (changed + newRes + deleted > 0)
            {
                Console.WriteLine($"  {type}: {changed} changed, {newRes} new, {deleted} deleted");
            }
        }
    }

    // A patch whose manifest cannot be read may still be a zip archive: list what it holds.
    private static async Task ShowArchiveInfoAsync(string path, string? warning, bool verbose)
    {
        Console.WriteLine("Warning: " + warning);
        Console.WriteLine("Attempting to show basic patch information...");
        Console.WriteLine();
        try
        {
            using var zipStream = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            List<string> entries = [.. archive.Entries.Select(e => e.FullName)];
            List<string> folders = [.. (from e in entries
                                    select e.Split('/')[0] into f
                                    where !string.IsNullOrEmpty(f)
                                    select f).Distinct()];
            Console.WriteLine("Patch File: " + Path.GetFileName(path));
            Console.WriteLine($"Size: {new FileInfo(path).Length:N0} bytes");
            Console.WriteLine($"Entries: {entries.Count}");
            Console.WriteLine("Detected folders: " + string.Join(", ", folders));
            if (!verbose)
            {
                return;
            }
            Console.WriteLine();
            Console.WriteLine("Patch contents:");
            foreach (var entry in entries.Take(50))
            {
                Console.WriteLine("  " + entry);
            }
            if (entries.Count > 50)
            {
                Console.WriteLine($"  ... and {entries.Count - 50} more");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Failed to read patch file: " + ex.Message);
            Environment.ExitCode = 1;
        }
    }
}
