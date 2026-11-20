using G3MToolGUI.Services;

namespace G3MToolGUI;

/// <summary>What the window shows for one operation: its texts, which fields it uses and which options apply.</summary>
internal sealed record OperationView
{
    public required ModdingOperation Operation { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Action { get; init; }
    public required string OriginalLabel { get; init; }
    public required string PrimaryLabel { get; init; }
    public string AdditionalLabel { get; init; } = "Additional inputs";
    public required string OutputLabel { get; init; }
    public bool HasOriginal { get; init; }
    public bool HasPrimary { get; init; }
    public bool HasAdditional { get; init; }
    public bool HasOutput { get; init; }
    public bool OutputIsDirectory { get; init; }
    public bool SupportsMergeOptions { get; init; }
    public bool SupportsSequentialMerge { get; init; }
    public bool SupportsXDeltaFallback { get; init; }
    public bool SupportsReport { get; init; }
    public bool SupportsContinueOnError { get; init; }
    public bool SupportsXDeltaOutput { get; init; }

    public static IReadOnlyList<OperationView> All { get; } =
    [
        new() { Operation = ModdingOperation.CreatePatch, Title = "Create a patch", Description = "Turn a modified data file or supported input into a portable G3M patch.", Action = "Create patch", OriginalLabel = "Original data", PrimaryLabel = "Modified input", OutputLabel = "Output patch", HasOriginal = true, HasPrimary = true, HasOutput = true, SupportsXDeltaFallback = true },
        new() { Operation = ModdingOperation.ApplyPatch, Title = "Apply a patch", Description = "Apply a G3M patch, XDelta patch, or data-file input to an original data file.", Action = "Apply patch", OriginalLabel = "Original data", PrimaryLabel = "Patch input", OutputLabel = "Patched data", HasOriginal = true, HasPrimary = true, HasOutput = true, SupportsXDeltaFallback = true },
        new() { Operation = ModdingOperation.ValidatePatch, Title = "Validate a patch", Description = "Check patch structure and, optionally, compatibility with an original data file.", Action = "Validate patch", OriginalLabel = "Original data (optional)", PrimaryLabel = "Patch", OutputLabel = "Output", HasOriginal = true, HasPrimary = true },
        new() { Operation = ModdingOperation.MergePatches, Title = "Merge patches", Description = "Combine two or more patch inputs in priority order, from lowest to highest.", Action = "Merge patches", OriginalLabel = "Original data", PrimaryLabel = "Data output (optional)", AdditionalLabel = "Patches, low → high priority", OutputLabel = "Merged patch (optional)", HasOriginal = true, HasPrimary = true, HasAdditional = true, HasOutput = true, SupportsMergeOptions = true, SupportsSequentialMerge = true, SupportsReport = true },
        new() { Operation = ModdingOperation.Compare, Title = "Compare files", Description = "Create a Markdown report showing resource-level differences between two files.", Action = "Create report", OriginalLabel = "First file", PrimaryLabel = "Second file", OutputLabel = "Markdown report", HasOriginal = true, HasPrimary = true, HasOutput = true, SupportsReport = true },
        new() { Operation = ModdingOperation.Inspect, Title = "Inspect a file", Description = "Read metadata and resource counts from a data file or G3M patch.", Action = "Inspect file", OriginalLabel = "Original data", PrimaryLabel = "Data file or patch", OutputLabel = "Output", HasPrimary = true },
        new() { Operation = ModdingOperation.BatchCreate, Title = "Create patch batch", Description = "Create a patch for every input against one original data file.", Action = "Create batch", OriginalLabel = "Original data", PrimaryLabel = "Input", AdditionalLabel = "Modified inputs", OutputLabel = "Output directory", HasOriginal = true, HasAdditional = true, HasOutput = true, OutputIsDirectory = true, SupportsXDeltaFallback = true, SupportsContinueOnError = true, SupportsXDeltaOutput = true },
        new() { Operation = ModdingOperation.BatchApply, Title = "Apply patch batch", Description = "Apply every patch independently to the same original data file.", Action = "Apply batch", OriginalLabel = "Original data", PrimaryLabel = "Input", AdditionalLabel = "Patch inputs", OutputLabel = "Output directory", HasOriginal = true, HasAdditional = true, HasOutput = true, OutputIsDirectory = true, SupportsXDeltaFallback = true, SupportsContinueOnError = true },
        new() { Operation = ModdingOperation.BatchMerge, Title = "Merge patch batches", Description = "Run independent merges. Enter one comma-separated, low-to-high patch set per line.", Action = "Merge batches", OriginalLabel = "Original data", PrimaryLabel = "Data output directory", AdditionalLabel = "Merge sets", OutputLabel = "Patch output directory (optional)", HasOriginal = true, HasPrimary = true, HasAdditional = true, HasOutput = true, OutputIsDirectory = true, SupportsMergeOptions = true, SupportsReport = true, SupportsContinueOnError = true },
        new() { Operation = ModdingOperation.CreateXDelta, Title = "Create XDelta patch", Description = "Create a compact binary XDelta patch between two files.", Action = "Create XDelta", OriginalLabel = "Original file", PrimaryLabel = "Modified file", OutputLabel = "XDelta output", HasOriginal = true, HasPrimary = true, HasOutput = true },
        new() { Operation = ModdingOperation.ApplyXDelta, Title = "Apply XDelta patch", Description = "Apply an XDelta or VCDIFF patch to an original file.", Action = "Apply XDelta", OriginalLabel = "Original file", PrimaryLabel = "XDelta patch", OutputLabel = "Output file", HasOriginal = true, HasPrimary = true, HasOutput = true },
        new() { Operation = ModdingOperation.RunXDelta, Title = "Run XDelta arguments", Description = "Run an advanced XDelta invocation using one argument per line.", Action = "Run XDelta", OriginalLabel = "Original data", PrimaryLabel = "Input", AdditionalLabel = "XDelta arguments", OutputLabel = "Output", HasAdditional = true },
        new() { Operation = ModdingOperation.RunScript, Title = "Run a script", Description = "Run a C# script, optionally against a data file, and save the result separately.", Action = "Run script", OriginalLabel = "Data file (optional)", PrimaryLabel = "C# script", AdditionalLabel = "Script arguments", OutputLabel = "Output data", HasOriginal = true, HasPrimary = true, HasAdditional = true, HasOutput = true },
        new() { Operation = ModdingOperation.RunProgram, Title = "Run a program", Description = "Run an external program with one argument per line and capture its output in Activity.", Action = "Run program", OriginalLabel = "Original data", PrimaryLabel = "Program", AdditionalLabel = "Program arguments", OutputLabel = "Output", HasPrimary = true, HasAdditional = true }
    ];

    public override string ToString() => Title;
}
