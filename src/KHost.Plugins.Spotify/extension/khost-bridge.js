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

  // How long the player is given before its silence is called a fault rather than a slow start.
  // Generous enough for Spotify finishing its own boot, and deliberately shorter than the grace
  // the host gives the bridge (SpicetifyBridgeSetup.GracePeriod) — the host asks for a verdict
  // when that grace runs out, and a wait longer than it would have nothing to answer with.
  const PLAYER_WAIT_MS = 15000;
  const PLAYER_POLL_MS = 250;

  let socket = null;
  let backoff = RECONNECT_MIN;
  let fadeToken = 0;

  // Whether the player can actually be driven. Everything below the socket depends on it; the
  // socket itself deliberately does not.
  let ready = false;

  // What went wrong while waiting, as the host will be asked to explain it to somebody.
  let waitedMs = 0;
  let lastError = '';

  // The level to come back up to. Held here rather than read back before each fade: Spotify rounds
  // what it reports, so restoring a reading walks the room's level down a point every time.
  let previous = 1;

  // The level this extension last wrote. Anything else the player reports is the host's own hand
  // on Spotify's slider, which is the only place the room's level is ever really set.
  let ours = 1;

  // Read rather than assumed, and the one call that says whether the player is up: getVolume is a
  // function on the player well before the player can serve it, and calling it early throws.
  function readVolume() {
    try {
      const v = window.Spicetify && Spicetify.Player && Spicetify.Player.getVolume();

      lastError = '';

      return typeof v === 'number' && isFinite(v) ? v : null;
    } catch (e) {
      lastError = String((e && e.message) || e).slice(0, 120);

      return null;
    }
  }

  // Everything the host needs to explain a bridge that is attached and cannot work. Sent on every
  // connection and again the moment the answer changes, so a host that started first still learns
  // it — and sent from in here because this is the only code on the inside of the client, which is
  // what makes the explanation the same on every operating system.
  function sendDiagnosis() {
    const S = window.Spicetify;

    send({
      type: 'diagnosis',
      ready: ready,
      waitedMs: waitedMs,
      spicetify: !!S,
      player: !!(S && S.Player),
      // Spicetify's API arrives as a populated Platform. An empty one that stays empty is the
      // signature of a Spicetify older than the Spotify it patched: it patches without error and
      // then never binds, so every extension loads and none of them can do anything.
      platformKeys: (S && S.Platform) ? Object.keys(S.Platform).length : -1,
      error: lastError || null,
    });
  }

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

    // Both: the level is the room's to come back to, and it is now the last level known to be
    // real, which is what a ramp starts from.
    if (Math.abs(now - ours) > 0.005) previous = ours = now;
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
      sendDiagnosis();

      if (ready) report();
    };

    socket.onmessage = (event) => {
      let ask;
      try { ask = JSON.parse(event.data); } catch (e) { return; }

      const run = COMMANDS[ask.type];

      // Anything unrecognised is dropped rather than answered: a newer plugin talking to an older
      // extension is a version pair a host can end up with, and it should degrade quietly.
      if (!run) return;

      // A command arriving before the player is up is answered rather than attempted. The host
      // gates on the diagnosis and should not be sending one, but a race at startup is cheap to
      // absorb and a thrown command would leave whoever asked waiting for an acknowledgement.
      if (!ready) return sendDiagnosis();

      run(ask, Math.max(0, ask.ms | 0));
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

    // What we last wrote, not what the player reports: a read taken straight after a write comes
    // back with the level Spotify has yet to apply, so a fade in from silence saw the level it had
    // just left and either skipped the ramp or ran it from the wrong end. The host moving the
    // slider is picked up by noteHostLevel instead, which is the only thing that can tell.
    const from = ours;

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

  // Attaches the half of this that needs a working player. Runs once, whenever the player turns
  // up — which may be before the socket, after it, or never.
  function startDriving(volume) {
    ready = true;
    previous = ours = volume;

    Spicetify.Player.addEventListener('onplaypause', report);
    Spicetify.Player.addEventListener('songchange', report);

    sendDiagnosis();
    report();
  }

  // First, and outside the wait below. The socket is the only way anything in here can be
  // explained, so it is never behind the thing that might be broken — this extension used to open
  // it only once the player answered, so a player that never answered left the host watching a
  // port nothing ever connected to, with no way to tell that from an unpatched Spotify.
  connect();

  (function waitForPlayer() {
    const volume = readVolume();

    if (volume !== null) return startDriving(volume);

    waitedMs += PLAYER_POLL_MS;

    // Said once, at the point the wait becomes a verdict. Before that it is a slow start, and
    // after it nothing changes by saying so again every quarter second.
    if (waitedMs === PLAYER_WAIT_MS) sendDiagnosis();

    setTimeout(waitForPlayer, PLAYER_POLL_MS);
  })();
})();
