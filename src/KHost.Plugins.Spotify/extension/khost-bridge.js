// KHost bridge for Spotify, as a Spicetify extension: runs inside the client, the only place a
// smooth volume ramp exists. Editing this copy does nothing; the plugin overwrites it on mismatch.

(function KHostBridge() {
  const PORT = Number(localStorage.getItem('khost.bridge.port')) || 8974;
  const RECONNECT_MIN = 1000;
  const RECONNECT_MAX = 15000;

  // How long before silence is called a fault, not a slow start. Deliberately shorter than the
  // host's own grace (SpicetifyBridgeSetup.GracePeriod), so it has an answer when that runs out.
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

  // Explains a bridge that is attached and cannot work. Sent on every connection and again the
  // moment the answer changes, so a host that started first still learns it.
  function sendDiagnosis() {
    const S = window.Spicetify;

    send({
      type: 'diagnosis',
      ready: ready,
      waitedMs: waitedMs,
      spicetify: !!S,
      player: !!(S && S.Player),
      // An empty Platform that stays empty signals a Spicetify older than the Spotify it patched:
      // it patches without error and never binds, so every extension loads and can do nothing.
      platformKeys: (S && S.Platform) ? Object.keys(S.Platform).length : -1,
      error: lastError || null,
    });
  }

  function write(to) {
    ours = to;
    Spicetify.Player.setVolume(to);
  }

  // Adopts a level the host set themselves. Compared against what we wrote, not zero: a fade
  // leaves levels of its own behind, and Spotify's reported level is only a rounding of that.
  function noteHostLevel() {
    const now = Spicetify.Player.getVolume();

    // Both: the level is the room's to come back to, and it is now the last level known to be
    // real, which is what a ramp starts from.
    if (Math.abs(now - ours) > 0.005) previous = ours = now;
  }

  // Null when not a number. Dropped rather than substituted: Math.max(0, undefined) is NaN, and
  // a NaN reaching the player becomes the level every later fade in comes back to.
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

      // Answered rather than attempted: the host should not send one before the diagnosis says
      // ready, but a startup race is cheap to absorb versus leaving the caller with no acknowledgement.
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

  // Neither half carries a level: out is always to silence, back always to what silence left.
  // Silent when superseded, so a stale acknowledgement cannot unblock the wrong caller.
  async function silence(ms) {
    if (await fadeOut(ms)) send({ type: 'faded', to: 0 });
  }

  async function restore(ms) {
    if (await ramp(previous, ms)) send({ type: 'faded', to: previous });
  }

  // The ramp itself, with no message on the end: each caller has its own thing to say once it
  // lands. False when a newer command took over part way, which the caller must honour.
  async function ramp(to, ms) {
    cancelFade();
    const mine = fadeToken;

    // What we last wrote, not what the player reports: a read taken right after a write returns
    // the level Spotify has yet to apply. The host moving the slider is caught by noteHostLevel instead.
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
    // Nothing after this point if a newer command took over: pausing would stop the playback it
    // never asked to interrupt, while claiming the room had reached silence.
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

  // A table, not a chain of comparisons: adding a command is a line. Null-prototyped so a message
  // naming 'constructor' or 'toString' finds nothing, since anything on the machine can reach this.
  const COMMANDS = Object.assign(Object.create(null), {
    pauseWithFadeOut: (ask, ms) => pauseWithFadeOut(ms),
    playWithFadeIn: (ask, ms) => playWithFadeIn(ms),
    silence: (ask, ms) => silence(ms),
    restore: (ask, ms) => restore(ms),
    volume: (ask) => { const to = level(ask.to); if (to !== null) setLevel(to); },
  });

  // Attaches the half of this that needs a working player. Runs once, whenever the player turns
  // up, which may be before the socket, after it, or never.
  function startDriving(volume) {
    ready = true;
    previous = ours = volume;

    Spicetify.Player.addEventListener('onplaypause', report);
    Spicetify.Player.addEventListener('songchange', report);

    sendDiagnosis();
    report();
  }

  // First, and outside the wait below: the socket is the only way anything in here can be
  // explained, so it must never be behind the thing that might be broken.
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
