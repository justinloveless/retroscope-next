import { after, afterEach, before, beforeEach, describe, test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { request } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { isAllowedDoc } from '../../../web/src/markup/allow.js';
import { uuidv7Time } from '../../../web/src/poker/model.js';
import { TestClient } from '../client.js';
import { startFakeServer } from '../server.js';
import { connectWebSocket } from '../ws.js';
import { ALL_HANDS_TEAM, GUILD_TEAM } from './data.js';
import { CODES, DEMO_TEAM, installDemo } from './index.js';
import { CROWD, DEMO, LENA, MARCUS, PRIYA, TOM } from './people.js';
import { winnerOf } from './poker.js';

const THEME = '(function(){document.documentElement.dataset.theme="light"})();';
const ROOT_FILES = { 'favicon.ico': 'ICO', 'manifest.webmanifest': '{"name":"Retroscope"}', 'robots.txt': 'User-agent: *\n' };

// A dist with the shape build.mjs writes, so these tests do not depend on ./build.sh web.
async function makeDist() {
  const dir = await mkdtemp(join(tmpdir(), 'rs-demo-dist-'));
  await mkdir(join(dir, 'assets'), { recursive: true });
  const text = 'export {};\n';
  await writeFile(join(dir, 'assets/entry-TEST.js'), text);
  const manifest = {
    entry: ['/assets/entry-TEST.js'],
    css: [],
    routes: {},
    routeCss: {},
    styles: {},
    assets: {},
    files: { '/assets/entry-TEST.js': { size: text.length, etag: '"test"', br: null, gz: null } },
    public: {},
  };
  for (const [name, body] of Object.entries(ROOT_FILES)) {
    await writeFile(join(dir, name), body);
    manifest.public[`/${name}`] = { size: body.length, etag: `"${name}"`, br: null, gz: null };
  }
  await writeFile(join(dir, 'manifest.json'), JSON.stringify(manifest));
  await writeFile(join(dir, 'csp.json'), JSON.stringify({ scriptHashes: [`sha256-${createHash('sha256').update(THEME).digest('base64')}`], themeScript: THEME }));
  return dir;
}

const pokerKey = (demo, code) => demo.sessionByCode(code).key;
const boardKey = (demo, code) => demo.boardByCode(code).key;

let dist;
let fake;
let demo;
let clients;
let errors;

before(async () => {
  dist = await makeDist();
});

after(async () => {
  await rm(dist, { recursive: true, force: true });
});

beforeEach(async () => {
  fake = await startFakeServer({ dist, aliases: ['localhost'] });
  errors = [];
  fake.on('error', (error) => errors.push(error));
  demo = installDemo(fake, { peers: false });
  demo.log = () => {};
  clients = [];
});

afterEach(async () => {
  for (const client of clients) {
    client.ws.terminate();
  }
  demo.stop();
  await fake.close();
  assert.deepEqual(errors, [], 'errors thrown by demo handlers');
  assert.deepEqual(fake.violations.map((v) => v.errors), [], 'client frames refused');
});

async function connect(as, subs = []) {
  const headers = as ? { cookie: `rs_demo_as=${as}` } : {};
  const ws = await connectWebSocket(fake.wsUrl, { origin: fake.origin, headers });
  const client = new TestClient(ws);
  clients.push(client);
  client.send({ t: 'hello', v: 1, cid: randomUUID(), subs: subs.map((r) => ({ r })) });
  const welcome = await client.next();
  assert.equal(welcome.t, 'welcome');
  client.me = welcome.me;
  return client;
}

/** The next frame matching `predicate`, skipping others (presence, notices). */
async function frame(client, predicate, timeout = 2000) {
  const deadline = Date.now() + timeout;
  for (;;) {
    const next = await client.next(Math.max(1, deadline - Date.now()));
    if (predicate(next)) {
      return next;
    }
  }
}

/** A request with a Host header of our choosing, which fetch does not allow. */
function rawRequest(method, path, headers, body = '') {
  const target = new URL(fake.url);
  return new Promise((done, fail) => {
    const req = request({ host: target.hostname, port: target.port, method, path, headers }, (res) => {
      res.resume();
      res.on('end', () => done(res));
    });
    req.on('error', fail);
    req.end(body);
  });
}

const events = (f, k) => (f.t === 'tx' ? f.ev.filter((e) => e.k === k) : []);
const http = (path, init = {}) => fetch(new URL(path, fake.url), {
  ...init,
  headers: { 'Content-Type': 'application/json', 'X-RS': '1', Origin: fake.origin, ...init.headers },
});

describe('fake server hooks', () => {
  test('an alias host is its own origin for CSRF, the socket and the CSP', async () => {
    const port = new URL(fake.url).port;
    const alias = `http://localhost:${port}`;
    const shell = await rawRequest('GET', '/account', { Host: `localhost:${port}` });
    assert.match(shell.headers['content-security-policy'], new RegExp(`ws://localhost:${port}`));
    const body = JSON.stringify({ email: DEMO.email });
    const refused = await rawRequest('POST', '/api/auth/magic/request', { 'X-RS': '1', Origin: alias }, body);
    assert.equal(refused.statusCode, 403);
    const accepted = await rawRequest('POST', '/api/auth/magic/request', { 'X-RS': '1', Origin: alias, Host: `localhost:${port}`, 'Content-Type': 'application/json' }, body);
    assert.equal(accepted.statusCode, 202);

    const ws = await connectWebSocket(fake.wsUrl, { origin: alias, headers: { Host: `localhost:${port}` } });
    const client = new TestClient(ws);
    clients.push(client);
    client.send({ t: 'hello', v: 1, cid: randomUUID(), subs: [] });
    assert.equal((await client.next()).t, 'welcome');
  });

  test('requests keep their raw bytes and subscriptions are announced once', async () => {
    const png = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0xff, 0x00, 0x80]);
    const board = demo.boardByCode(CODES.thinking);
    const upload = await http(`/api/retro/${board.id}/images`, { method: 'POST', headers: { 'Content-Type': 'image/png' }, body: png });
    const { blobId } = await upload.json();
    assert.deepEqual(Buffer.from(await (await fetch(`${fake.url}/blob/${blobId}`)).arrayBuffer()), png);

    const seen = [];
    fake.on('subscribe', (conn, room) => seen.push(['sub', room.key]));
    fake.on('unsubscribe', (conn, room) => seen.push(['unsub', room.key]));
    const client = await connect('demo', [board.key]);
    await frame(client, (f) => f.t === 'snap');
    client.send({ t: 'sub', r: board.key });
    await frame(client, (f) => f.t === 'snap' || f.t === 'replay');
    client.send({ t: 'unsub', r: board.key });
    await client.expectSilence(50);
    assert.deepEqual(seen, [['sub', board.key], ['unsub', board.key]]);
  });
});

