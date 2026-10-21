import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createClock } from '../../../web/src/core/clock.js';
import { createOutbox } from '../../../web/src/net/outbox.js';
import {
  BACKOFF_MAX_MS, BACKOFF_RESET_MS, PING_EVERY_MS, WATCHDOG_MS, backoffDelay, createSocket,
} from '../../../web/src/net/socket.js';
import {
  OTHER_PID, OTHER_ROOM, PID, ROOM, fakeDocument, fakeTimers, fakeWebSocketClass, fakeWindow, listModel,
  memoryStorage, syncSchedule, uuidSequence,
} from './_fakes.js';

const EPOCH = '00000000000000aa';
const ME = { pid: PID, kind: 'user', name: 'Ada' };

function setup({ random = () => 0.5, storage = memoryStorage(), pid = PID } = {}) {
  const timers = fakeTimers();
  const ws = fakeWebSocketClass();
  const doc = fakeDocument();
  const win = fakeWindow();
  const outbox = createOutbox({ storage, channel: null, pid, now: timers.now, setTimer: timers.setTimer, uuid: uuidSequence() });
  const nacks = [];
  const socket = createSocket({
    url: 'ws://test/ws',
    outbox,
    clock: createClock({ now: timers.now, monotonic: timers.now }),
    WebSocketImpl: ws.FakeWebSocket,
    now: timers.now,
    random,
    setTimer: timers.setTimer,
    win,
    doc,
    onNack: (n) => nacks.push(n),
  });
  return { socket, outbox, timers, ws, doc, win, nacks };
}

async function welcome(server, me = ME) {
  await server.open();
  server.receive({ t: 'welcome', conn: 'conn', me, n: 5_000_000 });
}

function joinRoom(socket, key = ROOM) {
  return socket.room(key, listModel, { schedule: syncSchedule });
}

test('hello carries the protocol version, cid and every room to resume', async () => {
  const { socket, ws, outbox } = setup();
  const room = joinRoom(socket);
  room.seed({ e: EPOCH, s: 7, d: { items: [] } });
  joinRoom(socket, OTHER_ROOM);
  socket.start();
  assert.equal(ws.last().url, 'ws://test/ws');
  await ws.last().open();
  assert.deepEqual(ws.last().sent[0], {
    t: 'hello', v: 1, cid: outbox.cid, subs: [{ r: ROOM, e: EPOCH, s: 7 }, { r: OTHER_ROOM }],
  });
});

test('hello waits for the duplicate-tab check', async () => {
  const { socket, ws, outbox } = setup();
  let release;
  Object.defineProperty(outbox, 'ready', { value: new Promise((done) => { release = done; }) });
  socket.start();
  const opening = ws.last().open();
  await Promise.resolve();
  assert.deepEqual(ws.last().sent, []);
  release();
  await opening;
  assert.equal(ws.last().sent[0].t, 'hello');
});

test('welcome seeds the clock, pings at once and then every 60 s while visible', async () => {
  const { socket, ws, timers, doc } = setup();
  socket.start();
  await welcome(ws.last());
  assert.equal(socket.status.get().state, 'open');
  assert.equal(socket.me.pid, PID);
  assert.equal(socket.clock.offset, 5_000_000 - timers.now());
  assert.deepEqual(ws.last().frames('ping'), [{ t: 'ping', c: timers.now() }]);
  timers.advance(PING_EVERY_MS);
  assert.equal(ws.last().frames('ping').length, 2);
  doc.setVisibility('hidden');
  timers.advance(PING_EVERY_MS);
  assert.equal(ws.last().frames('ping').length, 2, 'no pings while hidden');
  doc.setVisibility('visible');
  assert.equal(ws.last().frames('ping').length, 3, 'visible again pings at once');
});

test('pong samples the clock', async () => {
  const { socket, ws, timers } = setup();
  socket.start();
  await welcome(ws.last());
  const c = timers.now();
  timers.advance(100);
  ws.last().receive({ t: 'pong', c, n: c + 50 + 2000 });
  assert.equal(socket.clock.offset, 2000);
});

