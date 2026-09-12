using System;
using System.IO;
using G3MLib.DataFile;
using G3MToolCLI.Utils;
using Xunit;

namespace G3MToolCLI.Tests;

public sealed class CoreUtilityTests
{
    [Fact("CoreUtilityTests.cs", 10)]
    public void GeneralInfoUtilitiesExposeStableData()
    {
        using GameMakerData data = GameMakerData.CreateNew2026LTS();
        var info = GeneralInfoUtil.ExtractGeneralInfo(data);
        Assert.NotNull(info);
        Assert.Equal(data.GeneralInfo.DisplayName.Content, info.DisplayName);
        Assert.Contains("2026.1", GeneralInfoUtil.GetVersionDisplay(data.GeneralInfo));
    }

    [Fact("CoreUtilityTests.cs", 21)]
    public void ExecutableDirectoryExists()
    {
        Assert.True(Directory.Exists(PlatformUtil.GetExecutableDirectory()));
        string expected = Environment.ProcessPath is null || string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? AppContext.BaseDirectory : Path.GetDirectoryName(Environment.ProcessPath)!;
        Assert.Equal(expected, PlatformUtil.GetExecutableDirectory());
    }
}
