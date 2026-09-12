using System.Diagnostics;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using G3MLib.Modding.Logging;
using G3MToolGUI.Services;

namespace G3MToolGUI;

public partial class MainWindow : Window
{
    private static readonly IReadOnlyDictionary<ModdingOperation, ModdingOperation> BatchOperations = new Dictionary<ModdingOperation, ModdingOperation>
    {
        [ModdingOperation.CreatePatch] = ModdingOperation.BatchCreate,
        [ModdingOperation.ApplyPatch] = ModdingOperation.BatchApply,
        [ModdingOperation.MergePatches] = ModdingOperation.BatchMerge
    };

    private readonly ModdingOperationService _operations;
    private readonly StringBuilder _log = new();
    private readonly Queue<LogLine> _logLines = new();
    private bool _isRunning;
    private bool _closeAfterOperation;
    private bool _logsExpanded;
    private ModdingOperation? _configuredOperation;

    private sealed record LogLine(ModdingLogLevel Level, string Text);

    public MainWindow()
    {
        InitializeComponent();
        _operations = new(new ScriptInteraction
        {
            Question = message => Dispatcher.UIThread.InvokeAsync(async () => await ShowScriptInputAsync("Script question", message, "", false, true) is not null),
            Input = (title, label, value, multiline) => Dispatcher.UIThread.InvokeAsync(() => ShowScriptInputAsync(title, label, value, multiline)),
            OpenFile = (filter, path) => Dispatcher.UIThread.InvokeAsync(() => PickScriptFileAsync(filter, path, false)),
            SaveFile = (filter, path) => Dispatcher.UIThread.InvokeAsync(() => PickScriptFileAsync(filter, path, true)),
            ChooseDirectory = () => Dispatcher.UIThread.InvokeAsync(() => PickFolderAsync("Choose script directory"))
        });
        string version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "unknown";
        BuildVersionText.Text = $"G3MTool (GUI) - {version}";
        OperationPicker.ItemsSource = OperationView.All.Where(view => !BatchOperations.Values.Contains(view.Operation)).ToArray();
        OperationPicker.SelectedIndex = 0;
        _operations.LogReceived += AddLog;
        _operations.ProgressChanged += UpdateProgress;
        Closed += (_, _) => _operations.Dispose();
        Closing += (_, args) =>
        {
            if (!_isRunning) return;
            args.Cancel = true;
            _closeAfterOperation = true;
            ProgressText.Text = "Waiting for the current operation before closing…";
        };
        SizeChanged += (_, _) => UpdateLogPanelHeight();
    }

    private OperationView SelectedBaseView => (OperationPicker.SelectedItem as OperationView) ?? OperationView.All[0];
    private OperationView SelectedView => BatchModeCheck.IsChecked == true && BatchOperations.TryGetValue(SelectedBaseView.Operation, out ModdingOperation batchOperation)
        ? OperationView.All.First(view => view.Operation == batchOperation)
        : SelectedBaseView;

    private void OperationChanged(object? sender, SelectionChangedEventArgs e) => ConfigureOperation();
    private void BatchModeChanged(object? sender, RoutedEventArgs e) => ConfigureOperation();