describe('identity', () => {
  test('visitors are Demo User until ?as= picks another member, which sticks in a cookie', async () => {
    const boot = (html) => JSON.parse(/id="boot">(.*?)<\/script>/.exec(html)[1]);
    const plain = await fetch(`${fake.url}/account`);
    assert.equal(boot(await plain.text()).me.principal.name, 'Demo User');

    const as = await fetch(`${fake.url}/account?as=priya`);
    assert.equal(boot(await as.text()).me.principal.name, 'Priya Raman');
    assert.match(as.headers.get('set-cookie'), /rs_demo_as=priya/);

    const priya = await connect('priya');
    assert.equal(priya.me.pid, PRIYA.pid);
    const out = await fetch(`${fake.url}/login`, { headers: { cookie: 'rs_demo_as=signed-out' } });
    assert.equal(boot(await out.text()).me, null);
  });

  test("'/' sends a signed-in visitor to their team before the page loads, ?as= included, and welcomes the signed out", async () => {
    const home = (path) => fetch(`${fake.url}${path}`, { redirect: 'manual' });
    const demoUser = await home('/');
    assert.equal(demoUser.status, 302);
    assert.equal(demoUser.headers.get('location'), `/teams/${DEMO_TEAM}`);
    assert.equal(demoUser.headers.get('cache-control'), 'no-store');

    const lena = await home('/?as=lena');
    assert.equal(lena.headers.get('location'), `/teams/${GUILD_TEAM}`, 'Lena has visited no team yet: her first one');
    assert.match(lena.headers.get('set-cookie'), /rs_demo_as=lena/, 'the redirect keeps who ?as= picked');

    assert.equal((await http(`/api/teams/${GUILD_TEAM}`)).status, 200);
    assert.equal((await home('/')).headers.get('location'), `/teams/${GUILD_TEAM}`, 'the team Demo User visited last');

    const out = await home('/?as=signed-out');
    assert.equal(out.status, 200);
    assert.match(out.headers.get('set-cookie'), /rs_demo_as=signed-out/);
  });

  test('a signed-out visitor has no socket: /ws admits signed-in users only', async () => {
    const ws = await connectWebSocket(fake.wsUrl, { origin: fake.origin, headers: { cookie: 'rs_demo_as=signed-out' } });
    const client = new TestClient(ws);
    clients.push(client);
    client.send({ t: 'hello', v: 1, cid: randomUUID(), subs: [] });
    assert.equal((await client.closed).code, 4401);
  });

  test('a sign-in link to a new address creates a verified account with no team yet', async () => {
    const lines = [];
    demo.log = (line) => lines.push(line);
    assert.equal((await http('/api/auth/magic/request', { method: 'POST', body: JSON.stringify({ email: 'New.Person@example.test' }) })).status, 202);
    const token = /#t=(.+)$/.exec(lines.at(-1))[1];
    assert.deepEqual(await (await http('/api/auth/magic/preview', { method: 'POST', body: JSON.stringify({ token }) })).json(), { email: 'n•••@example.test' });
    const signedIn = await http('/api/auth/magic/consume', { method: 'POST', body: JSON.stringify({ token }) });
    const me = await signedIn.json();
    assert.equal(me.user.email, 'new.person@example.test');
    assert.equal(me.user.emailVerified, true);
    assert.deepEqual(me.teams, []);
    assert.equal(me.lastTeamId, null);
    assert.equal((await http('/api/auth/magic/consume', { method: 'POST', body: JSON.stringify({ token }) })).status, 400);
  });

  test('an unverified user can ask for the verification email again, and the answer never says whether it went', async () => {
    const lines = [];
    demo.log = (line) => lines.push(line);
    const asTom = { headers: { cookie: 'rs_demo_as=tom' } };
    assert.equal((await http('/api/auth/verify/request', { method: 'POST', body: '{}', ...asTom })).status, 202);
    assert.match(lines.at(-1), new RegExp(`to ${TOM.email}: Verify your email: .*/auth/verify#t=`));
    assert.equal((await http('/api/auth/verify/request', { method: 'POST', body: '{}' })).status, 202);
    assert.equal(lines.length, 1, 'a verified user gets no mail');
    assert.equal((await http('/api/auth/verify/request', { method: 'POST', body: '{}', headers: { cookie: 'rs_demo_as=signed-out' } })).status, 401);
  });

  test('signing in by email and out again moves the cookie', async () => {
    const login = await http('/api/auth/login', { method: 'POST', body: JSON.stringify({ email: MARCUS.email, password: 'anything' }) });
    assert.equal((await login.json()).principal.name, 'Marcus Chen');
    assert.match(login.headers.get('set-cookie'), /rs_demo_as=marcus/);
    assert.equal((await http('/api/auth/login', { method: 'POST', body: JSON.stringify({ email: 'nobody@example.test', password: 'x' }) })).status, 401);
    const logout = await http('/api/auth/logout', { method: 'POST' });
    assert.match(logout.headers.get('set-cookie'), /rs_demo_as=signed-out/);
  });
});

