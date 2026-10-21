// Deterministic stand-ins for timers, sessionStorage, BroadcastChannel and the browser WebSocket.

export function fakeTimers(start = 1_000_000) {
  let t = start;
  let nextId = 1;
  const pending = new Map();
  const timers = {
    now: () => t,
    setTimer(fn, ms) {
      const id = nextId++;
      pending.set(id, { at: t + ms, fn });
      return () => pending.delete(id);
    },
    // Runs every timer due within ms, in due order, then sets the clock to the end.
    advance(ms) {
      const end = t + ms;
      for (;;) {
        let due = null;
        for (const [id, timer] of pending) {
          if (timer.at <= end && (due === null || timer.at < due[1].at)) {
            due = [id, timer];
          }
        }
        if (due === null) {
          break;
        }
        pending.delete(due[0]);
        t = Math.max(t, due[1].at);
        due[1].fn();
      }
      t = end;
    },
    delays() {
      return [...pending.values()].map((timer) => timer.at - t);
    },
  };
  return timers;
}

export function memoryStorage(initial = {}) {
  const data = new Map(Object.entries(initial));
  return {
    data,
    failWrites: false,
    getItem: (key) => (data.has(key) ? data.get(key) : null),
    setItem(key, value) {
      if (this.failWrites) {
        throw new Error('QuotaExceededError');
      }
      data.set(key, String(value));
    },
    removeItem: (key) => data.delete(key),
  };
}

export function brokenStorage() {
  const fail = () => {
    throw new Error('SecurityError');
  };
  return { getItem: fail, setItem: fail, removeItem: fail };
}

// Channels created from one hub see each other's messages (not their own), synchronously.
export function channelHub() {
  const members = new Set();
  return function open() {
    const listeners = new Set();
    const channel = {
      addEventListener: (type, fn) => listeners.add(fn),
      postMessage(data) {
        for (const other of members) {
          if (other !== channel) {
            for (const fn of other.listeners) {
              fn({ data: structuredClone(data) });
            }
          }
        }
      },
      listeners,
    };
    members.add(channel);
    return channel;
  };
}

export function uuidSequence(prefix = 'c') {
  let n = 0;
  return () => `${prefix}${++n}`;
}

export function fakeWebSocketClass() {
  const sockets = [];
  class FakeWebSocket {
    constructor(url) {
      this.url = url;
      this.readyState = 0;
      this.sent = [];
      this.closedWith = null;
      sockets.push(this);
    }

    send(text) {
      if (this.readyState !== 1) {
        throw new Error('send on a socket that is not open');
      }
      this.sent.push(JSON.parse(text));
    }

    close(code = 1000) {
      this.closedWith = code;
      this.readyState = 3;
    }

    // Test side.
    async open() {
      this.readyState = 1;
      await this.onopen?.();
    }

    receive(frame) {
      this.onmessage?.({ data: JSON.stringify(frame) });
    }

    serverClose(code) {
      this.readyState = 3;
      this.onclose?.({ code });
    }

    frames(t) {
      return this.sent.filter((f) => f.t === t);
    }
  }
  return { FakeWebSocket, sockets, last: () => sockets.at(-1) };
}

export function fakeDocument() {
  const listeners = new Set();
  return {
    visibilityState: 'visible',
    addEventListener: (type, fn) => listeners.add(fn),
    removeEventListener: (type, fn) => listeners.delete(fn),
    setVisibility(state) {
      this.visibilityState = state;
      for (const fn of listeners) {
        fn();
      }
    },
  };
}

export function fakeWindow() {
  const listeners = new Set();
  return {
    addEventListener: (type, fn) => listeners.add(fn),
    removeEventListener: (type, fn) => listeners.delete(fn),
    goOnline() {
      for (const fn of listeners) {
        fn();
      }
    },
  };
}

// A list room: events push or remove items; intents do the same optimistically.
export const listModel = {
  snapshot: (d, o) => Object.freeze({ items: Object.freeze([...d.items]), overlay: o ?? null }),
  apply(state, ev) {
    if (ev.k === 'test.added') {
      return Object.freeze({ ...state, items: Object.freeze([...state.items, ev.d.item]) });
    }
    if (ev.k === 'test.order') {
      return Object.freeze({ ...state, items: Object.freeze([...ev.d.order]) });
    }
    return state;
  },
  optimistic(state, k, d) {
    return k === 'retro.card.add' ? listModel.apply(state, { k: 'test.added', d: { item: d.body } }) : state;
  },
};

export const syncSchedule = (run) => run();

export const ROOM = 'board:00000000-0000-4000-8000-0000000000b1';
export const OTHER_ROOM = 'poker:00000000-0000-4000-8000-0000000000c1';
export const PID = '00000000-0000-4000-8000-000000000001';
export const OTHER_PID = '00000000-0000-4000-8000-000000000002';
