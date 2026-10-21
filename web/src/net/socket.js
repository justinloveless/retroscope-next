// The tab's one WebSocket (DESIGN §c.3, §g.4): hello/welcome, clock pings, reconnect with backoff,
// a last-resort silence watchdog, and routing of room frames to their RoomStores.

import { CLOSE, PROTOCOL_VERSION } from './protocol.gen.js';
import { createOutbox, TWIN_RECHECK_MS } from './outbox.js';
import { createRoom } from './room.js';
import { createClock } from '../core/clock.js';
import { createStore } from '../core/store.js';

export const BACKOFF_BASE_MS = 250;
export const BACKOFF_MAX_MS = 10_000;
export const BACKOFF_RESET_MS = 30_000;
export const PING_EVERY_MS = 60_000;
export const WATCHDOG_MS = 90_000;

const OPEN = 1;
const TERMINAL = new Set([CLOSE.UNAUTHENTICATED, CLOSE.BAD_ORIGIN]);

/** Full jitter: uniform in [0, min(10 s, 250 ms × 2ⁿ)). */
export function backoffDelay(attempt, random = Math.random) {
  return random() * Math.min(BACKOFF_MAX_MS, BACKOFF_BASE_MS * 2 ** attempt);
}

function defaultUrl() {
  return `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws`;
}

function defaultTimer(fn, ms) {
  const id = setTimeout(fn, ms);
  return () => clearTimeout(id);
}

/**
 * `status` is a store of `{state, offlineSince, code}`: state is `connecting`, `open` (welcomed),
 * `waiting` (a reconnect is scheduled) or `closed` (stop(), 4401 or 4403; `code` says which).
 * `offlineSince` is when the tab last lost its connection, null while open.
 */
