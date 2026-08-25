using KHost.Plugins.Spotify.Control;

namespace KHost.Plugins.Spotify.Tests;

public class MacOsScriptsTests
{
    /// <summary>
    /// Naming an application inside a tell block launches it, so an unguarded pause would start
    /// Spotify in order to pause it.
    /// </summary>
    [Fact]
    public void EveryScript_ChecksSpotifyIsRunningBeforeTellingIt()
    {
        string[] scripts =
        [
            MacOsScripts.Play("spotify:playlist:abc123", shuffle: true),
            MacOsScripts.Play(contextUri: null, shuffle: false),
            MacOsScripts.Pause(),
            MacOsScripts.Skip(),
        ];

        foreach (var script in scripts)
        {
            Assert.Contains("if application \"Spotify\" is running then", script);
            Assert.True(
                script.IndexOf("is running", StringComparison.Ordinal) < script.IndexOf("tell application", StringComparison.Ordinal),
                $"the guard has to come first:\n{script}");
        }
    }

    [Fact]
    public void Play_WithAPlaylist_PlaysThatContext()
        => Assert.Contains("play track \"spotify:playlist:abc123\"", MacOsScripts.Play("spotify:playlist:abc123", shuffle: false));

    [Fact]
    public void Play_WithNoPlaylist_ResumesWhateverIsLoaded()
    {
        var script = MacOsScripts.Play(contextUri: null, shuffle: false);

        Assert.DoesNotContain("play track", script);
        Assert.Contains("play", script);
    }

    [Fact]
    public void Play_WithShuffle_TurnsShufflingOnBeforePlaying()
    {
        var script = MacOsScripts.Play("spotify:playlist:abc123", shuffle: true);

        // Asserted present first: IndexOf returns -1 for a missing needle, so the ordering check
        // below passes on its own even when shuffle was never set at all.
        Assert.Contains("set shuffling to true", script);
        Assert.True(
            script.IndexOf("set shuffling to true", StringComparison.Ordinal) < script.IndexOf("play track", StringComparison.Ordinal),
            $"shuffle has to be set before the context starts:\n{script}");
    }

    [Fact]
    public void Play_WithoutShuffle_LeavesShufflingAlone()
        => Assert.DoesNotContain("set shuffling", MacOsScripts.Play("spotify:playlist:abc123", shuffle: false));

    [Fact]
    public void Pause_PausesRatherThanToggling()
    {
        var script = MacOsScripts.Pause();

        Assert.Contains("pause", script);
        Assert.DoesNotContain("playpause", script);
    }

    [Fact]
    public void Skip_AsksForTheNextTrack()
        => Assert.Contains("next track", MacOsScripts.Skip());

    [Fact]
    public void ReachedSpotify_TheGuardDeclined_IsFalse()
        => Assert.False(MacOsScripts.ReachedSpotify("notrunning\n"));

    [Fact]
    public void ReachedSpotify_TheCommandRan_IsTrue()
        => Assert.True(MacOsScripts.ReachedSpotify("ok\n"));
}
