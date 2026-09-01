// Node's own test runner, no dependency: this ships inside a plugin repo, so it stays plain.
// Run directly with `node --test tests/extension/khost-bridge.test.mjs`, or via the xUnit wrapper
// in KHost.Plugins.Spotify.Tests, which shells out to exactly that.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadExtension, connectExtension, lastFaded } from './harness.mjs';

// ── each command reaches its handler ────────────────────────────────────────────────

test('a volume command sets the level directly, with no ramp, and acknowledges', async (t) => {
  const ext = connectExtension({ volume: 0.5 });
  t.after(() => ext.dispose());

  ext.socket.receive('{"type":"volume","to":0.8}');

  assert.deepEqual(ext.player.setCalls, [0.8]);
  assert.deepEqual(lastFaded(ext), { type: 'faded', to: 0.8 });
});

test('a silence command ramps down to 0 over time, then acknowledges', async (t) => {
  const ext = connectExtension({ volume: 0.5 });
  t.after(() => ext.dispose());

  ext.socket.receive('{"type":"silence","ms":32}');
  // The first step runs synchronously (an async function runs up to its first await), so the ramp
  // is already under way before the handler even returns.
  assert.equal(ext.player.setCalls.length, 1);
  assert.notEqual(ext.player.volume, 0);

  await ext.clock.drain();

  assert.equal(ext.player.volume, 0);
  assert.deepEqual(lastFaded(ext), { type: 'faded', to: 0 });
});

// ── silence/restore: the room's own level, not one this end guessed ────────────────

test('silence then restore is a round trip back to the level the room was set to, not to full', async (t) => {
  // The pair a break start and a break stop are built out of. Sending an explicit level for the
  // way back is what put a venue at 100% on the first break: nothing outside Spotify knows what
  // the slider was on, and the plugin had nothing to send but a guess.
  const ext = connectExtension({ volume: 0.4, playing: true });
  t.after(() => ext.dispose());

  // Moved in Spotify's own window, which is where a venue sets its level and where nothing tells
  // this extension about it. Read at the last moment for exactly that reason.
  ext.player.volume = 0.55;

  ext.socket.receive('{"type":"silence","ms":0}');
  await ext.clock.drain();
  assert.equal(ext.player.volume, 0);
  assert.equal(ext.player.playing, true, 'silence is a level, not a transport command');

  ext.socket.receive('{"type":"restore","ms":32}');
  await ext.clock.drain();

  assert.equal(ext.player.volume, 0.55);
  assert.deepEqual(lastFaded(ext), { type: 'faded', to: 0.55 });
});

test('silence does not take an already-silent level as the one to come back to', async (t) => {
  // A stop straight after a pause is the case: the fade out already took the room to zero, and
  // reading that as the level to return to leaves break music silent from then on.
  const ext = connectExtension({ volume: 0.42, playing: true });
  t.after(() => ext.dispose());

  ext.socket.receive('{"type":"pauseWithFadeOut","ms":16}');
  await ext.clock.drain();

  ext.socket.receive('{"type":"silence","ms":16}');
  await ext.clock.drain();

  ext.socket.receive('{"type":"restore","ms":16}');
  await ext.clock.drain();

  assert.equal(ext.player.volume, 0.42);
});

// ── pauseWithFadeOut: ramps to exactly 0, pauses only after, then acknowledges ──────

test('pauseWithFadeOut ramps down to exactly 0 while still playing, and pauses only once the ramp lands', async (t) => {
  const ext = connectExtension({ volume: 0.6, playing: true });
  t.after(() => ext.dispose());

  ext.socket.receive('{"type":"pauseWithFadeOut","ms":32}'); // 32ms -> 2 steps
  assert.equal(ext.player.playing, true, 'still playing after the first step');
  assert.ok(ext.player.volume > 0 && ext.player.volume < 0.6, 'ramping, not jumped to 0');

  ext.clock.fireOldest();
  await ext.clock.tick();
  assert.equal(ext.player.volume, 0, 'the ramp itself reaches exactly 0');
  assert.equal(ext.player.playing, true, 'not paused yet — pausing happens after the ramp, not as soon as the level matches');

  ext.clock.fireOldest();
  await ext.clock.tick();
  assert.equal(ext.player.playing, false);
  assert.deepEqual(lastFaded(ext), { type: 'faded', to: 0, paused: true });
});

