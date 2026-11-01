using System.Diagnostics;
using G3MLib.Modding.Logging;

namespace G3MToolGUI.Services;

public sealed record ExternalProcessResult(int ExitCode);

public static class ExternalProcessService
{
    public static async Task<ExternalProcessResult> RunAsync(string program, IReadOnlyList<string> arguments)
    {
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = program,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null) return new(-1);
            var standardOutput = ReadLinesAsync(process.StandardOutput, LogService.Info);
            var standardError = ReadLinesAsync(process.StandardError, LogService.Error);
            await process.WaitForExitAsync();
            await Task.WhenAll(standardOutput, standardError);
            return new(process.ExitCode);
        }
        catch (Exception exception)
        {
            LogService.Error("Unable to start program: " + exception.Message);
            return new(-1);
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> log)
    {
        while (await reader.ReadLineAsync() is { } line)
            if (line.Length != 0) log(line);
    }
}
