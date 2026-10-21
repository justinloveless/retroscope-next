// RoomStore (DESIGN §g.4): confirmed state from snap/replay/tx, the room's outbox entries on top as
// optimistic intents, and the view derived from both. Entries are matched by q only.

import { isRetryable } from './protocol.gen.js';
import { createHub } from '../core/store.js';

export const RETRY_BASE_MS = 250;
export const RETRY_MAX_MS = 5000;

// What a `stale` nack means per kind. `rebase`: recompute the intent against fresh state and resend
// once. `silent`: the fresh value is already on screen. Anything else goes to the nack handlers.
export const STALE = Object.freeze({
  'poker.round.reorder': 'rebase',
  'retro.column.reorder': 'rebase',
  'poker.round.setTicket': 'silent',
  'poker.settings.update': 'silent',
  'retro.focus.set': 'silent',
  'retro.config.update': 'silent',
  'retro.board.rename': 'silent',
});

const identity = (state) => state;

export function retryDelay(attempt) {
  return Math.min(RETRY_MAX_MS, RETRY_BASE_MS * 2 ** attempt);
}

/**
 * `model` mirrors the C# projection: `snapshot(d, o)` builds state from a snap, `apply(state, ev)`
 * folds one wire event `{k, d}`, and the optional `optimistic(state, k, d)` re-derives an intent for
 * entries restored after a reload (they carry no `apply`).
 *
 * `net` is the socket's port: `{send(frame) → bool, online() → bool, conn() → id,
 * setTimer(fn, ms) → cancel, cidReuse(), drained(room)}`.
 *
 * A retryable nack keeps the entry's q. The server refuses every later command of the room on that
 * connection until the room is subscribed again, so the retry resubscribes first and resends after the
 * replay: no later command can commit ahead of it and turn the resend into a dup.
 *
 * `onNack(nack)` receives final nacks no per-send handler took (screens toast them), plus the
 * synthetic `{code: 'outbox_full'}` when a command is refused.
 */
