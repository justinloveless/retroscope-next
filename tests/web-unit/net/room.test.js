import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createOutbox } from '../../../web/src/net/outbox.js';
import { RETRY_MAX_MS, createRoom, retryDelay } from '../../../web/src/net/room.js';
import { ROOM, fakeTimers, listModel, memoryStorage, syncSchedule, uuidSequence } from './_fakes.js';

const EPOCH = '00000000000000aa';

function setup({ storage = memoryStorage(), online = true } = {}) {
  const timers = fakeTimers();
  const outbox = createOutbox({ storage, channel: null, now: timers.now, setTimer: timers.setTimer, uuid: uuidSequence() });
  const net = {
    frames: [],
    isOnline: online,
    connId: 1,
    send(frame) {
      if (!this.isOnline) {
        return false;
      }
      this.frames.push(frame);
      return true;
    },
    online() {
      return this.isOnline;
    },
    conn() {
      return this.connId;
    },
    setTimer: timers.setTimer,
    reused: 0,
    cidReuse() {
      this.reused++;
    },
    drainedRooms: [],
    drained(room) {
      this.drainedRooms.push(room);
    },
    cmds() {
      return this.frames.filter((f) => f.t === 'cmd');
    },
  };
  const nacks = [];
  const room = createRoom(ROOM, listModel, { outbox, net, onNack: (n) => nacks.push(n), schedule: syncSchedule });
  return { room, outbox, net, nacks, timers };
}

function snap(room, { s = 10, lastQ = 0, items = ['a'], e = EPOCH } = {}) {
  room.handle({ t: 'snap', r: ROOM, e, s, lastQ, d: { items }, o: { mine: [] } });
}

const add = (body) => ({ cardId: `id-${body}`, columnId: 'col', body, anonymous: false });

test('a snap sets confirmed state, epoch and seq', () => {
  const { room } = setup();
  snap(room);
  assert.deepEqual(room.view.items, ['a']);
  assert.deepEqual(room.view.overlay, { mine: [] });
  assert.equal(room.epoch, EPOCH);
  assert.equal(room.seq, 10);
  assert.equal(room.synced, true);
  assert.deepEqual(room.resume(), { r: ROOM, e: EPOCH, s: 10 });
});

test('commands wait for the room to sync, then go out in q order', () => {
  const { room, net } = setup();
  room.send('retro.card.add', add('x'));
  room.send('retro.card.add', add('y'));
  assert.deepEqual(net.cmds(), []);
  snap(room);
  assert.deepEqual(net.cmds().map((f) => [f.q, f.d.body]), [[1, 'x'], [2, 'y']]);
  assert.deepEqual(net.cmds()[0], { t: 'cmd', r: ROOM, q: 1, k: 'retro.card.add', d: add('x') });
});

test('the view applies pending intents on top of confirmed state', () => {
  const { room } = setup();
  snap(room);
  room.send('retro.card.add', add('x'), { apply: (s) => ({ ...s, items: [...s.items, 'x!'] }) });
  assert.deepEqual(room.confirmed.items, ['a']);
  assert.deepEqual(room.view.items, ['a', 'x!']);
});

test('tx then ack: the effect moves from pending to confirmed without a flicker', () => {
  const { room } = setup();
  snap(room);
  const seen = [];
  room.select((s) => s.items, Object.is, (items) => seen.push(items));
  room.send('retro.card.add', add('x'));
  room.handle({ t: 'tx', r: ROOM, s: 11, ev: [{ k: 'test.added', d: { item: 'x' } }] });
  assert.deepEqual(room.view.items, ['a', 'x', 'x'], 'confirmed has it and the intent is still on top');
  room.handle({ t: 'ack', r: ROOM, q: 1, s: 11 });
  assert.deepEqual(room.view.items, ['a', 'x']);
  assert.equal(room.pending().length, 0);
  assert.ok(seen.every((items) => items.includes('x')), 'x never disappears');
});

test('a dup ack clears the entry like any ack', () => {
  const { room } = setup();
  snap(room);
  room.send('retro.card.add', add('x'));
  room.handle({ t: 'ack', r: ROOM, q: 1, s: 10, dup: true });
  assert.equal(room.pending().length, 0);
});

