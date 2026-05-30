using System.Text;

namespace G3MToolCLI.Services;

internal sealed class FileLogWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    public FileLogWriter(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        _writer = new StreamWriter(path, append: false, Encoding.UTF8)
        {
            AutoFlush = true
        };
    }

    public void WriteLine(string message)
    {
        lock (_lock)
        {
            _writer.Write('[');
            _writer.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            _writer.Write("] ");
            _writer.WriteLine(message);
        }
    }

    public void Dispose()
    {
        _writer.Dispose();
    }
}
