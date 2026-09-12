using System;
using System.IO;
using System.Threading;

namespace G3MToolCLI.Services.Logging;

public static class LogService
{
    private static readonly Lock _lock = new Lock();

    private static readonly Lock _fileLock = new Lock();

    private static int _lastProgressPercent = -1;

    private static string _currentOperation = "";

    private static FileLogWriter? _fileWriter;

    public static bool Verbose { get; set; }

    public static bool Suppress { get; set; }

    public static bool FileLoggingEnabled => _fileWriter != null;

    public static void SetOperation(string operation)
    {
        _currentOperation = operation;
        _lastProgressPercent = -1;
    }

    public static void SetFileLogging(string? path)
    {
        using (_fileLock.EnterScope())
        {
            _fileWriter?.Dispose();
            _fileWriter = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            try
            {
                _fileWriter = new FileLogWriter(path);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine("Warning: Could not open log file '" + path + "': " + ex.Message);
            }
            catch (UnauthorizedAccessException ex2)
            {
                Console.Error.WriteLine("Warning: Could not open log file '" + path + "': " + ex2.Message);
            }
        }
    }

    public static void Shutdown()
    {
        using (_fileLock.EnterScope())
        {
            _fileWriter?.Dispose();
            _fileWriter = null;
        }
    }

    public static void Log(string message)
    {
        WriteToFile(message);
        if (Verbose && !Suppress)
        {
            Console.WriteLine(message);
        }
    }

    public static void Info(string message)
    {
        WriteToFile(message);
        if (!Suppress)
        {
            Console.WriteLine(message);
        }
    }

    public static void Error(string message)
    {
        WriteToFile("Error: " + message);
        Console.Error.WriteLine("Error: " + message);
    }

    public static void Warning(string message)
    {
        WriteToFile("[Warning] " + message);
        if (!Suppress)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[Warning] " + message);
            Console.ResetColor();
        }
    }

    public static void Progress(int current, int total)
    {
        if (total <= 0 || Suppress)
        {
            return;
        }
        int percent = (int)((double)current * 100.0 / (double)total);
        using (_lock.EnterScope())
        {
            if (percent > _lastProgressPercent)
            {
                _lastProgressPercent = percent;
                Console.Write($"\r{_currentOperation}: {percent}%          ");
                Console.Out.Flush();
            }
        }
    }

    public static void ProgressRange(int current, int total, int rangeStart, int rangeEnd)
    {
        if (total > 0 && !Suppress)
        {
            int clampedStart = Math.Clamp(rangeStart, 0, 100);
            int span = Math.Clamp(rangeEnd, clampedStart, 100) - clampedStart;
            Progress(clampedStart + (int)Math.Round((double)(current * span) / (double)total), 100);
        }
    }

    public static void ProgressComplete()
    {
        if (Suppress)
        {
            return;
        }
        using (_lock.EnterScope())
        {
            if (_lastProgressPercent >= 0)
            {
                Console.WriteLine();
                Console.Out.Flush();
            }
            _lastProgressPercent = -1;
        }
    }

    private static void WriteToFile(string message)
    {
        using (_fileLock.EnterScope())
        {
            _fileWriter?.WriteLine(message);
        }
    }
}