test('a snapshot replaces confirmed state and rebases pending entries on it', () => {
  const { room } = setup();
  snap(room);
  room.send('retro.card.add', add('x'));
  snap(room, { s: 3, e: '00000000000000bb', items: ['fresh'] });
  assert.deepEqual(room.confirmed.items, ['fresh']);
  assert.deepEqual(room.view.items, ['fresh', 'x']);
});

test('tx frames apply only at seq + 1; older ones are ignored', () => {
  const { room, net } = setup();
  snap(room);
  room.handle({ t: 'tx', r: ROOM, s: 10, ev: [{ k: 'test.added', d: { item: 'old' } }] });
  room.handle({ t: 'tx', r: ROOM, s: 11, ev: [{ k: 'test.added', d: { item: 'b' } }, { k: 'test.added', d: { item: 'c' } }] });
  assert.deepEqual(room.confirmed.items, ['a', 'b', 'c']);
  assert.equal(room.seq, 11);
  assert.deepEqual(net.frames.filter((f) => f.t === 'sub'), []);
});

test('an empty tx still advances seq', () => {
  const { room } = setup();
  snap(room);
  room.handle({ t: 'tx', r: ROOM, s: 11, ev: [] });
  assert.equal(room.seq, 11);
});

test('a gap drops the frame and resubscribes from the last seq', () => {
  const { room, net } = setup();
  snap(room);
  room.handle({ t: 'tx', r: ROOM, s: 13, ev: [{ k: 'test.added', d: { item: 'late' } }] });
  assert.deepEqual(room.confirmed.items, ['a']);
  assert.deepEqual(net.frames.at(-1), { t: 'sub', r: ROOM, e: EPOCH, s: 10 });
  assert.equal(room.synced, false);
  room.handle({ t: 'tx', r: ROOM, s: 14, ev: [{ k: 'test.added', d: { item: 'later' } }] });
  assert.equal(net.frames.filter((f) => f.t === 'sub').length, 1, 'one resubscribe per gap');
  room.handle({
    t: 'replay', r: ROOM, e: EPOCH, from: 11, lastQ: 0, txs: [
      { s: 11, ev: [{ k: 'test.added', d: { item: 'b' } }] },
      { s: 12, ev: [] },
      { s: 13, ev: [{ k: 'test.added', d: { item: 'late' } }] },
      { s: 14, ev: [{ k: 'test.added', d: { item: 'later' } }] },
    ],
  });
  assert.deepEqual(room.confirmed.items, ['a', 'b', 'late', 'later']);
  assert.equal(room.seq, 14);
  assert.equal(room.synced, true);
});

test('an ack that arrives during a resync waits for the replay', () => {
  const { room } = setup();
  snap(room);
  room.send('retro.card.add', add('x'));
  room.handle({ t: 'tx', r: ROOM, s: 12, ev: [] });
  room.handle({ t: 'ack', r: ROOM, q: 1, s: 12 });
  assert.deepEqual(room.view.items, ['a', 'x'], 'the intent stays until its tx is applied');
  room.handle({ t: 'replay', r: ROOM, e: EPOCH, from: 11, lastQ: 1, txs: [{ s: 11, ev: [] }, { s: 12, ev: [{ k: 'test.added', d: { item: 'x' } }] }] });
  assert.deepEqual(room.view.items, ['a', 'x']);
  assert.equal(room.pending().length, 0);
});

test('a replay from another epoch or with a hole resubscribes', () => {
  const { room, net } = setup();
  snap(room);
  room.handle({ t: 'replay', r: ROOM, e: '00000000000000cc', from: 11, lastQ: 0, txs: [] });
  assert.equal(net.frames.filter((f) => f.t === 'sub').length, 1);
  snap(room);
  room.handle({ t: 'replay', r: ROOM, e: EPOCH, from: 12, lastQ: 0, txs: [{ s: 12, ev: [] }] });
  assert.equal(net.frames.filter((f) => f.t === 'sub').length, 2);
});

