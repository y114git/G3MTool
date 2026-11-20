using System.Diagnostics;
using G3MLib.Modding.Logging;

namespace G3MToolGUI.Services;

public sealed record ExternalProcessResult(int ExitCode, string ErrorText = "");

public static class ExternalProcessService
{
    /// <summary>
    /// Runs a program and logs its output. Cancelling the token kills the process and its children and throws <see cref="OperationCanceledException"/>.
    /// With <paramref name="errorOutputIsError"/> off, standard error is logged as information, for tools that report progress there.
    /// </summary>
    public static async Task<ExternalProcessResult> RunAsync(string program, IReadOnlyList<string> arguments, bool errorOutputIsError = true, CancellationToken cancellationToken = default)
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
            List<string> errorLines = [];
            var standardOutput = ReadLinesAsync(process.StandardOutput, LogService.Info, null);
            var standardError = ReadLinesAsync(process.StandardError, errorOutputIsError ? LogService.Error : LogService.Info, errorLines);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // the process ended on its own right before the kill
                }
                // Wait for the readers, so a read that fails when the process goes away is observed.
                try { await Task.WhenAll(standardOutput, standardError).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); }
                catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException)
                {
                    // a grandchild may still hold the pipes; a cancelled run ignores their text
                }
                throw;
            }
            await Task.WhenAll(standardOutput, standardError);
            return new(process.ExitCode, string.Join(Environment.NewLine, errorLines));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogService.Error("Unable to start program: " + exception.Message);
            return new(-1);
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> log, List<string>? collected)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) continue;
            log(line);
            collected?.Add(line);
        }
    }
}