describe('poker', () => {
  test('every room of the demo sends a snapshot the protocol accepts', async () => {
    const client = await connect('demo', [...demo.rooms.keys()]);
    const snaps = new Set();
    while (snaps.size < demo.rooms.size) {
      const next = await frame(client, (f) => f.t === 'snap');
      snaps.add(next.r);
    }
    assert.equal(snaps.size, demo.rooms.size);
  });

  test('a vote travels with its value to the voter and as a status to everyone else', async () => {
    const key = pokerKey(demo, CODES.teamPoker);
    const session = demo.sessionByCode(CODES.teamPoker);
    const roundId = session.currentRoundId;
    const me = await connect('demo', [key]);
    const priya = await connect('priya', [key]);
    const snap = await frame(priya, (f) => f.t === 'snap');
    assert.deepEqual(snap.d.votes.find((v) => v.roundId === roundId && v.pid === MARCUS.pid), { roundId, pid: MARCUS.pid, status: 'locked' });
    await frame(me, (f) => f.t === 'snap');

    me.send({ t: 'cmd', r: key, q: 1, k: 'poker.vote.set', d: { roundId, points: 5 } });
    const own = await frame(me, (f) => events(f, 'poker.vote.state').length > 0);
    assert.deepEqual(events(own, 'poker.vote.state')[0].d, { roundId, pid: DEMO.pid, points: 5, locked: false, abstained: false });
    assert.equal((await frame(me, (f) => f.t === 'ack')).q, 1);
    const seen = await frame(priya, (f) => events(f, 'poker.vote.state').length > 0);
    assert.deepEqual(events(seen, 'poker.vote.state')[0].d, { roundId, pid: DEMO.pid, status: 'selected' });

    me.send({ t: 'cmd', r: key, q: 2, k: 'poker.lock.set', d: { roundId, locked: true } });
    const locked = await frame(priya, (f) => events(f, 'poker.vote.state').length > 0);
    assert.deepEqual(events(locked, 'poker.vote.state')[0].d, { roundId, pid: DEMO.pid, status: 'locked' });
  });

  test('when everyone present has locked in, the round reveals itself with the winner', async () => {
    const created = await (await http(`/api/teams/${DEMO_TEAM}/poker-sessions`, { method: 'POST', body: '{}' })).json();
    const session = demo.sessions.get(created.id);
    const roundId = session.currentRoundId;
    const me = await connect('demo', [session.key]);
    const priya = await connect('priya', [session.key]);
    await frame(me, (f) => f.t === 'snap');
    await frame(priya, (f) => f.t === 'snap');
    me.send({ t: 'cmd', r: session.key, q: 1, k: 'poker.vote.set', d: { roundId, points: 5, lock: true } });
    priya.send({ t: 'cmd', r: session.key, q: 1, k: 'poker.vote.set', d: { roundId, points: 5, high: 8, lock: true } });
    const revealed = events(await frame(me, (f) => events(f, 'poker.round.revealed').length > 0), 'poker.round.revealed')[0].d;
    assert.equal(revealed.winner, 5);
    assert.deepEqual(revealed.votes.map((v) => v.pid).sort(), [DEMO.pid, PRIYA.pid].sort());
    assert.equal(revealed.by, undefined);
  });

  test('committing a previewed round brings the holder\'s spotlight with it', async () => {
    const key = pokerKey(demo, CODES.teamPoker);
    const session = demo.sessionByCode(CODES.teamPoker);
    const pending = session.rounds.find((r) => r.pending);
    const priya = await connect('priya', [key]);
    await frame(priya, (f) => f.t === 'snap');
    priya.send({ t: 'cmd', r: key, q: 1, k: 'poker.round.commitPending', d: { roundId: pending.id } });
    const moved = await frame(priya, (f) => events(f, 'poker.spotlight').length > 0);
    assert.equal(events(moved, 'poker.spotlight')[0].d.roundId, pending.id);
    assert.equal(session.spotlight.roundId, session.currentRoundId);
  });

  test('the winner is the plurality, a between vote counts for both ends, ties go up', () => {
    assert.equal(winnerOf([{ points: 3 }, { points: 5 }, { points: 5 }]), 5);
    assert.equal(winnerOf([{ points: 3 }, { points: 3, high: 5 }, { points: 5 }]), 5);
    assert.equal(winnerOf([{ abstained: true }]), 0);
  });

  test('simulated teammates vote only while someone real is watching', async () => {
    const session = demo.sessionByCode(CODES.teamPoker);
    const round = session.current;
    const votesBefore = round.votes.size;
    for (let i = 0; i < 40; i++) {
      demo.tick();
    }
    assert.equal(round.votes.size, votesBefore);

    const me = await connect('demo', [session.key]);
    await frame(me, (f) => f.t === 'snap');
    for (let i = 0; i < 60; i++) {
      demo.tick();
    }
    assert.ok(round.votes.size > votesBefore, 'the peers voted');
    assert.ok([...round.votes.values()].filter((v) => v.locked).length > 1, 'some locked in');
    assert.ok(!round.votes.has(DEMO.pid), 'nobody votes for the visitor');
  });

  test('a peer played by a real window is left to that window', async () => {
    const session = demo.sessionByCode(CODES.teamPoker);
    const priya = await connect('priya', [session.key]);
    await frame(priya, (f) => f.t === 'snap');
    for (let i = 0; i < 80; i++) {
      demo.tick();
    }
    assert.ok(!session.current.votes.has(PRIYA.pid));
  });
});

