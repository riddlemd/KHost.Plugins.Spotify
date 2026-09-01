// KHost bridge for Spotify, as a Spicetify extension.
//
// Spotify's transport surface offers no volume on Windows at all, and on macOS only one process
// spawn per step — about 100ms each, which is why fading was dropped the first time. From inside
// the client a ramp is a loop, so this is where the fade lives; KHost drives it over a loopback
// socket and falls back to the platform backend whenever this is not attached.
//
// The plugin installs and registers this itself when it finds Spicetify, so a host installs
// Spicetify and nothing else. Editing this copy does nothing: the plugin overwrites it whenever
// the file beside the assembly differs from the one Spicetify holds.

(function KHostBridge() {
  const PORT = Number(localStorage.getItem('khost.bridge.port')) || 8974;
  const RECONNECT_MIN = 1000;
  const RECONNECT_MAX = 15000;

  // Spicetify injects extensions before its own API is ready, so every extension waits. Polled
  // rather than hooked because there is no event for it.
  //
  // Ready means able to answer, not merely defined: getVolume is a function on the player well
  // before the player can serve it, and calling it early throws. That throw killed this extension
  // outright — it never reached connect(), and the host saw only a bridge nothing attached to.
  let startingVolume = null;
  try { startingVolume = Spicetify.Player && Spicetify.Player.getVolume(); } catch (e) { /* not yet */ }

  if (!window.Spicetify || !Spicetify.Player || typeof startingVolume !== 'number') {
    setTimeout(KHostBridge, 250);
    return;
  }

  let socket = null;
  let backoff = RECONNECT_MIN;
  let fadeToken = 0;

  // The level to come back up to. Held here rather than read back before each fade: Spotify rounds
  // what it reports, so restoring a reading walks the room's level down a point every time.
  let previous = startingVolume;

  // The level this extension last wrote. Anything else the player reports is the host's own hand
  // on Spotify's slider, which is the only place the room's level is ever really set.
  let ours = startingVolume;

  function write(to) {
    ours = to;
    Spicetify.Player.setVolume(to);
  }

  // Adopts a level the host set themselves. Compared against what we wrote rather than against
  // zero: a fade leaves levels of its own behind, and taking one of those makes the room come back
  // to a point part way up a ramp, or to the silence a fade out ended on. Loose, because the level
  // Spotify reports back is a rounding of the one it was given.
  function noteHostLevel() {
    const now = Spicetify.Player.getVolume();
    if (Math.abs(now - ours) > 0.005) previous = now;
  }

  // Null when the level is not a number to begin with. Dropped rather than substituted, because
  // Math.max(0, undefined) is NaN, and a NaN reaching the player also becomes the level every
  // later fade in comes back to.
  const level = (v) => (typeof v === 'number' && Number.isFinite(v) ? Math.min(1, Math.max(0, v)) : null);

  function connect() {
    try {
      socket = new WebSocket('ws://127.0.0.1:' + PORT + '/khost');
    } catch (e) {
      return retry();
    }

    socket.onopen = () => {
      backoff = RECONNECT_MIN;
      report();
    };

    socket.onmessage = (event) => {
      let ask;
      try { ask = JSON.parse(event.data); } catch (e) { return; }

      const run = COMMANDS[ask.type];

      // Anything unrecognised is dropped rather than answered: a newer plugin talking to an older
      // extension is a version pair a host can end up with, and it should degrade quietly.
      if (run) run(ask, Math.max(0, ask.ms | 0));
    };

    // Both, because a socket can fail either way and KHost has to see the gap and fall back.
    socket.onerror = () => { try { socket.close(); } catch (e) {} };
    socket.onclose = () => { socket = null; retry(); };
  }

  function retry() {
    setTimeout(connect, backoff);
    backoff = Math.min(RECONNECT_MAX, backoff * 2);
  }

  function send(message) {
    if (socket && socket.readyState === 1) {
      try { socket.send(JSON.stringify(message)); } catch (e) { /* closing */ }
    }
  }

  function report() {
    const track = Spicetify.Player.data && (Spicetify.Player.data.item || Spicetify.Player.data.track);
    const meta = (track && track.metadata) || {};

    send({
      type: 'state',
      playing: Spicetify.Player.isPlaying(),
      title: meta.title || (track && track.name) || null,
      artist: meta.artist_name || null,
      volume: Spicetify.Player.getVolume(),
    });
  }

  // A newer fade supersedes an older one rather than fighting it: two ramps setting the volume in
  // turn is what makes a fade stutter.
  function cancelFade() { fadeToken++; }

  // An explicit level is the room's level, so it is also what a later fade in comes back to.
  function setLevel(to) {
    cancelFade();
    previous = to;
    write(to);
    send({ type: 'faded', to });
  }

  // Remembers the level being left, so whatever comes back up lands on what the room was set to.
  async function fadeOut(ms) {
    noteHostLevel();

    return ramp(0, ms);
  }

  // The two halves with no transport on the end of them: going quiet before the backend loads a
  // playlist or ends a session, and coming back once it has. Neither carries a level — out is
  // always to silence, and back is always to what silence was taken from.
  //
  // Silent when superseded: the plugin takes the first acknowledgement as the answer to what it
  // last asked, so one for a level the room never settled at unblocks the wrong caller.
  async function silence(ms) {
    if (await fadeOut(ms)) send({ type: 'faded', to: 0 });
  }

  async function restore(ms) {
    if (await ramp(previous, ms)) send({ type: 'faded', to: previous });
  }

  // The ramp itself, with no message on the end: each command that uses it has its own thing to
  // say once it lands. False when a newer command took over part way, which the
  // caller has to honour — its own work is as superseded as the writes were.
  async function ramp(to, ms) {
    cancelFade();
    const mine = fadeToken;

    const from = Spicetify.Player.getVolume();
    if (ms === 0 || Math.abs(to - from) < 0.005) {
      write(to);
      return true;
    }

    // ~60fps, capped: a fade is heard, not watched, and past this the steps cost more than they add.
    const steps = Math.max(1, Math.min(120, Math.round(ms / 16)));

    for (let i = 1; i <= steps; i++) {
      if (mine !== fadeToken) return false;
      write(from + (to - from) * (i / steps));
      await new Promise((r) => setTimeout(r, ms / steps));
    }

    return true;
  }

  // Faded out and paused as one act.
  async function pauseWithFadeOut(ms) {
    // Nothing after this point if a newer command took over: pausing would stop playback the
    // newer command never asked to interrupt, at whatever level it had just set, while claiming
    // the room had reached silence.
    if (!await fadeOut(ms)) return;

    if (Spicetify.Player.isPlaying()) Spicetify.Player.pause();

    send({ type: 'faded', to: 0, paused: true });
  }

  // Silent before it plays, or the first instant arrives at full level and the fade is decoration.
  async function playWithFadeIn(ms) {
    // Before the silence, or the reading is one we just wrote. A host who turned Spotify up while
    // it sat paused has set the room's level, and pressing play must come up to it.
    noteHostLevel();

    write(0);

    if (!Spicetify.Player.isPlaying()) Spicetify.Player.play();

    if (!await ramp(previous, ms)) return;

    send({ type: 'faded', to: previous, playing: true });
  }

  // A table rather than a chain of comparisons: what each command takes sits beside its name, and
  // adding one is a line. Null-prototyped so a message naming 'constructor' or 'toString' finds
  // nothing — the port is loopback, but anything on the machine can reach it.
  const COMMANDS = Object.assign(Object.create(null), {
    pauseWithFadeOut: (ask, ms) => pauseWithFadeOut(ms),
    playWithFadeIn: (ask, ms) => playWithFadeIn(ms),
    silence: (ask, ms) => silence(ms),
    restore: (ask, ms) => restore(ms),
    volume: (ask) => { const to = level(ask.to); if (to !== null) setLevel(to); },
  });

  Spicetify.Player.addEventListener('onplaypause', report);
  Spicetify.Player.addEventListener('songchange', report);

  connect();
})();
