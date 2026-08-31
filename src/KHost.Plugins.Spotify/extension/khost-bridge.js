// KHost bridge for Spotify, as a Spicetify extension.
//
// Spotify's transport surface offers no volume on Windows at all, and on macOS only one process
// spawn per step — about 100ms each, which is why fading was dropped the first time. From inside
// the client a ramp is a loop, so this is where the fade lives; KHost drives it over a loopback
// socket and falls back to the platform backend whenever this is not attached.
//
// Install: copy to the Spicetify extensions folder, then
//     spicetify config extensions khost-bridge.js
//     spicetify backup apply
// The KHost Plugins page reports the path and whether this is attached.

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
    Spicetify.Player.setVolume(to);
    send({ type: 'faded', to });
  }

  async function fade(to, ms) {
    // Silent when superseded: the plugin takes the first acknowledgement as the answer to what it
    // last asked, so one for a level the room never settled at unblocks the wrong caller.
    if (await ramp(to, ms)) send({ type: 'faded', to });
  }

  // The ramp itself, with no message on the end: the three commands that use it each have their
  // own thing to say once it lands. False when a newer command took over part way, which the
  // caller has to honour — its own work is as superseded as the writes were.
  async function ramp(to, ms) {
    cancelFade();
    const mine = fadeToken;

    const from = Spicetify.Player.getVolume();
    if (ms === 0 || Math.abs(to - from) < 0.005) {
      Spicetify.Player.setVolume(to);
      return true;
    }

    // ~60fps, capped: a fade is heard, not watched, and past this the steps cost more than they add.
    const steps = Math.max(1, Math.min(120, Math.round(ms / 16)));

    for (let i = 1; i <= steps; i++) {
      if (mine !== fadeToken) return false;
      Spicetify.Player.setVolume(from + (to - from) * (i / steps));
      await new Promise((r) => setTimeout(r, ms / steps));
    }

    return true;
  }

  // Faded out and paused as one act. Remembering the level here, before the ramp, is what makes
  // the fade back in land on what the room was actually set to.
  async function pauseWithFadeOut(ms) {
    const from = Spicetify.Player.getVolume();
    if (from > 0.005) previous = from;

    // Nothing after this point if a newer command took over: pausing would stop playback the
    // newer command never asked to interrupt, at whatever level it had just set, while claiming
    // the room had reached silence.
    if (!await ramp(0, ms)) return;

    if (Spicetify.Player.isPlaying()) Spicetify.Player.pause();

    send({ type: 'faded', to: 0, paused: true });
  }

  // Silent before it plays, or the first instant arrives at full level and the fade is decoration.
  async function playWithFadeIn(ms) {
    Spicetify.Player.setVolume(0);

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
    fade: (ask, ms) => { const to = level(ask.to); if (to !== null) fade(to, ms); },
    volume: (ask) => { const to = level(ask.to); if (to !== null) setLevel(to); },
  });

  Spicetify.Player.addEventListener('onplaypause', report);
  Spicetify.Player.addEventListener('songchange', report);

  connect();
})();
