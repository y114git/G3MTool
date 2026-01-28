namespace G3MToolCLI.Services;

public static class LogService
{
    private static bool _verbose = false;
    private static readonly object _lock = new();
    private static int _lastProgressPercent = -1;
    private static string _currentOperation = "";

    public static bool Verbose
    {
        get => _verbose;
        set => _verbose = value;
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
        if (_verbose)
        {
            Console.WriteLine($"[Warning] {message}");
        }
    }

    public static void Progress(int current, int total, string? item = null)
    {
        if (total <= 0) return;

        int percent = (int)((current * 100.0) / total);
        
        lock (_lock)
        {
            // Only update if percent changed or it's 100%
            if (percent != _lastProgressPercent || percent == 100)
            {
                _lastProgressPercent = percent;
                
                // Clear line and write progress (pad with spaces to clear previous content)
                Console.Write($"\r{_currentOperation}: {percent}%          ");
                
                if (percent == 100)
                {
                    Console.WriteLine("\r" + _currentOperation + ": 100% Done          ");
                }
            }
        }
    }

    public static void ProgressComplete()
    {
        lock (_lock)
        {
            if (_lastProgressPercent >= 0 && _lastProgressPercent < 100)
            {
                Console.WriteLine($"\r{_currentOperation}: 100% Done");
            }
            _lastProgressPercent = -1;
        }
    }
}
