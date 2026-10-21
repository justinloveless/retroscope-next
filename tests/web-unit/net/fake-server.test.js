// The net layer against tests/web-fake-server over a real socket, so the frames it writes are checked by
// the fake's protocol validator and the resume rules are the fake's, not this suite's.
import { after, before, beforeEach, afterEach, test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { startFakeServer } from '../../web-fake-server/server.js';
import { connectWebSocket } from '../../web-fake-server/ws.js';
import { createOutbox } from '../../../web/src/net/outbox.js';
import { createSocket } from '../../../web/src/net/socket.js';
import { memoryStorage } from './_fakes.js';

const POKER = 'poker:5e551011-0000-4000-8000-000000000001';
const ROUND = '70000000-0000-4000-8000-000000000001';
const PID = '00000000-0000-4000-8000-000000000001';

const model = {
  snapshot: (d) => ({ code: d.code, kinds: [] }),
  apply: (state, ev) => ({ ...state, kinds: [...state.kinds, ev.k] }),
};

// The browser WebSocket surface the socket uses, over the fake's Node client.
function nodeWebSocket(origin) {
  return class {
    constructor(url) {
      this.readyState = 0;
      connectWebSocket(url, { origin }).then((conn) => {
        this.conn = conn;
        if (this.readyState === 3) {
          conn.close(1000);
          return;
        }
        this.readyState = 1;
        conn.on('message', (data) => this.onmessage?.({ data }));
        conn.on('close', (code) => {
          this.readyState = 3;
          this.onclose?.({ code });
        });
        this.onopen?.();
      }, () => {
        this.readyState = 3;
        this.onclose?.({ code: 1006 });
      });
    }

    send(text) {
      this.conn.send(text);
    }

    close(code = 1000) {
      this.readyState = 3;
      this.conn?.close(code);
    }
  };
}

async function until(check, what, timeout = 2000) {
  const end = Date.now() + timeout;
  while (!check()) {
    if (Date.now() > end) {
      throw new Error(`timed out waiting for ${what}`);
    }
    await new Promise((done) => setTimeout(done, 5));
  }
}

let dist;
let fake;
let socket;
let outbox;

before(async () => {
  dist = await mkdtemp(join(tmpdir(), 'rs-net-dist-'));
  const manifest = { entry: [], css: [], routes: {}, routeCss: {}, styles: {}, assets: {}, files: {} };
  await writeFile(join(dist, 'manifest.json'), JSON.stringify(manifest));
  await writeFile(join(dist, 'csp.json'), JSON.stringify({ scriptHashes: [], themeScript: '' }));
});

after(async () => {
  await rm(dist, { recursive: true, force: true });
});

beforeEach(async () => {
  fake = await startFakeServer({ dist });
  fake.room(POKER, { snapshot: { code: 'FAKE123456' }, overlay: { own: [], heldByThisTab: false } });
  outbox = createOutbox({ storage: memoryStorage(), channel: null, pid: PID });
  socket = createSocket({
    url: fake.wsUrl,
    outbox,
    WebSocketImpl: nodeWebSocket(fake.origin),
    random: () => 0,
    win: null,
    doc: null,
  });
});

afterEach(async () => {
  socket.stop();
  await fake.close();
  assert.deepEqual(fake.violations, [], 'the fake refused a client frame');
});

const vote = (points) => ({ roundId: ROUND, points });
const hellos = () => fake.received.filter((r) => r.frame.t === 'hello').map((r) => r.frame.cid);
const cmds = () => fake.received.filter((r) => r.frame.t === 'cmd').map((r) => r.frame);

test('a command commits: tx then ack, and the outbox empties', async () => {
  fake.onCommand('poker.vote.set', (ctx) => ctx.commit([{ k: 'poker.vote.state', d: { roundId: ROUND, pid: PID, status: 'selected' } }]));
  const room = socket.room(POKER, model);
  socket.start();
  await until(() => room.synced, 'the snapshot');
  room.send('poker.vote.set', vote(5));
  await until(() => room.pending().length === 0, 'the ack');
  assert.deepEqual(room.view.kinds, ['poker.vote.state']);
  assert.equal(room.seq, 1);
});

test('after a bye the client reconnects at retryMs and resends with the same q; a landed commit comes back dup', async () => {
  const seen = [];
  fake.onCommand('poker.vote.set', (ctx) => {
    seen.push(ctx.frame.q);
  });
  const room = socket.room(POKER, model);
  socket.start();
  await until(() => room.synced, 'the snapshot');
  room.send('poker.vote.set', vote(5));
  await until(() => seen.length === 1, 'the command');
  fake.rooms.get(POKER).cursors.set(outbox.cid, seen[0]);
  fake.bye({ retryMs: 50 });
  await until(() => hellos().length === 2, 'the second hello');
  await until(() => room.pending().length === 0, 'the dup ack');
  assert.deepEqual(seen, [1], 'the resend was deduped by the cursor, not handled again');
  assert.deepEqual(cmds().map((f) => f.q), [1, 1]);
});

test('a gap resubscribes and the replay fills it', async () => {
  const room = socket.room(POKER, model);
  socket.start();
  await until(() => room.synced, 'the snapshot');
  fake.gap(POKER, 2);
  fake.tx(POKER, [{ k: 'poker.observers', d: { ids: [] } }]);
  await until(() => room.seq === 3, 'the replay');
  assert.deepEqual(room.view.kinds, ['poker.observers']);
  assert.ok(fake.received.some((r) => r.frame.t === 'sub' && r.frame.s === 0));
});

test('a 4409 hello rejection with no twin keeps the cid, and the next hello is welcomed', async () => {
  fake.rejectNextHello(4409);
  const room = socket.room(POKER, model);
  const firstCid = outbox.cid;
  socket.start();
  await until(() => room.synced, 'the snapshot after the second hello');
  assert.deepEqual(hellos(), [firstCid, firstCid]);
});

test('a server_busy nack resubscribes before the resend, which keeps its q', async () => {
  let first = true;
  fake.onCommand('poker.vote.set', (ctx) => {
    if (first) {
      first = false;
      ctx.nack('server_busy');
      return;
    }
    ctx.commit([{ k: 'poker.vote.state', d: { roundId: ROUND, pid: PID, status: 'selected' } }]);
  });
  const room = socket.room(POKER, model);
  socket.start();
  await until(() => room.synced, 'the snapshot');
  room.send('poker.vote.set', vote(3));
  await until(() => room.pending().length === 0, 'the ack of the resend', 3000);
  const after = fake.received.map((r) => r.frame).filter((f) => f.t === 'sub' || f.t === 'cmd').map((f) => f.t === 'cmd' ? f.q : 'sub');
  assert.deepEqual(after, [1, 'sub', 1]);
});

test('cid_reuse without a twin keeps the cid and resends the entry with a new q', async () => {
  let first = true;
  fake.onCommand('poker.vote.set', (ctx) => {
    if (first) {
      first = false;
      ctx.nack('cid_reuse');
      return;
    }
    ctx.commit([{ k: 'poker.vote.state', d: { roundId: ROUND, pid: PID, status: 'selected' } }]);
  });
  const room = socket.room(POKER, model);
  socket.start();
  await until(() => room.synced, 'the snapshot');
  room.send('poker.vote.set', vote(3));
  await until(() => room.pending().length === 0 && room.synced, 'the ack of the renumbered entry', 3000);
  assert.equal(hellos().length, 1);
  assert.deepEqual(cmds().map((f) => f.q), [1, 2]);
  assert.deepEqual(room.view.kinds, ['poker.vote.state']);
});
