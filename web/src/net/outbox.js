// Client id, command sequence and the outbox of unacknowledged commands (DESIGN §g.4, R3, R4, S15).
// Entries are persisted to sessionStorage so a reload resends them with the same (cid, q) and the
// server's cursor dedupes. When storage fails the tab keeps going in memory with a fresh cid.

import { EPHEMERAL_KINDS } from './protocol.gen.js';

export const OUTBOX_CAP = 200;
export const EPHEMERAL_MAX_AGE_MS = 10_000;
export const CID_CHECK_MS = 100;

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
 * Entries are `{r, q, k, d, pid, sentAt, sent}`: `sentAt` is when the user acted, `sent` whether the
 * entry ever went out under its current `q`. Rooms keep references to entry objects, so a renumbered
 * entry stays the same object.
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
      // A duplicated tab copied sessionStorage: the cid and the outbox belong to the original.
      mint({ clear: true });
      save();
      changed();
    }
  });

  let ready = Promise.resolve();
  if (restored && channel) {
    checking = true;
    channel.postMessage({ t: 'cid?', cid });
    ready = new Promise((done) => setTimer(() => {
      checking = false;
      done();
    }, CID_CHECK_MS));
  }

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

    onChange(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },

    /** Returns the new entry, or null when the outbox is full ("offline too long"). */
    add(r, k, d) {
      if (entries.length >= OUTBOX_CAP) {
        return null;
      }
      const entry = { r, q: ++cseq, k, d, pid: owner, sentAt: now(), sent: false };
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

    // A fresh q for an entry the server has not applied, so later commands cannot overtake it into a dup.
    renumber(entry) {
      entry.q = ++cseq;
      entry.sent = false;
      entries.sort((a, b) => a.q - b.q);
      save();
    },

    /**
     * snap/replay carry the persisted cursor. cseq never falls behind it (R4), and an entry that never
     * went out but whose q the server has already seen (storage regressed) gets a fresh q.
     */
    sync(r, lastQ) {
      cseq = Math.max(cseq, lastQ);
      for (const entry of entries) {
        if (entry.r === r && !entry.sent && entry.q <= lastQ) {
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

    /**
     * A new cid after 4409 (keep: entries resend as they are) or cid_reuse (renumber: the server
     * already holds another tab's commands under the old (cid, q) pairs).
     */
    remint({ renumber = false } = {}) {
      mint({ clear: false });
      if (renumber) {
        for (const entry of entries) {
          entry.q = ++cseq;
          entry.sent = false;
        }
        entries.sort((a, b) => a.q - b.q);
      }
      save();
    },
  };
}