test('pauseWithFadeOut does not pause a track that was already stopped', async (t) => {
  const ext = connectExtension({ volume: 0.6, playing: false });
  t.after(() => ext.dispose());

  ext.socket.receive('{"type":"pauseWithFadeOut","ms":16}');
  await ext.clock.drain();

  assert.equal(ext.player.volume, 0);
  assert.deepEqual(lastFaded(ext), { type: 'faded', to: 0, paused: true });
});

// ── playWithFadeIn: silent, then playing, then ramps to the remembered level ───────

test('playWithFadeIn sets silence and plays immediately, then ramps up — not to a level the command carries, since it sends none', async (t) => {
  const ext = connectExtension({ volume: 0.5, playing: false });
  t.after(() => ext.dispose());

  ext.socket.receive('{"type":"volume","to":0.73}');
  ext.player.setCalls.length = 0;

  ext.socket.receive('{"type":"playWithFadeIn","ms":32}');
  // Both effects, and the first ramp step, happen synchronously before the handler returns.
  assert.equal(ext.player.setCalls[0], 0, 'silent first');
  assert.equal(ext.player.playing, true, 'played immediately, not after the ramp');
  assert.ok(ext.player.setCalls[1] > 0 && ext.player.setCalls[1] < 0.73, 'ramping up, not jumped to target');

  await ext.clock.drain();

  assert.equal(ext.player.volume, 0.73, 'lands on the level "volume" set earlier, never sent in this command');
  assert.deepEqual(lastFaded(ext), { type: 'faded', to: 0.73, playing: true });
});

test('pauseWithFadeOut then playWithFadeIn is a round trip back to the exact original volume', async (t) => {
  const ext = connectExtension({ volume: 0.42, playing: true });
  t.after(() => ext.dispose());

  ext.socket.receive('{"type":"pauseWithFadeOut","ms":32}');
  await ext.clock.drain();
  assert.equal(ext.player.volume, 0);
  assert.equal(ext.player.playing, false);

  ext.socket.receive('{"type":"playWithFadeIn","ms":32}');
  await ext.clock.drain();

  assert.equal(ext.player.volume, 0.42, 'exact — Spotify never got asked to report a rounded reading back');
  assert.equal(ext.player.playing, true);
  assert.deepEqual(lastFaded(ext), { type: 'faded', to: 0.42, playing: true });
});

// ── the host's own hand on Spotify's slider ────────────────────────────────────────

test('a level the host raised in Spotify itself is the one play comes up to, not the stale one', async (t) => {
  // The reported case: the extension attached while Spotify happened to be muted, so the level it
  // had to come back to was zero. Raising the volume in Spotify's own window told it nothing, and
  // pressing play faded the room back down to silence.
  const ext = connectExtension({ volume: 0, playing: false });
  t.after(() => ext.dispose());

  ext.player.volume = 0.7;

  ext.socket.receive('{"type":"playWithFadeIn","ms":32}');
  await ext.clock.drain();

  assert.equal(ext.player.volume, 0.7);
  assert.equal(ext.player.playing, true);
  assert.deepEqual(lastFaded(ext), { type: 'faded', to: 0.7, playing: true });
});

