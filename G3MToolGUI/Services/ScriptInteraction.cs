namespace G3MToolGUI.Services;

public sealed class ScriptInteraction
{
    public required Func<string, Task<bool>> Question { get; init; }
    public required Func<string, string, string, bool, Task<string?>> Input { get; init; }
    public required Func<string, string, Task<string?>> OpenFile { get; init; }
    public required Func<string, string, Task<string?>> SaveFile { get; init; }
    public required Func<Task<string?>> ChooseDirectory { get; init; }
}
