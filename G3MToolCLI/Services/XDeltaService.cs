using System.Diagnostics;
using G3MToolCLI.Utils;

namespace G3MToolCLI.Services;

public class XDeltaService
{
    private readonly string? _xdeltaPath;

    public XDeltaService()
    {
        _xdeltaPath = PlatformUtils.GetXDeltaPath();
    }

    public async Task<XDeltaResult> CreatePatchAsync(string originalPath, string modifiedPath, string outputPath)
    {
        if (_xdeltaPath == null)
            return new XDeltaResult { Success = false, Error = "xdelta executable not found" };

        if (!File.Exists(originalPath))
            return new XDeltaResult { Success = false, Error = $"Original file not found: {originalPath}" };
        
        if (!File.Exists(modifiedPath))
            return new XDeltaResult { Success = false, Error = $"Modified file not found: {modifiedPath}" };

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        // xdelta3 -e -s <original> <modified> <output>
        var args = new[] { "-e", "-s", originalPath, modifiedPath, outputPath };
        return await ExecuteXDeltaAsync(args);
    }

    public async Task<XDeltaResult> ApplyPatchAsync(string originalPath, string patchPath, string outputPath)
    {
        if (_xdeltaPath == null)
            return new XDeltaResult { Success = false, Error = "xdelta executable not found" };

        if (!File.Exists(originalPath))
            return new XDeltaResult { Success = false, Error = $"Original file not found: {originalPath}" };
        
        if (!File.Exists(patchPath))
            return new XDeltaResult { Success = false, Error = $"Patch file not found: {patchPath}" };

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        // xdelta3 -d -s <original> <patch> <output>
        var args = new[] { "-d", "-s", originalPath, patchPath, outputPath };
        return await ExecuteXDeltaAsync(args);
    }

    public async Task<XDeltaResult> ExecuteRawAsync(string[] args)
    {
        if (_xdeltaPath == null)
            return new XDeltaResult { Success = false, Error = "xdelta executable not found" };

        return await ExecuteXDeltaAsync(args);
    }

    private async Task<XDeltaResult> ExecuteXDeltaAsync(string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _xdeltaPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new XDeltaResult { Success = false, Error = "Failed to start xdelta process" };
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode == 0)
            {
                return new XDeltaResult { Success = true, Output = output };
            }
            else
            {
                return new XDeltaResult 
                { 
                    Success = false, 
                    Error = string.IsNullOrEmpty(error) ? $"xdelta exited with code {process.ExitCode}" : error,
                    Output = output
                };
            }
        }
        catch (Exception ex)
        {
            return new XDeltaResult { Success = false, Error = ex.Message };
        }
    }
}

public class XDeltaResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Output { get; set; }
}
