// One browser tab's protocol state for the drills: a stable cid, a q counter, an outbox of unacked commands that is
// resent after a reconnect (§c.3, §g.4), and the room's epoch and seq for resume. Every ack and every reconnect is
// timed so the drills can report latency and recovery.
import crypto from 'node:crypto';
import { Socket, sleep } from './client.mjs';

export class Tab {
  constructor(user, room) {
    this.user = user;
    this.room = room;
    this.cid = crypto.randomUUID();
    this.q = 0;
    this.outbox = new Map();
    this.applied = [];
    this.nacks = [];
    this.latencies = [];
    this.reconnects = [];
    this.e = null;
    this.s = null;
    this.me = null;
    this.snapshot = null;
    this.socket = null;
    this.stopping = false;
    this.ready = null;
  }

  get connected() { return this.socket !== null && this.ready === null; }

  /** Connects and waits for the room's snap or replay; retries every 250 ms (or bye.retryMs) until it gets in. */
  async connect() {
    const started = Date.now();
    let retryMs = 250;
    for (;;) {
      try {
        await this.open();
        return Date.now() - started;
      } catch {
        if (this.stopping) return null;
        await sleep(this.retryMs ?? retryMs);
        this.retryMs = null;
        retryMs = Math.min(retryMs * 2, 1000);
      }
    }
  }

  async open() {
    let settle;
    this.ready = new Promise((resolve, reject) => { settle = { resolve, reject }; });
    const subs = [{ r: this.room, ...(this.e !== null ? { e: this.e, s: this.s } : {}) }];
    const socket = await Socket.open(this.user, {
      onFrame: (f) => this.receive(f, settle),
      onClose: (code) => this.closed(code, settle),
    });
    this.socket = socket;
    socket.send({ t: 'hello', v: 1, cid: this.cid, subs });
    const timer = setTimeout(() => socket.finish(4000), 10_000);
    try {
      await this.ready;
    } finally {
      clearTimeout(timer);
    }
    this.ready = null;
    for (const [, entry] of [...this.outbox].sort(([a], [b]) => a - b)) {
      entry.sentAt = Date.now();
      socket.send(entry.frame);
    }
  }

  receive(f, settle) {
    switch (f.t) {
      case 'welcome':
        this.me = f.me;
        break;
      case 'snap':
      case 'replay':
        if (f.r !== this.room) break;
        this.e = f.e;
        if (f.t === 'snap') {
          this.s = f.s;
          this.snapshot = f.d;
        } else if (f.txs.length > 0) {
          this.s = f.txs[f.txs.length - 1].s;
        }
        // Commands at or below the server's cursor were applied; their acks were lost with the old socket.
        for (const q of [...this.outbox.keys()].filter((x) => x <= f.lastQ)) {
          this.applied.push({ ...this.outbox.get(q), via: 'cursor' });
          this.outbox.delete(q);
        }
        settle.resolve();
        break;
      case 'tx':
        if (f.r === this.room) this.s = f.s;
        break;
      case 'ack': {
        const entry = this.outbox.get(f.q);
        if (entry) {
          this.outbox.delete(f.q);
          this.latencies.push(Date.now() - entry.sentAt);
          this.applied.push({ ...entry, via: f.dup ? 'dup' : 'ack' });
        }
        break;
      }
      case 'nack': {
        const entry = this.outbox.get(f.q);
        if (entry && !f.retry) this.outbox.delete(f.q);
        this.nacks.push({ q: f.q, k: entry?.frame.k, code: f.code, retry: f.retry });
        break;
      }
      case 'bye':
        this.retryMs = f.retryMs;
        break;
      case 'kick':
        this.nacks.push({ kick: f.code });
        break;
      default:
        break;
    }
  }

  closed(code, settle) {
    const wasReady = this.ready === null;
    this.socket = null;
    if (!wasReady) {
      settle.reject(new Error(`closed ${code} before the room arrived`));
      return;
    }
    if (this.stopping) return;
    const closedAt = Date.now();
    this.connect().then((took) => {
      if (took !== null) this.reconnects.push({ code, ms: Date.now() - closedAt });
    });
  }

  /** Queues a command; it is sent now when connected, else after the next reconnect. */
  command(k, d) {
    const q = ++this.q;
    const entry = { frame: { t: 'cmd', r: this.room, q, k, d }, sentAt: Date.now() };
    this.outbox.set(q, entry);
    if (this.connected) this.socket.send(entry.frame);
    return q;
  }

  async drain(timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    while (this.outbox.size > 0 && Date.now() < deadline) await sleep(100);
    return this.outbox.size;
  }

  close() {
    this.stopping = true;
    this.socket?.finish(1000);
  }
}
