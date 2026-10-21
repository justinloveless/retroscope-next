import { fileURLToPath } from 'node:url';
import { loadTranscript } from '../../../web-fake-server/transcripts.js';
import { expect, openSocket, test } from './fixtures.js';

const SAMPLE = fileURLToPath(new URL('../../../web-fake-server/fixtures/transcripts/poker-sample.json', import.meta.url));
const POKER = 'poker:5e551011-0000-4000-8000-000000000001';
const ROUND = '40000000-0000-4000-8000-000000000001';
const CID = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const SNAPSHOT = {
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
const vote = (q, points = 5) => ({ t: 'cmd', r: POKER, q, k: 'poker.vote.set', d: { roundId: ROUND, points } });

async function connect(page, subs = [{ r: POKER }]) {
  await page.goto('/poker/FAKE123456');
  const socket = await openSocket(page);
  await socket.send({ t: 'hello', v: 1, cid: CID, subs });
  return socket;
}

test('a golden transcript reaches the browser frame for frame', async ({ page, fake }) => {
  const transcript = loadTranscript(SAMPLE);
  fake.replayTranscript(SAMPLE);
  const socket = await connect(page);
  const expected = transcript.frames.filter((f) => !['welcome', 'hello', 'cmd'].includes(f.t));
  await socket.waitForFrames(1 + expected.length);
  const [welcome, ...frames] = await socket.frames();
  expect(welcome.me).toEqual(transcript.frames[0].me);
  expect(frames).toEqual(expected);
});

test('scripted ack, nack and a gap the client must notice', async ({ page, fake }) => {
  const room = fake.room(POKER, { snapshot: SNAPSHOT });
  fake.onCommand('poker.vote.set', (ctx) => (ctx.frame.d.points === 13
    ? ctx.nack('vote_locked', { msg: 'locked' })
    : ctx.commit([{ k: 'poker.vote.state', d: { roundId: ROUND, pid: ctx.conn.me.pid, points: ctx.frame.d.points } }])));
  const socket = await connect(page);
  await socket.waitForFrames(2);
  await socket.send(vote(1));
  await socket.send(vote(2, 13));
  await socket.waitForFrames(5);
  fake.gap(POKER);
  fake.tx(POKER, [{ k: 'presence.leave', d: { pid: '22222222-2222-4222-8222-222222222222' } }]);
  await socket.waitForFrames(6);
  const frames = await socket.frames();
  expect(frames.map((f) => f.t)).toEqual(['welcome', 'snap', 'tx', 'ack', 'nack', 'tx']);
  expect(frames[3]).toEqual({ t: 'ack', r: POKER, q: 1, s: 1 });
  expect(frames[4]).toEqual({ t: 'nack', r: POKER, q: 2, code: 'vote_locked', retry: false, msg: 'locked' });
  expect(frames[5].s).toBe(3);

  await socket.send({ t: 'sub', r: POKER, e: room.epoch, s: 1 });
  await socket.waitForFrames(7);
  const replay = (await socket.frames())[6];
  expect(replay.txs.map((t) => t.s)).toEqual([2, 3]);
});

test('bye carries retryMs and closes the socket', async ({ page, fake, browserName }) => {
  fake.room(POKER, { snapshot: SNAPSHOT });
  const socket = await connect(page);
  await socket.waitForFrames(2);
  fake.bye({ retryMs: 1500 });
  await socket.waitForClose();
  expect((await socket.frames()).at(-1)).toEqual({ t: 'bye', code: 'restart', retryMs: 1500 });
  // The server closes with 1012 (Service Restart); WebKit reports that code as 1005, so clients must
  // act on the bye frame rather than on the close code.
  expect((await socket.close()).code).toBe(browserName === 'webkit' ? 1005 : 1012);
});

test('a cid already in use closes with 4409', async ({ page, fake }) => {
  fake.rejectNextHello(4409, 'cid_in_use');
  const socket = await connect(page, []);
  await socket.waitForClose();
  expect(await socket.close()).toEqual({ code: 4409, reason: 'cid_in_use' });
});

test.describe('a client that breaks the protocol', () => {
  test.use({ allowProtocolViolations: true });

  test('is refused and recorded', async ({ page, fake }) => {
    const socket = await connect(page, [{ r: 'poker:nope' }]);
    await socket.waitForClose();
    expect((await socket.close()).code).toBe(4400);
    expect(fake.violations).toHaveLength(1);
  });
});

test('pages served by the fake connect from the page origin', async ({ page, fake }) => {
  await connect(page, []);
  const conn = await fake.waitForConnection();
  expect(conn.req.headers.origin).toBe(fake.origin);
});