    private void ConfigureOperation()
    {
        bool supportsBatch = BatchOperations.ContainsKey(SelectedBaseView.Operation);
        BatchModeCheck.IsVisible = supportsBatch;
        if (!supportsBatch && BatchModeCheck.IsChecked == true)
        {
            BatchModeCheck.IsChecked = false;
        }

        OperationView view = SelectedView;
        if (_configuredOperation != view.Operation)
        {
            ResetOperationFields();
            _configuredOperation = view.Operation;
        }
        PageTitle.Text = SelectedBaseView.Title;
        PageDescription.Text = SelectedBaseView.Description;
        OriginalLabel.Text = view.OriginalLabel;
        PrimaryLabel.Text = view.PrimaryLabel;
        AdditionalLabel.Text = view.AdditionalLabel;
        OutputLabel.Text = view.OutputLabel;
        RunButton.Content = view.Action;
        SetVisible(view.HasOriginal, OriginalLabel, OriginalPathBox, OriginalBrowseButton);
        SetVisible(view.HasPrimary, PrimaryLabel, PrimaryPathBox, PrimaryBrowseButton);
        SetVisible(view.HasAdditional, AdditionalLabel, AdditionalPathsBox, AdditionalBrowseButton);
        SetVisible(view.HasOutput, OutputLabel, OutputPathBox, OutputBrowseButton);
        CodeMergeCheck.IsVisible = view.SupportsMergeOptions;
        PropertiesMergeCheck.IsVisible = view.SupportsMergeOptions;
        SequentialMergeCheck.IsVisible = view.SupportsSequentialMerge;
        XDeltaFallbackCheck.IsVisible = view.SupportsXDeltaFallback;
        CreateXDeltaCheck.IsVisible = view.SupportsXDeltaOutput;
        FullReportCheck.IsVisible = view.SupportsReport;
        ContinueOnErrorCheck.IsVisible = view.SupportsContinueOnError;
        InputOptionsPanel.IsVisible = supportsBatch || view.SupportsXDeltaFallback || view.SupportsXDeltaOutput || view.SupportsContinueOnError;
        MergeOptionsPanel.IsVisible = view.SupportsMergeOptions || view.SupportsSequentialMerge || view.SupportsReport;
        OptionsLabel.IsVisible = InputOptionsPanel.IsVisible || MergeOptionsPanel.IsVisible;
        OperationCard.Opacity = 0.86;
        Dispatcher.UIThread.Post(() =>
        {
            OperationCard.Opacity = 1;
            if (!_logsExpanded) ContentScroll.Offset = default;
        });
    }

    private static void SetVisible(bool visible, params Control[] controls)
    {
        foreach (Control control in controls) control.IsVisible = visible;
    }

    private async void BrowseOriginal(object? sender, RoutedEventArgs e)
    {
        OriginalPathBox.Text = await PickFileAsync("Choose original data file") ?? OriginalPathBox.Text;
    }

    private async void BrowsePrimary(object? sender, RoutedEventArgs e)
    {
        ModdingOperation operation = SelectedView.Operation;
        if (operation == ModdingOperation.BatchMerge)
        {
            PrimaryPathBox.Text = await PickFolderAsync("Choose data output directory") ?? PrimaryPathBox.Text;
            return;
        }
        if (operation == ModdingOperation.MergePatches)
        {
            string? extension = Path.GetExtension(OriginalPathBox.Text);
            PrimaryPathBox.Text = await SavePathAsync("Choose merged data output", "merged" + (string.IsNullOrEmpty(extension) ? ".win" : extension)) ?? PrimaryPathBox.Text;
            return;
        }

        PrimaryPathBox.Text = await PickFileAsync("Choose input file") ?? PrimaryPathBox.Text;
    }

    private async void BrowseAdditional(object? sender, RoutedEventArgs e)
    {
        if (SelectedView.Operation == ModdingOperation.RunXDelta)
        {
            AddLog(new ModdingLogMessage { Level = ModdingLogLevel.Information, Message = "Enter one XDelta argument per line." });
            return;
        }

        IReadOnlyList<string> paths = await PickFilesAsync("Choose input files");
        if (paths.Count == 0) return;
        string prefix = string.IsNullOrWhiteSpace(AdditionalPathsBox.Text) ? string.Empty : AdditionalPathsBox.Text.TrimEnd() + Environment.NewLine;
        if (SelectedView.Operation == ModdingOperation.BatchMerge && paths.Any(path => path.Contains(',') || path.Contains('\n') || path.Contains('\r')))
        {
            AddLog(new ModdingLogMessage { Level = ModdingLogLevel.Error, Message = "Merge-set paths cannot contain commas or line breaks." });
            return;
        }
        AdditionalPathsBox.Text = prefix + string.Join(SelectedView.Operation == ModdingOperation.BatchMerge ? "," : Environment.NewLine, paths);
    }