export function createRoom(key, model, { outbox, net, onNack = () => {}, schedule } = {}) {
  let confirmed = null;
  let epoch = null;
  let seq = null;
  let synced = false;
  let everSynced = false;
  let resyncing = false;
  let kicked = null;
  let extras = {};
  let view = null;
  let dirty = true;
  let retryAttempt = 0;
  let cancelRetry = null;
  const intents = new WeakMap();
  const deferredAcks = new Set();

  const hub = createHub(read, schedule ? { schedule } : undefined);
  const stopWatching = outbox.onChange(changed);

  function entries() {
    return outbox.entries(key);
  }

  function read() {
    if (dirty) {
      dirty = false;
      view = confirmed === null ? null : entries().reduce((state, entry) => {
        const intent = intents.get(entry)?.apply ?? ((s) => (model.optimistic ?? identity)(s, entry.k, entry.d));
        return intent(state);
      }, confirmed);
    }
    return view;
  }

  function changed() {
    dirty = true;
    hub.notify();
  }

  function transmit(entry) {
    if (net.send({ t: 'cmd', r: key, q: entry.q, k: entry.k, d: entry.d })) {
      outbox.markSent(entry);
      intents.set(entry, { ...intents.get(entry), conn: net.conn() });
    }
  }

  // While a retry is pending new commands queue behind it, so q values reach the server in order (§c.3).
  function canSend() {
    return synced && !resyncing && kicked === null && cancelRetry === null && net.online();
  }

  function stopRetry() {
    cancelRetry?.();
    cancelRetry = null;
  }

  function drop(entry) {
    outbox.remove(entry);
    intents.delete(entry);
    changed();
    if (entries().length === 0) {
      net.drained?.(room);
    }
  }

  // Everything not yet on the wire of this connection, in q order (R3, R4). A retry that a later entry
  // superseded while it waited would reverse that entry's intent, so it is dropped instead (U28).
  function flush() {
    if (!canSend()) {
      return;
    }
    const conn = net.conn();
    for (const entry of entries()) {
      if (entry.retry && entry.superseded) {
        drop(entry);
      } else if (intents.get(entry)?.conn !== conn) {
        transmit(entry);
      }
    }
  }

  function scheduleRetry() {
    stopRetry();
    const delay = retryDelay(retryAttempt++);
    cancelRetry = net.setTimer(() => {
      cancelRetry = null;
      resubscribe();
    }, delay);
  }

  function settle(lastQ) {
    outbox.sync(key, lastQ);
    for (const entry of outbox.dropStaleEphemeral(key)) {
      intents.delete(entry);
    }
    for (const q of deferredAcks) {
      const entry = outbox.find(key, q);
      if (entry) {
        outbox.remove(entry);
      }
    }
    deferredAcks.clear();
    synced = true;
    everSynced = true;
    resyncing = false;
    stopRetry();
    changed();
    flush();
    if (entries().length === 0) {
      net.drained?.(room);
    }
  }

  function resubscribe() {
    resyncing = true;
    net.send({ t: 'sub', r: key, e: epoch, s: seq });
  }

  function applyTx(tx) {
    confirmed = tx.ev.reduce(model.apply, confirmed);
    seq = tx.s;
  }

  function onSnap(frame) {
    confirmed = model.snapshot(frame.d, frame.o);
    epoch = frame.e;
    seq = frame.s;
    settle(frame.lastQ);
  }

  function onReplay(frame) {
    if (frame.e !== epoch || seq === null) {
      resubscribe();
      return;
    }
    for (const tx of frame.txs) {
      if (tx.s <= seq) {
        continue;
      }
      if (tx.s !== seq + 1) {
        resubscribe();
        return;
      }
      applyTx(tx);
    }
    settle(frame.lastQ);
  }

  function onTx(frame) {
    if (resyncing || seq === null || frame.s <= seq) {
      return;
    }
    if (frame.s !== seq + 1) {
      resubscribe();
      return;
    }
    applyTx(frame);
    changed();
  }

  function onAck(frame) {
    if (resyncing) {
      deferredAcks.add(frame.q);
      return;
    }
    const entry = outbox.find(key, frame.q);
    if (!entry) {
      return;
    }
    outbox.remove(entry);
    intents.delete(entry);
    retryAttempt = 0;
    changed();
    if (entries().length === 0) {
      net.drained?.(room);
    }
  }

  function onNackFrame(frame) {
    const entry = outbox.find(key, frame.q);
    if (!entry) {
      return;
    }
    if (frame.code === 'cid_reuse') {
      // Another intent of this cid holds that q and this one was not applied: it goes out again under a
      // fresh q. Entries in flight behind it keep theirs, since they may have committed.
      outbox.renumber(entry);
      intents.set(entry, { ...intents.get(entry), conn: null });
      changed();
      scheduleRetry();
      net.cidReuse();
      return;
    }
    if (frame.retry || isRetryable(frame.code)) {
      if (entry.superseded) {
        drop(entry);
        return;
      }
      outbox.markRetry(entry);
      intents.set(entry, { ...intents.get(entry), conn: null });
      changed();
      scheduleRetry();
      return;
    }
    const intent = intents.get(entry);
    drop(entry);
    const nack = { code: frame.code, msg: frame.msg, d: frame.d, k: entry.k, sent: entry.d };
    if (frame.code === 'stale') {
      const policy = STALE[entry.k];
      if (policy === 'silent') {
        return;
      }
      if (policy === 'rebase' && intent?.rebase && !intent.rebased) {
        const next = intent.rebase(read(), nack);
        if (next) {
          send(entry.k, next, { apply: intent.applyFor?.(next), rebased: true, onNack: intent.onNack });
          return;
        }
      }
    }
    if (intent?.onNack?.(nack) === true) {
      return;
    }
    onNack(nack);
  }

  function onKick(frame) {
    kicked = frame.code;
    synced = false;
    stopRetry();
    outbox.removeRoom(key);
    changed();
  }

  function onX(frame) {
    const byKey = { ...extras[frame.k], [frame.d?.key ?? '']: frame.d };
    extras = { ...extras, [frame.k]: byKey };
    hub.notify();
  }

  /**
   * Queues command k and sends it as soon as the room is in sync. Options: `apply(state) → state`
   * (optimistic intent), `rebase(view, nack) → d | null` with `applyFor(d)` (stale reorders),
   * `onNack(nack) → true when handled` (merge prompts, composer drafts, "Take spotlight?").
   * Returns the entry, or null when the outbox is full.
   */
  function send(k, d, { apply, rebase, applyFor, onNack: handler, rebased = false } = {}) {
    if (kicked !== null) {
      return null;
    }
    const entry = outbox.add(key, k, d, { beforeSync: !everSynced });
    if (entry === null) {
      onNack({ code: 'outbox_full', k, sent: d });
      return null;
    }
    intents.set(entry, { apply, rebase, applyFor, onNack: handler, rebased, conn: null });
    changed();
    if (canSend()) {
      transmit(entry);
    }
    return entry;
  }

  const room = {
    key,
    get epoch() {
      return epoch;
    },
    get seq() {
      return seq;
    },
    get synced() {
      return synced && !resyncing;
    },
    get kicked() {
      return kicked;
    },
    get confirmed() {
      return confirmed;
    },
    get view() {
      return read();
    },
    get extras() {
      return extras;
    },
    pending() {
      return entries();
    },
    select: hub.select,
    flush: hub.flush,
    send,

    /** Paints from the boot JSON's snapshot before the socket is up (§c.3 boot snapshot). */
    seed({ e, s, d, o }) {
      confirmed = model.snapshot(d, o);
      epoch = e;
      seq = s;
      changed();
    },

    /** The `{r, e?, s?}` this room resumes from in hello or sub. */
    resume() {
      return epoch === null || seq === null ? { r: key } : { r: key, e: epoch, s: seq };
    },

    disconnected() {
      synced = false;
      resyncing = false;
      stopRetry();
    },

    handle(frame) {
      switch (frame.t) {
        case 'snap':
          onSnap(frame);
          break;
        case 'replay':
          onReplay(frame);
          break;
        case 'tx':
          onTx(frame);
          break;
        case 'ack':
          onAck(frame);
          break;
        case 'nack':
          onNackFrame(frame);
          break;
        case 'x':
          onX(frame);
          break;
        case 'kick':
          onKick(frame);
          break;
      }
    },

    retryNow() {
      if (cancelRetry !== null) {
        stopRetry();
        resubscribe();
      }
    },

    dispose() {
      stopRetry();
      stopWatching();
    },
  };
  return room;
}