test('cseq = max(cseq, lastQ) before the room sends', () => {
  const { room, net, outbox } = setup();
  room.send('retro.card.add', add('x'));
  snap(room, { lastQ: 41 });
  assert.equal(outbox.cseq, 42);
  assert.deepEqual(net.cmds().map((f) => f.q), [42], 'an unsent q the server already saw would be acked dup and lost');
  room.send('retro.card.add', add('y'));
  assert.deepEqual(net.cmds().map((f) => f.q), [42, 43]);
});

test('entries sent on a lost connection resend with the same q after the next replay', () => {
  const { room, net } = setup();
  snap(room);
  room.send('retro.card.add', add('x'));
  assert.deepEqual(net.cmds().map((f) => f.q), [1]);
  room.disconnected();
  net.connId = 2;
  room.send('retro.card.add', add('y'));
  assert.equal(net.cmds().length, 1, 'nothing goes out before the room is back in sync');
  room.handle({ t: 'replay', r: ROOM, e: EPOCH, from: 11, lastQ: 1, txs: [] });
  assert.deepEqual(net.cmds().map((f) => f.q), [1, 1, 2]);
  room.handle({ t: 'ack', r: ROOM, q: 1, s: 10, dup: true });
  room.handle({ t: 'ack', r: ROOM, q: 2, s: 11 });
  assert.equal(room.pending().length, 0);
});

test('a final nack drops the entry, rolls back and reports', () => {
  const { room, nacks } = setup();
  snap(room);
  room.send('retro.card.add', add('x'));
  assert.deepEqual(room.view.items, ['a', 'x']);
  room.handle({ t: 'nack', r: ROOM, q: 1, code: 'board_archived', retry: false, msg: 'archived' });
  assert.deepEqual(room.view.items, ['a']);
  assert.equal(room.pending().length, 0);
  assert.deepEqual(nacks, [{ code: 'board_archived', msg: 'archived', d: undefined, k: 'retro.card.add', sent: add('x') }]);
});

test('a per-send handler can take a final nack (composer draft restore)', () => {
  const { room, nacks } = setup();
  snap(room);
  const handled = [];
  room.send('retro.card.add', add('x'), { onNack: (n) => handled.push(n.sent.body) === 1 });
  room.handle({ t: 'nack', r: ROOM, q: 1, code: 'stage_forbids', retry: false });
  assert.deepEqual(handled, ['x']);
  assert.deepEqual(nacks, []);
});

const replay = (room, { from = 11, lastQ = 0 } = {}) => room.handle({ t: 'replay', r: ROOM, e: EPOCH, from, lastQ, txs: [] });
const subs = (net) => net.frames.filter((f) => f.t === 'sub');

test('a retryable nack keeps the entry and its q, resubscribes after backoff and resends after the replay', () => {
  const { room, net, timers, nacks } = setup();
  snap(room);
  room.send('retro.card.add', add('x'));
  room.handle({ t: 'nack', r: ROOM, q: 1, code: 'server_busy', retry: true });
  assert.deepEqual(room.view.items, ['a', 'x'], 'the intent stays');
  assert.deepEqual(nacks, []);
  timers.advance(249);
  assert.deepEqual(subs(net), []);
  timers.advance(1);
  assert.deepEqual(subs(net), [{ t: 'sub', r: ROOM, e: EPOCH, s: 10 }], 'the sub lifts the server\'s refusal');
  assert.equal(net.cmds().length, 1, 'the resend waits for the replay');
  replay(room);
  assert.deepEqual(net.cmds().map((f) => f.q), [1, 1]);
  room.handle({ t: 'nack', r: ROOM, q: 1, code: 'rate_limited', retry: true });
  timers.advance(499);
  assert.equal(subs(net).length, 1);
  timers.advance(1);
  replay(room);
  assert.deepEqual(net.cmds().map((f) => f.q), [1, 1, 1]);
  room.handle({ t: 'ack', r: ROOM, q: 1, s: 11 });
  assert.equal(room.pending().length, 0);
});

