using System.Runtime.CompilerServices;

namespace G3MToolCLI.Services;

/// <summary>
/// Interpolated string handler that skips string construction when verbose logging is disabled.
/// </summary>
[InterpolatedStringHandler]
public ref struct LogInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler _inner;
    private readonly bool _enabled;

    public LogInterpolatedStringHandler(int literalLength, int formattedCount, out bool shouldAppend)
    {
        _enabled = LogService.Verbose && !LogService.Suppress;
        shouldAppend = _enabled;
        _inner = _enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    public void AppendLiteral(string value)
    {
        if (_enabled) _inner.AppendLiteral(value);
    }

    public void AppendFormatted<T>(T value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    public void AppendFormatted<T>(T value, string? format)
    {
        if (_enabled) _inner.AppendFormatted(value, format);
    }

    public void AppendFormatted<T>(T value, int alignment)
    {
        if (_enabled) _inner.AppendFormatted(value, alignment);
    }

    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        if (_enabled) _inner.AppendFormatted(value, alignment, format);
    }

    public void AppendFormatted(ReadOnlySpan<char> value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    public override string ToString() => _enabled ? _inner.ToString() : "";
}

public static class LogService
{
    private static bool _verbose = false;
    private static bool _suppress = false;
    private static readonly Lock _lock = new();
    private static int _lastProgressPercent = -1;
    private static string _currentOperation = "";

    public static bool Verbose
    {
        get => _verbose;
        set => _verbose = value;
    }

    public static bool Suppress
    {
        get => _suppress;
        set => _suppress = value;
    }

    public static void SetOperation(string operation)
    {
        _currentOperation = operation;
        _lastProgressPercent = -1;
    }

    public static void Log(string message)
    {
        if (_verbose)
        {
            Console.WriteLine(message);
        }
    }

    public static void Log(ref LogInterpolatedStringHandler handler)
    {
        if (_verbose && !_suppress)
        {
            Console.WriteLine(handler.ToString());
        }
    }

    public static void Info(string message)
    {
        Console.WriteLine(message);
    }

    public static void Error(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
    }

    public static void Warning(string message)
    {
        if (_suppress) return;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[Warning] {message}");
        Console.ResetColor();
    }

    public static void Progress(int current, int total)
    {
        if (total <= 0 || _suppress) return;

        int percent = (int)((current * 100.0) / total);

        lock (_lock)
        {
            // Never go backwards - clamp to max seen so far
            if (percent <= _lastProgressPercent) return;
            _lastProgressPercent = percent;
            Console.Write($"\r{_currentOperation}: {percent}%          ");
        }
    }

    public static void ProgressRange(int current, int total, int rangeStart, int rangeEnd)
    {
        if (total <= 0 || _suppress) return;
        int clampedStart = Math.Clamp(rangeStart, 0, 100);
        int clampedEnd = Math.Clamp(rangeEnd, clampedStart, 100);
        int span = clampedEnd - clampedStart;
        int percent = clampedStart + (int)Math.Round((current * span) / (double)total);
        Progress(percent, 100);
    }

    public static void ProgressComplete()
    {
        if (_suppress) return;
        lock (_lock)
        {
            if (_lastProgressPercent >= 0)
            {
                // Move to next line after progress, no "Done" text
                Console.WriteLine();
            }
            _lastProgressPercent = -1;
        }
    }
}