export function createSocket({
  url = defaultUrl(),
  pid = null,
  outbox = createOutbox({ pid }),
  clock = createClock(),
  WebSocketImpl = globalThis.WebSocket,
  now = () => Date.now(),
  random = Math.random,
  setTimer = defaultTimer,
  win = globalThis,
  doc = globalThis.document,
  onNack = () => {},
} = {}) {
  const rooms = new Map();
  const leaving = new Set();
  const status = createStore({ state: 'connecting', offlineSince: now(), code: null });
  let ws = null;
  let connId = 0;
  let helloSent = false;
  let welcomed = false;
  let me = null;
  let attempt = 0;
  let stopped = false;
  let byeWait = false;
  let cancelReconnect = null;
  let cancelReset = null;
  let cancelPing = null;
  let cancelWatchdog = null;

  function visible() {
    return doc?.visibilityState !== 'hidden';
  }

  function setStatus(state, code = null) {
    const prev = status.get();
    const offlineSince = state === 'open' ? null : (prev.offlineSince ?? now());
    status.set({ state, offlineSince, code });
  }

  function sendFrame(frame) {
    if (!helloSent || ws?.readyState !== OPEN) {
      return false;
    }
    ws.send(JSON.stringify(frame));
    return true;
  }

  function ping() {
    sendFrame({ t: 'ping', c: now() });
  }

  function armWatchdog() {
    cancelWatchdog?.();
    cancelWatchdog = null;
    // Hidden tabs send no clock pings, so silence there proves nothing; the server's protocol-level
    // PING/PONG keeps the socket alive (R6).
    if (ws !== null && visible()) {
      cancelWatchdog = setTimer(() => {
        cancelWatchdog = null;
        lost();
      }, WATCHDOG_MS);
    }
  }

  function schedulePing() {
    cancelPing = setTimer(() => {
      if (visible()) {
        ping();
      }
      schedulePing();
    }, PING_EVERY_MS);
  }

  function clearTimers() {
    for (const cancel of [cancelReset, cancelPing, cancelWatchdog]) {
      cancel?.();
    }
    cancelReset = cancelPing = cancelWatchdog = null;
  }

  function detach() {
    const old = ws;
    ws = null;
    helloSent = false;
    welcomed = false;
    clearTimers();
    for (const room of rooms.values()) {
      room.disconnected();
    }
    if (old) {
      old.onopen = old.onmessage = old.onclose = old.onerror = null;
      try {
        old.close(1000);
      } catch {
        // Already closing.
      }
    }
  }

  function connect() {
    if (stopped) {
      return;
    }
    cancelReconnect?.();
    cancelReconnect = null;
    byeWait = false;
    detach();
    setStatus('connecting');
    const socket = new WebSocketImpl(url);
    connId++;
    ws = socket;
    socket.onopen = async () => {
      await outbox.ready;
      if (ws !== socket || socket.readyState !== OPEN) {
        return;
      }
      const subs = [...rooms.values()].map((room) => room.resume());
      socket.send(JSON.stringify({ t: 'hello', v: PROTOCOL_VERSION, cid: outbox.cid, subs }));
      helloSent = true;
      armWatchdog();
    };
    socket.onmessage = (event) => {
      if (ws === socket) {
        armWatchdog();
        receive(event.data);
      }
    };
    socket.onclose = (event) => {
      if (ws === socket) {
        closed(event.code);
      }
    };
  }

  function scheduleReconnect(delay) {
    detach();
    setStatus('waiting');
    cancelReconnect = setTimer(() => {
      cancelReconnect = null;
      connect();
    }, delay);
  }

  function lost() {
    scheduleReconnect(backoffDelay(attempt++, random));
  }

  // WebKit reports the server's 1012 as 1005, so a restart is recognised by its bye frame, and a
  // close without one is handled like any other drop.
  function closed(code) {
    if (TERMINAL.has(code)) {
      detach();
      stopped = true;
      setStatus('closed', code);
      return;
    }
    if (code === CLOSE.CID_IN_USE) {
      cidInUse();
      return;
    }
    lost();
  }

  // 4409: a live socket holds this cid. A twin that answers owns the cid and the outbox, and this tab
  // starts empty under a new cid. Otherwise the holder is this tab's own dead socket, which the server's
  // keep-alive reaps: the cid and every q are kept, so commands that committed come back dup.
  function cidInUse() {
    detach();
    setStatus('waiting');
    const socket = connId;
    outbox.checkTwin(TWIN_RECHECK_MS).then(() => {
      if (!stopped && ws === null && cancelReconnect === null && connId === socket) {
        lost();
      }
    });
  }

  function onWelcome(frame) {
    me = frame.me;
    if (outbox.bindPrincipal(me.pid)) {
      // The hello went out under the previous principal's cid.
      connect();
      return;
    }
    welcomed = true;
    clock.seed(frame.n);
    clock.checkStep();
    ping();
    schedulePing();
    cancelReset = setTimer(() => {
      cancelReset = null;
      attempt = 0;
    }, BACKOFF_RESET_MS);
    setStatus('open');
  }

  function onBye(frame) {
    scheduleReconnect(frame.retryMs);
    byeWait = true;
  }

  function receive(text) {
    let frame;
    try {
      frame = JSON.parse(text);
    } catch {
      return;
    }
    switch (frame?.t) {
      case 'welcome':
        onWelcome(frame);
        return;
      case 'pong':
        clock.sample(frame.c, frame.n, now());
        return;
      case 'bye':
        onBye(frame);
        return;
    }
    const room = rooms.get(frame.r);
    if (!room) {
      return;
    }
    room.handle(frame);
    if (frame.t === 'kick') {
      rooms.delete(frame.r);
      leaving.delete(room);
    }
  }

  function wake() {
    if (stopped) {
      return;
    }
    if (welcomed) {
      clock.checkStep();
      ping();
      armWatchdog();
    } else if (cancelReconnect !== null && !byeWait) {
      connect();
    }
  }

  function onVisibility() {
    if (visible()) {
      wake();
    } else {
      cancelWatchdog?.();
      cancelWatchdog = null;
    }
  }

  const net = {
    send: sendFrame,
    online: () => welcomed,
    conn: () => connId,
    setTimer,
    cidReuse() {
      outbox.checkTwin(TWIN_RECHECK_MS).then((yielded) => {
        if (yielded && !stopped) {
          connect();
        }
      });
    },
    drained(room) {
      if (leaving.has(room)) {
        unsubscribe(room);
      }
    },
  };

  function unsubscribe(room) {
    leaving.delete(room);
    rooms.delete(room.key);
    room.dispose();
    sendFrame({ t: 'unsub', r: room.key });
  }

  return {
    status,
    outbox,
    clock,
    get me() {
      return me;
    },

    start() {
      if (ws === null && cancelReconnect === null && !stopped) {
        win?.addEventListener?.('online', wake);
        doc?.addEventListener?.('visibilitychange', onVisibility);
        connect();
      }
    },

    stop() {
      stopped = true;
      cancelReconnect?.();
      cancelReconnect = null;
      detach();
      win?.removeEventListener?.('online', wake);
      doc?.removeEventListener?.('visibilitychange', onVisibility);
      setStatus('closed');
    },

    /** For a page that saw the wall clock jump (sleep, a manual change): drop the clock samples and ping (§g.5). */
    resync() {
      clock.discard();
      wake();
    },

    /** The RoomStore for `key`, subscribed now or with the next hello. */
    room(key, model, options = {}) {
      const existing = rooms.get(key);
      if (existing) {
        leaving.delete(existing);
        return existing;
      }
      const room = createRoom(key, model, { outbox, net, onNack, ...options });
      rooms.set(key, room);
      sendFrame({ t: 'sub', ...room.resume() });
      return room;
    },

    /** Unsubscribes once the room's outbox entries are acked or nacked, so leaving a page loses nothing. */
    leave(room) {
      if (rooms.get(room.key) !== room) {
        return;
      }
      if (room.pending().length === 0) {
        unsubscribe(room);
      } else {
        leaving.add(room);
      }
    },
  };
}
