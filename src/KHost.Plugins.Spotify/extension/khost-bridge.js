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

      if (ask.type === 'configure') return configure(ask);

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
      // Milliseconds. The host cannot tell a stall from a hand on the pause button without them.
      progressMs: readPlayer('getProgress'),
      durationMs: readPlayer('getDuration'),
    });
  }

  const number = (v) => (typeof v === 'number' && Number.isFinite(v) ? v : null);

  // Every player reading goes through this. These arrived later than the rest of the API, and a
  // Spicetify without them must cost the stall recovery only: report() runs on every player event,
  // so letting it throw would take the state the host fades on with it.
  function readPlayer(method) {
    try {
      return number(Spicetify.Player[method]());
    } catch (e) {
      // Absent as well as throwing: calling undefined lands here too, which is the whole point.
      return null;
    }
  }

  // How close to the end counts as "the track ran out" rather than somebody pausing near it.
  const STALL_END_MS = 2000;

  // A track loaded but sitting at zero is the other face of the same bug.
  const STALL_START_MS = 250;

  // The nudge is a workaround for somebody else's bug, so it is bounded: past this it stops and
  // lets the room fall silent rather than fighting whatever is really wrong.
  const STALL_MAX_NUDGES = 2;
  const STALL_WINDOW_MS = 60000;
  const STALL_CONFIRM_MS = 1000;

  // Set while the host's own pauseWithFadeOut is running, and left set until the host plays again:
  // resuming a pause the host asked for is worse than the bug this recovers from.
  let pausedByHost = false;

  let nudges = [];
  let songChangedAt = 0;

  // The venue's answer, sent by the host on every attach. Defaults on to match the shipped
  // setting, so an extension that attaches before being told behaves the way the box is ticked.
  let recoverStalls = true;

  // Spotify sometimes ends a track without starting the next: playback stops outright, or the next
  // loads and sits at 0:00. Both look the same from here — paused, nobody asked, and the playhead
  // at one end of the track or the other.
  function looksLikeAStall(progressMs, durationMs, sinceSongChangeMs) {
    if (!recoverStalls || pausedByHost) return false;
    if (typeof progressMs !== 'number' || typeof durationMs !== 'number' || durationMs <= 0) return false;

    if (durationMs - progressMs <= STALL_END_MS) return true;

    // Only just after a track change: a host who pauses a track they have just started is at the
    // beginning of it too, and that is theirs to do.
    return progressMs <= STALL_START_MS && sinceSongChangeMs <= STALL_CONFIRM_MS;
  }

  // Bounded per window rather than per track: a fault that returns every time would otherwise be
  // nudged all night, and the log would say it recovered each time.
  function mayNudge(now) {
    nudges = nudges.filter((at) => now - at < STALL_WINDOW_MS);

    return nudges.length < STALL_MAX_NUDGES;
  }

  // Nothing queued behind it is a queue that ended, or Autoplay off, and both are correct
  // behaviour. Unknown counts as nothing: guessing wrong here restarts music a room turned off.
  function hasSomethingNext() {
    const data = Spicetify.Player.data || {};
    const next = data.nextTracks || (data.queue && data.queue.nextTracks);

    return Array.isArray(next) && next.length > 0;
  }

  async function recoverFromStall() {
    const now = Date.now();

    if (!mayNudge(now) || !hasSomethingNext()) return;

    nudges.push(now);

    // Play first: it is the lighter of the two, and for the sits-at-0:00 face of the bug it is
    // the whole fix. Skipping straight to next would lose a track nobody has heard.
    Spicetify.Player.play();

    await new Promise((r) => setTimeout(r, STALL_CONFIRM_MS));

    if (Spicetify.Player.isPlaying()) return send({ type: 'recovered', how: 'play' });

    if (typeof Spicetify.Player.next !== 'function') return send({ type: 'recovered', how: 'failed' });

    Spicetify.Player.next();

    await new Promise((r) => setTimeout(r, STALL_CONFIRM_MS));

    send({ type: 'recovered', how: Spicetify.Player.isPlaying() ? 'next' : 'failed' });
  }

  function onPlayPause() {
    report();

    if (Spicetify.Player.isPlaying()) {
      // Whatever started it — a fade in, the host's backend, or a hand on the keyboard — the
      // room is playing again and the host's pause is over.
      pausedByHost = false;

      return;
    }

    if (looksLikeAStall(readPlayer('getProgress'), readPlayer('getDuration'), Date.now() - songChangedAt)) {
      recoverFromStall();
    }
  }

  function onSongChange() {
    songChangedAt = Date.now();
    report();
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
    // Set before the fade, not after it: the host has asked to stop as of now, and a track that
    // ends during a five second fade would otherwise read as a stall and be nudged back to life
    // over the top of it.
    pausedByHost = true;

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
    pausedByHost = false;

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

  // Outside COMMANDS, because that table is gated on the player being ready and this touches no
  // player: a host who turned recovery off must be honoured even while the client is still waking.
  function configure(ask) {
    if (typeof ask.recoverStalls === 'boolean') recoverStalls = ask.recoverStalls;
  }

  // Attaches the half of this that needs a working player. Runs once, whenever the player turns
  // up, which may be before the socket, after it, or never.
  function startDriving(volume) {
    ready = true;
    previous = ours = volume;

    Spicetify.Player.addEventListener('onplaypause', onPlayPause);
    Spicetify.Player.addEventListener('songchange', onSongChange);

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
