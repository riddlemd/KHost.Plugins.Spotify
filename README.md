# KHost.Plugins.Spotify

Break-music provider for [KHost](../KHost). Puts the Spotify desktop app on between singers, and
takes it off again when one starts.

This plugin **drives Spotify; it does not carry its audio**. The sound comes out of Spotify's own
output, where the host cannot route it, mix it, or send it to a Cast device — so
`RendersThroughHost` is false. There is no API key, no OAuth, and no Spotify Premium requirement:
everything goes through the app already running on the machine.

## What it does, and what it deliberately does not

It sends five commands and no more: start, pause, resume, stop and skip. **Nothing is ever read
back out of Spotify**, and **nothing sets its volume.**

Two visible consequences. The console shows break music as playing but does not name the track —
Spotify's own window is where a host sees what is on. And the venue volume does not reach Spotify:
its level is set in Spotify, by whoever is running the room. KHost asks every provider it cannot
mix to take the venue level, and this one declines rather than move a slider out from under them.

That also means **the suspend fade is ignored**. KHost asks for a two second fade before it loads
a song, and waits on it. Fading here would mean ramping Spotify's volume a step at a time, each
step its own process — which measured nearly five seconds of dead air with the singer stood
there. Spotify is stopped at once instead.

## Settings

| Setting | Default | |
|---|---|---|
| Playlist | blank | A Spotify link or URI. Blank resumes whatever Spotify already has loaded. |
| Shuffle | on | |
| Launch Spotify if it is not already running | on | |

The Playlist field takes what "Copy link to playlist" puts on the clipboard
(`https://open.spotify.com/playlist/…?si=…`) as well as the `spotify:playlist:…` form; the `si`
share token is dropped. Albums and artists work too. A **single track is refused** — a bed that
ends after one song is not a bed, and nothing here reads Spotify back to notice that it stopped.
Anything else is refused with a warning on the Plugins page, and the bed falls back to resuming
whatever Spotify has loaded.

## Platforms

| | macOS | Windows | Linux |
|---|---|---|---|
| Backend | AppleScript | media keys | MPRIS over `gdbus` |
| Start / pause / resume / stop | yes | toggle-based, see below | yes |
| Skip | yes | yes | yes |
| Choose the playlist | yes | via the `spotify:` URI | yes |
| Shuffle | yes | left to Spotify's own setting | yes |

**macOS** is the only backend with a complete surface. Spotify.app ships an AppleScript dictionary
with discrete `play`, `pause` and `next track` commands, so every command lands exactly and
nothing has to track what Spotify is currently doing. Every script is wrapped in an
`if application "Spotify" is running` guard,
because naming an app inside a `tell` block launches it — an unguarded pause would start Spotify
in order to pause it.

macOS will ask once for permission to control Spotify (**System Settings → Privacy & Security →
Automation**). Until that is granted every command fails with Apple event error `-1743`, which the
log calls out by name. In development the permission attaches to whatever binary is running KHost,
so it is re-asked after switching between `dotnet run` and a published build.

**Windows** has no scripting interface, only the global media keys. Two things follow. The keys go
to whichever app owns media focus — normally Spotify, but not guaranteed on a machine running
another player. And Windows exposes a play/pause **toggle** with no discrete play or pause, so the
backend tracks what it last commanded in order to know whether pressing it would land the right
way up; a host who pauses in Spotify's own window puts that record out of step until the next
start.

**Linux** talks MPRIS, which is a good fit — discrete `Play`, `Pause`, `Stop`, `Next` and an
`OpenUri` for the playlist. `gdbus` is shelled out to rather than taking a D-Bus client
dependency, since a plugin's dependencies get copied into the host's plugin folder and glib ships
`gdbus` on any desktop that has Spotify.

The Plugins page states each backend's limitation once at startup; macOS and Linux have none to
state.

## Building

Requires a sibling checkout of the KHost repo (the plugin compiles against `KHost.Plugins.Sdk` by
project reference until the Sdk ships as a NuGet package):

```
~/Developer/riddlemd/
  KHost/
  KHost.Plugins.Spotify/
```

```bash
dotnet build KHost.Plugins.Spotify.slnx
dotnet test tests/KHost.Plugins.Spotify.Tests
```

Building also drops the plugin into the sibling KHost checkout's runtime plugins folder
(`src/KHost.UserInterface/bin/Debug/net10.0/plugins/khost.spotify/`) when it exists.

## Installing

Copy the build output (entry dll, `manifest.json`, and dependency dlls) into a folder under
KHost's `plugins/` directory, enable it on KHost's Plugins settings page, and restart KHost. Then
pick **Spotify** as the venue's break-music provider.
