using System.Globalization;
using System.Runtime.Versioning;
using KHost.Plugins.Spotify.Control;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.Spotify.Tests;

public class MacOsFadeScriptTests
{
    /// <summary>An unguarded <c>tell application "Spotify"</c> launches it, so a fade asked of a
    /// closed Spotify would open it in order to make it quiet.</summary>
    [Fact]
    public void Fade_ChecksSpotifyIsRunningBeforeTellingIt()
    {
        var script = MacOsScripts.Fade(64, TimeSpan.FromSeconds(1.5), (0, 0));

        Assert.Contains("if application \"Spotify\" is running then", script);
        Assert.True(
            script.IndexOf("is running", StringComparison.Ordinal) < script.IndexOf("tell application", StringComparison.Ordinal),
            $"the guard has to come first:\n{script}");
    }

    // One process for the whole ramp: the steps are delays inside the script, not spawns.
    [Fact]
    public void Fade_WithALength_RampsInStepsInsideTheScript()
    {
        var script = MacOsScripts.Fade(0, TimeSpan.FromSeconds(1.5));

        Assert.Contains($"repeat with i from 1 to {MacOsScripts.FadeSteps - 1}", script);
        Assert.Contains("delay 0.05", script);
    }

    [Fact]
    public void Fade_WithNoLength_WritesOnlyTheFinalLevel()
    {
        var script = MacOsScripts.Fade(0, TimeSpan.Zero);

        Assert.DoesNotContain("repeat with i", script);
        Assert.DoesNotContain("delay", script);
    }

    // AppleScript reads "0,05" as a list of two numbers, which a German Mac would hand it.
    [Fact]
    public void Fade_UnderACommaDecimalCulture_StillWritesAPointInTheDelay()
    {
        var before = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Contains("delay 0.05", MacOsScripts.Fade(0, TimeSpan.FromSeconds(1.5)));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    // Spotify usually reads a write back one lower, so above the target is tried first.
    [Fact]
    public void Fade_TriesTheNeighbourAboveTheTargetFirst()
        => Assert.Contains("set candidates to {51, 50, 49}", MacOsScripts.Fade(50, TimeSpan.Zero));

    [Theory]
    [InlineData(50, new[] { 51, 50, 49 })]
    [InlineData(100, new[] { 100, 99 })]
    [InlineData(1, new[] { 2, 1, 0 })]
    public void FinalCandidates_AreTheTargetsNeighboursWithinRange(int target, int[] expected)
        => Assert.Equal(expected, MacOsScripts.FinalCandidates(target));

    // A 1 that reads back as 0 is still a sound.
    [Fact]
    public void FinalCandidates_ForSilence_AreOnlyZero()
        => Assert.Equal([0], MacOsScripts.FinalCandidates(0));

    // Restoring a level the host has since changed would move their slider.
    [Fact]
    public void Fade_ARestore_LeavesALevelThisEndDidNotWriteAlone()
    {
        var script = MacOsScripts.Fade(64, TimeSpan.Zero, (0, 3));

        Assert.Contains("if startLevel < 0 or startLevel > 3 then return", script);
        Assert.True(
            script.IndexOf("if startLevel <", StringComparison.Ordinal) < script.IndexOf("set sound volume", StringComparison.Ordinal),
            $"the check has to come before any write:\n{script}");
    }

    [Fact]
    public void Fade_ASilence_RampsFromWhereverTheRoomIs()
        => Assert.DoesNotContain("if startLevel <", MacOsScripts.Fade(0, TimeSpan.Zero));

    [Fact]
    public void ParseFade_ReadsTheStartAndFinalLevels()
        => Assert.Equal((64, 0), MacOsScripts.ParseFade("64\t0\n"));

    [Theory]
    [InlineData("notrunning\n")]
    [InlineData("ok\n")]
    [InlineData("64\n")]
    [InlineData("high\tlow")]
    public void ParseFade_AnythingElse_IsNull(string output)
        => Assert.Null(MacOsScripts.ParseFade(output));
}

public class MacOsStatePlayheadTests
{
    [Fact]
    public void ParseState_WithAPlayhead_ReadsProgressAndDuration()
    {
        var state = MacOsScripts.ParseState("paused\tBlue Monday\tNew Order\t179000\t180000\n");

        Assert.Equal(179000, state!.ProgressMs);
        Assert.Equal(180000, state.DurationMs);
    }