test('a pause part way through a fade in does not take the level it interrupted as the room\'s', async (t) => {
  // Every level on a ramp is one this extension wrote, and adopting one walks the room down: a
  // singer cutting a fade in short would leave break music quieter for the rest of the night.
  const ext = connectExtension({ volume: 0.8, playing: true });
  t.after(() => ext.dispose());

  ext.socket.receive('{"type":"pauseWithFadeOut","ms":16}');
  await ext.clock.drain();

  ext.socket.receive('{"type":"playWithFadeIn","ms":64}');   // 4 steps back up to 0.8
  assert.ok(ext.player.volume > 0 && ext.player.volume < 0.8, 'caught part way up');

  ext.socket.receive('{"type":"pauseWithFadeOut","ms":16}'); // cuts it short
  await ext.clock.drain();

  ext.socket.receive('{"type":"playWithFadeIn","ms":16}');
  await ext.clock.drain();

  assert.equal(ext.player.volume, 0.8);
});

test('a silence with no ramp to it is still a level we wrote, and is not adopted afterwards', async (t) => {
  // ms:0 is the start path: quiet before the backend loads a playlist, with nothing to hear. A
  // write that goes unrecorded reads back as the host having chosen silence, and everything after
  // it comes back to zero.
  const ext = connectExtension({ volume: 0.45, playing: true });
  t.after(() => ext.dispose());

  ext.socket.receive('{"type":"silence","ms":0}');
  await ext.clock.drain();
  assert.equal(ext.player.volume, 0);

  ext.socket.receive('{"type":"playWithFadeIn","ms":16}');
  await ext.clock.drain();

  assert.equal(ext.player.volume, 0.45);
});

test('a host who lowers Spotify while it plays is followed, not overridden on the next fade', async (t) => {
  const ext = connectExtension({ volume: 0.9, playing: true });
  t.after(() => ext.dispose());

  ext.player.volume = 0.25;

  ext.socket.receive('{"type":"pauseWithFadeOut","ms":16}');
  await ext.clock.drain();
  ext.socket.receive('{"type":"playWithFadeIn","ms":16}');
  await ext.clock.drain();

  assert.equal(ext.player.volume, 0.25);
});

test('a fade out after the host moved the slider starts from where the room actually is', async (t) => {
  const ext = connectExtension({ volume: 0.6, playing: true });
  t.after(() => ext.dispose());

  ext.player.volume = 0.2; // pulled down in Spotify's own window

  ext.socket.receive('{"type":"pauseWithFadeOut","ms":64}'); // 4 steps
  await ext.clock.drain();

  // A ramp that began at the level we merely remembered would jump the room up to it before
  // taking it down, which is the opposite of what a fade out is for.
  assert.ok(
    ext.player.setCalls.every((v) => v <= 0.2 + 1e-9),
    `stepped above the room's level: ${ext.player.setCalls}`,
  );
  assert.equal(ext.player.volume, 0);
});

test('a fade in ramps even when the player still reports the level it has not applied yet', async (t) => {
  // The real client reports the old level for a moment after setVolume. A ramp that reads its
  // starting point back off the player therefore sees the level it just left, and either skips
  // the fade entirely (when that equals the target) or runs it from the wrong end — which is a
  // fade in that audibly starts at full and drops.
  const ext = connectExtension({ volume: 0.6, playing: false });
  t.after(() => ext.dispose());

  ext.player.getVolume = () => 0.6;

  ext.socket.receive('{"type":"pauseWithFadeOut","ms":16}');
  await ext.clock.drain();
  assert.equal(ext.player.volume, 0);

  ext.player.setCalls.length = 0;
  ext.socket.receive('{"type":"playWithFadeIn","ms":64}');   // 4 steps up
  await ext.clock.drain();

  // The silence, then a step per frame — not one jump straight to the target.
  assert.ok(ext.player.setCalls.length >= 5, `expected a ramp, got ${ext.player.setCalls.length} writes`);
  assert.ok(ext.player.setCalls[1] > 0 && ext.player.setCalls[1] < 0.6, 'starts from the silence it wrote');
  assert.equal(ext.player.volume, 0.6);
});

