using G3MToolGUI.Services;

namespace G3MToolGUI.Tests;

[Collection("LastFolder")]
public sealed class LastFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "g3mtool-lastfolder-" + Guid.NewGuid().ToString("N"));
    private readonly string _original = LastFolder.FilePath;

    public LastFolderTests()
    {
        Directory.CreateDirectory(_root);
        LastFolder.FilePath = Path.Combine(_root, "data", "last-folder.txt");
    }

    public void Dispose()
    {
        LastFolder.FilePath = _original;
        Directory.Delete(_root, true);
    }

    [Fact]
    public void ThereIsNoFolderBeforeOneWasPicked() => Assert.Null(LastFolder.Get());

    [Fact]
    public void TheFolderOfAPickedFileIsKeptAndAPickedFolderIsKeptItself()
    {
        var mods = Directory.CreateDirectory(Path.Combine(_root, "mods")).FullName;
        LastFolder.Remember(Path.Combine(mods, "new.g3mpatch"));
        Assert.Equal(mods, LastFolder.Get());

        var other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        LastFolder.Remember(other);
        Assert.Equal(other, LastFolder.Get());
    }

    [Fact]
    public void NothingChangesForAnEmptyOrMissingPathAndAFolderThatIsGoneIsNotOffered()
    {
        var mods = Directory.CreateDirectory(Path.Combine(_root, "mods")).FullName;
        LastFolder.Remember(Path.Combine(mods, "a.win"));
        LastFolder.Remember(null);
        LastFolder.Remember("");
        LastFolder.Remember(Path.Combine(_root, "nowhere", "a.win"));
        Assert.Equal(mods, LastFolder.Get());

        Directory.Delete(mods);
        Assert.Null(LastFolder.Get());
    }
}