describe('retro', () => {
  test('a new card is mine to its author only, and an anonymous one names nobody', async () => {
    const board = demo.boardByCode(CODES.thinking);
    const columnId = board.columns[0].id;
    const me = await connect('demo', [board.key]);
    const priya = await connect('priya', [board.key]);
    await frame(me, (f) => f.t === 'snap');
    await frame(priya, (f) => f.t === 'snap');
    const cardId = randomUUID();
    me.send({ t: 'cmd', r: board.key, q: 1, k: 'retro.card.add', d: { cardId, columnId, body: 'Shorter standups', anonymous: true } });
    const mine = events(await frame(me, (f) => events(f, 'retro.card.added').length > 0), 'retro.card.added')[0].d.card;
    const theirs = events(await frame(priya, (f) => events(f, 'retro.card.added').length > 0), 'retro.card.added')[0].d.card;
    assert.equal(mine.mine, true);
    assert.equal(theirs.mine, false);
    assert.equal(theirs.author, undefined);
  });

  test('votes wait for the voting stage, and voters stay hidden until discussing', async () => {
    const thinking = demo.boardByCode(CODES.thinking);
    const voting = demo.boardByCode(CODES.voting);
    const me = await connect('demo', [thinking.key, voting.key]);
    await frame(me, (f) => f.t === 'snap' && f.r === thinking.key);
    const snap = await frame(me, (f) => f.t === 'snap' && f.r === voting.key);
    assert.ok(snap.d.votes.length > 0 && snap.d.votes.every((v) => v.voters === undefined));

    me.send({ t: 'cmd', r: thinking.key, q: 1, k: 'retro.vote.set', d: { cardId: thinking.orderedCards[0].id, on: true } });
    assert.equal((await frame(me, (f) => f.t === 'nack')).code, 'stage_forbids');

    const card = voting.orderedCards.find((c) => !c.voters.has(DEMO.pid) && c.author !== DEMO.pid);
    me.send({ t: 'cmd', r: voting.key, q: 2, k: 'retro.vote.set', d: { cardId: card.id, on: true } });
    const vote = events(await frame(me, (f) => events(f, 'retro.vote').length > 0), 'retro.vote')[0].d;
    assert.deepEqual(vote, { cardId: card.id, count: card.voters.size, mine: true });

    me.send({ t: 'cmd', r: voting.key, q: 3, k: 'retro.stage.set', d: { from: 'voting', to: 'discussing' } });
    const moved = await frame(me, (f) => events(f, 'retro.stage').length > 0);
    assert.ok(events(moved, 'retro.vote').every((e) => Array.isArray(e.d.voters)));
  });

  test('the thinking board has a running timer and the recipient of kudos gets the celebration', async () => {
    const thinking = demo.boardByCode(CODES.thinking);
    const discussing = demo.boardByCode(CODES.discussing);
    const me = await connect('demo', [thinking.key, discussing.key]);
    const snap = await frame(me, (f) => f.t === 'snap' && f.r === thinking.key);
    assert.equal(snap.d.timer.state, 'running');
    assert.ok(snap.d.timer.endsAt > Date.now());
    await frame(me, (f) => f.t === 'snap' && f.r === discussing.key);

    assert.equal((await http('/__demo/celebrate', { method: 'POST' })).status, 204);
    const added = events(await frame(me, (f) => events(f, 'retro.endorsement.added').length > 0), 'retro.endorsement.added')[0].d;
    assert.equal(added.celebrate, true);
    assert.equal(added.endorsement.to, DEMO.pid);
  });

  test('an action-item card on a team board becomes a team action item', async () => {
    const board = demo.boardByCode(CODES.discussing);
    const team = demo.teams.get(DEMO_TEAM);
    const me = await connect('demo', [board.key, team.key]);
    await frame(me, (f) => f.t === 'snap' && f.r === board.key);
    await frame(me, (f) => f.t === 'snap' && f.r === team.key);
    const actions = board.columns.find((c) => c.actionItems);
    me.send({ t: 'cmd', r: board.key, q: 1, k: 'retro.card.add', d: { cardId: randomUUID(), columnId: actions.id, body: 'Pair on the flaky test', anonymous: false } });
    const added = events(await frame(me, (f) => events(f, 'team.action.added').length > 0), 'team.action.added')[0].d.item;
    assert.equal(added.text, 'Pair on the flaky test');
    assert.equal(added.sourceBoardId, board.id);
  });

  test('simulated teammates add cards to the thinking board while someone watches', async () => {
    const board = demo.boardByCode(CODES.thinking);
    const before = board.cards.size;
    const me = await connect('demo', [board.key]);
    await frame(me, (f) => f.t === 'snap');
    for (let i = 0; i < 120; i++) {
      demo.tick();
    }
    assert.ok(board.cards.size > before);
  });
});

