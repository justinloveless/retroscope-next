// Client id, command sequence and the outbox of unacknowledged commands (DESIGN §g.4, R3, R4, S15).
// Entries are persisted to sessionStorage so a reload resends them with the same (cid, q) and the
// server's cursor dedupes. When storage fails the tab keeps going in memory with a fresh cid.

import { EPHEMERAL_KINDS } from './protocol.gen.js';
import { supersedeKey } from './supersede.js';

export const OUTBOX_CAP = 200;
export const EPHEMERAL_MAX_AGE_MS = 10_000;
export const CID_CHECK_MS = 100;
export const TWIN_RECHECK_MS = 1000;

const CID_KEY = 'rs.cid';
const OUTBOX_KEY = 'rs.outbox';
const EPHEMERAL = new Set(EPHEMERAL_KINDS);

function defaultStorage() {
  try {
    return globalThis.sessionStorage ?? null;
  } catch {
    return null;
  }
}

function defaultChannel() {
  return typeof BroadcastChannel === 'function' ? new BroadcastChannel('rs') : null;
}

export function isEphemeral(kind) {
  return EPHEMERAL.has(kind);
}

/**
 * Entries are `{r, q, k, d, pid, sentAt, sent, key, superseded, retry}`: `sentAt` is when the user acted,
 * `sent` whether the entry ever went out under its current `q`, `key` its supersede key, `superseded`
 * whether a later entry of the room has the same key, and `retry` whether it awaits a resend after a
 * retryable nack. Rooms keep references to entry objects, so a renumbered entry stays the same object.
 *
 * Entries restored from sessionStorage may have been sent and committed since that copy was written (by
 * this tab before a reload, or by a duplicate of it), so they keep their q: the server's cursor answers
 * a committed one `dup`. Only entries this page created before their room first synced, whose q may come
 * from a cseq that storage let fall behind the cursor, are renumbered at sync.
 */
