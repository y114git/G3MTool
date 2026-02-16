using System.Collections.Concurrent;
using G3MToolCLI.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using UndertaleModLib;

namespace G3MToolCLI.Services;

public class ExecuteService
{
    private static readonly ConcurrentDictionary<string, Script<object>> _scriptCache = new();

    private static ScriptOptions GetDefaultOptions() => ScriptOptions.Default
        .AddImports(DefaultImports)
        .AddReferences(
            typeof(UndertaleData).Assembly,
            typeof(ImageMagick.MagickImage).Assembly,
            typeof(System.Text.Json.JsonSerializer).Assembly
        )
        .WithOptimizationLevel(OptimizationLevel.Release);

    private static readonly string[] DefaultImports =
    [
        "System",
        "System.IO",
        "System.Text",
        "System.Text.Json",
        "System.Linq",
        "System.Threading.Tasks",
        "System.Collections.Generic",
        "UndertaleModLib",
        "UndertaleModLib.Models",
        "UndertaleModLib.Util",
        "UndertaleModLib.Decompiler",
        "UndertaleModLib.Compiler",
        "ImageMagick"
    ];

    public static async Task<ScriptResult> ExecuteScriptAsync(
        string scriptPath,
        string? dataPath,
        string outputPath,
        string[] args)
    {
        if (!File.Exists(scriptPath))
            return new ScriptResult { Success = false, Error = $"Script not found: {scriptPath}" };

        var hasData = !string.IsNullOrEmpty(dataPath) && File.Exists(dataPath);

        if (!string.IsNullOrEmpty(dataPath) && !hasData)
            return new ScriptResult { Success = false, Error = $"Data file not found: {dataPath}" };

        try
        {
            UndertaleData? data = null;

            if (hasData)
            {
                LogService.Log($"[ScriptExecutor] Loading data file: {dataPath}");

                using (var stream = new FileStream(dataPath!, FileMode.Open, FileAccess.Read))
                {
                    data = UndertaleIO.Read(stream);
                }

                LogService.Log($"[ScriptExecutor] Data loaded. Game: {data.GeneralInfo?.DisplayName?.Content ?? "Unknown"}");
            }
            else
            {
                LogService.Log("[ScriptExecutor] No data file specified, running script without game data");
            }

            LogService.Log($"[ScriptExecutor] Executing script: {scriptPath}");

            // Determine outputDir for script globals
            var outputDir = Directory.Exists(outputPath) ? outputPath : (Path.GetDirectoryName(outputPath) ?? outputPath);

            // Use first argument as inputDir for import scripts
            var inputDir = args.Length > 0 ? args[0] : null;

            var scriptContent = await File.ReadAllTextAsync(scriptPath);
            var result = await ExecuteScriptWithRoslynAsync(scriptContent, data!, scriptPath, outputDir, inputDir, dataPath);

            if (!result.Success)
                return result;

            if (hasData && !string.IsNullOrEmpty(outputPath))
            {
                LogService.Log($"[ScriptExecutor] Saving to: {outputPath}");

                var outDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                    Directory.CreateDirectory(outDir);

                using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                UndertaleIO.Write(stream, data!);
            }

            LogService.Log("[ScriptExecutor] Script executed successfully");
            return new ScriptResult { Success = true, Output = result.Output };
        }
        catch (Exception ex)
        {
            return new ScriptResult { Success = false, Error = $"Script execution failed: {ex.Message}" };
        }
    }

    private static async Task<ScriptResult> ExecuteScriptWithRoslynAsync(
        string scriptContent,
        UndertaleData data,
        string? scriptPath = null,
        string? outputDir = null,
        string? inputDir = null,
        string? dataFilePath = null)
    {
        try
        {
            var globals = new ScriptGlobals
            {
                Data = data,
                FilePath = dataFilePath ?? scriptPath ?? "inline",
                ScriptPath = scriptPath,
                DataFilePath = dataFilePath ?? string.Empty,
                OutputDir = outputDir ?? string.Empty,
                InputDir = inputDir ?? string.Empty
            };

            // Use cached compiled script if available, otherwise compile and cache
            var cacheKey = scriptPath ?? scriptContent;
            var script = _scriptCache.GetOrAdd(cacheKey, _ =>
            {
                var s = CSharpScript.Create(scriptContent, GetDefaultOptions(), typeof(ScriptGlobals));
                s.Compile();
                return s;
            });

            // Suppress script console output in non-verbose mode
            TextWriter? originalOut = null;
            if (!LogService.Verbose)
            {
                originalOut = Console.Out;
                Console.SetOut(TextWriter.Null);
            }
            try
            {
                await script.RunAsync(globals);
            }
            finally
            {
                if (originalOut != null)
                    Console.SetOut(originalOut);
            }

            return new ScriptResult { Success = true };
        }
        catch (CompilationErrorException ex)
        {
            var errors = string.Join("\n", ex.Diagnostics.Select(d => d.ToString()));
            return new ScriptResult { Success = false, Error = $"Compilation error:\n{errors}" };
        }
        catch (ScriptException ex)
        {
            return new ScriptResult { Success = false, Error = ex.Message };
        }
        catch (Exception ex)
        {
            return new ScriptResult { Success = false, Error = $"Runtime error: {ex.Message}" };
        }
    }

}

public class ScriptResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Output { get; set; }
}
