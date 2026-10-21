// One soak tab: the drill Tab (cid, q, outbox resend, resume) plus what a browser tab also does and what the soak
// has to observe. It folds every frame through the real client models (web/src/*/model.js), renumbers commands after a
// retryable nack, drops ephemeral commands queued for more than 10 s (§c.8), and checks each frame it receives: no
// other principal's vote values before reveal (§c.4 vote secrecy) and no seq gaps (§c.3).
import { Tab } from '../../../deploy/drills/tab.mjs';
import * as poker from '../../../web/src/poker/model.js';
import * as retro from '../../../web/src/retro/model.js';

const EPHEMERAL = new Set([
  'poker.spotlight.move', 'retro.focus.set', 'retro.timer.start', 'retro.timer.pause', 'retro.timer.resume',
  'retro.timer.reset', 'retro.timer.setFlags',
]);
const EPHEMERAL_TTL_MS = 10_000;

export class SoakTab extends Tab {
  /**
   * @param {object} options `kind` is 'poker' or 'board'; `onTx(tab, e, s, ev)` sees every tx this tab folds;
   *   `onReconnect(tab, record)` and `onViolation(tab, violation)` report as they happen.
   */
  constructor(user, room, { kind, label, onTx = () => {}, onReconnect = () => {}, onViolation = () => {} }) {
    super(user, room);
    this.kind = kind;
    this.label = label;
    this.onTx = onTx;
    this.onReconnect = onReconnect;
    this.onViolation = onViolation;
    this.state = null;
    this.snap = null;
    this.model = null;
    this.nextIntent = 0;
    this.rejected = [];
    this.dropped = [];
    this.violations = [];
    this.settledAt = 0;
  }

  get pid() { return this.me?.pid ?? null; }

  /** Queues a command as a new intent; the intent id survives renumbering. */
  command(k, d) {
    const q = super.command(k, d);
    const entry = this.outbox.get(q);
    entry.intent = ++this.nextIntent;
    entry.createdAt = Date.now();
    return q;
  }

  pending() {
    return [...this.outbox.values()].map((entry) => ({ k: entry.frame.k, d: entry.frame.d }));
  }

  receive(f, settle) {
    if (f.r !== undefined && f.r !== this.room) {
      super.receive(f, settle);
      return;
    }
    switch (f.t) {
      case 'snap':
        this.fromSnap(f);
        super.receive(f, settle);
        this.dropStaleEphemeral();
        return;
      case 'replay':
        if (f.e !== this.e) {
          this.violate('replay_epoch', { have: this.e, got: f.e });
        }
        for (const tx of f.txs) {
          this.fold(f.e, tx.s, tx.ev);
        }
        super.receive(f, settle);
        this.dropStaleEphemeral();
        return;
      case 'tx':
        this.fold(this.e, f.s, f.ev);
        break;
      case 'ack': {
        const entry = this.outbox.get(f.q);
        if (entry) {
          entry.ackS = f.s;
        }
        break;
      }
      case 'nack':
        this.nacked(f, settle);
        return;
      default:
        break;
    }
    super.receive(f, settle);
  }

  nacked(f, settle) {
    const entry = this.outbox.get(f.q);
    super.receive(f, settle);
    if (!entry) {
      return;
    }
    if (!f.retry) {
      this.rejected.push({ intent: entry.intent, k: entry.frame.k, d: entry.frame.d, code: f.code });
      return;
    }
    // A retryable nack leaves the cursor where it was, but later commands move it on; resending the same q would then be
    // acked as a duplicate without being applied. So the intent goes out again under a new q, as outbox.js does.
    this.outbox.delete(f.q);
    const q = ++this.q;
    entry.frame = { ...entry.frame, q };
    this.outbox.set(q, entry);
    setTimeout(() => {
      if (this.connected && this.outbox.get(q) === entry) {
        entry.sentAt = Date.now();
        this.socket.send(entry.frame);
      }
    }, 500 + Math.random() * 1000);
  }

  dropStaleEphemeral() {
    const now = Date.now();
    for (const [q, entry] of this.outbox) {
      if (EPHEMERAL.has(entry.frame.k) && now - entry.createdAt > EPHEMERAL_TTL_MS) {
        this.outbox.delete(q);
        this.dropped.push({ intent: entry.intent, k: entry.frame.k, d: entry.frame.d });
      }
    }
  }

  fromSnap(f) {
    const me = this.pid;
    this.snap = { e: f.e, s: f.s, d: f.d, o: f.o };
    if (this.kind === 'poker') {
      this.checkPokerSnap(f.d, f.o ?? {});
      this.model = poker.pokerModel(me, { pending: () => this.pending() });
    } else {
      this.model = retro.createRetroModel({ me: () => me });
    }
    this.state = this.model.snapshot(f.d, f.o);
    this.settledAt = Date.now();
  }