// ── invalid input is dropped, not substituted ───────────────────────────────────────

const droppedCases = [
  ['volume with no "to"', '{"type":"volume","ms":10}'],
  ['volume with non-numeric "to"', '{"type":"volume","to":"loud"}'],
  ['volume with null "to"', '{"type":"volume","to":null}'],
  ['malformed JSON', '{"type":'],
  ['no "type" at all', '{"to":0.5}'],
  ['unknown "type"', '{"type":"bogus","to":0.5}'],
  // "1e400" is valid JSON number syntax that overflows to Infinity once parsed — a real way for a
  // non-finite value to arrive through valid JSON, unlike NaN/Infinity literals which JSON has no
  // syntax for at all.
  ['volume "to" that overflows to Infinity', '{"type":"volume","to":1e400}'],
];

for (const [name, text] of droppedCases) {
  test(`invalid input is dropped rather than substituted: ${name}`, async (t) => {
    const ext = connectExtension({ volume: 0.5 });
    t.after(() => ext.dispose());

    ext.socket.receive(text);
    await ext.clock.drain();

    assert.equal(ext.player.setCalls.length, 0);
    assert.equal(ext.player.volume, 0.5, 'unchanged, and in particular not NaN');
    assert.ok(!ext.sentMessages.some((m) => m.type === 'faded'));
  });
}

test('a missing "to" does not write NaN to the player (regression)', async (t) => {
  const ext = connectExtension({ volume: 0.5 });
  t.after(() => ext.dispose());

  ext.socket.receive('{"type":"volume"}');

  assert.ok(!Number.isNaN(ext.player.volume));
  assert.equal(ext.player.volume, 0.5);
});

// ── prototype-named types resolve to nothing (regression: null-prototyped dispatch table) ──

for (const type of ['constructor', '__proto__', 'toString']) {
  test(`a "${type}" type is not a command and does nothing`, async (t) => {
    const ext = connectExtension({ volume: 0.5 });
    t.after(() => ext.dispose());

    // The point of the regression: this must not throw and must not resolve to some inherited
    // Object.prototype member standing in for a handler.
    assert.doesNotThrow(() => ext.socket.receive(JSON.stringify({ type, to: 0.9, ms: 10 })));
    await ext.clock.drain();

    assert.equal(ext.player.setCalls.length, 0);
  });
}

// ── out-of-range levels clamp to 0..1 ───────────────────────────────────────────────

test('a level above 1 clamps to 1', async (t) => {
  const ext = connectExtension({ volume: 0.5 });
  t.after(() => ext.dispose());

  ext.socket.receive('{"type":"volume","to":9}');

  assert.deepEqual(ext.player.setCalls, [1]);
});

test('a level below 0 clamps to 0', async (t) => {
  const ext = connectExtension({ volume: 0.5 });
  t.after(() => ext.dispose());

  ext.socket.receive('{"type":"volume","to":-5}');

  assert.deepEqual(ext.player.setCalls, [0]);
});

// ── waiting for a player that is defined before it is ready ─────────────────────────

test('a player whose getVolume throws is waited for, not treated as ready', async (t) => {
  // The real client does exactly this. Taking defined for ready meant the very first read threw,
  // the extension died before it ever opened a socket, and the host saw a bridge nothing attached
  // to — with nothing anywhere saying why.
  const ext = loadExtension({ volume: 0.4, throwsUntil: 3 });
  t.after(() => ext.dispose());

  assert.equal(ext.sockets.length, 0);

  await ext.clock.drain();

  assert.equal(ext.sockets.length, 1);
});

test('the level a player finally reports is the one a fade in returns to', async (t) => {
  const ext = loadExtension({ volume: 0.4, throwsUntil: 2 });
  t.after(() => ext.dispose());

  await ext.clock.drain();
  ext.sockets[0].open();

  ext.socket.receive('{"type":"pauseWithFadeOut","ms":16}');
  await ext.clock.drain();
  ext.socket.receive('{"type":"playWithFadeIn","ms":16}');
  await ext.clock.drain();

  assert.equal(ext.player.volume, 0.4);
});

