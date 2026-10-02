using System.Globalization;

namespace KHost.Plugins.Spotify.Control;

/// <summary>The AppleScript each command sends. Split out from the backend so the script a
/// setting produces can be asserted without Spotify, or a Mac, being involved.</summary>
public static class MacOsScripts
{
    /// <summary>Guarded rather than told directly: naming an application inside a <c>tell</c>
    /// launches it, so an unguarded pause would start Spotify in order to pause it.</summary>
    private const string NotRunning = "notrunning";

    public static string Play(string? contextUri, bool shuffle)
    {
        var body = shuffle
            ? "set shuffling to true\n\t\t"
            : string.Empty;

        // play track takes a context URI directly; a bare play resumes whatever is already loaded.
        body += contextUri is null
            ? "play"
            : $"play track \"{contextUri}\"";

        return Guarded(body);
    }

    /// <summary>One line, tab separated, since AppleScript returns a list as a comma-joined string
    /// that a track called "Hello, Goodbye" would split in the wrong place.</summary>
    /// <remarks>Each track read is in its own <c>try</c>: with nothing loaded they fail with -1728,
    /// which would otherwise lose the transport along with them. The playhead goes out as whole
    /// milliseconds, because a real is printed with the locale's decimal separator.</remarks>
    public static string State() => Guarded(
        """
        set stateLine to (player state as text)
        		try
        			set stateLine to stateLine & tab & (name of current track) & tab & (artist of current track)
        		on error
        			set stateLine to stateLine & tab & tab
        		end try
        		try
        			set stateLine to stateLine & tab & (((player position) * 1000) div 1) & tab & (duration of current track)
        		end try
        		return stateLine
        """);

    /// <summary>Reads what <see cref="State"/> returned, or null when Spotify was not running.</summary>
    public static SpotifyState? ParseState(string standardOutput)
    {
        if (!ReachedSpotify(standardOutput))
            return null;

        var parts = standardOutput.Trim().Split('\t');

        var playback = parts[0].Trim().ToLowerInvariant() switch
        {
            "playing" => SpotifyPlayback.Playing,
            "paused" => SpotifyPlayback.Paused,
            _ => SpotifyPlayback.Stopped,
        };

        return new SpotifyState(
            playback,
            parts.Length > 1 ? NullIfEmpty(parts[1]) : null,
            parts.Length > 2 ? NullIfEmpty(parts[2]) : null,
            parts.Length > 3 ? WholeNumber(parts[3]) : null,
            parts.Length > 4 ? WholeNumber(parts[4]) : null);
    }

    private static long? WholeNumber(string value)
        => long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static string? NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string Pause() => Guarded("pause");

    public static string Skip() => Guarded("next track");

    /// <summary>Steps a fade takes. Each is one write inside the same process, so this buys
    /// smoothness without costing a spawn.</summary>
    internal const int FadeSteps = 30;

    /// <summary>The whole ramp in one osascript run, finishing on an exact write, and returning
    /// the level it started from and the level it landed on, tab separated.</summary>
    /// <param name="target">Spotify's own 0 to 100 scale.</param>
    /// <param name="onlyFrom">Restores pass the range this end last wrote: a level outside it is
    /// the host's, and is reported back untouched.</param>
    /// <remarks>Spotify keeps a written level only some of the time (most writes read back one
    /// lower), so the last write tries each neighbour of the target and keeps the closest reading.
    /// Nothing is ever written back as read: that walks the level down a point per round trip.</remarks>
    public static string Fade(int target, TimeSpan duration, (int Low, int High)? onlyFrom = null)
    {
        target = Math.Clamp(target, 0, 100);

        var body = "set startLevel to sound volume\n";

        if (onlyFrom is { } range)
        {
            body += $"\t\tif startLevel < {range.Low} or startLevel > {range.High} then return (startLevel as text) & tab & (startLevel as text)\n";
        }

        if (duration > TimeSpan.Zero)
        {
            var pause = (duration.TotalSeconds / FadeSteps).ToString("0.###", CultureInfo.InvariantCulture);

            body +=
                $"""
                		repeat with i from 1 to {FadeSteps - 1}
                			set sound volume to (startLevel + ({target} - startLevel) * i / {FadeSteps}) as integer
                			delay {pause}
                		end repeat

                """;
        }

        var candidates = string.Join(", ", FinalCandidates(target));

        body +=
            $$"""
            		set candidates to {{{candidates}}}
            		set best to item 1 of candidates
            		set bestGap to 101
            		set lastWritten to -1
            		repeat with k from 1 to count of candidates
            			set candidate to item k of candidates
            			set sound volume to candidate
            			set lastWritten to candidate
            			set gap to (sound volume) - {{target}}
            			if gap < 0 then set gap to -gap
            			if gap < bestGap then
            				set best to candidate
            				set bestGap to gap
            			end if
            			if gap = 0 then exit repeat
            		end repeat
            		if lastWritten is not best then set sound volume to best
            		return (startLevel as text) & tab & ((sound volume) as text)
            """;

        return Guarded(body);
    }

    /// <summary>Above the target first, since Spotify's usual loss is one point. Silence is only
    /// ever 0: a 1 that reads back as 0 is still a sound.</summary>
    internal static int[] FinalCandidates(int target)
        => target <= 0
            ? [0]
            : new[] { target + 1, target, target - 1 }.Select(level => Math.Clamp(level, 0, 100)).Distinct().ToArray();

    /// <summary>The start and final levels <see cref="Fade"/> printed, or null when Spotify was
    /// not running or the output is not that.</summary>
    public static (int Start, int Final)? ParseFade(string standardOutput)
    {
        if (!ReachedSpotify(standardOutput))
            return null;

        var parts = standardOutput.Trim().Split('\t');

        if (parts.Length != 2
            || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
            || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var final))
        {
            return null;
        }

        return (start, final);
    }

    /// <summary>True when the script ran against a live Spotify rather than declining to start one.</summary>
    public static bool ReachedSpotify(string standardOutput)
        => !standardOutput.Trim().Equals(NotRunning, StringComparison.OrdinalIgnoreCase);

    private static string Guarded(string body) =>
        $"""
        if application "Spotify" is running then
        	tell application "Spotify"
        		{body}
        	end tell
        	return "ok"
        else
        	return "{NotRunning}"
        end if
        """;
}