test('commands pipelined behind a refused one are refused too, and all resend in q order', () => {
  const { room, net, timers } = setup();
  snap(room);
  room.send('retro.card.add', add('a'));
  room.send('retro.card.add', add('b'));
  room.handle({ t: 'nack', r: ROOM, q: 1, code: 'server_busy', retry: true });
  room.handle({ t: 'nack', r: ROOM, q: 2, code: 'server_busy', retry: true });
  timers.advance(500);
  replay(room);
  assert.deepEqual(net.cmds().map((f) => [f.q, f.d.body]), [[1, 'a'], [2, 'b'], [1, 'a'], [2, 'b']]);
});

test('commands sent during a retry backoff queue behind the retried one', () => {
  const { room, net, timers } = setup();
  snap(room);
  room.send('retro.card.add', add('a'));
  room.handle({ t: 'nack', r: ROOM, q: 1, code: 'rate_limited', retry: true });
  room.send('retro.card.add', add('b'));
  assert.equal(net.cmds().length, 1, 'b waits for the retry');
  timers.advance(250);
  assert.equal(net.cmds().length, 1, 'and for the replay');
  replay(room);
  const wire = net.cmds().map((f) => [f.q, f.d.body]);
  assert.deepEqual(wire, [[1, 'a'], [1, 'a'], [2, 'b']]);
});

const vote = (points) => ({ roundId: 'r1', points });
const edit = (body) => ({ cardId: 'c1', body, baseVersion: 1 });

test('vote 3 then 5: a retryable nack on 3 never resends it over 5', () => {
  const { room, net, timers, nacks } = setup();
  snap(room);
  room.send('poker.vote.set', vote('3'));
  room.send('poker.vote.set', vote('5'));
  room.handle({ t: 'nack', r: ROOM, q: 1, code: 'rate_limited', retry: true });
  room.handle({ t: 'ack', r: ROOM, q: 2, s: 11 });
  timers.advance(10_000);
  room.handle({ t: 'replay', r: ROOM, e: EPOCH, from: 11, lastQ: 2, txs: [] });
  assert.deepEqual(net.cmds().map((f) => [f.q, f.d.points]), [[1, '3'], [2, '5']]);
  assert.equal(room.pending().length, 0);
  assert.deepEqual(nacks, []);
});

test('a retry waiting out its backoff is dropped once a newer intent for its target is sent', () => {
  const { room, net, timers, nacks } = setup();
  snap(room);
  room.send('retro.card.edit', edit('first'));
  room.handle({ t: 'nack', r: ROOM, q: 1, code: 'server_busy', retry: true });
  room.send('retro.card.edit', edit('second'));
  timers.advance(250);
  replay(room);
  assert.deepEqual(net.cmds().map((f) => [f.q, f.d.body]), [[1, 'first'], [2, 'second']]);
  assert.deepEqual(room.pending().map((e) => e.d.body), ['second']);
  assert.deepEqual(nacks, []);
});

test('edit then edit: a retryable nack on the first drops it while the second is in flight', () => {
  const { room, net, timers } = setup();
  snap(room);
  room.send('retro.card.edit', edit('first'));
  room.send('retro.card.edit', edit('second'));
  room.handle({ t: 'nack', r: ROOM, q: 1, code: 'server_busy', retry: true });
  assert.deepEqual(room.pending().map((e) => e.d.body), ['second']);
  timers.advance(10_000);
  assert.deepEqual(net.cmds().map((f) => f.d.body), ['first', 'second']);
});

test('a superseded retry is still dropped after a reload', () => {
  const storage = memoryStorage();
  const first = setup({ storage });
  snap(first.room);
  first.room.send('poker.vote.set', vote('3'));
  first.room.handle({ t: 'nack', r: ROOM, q: 1, code: 'rate_limited', retry: true });
  first.room.send('poker.vote.set', vote('5'));

  const reloaded = setup({ storage });
  snap(reloaded.room);
  assert.deepEqual(reloaded.net.cmds().map((f) => [f.q, f.d.points]), [[2, '5']]);
  assert.deepEqual(reloaded.room.pending().map((e) => e.d.points), ['5']);
});