    private async void BrowseOutput(object? sender, RoutedEventArgs e)
    {
        if (SelectedView.OutputIsDirectory)
        {
            OutputPathBox.Text = await PickFolderAsync("Choose output directory") ?? OutputPathBox.Text;
            return;
        }

        string extension = SelectedView.Operation switch
        {
            ModdingOperation.Compare => "md",
            ModdingOperation.CreateXDelta => "xdelta",
            ModdingOperation.ApplyPatch or ModdingOperation.ApplyXDelta or ModdingOperation.RunScript => "win",
            _ => "g3mpatch"
        };
        OutputPathBox.Text = await SavePathAsync("Choose output path", "output." + extension) ?? OutputPathBox.Text;
    }

    private async void BrowseCache(object? sender, RoutedEventArgs e)
    {
        CachePathBox.Text = await PickFolderAsync("Choose cache directory") ?? CachePathBox.Text;
    }

    private async void RunOperation(object? sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        _isRunning = true;
        RunButton.IsEnabled = false;
        OperationCard.IsEnabled = false;
        OperationProgress.IsIndeterminate = true;
        ProgressText.Text = "Starting…";
        try
        {
            ModdingOperationRequest request = CreateRequest();
            ModdingOperationResult result = await Task.Run(() => _operations.RunAsync(request));
            ProgressText.Text = result.Summary;
            ProgressPercentText.Text = result.Success ? "100%" : "—";
            OperationProgress.Value = result.Success ? 100 : 0;
            AddLog(new ModdingLogMessage { Level = result.Success ? ModdingLogLevel.Information : ModdingLogLevel.Error, Message = result.Summary });
            foreach (string output in result.Outputs) AddLog(new ModdingLogMessage { Level = ModdingLogLevel.Information, Message = "Output: " + output });
        }
        finally
        {
            _isRunning = false;
            RunButton.IsEnabled = true;
            OperationCard.IsEnabled = true;
            OperationProgress.IsIndeterminate = false;
            if (_closeAfterOperation) Close();
        }
    }

    private void ClearForm(object? sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        ResetOperationFields();
        CachePathBox.Text = string.Empty;
        BatchModeCheck.IsChecked = false;
        ConfigureOperation();
    }

    private ModdingOperationRequest CreateRequest()
    {
        OperationView view = SelectedView;
        return new()
        {
            Operation = view.Operation,
            OriginalPath = view.HasOriginal ? OriginalPathBox.Text?.Trim() ?? string.Empty : string.Empty,
            PrimaryPath = view.HasPrimary ? PrimaryPathBox.Text?.Trim() ?? string.Empty : string.Empty,
            InputPaths = view.HasAdditional ? ReadLines(AdditionalPathsBox.Text) : [],
            OutputPath = view.HasOutput ? OutputPathBox.Text?.Trim() ?? string.Empty : string.Empty,
            CacheDirectory = CachePathBox.Text?.Trim() ?? string.Empty,
            UseCodeMerge = view.SupportsMergeOptions && CodeMergeCheck.IsChecked == true,
            UsePropertyMerge = view.SupportsMergeOptions && PropertiesMergeCheck.IsChecked == true,
            UseSequentialMerge = view.SupportsSequentialMerge && SequentialMergeCheck.IsChecked == true,
            IncludeXDeltaFallback = view.SupportsXDeltaFallback && XDeltaFallbackCheck.IsChecked == true,
            CreateXDelta = view.SupportsXDeltaOutput && CreateXDeltaCheck.IsChecked == true,
            FullReport = view.SupportsReport && FullReportCheck.IsChecked == true,
            ContinueOnError = view.SupportsContinueOnError && ContinueOnErrorCheck.IsChecked == true
        };
    }

