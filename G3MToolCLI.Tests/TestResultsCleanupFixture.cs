using Xunit;

[assembly: AssemblyFixture<G3MToolCLI.Tests.TestResultsCleanupFixture>]

namespace G3MToolCLI.Tests;

public sealed class TestResultsCleanupFixture : IDisposable
{
    private readonly bool _preserveTestResults = Environment.GetCommandLineArgs()
        .Any(argument => argument.StartsWith("--results-directory", StringComparison.Ordinal));

    public TestResultsCleanupFixture()
    {
        if (!_preserveTestResults)
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            DeleteDefaultTestResults();
        }
    }

    public void Dispose()
    {
        if (!_preserveTestResults)
            DeleteDefaultTestResults();
    }

    private void OnProcessExit(object? sender, EventArgs e) => DeleteDefaultTestResults();

    private static void DeleteDefaultTestResults()
    {
        foreach (string path in new[]
        {
            Path.Combine(Environment.CurrentDirectory, "TestResults"),
            Path.Combine(AppContext.BaseDirectory, "TestResults")
        }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }
}