describe('HTTP', () => {
  const get = async (path) => {
    const response = await fetch(new URL(path, fake.url));
    assert.equal(response.status, 200, path);
    return response.json();
  };

  test('team pages get the shapes of docs/decisions/U30.md and UX18.md', async () => {
    const team = await get(`/api/teams/${DEMO_TEAM}`);
    assert.equal(team.role, 'owner');
    assert.equal(team.jira.projectKey, 'DEMO');
    assert.deepEqual(team.counts, { boards: 4, members: 6 });

    const boards = await get(`/api/teams/${DEMO_TEAM}/boards?archived=false`);
    assert.deepEqual(boards.items.map((b) => b.code), [CODES.thinking, CODES.voting, CODES.discussing, CODES.closed]);
    assert.equal(boards.nextCursor, null);
    const thinking = demo.boardByCode(CODES.thinking);
    assert.deepEqual(boards.items[0], {
      id: thinking.id,
      code: CODES.thinking,
      title: 'Sprint 42 retro',
      archived: false,
      createdAt: thinking.createdAt,
      stage: 'thinking',
      stagesEnabled: true,
      cardCount: thinking.cards.size,
      lastActivityAt: Math.max(...[...thinking.cards.values(), ...thinking.comments.values()].map((c) => c.createdAt)),
    });
    assert.equal((await get(`/api/teams/${DEMO_TEAM}/boards?archived=true`)).items[0].code, CODES.archived);

    const sessions = (await get(`/api/teams/${DEMO_TEAM}/poker-sessions`)).items;
    const teamPoker = demo.sessionByCode(CODES.teamPoker);
    assert.deepEqual(sessions.find((s) => s.code === CODES.teamPoker), {
      id: teamPoker.id, code: CODES.teamPoker, createdAt: teamPoker.createdAt,
      pointed: 3, total: 4, parentKeys: [{ key: 'DEMO-100', count: 4 }],
      lastActivityAt: Math.max(...teamPoker.rounds.map((r) => r.revealedAt ?? 0)),
    });

    const open = (await get(`/api/teams/${DEMO_TEAM}/action-items?status=open&sort=newest`)).items;
    assert.ok(open.every((i) => !i.doneAt && i.boardTitle));
    const leaderboard = (await get(`/api/teams/${DEMO_TEAM}/endorsements/leaderboard?period=all`)).entries;
    assert.ok(leaderboard[0].total >= leaderboard.at(-1).total && leaderboard[0].name);
  });

  test('me lists the teams by name with their open action items, and the team visited last', async () => {
    const me = await get('/api/me');
    assert.deepEqual(me.teams.map((t) => [t.name, t.role]), [['Demo Team', 'owner'], ['Platform Guild', 'member'], ['Product All-Hands', 'member']]);
    assert.deepEqual(me.teams.map((t) => t.openActions), [4, 0, 0]);
    assert.equal(me.lastTeamId, DEMO_TEAM);
    assert.ok(me.unreadMentions > 0);
    const mentions = await get('/api/me/mentions');
    assert.ok(mentions.items.some((m) => m.boardCode === CODES.thinking && m.author?.name === 'Marcus Chen'));
    assert.ok((await get('/api/me/endorsements')).some((e) => e.typeName === 'Shipped It'));

    // Opening a team page or any room of a team makes it the last team.
    await get(`/api/teams/${GUILD_TEAM}`);
    assert.equal((await get('/api/me')).lastTeamId, GUILD_TEAM);
    const client = await connect('demo', [demo.sessionByCode(CODES.crowdPoker).key]);
    await frame(client, (f) => f.t === 'snap');
    assert.equal((await get('/api/me')).lastTeamId, ALL_HANDS_TEAM);

    const lena = await (await http('/api/me', { headers: { cookie: 'rs_demo_as=lena' } })).json();
    assert.equal(lena.user.email, LENA.email);
    assert.equal(lena.lastTeamId, null, 'nobody has seen Lena visit a team yet');
  });

  test('the removed routes are gone: no guests, quick rooms, board passwords, Dashboard, team list or favorites', async () => {
    const board = demo.boardByCode(CODES.thinking);
    const gone = [
      ['POST', '/api/guest'], ['POST', '/api/poker'], ['POST', '/api/retro'], ['GET', '/api/dashboard'], ['GET', '/api/teams'],
      ['PUT', `/api/teams/${DEMO_TEAM}/favorite`], ['POST', `/api/retro/${board.id}/unlock`], ['PATCH', `/api/retro/${board.id}`],
    ];
    for (const [method, path] of gone) {
      assert.equal((await http(path, { method, body: method === 'GET' ? undefined : '{}' })).status, 404, `${method} ${path}`);
    }
    const old = await fetch(`${fake.url}/dashboard`, { redirect: 'manual' });
    assert.equal(old.status, 302);
    assert.equal(old.headers.get('location'), '/');
    assert.equal(old.headers.get('cache-control'), 'no-store');
  });

  test('a session or board opens by its code for the team\'s members, and is as missing as a wrong code to anyone else', async () => {
    const created = await (await http(`/api/teams/${DEMO_TEAM}/boards`, { method: 'POST', body: JSON.stringify({ title: 'Kick-off', visibility: 'link', password: 'hunter2' }) })).json();
    assert.deepEqual(await get(`/api/retro/by-code/${created.code}`), { id: created.id, access: 'granted' });
    const board = demo.boards.get(created.id);
    assert.deepEqual(board.meta, { title: 'Kick-off', archived: false });
    const poker = demo.sessionByCode(CODES.teamPoker);
    assert.deepEqual(await get(`/api/poker/by-code/${poker.code.toLowerCase()}`), { id: poker.id, access: 'granted' });

    const asLena = { headers: { cookie: 'rs_demo_as=lena' } };
    for (const path of [`/api/retro/by-code/${created.code}`, `/api/poker/by-code/${poker.code}`, '/api/poker/by-code/NOPE000000', `/api/teams/${DEMO_TEAM}`]) {
      assert.equal((await fetch(new URL(path, fake.url), asLena)).status, 404, path);
    }
    assert.equal((await fetch(new URL(`/api/retro/by-code/${created.code}`, fake.url), { headers: { cookie: 'rs_demo_as=signed-out' } })).status, 401);
  });

  test('a member who is not an admin cannot archive or delete a board, even one they made', async () => {
    const asMarcus = { headers: { cookie: 'rs_demo_as=marcus' } };
    const created = await (await http(`/api/teams/${DEMO_TEAM}/boards`, { method: 'POST', body: JSON.stringify({ title: 'Mine' }), ...asMarcus })).json();
    assert.equal((await http(`/api/retro/${created.id}/archive`, { method: 'POST', ...asMarcus })).status, 403);
    assert.equal((await http(`/api/retro/${created.id}`, { method: 'DELETE', body: '{}', ...asMarcus })).status, 403);
    assert.equal((await http(`/api/retro/${created.id}/archive`, { method: 'POST' })).status, 204);
  });

  test('a non-member\'s subscribe to a team room is kicked forbidden, with no snapshot and no presence', async () => {
    const session = demo.sessionByCode(CODES.teamPoker);
    const board = demo.boardByCode(CODES.thinking);
    const team = demo.teams.get(DEMO_TEAM);
    const lena = await connect('lena', [session.key, board.key]);
    assert.deepEqual(await lena.next(), { t: 'kick', r: session.key, code: 'forbidden' });
    assert.deepEqual(await lena.next(), { t: 'kick', r: board.key, code: 'forbidden' });
    lena.send({ t: 'sub', r: team.key });
    assert.deepEqual(await lena.next(), { t: 'kick', r: team.key, code: 'forbidden' });
    await lena.expectSilence();
    assert.ok(!session.presence.has(LENA.pid) && !board.presence.has(LENA.pid), 'Lena never joined');
    assert.ok([session, board, team].every((room) => [...room.room.subscribers].every((c) => c.me.pid !== LENA.pid)));
    lena.send({ t: 'cmd', r: session.key, q: 1, k: 'poker.vote.set', d: { roundId: session.currentRoundId, points: 3 } });
    assert.equal((await lena.next()).code, 'not_subscribed');
  });

  test('a member removed from the team while subscribed has their commands refused', async () => {
    const session = demo.sessionByCode(CODES.teamPoker);
    const board = demo.boardByCode(CODES.thinking);
    const marcus = await connect('marcus', [session.key, board.key]);
    await frame(marcus, (f) => f.t === 'snap' && f.r === session.key);
    await frame(marcus, (f) => f.t === 'snap' && f.r === board.key);
    // No kick yet: the commands race the removal, which the room must still refuse.
    const team = demo.teams.get(DEMO_TEAM);
    team.members = team.members.filter((m) => m.pid !== MARCUS.pid);
    marcus.send({ t: 'cmd', r: session.key, q: 1, k: 'poker.vote.set', d: { roundId: session.currentRoundId, points: 3 } });
    assert.equal((await frame(marcus, (f) => f.t === 'nack')).code, 'forbidden');
    marcus.send({ t: 'cmd', r: board.key, q: 1, k: 'retro.card.add', d: { cardId: randomUUID(), columnId: board.columns[0].id, body: 'Hi', anonymous: false } });
    assert.equal((await frame(marcus, (f) => f.t === 'nack')).code, 'forbidden');
  });

  test('kudos you received count only in the teams you are still on', async () => {
    const team = demo.teams.get(DEMO_TEAM);
    const before = await get('/api/me/endorsements');
    assert.ok(before.length > 0 && before.every((e) => e.teamName === 'Demo Team'));
    team.members = team.members.filter((m) => m.pid !== DEMO.pid);
    assert.deepEqual(await get('/api/me/endorsements'), []);
  });

  test('every room is a team room that seats the team\'s members', async () => {
    const crowd = demo.sessionByCode(CODES.crowdPoker);
    const client = await connect('demo', [crowd.key]);
    const snap = await frame(client, (f) => f.t === 'snap');
    assert.equal(snap.d.teamId, ALL_HANDS_TEAM);
    assert.equal(snap.d.createdAt, crowd.createdAt);
    assert.equal(snap.d.createdBy, undefined);
    assert.deepEqual(snap.d.roster.map((p) => p.pid).sort(), [DEMO.pid, ...CROWD.map((p) => p.pid)].sort());
    assert.ok(snap.d.roster.every((p) => p.kind === 'user'));
    for (const room of demo.rooms.values()) {
      assert.ok(room.key.startsWith('team:') || demo.teams.get(room.team?.id) === room.team, `${room.key} belongs to a team`);
    }
  });

  test('a seeded session is dated by its UUIDv7 id, as the poker toolbar reads it', () => {
    for (const session of demo.sessions.values()) {
      assert.equal(uuidv7Time(session.id), Math.floor(session.createdAt), session.code);
    }
  });

  test('the fake Jira serves the demo project with sprints, details, comments and links', async () => {
    const browse = await get(`/api/teams/${DEMO_TEAM}/jira/browse`);
    assert.deepEqual(browse.buckets.map((b) => b.name), ['Sprint 41 · Checkout polish', 'Sprint 42 · Onboarding', 'Backlog']);
    const issue = await get(`/api/teams/${DEMO_TEAM}/jira/issues/DEMO-123`);
    assert.equal(issue.fields.summary, 'Retry failed payments with exponential backoff');
    assert.equal(issue.islandIds.length, 17);
    assert.ok(isAllowedDoc(issue.description), 'the demo islands use the DocNode wire shape');
    assert.equal(issue.comments.length, 3);
    assert.equal(issue.links.length, 2);
    assert.equal(issue.attachments.length, 2);
  });

  test('the demo sprint report follows the demo clock: an active sprint around today and closed ones before it', async () => {
    const body = await get(`/api/teams/${DEMO_TEAM}/jira/sprint-report`);
    assert.deepEqual(body.sprints.map((s) => s.state), ['active', 'closed', 'closed', 'closed', 'closed']);
    assert.equal(body.hasMoreSprints, true);
    const [active, last] = body.sprints;
    const now = Date.now();
    assert.ok(Date.parse(active.startDate) < now && now < Date.parse(active.endDate), 'the active sprint spans today');
    assert.equal(last.completeDate, last.endDate);
    assert.equal(Date.parse(last.endDate), Date.parse(active.startDate));
    assert.equal(body.report.sprintId, last.id);
    assert.ok(body.report.groups.find((g) => g.outcome === 'notDone').issues.some((i) => i.next === active.name));
    const report = await get(`/api/teams/${DEMO_TEAM}/jira/sprint-report?sprint=${active.id}`);
    assert.equal(report.report.sprintId, active.id);
  });

  test('a role change reaches the team room as the new member list', async () => {
    const team = demo.teams.get(DEMO_TEAM);
    const me = await connect('demo', [team.key]);
    await frame(me, (f) => f.t === 'snap');
    const changed = await http(`/api/teams/${DEMO_TEAM}/members/${MARCUS.pid}`, { method: 'PATCH', body: JSON.stringify({ role: 'admin' }) });
    assert.equal(changed.status, 204);
    const members = events(await frame(me, (f) => events(f, 'team.members').length > 0), 'team.members')[0].d.members;
    assert.equal(members.find((m) => m.pid === MARCUS.pid).role, 'admin');
  });

  test('avatars and the burndown chart are served as images', async () => {
    const avatar = await fetch(`${fake.url}/blob/${PRIYA.avatar}`);
    assert.equal(avatar.headers.get('content-type'), 'image/svg+xml');
    assert.match(await avatar.text(), /^<svg/);
    assert.equal((await fetch(`${fake.url}/blob/${randomUUID()}`)).status, 404);
  });

  test('the root files are served with their types, a one-day cache and ETag revalidation', async () => {
    const types = { 'favicon.ico': 'image/x-icon', 'manifest.webmanifest': 'application/manifest+json', 'robots.txt': 'text/plain; charset=utf-8' };
    for (const [name, type] of Object.entries(types)) {
      const response = await fetch(`${fake.url}/${name}`);
      assert.equal(response.status, 200, name);
      assert.equal(response.headers.get('content-type'), type);
      assert.equal(response.headers.get('cache-control'), 'public, max-age=86400');
      assert.equal(response.headers.get('etag'), `"${name}"`);
      assert.equal(await response.text(), ROOT_FILES[name]);
      const again = await fetch(`${fake.url}/${name}`, { headers: { 'If-None-Match': `"${name}"` } });
      assert.equal(again.status, 304, name);
    }
  });
});
