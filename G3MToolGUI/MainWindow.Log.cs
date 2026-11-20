using System.Text;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using G3MLib.Modding.Logging;

namespace G3MToolGUI;

// The activity log and the progress bar of the window.
public sealed partial class MainWindow
{
    private const int LogLimit = 1_000_000;

    private readonly StringBuilder _log = new();
    private readonly Queue<LogLine> _logLines = new();
    private bool _scrollPending;
    private bool _logsExpanded;

    private sealed record LogLine(ModdingLogLevel Level, string Text);

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

    private void SaveLog(object? sender, RoutedEventArgs e) => Observe(SaveLogAsync());

    private async Task SaveLogAsync()
    {
        var path = await SavePathAsync("Save activity log", "g3mtool.log");
        if (path is not null) await File.WriteAllTextAsync(path, _log.ToString());
    }

    private void AddLog(ModdingLogMessage message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddLog(message));
            return;
        }

        var text = $"[{DateTime.Now:HH:mm:ss}] {message.Level}: {message.Message}{Environment.NewLine}";
        LogLine line = new(message.Level, text);
        _log.Append(text);
        _logLines.Enqueue(line);
        LogBox.Inlines?.Add(CreateLogRun(line));
        if (_log.Length > LogLimit)
        {
            // Trim to a low-water mark so the cost is paid once per many messages.
            var trimmed = 0;
            while (_log.Length - trimmed > LogLimit * 4 / 5 && _logLines.TryDequeue(out var removed))
            {
                trimmed += removed.Text.Length;
                if (LogBox.Inlines is { Count: > 0 } inlines) inlines.RemoveAt(0);
            }

            _log.Remove(0, trimmed);
        }

        if (_scrollPending) return;
        _scrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollPending = false;
            LogScroll.Offset = new Vector(0, LogScroll.Extent.Height);
        });
    }

    private void UpdateLogPanelHeight()
    {
        LogPanel.Height = _logsExpanded ? Math.Max(220, (Bounds.Height - 80) * .85) : 110;
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
            var percent = progress.Total == 0 ? 0 : (int)Math.Round(progress.Current * 100d / progress.Total);
            OperationProgress.Value = percent;
            ProgressPercentText.Text = percent + "%";
            ProgressText.Text = string.IsNullOrWhiteSpace(progress.Operation) ? "Working…" : progress.Operation;
        });
    }
}
