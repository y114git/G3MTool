using System;

namespace G3MToolCLI.Utils;

public static class PlatformUtil
{
    public static string GetExecutableDirectory()
    {
        string? processPath = Environment.ProcessPath;
        return processPath is null || Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory;
    }
}
