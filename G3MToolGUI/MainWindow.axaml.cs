using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using G3MLib.Modding.Logging;
using G3MToolGUI.Services;

namespace G3MToolGUI;

public sealed partial class MainWindow : Window, IDisposable
{
    private static readonly IReadOnlyDictionary<ModdingOperation, ModdingOperation> BatchOperations = new Dictionary<ModdingOperation, ModdingOperation>
    {
        [ModdingOperation.CreatePatch] = ModdingOperation.BatchCreate,
        [ModdingOperation.ApplyPatch] = ModdingOperation.BatchApply,
        [ModdingOperation.MergePatches] = ModdingOperation.BatchMerge
    };

    private readonly ModdingOperationService _operations;
    private bool _isRunning;
    private CancellationTokenSource? _cancellation;
    private bool _closeAfterOperation;
    private ModdingOperation? _configuredOperation;

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
        var version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "unknown";
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
            ProgressText.Text = "Cancelling the current operation before closing…";
            CancelButton.IsEnabled = false;
            _cancellation?.Cancel();
        };
        SizeChanged += (_, _) => UpdateLogPanelHeight();
    }

    private OperationView SelectedBaseView => (OperationPicker.SelectedItem as OperationView) ?? OperationView.All[0];
    private OperationView SelectedView => BatchModeCheck.IsChecked == true && BatchOperations.TryGetValue(SelectedBaseView.Operation, out var batchOperation)
        ? OperationView.All.First(view => view.Operation == batchOperation)
        : SelectedBaseView;

    private void OperationChanged(object? sender, SelectionChangedEventArgs e) => ConfigureOperation();
    private void BatchModeChanged(object? sender, RoutedEventArgs e) => ConfigureOperation();

    private void ConfigureOperation()
    {
        var supportsBatch = BatchOperations.ContainsKey(SelectedBaseView.Operation);
        BatchModeCheck.IsVisible = supportsBatch;
        if (!supportsBatch && BatchModeCheck.IsChecked == true)
        {
            BatchModeCheck.IsChecked = false;
        }

        var view = SelectedView;
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
        UpdateStudioButtons();
        OperationCard.Opacity = 0.86;
        Dispatcher.UIThread.Post(() =>
        {
            OperationCard.Opacity = 1;
            if (!_logsExpanded) ContentScroll.Offset = default;
        });
    }

    private static void SetVisible(bool visible, params Control[] controls)
    {
        foreach (var control in controls) control.IsVisible = visible;
    }

    // Event handlers cannot be awaited: a failure goes to the activity log instead of ending the program.
    private void Observe(Task task) => _ = ObserveAsync(task);

    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddLog(new ModdingLogMessage { Level = ModdingLogLevel.Error, Message = exception.Message });
        }
    }

    private void RunOperation(object? sender, RoutedEventArgs e) => Observe(RunOperationAsync());

    private async Task RunOperationAsync()
    {
        if (_isRunning) return;
        _isRunning = true;
        RunButton.IsEnabled = false;
        OperationCard.IsEnabled = false;
        OperationProgress.IsIndeterminate = true;
        ProgressText.Text = "Starting…";
        _cancellation = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        CancelButton.IsVisible = true;
        try
        {
            var request = CreateRequest();
            var result = await Task.Run(() => _operations.RunAsync(request, _cancellation.Token));
            ProgressText.Text = result.Summary;
            ProgressPercentText.Text = result.Success ? "100%" : "—";
            OperationProgress.Value = result.Success ? 100 : 0;
            AddLog(new ModdingLogMessage { Level = result.Success ? ModdingLogLevel.Information : ModdingLogLevel.Error, Message = result.Summary });
            foreach (var output in result.Outputs) AddLog(new ModdingLogMessage { Level = ModdingLogLevel.Information, Message = "Output: " + output });
        }
        finally
        {
            _isRunning = false;
            _cancellation?.Dispose();
            _cancellation = null;
            CancelButton.IsVisible = false;
            RunButton.IsEnabled = true;
            OperationCard.IsEnabled = true;
            OperationProgress.IsIndeterminate = false;
            if (_closeAfterOperation) Close();
        }
    }

    private void CancelOperation(object? sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        _cancellation?.Cancel();
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
        var view = SelectedView;
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

    private void OpenBoosty(object? sender, RoutedEventArgs e) => OpenLink("https://boosty.to/y114");
    private void OpenTelegram(object? sender, RoutedEventArgs e) => OpenLink("https://t.me/y_maintg");
    private void OpenDiscord(object? sender, RoutedEventArgs e) => OpenLink("https://discord.gg/2MFdvFfD9a");
    private void OpenWebsite(object? sender, RoutedEventArgs e) => OpenLink("https://y114.space");

    private void PathChanged(object? sender, TextChangedEventArgs e) => UpdateStudioButtons();
    private void OpenOriginalInStudio(object? sender, RoutedEventArgs e) => OpenInStudio(OriginalPathBox.Text);
    private void OpenPrimaryInStudio(object? sender, RoutedEventArgs e) => OpenInStudio(PrimaryPathBox.Text);

    private void UpdateStudioButtons()
    {
        var view = SelectedView;
        OriginalStudioButton.IsVisible = view.HasOriginal && StudioLink.IsDataFile(OriginalPathBox.Text);
        PrimaryStudioButton.IsVisible = view.HasPrimary && StudioLink.IsDataFile(PrimaryPathBox.Text);
    }

    private void OpenInStudio(string? path)
    {
        if (StudioLink.IsDataFile(path))
        {
            OpenLink(StudioLink.ForFile(path!), $"Start G3MStudio once so it registers the g3mstudio:// link, or download it from {StudioLink.DownloadUrl}");
        }
    }

    private void OpenLink(string url, string? hint = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            AddLog(new ModdingLogMessage { Level = ModdingLogLevel.Error, Message = "Could not open link: " + exception.Message + (hint is null ? string.Empty : ". " + hint) });
        }
    }

    private static string[] ReadLines(string? value) => (value ?? string.Empty).Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void Dispose()
    {
        _cancellation?.Dispose();
        _operations.Dispose();
    }
}