test('a command is resent with the same q after a reconnect, once the replay arrives', async () => {
  const { socket, ws, timers } = setup();
  const room = joinRoom(socket);
  socket.start();
  await welcome(ws.last());
  ws.last().receive({ t: 'snap', r: ROOM, e: EPOCH, s: 3, lastQ: 0, d: { items: [] } });
  room.send('retro.card.add', { cardId: 'c', columnId: 'k', body: 'x', anonymous: false });
  assert.deepEqual(ws.last().frames('cmd').map((f) => f.q), [1]);
  ws.last().serverClose(1006);
  assert.equal(socket.status.get().state, 'waiting');
  timers.advance(BACKOFF_MAX_MS);
  const second = ws.last();
  assert.notEqual(second, ws.sockets[0]);
  await welcome(second);
  assert.deepEqual(second.sent[0].subs, [{ r: ROOM, e: EPOCH, s: 3 }]);
  assert.deepEqual(second.frames('cmd'), [], 'nothing before the replay');
  second.receive({ t: 'replay', r: ROOM, e: EPOCH, from: 4, lastQ: 1, txs: [] });
  assert.deepEqual(second.frames('cmd').map((f) => f.q), [1]);
  second.receive({ t: 'ack', r: ROOM, q: 1, s: 3, dup: true });
  assert.equal(room.pending().length, 0);
});

test('reconnect delays use full jitter, double from 250 ms and cap at 10 s', async () => {
  assert.equal(backoffDelay(0, () => 0), 0);
  assert.equal(backoffDelay(0, () => 0.999), 249.75);
  assert.equal(backoffDelay(3, () => 1), 2000);
  assert.equal(backoffDelay(6, () => 1), BACKOFF_MAX_MS);
  assert.equal(backoffDelay(30, () => 1), BACKOFF_MAX_MS);

  const { socket, ws, timers } = setup({ random: () => 1 });
  socket.start();
  const delays = [];
  for (let i = 0; i < 8; i++) {
    ws.last().serverClose(1006);
    delays.push(...timers.delays());
    timers.advance(timers.delays()[0]);
  }
  assert.deepEqual(delays, [250, 500, 1000, 2000, 4000, 8000, 10_000, 10_000]);
});

test('the backoff resets after 30 s connected', async () => {
  const { socket, ws, timers } = setup({ random: () => 1 });
  socket.start();
  for (let i = 0; i < 3; i++) {
    ws.last().serverClose(1006);
    timers.advance(timers.delays()[0]);
  }
  await welcome(ws.last());
  timers.advance(BACKOFF_RESET_MS - 1);
  ws.last().serverClose(1006);
  assert.deepEqual(timers.delays(), [2000], 'still escalated before 30 s');
  timers.advance(2000);
  await welcome(ws.last());
  timers.advance(BACKOFF_RESET_MS);
  ws.last().serverClose(1006);
  assert.deepEqual(timers.delays(), [250]);
});

test('bye.retryMs is honoured exactly, with no jitter and without waiting for the close', async () => {
  const { socket, ws, timers, win } = setup({ random: () => 0 });
  socket.start();
  const first = ws.last();
  await welcome(first);
  first.receive({ t: 'bye', code: 'restart', retryMs: 2600 });
  assert.equal(first.closedWith, 1000, 'the client closes on bye');
  assert.equal(socket.status.get().state, 'waiting');
  first.serverClose(1012);
  win.goOnline();
  timers.advance(2599);
  assert.equal(ws.sockets.length, 1, 'neither the close nor online cuts the wait short');
  timers.advance(1);
  assert.equal(ws.sockets.length, 2);
});

test('a close with 1005 (how WebKit reports 1012) reconnects like 1012', async () => {
  for (const code of [1012, 1005]) {
    const { socket, ws, timers } = setup({ random: () => 1 });
    socket.start();
    await welcome(ws.last());
    ws.last().serverClose(code);
    assert.equal(socket.status.get().state, 'waiting', `close ${code}`);
    timers.advance(250);
    assert.equal(ws.sockets.length, 2, `close ${code}`);
  }
});

