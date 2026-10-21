// Fake Retroscope server for front-end checks (DESIGN §g.4, §k.1). It serves the built web/dist with
// the shell and boot JSON, answers HTTP fixtures (fake Jira proxy included, see jira.js), and runs a
// scriptable /ws endpoint that speaks the real protocol: hello/welcome, snap/replay with per-cid
// cursors, tx then ack, nacks, gaps, x, kick, bye and close codes. Every frame it sends is validated
// against web/src/net/protocol.gen.js, and every client frame is checked the same way.
import { randomBytes, randomUUID } from 'node:crypto';
import { EventEmitter } from 'node:events';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { dirname, extname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { CLOSE, MAX_FRAME_BYTES, NACK } from '../../web/src/net/protocol.gen.js';
import { Router, problem, writeResponse } from './http.js';
import { installJira } from './jira.js';
import { assertServerFrame, checkClientFrame, roomKind } from './protocol.js';
import { loadDist, renderShell, securityHeaders, shellRoute } from './shell.js';
import { createReplay, loadTranscript } from './transcripts.js';
import { acceptUpgrade } from './ws.js';

const here = dirname(fileURLToPath(import.meta.url));
export const DEFAULT_DIST = join(here, '../../web/dist');
export const DEFAULT_ME = Object.freeze({ pid: '00000000-0000-4000-8000-000000000001', kind: 'user', name: 'Ada Fake' });
const MAX_BODY_BYTES = 4 * 1024 * 1024;

const CONTENT_TYPES = {
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.webp': 'image/webp',
  '.woff2': 'font/woff2',
  '.mp3': 'audio/mpeg',
  '.map': 'application/json; charset=utf-8',
};

export function newEpoch() {
  return randomBytes(8).toString('hex');
}

const resolve = (value, ...args) => (typeof value === 'function' ? value(...args) : value);

class Room {
  constructor(key, { snapshot = {}, overlay, epoch = newEpoch(), seq = 0 } = {}) {
    this.key = key;
    this.kind = roomKind(key);
    if (!this.kind) {
      throw new Error(`invalid room key ${key}`);
    }
    this.snapshot = snapshot;
    this.overlay = overlay;
    this.epoch = epoch;
    this.seq = seq;
    this.firstSeq = seq + 1;
    this.log = [];
    this.cursors = new Map();
    this.subscribers = new Set();
    this.replay = null;
  }

  restart() {
    this.epoch = newEpoch();
    this.firstSeq = this.seq + 1;
    this.log = [];
  }
}

export class FakeConnection {
  constructor(server, ws, req) {
    this.server = server;
    this.ws = ws;
    this.req = req;
    this.id = randomUUID();
    this.cid = null;
    this.me = null;
    this.subs = new Set();
    this.sent = [];
  }

  get open() {
    return this.ws.open;
  }

  send(frame) {
    assertServerFrame(frame);
    this.sent.push(frame);
    this.ws.send(JSON.stringify(frame));
  }

  // Sends text the validator would refuse, for client-robustness tests.
  sendRaw(text) {
    this.ws.send(text);
  }

  close(code, reason = '') {
    this.ws.close(code, reason);
  }

  bye(retryMs = 0) {
    this.send({ t: 'bye', code: 'restart', retryMs });
    this.close(CLOSE.RESTART, 'restart');
  }
}

export class FakeServer extends EventEmitter {
  constructor({ dist = DEFAULT_DIST, boot, me = DEFAULT_ME, helloTimeoutMs = 5000, clockOffsetMs = 0 } = {}) {
    super();
    this.distDir = dist;
    this.boot = boot ?? ((path) => ({ build: 'fake', theme: 'system', me: null, path }));
    this.me = me;
    this.helloTimeoutMs = helloTimeoutMs;
    this.clockOffsetMs = clockOffsetMs;
    this.router = new Router();
    this.rooms = new Map();
    this.connections = [];
    this.received = [];
    this.violations = [];
    this.requests = [];
    this.commandHandlers = [];
    this.helloRejections = [];
    this.http = createServer((req, res) => {
      this.onRequest(req, res).catch((error) => {
        if (!res.headersSent) {
          writeResponse(res, problem(500, 'internal', { detail: error.message }));
        }
        this.emit('error', error);
      });
    });
    this.http.on('upgrade', (req, socket, head) => this.onUpgrade(req, socket, head));
  }

  async listen({ port = 0, host = '127.0.0.1' } = {}) {
    this.dist = await loadDist(this.distDir);
    await new Promise((done, fail) => {
      this.http.once('error', fail);
      this.http.listen(port, host, done);
    });
    const address = this.http.address();
    this.host = `${host}:${address.port}`;
    this.origin = `http://${this.host}`;
    this.url = this.origin;
    this.wsUrl = `ws://${this.host}/ws`;
    return this;
  }

  async close() {
    for (const conn of this.connections) {
      conn.ws.terminate();
    }
    this.http.closeAllConnections?.();
    await new Promise((done) => this.http.close(() => done()));
  }

  // ---- HTTP -------------------------------------------------------------------------------------

  route(method, pattern, response) {
    return this.router.add(method, pattern, response);
  }

  jira(options) {
    return installJira(this, options);
  }

  bootFor(path, req) {
    return resolve(this.boot, path, req);
  }

  async onRequest(req, res) {
    const url = new URL(req.url, this.origin);
    const body = await readBody(req);
    const record = {
      method: req.method,
      path: url.pathname,
      query: url.searchParams,
      headers: req.headers,
      text: body.toString('utf8'),
    };
    record.json = parseJson(record.text);
    this.requests.push(record);
    this.emit('request', record);

    const unsafe = req.method !== 'GET' && req.method !== 'HEAD';
    if (unsafe && url.pathname.startsWith('/api/')) {
      const badOrigin = req.headers.origin !== undefined && req.headers.origin !== this.origin;
      if (req.headers['x-rs'] !== '1' || badOrigin) {
        await writeResponse(res, problem(403, 'csrf'));
        return;
      }
    }

    const found = this.router.find(req.method, url.pathname);
    if (found) {
      const response = await resolve(found.route.response, { ...record, params: found.params, body: record.json });
      await writeResponse(res, response);
      return;
    }

    if (req.method === 'GET' && url.pathname === '/api/boot') {
      await writeResponse(res, { body: this.bootFor(url.searchParams.get('path') ?? '/', req) });
      return;
    }
    if (url.pathname.startsWith('/api/')) {
      await writeResponse(res, problem(404, 'not_found'));
      return;
    }
    if (req.method === 'GET' || req.method === 'HEAD') {
      if (Object.hasOwn(this.dist.manifest.files, url.pathname)) {
        await this.serveFile(res, url.pathname, req.method === 'HEAD');
        return;
      }
      const route = shellRoute(url.pathname);
      if (route) {
        const html = renderShell(this.dist, route, this.bootFor(url.pathname + url.search, req));
        await writeResponse(res, { body: html, headers: { 'Content-Type': 'text/html; charset=utf-8', ...securityHeaders(this.dist, this.host) } });
        return;
      }
    }
    await writeResponse(res, { status: 404, body: 'not found', headers: { 'Content-Type': 'text/plain; charset=utf-8' } });
  }

  async serveFile(res, publicPath, headOnly) {
    const { etag } = this.dist.manifest.files[publicPath];
    const bytes = await readFile(join(this.dist.dir, publicPath));
    res.writeHead(200, {
      'Content-Type': CONTENT_TYPES[extname(publicPath)] ?? 'application/octet-stream',
      'Content-Length': bytes.length,
      'Cache-Control': 'public, max-age=31536000, immutable',
      'X-Content-Type-Options': 'nosniff',
      ETag: etag,
    });
    res.end(headOnly ? null : bytes);
  }

  // ---- WebSocket: rooms and scripting ------------------------------------------------------------

  room(key, options) {
    let room = this.rooms.get(key);
    if (!room) {
      room = new Room(key, options);
      this.rooms.set(key, room);
    } else if (options) {
      Object.assign(room, options);
    }
    return room;
  }

  // handler(ctx) for commands of `kind` ('*' for any); the latest matching registration runs. ctx has
  // {frame, conn, room, commit(events), ack(), nack(code, {msg, d})}; a handler that answers nothing
  // leaves the command unanswered (pending in the client's outbox).
  onCommand(kind, handler) {
    const entry = { kind, handler };
    this.commandHandlers.unshift(entry);
    return () => {
      this.commandHandlers = this.commandHandlers.filter((h) => h !== entry);
    };
  }

  // The next hello is answered by closing with `code` (4409 cid_in_use, 4401, 4400…).
  rejectNextHello(code, reason = '') {
    this.helloRejections.push({ code, reason });
  }

  // Appends one tx to the room and fans it out. `events` is a list of {k, d}, or a function of the
  // subscriber connection returning that viewer's list (per-viewer projection, e.g. vote secrecy).
  tx(roomKey, events) {
    const room = this.requireRoom(roomKey);
    if (typeof events !== 'function') {
      assertServerFrame({ t: 'tx', r: room.key, s: room.seq + 1, ev: events });
    }
    room.seq += 1;
    const entry = { s: room.seq, ev: events };
    room.log.push(entry);
    for (const conn of room.subscribers) {
      conn.send({ t: 'tx', r: room.key, s: entry.s, ev: resolve(events, conn) });
    }
    return room.seq;
  }

  // Advances the seq by `count` without telling anyone, so the next tx arrives as a gap. The skipped
  // entries are logged (empty), so the client's resubscribe gets them as a replay.
  gap(roomKey, count = 1) {
    const room = this.requireRoom(roomKey);
    for (let i = 0; i < count; i++) {
      room.seq += 1;
      room.log.push({ s: room.seq, ev: [] });
    }
    return room.seq;
  }

  x(roomKey, k, d) {
    const room = this.requireRoom(roomKey);
    for (const conn of room.subscribers) {
      conn.send({ t: 'x', r: room.key, k, d });
    }
  }

  kick(roomKey, code) {
    const room = this.requireRoom(roomKey);
    for (const conn of [...room.subscribers]) {
      conn.send({ t: 'kick', r: room.key, code });
      this.unsubscribe(conn, room);
    }
  }

  // Graceful restart: `bye` + 1012 to every socket. With `newEpoch`, every room starts a new
  // incarnation, so resubscribing clients get snapshots instead of replays.
  bye({ retryMs = 0, newEpoch: rotate = false } = {}) {
    for (const conn of this.liveConnections()) {
      conn.bye(retryMs);
    }
    if (rotate) {
      for (const room of this.rooms.values()) {
        room.restart();
      }
    }
  }

  closeAll(code, reason = '') {
    for (const conn of this.liveConnections()) {
      conn.close(code, reason);
    }
  }

  // Replays a golden transcript (path or object) to the room it is about. By default the whole
  // transcript goes to each connection when it subscribes; with {step:true} frames go out one by one
  // through the returned player's next().
  replayTranscript(transcript, options = {}) {
    const loaded = typeof transcript === 'string' ? loadTranscript(transcript) : transcript;
    const replay = createReplay(this, loaded, options);
    this.room(replay.roomKey).replay = replay;
    return replay;
  }

  liveConnections() {
    return this.connections.filter((c) => c.open);
  }

  async waitForConnection({ timeout = 5000, hello = true } = {}) {
    const ready = () => this.liveConnections().find((c) => !hello || c.cid !== null);
    return waitFor(this, hello ? 'hello' : 'connection', ready, timeout, 'a WebSocket connection');
  }

  // Resolves with the first received client frame matching `predicate(frame, conn)` (already received
  // ones count unless {after} gives a received-list index to start from).
  async waitForFrame(predicate, { timeout = 5000, after = 0 } = {}) {
    const find = () => this.received.slice(after).find(({ frame, conn }) => predicate(frame, conn));
    return waitFor(this, 'frame', find, timeout, 'a matching client frame');
  }

  requireRoom(key) {
    const room = this.rooms.get(key);
    if (!room) {
      throw new Error(`fake room ${key} does not exist; create it with fake.room(key, {snapshot})`);
    }
    return room;
  }

  onUpgrade(req, socket, head) {
    const url = new URL(req.url, this.origin);
    if (url.pathname !== '/ws') {
      socket.end('HTTP/1.1 404 Not Found\r\nConnection: close\r\n\r\n');
      return;
    }
    const ws = acceptUpgrade(req, socket, head, { maxMessageBytes: MAX_FRAME_BYTES });
    if (!ws) {
      return;
    }
    const conn = new FakeConnection(this, ws, req);
    this.connections.push(conn);
    if (req.headers.origin !== this.origin) {
      conn.close(CLOSE.BAD_ORIGIN, 'bad origin');
      return;
    }
    const helloTimer = setTimeout(() => conn.close(CLOSE.PROTOCOL_VIOLATION, 'no hello'), this.helloTimeoutMs);
    ws.on('message', (text) => this.onMessage(conn, text));
    ws.on('close', (code) => {
      clearTimeout(helloTimer);
      for (const room of this.rooms.values()) {
        this.unsubscribe(conn, room);
      }
      this.emit('disconnect', conn, code);
    });
    conn.helloTimer = helloTimer;
    this.emit('connection', conn);
  }

  violation(conn, frame, errors) {
    const entry = { conn, frame, errors };
    this.violations.push(entry);
    this.emit('violation', entry);
  }

  onMessage(conn, text) {
    const frame = parseJson(text);
    this.received.push({ conn, frame: frame ?? text });
    const { errors, envelopeOk } = checkClientFrame(frame);
    if (errors.length > 0) {
      this.violation(conn, frame ?? text, errors);
      if (envelopeOk && conn.cid !== null) {
        this.nack(conn, frame, 'invalid', { msg: errors[0] });
      } else {
        conn.close(CLOSE.PROTOCOL_VIOLATION, 'protocol violation');
      }
      this.emit('frame', frame);
      return;
    }
    if (conn.cid === null && frame.t !== 'hello') {
      this.violation(conn, frame, [`first frame must be hello, got ${frame.t}`]);
      conn.close(CLOSE.PROTOCOL_VIOLATION, 'expected hello');
    } else if (frame.t === 'hello') {
      this.onHello(conn, frame);
    } else if (frame.t === 'sub') {
      this.subscribe(conn, frame.r, frame.e, frame.s);
    } else if (frame.t === 'unsub') {
      const room = this.rooms.get(frame.r);
      if (room) {
        this.unsubscribe(conn, room);
      }
    } else if (frame.t === 'ping') {
      conn.send({ t: 'pong', c: frame.c, n: this.now() });
    } else if (frame.t === 'cmd') {
      this.onCmd(conn, frame);
    }
    this.emit('frame', frame);
  }

  now() {
    return Date.now() + this.clockOffsetMs;
  }

  onHello(conn, frame) {
    if (conn.cid !== null) {
      this.violation(conn, frame, ['hello sent twice']);
      conn.close(CLOSE.PROTOCOL_VIOLATION, 'hello sent twice');
      return;
    }
    clearTimeout(conn.helloTimer);
    const rejection = this.helloRejections.shift();
    const inUse = this.liveConnections().some((c) => c !== conn && c.cid === frame.cid);
    if (rejection || inUse) {
      const { code, reason } = rejection ?? { code: CLOSE.CID_IN_USE, reason: 'cid_in_use' };
      conn.cid = frame.cid;
      conn.close(code, reason);
      this.emit('hello', conn);
      return;
    }
    conn.cid = frame.cid;
    conn.me = resolve(this.me, conn.req);
    conn.send({ t: 'welcome', conn: conn.id, me: conn.me, n: this.now() });
    for (const sub of frame.subs) {
      this.subscribe(conn, sub.r, sub.e, sub.s);
    }
    this.emit('hello', conn);
  }

  subscribe(conn, key, epoch, seq) {
    const room = this.rooms.get(key);
    if (!room) {
      conn.send({ t: 'kick', r: key, code: 'not_found' });
      return;
    }
    room.subscribers.add(conn);
    conn.subs.add(key);
    const lastQ = room.cursors.get(conn.cid) ?? 0;
    const sameIncarnation = epoch === room.epoch && seq !== undefined;
    if (sameIncarnation && seq <= room.seq && seq >= room.firstSeq - 1) {
      const txs = room.log.filter((tx) => tx.s > seq).map((tx) => ({ s: tx.s, ev: resolve(tx.ev, conn) }));
      conn.send({ t: 'replay', r: key, e: room.epoch, from: seq + 1, lastQ, txs });
      return;
    }
    if (room.replay) {
      room.replay.attach(conn);
      return;
    }
    const snap = { t: 'snap', r: key, e: room.epoch, s: room.seq, lastQ, d: resolve(room.snapshot, conn) };
    const overlay = resolve(room.overlay, conn);
    if (overlay !== undefined) {
      snap.o = overlay;
    }
    conn.send(snap);
  }

  unsubscribe(conn, room) {
    room.subscribers.delete(conn);
    conn.subs.delete(room.key);
  }

  nack(conn, frame, code, { msg, d } = {}) {
    const nack = { t: 'nack', r: frame.r, q: frame.q, code, retry: NACK[code]?.retry ?? false };
    if (msg !== undefined) {
      nack.msg = msg;
    }
    if (d !== undefined) {
      nack.d = d;
    }
    conn.send(nack);
  }

  onCmd(conn, frame) {
    const room = this.rooms.get(frame.r);
    if (!room || !room.subscribers.has(conn)) {
      this.nack(conn, frame, 'not_subscribed');
      return;
    }
    const cursor = room.cursors.get(conn.cid) ?? 0;
    if (frame.q <= cursor) {
      conn.send({ t: 'ack', r: room.key, q: frame.q, s: room.seq, dup: true });
      return;
    }
    let answered = false;
    const once = (fn) => (...args) => {
      if (answered) {
        throw new Error(`command q=${frame.q} was already answered`);
      }
      answered = true;
      return fn(...args);
    };
    const advance = () => room.cursors.set(conn.cid, Math.max(room.cursors.get(conn.cid) ?? 0, frame.q));
    const ctx = {
      frame,
      conn,
      room,
      commit: once((events = []) => {
        if (typeof events === 'function' || events.length > 0) {
          this.tx(room.key, events);
        }
        advance();
        conn.send({ t: 'ack', r: room.key, q: frame.q, s: room.seq });
        return room.seq;
      }),
      ack: once(() => {
        advance();
        conn.send({ t: 'ack', r: room.key, q: frame.q, s: room.seq });
      }),
      nack: once((code, details) => this.nack(conn, frame, code, details)),
    };
    const entry = this.commandHandlers.find((h) => h.kind === frame.k || h.kind === '*');
    if (!entry) {
      ctx.ack();
      return;
    }
    Promise.resolve()
      .then(() => entry.handler(ctx))
      .catch((error) => this.emit('error', error));
  }
}

function waitFor(emitter, event, check, timeout, what) {
  const found = check();
  if (found) {
    return Promise.resolve(found);
  }
  return new Promise((done, fail) => {
    const timer = setTimeout(() => {
      emitter.off(event, listener);
      fail(new Error(`timed out after ${timeout} ms waiting for ${what}`));
    }, timeout);
    const listener = () => {
      const hit = check();
      if (hit) {
        clearTimeout(timer);
        emitter.off(event, listener);
        done(hit);
      }
    };
    emitter.on(event, listener);
  });
}

function parseJson(text) {
  if (text === '') {
    return undefined;
  }
  try {
    return JSON.parse(text);
  } catch {
    return undefined;
  }
}

function readBody(req) {
  return new Promise((done, fail) => {
    const chunks = [];
    let size = 0;
    req.on('data', (chunk) => {
      size += chunk.length;
      if (size > MAX_BODY_BYTES) {
        req.destroy();
        fail(new Error('request body too large'));
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => done(Buffer.concat(chunks)));
    req.on('error', fail);
  });
}

export async function startFakeServer(options = {}) {
  const { port, host, ...rest } = options;
  return new FakeServer(rest).listen({ port, host });
}
