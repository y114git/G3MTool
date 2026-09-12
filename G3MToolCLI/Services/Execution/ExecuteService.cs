using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using G3MLib.DataFile;
using G3MLib.DataFile.Legacy;
using G3MToolCLI.Models.Scripting;
using G3MToolCLI.Services.Logging;
using ImageMagick;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace G3MToolCLI.Services.Execution;

public static class ExecuteService
{
    private static readonly System.Threading.Lock s_scriptLock = new();
    private static (string Content, string? Path, Script<object> Script)? s_lastScript;

    private static readonly string[] DefaultImports = new string[15]
    {
        "System", "System.IO", "System.Text", "System.Text.Json", "System.Text.RegularExpressions", "System.Linq", "System.Threading.Tasks", "System.Collections.Generic", "G3MLib.DataFile", "G3MLib.DataFile.Models",
        "G3MLib.DataFile.Util", "G3MLib.DataFile.Decompiler", "G3MLib.DataFile.Compiler", "G3MLib.DataFile.Scripting", "ImageMagick"
    };

    private static ScriptOptions GetDefaultOptions(string? scriptPath = null)
    {
        ScriptOptions options = ScriptOptions.Default.AddImports(DefaultImports).AddReferences(typeof(GameMakerData).Assembly, typeof(MagickImage).Assembly, typeof(JsonSerializer).Assembly).WithOptimizationLevel(OptimizationLevel.Release);
        if (!string.IsNullOrWhiteSpace(scriptPath))
        {
            string scriptDirectory = Path.GetDirectoryName(Path.GetFullPath(scriptPath)) ?? Environment.CurrentDirectory;
            options = options.WithFilePath(Path.GetFullPath(scriptPath)).WithSourceResolver(ScriptSourceResolver.Default.WithBaseDirectory(scriptDirectory)).WithMetadataResolver(ScriptMetadataResolver.Default.WithBaseDirectory(scriptDirectory));
        }
        return options;
    }

    public static async Task<ScriptResult> ExecuteScriptAsync(string scriptPath, string? dataPath, string outputPath, string[] args)
    {
        if (!File.Exists(scriptPath))
        {
            return new ScriptResult
            {
                Success = false,
                Error = "Script not found: " + scriptPath
            };
        }
        bool hasData = !string.IsNullOrEmpty(dataPath) && File.Exists(dataPath);
        if (!string.IsNullOrEmpty(dataPath) && !hasData)
        {
            return new ScriptResult
            {
                Success = false,
                Error = "Data file not found: " + dataPath
            };
        }
        GameMakerData? data = null;
        ScriptGlobals? globals = null;
        try
        {
            scriptPath = Path.GetFullPath(scriptPath);
            if (hasData)
            {
                LogService.Log("[ScriptExecutor] Loading data file: " + dataPath);
                using (FileStream stream = new FileStream(dataPath!, FileMode.Open, FileAccess.Read))
                {
                    data = GameMakerIO.Read(stream);
                }
                LogService.Log("[ScriptExecutor] Data loaded. Game: " + (data.GeneralInfo?.DisplayName?.Content ?? "Unknown"));
            }
            else
            {
                LogService.Log("[ScriptExecutor] No data file specified, running script without game data");
            }
            LogService.Log("[ScriptExecutor] Executing script: " + scriptPath);
            string outputDir = Directory.Exists(outputPath) ? outputPath : (Path.GetDirectoryName(outputPath) ?? outputPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }
            globals = new ScriptGlobals
            {
                Data = data,
                FilePath = dataPath ?? scriptPath,
                ScriptPath = scriptPath,
                ScriptRootPath = Path.GetDirectoryName(Path.GetFullPath(scriptPath)) ?? Environment.CurrentDirectory,
                DataFilePath = (dataPath ?? string.Empty),
                OutputDir = (outputDir ?? string.Empty),
                InputDir = (args.FirstOrDefault() ?? string.Empty)
            };
            ScriptResult result = await ExecuteScriptWithRoslynAsync(UTMTScriptTranslator.Translate(await File.ReadAllTextAsync(scriptPath)), globals);
            if (!result.Success)
            {
                return result;
            }
            if (globals.Data is not null && !string.IsNullOrEmpty(outputPath))
            {
                LogService.Log("[ScriptExecutor] Saving to: " + outputPath);
                string? outDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                {
                    Directory.CreateDirectory(outDir);
                }
                PatchInputService.WriteDataFile(globals.Data, outputPath, dataPath);
            }
            LogService.Log("[ScriptExecutor] Script executed successfully");
            return new ScriptResult
            {
                Success = true,
                Output = result.Output
            };
        }
        catch (Exception ex2)
        {
            return new ScriptResult
            {
                Success = false,
                Error = "Script execution failed: " + ex2.Message
            };
        }
        finally
        {
            if (!ReferenceEquals(data, globals?.Data)) globals?.Data?.Dispose();
            data?.Dispose();
        }
    }