test('other targets and creates still retry', () => {
  const { room, net, timers } = setup();
  snap(room);
  room.send('poker.vote.set', vote('3'));
  room.send('poker.vote.set', { roundId: 'r2', points: '5' });
  room.send('retro.card.add', add('x'));
  room.send('retro.card.add', add('x'));
  for (const q of [1, 3]) {
    room.handle({ t: 'nack', r: ROOM, q, code: 'rate_limited', retry: true });
  }
  room.handle({ t: 'ack', r: ROOM, q: 2, s: 11 });
  room.handle({ t: 'ack', r: ROOM, q: 4, s: 11 });
  timers.advance(10_000);
  replay(room);
  assert.deepEqual(net.cmds().slice(4).map((f) => [f.k, f.d.points ?? f.d.body]), [['poker.vote.set', '3'], ['retro.card.add', 'x']]);
});

test('a retryable nack resends at the next snap or replay without waiting', () => {
  const { room, net } = setup();
  snap(room);
  room.send('retro.card.add', add('x'));
  room.handle({ t: 'nack', r: ROOM, q: 1, code: 'jira_unavailable', retry: true });
  replay(room);
  assert.deepEqual(net.cmds().map((f) => f.q), [1, 1]);
});

test('retry backoff doubles from 250 ms and stops at 5 s', () => {
  assert.deepEqual([0, 1, 2, 3, 4, 5, 6, 10].map(retryDelay), [250, 500, 1000, 2000, 4000, 5000, 5000, 5000]);
  assert.equal(RETRY_MAX_MS, 5000);
});

test('stale on a reorder recomputes the order against fresh state and resends once', () => {
  const { room, net, nacks } = setup();
  snap(room, { items: ['a', 'b', 'c'] });
  const rebase = (view) => ({ order: [...view.items].reverse() });
  room.send('retro.column.reorder', { order: ['c', 'b', 'a'] }, { rebase });
  room.handle({ t: 'tx', r: ROOM, s: 11, ev: [{ k: 'test.added', d: { item: 'd' } }] });
  room.handle({ t: 'nack', r: ROOM, q: 1, code: 'stale', retry: false, d: { current: ['a', 'b', 'c', 'd'] } });
  assert.deepEqual(net.cmds().at(-1), { t: 'cmd', r: ROOM, q: 2, k: 'retro.column.reorder', d: { order: ['d', 'c', 'b', 'a'] } });
  room.handle({ t: 'nack', r: ROOM, q: 2, code: 'stale', retry: false });
  assert.equal(net.cmds().length, 2, 'only once');
  assert.equal(nacks.length, 1);
  assert.equal(nacks[0].code, 'stale');
});

test('stale on ticket, focus, settings, config and title is discarded silently', () => {
  const { room, nacks } = setup();
  snap(room);
  const kinds = {
    'poker.round.setTicket': { roundId: 'r', ticketKey: 'X-1', baseVersion: 1 },
    'retro.focus.set': { cardId: null, from: null },
    'poker.settings.update': { expected: {} },
    'retro.config.update': { patch: {}, expected: {} },
    'retro.board.rename': { title: 't', expected: 'u' },
  };
  for (const [k, d] of Object.entries(kinds)) {
    const entry = room.send(k, d, { onNack: () => assert.fail(`${k} must not reach a handler`) });
    room.handle({ t: 'nack', r: ROOM, q: entry.q, code: 'stale', retry: false, d: { current: 1 } });
  }
  assert.deepEqual(nacks, []);
  assert.equal(room.pending().length, 0);
});

test('stale on a card edit reaches the merge prompt with the current body', () => {
  const { room, nacks } = setup();
  snap(room);
  const prompts = [];
  room.send('retro.card.edit', { cardId: 'c', body: 'mine', baseVersion: 2 }, {
    onNack: (n) => n.code === 'stale' && prompts.push({ current: n.d.current, mine: n.sent.body }) > 0,
  });
  room.handle({ t: 'nack', r: ROOM, q: 1, code: 'stale', retry: false, d: { current: { body: 'theirs', version: 3 } } });
  assert.deepEqual(prompts, [{ current: { body: 'theirs', version: 3 }, mine: 'mine' }]);
  assert.deepEqual(nacks, []);
});

