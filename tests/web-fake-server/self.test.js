import { after, afterEach, before, beforeEach, describe, test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { isAllowedDoc } from '../../web/src/markup/allow.js';
import { DIALECT_JIRA_DESCRIPTION } from '../../web/src/markup/doc.js';
import { parse } from '../../web/src/markup/parse.js';
import { adfToDoc } from './adf.js';
import { TestClient } from './client.js';
import { json, problem } from './http.js';
import { checkClientFrame, checkServerFrame } from './protocol.js';
import { FakeServer, newEpoch, startFakeServer } from './server.js';
import { loadTranscript, parseTranscript } from './transcripts.js';
import { connectWebSocket, encodeFrame, OP, WsConnection } from './ws.js';

const here = dirname(fileURLToPath(import.meta.url));
const SAMPLE = join(here, 'fixtures/transcripts/poker-sample.json');
const POKER = 'poker:5e551011-0000-4000-8000-000000000001';
const BOARD = 'board:b0a7d000-0000-4000-8000-000000000001';
const ROUND = '40000000-0000-4000-8000-000000000001';
const CID = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const OTHER_CID = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const ME = '00000000-0000-4000-8000-000000000001';
const THEME = '(function(){document.documentElement.dataset.theme="light"})();';

// A dist with the shape build.mjs writes, so these tests do not depend on ./build.sh web.
async function makeDist() {
  const dir = await mkdtemp(join(tmpdir(), 'rs-fake-dist-'));
  await mkdir(join(dir, 'assets/chunks'), { recursive: true });
  const files = {
    '/assets/entry-TEST.js': 'import "./chunks/shared-TEST.js";\n',
    '/assets/chunks/shared-TEST.js': 'export const x = 1;\n',
    '/assets/chunks/poker-TEST.js': 'export function mount() {}\n',
    '/assets/kit-TEST.css': 'body{margin:0}\n',
    '/assets/neotro-TEST.css': '.table{}\n',
  };
  const meta = {};
  for (const [path, text] of Object.entries(files)) {
    await writeFile(join(dir, path), text);
    meta[path] = { size: Buffer.byteLength(text), etag: `"${createHash('sha256').update(text).digest('hex').slice(0, 16)}"`, br: null, gz: null };
  }
  const root = { '/favicon.ico': 'ICO', '/manifest.webmanifest': '{"name":"Retroscope"}', '/robots.txt': 'User-agent: *\n' };
  const publicFiles = {};
  for (const [path, text] of Object.entries(root)) {
    await writeFile(join(dir, path), text);
    publicFiles[path] = { size: Buffer.byteLength(text), etag: `"${path.slice(1)}"`, br: null, gz: null };
  }
  const manifest = {
    entry: ['/assets/entry-TEST.js', '/assets/chunks/shared-TEST.js'],
    css: ['/assets/kit-TEST.css'],
    routes: { '/poker/:code': ['/assets/chunks/poker-TEST.js', '/assets/chunks/shared-TEST.js'] },
    routeCss: { '/poker/:code': ['/assets/neotro-TEST.css'] },
    styles: {},
    assets: { 'logo.svg': '/assets/static/logo-TEST.svg' },
    files: meta,
    public: publicFiles,
  };
  const hash = `sha256-${createHash('sha256').update(THEME).digest('base64')}`;
  await writeFile(join(dir, 'manifest.json'), JSON.stringify(manifest));
  await writeFile(join(dir, 'csp.json'), JSON.stringify({ scriptHashes: [hash], themeScript: THEME }));
  return { dir, hash };
}

const pokerSnapshot = {
  sessionId: '5e551011-0000-4000-8000-000000000001',
  code: 'FAKE123456',
  presenceEnabled: true,
  followSpotlight: true,
  observers: [],
  rounds: [],
  votes: [],
  roster: [],
  presence: [],
};
const voteState = (pid, extra) => ({ k: 'poker.vote.state', d: { roundId: ROUND, pid, ...extra } });
const voteCmd = (q, points = 5) => ({ t: 'cmd', r: POKER, q, k: 'poker.vote.set', d: { roundId: ROUND, points } });

let dist;
let fake;

before(async () => {
  dist = await makeDist();
});

after(async () => {
  await rm(dist.dir, { recursive: true, force: true });
});

beforeEach(async () => {
  fake = await startFakeServer({ dist: dist.dir, helloTimeoutMs: 300 });
});

afterEach(async () => {
  await fake.close();
});

async function hello(subs = [], cid = CID) {
  const client = await TestClient.connect(fake);
  client.send({ t: 'hello', v: 1, cid, subs });
  const welcome = await client.next();
  assert.equal(welcome.t, 'welcome');
  return client;
}

describe('WebSocket codec', () => {
  test('frames of every length class round trip, masked and unmasked', () => {
    for (const size of [0, 125, 126, 65535, 65536, 70000]) {
      const payload = Buffer.alloc(size, 0x61);
      for (const mask of [false, true]) {
        const frame = encodeFrame(OP.text, payload, { mask });
        const conn = Object.create(WsConnection.prototype);
        conn.buffer = frame;
        conn.maxMessageBytes = Infinity;
        const read = conn.readFrame();
        assert.equal(read.payload.length, size);
        assert.equal(read.masked, mask);
        assert.ok(read.payload.equals(payload));
      }
    }
  });

  test('a fragmented message is reassembled and a ping is answered', async () => {
    const socket = await connectRaw();
    const text = JSON.stringify({ t: 'hello', v: 1, cid: CID, subs: [] });
    const pong = new Promise((done) => socket.once('pong', done));
    socket.sendFrame(OP.ping, Buffer.from('hi'));
    assert.equal((await pong).toString(), 'hi');
    socket.sendFrame(OP.text, Buffer.from(text.slice(0, 10)), { fin: false });
    socket.sendFrame(OP.continuation, Buffer.from(text.slice(10)));
    const message = await new Promise((done) => socket.once('message', done));
    assert.equal(JSON.parse(message).t, 'welcome');
    socket.close();
  });

  test('a binary frame closes the socket', async () => {
    const socket = await connectRaw();
    const closed = new Promise((done) => socket.once('close', done));
    socket.sendFrame(OP.binary, Buffer.from([1, 2, 3]));
    assert.equal(await closed, 1003);
  });

  async function connectRaw() {
    return connectWebSocket(fake.wsUrl, { origin: fake.origin });
  }
});

describe('frame validation follows protocol.gen.js', () => {
  test('accepts well-formed frames of both directions', () => {
    assert.deepEqual(checkClientFrame(voteCmd(1)).errors, []);
    assert.deepEqual(checkClientFrame({ t: 'hello', v: 1, cid: CID, subs: [{ r: POKER, e: newEpoch(), s: 4 }] }).errors, []);
    assert.deepEqual(checkServerFrame({ t: 'nack', r: POKER, q: 2, code: 'server_busy', retry: true }), []);
    assert.deepEqual(checkServerFrame({ t: 'tx', r: POKER, s: 1, ev: [voteState(ME, { status: 'locked' })] }), []);
    assert.deepEqual(checkServerFrame({ t: 'x', r: POKER, k: 'jira.meta', d: { key: 'RS-1' } }), []);
  });

  test('refuses unknown members, kinds, wrong rooms and bad codes', () => {
    const cases = [
      [checkClientFrame({ ...voteCmd(1), extra: 1 }).errors, /unknown member "extra"/],
      [checkClientFrame({ ...voteCmd(1), k: 'poker.vote.nope' }).errors, /unknown kind/],
      [checkClientFrame({ ...voteCmd(1), r: BOARD }).errors, /targets poker rooms/],
      [checkClientFrame({ t: 'cmd', r: POKER, q: 1, k: 'poker.vote.set', d: { roundId: ROUND } }).errors, /missing "points"/],
      [checkClientFrame({ t: 'hello', v: 2, cid: CID, subs: [] }).errors, /invalid "v"/],
      [checkServerFrame({ t: 'nack', r: POKER, q: 2, code: 'server_busy', retry: false }), /retry:true/],
      [checkServerFrame({ t: 'nack', r: POKER, q: 2, code: 'nope', retry: false }), /invalid "code"/],
      [checkServerFrame({ t: 'tx', r: BOARD, s: 1, ev: [voteState(ME, {})] }), /not emitted by board/],
      [checkServerFrame({ t: 'tx', r: POKER, s: 1, ev: [voteState(ME, { votes: 1 })] }), /unknown member "votes"/],
      [checkServerFrame({ t: 'kick', r: POKER, code: 'bored' }), /invalid "code"/],
      [checkServerFrame({ t: 'snap', r: 'team:5e551011-0000-4000-8000-000000000001', e: newEpoch(), s: 0, lastQ: 0, d: {}, o: {} }), /no overlay/],
      [checkServerFrame({ t: 'snap', r: POKER, e: 'XYZ', s: 0, lastQ: 0, d: {} }), /invalid "e"/],
    ];
    for (const [errors, pattern] of cases) {
      assert.ok(errors.some((e) => pattern.test(e)), `${pattern} not in ${JSON.stringify(errors)}`);
    }
  });

  test('a script cannot send an invalid frame', async () => {
    fake.room(POKER, { snapshot: pokerSnapshot });
    assert.throws(() => fake.tx(POKER, [{ k: 'retro.card.added', d: {} }]), /not emitted by poker/);
  });
});

describe('HTTP', () => {
  test('a shell route gets the shell with boot JSON, theme script and route assets', async () => {
    fake.boot = (path) => ({ theme: 'dark', me: null, path, note: '</script><b>' });
    const res = await fetch(`${fake.url}/poker/ABC?round=2`);
    assert.equal(res.status, 200);
    assert.match(res.headers.get('content-type'), /text\/html/);
    const csp = res.headers.get('content-security-policy');
    assert.ok(csp.includes(`script-src 'self' '${dist.hash}'`));
    assert.ok(csp.includes("trusted-types 'none'"));
    assert.ok(csp.includes(`connect-src 'self' ws://${fake.host}`));
    const html = await res.text();
    assert.ok(html.includes('"note":"\\u003c/script>\\u003cb>"'));
    assert.ok(html.includes('"path":"/poker/ABC?round=2"'));
    const order = ['/assets/kit-TEST.css', '/assets/neotro-TEST.css', 'modulepreload" href="/assets/chunks/shared-TEST.js',
      'modulepreload" href="/assets/chunks/poker-TEST.js', 'id="boot"', THEME, 'src="/assets/entry-TEST.js"', 'id="app"'];
    const positions = order.map((s) => html.indexOf(s));
    assert.ok(positions.every((p, i) => p >= 0 && (i === 0 || p > positions[i - 1])), JSON.stringify(positions));
  });

  test('serves manifest files with type and etag, /api/boot, and 404s the rest', async () => {
    const js = await fetch(`${fake.url}/assets/chunks/poker-TEST.js`);
    assert.equal(js.status, 200);
    assert.match(js.headers.get('content-type'), /text\/javascript/);
    assert.ok(js.headers.get('etag'));
    assert.match(js.headers.get('cache-control'), /immutable/);
    assert.equal((await fetch(`${fake.url}/manifest.json`)).status, 404);
    assert.equal((await fetch(`${fake.url}/nope`)).status, 404);
    const boot = await (await fetch(`${fake.url}/api/boot?path=/retro/X`)).json();
    assert.equal(boot.path, '/retro/X');
    const missing = await fetch(`${fake.url}/api/nothing`);
    assert.equal(missing.status, 404);
    assert.equal(missing.headers.get('content-type'), 'application/problem+json');
    assert.equal((await missing.json()).code, 'not_found');
  });

  test('the old /dashboard address sends visitors home with a no-store 302, as the real shell does', async () => {
    const old = await fetch(`${fake.url}/dashboard`, { redirect: 'manual' });
    assert.equal(old.status, 302);
    assert.equal(old.headers.get('location'), '/');
    assert.equal(old.headers.get('cache-control'), 'no-store');
  });

  test('serves the root files with a day of caching and ETag revalidation, and links them from the shell', async () => {
    const types = { '/favicon.ico': 'image/x-icon', '/manifest.webmanifest': 'application/manifest+json', '/robots.txt': 'text/plain; charset=utf-8' };
    for (const [path, type] of Object.entries(types)) {
      const response = await fetch(`${fake.url}${path}`);
      assert.equal(response.status, 200, path);
      assert.equal(response.headers.get('content-type'), type);
      assert.equal(response.headers.get('cache-control'), 'public, max-age=86400');
      const etag = response.headers.get('etag');
      assert.equal(etag, `"${path.slice(1)}"`);
      assert.equal((await fetch(`${fake.url}${path}`, { headers: { 'If-None-Match': etag } })).status, 304);
    }
    assert.equal(await (await fetch(`${fake.url}/robots.txt`)).text(), 'User-agent: *\n');
    const html = await (await fetch(`${fake.url}/poker/ABC`)).text();
    for (const link of ['<meta name="theme-color" content="#111827">', '<link rel="icon" href="/favicon.ico" sizes="48x48">',
      '<link rel="icon" href="/assets/static/logo-TEST.svg" type="image/svg+xml">', '<link rel="manifest" href="/manifest.webmanifest">']) {
      assert.ok(html.includes(link), link);
    }
  });

  test('fixture routes take params, functions and override earlier ones; requests are recorded', async () => {
    fake.route('GET', '/api/teams/:id', json({ name: 'first' }));
    fake.route('GET', '/api/teams/:id', ({ params, query }) => json({ id: params.id, tab: query.get('tab') }));
    fake.route('POST', '/api/retro', ({ body }) => json({ got: body }, 201));
    const team = await (await fetch(`${fake.url}/api/teams/t%201?tab=boards`)).json();
    assert.deepEqual(team, { id: 't 1', tab: 'boards' });
    const created = await fetch(`${fake.url}/api/retro`, { method: 'POST', headers: { 'X-RS': '1' }, body: '{"title":"x"}' });
    assert.equal(created.status, 201);
    assert.deepEqual(await created.json(), { got: { title: 'x' } });
    assert.deepEqual(fake.requests.map((r) => `${r.method} ${r.path}`), ['GET /api/teams/t%201', 'POST /api/retro']);
  });

  test('writes need X-RS and a same Origin, like the CSRF filter', async () => {
    fake.route('POST', '/api/retro', json({}));
    const noHeader = await fetch(`${fake.url}/api/retro`, { method: 'POST', body: '{}' });
    assert.equal(noHeader.status, 403);
    assert.equal((await noHeader.json()).code, 'csrf');
    const crossSite = await fetch(`${fake.url}/api/retro`, { method: 'POST', body: '{}', headers: { 'X-RS': '1', Origin: 'http://evil.test' } });
    assert.equal(crossSite.status, 403);
  });

  test('problem() responses carry problem+json and a delay is honoured', async () => {
    fake.route('GET', '/api/slow', { ...problem(503, 'server_busy'), delayMs: 50, headers: { 'Retry-After': '1' } });
    const started = Date.now();
    const res = await fetch(`${fake.url}/api/slow`);
    assert.ok(Date.now() - started >= 45);
    assert.equal(res.status, 503);
    assert.equal(res.headers.get('retry-after'), '1');
    assert.equal(res.headers.get('content-type'), 'application/problem+json');
  });
});

describe('timer track', () => {
  const bytes = Buffer.from(Array.from({ length: 1000 }, (_, i) => i % 256));
  let file;

  beforeEach(async () => {
    file = join(dist.dir, 'track.wav');
    await writeFile(file, bytes);
  });

  test('without a file the route is 404 and the retro boot has no flag', async () => {
    assert.equal((await fetch(`${fake.url}/media/timer-music`)).status, 404);
    fake.boot = { build: 'fake', me: null };
    assert.equal((await (await fetch(`${fake.url}/api/boot?path=/retro/ABC`)).json()).timerMusic, undefined);
  });

  test('a configured file is served whole, by range, and revalidated by ETag', async () => {
    fake.musicFile = file;
    const whole = await fetch(`${fake.url}/media/timer-music`);
    assert.equal(whole.status, 200);
    assert.equal(whole.headers.get('content-type'), 'audio/wav');
    assert.equal(whole.headers.get('accept-ranges'), 'bytes');
    assert.equal(whole.headers.get('x-content-type-options'), 'nosniff');
    assert.deepEqual(Buffer.from(await whole.arrayBuffer()), bytes);

    const part = await fetch(`${fake.url}/media/timer-music`, { headers: { Range: 'bytes=100-199' } });
    assert.equal(part.status, 206);
    assert.equal(part.headers.get('content-range'), 'bytes 100-199/1000');
    assert.deepEqual(Buffer.from(await part.arrayBuffer()), bytes.subarray(100, 200));

    const tail = await fetch(`${fake.url}/media/timer-music`, { headers: { Range: 'bytes=-10' } });
    assert.deepEqual(Buffer.from(await tail.arrayBuffer()), bytes.subarray(990));
    const past = await fetch(`${fake.url}/media/timer-music`, { headers: { Range: 'bytes=2000-' } });
    assert.equal(past.status, 416);
    await past.arrayBuffer();
    const again = await fetch(`${fake.url}/media/timer-music`, { headers: { 'If-None-Match': whole.headers.get('etag') } });
    assert.equal(again.status, 304);
  });

  test('the boot of a retro board, and only a retro board, says a track exists', async () => {
    fake.musicFile = file;
    fake.boot = { build: 'fake', me: null };
    assert.equal((await (await fetch(`${fake.url}/api/boot?path=/retro/ABC`)).json()).timerMusic, true);
    assert.equal((await (await fetch(`${fake.url}/api/boot?path=/poker/ABC`)).json()).timerMusic, undefined);
  });
});

describe('WebSocket protocol', () => {
  test('hello gets welcome then a snapshot per sub; ping gets pong on the shifted clock', async () => {
    fake.clockOffsetMs = 30_000;
    const room = fake.room(POKER, { snapshot: pokerSnapshot, overlay: { own: [], heldByThisTab: false } });
    const client = await TestClient.connect(fake);
    const before = Date.now();
    client.send({ t: 'hello', v: 1, cid: CID, subs: [{ r: POKER }] });
    const welcome = await client.next();
    assert.deepEqual(welcome.me, fake.me);
    assert.ok(welcome.n >= before + 30_000);
    const snap = await client.next();
    assert.deepEqual(snap, { t: 'snap', r: POKER, e: room.epoch, s: 0, lastQ: 0, d: pokerSnapshot, o: { own: [], heldByThisTab: false } });
    client.send({ t: 'ping', c: 123 });
    const pong = await client.next();
    assert.equal(pong.c, 123);
    assert.ok(pong.n >= before + 30_000);
    assert.equal(fake.violations.length, 0);
  });

  test('resume: current {e,s} gets an empty replay, older s gets the missed txs, a foreign epoch a snapshot', async () => {
    const room = fake.room(POKER, { snapshot: pokerSnapshot });
    fake.tx(POKER, [voteState(ME, { status: 'selected' })]);
    fake.tx(POKER, [voteState(ME, { status: 'locked' })]);
    const client = await hello([{ r: POKER, e: room.epoch, s: 2 }]);
    assert.deepEqual(await client.next(), { t: 'replay', r: POKER, e: room.epoch, from: 3, lastQ: 0, txs: [] });
    client.send({ t: 'sub', r: POKER, e: room.epoch, s: 1 });
    const replay = await client.next();
    assert.equal(replay.from, 2);
    assert.deepEqual(replay.txs.map((t) => t.s), [2]);
    client.send({ t: 'sub', r: POKER, e: newEpoch(), s: 1 });
    assert.equal((await client.next()).t, 'snap');
  });

  test('an unknown room is kicked with not_found; kick and unsub drop the subscription', async () => {
    fake.room(POKER, { snapshot: pokerSnapshot });
    const client = await hello([{ r: BOARD }, { r: POKER }]);
    assert.deepEqual(await client.next(), { t: 'kick', r: BOARD, code: 'not_found' });
    assert.equal((await client.next()).t, 'snap');
    fake.kick(POKER, 'access_revoked');
    assert.deepEqual(await client.next(), { t: 'kick', r: POKER, code: 'access_revoked' });
    client.send(voteCmd(1));
    assert.equal((await client.next()).code, 'not_subscribed');
  });

  test('commands: default ack, commit sends tx before ack, nack carries the retry flag, dup on resend', async () => {
    fake.room(POKER, { snapshot: pokerSnapshot });
    const client = await hello([{ r: POKER }]);
    await client.next();

    client.send(voteCmd(1));
    assert.deepEqual(await client.next(), { t: 'ack', r: POKER, q: 1, s: 0 });

    fake.onCommand('poker.vote.set', (ctx) => ctx.commit([voteState(ctx.conn.me.pid, { points: ctx.frame.d.points, locked: false, abstained: false })]));
    client.send(voteCmd(2, 8));
    const tx = await client.next();
    assert.equal(tx.t, 'tx');
    assert.equal(tx.s, 1);
    assert.equal(tx.ev[0].d.points, 8);
    assert.deepEqual(await client.next(), { t: 'ack', r: POKER, q: 2, s: 1 });

    fake.onCommand('poker.vote.set', (ctx) => ctx.nack('server_busy'));
    client.send(voteCmd(3));
    assert.deepEqual(await client.next(), { t: 'nack', r: POKER, q: 3, code: 'server_busy', retry: true });
    fake.onCommand('poker.vote.set', (ctx) => ctx.nack('stale', { msg: 'moved on', d: { current: { version: 4 } } }));
    client.send(voteCmd(4));
    assert.deepEqual(await client.next(), { t: 'nack', r: POKER, q: 4, code: 'stale', retry: false, msg: 'moved on', d: { current: { version: 4 } } });

    client.send(voteCmd(2, 8));
    assert.deepEqual(await client.next(), { t: 'ack', r: POKER, q: 2, s: 1, dup: true });
  });

  test('a dup whose payload differs from the recorded one is nacked cid_reuse', async () => {
    fake.room(POKER, { snapshot: pokerSnapshot });
    const client = await hello([{ r: POKER }]);
    await client.next();
    client.send(voteCmd(1, 3));
    assert.deepEqual(await client.next(), { t: 'ack', r: POKER, q: 1, s: 0 });
    client.send(voteCmd(1, 3));
    assert.deepEqual(await client.next(), { t: 'ack', r: POKER, q: 1, s: 0, dup: true });
    client.send(voteCmd(1, 8));
    assert.deepEqual(await client.next(), { t: 'nack', r: POKER, q: 1, code: 'cid_reuse', retry: false });
  });

  test('seedCommands scripts another tab on the same cid; a restart forgets the hashes', async () => {
    fake.room(POKER, { snapshot: pokerSnapshot });
    fake.seedCommands(POKER, CID, [{ q: 4, k: 'poker.vote.set', d: { roundId: ROUND, points: 13 } }]);
    const client = await hello([{ r: POKER }]);
    assert.equal((await client.next()).lastQ, 4);
    client.send(voteCmd(4, 5));
    assert.deepEqual(await client.next(), { t: 'nack', r: POKER, q: 4, code: 'cid_reuse', retry: false });
    client.send(voteCmd(4, 13));
    assert.deepEqual(await client.next(), { t: 'ack', r: POKER, q: 4, s: 0, dup: true });
    await client.close();

    fake.bye({ newEpoch: true });
    const again = await hello([{ r: POKER }]);
    assert.equal((await again.next()).lastQ, 4);
    again.send(voteCmd(4, 5));
    assert.deepEqual(await again.next(), { t: 'ack', r: POKER, q: 4, s: 0, dup: true });
  });

  test('a handler that answers nothing leaves the command pending; lastQ comes back on the next snap', async () => {
    fake.room(POKER, { snapshot: pokerSnapshot });
    let held;
    fake.onCommand('*', (ctx) => {
      held = ctx;
    });
    const client = await hello([{ r: POKER }]);
    await client.next();
    client.send(voteCmd(7));
    await fake.waitForFrame((f) => f.t === 'cmd' && f.q === 7);
    await client.expectSilence(50);
    held.ack();
    assert.equal((await client.next()).q, 7);
    await client.close();
    const again = await hello([{ r: POKER }]);
    assert.equal((await again.next()).lastQ, 7);
  });

  test('per-viewer events let a test project vote secrecy', async () => {
    fake.room(POKER, { snapshot: pokerSnapshot });
    fake.me = (req) => ({ pid: req.headers['x-test-pid'] ?? ME, kind: 'user', name: 'Viewer' });
    const a = await hello([{ r: POKER }]);
    const b = await hello([{ r: POKER }], OTHER_CID);
    await a.next();
    await b.next();
    const voter = fake.connections[0];
    fake.tx(POKER, (viewer) => [voteState(ME, viewer === voter ? { points: 5 } : { status: 'selected' })]);
    assert.equal((await a.next()).ev[0].d.points, 5);
    assert.deepEqual((await b.next()).ev[0].d, { roundId: ROUND, pid: ME, status: 'selected' });
  });

  test('gap: the next tx skips a seq, and resubscribing replays the skipped entries', async () => {
    const room = fake.room(POKER, { snapshot: pokerSnapshot });
    const client = await hello([{ r: POKER }]);
    await client.next();
    fake.gap(POKER);
    fake.tx(POKER, [voteState(ME, { status: 'locked' })]);
    assert.equal((await client.next()).s, 2);
    client.send({ t: 'sub', r: POKER, e: room.epoch, s: 0 });
    const replay = await client.next();
    assert.deepEqual(replay.txs.map((t) => [t.s, t.ev.length]), [[1, 0], [2, 1]]);
  });

  test('x frames reach subscribers', async () => {
    fake.room(POKER, { snapshot: pokerSnapshot });
    const client = await hello([{ r: POKER }]);
    await client.next();
    fake.x(POKER, 'jira.meta', { key: 'RS-1', summary: 'Epic' });
    assert.deepEqual(await client.next(), { t: 'x', r: POKER, k: 'jira.meta', d: { key: 'RS-1', summary: 'Epic' } });
  });

  test('bye sends retryMs then closes 1012; with a new epoch the resubscribe gets a snapshot', async () => {
    const room = fake.room(POKER, { snapshot: pokerSnapshot });
    const oldEpoch = room.epoch;
    const client = await hello([{ r: POKER }]);
    await client.next();
    fake.bye({ retryMs: 2600, newEpoch: true });
    assert.deepEqual(await client.next(), { t: 'bye', code: 'restart', retryMs: 2600 });
    assert.equal((await client.closed).code, 1012);
    const again = await hello([{ r: POKER, e: oldEpoch, s: 0 }]);
    const snap = await again.next();
    assert.equal(snap.t, 'snap');
    assert.notEqual(snap.e, oldEpoch);
  });

  test('a hello with nobody signed in (me resolves to null) is closed with 4401', async () => {
    fake.me = () => null;
    const client = await TestClient.connect(fake);
    client.send({ t: 'hello', v: 1, cid: CID, subs: [] });
    assert.equal((await client.closed).code, 4401);
  });

  test('cid in use and rejectNextHello close with 4409', async () => {
    await hello();
    const second = await TestClient.connect(fake);
    second.send({ t: 'hello', v: 1, cid: CID, subs: [] });
    assert.equal((await second.closed).code, 4409);
    fake.rejectNextHello(4409);
    const third = await TestClient.connect(fake);
    third.send({ t: 'hello', v: 1, cid: OTHER_CID, subs: [] });
    assert.equal((await third.closed).code, 4409);
    const fourth = await hello([], OTHER_CID);
    await fourth.expectSilence(20);
  });

  test('protocol violations: no hello, wrong first frame, bad origin, oversize frame, invalid command', async () => {
    const silent = await TestClient.connect(fake);
    assert.equal((await silent.closed).code, 4400);

    const early = await TestClient.connect(fake);
    early.send({ t: 'ping', c: 1 });
    assert.equal((await early.closed).code, 4400);

    const foreign = await TestClient.connect(fake, { origin: 'http://evil.test' });
    assert.equal((await foreign.closed).code, 4403);

    const big = await hello();
    big.send(JSON.stringify({ t: 'ping', c: 1, pad: 'x'.repeat(70_000) }));
    assert.equal((await big.closed).code, 4413);

    fake.room(POKER, { snapshot: pokerSnapshot });
    const client = await hello([{ r: POKER }], OTHER_CID);
    await client.next();
    client.send({ ...voteCmd(1), k: 'poker.vote.nope' });
    const nack = await client.next();
    assert.equal(nack.code, 'invalid');
    assert.equal(nack.q, 1);
    assert.ok(fake.violations.some((v) => v.errors.some((e) => /unknown kind/.test(e))));
    client.send({ t: 'sub', r: 'poker:not-a-uuid' });
    assert.equal((await client.closed).code, 4400);
  });

  test('waitForConnection and waitForFrame resolve on what arrives later', async () => {
    const waiting = fake.waitForConnection();
    const client = await hello();
    const conn = await waiting;
    assert.equal(conn.cid, CID);
    const frame = fake.waitForFrame((f) => f.t === 'ping');
    client.send({ t: 'ping', c: 9 });
    assert.equal((await frame).frame.c, 9);
    await assert.rejects(fake.waitForFrame((f) => f.t === 'unsub', { timeout: 30 }), /timed out/);
  });
});

describe('transcript replay', () => {
  const transcript = loadTranscript(SAMPLE);
  const serverFrames = transcript.frames.filter((f) => !['welcome', 'hello', 'cmd'].includes(f.t));

  test('replays every server frame verbatim, as the transcript viewer', async () => {
    fake.replayTranscript(SAMPLE);
    const client = await TestClient.connect(fake);
    client.send({ t: 'hello', v: 1, cid: CID, subs: [{ r: POKER }] });
    const welcome = await client.next();
    assert.deepEqual(welcome.me, transcript.frames[0].me);
    for (const expected of serverFrames) {
      assert.deepEqual(await client.next(), expected);
    }
    await client.expectSilence(30);
  });

  test('after the transcript the room resumes from its log and live txs continue the seq', async () => {
    fake.replayTranscript(SAMPLE);
    const room = fake.rooms.get(POKER);
    assert.equal(room.epoch, '00a1b2c3d4e5f607');
    assert.equal(room.seq, 6);
    const client = await hello([{ r: POKER, e: room.epoch, s: 4 }]);
    const replay = await client.next();
    assert.deepEqual(replay.txs.map((t) => t.s), [5, 6]);
    fake.tx(POKER, [voteState(ME, { status: 'empty' })]);
    assert.equal((await client.next()).s, 7);
  });

  test('step mode releases frames one at a time, and a late subscriber catches up', async () => {
    const player = fake.replayTranscript(SAMPLE, { step: true });
    const first = await hello([{ r: POKER }]);
    await first.expectSilence(30);
    player.next(2);
    assert.equal((await first.next()).t, 'snap');
    assert.equal((await first.next()).t, 'tx');
    const late = await hello([{ r: POKER }], OTHER_CID);
    assert.equal((await late.next()).t, 'snap');
    assert.equal((await late.next()).t, 'tx');
    player.rest();
    assert.equal(player.done, true);
    for (const client of [first, late]) {
      assert.deepEqual((await client.next()).t, 'ack');
    }
  });

  test('a transcript with an invalid frame, mixed rooms or no opening snapshot is refused', () => {
    const bad = (frames) => () => parseTranscript({ frames });
    const tx = serverFrames.find((f) => f.t === 'tx');
    assert.throws(bad([serverFrames[0], { ...tx, ev: [{ k: 'poker.nope', d: {} }] }]), /frame 1 .*unknown event kind/);
    assert.throws(bad([serverFrames[0], { ...tx, r: 'poker:5e551011-0000-4000-8000-000000000002' }]), /is about/);
    assert.throws(bad([tx]), /must start with the snap/);
    assert.throws(() => parseTranscript({}), /"frames" array/);
  });
});

describe('fake Jira proxy', () => {
  const TEAM = '7ea70000-0000-4000-8000-000000000001';
  const base = () => `${fake.url}/api/teams/${TEAM}/jira`;
  const write = (method, path, body) => fetch(`${base()}${path}`, {
    method,
    headers: { 'X-RS': '1', 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });

  test('browse buckets sprints then backlog and filters by text, status and points', async () => {
    fake.jira({ teamId: TEAM });
    const all = await (await fetch(`${base()}/browse`)).json();
    assert.deepEqual(all.buckets.map((b) => b.name), ['Sprint 9', 'Sprint 10', 'Backlog']);
    assert.ok(!all.buckets.flatMap((b) => b.issues).some((i) => i.issueType === 'Epic'));
    const text = await (await fetch(`${base()}/browse?q=saved`)).json();
    assert.deepEqual(text.buckets.flatMap((b) => b.issues.map((i) => i.key)), ['RS-12']);
    const pointed = await (await fetch(`${base()}/browse?points=pointed&status=new`)).json();
    assert.deepEqual(pointed.buckets.flatMap((b) => b.issues.map((i) => i.key)), ['RS-13']);
  });

  test('meta and detail: parent summaries, island content and comments as DocNodes', async () => {
    fake.jira({ teamId: TEAM });
    const meta = await (await write('POST', '/meta', { keys: ['RS-12', 'RS-404'] })).json();
    assert.deepEqual(meta.items.map((i) => [i.key, i.parentSummary]), [['RS-12', 'Checkout epic']]);
    const detail = await (await fetch(`${base()}/issues/RS-12`)).json();
    assert.deepEqual(detail.islandIds, ['t0b1e5c0ffee', 'r1chL1ne0001', 'pane1Warn001', 'shotMedia001', 'rs12Note0001', 'rs12Roadmap1']);
    assert.ok(isAllowedDoc(detail.description), 'the fixture islands use the DocNode wire shape');
    const island = detail.description.c.find((n) => n.t === 'island');
    assert.equal(island.c[0].t, 'table');
    assert.equal(detail.comments[0].body.t, 'doc');
    assert.equal(detail.commentTotal, 2);
    const png = await fetch(`${base()}/issues/RS-12/attachments/20001`);
    assert.equal(png.headers.get('content-type'), 'image/png');
    assert.equal((await fetch(`${base()}/issues/RS-12/attachments/99999`)).status, 404);
  });

  test('description writes need the current updated value and known islands', async () => {
    const jira = fake.jira({ teamId: TEAM });
    const { updated } = await (await fetch(`${base()}/issues/RS-12`)).json();
    const saved = await write('PATCH', '/issues/RS-12', { description: 'New text\n\n[[island:t0b1e5c0ffee]]', descriptionBase: updated });
    assert.equal(saved.status, 200);
    const after = await saved.json();
    assert.notEqual(after.updated, updated);
    const stale = await write('PATCH', '/issues/RS-12', { description: 'Again', descriptionBase: updated });
    assert.equal(stale.status, 409);
    assert.equal((await stale.json()).code, 'stale');
    const lostIsland = await write('PATCH', '/issues/RS-12', { description: '[[island:gone]]', descriptionBase: after.updated });
    assert.equal(lostIsland.status, 409);
    jira.touch('RS-12');
    const edited = await write('PATCH', '/issues/RS-12', { description: 'x', descriptionBase: after.updated });
    assert.equal(edited.status, 409);
  });

  test('the rich editor loads the projected ADF of a fixture issue, or ADF built from markup', async () => {
    fake.jira({ teamId: TEAM });
    const view = await (await fetch(`${base()}/issues/RS-12/description`)).json();
    const detail = await (await fetch(`${base()}/issues/RS-12`)).json();
    assert.equal(view.updated, detail.updated);
    assert.equal(view.editable, true);
    assert.deepEqual(view.adf.content.map((n) => n.type), ['paragraph', 'table', 'paragraph', 'panel', 'mediaSingle', 'paragraph', 'rsOpaque', 'bulletList']);
    assert.deepEqual(view.media, [{ id: view.adf.content[4].content[0].attrs.id, attachmentId: '20001', name: 'screenshot.png', image: true, w: 640, h: 360 }]);
    assert.deepEqual(view.opaque.map((p) => [p.kind, p.label]), [['inlineExtension', 'Sprint 10'], ['extension', 'Payments roadmap']]);
    assert.deepEqual(view.marks.map((m) => m.kind), ['annotation']);
    assert.equal(typeof view.adf.content[5].attrs.rsKeep, 'string');
    assert.equal(view.limits.nodes, 5000);
    const plain = await (await fetch(`${base()}/issues/RS-13/description`)).json();
    assert.equal(plain.adf.type, 'doc');
    assert.deepEqual([plain.opaque, plain.marks], [[], []]);
  });

  test('rich description writes need the current updated value and restore tokens byte for byte', async () => {
    const jira = fake.jira({ teamId: TEAM });
    const original = jira.issues.get('RS-12').adf;
    const { updated, adf, opaque } = await (await fetch(`${base()}/issues/RS-12/description`)).json();
    const conflict = await write('PATCH', '/issues/RS-12', { description: 'x', descriptionAdf: adf, descriptionBase: updated });
    assert.equal((await conflict.json()).fields.descriptionAdf, 'conflict');
    const unknown = { ...adf, content: [...adf.content, { type: 'rsOpaque', attrs: { ref: 'AAAAAAAAAAAAAAAA' } }] };
    assert.equal((await write('PATCH', '/issues/RS-12', { descriptionAdf: unknown, descriptionBase: updated })).status, 409);
    const twice = { ...adf, content: [...adf.content, adf.content[6]] };
    const duplicated = await write('PATCH', '/issues/RS-12', { descriptionAdf: twice, descriptionBase: updated });
    assert.equal(duplicated.status, 400);
    assert.deepEqual((await duplicated.json()).fields, { descriptionAdf: 'opaque_duplicated', 'descriptionAdf.at': 'content.8' });

    const edited = { ...adf, content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Rewritten', marks: [{ type: 'strong' }] }] }, ...adf.content.slice(1)] };
    const saved = await write('PATCH', '/issues/RS-12', { descriptionAdf: edited, descriptionBase: updated });
    assert.equal(saved.status, 200);
    const after = await saved.json();
    assert.deepEqual(after.description.c[0], { t: 'p', c: [{ t: 'b', c: [{ t: 'text', v: 'Rewritten' }] }] });
    assert.equal(after.description.c[1].t, 'island');
    const issue = jira.issues.get('RS-12');
    for (const part of opaque) {
      assert.ok(issue.adfText.includes(jira.originals('RS-12').opaque[part.ref][0]), `${part.kind} kept byte for byte`);
    }
    assert.deepEqual(issue.adf.content.slice(1), original.content.slice(1));
    const again = await (await fetch(`${base()}/issues/RS-12/description`)).json();
    assert.deepEqual(again.adf, edited);
    assert.equal(again.opaque.length, 2);

    const stale = await write('PATCH', '/issues/RS-12', { descriptionAdf: edited, descriptionBase: updated });
    assert.equal(stale.status, 409);
    const cleared = await write('PATCH', '/issues/RS-12', { descriptionAdf: null, descriptionBase: after.updated });
    assert.deepEqual((await cleared.json()).description, { t: 'doc', c: [] });
  });

  test('the read view rebuilt from ADF is the one the fixtures serve from markup and islands', async () => {
    const { readFile } = await import('node:fs/promises');
    for (const [file, key] of [['fixtures/jira.json', 'RS-12'], ['demo/jira.json', 'DEMO-123']]) {
      const issue = JSON.parse(await readFile(join(here, file), 'utf8')).issues.find((i) => i.key === key);
      const doc = parse(issue.description, { dialect: DIALECT_JIRA_DESCRIPTION });
      const fromAdf = adfToDoc(issue.adf, issue.attachments);
      const normal = (d) => ({ ...d, c: d.c.map((n) => (n.t === 'island' ? { ...n, id: 'island', c: n.c?.length ? n.c : [issue.islands[n.id]] } : n)) });
      assert.deepEqual(normal(fromAdf), normal(doc), key);
    }
  });

  test('points, comments, create, transitions and links are kept', async () => {
    const jira = fake.jira({ teamId: TEAM });
    assert.equal((await write('PUT', '/issues/RS-12/points', { points: 2.5 })).status, 200);
    assert.equal((await write('PUT', '/issues/RS-12/points', { points: 2.3 })).status, 422);
    assert.equal(jira.issues.get('RS-12').points, 2.5);
    assert.equal((await write('POST', '/issues/RS-12/comments', { body: 'Sent' })).status, 201);
    assert.equal((await (await fetch(`${base()}/issues/RS-12/comments`)).json()).total, 3);
    const created = await (await write('POST', '/issues', { summary: 'From retro', description: 'Do it' })).json();
    assert.equal(created.key, 'RS-32');
    const clone = await (await write('POST', '/issues', { summary: 'Clone', cloneFrom: 'RS-12' })).json();
    assert.deepEqual((await (await fetch(`${base()}/issues/${clone.key}`)).json()).islandIds, ['t0b1e5c0ffee', 'r1chL1ne0001', 'pane1Warn001', 'shotMedia001', 'rs12Note0001', 'rs12Roadmap1']);
    assert.equal((await write('POST', '/issues/RS-13/transition', { transitionId: '31' })).status, 204);
    assert.equal(jira.issues.get('RS-13').statusCategory, 'done');
    const link = await (await write('POST', '/issues/RS-13/links', { typeId: '10000', targetKey: 'RS-12' })).json();
    assert.equal((await write('DELETE', `/issues/RS-12/links/${link.id}`)).status, 404);
    assert.equal((await write('DELETE', `/issues/RS-13/links/${link.id}`)).status, 204);
  });

  test('the sprint report serves the picker and the default sprint, any listed sprint, and refuses the rest', async () => {
    const jira = fake.jira({ teamId: TEAM });
    const first = await (await fetch(`${base()}/sprint-report`)).json();
    assert.deepEqual(first.sprints.map((s) => [s.id, s.state]), [[101, 'active'], [100, 'closed'], [99, 'closed'], [98, 'closed'], [97, 'closed']]);
    assert.equal(first.hasMoreSprints, true);
    assert.equal(first.empty, null);
    assert.equal(first.report.sprintId, 100);
    assert.deepEqual(first.report.groups.map((g) => [g.outcome, g.count, g.points]), [['done', 3, 5], ['doneAfter', 1, 5], ['notDone', 3, 8]]);
    for (const group of first.report.groups) {
      assert.equal(group.issues.length, group.count, group.outcome);
    }
    const active = await (await fetch(`${base()}/sprint-report?sprint=101`)).json();
    assert.equal(active.report.sprintId, 101);
    assert.ok(active.report.groups.flatMap((g) => g.issues).every((i) => i.next === null), 'an active sprint sends no next sprint');
    assert.equal((await (await fetch(`${base()}/sprint-report?sprint=102`)).json()).code, 'jira_not_found');
    const invalid = await fetch(`${base()}/sprint-report?sprint=0`);
    assert.equal(invalid.status, 400);
    assert.deepEqual((await invalid.json()).fields, { sprint: 'invalid' });
    assert.equal((await fetch(`${base()}/sprint-report?sprint=abc`)).status, 400);
    for (const count of ['0', '101', 'x', '']) {
      const refused = await fetch(`${base()}/sprint-report?sprints=${count}`);
      assert.equal(refused.status, 400, count);
      assert.deepEqual((await refused.json()).fields, { sprints: 'invalid' }, count);
    }
    jira.config.boardId = null;
    assert.deepEqual(await (await fetch(`${base()}/sprint-report`)).json(), { sprints: [], hasMoreSprints: false, empty: 'no_board', report: null });
    jira.config.configured = false;
    assert.equal((await fetch(`${base()}/sprint-report`)).status, 409);
  });

  test('a fixture without a sprintReport block has no sprints to report', async () => {
    const { readFile } = await import('node:fs/promises');
    const { sprintReport, ...fixture } = JSON.parse(await readFile(join(here, 'fixtures/jira.json'), 'utf8'));
    fake.jira({ teamId: TEAM, fixture });
    assert.deepEqual(await (await fetch(`${base()}/sprint-report`)).json(), { sprints: [], hasMoreSprints: false, empty: 'no_sprints', report: null });
  });

  test('the sprint report lists the newest `sprints` closed sprints, extended down to an older reported one', async () => {
    fake.jira({ teamId: TEAM });
    const closed = (body) => body.sprints.filter((s) => s.state === 'closed').map((s) => s.id);
    const eight = await (await fetch(`${base()}/sprint-report?sprints=8`)).json();
    assert.deepEqual([closed(eight), eight.hasMoreSprints], [[100, 99, 98, 97, 96, 95, 94, 93], true]);
    const all = await (await fetch(`${base()}/sprint-report?sprints=100`)).json();
    assert.deepEqual([closed(all).length, all.hasMoreSprints], [11, false]);
    const old = await (await fetch(`${base()}/sprint-report?sprint=94&sprints=4`)).json();
    assert.deepEqual([closed(old), old.hasMoreSprints, old.report.sprintId], [[100, 99, 98, 97, 96, 95, 94], true, 94]);
    assert.deepEqual(old.report.groups.map((g) => [g.outcome, g.count, g.issues.length]), [['done', 0, 0], ['doneAfter', 0, 0], ['notDone', 0, 0]]);
    const oldest = await (await fetch(`${base()}/sprint-report?sprint=90`)).json();
    assert.deepEqual([closed(oldest).length, oldest.hasMoreSprints], [11, false]);
  });

  test('errors: injected failures, unknown issues, other teams and unconfigured teams', async () => {
    const jira = fake.jira({ teamId: TEAM });
    jira.failNext({ ...problem(503, 'jira_unavailable'), headers: { 'Retry-After': '2' } });
    const failed = await fetch(`${base()}/browse`);
    assert.equal(failed.status, 503);
    assert.equal((await failed.json()).code, 'jira_unavailable');
    assert.equal((await fetch(`${base()}/browse`)).status, 200);
    assert.equal((await (await fetch(`${base()}/issues/RS-404`)).json()).code, 'jira_not_found');
    const other = await fetch(`${fake.url}/api/teams/someone-else/jira/browse`);
    assert.equal((await other.json()).code, 'jira_not_configured');
    jira.config.configured = false;
    assert.deepEqual(await (await fetch(base())).json(), { configured: false });
    assert.equal((await fetch(`${base()}/sprints`)).status, 409);
  });
});

test('a missing dist is reported with the build hint', async () => {
  await assert.rejects(new FakeServer({ dist: join(tmpdir(), 'rs-no-such-dist') }).listen(), /run \.\/build\.sh web/);
});