// ── a newer ramp supersedes one in flight ───────────────────────────────────────────

test('a newer fade supersedes one already ramping, rather than interleaving with it', async (t) => {
  const ext = connectExtension({ volume: 0.5 });
  t.after(() => ext.dispose());

  ext.socket.receive('{"type":"silence","ms":64}'); // 4 steps down from 0.5
  ext.socket.receive('{"type":"restore","ms":16}'); // 1 step, issued before the first ramp continues

  await ext.clock.drain();

  // Only two setVolume calls happen in total: the first ramp's own single synchronous step, then
  // the second ramp's step. The first ramp's fadeToken check aborts it before it ever writes
  // again, so nothing from it lands after the second command starts.
  assert.equal(ext.player.setCalls.length, 2);
  assert.equal(ext.player.setCalls.at(-1), 0.5);
  assert.equal(ext.player.volume, 0.5);

  // And only the surviving command acknowledges. The plugin takes the first acknowledgement as
  // the answer to what it last asked, so a stale one unblocks the wrong caller at a level the
  // room never settled at.
  const acks = ext.sentMessages.filter((m) => m.type === 'faded');
  assert.deepEqual(acks, [{ type: 'faded', to: 0.5 }]);
});

test('a superseded fade out does not pause playback the newer command never asked to stop', async (t) => {
  const ext = connectExtension({ volume: 0.9, playing: true });
  t.after(() => ext.dispose());

  ext.socket.receive('{"type":"pauseWithFadeOut","ms":64}');   // 4 steps
  ext.socket.receive('{"type":"volume","to":0.9}');            // takes over before it finishes

  await ext.clock.drain();

  // Pausing here would stop the room at the level the newer command had just set, and claim it
  // had reached silence on the way.
  assert.equal(ext.player.playing, true);
  assert.equal(ext.player.volume, 0.9);

  const paused = ext.sentMessages.filter((m) => m.paused);
  assert.deepEqual(paused, []);
});

test('a superseded fade in does not claim it reached the level it was aiming for', async (t) => {
  const ext = connectExtension({ volume: 0.8, playing: false });
  t.after(() => ext.dispose());

  ext.socket.receive('{"type":"pauseWithFadeOut","ms":16}');
  await ext.clock.drain();

  ext.sentMessages.length = 0;
  ext.socket.receive('{"type":"playWithFadeIn","ms":64}');     // 4 steps back up to 0.8
  ext.socket.receive('{"type":"volume","to":0.2}');            // takes over

  await ext.clock.drain();

  const acks = ext.sentMessages.filter((m) => m.type === 'faded');
  assert.deepEqual(acks, [{ type: 'faded', to: 0.2 }]);
});

// ── reconnect backoff exists and grows ──────────────────────────────────────────────

test('reconnect backoff doubles on each failure and is capped at 15s', async (t) => {
  const ext = loadExtension();
  t.after(() => ext.dispose());

  ext.socket.open(); // resets backoff to its floor before the failures start

  const expectedDelays = [1000, 2000, 4000, 8000, 15000, 15000];
  for (const ms of expectedDelays) {
    ext.sockets.at(-1).disconnect(); // onclose -> retry(): schedules the current backoff, then doubles it
    assert.equal(ext.clock.scheduled.at(-1), ms);

    ext.clock.fireOldest(); // runs connect(), producing the next socket to fail
    await ext.clock.tick();
  }
});

test('a connect failure before the socket ever opens also retries', async (t) => {
  const ext = loadExtension();
  t.after(() => ext.dispose());

  ext.socket.disconnect(); // never opened — a refused connection looks like this too

  assert.equal(ext.clock.scheduled.length, 1);
  assert.equal(ext.clock.scheduled[0], 1000);
});