test('spotlight_held goes to the per-send handler, which can resend with force', () => {
  const { room, net } = setup();
  snap(room);
  const d = { roundId: 'r', force: false };
  room.send('poker.spotlight.take', d, {
    onNack: (n) => {
      if (n.code !== 'spotlight_held') {
        return false;
      }
      room.send('poker.spotlight.take', { ...n.sent, force: true });
      return true;
    },
  });
  room.handle({ t: 'nack', r: ROOM, q: 1, code: 'spotlight_held', retry: false, d: { holder: 'p' } });
  assert.deepEqual(net.cmds().at(-1).d, { roundId: 'r', force: true });
});

test('cid_reuse renumbers only the nacked entry and tells the socket to look for a twin', () => {
  const { room, net, timers, nacks } = setup();
  snap(room);
  room.send('retro.card.add', add('x'));
  room.send('retro.card.add', add('y'));
  room.handle({ t: 'nack', r: ROOM, q: 1, code: 'cid_reuse', retry: false });
  assert.equal(net.reused, 1);
  assert.deepEqual(room.pending().map((e) => [e.q, e.d.body]), [[2, 'y'], [3, 'x']], 'y may have committed: it keeps its q');
  assert.deepEqual(nacks, []);
  timers.advance(250);
  replay(room);
  assert.deepEqual(net.cmds().map((f) => f.q), [1, 2, 3]);
});

test('ephemeral entries older than 10 s are dropped at resync, the rest resent', () => {
  const { room, net, timers } = setup();
  snap(room);
  room.disconnected();
  room.send('retro.focus.set', { cardId: 'c', from: null });
  room.send('retro.card.add', add('x'));
  timers.advance(10_001);
  room.send('retro.timer.pause', { from: 'running' });
  room.handle({ t: 'replay', r: ROOM, e: EPOCH, from: 11, lastQ: 0, txs: [] });
  assert.deepEqual(net.cmds().map((f) => f.k), ['retro.card.add', 'retro.timer.pause']);
});

test('a full outbox refuses the command and reports outbox_full', () => {
  const { room, nacks } = setup();
  for (let i = 0; i < 200; i++) {
    room.send('retro.vote.set', { cardId: `${i}`, on: true });
  }
  assert.equal(room.send('retro.vote.set', { cardId: 'over', on: true }), null);
  assert.equal(nacks.at(-1).code, 'outbox_full');
});

test('entries restored after a reload are re-derived through model.optimistic', () => {
  const storage = memoryStorage();
  const first = setup({ storage });
  first.room.send('retro.card.add', add('x'));
  const second = setup({ storage });
  snap(second.room, { lastQ: 0 });
  assert.deepEqual(second.room.view.items, ['a', 'x']);
  assert.deepEqual(second.net.cmds().map((f) => f.q), [1]);
});

test('a kick drops the room and its entries', () => {
  const { room } = setup();
  snap(room);
  room.send('retro.card.add', add('x'));
  room.handle({ t: 'kick', r: ROOM, code: 'access_revoked' });
  assert.equal(room.kicked, 'access_revoked');
  assert.equal(room.pending().length, 0);
  assert.equal(room.send('retro.card.add', add('y')), null);
});

test('x frames fill a cache keyed by kind and key', () => {
  const { room } = setup();
  snap(room);
  room.handle({ t: 'x', r: ROOM, k: 'jira.meta', d: { key: 'A-1', summary: 'one' } });
  room.handle({ t: 'x', r: ROOM, k: 'jira.meta', d: { key: 'A-1', summary: 'two' } });
  room.handle({ t: 'x', r: ROOM, k: 'jira.meta', d: { key: 'A-2', summary: 'x' } });
  assert.deepEqual(Object.keys(room.extras['jira.meta']), ['A-1', 'A-2']);
  assert.equal(room.extras['jira.meta']['A-1'].summary, 'two');
});

test('a seeded boot snapshot resumes from its epoch and seq', () => {
  const { room } = setup();
  room.seed({ e: EPOCH, s: 7, d: { items: ['boot'] } });
  assert.deepEqual(room.view.items, ['boot']);
  assert.deepEqual(room.resume(), { r: ROOM, e: EPOCH, s: 7 });
  assert.equal(room.synced, false);
  room.handle({ t: 'replay', r: ROOM, e: EPOCH, from: 8, lastQ: 0, txs: [] });
  assert.equal(room.synced, true);
});
