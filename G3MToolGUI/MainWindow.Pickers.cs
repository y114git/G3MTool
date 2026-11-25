using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using G3MLib.Modding.Logging;
using G3MToolGUI.Services;

namespace G3MToolGUI;

// File, folder and dialog pickers of the window.
public sealed partial class MainWindow
{
    private void BrowseOriginal(object? sender, RoutedEventArgs e) => Observe(BrowseOriginalAsync());

    private async Task BrowseOriginalAsync()
    {
        OriginalPathBox.Text = await PickFileAsync("Choose original data file") ?? OriginalPathBox.Text;
    }

    private void BrowsePrimary(object? sender, RoutedEventArgs e) => Observe(BrowsePrimaryAsync());

    private async Task BrowsePrimaryAsync()
    {
        var operation = SelectedView.Operation;
        if (operation == ModdingOperation.BatchMerge)
        {
            PrimaryPathBox.Text = await PickFolderAsync("Choose data output directory") ?? PrimaryPathBox.Text;
            return;
        }
        if (operation == ModdingOperation.MergePatches)
        {
            var extension = Path.GetExtension(OriginalPathBox.Text);
            PrimaryPathBox.Text = await SavePathAsync("Choose merged data output", "merged" + (string.IsNullOrEmpty(extension) ? ".win" : extension)) ?? PrimaryPathBox.Text;
            return;
        }

        PrimaryPathBox.Text = await PickFileAsync("Choose input file") ?? PrimaryPathBox.Text;
    }

    private void BrowseAdditional(object? sender, RoutedEventArgs e) => Observe(BrowseAdditionalAsync());

    private async Task BrowseAdditionalAsync()
    {
        if (SelectedView.Operation == ModdingOperation.RunXDelta)
        {
            AddLog(new ModdingLogMessage { Level = ModdingLogLevel.Information, Message = "Enter one XDelta argument per line." });
            return;
        }

        var paths = await PickFilesAsync("Choose input files");
        if (paths.Count == 0) return;
        var prefix = string.IsNullOrWhiteSpace(AdditionalPathsBox.Text) ? string.Empty : AdditionalPathsBox.Text.TrimEnd() + Environment.NewLine;
        if (SelectedView.Operation == ModdingOperation.BatchMerge && paths.Any(path => path.Contains(',', StringComparison.Ordinal) || path.Contains('\n', StringComparison.Ordinal) || path.Contains('\r', StringComparison.Ordinal)))
        {
            AddLog(new ModdingLogMessage { Level = ModdingLogLevel.Error, Message = "Merge-set paths cannot contain commas or line breaks." });
            return;
        }
        AdditionalPathsBox.Text = prefix + string.Join(SelectedView.Operation == ModdingOperation.BatchMerge ? "," : Environment.NewLine, paths);
    }

    private void BrowseOutput(object? sender, RoutedEventArgs e) => Observe(BrowseOutputAsync());

    private async Task BrowseOutputAsync()
    {
        if (SelectedView.OutputIsDirectory)
        {
            OutputPathBox.Text = await PickFolderAsync("Choose output directory") ?? OutputPathBox.Text;
            return;
        }

        var extension = SelectedView.Operation switch
        {
            ModdingOperation.Compare => "md",
            ModdingOperation.CreateXDelta => "xdelta",
            ModdingOperation.ApplyPatch or ModdingOperation.ApplyXDelta or ModdingOperation.RunScript => "win",
            _ => "g3mpatch"
        };
        OutputPathBox.Text = await SavePathAsync("Choose output path", "output." + extension) ?? OutputPathBox.Text;
    }

    private void BrowseCache(object? sender, RoutedEventArgs e) => Observe(BrowseCacheAsync());

    private async Task BrowseCacheAsync()
    {
        CachePathBox.Text = await PickFolderAsync("Choose cache directory") ?? CachePathBox.Text;
    }

    private Task<string?> ShowScriptInputAsync(string title, string label, string value, bool multiline, bool question = false)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 480,
            MinWidth = 320,
            MaxHeight = 650,
            SizeToContent = SizeToContent.Height,
            CanResize = multiline,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var input = new TextBox { Name = "ScriptInput", Text = value, AcceptsReturn = multiline, MinHeight = multiline ? 140 : 0 };
        var accept = new Button { Name = "ScriptAccept", Content = question ? "Yes" : "OK", IsDefault = true };
        var cancel = new Button { Name = "ScriptCancel", Content = question ? "No" : "Cancel", IsCancel = true };
        accept.Click += (_, _) => dialog.Close(question ? "yes" : input.Text ?? "");
        cancel.Click += (_, _) => dialog.Close(null);
        var panel = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12 };
        panel.Children.Add(new ScrollViewer { MaxHeight = 300, Content = new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap } });
        if (!question) panel.Children.Add(input);
        panel.Children.Add(new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Children = { cancel, accept }
        });
        dialog.Content = panel;
        dialog.Opened += (_, _) => { if (!question) input.Focus(); };
        return dialog.ShowDialog<string?>(this);
    }

    private async Task<string?> PickScriptFileAsync(string filter, string suggestedPath, bool save)
    {
        var parts = filter.Split('|');
        var types = new List<FilePickerFileType>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
            types.Add(new FilePickerFileType(parts[i]) { Patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) });
        if (types.Count == 0) types.Add(FilePickerFileTypes.All);
        IStorageFolder? folder = null;
        if (!string.IsNullOrWhiteSpace(suggestedPath))
        {
            var directory = Directory.Exists(suggestedPath) ? suggestedPath : Path.GetDirectoryName(Path.GetFullPath(suggestedPath));
            if (directory is not null) folder = await StorageProvider.TryGetFolderFromPathAsync(directory);
        }
        folder ??= await StartFolderAsync();
        if (save)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save script file",
                SuggestedFileName = Path.GetFileName(suggestedPath),
                SuggestedStartLocation = folder,
                FileTypeChoices = types
            });
            return Remembered(file?.TryGetLocalPath());
        }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open script file",
            AllowMultiple = false,
            SuggestedStartLocation = folder,
            FileTypeFilter = types
        });
        return files is [{ } first, ..] ? Remembered(first.TryGetLocalPath()) : null;
    }

    // The folder of the last pick is where the next dialog starts, also after a restart.
    private async Task<IStorageFolder?> StartFolderAsync() => LastFolder.Get() is { } folder ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null;

    private static string? Remembered(string? path)
    {
        LastFolder.Remember(path);
        return path;
    }

    private async Task<string?> PickFileAsync(string title)
    {
        var files = await PickFilesAsync(title, false);
        return files is [var first, ..] ? first : null;
    }

    private async Task<IReadOnlyList<string>> PickFilesAsync(string title, bool allowMultiple = true)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = [FilePickerFileTypes.All],
            SuggestedStartLocation = await StartFolderAsync()
        });
        var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) LastFolder.Remember(paths[0]);
        return paths;
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = await StartFolderAsync() });
        return folders is [{ } first, ..] ? Remembered(first.TryGetLocalPath()) : null;
    }

    private async Task<string?> SavePathAsync(string title, string suggestedFileName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedFileName, SuggestedStartLocation = await StartFolderAsync() });
        return Remembered(file?.TryGetLocalPath());
    }
}
