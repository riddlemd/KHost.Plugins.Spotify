# KHost.Plugins.Spotify

Break-music provider for [KHost](https://github.com/riddlemd/KHost). Puts the Spotify desktop app on
between singers, and takes it off again when one starts.

This plugin **drives Spotify; it does not carry its audio**. The sound comes out of Spotify's own
output, where the host cannot route it, mix it, or send it to a Cast device — so
`RendersThroughHost` is false. There is no API key, no OAuth, and no Spotify Premium requirement:
everything goes through the app already running on the machine.

## What it does, and what it deliberately does not

It sends five commands and no more: start, pause, resume, stop and skip. It **reads Spotify's
state back** — playing or paused, and the track — so the console names what is on, and a host who
put Spotify on themselves before the first singer is left alone rather than restarted. On macOS and
Windows it is also told when Spotify moves by itself (a track ends, or someone presses pause in
Spotify's window); Linux has no watch yet, so the host asks.

**Spotify's level stays the host's.** It is set in Spotify, by whoever is running the room. KHost
asks every provider it cannot mix to take the venue level, and this one declines rather than move a
slider out from under them. Fades only ever take Spotify down from wherever it was found and back to
that same level (see below).

Resume starts the playlist instead when Spotify has **no track loaded**. A freshly launched Spotify
reports itself paused with nothing to resume, and accepts a resume that then plays nothing.

## Fading

Pause, resume and stop fade over the plugin's own fade length, and a start comes up from silence.
KHost's own two second fade hint is ignored in favour of that setting. The fade moves Spotify's level
from outside the app:

- **macOS**: Spotify's AppleScript `sound volume`, with the whole ramp done in one `osascript` run.
  Spotify loses a point on most writes (writing 50 reads back 49), so the last write tries the
  target's neighbours and keeps whichever reads back closest.
- **Windows**: Spotify's own rows in the Windows volume mixer, on every active output, through Core
  Audio. The level put back is the exact one found.
- **Linux**: no fading. Break music starts and stops at full level, and the Plugins page says so.

A fade never picks a level of its own. If the host moves Spotify's slider while it is playing, the
next fade comes back to that level; if they move it while break music is faded out, their level is
kept rather than overwritten. A fade that cannot reach Spotify's level lets the command through
unfaded, and the console says so once.

The plugin also nudges Spotify when it ends a track without starting the next, a stall Spotify is
prone to: when playback stops near the end of a track, or sits at 0:00 just after a track change, and
the host did not ask for it, it presses play, then skips if play did not take. That is at most twice
a minute. It needs a backend that is told when Spotify moves, so it does nothing on Linux, and it
cannot see Spotify's queue, so a playlist that genuinely ran out is nudged as well.

## Settings

| Section | Setting | Default | |
|---|---|---|---|
| Break music | Playlist | blank | A Spotify link or URI. Blank resumes whatever Spotify already has loaded. |
| Break music | Shuffle the playlist | on | Left to Spotify's own setting on Windows. |
| Break music | Launch Spotify if it is not already running | on | |
| Break music | Fade length in milliseconds | 1500 | 0 turns fading off. |
| Bug fixes | Nudge Spotify when it ends a track without starting the next | on | macOS and Windows only. |

The Playlist field takes what "Copy link to playlist" puts on the clipboard
(`https://open.spotify.com/playlist/…?si=…`) as well as the `spotify:playlist:…` form; the `si`
share token is dropped. Albums, artists and `spotify:collection:…` links work too. A **single track
is refused** — a bed that ends after one song is not a bed. Anything else is refused with a warning
on the Plugins page, and the bed falls back to resuming whatever Spotify has loaded.

## Platforms

| | macOS | Windows | Linux |
|---|---|---|---|
| Backend | AppleScript | Spotify's media session (media keys as fallback) | MPRIS over `gdbus` |
| Start / pause / resume / stop | yes | yes | yes |
| Skip | yes | yes | yes |
| Reads state and track back | yes | yes | yes |
| Told when Spotify moves by itself | yes | yes | no, asked |
| Fades | yes | yes | no |
| Choose the playlist | yes | via the `spotify:` URI, see below | yes |
| Shuffle | yes | left to Spotify's own setting | yes |
| Release zip | portable | `-win` | portable |

**macOS** — Spotify.app ships an AppleScript dictionary with discrete `play`, `pause` and
`next track` commands, so every command lands exactly. Every script is wrapped in an
`if application "Spotify" is running` guard, because naming an app inside a `tell` block launches it
— an unguarded pause would start Spotify in order to pause it.

macOS will ask once for permission to control Spotify (**System Settings → Privacy & Security →
Automation**). Until that is granted every command fails with Apple event error `-1743`, which the
log calls out by name. In development the permission attaches to whatever binary is running KHost,
so it is re-asked after switching between `dotnet run` and a published build.

**Windows** has no scripting interface, so the plugin drives Spotify's own row on the system media
transport (`Windows.Media.Control`) — the same one the volume flyout shows — with discrete play,
pause, stop and next, and reads the state and track from it. Picking Spotify's row by its app id
means another player holding media focus does not get in the way. That row appears only once
Spotify has played something; until then the global media keys are the fallback, and they reach
whichever app owns media focus. This is the one limitation the Plugins page states for Windows.

Loading a playlist on Windows means opening its `spotify:` URI. That shows the playlist in Spotify,
but whether playback moves to it is Spotify's call: with a track already loaded, Spotify has been
seen to resume that track instead.

The media session needs the WinRT projection, which the host does not carry, so the Windows build
ships as its own `-win` zip with `Microsoft.Windows.SDK.NET.dll` and `WinRT.Runtime.dll` beside the
plugin. The portable zip still loads on Windows, but drives Spotify by media keys alone and reads
nothing back.

**Linux** talks MPRIS, which is a good fit — discrete `Play`, `Pause`, `Stop`, `Next` and an
`OpenUri` for the playlist. `gdbus` is shelled out to rather than taking a D-Bus client
dependency, since a plugin's dependencies get copied into the host's plugin folder and glib ships
`gdbus` on any desktop that has Spotify.

The Plugins page states a backend's limitation once at startup; macOS has none to state, and Linux
states that it does not fade.

## Building

The plugin builds against the published contracts, `KHost.Abstractions` and `KHost.Common` 0.30.0,
as NuGet packages. Until they are on nuget.org they come from the local feed KHost's
`./build/pack-contracts.sh` fills (see KHost's AGENTS.md, **The published contracts**).

```bash
dotnet build src/KHost.Plugins.Spotify
dotnet test tests/KHost.Plugins.Spotify.Tests
```

It targets `net10.0` and `net10.0-windows10.0.19041.0`; the second exists only for the Windows media
session.

## Installing

From a KHost host: **Plugins → Available**, once the release is in the plugin catalog. The catalog
offers the `-win` zip on Windows and the portable one elsewhere.

By hand: unzip the release that matches the machine into its own folder under KHost's `plugins/`
directory, enable it on KHost's Plugins page, and restart KHost. The zip carries `manifest.json`,
the entry dll and its `.deps.json` (plus the two WinRT dlls in the `-win` zip), and never a copy of the KHost contract assemblies. Then pick **Spotify** as the venue's
break-music mode.