export function createOutbox({
  storage = defaultStorage(),
  channel = defaultChannel(),
  pid = null,
  now = () => Date.now(),
  uuid = () => crypto.randomUUID(),
  setTimer = (fn, ms) => setTimeout(fn, ms),
} = {}) {
  let persistent = storage !== null;
  let cid = null;
  let owner = null;
  let cseq = 0;
  let entries = [];
  let checking = false;
  const early = new WeakSet();
  const listeners = new Set();

  function changed() {
    for (const listener of [...listeners]) {
      listener();
    }
  }

  function goMemory() {
    if (!persistent) {
      return;
    }
    persistent = false;
    try {
      storage.removeItem(OUTBOX_KEY);
      storage.removeItem(CID_KEY);
    } catch {
      // Nothing more can be done; a reload starts from whatever is left, under a new cid.
    }
  }

  function save() {
    if (!persistent) {
      return;
    }
    try {
      storage.setItem(CID_KEY, JSON.stringify({ cid, pid: owner }));
      storage.setItem(OUTBOX_KEY, JSON.stringify({ cseq, entries }));
    } catch {
      goMemory();
    }
  }

  function load() {
    if (!persistent) {
      return false;
    }
    try {
      const id = JSON.parse(storage.getItem(CID_KEY) ?? 'null');
      const box = JSON.parse(storage.getItem(OUTBOX_KEY) ?? 'null');
      if (typeof id?.cid !== 'string') {
        return false;
      }
      cid = id.cid;
      owner = id.pid ?? null;
      cseq = Number.isSafeInteger(box?.cseq) ? box.cseq : 0;
      entries = Array.isArray(box?.entries) ? box.entries.filter((e) => e && typeof e.r === 'string' && Number.isSafeInteger(e.q)) : [];
      return true;
    } catch {
      goMemory();
      return false;
    }
  }

  function mint({ clear }) {
    cid = uuid();
    if (clear) {
      entries = [];
    }
  }

  const restored = load();
  if (!restored) {
    mint({ clear: true });
  }
  if (pid !== null && owner !== null && owner !== pid) {
    mint({ clear: true });
  }
  owner = pid ?? owner;
  save();

  channel?.addEventListener('message', (event) => {
    const msg = event.data;
    if (msg?.cid !== cid) {
      return;
    }
    if (msg.t === 'cid?' && !checking) {
      channel.postMessage({ t: 'cid!', cid });
    } else if (msg.t === 'cid!' && checking) {
      // A live twin holds this cid: a duplicated tab copied sessionStorage, and the cid and the outbox
      // belong to the tab it copied.
      mint({ clear: true });
      save();
      changed();
    }
  });

  /** Asks other tabs for this cid and waits `ms`; resolves true when a twin answered and this tab yielded. */
  function checkTwin(ms) {
    if (!channel || checking) {
      return Promise.resolve(false);
    }
    const before = cid;
    checking = true;
    channel.postMessage({ t: 'cid?', cid });
    return new Promise((done) => setTimer(() => {
      checking = false;
      done(cid !== before);
    }, ms));
  }

  const ready = restored ? checkTwin(CID_CHECK_MS).then(() => {}) : Promise.resolve();

  return {
    get cid() {
      return cid;
    },
    get pid() {
      return owner;
    },
    get cseq() {
      return cseq;
    },
    get persistent() {
      return persistent;
    },
    get size() {
      return entries.length;
    },
    ready,
    checkTwin,

    onChange(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },

    /**
     * Returns the new entry, or null when the outbox is full ("offline too long"). `beforeSync`: the room
     * has not synced since the page loaded, so cseq may still be behind its cursor.
     */
    add(r, k, d, { beforeSync = false } = {}) {
      if (entries.length >= OUTBOX_CAP) {
        return null;
      }
      const key = supersedeKey(k, d);
      if (key !== null) {
        for (const older of entries) {
          if (older.r === r && older.key === key) {
            older.superseded = true;
          }
        }
      }
      const entry = { r, q: ++cseq, k, d, pid: owner, sentAt: now(), sent: false, key, superseded: false, retry: false };
      if (beforeSync) {
        early.add(entry);
      }
      entries.push(entry);
      save();
      return entry;
    },

    entries(r) {
      return entries.filter((e) => e.r === r);
    },

    find(r, q) {
      return entries.find((e) => e.r === r && e.q === q) ?? null;
    },

    remove(entry) {
      const before = entries.length;
      entries = entries.filter((e) => e !== entry);
      if (entries.length !== before) {
        save();
      }
    },

    removeRoom(r) {
      const gone = entries.filter((e) => e.r === r);
      if (gone.length > 0) {
        entries = entries.filter((e) => e.r !== r);
        save();
      }
      return gone;
    },

    markSent(entry) {
      if (!entry.sent) {
        entry.sent = true;
        save();
      }
    },

    // A retryable nack keeps the q: the server refuses every later command of the room on that
    // connection until the room is subscribed again, so nothing behind it commits first.
    markRetry(entry) {
      entry.retry = true;
      save();
    },

    // cid_reuse: another intent of this cid holds the entry's q, and this one was not applied.
    renumber(entry) {
      entry.q = ++cseq;
      entry.sent = false;
      entry.retry = true;
      entries.sort((a, b) => a.q - b.q);
      save();
    },

    /**
     * snap/replay carry the persisted cursor. cseq never falls behind it (R4), and an entry created
     * before the room first synced that never went out but whose q the server has already seen (storage
     * regressed) gets a fresh q.
     */
    sync(r, lastQ) {
      cseq = Math.max(cseq, lastQ);
      for (const entry of entries) {
        if (entry.r === r && early.has(entry) && !entry.sent && entry.q <= lastQ) {
          entry.q = ++cseq;
        }
      }
      entries.sort((a, b) => a.q - b.q);
      save();
    },

    /** Drops ephemeral entries of room r created more than 10 s ago (R5); returns them. */
    dropStaleEphemeral(r) {
      const cutoff = now() - EPHEMERAL_MAX_AGE_MS;
      const gone = entries.filter((e) => e.r === r && isEphemeral(e.k) && e.sentAt < cutoff);
      if (gone.length > 0) {
        entries = entries.filter((e) => !gone.includes(e));
        save();
      }
      return gone;
    },

    /** welcome.me.pid; another principal's cid and outbox are discarded (S15). Returns true when they were. */
    bindPrincipal(nextPid) {
      if (owner === nextPid) {
        return false;
      }
      const switched = owner !== null;
      if (switched) {
        mint({ clear: true });
      }
      owner = nextPid;
      for (const entry of entries) {
        entry.pid ??= nextPid;
      }
      save();
      if (switched) {
        changed();
      }
      return switched;
    },
  };
}
