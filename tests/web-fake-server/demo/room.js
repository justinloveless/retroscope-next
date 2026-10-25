// What every demo room shares: per-viewer projection of event lists, the presence table (real sockets
// plus simulated peers who are "in the room" without one) and the command entry point.
import { newEpoch } from '../server.js';
import { byPid } from './util.js';

/**
 * An event list entry is `{k, d}` for everyone or `(viewerPid, conn) => [{k, d}…]` for a per-viewer
 * projection (vote secrecy, `mine` flags, member-only data).
 */
export function project(entries) {
  return (conn) => entries.flatMap((entry) => (typeof entry === 'function' ? entry(conn.me?.pid ?? null, conn) : [entry]));
}

export const nack = (code, extra = {}) => ({ nack: code, ...extra });

// The grace before a closed socket's principal leaves, as the server's 10 s grace, shortened so that
// closing a demo window shows up while you watch.
const LEAVE_GRACE_MS = 3000;

export class Presence {
  constructor(onChange) {
    this.onChange = onChange;
    this.entries = new Map();
  }

  list() {
    return [...this.entries.values()].map((e) => e.info).sort(byPid);
  }

  has(pid) {
    return this.entries.has(pid);
  }

  /** True while a real socket (a browser window) speaks for `pid`. */
  isReal(pid) {
    return (this.entries.get(pid)?.conns.size ?? 0) > 0;
  }

  realCount() {
    return [...this.entries.values()].filter((e) => e.conns.size > 0).length;
  }

  addVirtual(info) {
    this.enter(info).virtual = true;
  }

  removeVirtual(pid) {
    const entry = this.entries.get(pid);
    if (entry) {
      entry.virtual = false;
      this.maybeLeave(pid, 0);
    }
  }

  connect(conn, info) {
    this.enter(info).conns.add(conn);
  }

  disconnect(conn) {
    const pid = conn.me?.pid;
    const entry = pid && this.entries.get(pid);
    if (entry?.conns.delete(conn)) {
      this.maybeLeave(pid, LEAVE_GRACE_MS);
    }
  }

  enter(info) {
    let entry = this.entries.get(info.pid);
    if (entry) {
      clearTimeout(entry.timer);
      entry.timer = null;
      return entry;
    }
    entry = { info, conns: new Set(), virtual: false, timer: null };
    this.entries.set(info.pid, entry);
    this.onChange('join', info);
    return entry;
  }

  maybeLeave(pid, graceMs) {
    const entry = this.entries.get(pid);
    if (!entry || entry.virtual || entry.conns.size > 0) {
      return;
    }
    clearTimeout(entry.timer);
    entry.timer = setTimeout(() => {
      if (entry.conns.size === 0 && !entry.virtual && this.entries.get(pid) === entry) {
        this.entries.delete(pid);
        this.onChange('leave', entry.info);
      }
    }, graceMs);
    entry.timer.unref?.();
  }

  stop() {
    for (const entry of this.entries.values()) {
      clearTimeout(entry.timer);
    }
  }
}

export class DemoRoom {
  constructor(demo, key, { tracksPresence = true } = {}) {
    this.demo = demo;
    this.key = key;
    this.timers = new Set();
    this.presence = tracksPresence
      ? new Presence((what, info) => this.tx([what === 'join' ? { k: 'presence.join', d: info } : { k: 'presence.leave', d: { pid: info.pid } }]))
      : null;
  }

  install() {
    this.room = this.demo.fake.room(this.key, {
      snapshot: (conn) => this.snapshot(conn.me?.pid ?? null, conn),
      overlay: (conn) => this.overlay?.(conn.me?.pid ?? null, conn),
      epoch: newEpoch(),
      seq: 0,
    });
    return this;
  }

  get pids() {
    return this.presence?.list().map((p) => p.pid) ?? [];
  }

  /** Appends one tx (skipped when nothing would be sent) and keeps the replay log short. */
  tx(entries) {
    if (entries.length === 0 || !this.room) {
      return;
    }
    this.demo.fake.tx(this.key, project(entries));
    const log = this.room.log;
    if (log.length > 600) {
      log.splice(0, log.length - 400);
      this.room.firstSeq = log[0].s;
    }
  }

  /** A command from a real socket; answers with commit, ack or nack. */
  handle(ctx) {
    const result = this.decide(ctx.frame.k, ctx.frame.d, ctx.conn.me, ctx.conn);
    if (result.nack) {
      ctx.nack(result.nack, { msg: result.msg, d: result.d });
      return;
    }
    const events = result.events ?? [];
    if (events.length > 0) {
      this.touch();
      ctx.commit(project(events));
    } else {
      ctx.ack();
    }
    result.after?.();
  }

  /** The same decision for a simulated peer, applied without an ack. */
  act(who, k, d) {
    const result = this.decide(k, d, who, null);
    if (!result.nack) {
      if ((result.events ?? []).length > 0) {
        this.touch();
      }
      this.tx(result.events ?? []);
      result.after?.();
    }
    return result;
  }

  /** A committed write: the team lists' lastActivityAt, which the server keeps to the minute. */
  touch() {
    this.lastActivityAt = Math.floor(this.demo.now() / 60_000) * 60_000;
  }

  later(ms, fn) {
    const timer = setTimeout(() => {
      this.timers.delete(timer);
      fn();
    }, ms);
    timer.unref?.();
    this.timers.add(timer);
    return timer;
  }

  cancel(timer) {
    clearTimeout(timer);
    this.timers.delete(timer);
  }

  onSubscribe(conn) {
    if (this.presence && conn.me) {
      this.presence.connect(conn, this.demo.infoOf(conn.me));
    }
  }

  onUnsubscribe(conn) {
    this.presence?.disconnect(conn);
  }

  stop() {
    for (const timer of this.timers) {
      clearTimeout(timer);
    }
    this.timers.clear();
    this.presence?.stop();
  }
}