    private static async Task<ScriptResult> ExecuteScriptWithRoslynAsync(string scriptContent, ScriptGlobals globals)
    {
        try
        {
            globals.ScriptRunner = (string path) => ExecuteNestedScriptAsync(path, globals).GetAwaiter().GetResult();
            Script<object> script;
            lock (s_scriptLock)
            {
                if (s_lastScript is { } cached && cached.Content == scriptContent && cached.Path == globals.ScriptPath)
                    script = cached.Script;
                else
                {
                    script = CSharpScript.Create(scriptContent, GetDefaultOptions(globals.ScriptPath), typeof(ScriptGlobals));
                    script.Compile();
                    s_lastScript = (scriptContent, globals.ScriptPath, script);
                }
            }
            TextWriter? originalOut = null;
            if (!LogService.Verbose)
            {
                originalOut = Console.Out;
                Console.SetOut(TextWriter.Null);
            }
            try
            {
                await script.RunAsync(globals).ConfigureAwait(continueOnCapturedContext: false);
            }
            finally
            {
                if (originalOut != null)
                {
                    Console.SetOut(originalOut);
                }
            }
            return new ScriptResult
            {
                Success = true
            };
        }
        catch (CompilationErrorException ex)
        {
            string errors = string.Join("\n", ex.Diagnostics.Select((Diagnostic d) => d.ToString()));
            return new ScriptResult
            {
                Success = false,
                Error = "Compilation error:\n" + errors
            };
        }
        catch (ScriptException ex2)
        {
            return new ScriptResult
            {
                Success = false,
                Error = ex2.Message
            };
        }
        catch (Exception ex3)
        {
            return new ScriptResult
            {
                Success = false,
                Error = "Runtime error: " + ex3.Message
            };
        }
    }

    private static async Task<bool> ExecuteNestedScriptAsync(string path, ScriptGlobals globals)
    {
        string scriptDirectory = Path.GetDirectoryName(globals.ScriptPath) ?? globals.ScriptRootPath;
        path = Path.GetFullPath(path, scriptDirectory);
        string relativePath = Path.GetRelativePath(globals.ScriptRootPath, path);
        if (Path.IsPathRooted(relativePath) || relativePath.Equals("..", StringComparison.Ordinal) || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ScriptException("Nested script path escapes the mod directory.");
        }
        if (!File.Exists(path))
        {
            throw new ScriptException("Script not found: " + path);
        }
        string? previousPath = globals.ScriptPath;
        try
        {
            globals.ScriptPath = path;
            await CSharpScript.EvaluateAsync(UTMTScriptTranslator.Translate(await File.ReadAllTextAsync(path).ConfigureAwait(continueOnCapturedContext: false)), GetDefaultOptions(path), globals, typeof(ScriptGlobals)).ConfigureAwait(continueOnCapturedContext: false);
            return true;
        }
        finally
        {
            globals.ScriptPath = previousPath;
        }
    }
}

public sealed class ScriptResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Output { get; set; }
}