    private void ResetOperationFields()
    {
        OriginalPathBox.Text = string.Empty;
        PrimaryPathBox.Text = string.Empty;
        AdditionalPathsBox.Text = string.Empty;
        OutputPathBox.Text = string.Empty;
        CodeMergeCheck.IsChecked = false;
        PropertiesMergeCheck.IsChecked = false;
        SequentialMergeCheck.IsChecked = false;
        XDeltaFallbackCheck.IsChecked = false;
        CreateXDeltaCheck.IsChecked = false;
        FullReportCheck.IsChecked = false;
        ContinueOnErrorCheck.IsChecked = false;
    }

    private void OpenBoosty(object? sender, RoutedEventArgs e) => OpenSocialLink("https://boosty.to/y114");
    private void OpenTelegram(object? sender, RoutedEventArgs e) => OpenSocialLink("https://t.me/y_maintg");
    private void OpenDiscord(object? sender, RoutedEventArgs e) => OpenSocialLink("https://discord.gg/2MFdvFfD9a");
    private void OpenWebsite(object? sender, RoutedEventArgs e) => OpenSocialLink("https://y114.space");

    private void OpenSocialLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            AddLog(new ModdingLogMessage { Level = ModdingLogLevel.Error, Message = "Could not open link: " + exception.Message });
        }
    }

    private void ClearLog(object? sender, RoutedEventArgs e)
    {
        _log.Clear();
        _logLines.Clear();
        LogBox.Inlines?.Clear();
    }

    private void ToggleLogs(object? sender, RoutedEventArgs e)
    {
        _logsExpanded = !_logsExpanded;
        TopBar.IsVisible = !_logsExpanded;
        PageHeader.IsVisible = !_logsExpanded;
        OperationCard.IsVisible = !_logsExpanded;
        ExpandLogsButton.Content = _logsExpanded ? "Collapse" : "Expand";
        UpdateLogPanelHeight();
    }

    private async void SaveLog(object? sender, RoutedEventArgs e)
    {
        string? path = await SavePathAsync("Save activity log", "g3mtool.log");
        if (path is not null) await File.WriteAllTextAsync(path, _log.ToString());
    }

    private void AddLog(ModdingLogMessage message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddLog(message));
            return;
        }

        string text = $"[{DateTime.Now:HH:mm:ss}] {message.Level}: {message.Message}{Environment.NewLine}";
        LogLine line = new(message.Level, text);
        _log.Append(text);
        _logLines.Enqueue(line);
        bool trimmed = false;
        while (_log.Length > 1_000_000 && _logLines.TryDequeue(out LogLine? removed))
        {
            _log.Remove(0, removed.Text.Length);
            trimmed = true;
        }

        if (trimmed)
        {
            RenderLog();
        }
        else
        {
            LogBox.Inlines?.Add(CreateLogRun(line));
        }

        Dispatcher.UIThread.Post(() => LogScroll.Offset = new Vector(0, LogScroll.Extent.Height));
    }

    private void UpdateLogPanelHeight()
    {
        LogPanel.Height = _logsExpanded ? Math.Max(220, (Bounds.Height - 80) * .85) : 110;
    }

    private void RenderLog()
    {
        LogBox.Inlines?.Clear();
        foreach (LogLine line in _logLines) LogBox.Inlines?.Add(CreateLogRun(line));
    }

    private static Run CreateLogRun(LogLine line) => new(line.Text)
    {
        Foreground = line.Level switch
        {
            ModdingLogLevel.Information => new SolidColorBrush(Color.Parse("#6DE985")),
            ModdingLogLevel.Warning => new SolidColorBrush(Color.Parse("#F6C453")),
            ModdingLogLevel.Error => new SolidColorBrush(Color.Parse("#FF7474")),
            _ => new SolidColorBrush(Color.Parse("#9CA3AF"))
        }
    };

    private void UpdateProgress(ModdingProgress progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (progress.IsComplete) return;
            OperationProgress.IsIndeterminate = false;
            int percent = progress.Total == 0 ? 0 : (int)Math.Round(progress.Current * 100d / progress.Total);
            OperationProgress.Value = percent;
            ProgressPercentText.Text = percent + "%";
            ProgressText.Text = string.IsNullOrWhiteSpace(progress.Operation) ? "Working…" : progress.Operation;
        });
    }

    private Task<string?> ShowScriptInputAsync(string title, string label, string value, bool multiline, bool question = false)
    {
        var dialog = new Window
        {
            Title = title, Width = 480, MinWidth = 320, MaxHeight = 650,
            SizeToContent = SizeToContent.Height, CanResize = multiline,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        var input = new TextBox { Name = "ScriptInput", Text = value, AcceptsReturn = multiline, MinHeight = multiline ? 140 : 0 };
        var accept = new Button { Name = "ScriptAccept", Content = question ? "Yes" : "OK", IsDefault = true };
        var cancel = new Button { Name = "ScriptCancel", Content = question ? "No" : "Cancel", IsCancel = true };
        accept.Click += (_, _) => dialog.Close(question ? "yes" : input.Text ?? "");
        cancel.Click += (_, _) => dialog.Close(null);
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(new ScrollViewer { MaxHeight = 300, Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap } });
        if (!question) panel.Children.Add(input);
        panel.Children.Add(new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Children = { cancel, accept }
        });
        dialog.Content = panel;
        dialog.Opened += (_, _) => { if (!question) input.Focus(); };
        return dialog.ShowDialog<string?>(this);
    }

    private async Task<string?> PickScriptFileAsync(string filter, string suggestedPath, bool save)
    {
        string[] parts = filter.Split('|');
        var types = new List<FilePickerFileType>();
        for (int i = 0; i + 1 < parts.Length; i += 2)
            types.Add(new FilePickerFileType(parts[i]) { Patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) });
        if (types.Count == 0) types.Add(FilePickerFileTypes.All);
        IStorageFolder? folder = null;
        if (!string.IsNullOrWhiteSpace(suggestedPath))
        {
            string? directory = Directory.Exists(suggestedPath) ? suggestedPath : Path.GetDirectoryName(Path.GetFullPath(suggestedPath));
            if (directory is not null) folder = await StorageProvider.TryGetFolderFromPathAsync(directory);
        }
        if (save)
        {
            IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save script file", SuggestedFileName = Path.GetFileName(suggestedPath), SuggestedStartLocation = folder, FileTypeChoices = types
            });
            return file?.TryGetLocalPath();
        }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open script file", AllowMultiple = false, SuggestedStartLocation = folder, FileTypeFilter = types
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickFileAsync(string title)
    {
        IReadOnlyList<string> files = await PickFilesAsync(title, false);
        return files.FirstOrDefault();
    }

    private async Task<IReadOnlyList<string>> PickFilesAsync(string title, bool allowMultiple = true)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        return files.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> SavePathAsync(string title, string suggestedFileName)
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedFileName });
        return file?.TryGetLocalPath();
    }

    private static IReadOnlyList<string> ReadLines(string? value) => (value ?? string.Empty).Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record OperationView(
        ModdingOperation Operation, string Title, string Description, string Action, string Hint,
        string OriginalLabel, string PrimaryLabel, string AdditionalLabel, string OutputLabel,
        bool HasOriginal, bool HasPrimary, bool HasAdditional, bool HasOutput, bool OutputIsDirectory,
        bool SupportsMergeOptions = false, bool SupportsSequentialMerge = false, bool SupportsXDeltaFallback = false,
        bool SupportsReport = false, bool SupportsContinueOnError = false, bool SupportsXDeltaOutput = false)
    {
        public static IReadOnlyList<OperationView> All { get; } =
        [
            new(ModdingOperation.CreatePatch, "Create a patch", "Turn a modified data file or supported input into a portable G3M patch.", "Create patch", "A G3M patch stores resource-level changes and can include an optional XDelta fallback.", "Original data", "Modified input", "Additional inputs", "Output patch", true, true, false, true, false, SupportsXDeltaFallback: true),
            new(ModdingOperation.ApplyPatch, "Apply a patch", "Apply a G3M patch, XDelta patch, or data-file input to an original data file.", "Apply patch", "The output is written separately; the original data file is never changed.", "Original data", "Patch input", "Additional inputs", "Patched data", true, true, false, true, false, SupportsXDeltaFallback: true),
            new(ModdingOperation.ValidatePatch, "Validate a patch", "Check patch structure and, optionally, compatibility with an original data file.", "Validate patch", "Choose an original data file only when compatibility should be checked.", "Original data (optional)", "Patch", "Additional inputs", "Output", true, true, false, false, false),
            new(ModdingOperation.MergePatches, "Merge patches", "Combine two or more patch inputs in priority order, from lowest to highest.", "Merge patches", "Use one extra input per line. Detailed reports are written beside the merged output.", "Original data", "Data output (optional)", "Patches, low → high priority", "Merged patch (optional)", true, true, true, true, false, true, true, false, true),
            new(ModdingOperation.Compare, "Compare files", "Create a Markdown report showing resource-level differences between two files.", "Create report", "Full reports include detailed text, code, and JSON differences.", "First file", "Second file", "Additional inputs", "Markdown report", true, true, false, true, false, SupportsReport: true),
            new(ModdingOperation.Inspect, "Inspect a file", "Read metadata and resource counts from a data file or G3M patch.", "Inspect file", "The result is shown in Activity; use the cache folder to reuse analysis later.", "Original data", "Data file or patch", "Additional inputs", "Output", false, true, false, false, false),
            new(ModdingOperation.BatchCreate, "Create patch batch", "Create a patch for every input against one original data file.", "Create batch", "Choose all modified inputs; each result is written into the output folder.", "Original data", "Input", "Modified inputs", "Output directory", true, false, true, true, true, SupportsXDeltaFallback: true, SupportsContinueOnError: true, SupportsXDeltaOutput: true),
            new(ModdingOperation.BatchApply, "Apply patch batch", "Apply every patch independently to the same original data file.", "Apply batch", "Each result is written into the output folder without changing the original.", "Original data", "Input", "Patch inputs", "Output directory", true, false, true, true, true, SupportsXDeltaFallback: true, SupportsContinueOnError: true),
            new(ModdingOperation.BatchMerge, "Merge patch batches", "Run independent merges. Enter one comma-separated, low-to-high patch set per line.", "Merge batches", "Choose the data output directory. The patch output directory is optional.", "Original data", "Data output directory", "Merge sets", "Patch output directory (optional)", true, true, true, true, true, true, false, false, true, true),
            new(ModdingOperation.CreateXDelta, "Create XDelta patch", "Create a compact binary XDelta patch between two files.", "Create XDelta", "Use this when resource-aware G3M patching is not appropriate.", "Original file", "Modified file", "Additional inputs", "XDelta output", true, true, false, true, false),
            new(ModdingOperation.ApplyXDelta, "Apply XDelta patch", "Apply an XDelta or VCDIFF patch to an original file.", "Apply XDelta", "The original is read-only; choose a separate output file.", "Original file", "XDelta patch", "Additional inputs", "Output file", true, true, false, true, false),
            new(ModdingOperation.RunXDelta, "Run XDelta arguments", "Run an advanced XDelta invocation using one argument per line.", "Run XDelta", "Arguments are passed exactly in the order shown. This is intended for advanced use.", "Original data", "Input", "XDelta arguments", "Output", false, false, true, false, false),
            new(ModdingOperation.RunScript, "Run a script", "Run a C# script, optionally against a data file, and save the result separately.", "Run script", "When a data file is chosen, an output data path is required. Extra lines are passed as script arguments.", "Data file (optional)", "C# script", "Script arguments", "Output data", true, true, true, true, false),
            new(ModdingOperation.RunProgram, "Run a program", "Run an external program with one argument per line and capture its output in Activity.", "Run program", "Arguments are passed without shell interpretation.", "Original data", "Program", "Program arguments", "Output", false, true, true, false, false)
        ];

        public override string ToString() => Title;
    }
}
