// Loads khost-bridge.js into a fully-stubbed browser shape so it can run under plain node.
//
// The extension is an IIFE that only ever touches window.Spicetify, localStorage, WebSocket and
// setTimeout, so those are the only seams this fakes. Each loadExtension() call evals a fresh copy
// of the source: the IIFE's own locals (socket, backoff, fadeToken, previous) become fresh closure
// state per call, so tests never share extension state with each other.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const SOURCE_PATH = path.resolve(__dirname, '../../src/KHost.Plugins.Spotify/extension/khost-bridge.js');
const SOURCE = fs.readFileSync(SOURCE_PATH, 'utf8');

const REAL_SET_TIMEOUT = globalThis.setTimeout;
const REAL_CLEAR_TIMEOUT = globalThis.clearTimeout;

// A controllable stand-in for setTimeout. The extension uses it for ramp steps and reconnect
// backoff; tests fast-forward both by firing queued callbacks directly instead of waiting.
function createClock() {
  const queue = [];
  const scheduled = []; // every delay ever requested, in order — what the backoff tests read

  function fakeSetTimeout(fn, ms) {
    scheduled.push(ms);
    queue.push(fn);
    return queue.length;
  }

  function fireOldest() {
    const fn = queue.shift();
    if (!fn) return false;
    fn();
    return true;
  }

  // setImmediate is a real macrotask and is never faked, so it reliably runs only after every
  // microtask the fired callback queued (including further chained awaits) has drained.
  function tick() {
    return new Promise((resolve) => setImmediate(resolve));
  }

  async function drain(limit = 10000) {
    let n = 0;
    while (queue.length && n++ < limit) {
      fireOldest();
      await tick();
    }
  }

  return {
    fakeSetTimeout,
    fakeClearTimeout: () => {},
    fireOldest,
    tick,
    drain,
    scheduled,
    get pendingCount() {
      return queue.length;
    },
  };
}

class FakeWebSocket {
  constructor(url, sentMessages) {
    this.url = url;
    this.readyState = 0; // CONNECTING, matching the real enum the extension checks against
    this.onopen = null;
    this.onmessage = null;
    this.onerror = null;
    this.onclose = null;
    this._sentMessages = sentMessages;
  }

  send(data) {
    // Mirrors the real WebSocket: a send while not open is a bug in the caller, not something to
    // swallow — khost-bridge.js guards every send() with a readyState check for this reason.
    if (this.readyState !== 1) throw new Error(`FakeWebSocket: send while readyState is ${this.readyState}`);
    this._sentMessages.push(JSON.parse(data));
  }

  close() {
    this.readyState = 3;
  }

  // Test-only: stand in for what the browser would do. The extension only ever observes
  // onopen/onmessage/onclose, so driving those directly is enough — no real socket needed.
  open() {
    this.readyState = 1;
    this.onopen?.();
  }

  receive(text) {
    this.onmessage?.({ data: text });
  }

  disconnect() {
    this.readyState = 3;
    this.onclose?.();
  }
}

/**
 * Loads a fresh instance of the extension with fully stubbed globals.
 *
 * @param {object} [options]
 * @param {number} [options.volume] initial Spicetify.Player volume
 * @param {boolean} [options.playing] initial Spicetify.Player playing state
 * @param {number|null} [options.port] value localStorage reports for 'khost.bridge.port'
 */
/// A real client's getVolume is a function on the player before the player can serve it, and
/// throws until it can. `throwsUntil` reproduces that: the extension has to keep waiting rather
/// than take defined for ready.
export function loadExtension({ volume = 0.5, playing = false, port = null, throwsUntil = 0 } = {}) {
  const player = {
    volume,
    playing,
    setCalls: [],
    getVolumeCalls: 0,
    getVolume() {
      if (player.getVolumeCalls++ < throwsUntil) throw new TypeError('player is not ready');
      return player.volume;
    },
    setVolume(v) {
      player.volume = v;
      player.setCalls.push(v);
    },
    isPlaying() {
      return player.playing;
    },
    play() {
      player.playing = true;
    },
    pause() {
      player.playing = false;
    },
    data: { item: { metadata: { title: 'Track', artist_name: 'Artist' } } },
    listeners: {},
    addEventListener(name, fn) {
      player.listeners[name] = fn;
    },
  };

  const sockets = [];
  const sentMessages = [];
  const clock = createClock();

  globalThis.window = globalThis;
  globalThis.localStorage = {
    getItem: (key) => (key === 'khost.bridge.port' && port !== null ? String(port) : null),
    setItem: () => {},
  };
  globalThis.Spicetify = { Player: player };
  globalThis.WebSocket = class extends FakeWebSocket {
    constructor(url) {
      super(url, sentMessages);
      sockets.push(this);
    }
  };
  globalThis.setTimeout = clock.fakeSetTimeout;
  globalThis.clearTimeout = clock.fakeClearTimeout;

  // The file is written to run as a plain browser script (no exports), so eval is how it reaches
  // the globals just assigned above.
  // eslint-disable-next-line no-eval
  eval(SOURCE);

  function dispose() {
    globalThis.setTimeout = REAL_SET_TIMEOUT;
    globalThis.clearTimeout = REAL_CLEAR_TIMEOUT;
  }

  return {
    player,
    sockets,
    get socket() {
      return sockets.at(-1);
    },
    sentMessages,
    clock,
    dispose,
  };
}

/** loadExtension() plus opening the socket, which is what nearly every test needs. */
export function connectExtension(options) {
  const ext = loadExtension(options);
  ext.socket.open();
  return ext;
}

export function lastFaded(ext) {
  return ext.sentMessages.filter((m) => m.type === 'faded').at(-1);
}
