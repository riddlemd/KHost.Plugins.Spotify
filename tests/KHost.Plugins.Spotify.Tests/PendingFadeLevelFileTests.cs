using KHost.Plugins.Spotify.Control;

namespace KHost.Plugins.Spotify.Tests;

public sealed class PendingFadeLevelFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "khost-spotify-tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_folder, "pending-fade-levels.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Read_WithNoFile_IsEmpty()
        => Assert.Empty(new PendingFadeLevelFile(FilePath).Read());

    [Fact]
    public void Write_ThenRead_GivesTheLevelsBack()
    {
        var file = new PendingFadeLevelFile(FilePath);

        file.Write(new Dictionary<string, double> { ["speakers"] = 0.37, ["headphones"] = 1 });

        Assert.Equal(0.37, new PendingFadeLevelFile(FilePath).Read()["speakers"]);
        Assert.Equal(1, new PendingFadeLevelFile(FilePath).Read()["headphones"]);
    }

    // Moved over the old record, so nothing half written is ever the record.
    [Fact]
    public void Write_LeavesNoTemporaryFileBehind()
    {
        var file = new PendingFadeLevelFile(FilePath);

        file.Write(new Dictionary<string, double> { ["a"] = 0.4 });
        file.Write(new Dictionary<string, double> { ["a"] = 0.5 });

        Assert.Equal(["pending-fade-levels.json"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void Write_Empty_RemovesTheFile()
    {
        var file = new PendingFadeLevelFile(FilePath);
        file.Write(new Dictionary<string, double> { ["a"] = 0.4 });

        file.Write(new Dictionary<string, double>());

        Assert.False(File.Exists(FilePath));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"Levels":{"a":"loud"}}""")]
    [InlineData("[1, 2]")]
    public void Read_ACorruptFile_IsNoRecord(string contents)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, contents);

        Assert.Empty(new PendingFadeLevelFile(FilePath).Read());
    }

    // A saved 0 is no level to come back to; past 1 is not a mixer level at all.
    [Fact]
    public void Read_DropsLevelsOutOfRange()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, """{"Levels":{"zero":0,"negative":-0.3,"over":1.2,"good":0.37}}""");

        Assert.Equal(["good"], new PendingFadeLevelFile(FilePath).Read().Keys);
    }
}
