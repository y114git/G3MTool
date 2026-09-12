using System;

namespace G3MToolCLI.Models.Scripting;

public class ScriptException : Exception
{
    public ScriptException(string message)
        : base(message)
    {
    }

    public ScriptException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
