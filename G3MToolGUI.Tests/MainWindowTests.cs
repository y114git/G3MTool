using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using G3MToolGUI.Services;

namespace G3MToolGUI.Tests;

public sealed class MainWindowTests
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

    [Fact]
    public async Task OperationChangesAndFilePickersPreserveInputOutputRoles()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(MainWindowTests));
        await session.Dispatch(() =>
        {
            var window = new MainWindow();
            window.Show();
            try
            {
                T Control<T>(string name) where T : Control => window.FindControl<T>(name)!;
                void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                void Select(string title)
                {
                    var picker = Control<ComboBox>("OperationPicker");
                    picker.SelectedItem = picker.Items.Cast<object>().Single(item => item.ToString() == title);
                }
                void Batch(bool enabled)
                {
                    var check = Control<CheckBox>("BatchModeCheck");
                    check.IsChecked = enabled;
                    check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                ModdingOperationRequest Request() => (ModdingOperationRequest)typeof(MainWindow)
                    .GetMethod("CreateRequest", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;

                Control<TextBox>("OriginalPathBox").Text = "original.win";
                Control<TextBox>("PrimaryPathBox").Text = "modified.win";
                Control<TextBox>("OutputPathBox").Text = "patch.g3mpatch";
                Control<CheckBox>("XDeltaFallbackCheck").IsChecked = true;
                Select("Merge patches");
                Assert.Empty(Control<TextBox>("PrimaryPathBox").Text!);
                Assert.Empty(Control<TextBox>("OutputPathBox").Text!);
                Assert.False(Request().IncludeXDeltaFallback);

                Control<CheckBox>("SequentialMergeCheck").IsChecked = true;
                Batch(true);
                Assert.Equal(ModdingOperation.BatchMerge, Request().Operation);
                Assert.False(Request().UseSequentialMerge);
                Assert.True(Control<TextBox>("AdditionalPathsBox").IsVisible);

                var paths = new[] { Path.GetFullPath("low.g3mpatch"), Path.GetFullPath("high.g3mpatch") };
                var files = paths.Select(path => Mock<IStorageFile>((method, _) => method == "get_Path" ? new Uri(path) : throw new NotSupportedException(method))).ToArray();
                FilePickerSaveOptions? saveOptions = null;
                bool cancelSave = false;
                IStorageProvider storage = Mock<IStorageProvider>((method, args) => method switch
                {
                    "OpenFilePickerAsync" => Task.FromResult<IReadOnlyList<IStorageFile>>(files),
                    "SaveFilePickerAsync" => Save((FilePickerSaveOptions)args![0]!),
                    _ => throw new NotSupportedException(method)
                });
                Task<IStorageFile?> Save(FilePickerSaveOptions options)
                {
                    saveOptions = options;
                    return Task.FromResult(cancelSave ? null : files[0]);
                }
                typeof(TopLevel).GetField("_storageProvider", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, storage);
                Click("AdditionalBrowseButton");
                Assert.Equal(string.Join(',', paths), Control<TextBox>("AdditionalPathsBox").Text);
                Click("AdditionalBrowseButton");
                Assert.Equal(2, Request().InputPaths.Count);
                Assert.All(Request().InputPaths, set => Assert.Equal(paths, set.Split(',')));

                Batch(false);
                Control<TextBox>("OriginalPathBox").Text = "original.unx";
                Click("PrimaryBrowseButton");
                Assert.NotNull(saveOptions);
                Assert.Equal("merged.unx", saveOptions.SuggestedFileName);
                Assert.Equal(paths[0], Control<TextBox>("PrimaryPathBox").Text);
                cancelSave = true;
                Click("PrimaryBrowseButton");
                Assert.Equal(paths[0], Control<TextBox>("PrimaryPathBox").Text);

                Batch(true);
                window.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, "Clear"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(ModdingOperation.MergePatches, Request().Operation);
                Assert.Equal("Data output (optional)", Control<TextBlock>("PrimaryLabel").Text);

                Batch(true);
                Select("Run a program");
                Assert.False(Control<CheckBox>("BatchModeCheck").IsVisible);
                Assert.Equal("Program", Control<TextBlock>("PrimaryLabel").Text);
                Control<TextBox>("OriginalPathBox").Text = "hidden-original.win";
                Control<TextBox>("OutputPathBox").Text = "hidden-output.win";
                Control<CheckBox>("SequentialMergeCheck").IsChecked = true;
                Assert.Empty(Request().OriginalPath);
                Assert.Empty(Request().OutputPath);
                Assert.False(Request().UseSequentialMerge);

                foreach (object item in Control<ComboBox>("OperationPicker").Items.Cast<object>())
                {
                    Control<TextBox>("PrimaryPathBox").Text = "stale-input.win";
                    Control<ComboBox>("OperationPicker").SelectedItem = item;
                    Assert.Empty(Control<TextBox>("PrimaryPathBox").Text!);
                }
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }

    private static T Mock<T>(Func<string, object?[]?, object?> invoke) where T : class
    {
        T proxy = DispatchProxy.Create<T, StorageProxy>();
        ((StorageProxy)(object)proxy).Handler = invoke;
        return proxy;
    }

    public class StorageProxy : DispatchProxy
    {
        public Func<string, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!.Name, args);
    }
}