test('4409 mints a new cid, keeps the outbox and reconnects', async () => {
  const { socket, ws, timers, outbox } = setup();
  const room = joinRoom(socket);
  socket.start();
  await welcome(ws.last());
  ws.last().receive({ t: 'snap', r: ROOM, e: EPOCH, s: 3, lastQ: 0, d: { items: [] } });
  room.send('retro.card.add', { cardId: 'c', columnId: 'k', body: 'x', anonymous: false });
  const oldCid = outbox.cid;
  ws.last().serverClose(4409);
  assert.notEqual(outbox.cid, oldCid);
  timers.advance(BACKOFF_MAX_MS);
  await ws.last().open();
  assert.equal(ws.last().sent[0].cid, outbox.cid);
  assert.equal(room.pending().length, 1);
});

test('4401 and 4403 stop reconnecting', async () => {
  for (const code of [4401, 4403]) {
    const { socket, ws, timers } = setup();
    socket.start();
    await welcome(ws.last());
    ws.last().serverClose(code);
    assert.deepEqual(socket.status.get(), { state: 'closed', offlineSince: timers.now(), code });
    timers.advance(60_000);
    assert.equal(ws.sockets.length, 1);
  }
});

test('a welcome for another principal clears the outbox and reconnects under a new cid', async () => {
  const { socket, ws, outbox } = setup();
  const room = joinRoom(socket);
  room.send('retro.card.add', { cardId: 'c', columnId: 'k', body: 'x', anonymous: false });
  socket.start();
  const first = ws.last();
  const oldCid = outbox.cid;
  await welcome(first, { ...ME, pid: OTHER_PID });
  assert.equal(outbox.size, 0);
  assert.notEqual(outbox.cid, oldCid);
  assert.equal(first.closedWith, 1000);
  assert.equal(ws.sockets.length, 2, 'reconnects at once');
  await ws.last().open();
  assert.equal(ws.last().sent[0].cid, outbox.cid);
  assert.equal(room.view, null);
});

test('cid_reuse mints a new cid and resends every pending entry with a new q', async () => {
  const { socket, ws, outbox } = setup();
  const room = joinRoom(socket);
  socket.start();
  await welcome(ws.last());
  ws.last().receive({ t: 'snap', r: ROOM, e: EPOCH, s: 3, lastQ: 0, d: { items: [] } });
  room.send('retro.card.add', { cardId: 'c', columnId: 'k', body: 'x', anonymous: false });
  room.send('retro.card.add', { cardId: 'd', columnId: 'k', body: 'y', anonymous: false });
  const oldCid = outbox.cid;
  ws.last().receive({ t: 'nack', r: ROOM, q: 1, code: 'cid_reuse', retry: false });
  assert.notEqual(outbox.cid, oldCid);
  const next = ws.last();
  assert.equal(ws.sockets.length, 2);
  await welcome(next);
  assert.equal(next.sent[0].cid, outbox.cid);
  next.receive({ t: 'replay', r: ROOM, e: EPOCH, from: 4, lastQ: 0, txs: [] });
  assert.deepEqual(next.frames('cmd').map((f) => [f.q, f.d.body]), [[3, 'x'], [4, 'y']]);
});

test('after cid_reuse, a create that already committed under the old cid is acked once, not applied twice', async () => {
  const { socket, ws } = setup();
  const room = joinRoom(socket);
  socket.start();
  await welcome(ws.last());
  ws.last().receive({ t: 'snap', r: ROOM, e: EPOCH, s: 3, lastQ: 0, d: { items: [] } });
  room.send('retro.card.add', { cardId: 'c', columnId: 'k', body: 'x', anonymous: false });
  room.send('retro.card.add', { cardId: 'd', columnId: 'k', body: 'y', anonymous: false });
  // x committed, but its ack was lost; y collided with another tab's (cid, q).
  ws.last().receive({ t: 'tx', r: ROOM, s: 4, ev: [{ k: 'test.added', d: { item: 'x' } }] });
  ws.last().receive({ t: 'nack', r: ROOM, q: 2, code: 'cid_reuse', retry: false });
  const next = ws.last();
  await welcome(next);
  next.receive({ t: 'replay', r: ROOM, e: EPOCH, from: 5, lastQ: 0, txs: [] });
  assert.deepEqual(next.frames('cmd').map((f) => [f.q, f.d.cardId]), [[3, 'c'], [4, 'd']]);

  // The server finds card c with the same payload and acks without a tx (Core's idempotent-create rule).
  next.receive({ t: 'ack', r: ROOM, q: 3, s: 4 });
  next.receive({ t: 'tx', r: ROOM, s: 5, ev: [{ k: 'test.added', d: { item: 'y' } }] });
  next.receive({ t: 'ack', r: ROOM, q: 4, s: 5 });
  assert.equal(room.pending().length, 0);
  assert.deepEqual(room.view.items, ['x', 'y']);
});