    // A track with a position but no readable duration still has its position.
    [Fact]
    public void ParseState_WithOnlyAPosition_ReadsIt()
        => Assert.Equal(179000, MacOsScripts.ParseState("paused\tX\tY\t179000")!.ProgressMs);

    [Fact]
    public void ParseState_WithoutAPlayhead_LeavesItUnknown()
    {
        var state = MacOsScripts.ParseState("paused\tBlue Monday\tNew Order");

        Assert.Null(state!.ProgressMs);
        Assert.Null(state.DurationMs);
    }

    [Fact]
    public void ParseState_APlayheadThatIsNotANumber_IsUnknown()
        => Assert.Null(MacOsScripts.ParseState("paused\tX\tY\tsoon\t180000")!.ProgressMs);

    // With nothing loaded the track reads fail with -1728, and the transport must survive them.
    [Theory]
    [InlineData("name of current track")]
    [InlineData("duration of current track")]
    public void State_ReadsTheTrackInsideATry(string read)
    {
        var lines = MacOsScripts.State().Split('\n').Select(line => line.Trim()).ToList();
        var at = lines.FindIndex(line => line.Contains(read, StringComparison.Ordinal));

        Assert.True(at > 0, read);

        // The nearest block keyword above the read has to open a try, not close one.
        var opener = lines.Take(at).Last(line => line is "try" or "end try" or "on error");

        Assert.Equal("try", opener);
    }
}

/// <summary>The fader's own bookkeeping, over osascript's printed answer rather than a real Spotify.</summary>
[SupportedOSPlatform("macos")]
public class MacOsSpotifyFaderTests
{
    private readonly List<string> _scripts = [];
    private readonly Queue<ProcessResult> _replies = new();

    private MacOsSpotifyFader Build()
        => new(NullLogger.Instance, (_, arguments, _) =>
        {
            _scripts.Add(arguments.Last());
            return Task.FromResult(_replies.Dequeue());
        });

    private void Reply(string output, int exitCode = 0)
        => _replies.Enqueue(new ProcessResult(exitCode, output, exitCode == 0 ? "" : output));

    [Fact]
    public async Task SilenceAsync_TakesItToZero()
    {
        Reply("64\t0");

        Assert.Equal(FadeOutcome.Landed, await Build().SilenceAsync(TimeSpan.FromSeconds(1.5)));
        Assert.Contains("set candidates to {0}", _scripts[0]);
    }

    // The level found is the one put back, and the restore only proceeds from the level this end wrote.
    [Fact]
    public async Task RestoreAsync_AfterASilence_ComesBackToTheLevelFound()
    {
        var fader = Build();
        Reply("64\t0");
        await fader.SilenceAsync(TimeSpan.Zero);

        Reply("0\t64");
        Assert.Equal(FadeOutcome.Landed, await fader.RestoreAsync(TimeSpan.FromSeconds(1.5)));

        Assert.Contains("set candidates to {65, 64, 63}", _scripts[1]);
        Assert.Contains("if startLevel < 0 or startLevel > 0 then return", _scripts[1]);
    }

    [Fact]
    public async Task RestoreAsync_WithNothingSilenced_DoesNotRun()
    {
        Assert.Equal(FadeOutcome.NothingToFade, await Build().RestoreAsync(TimeSpan.Zero));
        Assert.Empty(_scripts);
    }

    // Writing 19 reads back 18 and writing 20 reads back 20, so a restore to 19 lands on 20. The
    // next fade out reads 20, a level we wrote, and must still come back to 19, not 20.
    [Fact]
    public async Task ALevelSpotifyCannotHold_DoesNotDriftAcrossFades()
    {
        var fader = Build();
        Reply("19\t0");
        await fader.SilenceAsync(TimeSpan.Zero);
        Reply("0\t20");
        await fader.RestoreAsync(TimeSpan.Zero);

        Reply("20\t0");
        await fader.SilenceAsync(TimeSpan.Zero);
        Reply("0\t20");
        await fader.RestoreAsync(TimeSpan.Zero);

        Assert.Contains("set candidates to {20, 19, 18}", _scripts[3]);
    }

