using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using UndertaleModLib;

namespace G3MToolCLI.Services;

public class ScriptExecutorService
{
    private static readonly string[] DefaultImports = new[]
    {
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
    };

    public async Task<ScriptResult> ExecuteScriptAsync(
        string scriptPath, 
        string dataPath, 
        string outputPath,
        string[] args)
    {
        if (!File.Exists(scriptPath))
            return new ScriptResult { Success = false, Error = $"Script not found: {scriptPath}" };

        if (!File.Exists(dataPath))
            return new ScriptResult { Success = false, Error = $"Data file not found: {dataPath}" };

        try
        {
            LogService.Log($"[ScriptExecutor] Loading data file: {dataPath}");
            
            UndertaleData data;
            using (var stream = new FileStream(dataPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            LogService.Log($"[ScriptExecutor] Data loaded. Game: {data.GeneralInfo?.DisplayName?.Content ?? "Unknown"}");
            LogService.Log($"[ScriptExecutor] Executing script: {scriptPath}");

            // Determine outputDir for script globals
            var outputDir = Directory.Exists(outputPath) ? outputPath : (Path.GetDirectoryName(outputPath) ?? outputPath);
            
            var scriptContent = await File.ReadAllTextAsync(scriptPath);
            var result = await ExecuteScriptWithRoslynAsync(scriptContent, data, scriptPath, outputDir, null, dataPath);

            if (!result.Success)
                return result;

            LogService.Log($"[ScriptExecutor] Saving to: {outputPath}");
            
            var outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            {
                UndertaleIO.Write(stream, data);
            }

            LogService.Log("[ScriptExecutor] Script executed successfully");
            return new ScriptResult { Success = true, Output = result.Output };
        }
        catch (Exception ex)
        {
            return new ScriptResult { Success = false, Error = $"Script execution failed: {ex.Message}" };
        }
    }

    private async Task<ScriptResult> ExecuteScriptWithRoslynAsync(
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

            var options = ScriptOptions.Default
                .AddImports(DefaultImports)
                .AddReferences(
                    typeof(UndertaleData).Assembly,
                    typeof(ImageMagick.MagickImage).Assembly,
                    typeof(System.Text.Json.JsonSerializer).Assembly
                )
                .WithOptimizationLevel(Microsoft.CodeAnalysis.OptimizationLevel.Release);

            await CSharpScript.RunAsync(scriptContent, options, globals, typeof(ScriptGlobals));
            
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

    public async Task<ScriptResult> ExecuteEmbeddedScriptAsync(
        string scriptName,
        string dataPath,
        string outputPath,
        string? inputDir = null)
    {
        if (!File.Exists(dataPath))
            return new ScriptResult { Success = false, Error = $"Data file not found: {dataPath}" };

        // Determine outputDir - if outputPath is a directory use it, otherwise use parent
        var outputDir = Directory.Exists(outputPath) ? outputPath : (Path.GetDirectoryName(outputPath) ?? outputPath);

        try
        {
            LogService.Log($"[ScriptExecutor] Loading data file: {dataPath}");
            
            UndertaleData data;
            using (var stream = new FileStream(dataPath, FileMode.Open, FileAccess.Read))
            {
                data = UndertaleIO.Read(stream);
            }

            LogService.Log($"[ScriptExecutor] Data loaded. Game: {data.GeneralInfo?.DisplayName?.Content ?? "Unknown"}");

            // Try to load embedded script
            var scriptContent = await LoadEmbeddedScriptAsync(scriptName);
            if (scriptContent == null)
            {
                // Try loading from file path
                if (File.Exists(scriptName))
                {
                    scriptContent = await File.ReadAllTextAsync(scriptName);
                }
                else
                {
                    return new ScriptResult { Success = false, Error = $"Script not found: {scriptName}" };
                }
            }

            LogService.Log($"[ScriptExecutor] Executing script: {scriptName}");
            var result = await ExecuteScriptWithRoslynAsync(scriptContent, data, scriptName, outputDir, inputDir, dataPath);

            if (!result.Success)
                return result;

            // For import scripts, save the modified data
            if (scriptName.StartsWith("Import", StringComparison.OrdinalIgnoreCase))
            {
                LogService.Log($"[ScriptExecutor] Saving to: {outputPath}");
                
                var outDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                    Directory.CreateDirectory(outDir);

                using var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                UndertaleIO.Write(outStream, data);
            }

            LogService.Log("[ScriptExecutor] Script executed successfully");
            return new ScriptResult { Success = true };
        }
        catch (Exception ex)
        {
            return new ScriptResult { Success = false, Error = $"Script execution failed: {ex.Message}" };
        }
    }

    private async Task<string?> LoadEmbeddedScriptAsync(string scriptName)
    {
        // Try different resource name formats
        var assembly = typeof(ScriptExecutorService).Assembly;
        var resourceNames = new[]
        {
            $"G3MToolCLI.Assets.scripts.{scriptName}",
            $"G3MToolCLI.Assets.scripts.{scriptName}.csx",
            scriptName
        };

        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync();
            }
        }

        // Try loading from Assets/scripts folder relative to executable
        var scriptPaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "scripts", scriptName),
            Path.Combine(AppContext.BaseDirectory, "Assets", "scripts", $"{scriptName}.csx"),
            Path.Combine(Environment.CurrentDirectory, "Assets", "scripts", scriptName),
            Path.Combine(Environment.CurrentDirectory, "Assets", "scripts", $"{scriptName}.csx"),
        };

        foreach (var path in scriptPaths)
        {
            if (File.Exists(path))
            {
                return await File.ReadAllTextAsync(path);
            }
        }

        return null;
    }

    public IEnumerable<string> GetAvailableScripts()
    {
        var scripts = new List<string>();
        
        // Check Assets/scripts folder
        var scriptsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "scripts");
        if (Directory.Exists(scriptsDir))
        {
            scripts.AddRange(Directory.GetFiles(scriptsDir, "*.csx")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n != null)!);
        }

        return scripts.Distinct().OrderBy(s => s);
    }
}

public class ScriptResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Output { get; set; }
}