test('online and visible reconnect at once instead of waiting out the backoff', async () => {
  const { socket, ws, doc, win } = setup({ random: () => 1 });
  socket.start();
  ws.last().serverClose(1006);
  assert.equal(ws.sockets.length, 1);
  win.goOnline();
  assert.equal(ws.sockets.length, 2);
  ws.last().serverClose(1006);
  doc.setVisibility('hidden');
  doc.setVisibility('visible');
  assert.equal(ws.sockets.length, 3);
});

test('90 s of inbound silence while visible drops the socket; hidden tabs are left alone', async () => {
  const { socket, ws, timers, doc } = setup({ random: () => 0 });
  socket.start();
  await welcome(ws.last());
  doc.setVisibility('hidden');
  timers.advance(WATCHDOG_MS * 3);
  assert.equal(ws.sockets.length, 1);
  doc.setVisibility('visible');
  timers.advance(WATCHDOG_MS - 1);
  ws.last().receive({ t: 'pong', c: timers.now(), n: timers.now() });
  timers.advance(WATCHDOG_MS - 1);
  assert.equal(ws.sockets.length, 1, 'any frame rearms it');
  timers.advance(1);
  assert.equal(ws.sockets[0].closedWith, 1000);
  timers.advance(0);
  assert.equal(ws.sockets.length, 2);
});

test('status reports when the tab went offline', async () => {
  const { socket, ws, timers } = setup();
  socket.start();
  await welcome(ws.last());
  assert.equal(socket.status.get().offlineSince, null);
  const lostAt = timers.now();
  ws.last().serverClose(1006);
  timers.advance(BACKOFF_MAX_MS);
  assert.equal(socket.status.get().state, 'connecting');
  assert.equal(socket.status.get().offlineSince, lostAt);
});

test('rooms joined after hello subscribe at once; kicked rooms are not resumed', async () => {
  const { socket, ws, timers } = setup();
  socket.start();
  await welcome(ws.last());
  const room = joinRoom(socket);
  assert.deepEqual(ws.last().frames('sub'), [{ t: 'sub', r: ROOM }]);
  ws.last().receive({ t: 'kick', r: ROOM, code: 'forbidden' });
  assert.equal(room.kicked, 'forbidden');
  ws.last().serverClose(1006);
  timers.advance(BACKOFF_MAX_MS);
  await ws.last().open();
  assert.deepEqual(ws.last().sent[0].subs, []);
});

test('leaving a room waits for its pending entries before unsubscribing', async () => {
  const { socket, ws } = setup();
  const room = joinRoom(socket);
  socket.start();
  await welcome(ws.last());
  ws.last().receive({ t: 'snap', r: ROOM, e: EPOCH, s: 3, lastQ: 0, d: { items: [] } });
  room.send('retro.card.add', { cardId: 'c', columnId: 'k', body: 'x', anonymous: false });
  socket.leave(room);
  assert.deepEqual(ws.last().frames('unsub'), []);
  ws.last().receive({ t: 'ack', r: ROOM, q: 1, s: 4 });
  assert.deepEqual(ws.last().frames('unsub'), [{ t: 'unsub', r: ROOM }]);
  const other = joinRoom(socket);
  assert.notEqual(other, room);
});

test('frames for unknown rooms and malformed text are ignored', async () => {
  const { socket, ws } = setup();
  socket.start();
  await welcome(ws.last());
  ws.last().onmessage({ data: '{not json' });
  ws.last().receive({ t: 'tx', r: OTHER_ROOM, s: 1, ev: [] });
  assert.equal(socket.status.get().state, 'open');
});

test('stop closes the socket and never reconnects', async () => {
  const { socket, ws, timers } = setup();
  socket.start();
  await welcome(ws.last());
  socket.stop();
  assert.equal(ws.last().closedWith, 1000);
  timers.advance(60_000);
  assert.equal(ws.sockets.length, 1);
  assert.equal(socket.status.get().state, 'closed');
});