  fold(e, s, ev) {
    if (this.state === null || s <= this.s) {
      return;
    }
    if (s !== this.s + 1) {
      this.violate('seq_gap', { e, from: this.s, to: s });
    }
    if (this.kind === 'poker') {
      for (const event of ev) {
        this.checkPokerEvent(event);
      }
    }
    this.state = ev.reduce(this.model.apply, this.state);
    this.s = s;
    this.settledAt = Date.now();
    this.onTx(this, e, s, ev);
  }

  checkPokerSnap(d, o) {
    const me = this.pid;
    const voting = new Set(d.rounds.filter((r) => r.state === 'voting').map((r) => r.id));
    for (const vote of d.votes ?? []) {
      if (vote.pid !== me && voting.has(vote.roundId) && (vote.points != null || vote.high != null)) {
        this.violate('vote_leak', { via: 'snap', roundId: vote.roundId, of: vote.pid });
      }
    }
    for (const vote of o.own ?? []) {
      if (vote.pid !== me) {
        this.violate('vote_leak', { via: 'overlay', roundId: vote.roundId, of: vote.pid });
      }
    }
  }

  checkPokerEvent({ k, d }) {
    if (k !== 'poker.vote.state' || d.pid === this.pid || (d.points == null && d.high == null)) {
      return;
    }
    const round = this.state.rounds.find((r) => r.id === d.roundId);
    if (round?.state !== 'revealed') {
      this.violate('vote_leak', { via: 'tx', roundId: d.roundId, of: d.pid });
    }
  }

  violate(type, detail) {
    const violation = { type, room: this.room, tab: this.label, ...detail };
    this.violations.push(violation);
    this.onViolation(this, violation);
  }

  closed(code, settle) {
    const wasReady = this.ready === null;
    this.socket = null;
    if (!wasReady) {
      settle.reject(new Error(`closed ${code} before the room arrived`));
      return;
    }
    if (this.stopping) {
      return;
    }
    const closedAt = Date.now();
    this.connect().then((took) => {
      if (took !== null) {
        const record = { code, closedAt, ms: Date.now() - closedAt };
        this.reconnects.push(record);
        this.onReconnect(this, record);
      }
    });
  }

  /** The folded view in the server's snapshot form, as comparable to a fresh `snap`. */
  view() {
    const wire = this.kind === 'poker' ? poker.toWire(this.state) : retro.toWire(this.state);
    return canonicalView(this.kind, wire.d, wire.o);
  }

  /** Every intent with its final outcome. */
  outcomes() {
    const out = [];
    for (const entry of this.applied) {
      out.push({ intent: entry.intent, k: entry.frame.k, d: entry.frame.d, via: entry.via, s: entry.ackS ?? null });
    }
    for (const entry of this.rejected) {
      out.push({ ...entry, via: 'nack' });
    }
    for (const entry of this.dropped) {
      out.push({ ...entry, via: 'dropped' });
    }
    for (const entry of this.outbox.values()) {
      out.push({ intent: entry.intent, k: entry.frame.k, d: entry.frame.d, via: 'pending' });
    }
    return out.sort((a, b) => a.intent - b.intent);
  }
}

const byPid = (a, b) => (a.pid < b.pid ? -1 : a.pid > b.pid ? 1 : 0);

function sortKeys(value) {
  if (Array.isArray(value)) {
    return value.map(sortKeys);
  }
  if (value === null || typeof value !== 'object') {
    return value;
  }
  return Object.fromEntries(Object.keys(value).sort().map((key) => [key, sortKeys(value[key])]));
}

const byId = (key) => (a, b) => (a[key] < b[key] ? -1 : a[key] > b[key] ? 1 : 0);
const sorted = (list, compare) => [...(list ?? [])].sort(compare);

/**
 * `{d, o}` as one string to compare: object members sorted, presence in pid order (the server's order; the board model
 * keeps arrival order) and `heldByThisTab` left out, because it belongs to the tab and not to the room. A board's cards,
 * comments and votes are compared as sets: the server orders cards created in the same millisecond by their stored
 * microseconds, the model by id, and the screens sort cards themselves.
 */
export function canonicalView(kind, d, o) {
  const data = { ...d, presence: sorted(d.presence, byPid) };
  if (kind === 'poker') {
    return JSON.stringify(sortKeys({ d: data, o: { own: o?.own ?? [] } }));
  }
  data.cards = sorted(d.cards, byId('id'));
  data.comments = sorted(d.comments, byId('id'));
  data.votes = sorted(d.votes, byId('cardId'));
  const overlay = Object.fromEntries(Object.entries(o ?? {}).map(([key, ids]) => [key, [...ids].sort()]));
  return JSON.stringify(sortKeys({ d: data, o: overlay }));
}
