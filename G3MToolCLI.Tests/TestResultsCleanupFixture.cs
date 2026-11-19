[assembly: AssemblyFixture<G3MToolCLI.Tests.TestResultsCleanupFixture>]

namespace G3MToolCLI.Tests;

/// <summary>Removes the TestResults folders this test run created. A folder that existed before the run is never touched.</summary>
public sealed class TestResultsCleanupFixture : IDisposable
{
    private readonly string[] _createdByRun;

    public TestResultsCleanupFixture()
    {
        if (Environment.GetCommandLineArgs().Any(argument => argument.StartsWith("--results-directory", StringComparison.Ordinal)))
        {
            _createdByRun = [];
            return;
        }
        _createdByRun = [.. new[]
        {
            Path.Combine(Environment.CurrentDirectory, "TestResults"),
            Path.Combine(AppContext.BaseDirectory, "TestResults")
        }.Distinct(StringComparer.OrdinalIgnoreCase).Where(path => !Directory.Exists(path))];
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public void Dispose() => DeleteCreatedTestResults();

    private void OnProcessExit(object? sender, EventArgs e) => DeleteCreatedTestResults();

    private void DeleteCreatedTestResults()
    {
        foreach (var path in _createdByRun)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The results folder is only a convenience cleanup; a locked file must not fail the run.
            }
        }
    }
}