    // Still silent from the last fade out: a second one reads 0, which is ours, not the room's.
    [Fact]
    public async Task ASecondSilence_DoesNotTakeSilenceAsTheLevelToComeBackTo()
    {
        var fader = Build();
        Reply("64\t0");
        await fader.SilenceAsync(TimeSpan.Zero);
        Reply("0\t0");
        await fader.SilenceAsync(TimeSpan.Zero);

        Reply("0\t64");
        await fader.RestoreAsync(TimeSpan.Zero);

        Assert.Contains("set candidates to {65, 64, 63}", _scripts[2]);
    }

    // The host lowered Spotify while it played: that is the room's level now.
    [Fact]
    public async Task AHostWhoMovedTheSliderWhilePlaying_IsFollowed()
    {
        var fader = Build();
        Reply("64\t0");
        await fader.SilenceAsync(TimeSpan.Zero);
        Reply("0\t64");
        await fader.RestoreAsync(TimeSpan.Zero);

        Reply("40\t0");
        await fader.SilenceAsync(TimeSpan.Zero);
        Reply("0\t40");
        await fader.RestoreAsync(TimeSpan.Zero);

        Assert.Contains("set candidates to {41, 40, 39}", _scripts[3]);
    }

    // The script found 80 where this end left 0, so it wrote nothing; that level is now the one
    // every later fade comes back to.
    [Fact]
    public async Task AHostWhoMovedTheSliderWhileSilent_IsKept()
    {
        var fader = Build();
        Reply("64\t0");
        await fader.SilenceAsync(TimeSpan.Zero);
        Reply("80\t80");
        Assert.Equal(FadeOutcome.Landed, await fader.RestoreAsync(TimeSpan.Zero));

        // A second restore with no fade out between (a skip, say) proceeds only from 80, and to 80.
        Reply("80\t80");
        await fader.RestoreAsync(TimeSpan.Zero);

        Assert.Contains("set candidates to {81, 80, 79}", _scripts[2]);
        Assert.Contains("if startLevel < 80 or startLevel > 80 then return", _scripts[2]);
    }

    [Fact]
    public async Task ASpotifyThatIsNotRunning_HasNothingToFade()
    {
        Reply("notrunning");

        Assert.Equal(FadeOutcome.NothingToFade, await Build().SilenceAsync(TimeSpan.Zero));
    }

    [Theory]
    [InlineData("64\t0", 1)]
    [InlineData("sixty-four", 0)]
    public async Task AScriptThatFailsOrSaysSomethingElse_HasFailed(string output, int exitCode)
    {
        Reply(output, exitCode);

        Assert.Equal(FadeOutcome.Failed, await Build().SilenceAsync(TimeSpan.Zero));
    }

    // ProcessRunner cuts a run off at ten seconds, part way through a long ramp.
    [Fact]
    public async Task ALongFade_IsCappedInsideTheProcessTimeout()
    {
        Reply("64\t0");

        await Build().SilenceAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("delay 0.267", _scripts[0]);
    }

    // osascript killed part way leaves the level between where it was and where it was heading.
    // That level is still ours: adopting it would bring every later fade back to half volume.
    [Fact]
    public async Task AFadeCancelledPartWay_IsNotTakenForTheHostsLevel()
    {
        using var cancel = new CancellationTokenSource();
        var cancelNext = false;

        var fader = new MacOsSpotifyFader(NullLogger.Instance, (_, arguments, _) =>
        {
            _scripts.Add(arguments.Last());

            if (!cancelNext)
                return Task.FromResult(_replies.Dequeue());

            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        });

        Reply("64\t0");
        await fader.SilenceAsync(TimeSpan.Zero);

        cancelNext = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fader.RestoreAsync(TimeSpan.FromSeconds(1.5), cancel.Token));
        cancelNext = false;

        Reply("30\t0");
        await fader.SilenceAsync(TimeSpan.Zero);
        Reply("0\t64");
        await fader.RestoreAsync(TimeSpan.Zero);

        Assert.Contains("set candidates to {65, 64, 63}", _scripts[^1]);
    }
}
