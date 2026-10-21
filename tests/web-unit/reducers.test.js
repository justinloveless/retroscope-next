// Golden transcripts (DESIGN §g.4): the C# domain tests write what one viewer receives over a scenario;
// replaying the frames through web/src/*/model.js must give the server's snapshot for that viewer and the
// same navigation target after every frame.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import * as poker from '../../web/src/poker/model.js';

const DIR = fileURLToPath(new URL('../golden/transcripts/', import.meta.url));

function transcripts(prefix) {
  return readdirSync(DIR)
    .filter((name) => name.startsWith(prefix) && name.endsWith('.json'))
    .sort()
    .map((name) => JSON.parse(readFileSync(DIR + name, 'utf8')));
}

const R1 = '00000000-0000-4000-8000-000000001001';
const R2 = '00000000-0000-4000-8000-000000001002';
const ME = '00000000-0000-4000-8000-000000000003';
const BOB = '00000000-0000-4000-8000-000000000002';
const DAVE = '00000000-0000-4000-8000-000000000004';

const round = (id, number, extra = {}) => ({
  id, number, version: 1, state: 'voting', active: true, pending: false, autoReveal: true, ...extra,
});

function snapshot(extra = {}) {
  return poker.fromSnapshot({
    sessionId: 's',
    code: 'TEAM01',
    teamId: 't',
    presenceEnabled: true,
    followSpotlight: true,
    observers: [],
    rounds: [round(R1, 1)],
    votes: [],
    roster: [
      { pid: BOB, kind: 'user', name: 'Bob' },
      { pid: ME, kind: 'user', name: 'Carol' },
      { pid: DAVE, kind: 'guest', name: 'Dave' },
    ],
    presence: [{ pid: ME, kind: 'user', name: 'Carol' }, { pid: BOB, kind: 'user', name: 'Bob' }],
    currentRoundId: R1,
    pointDescriptions: {},
    jiraSubmits: [],
    ...extra,
  }, { own: [], heldByThisTab: false }, ME);
}

const goldens = transcripts('poker-');

test('there are poker transcripts to replay', () => {
  assert.ok(goldens.length >= 5);
});

for (const transcript of goldens) {
  test(`poker transcript ${transcript.name}`, () => {
    const [snap, ...txs] = transcript.frames.filter((f) => f.t === 'snap' || f.t === 'tx');
    assert.equal(snap.t, 'snap');
    let state = poker.pokerModel(transcript.viewer.pid).snapshot(snap.d, snap.o);
    let viewed = poker.initialRound(state);
    const nav = [viewed];
    for (const tx of txs) {
      const target = poker.navTarget(state, tx.ev, { viewed });
      state = tx.ev.reduce(poker.apply, state);
      viewed = target ?? viewed;
      nav.push(target);
    }
    assert.deepEqual(nav, transcript.expectedNav, 'navigation targets');
    assert.deepEqual(poker.toWire(state), transcript.expectedViewerState, 'folded state');
  });
}

test('a snapshot folds back to itself', () => {
  for (const transcript of goldens) {
    const snap = transcript.frames.find((f) => f.t === 'snap');
    assert.deepEqual(poker.toWire(poker.fromSnapshot(snap.d, snap.o, transcript.viewer.pid)), { d: snap.d, o: snap.o });
  }
});

test('frames coalesced into one notification resolve like their last navigation', () => {
  const start = snapshot({ rounds: [round(R1, 1), round(R2, 2)] });
  const next = [
    { k: 'poker.pointer', d: { roundId: R2, cause: 'create', nav: true } },
    { k: 'presence.leave', d: { pid: BOB } },
  ].reduce(poker.apply, start);
  assert.equal(poker.resolveNav(start, next, R1), R2);
  assert.equal(poker.resolveNav(next, next, R1), null);
  assert.equal(poker.resolveNav(start, poker.apply(start, { k: 'poker.round.deleted', d: { roundId: R1 } }), R1), R2,
    'the pointer was cleared with the round, so the first remaining round is the fallback target');
});

test('a re-snapshot never navigates a viewer whose round still exists', () => {
  const start = poker.apply(snapshot(), { k: 'poker.pointer', d: { roundId: R1, nav: true } });
  assert.equal(poker.resolveNav(start, snapshot(), R1), null);
});

test('the holder is not moved by their own spotlight', () => {
  const start = snapshot({ rounds: [round(R1, 1), round(R2, 2)] });
  assert.equal(poker.navTarget(start, [{ k: 'poker.spotlight', d: { holder: ME, roundId: R2 } }], { viewed: R1 }), null);
  assert.equal(poker.navTarget(start, [{ k: 'poker.spotlight', d: { holder: DAVE, roundId: R2 } }], { viewed: R1 }), R2);
  const unfollowed = poker.apply(start, { k: 'poker.settings', d: { presenceEnabled: true, followSpotlight: false } });
  assert.equal(poker.navTarget(unfollowed, [{ k: 'poker.spotlight', d: { holder: DAVE, roundId: R2 } }], { viewed: R1 }), null);
});

test("this tab is bound to the spotlight when its own take or move comes back, not someone else's", () => {
  let pending = [];
  const model = poker.pokerModel(ME, { pending: () => pending });
  const start = snapshot({ rounds: [round(R1, 1), round(R2, 2)] });
  const fold = (state, d) => model.apply(state, { k: 'poker.spotlight', d });

  pending = [{ k: 'poker.spotlight.take', d: { roundId: R1, force: false } }];
  const taken = fold(start, { holder: ME, roundId: R1 });
  assert.equal(taken.heldByThisTab, true);

  pending = [];
  assert.equal(fold(taken, { holder: ME, roundId: R2 }).heldByThisTab, true, 'a repair keeps the binding');
  pending = [{ k: 'poker.spotlight.move', d: { roundId: R2 } }];
  assert.equal(fold(taken, { holder: ME, roundId: R2 }).heldByThisTab, true);
  assert.equal(fold(taken, { holder: DAVE, roundId: R2 }).heldByThisTab, false);
  assert.equal(fold(taken, {}).heldByThisTab, false);

  pending = [{ k: 'poker.spotlight.move', d: { roundId: R1 } }];
  assert.equal(fold(start, { holder: ME, roundId: R2 }).heldByThisTab, false, 'a give or another tab binds nobody here');
  pending = [];
  const given = fold(fold(start, { holder: DAVE, roundId: R1 }), { holder: ME, roundId: R1 });
  assert.equal(given.heldByThisTab, false);

  assert.equal(poker.apply(start, { k: 'poker.spotlight', d: { holder: ME, roundId: R1 } }).heldByThisTab, false);
  assert.equal(poker.toWire(taken).o.heldByThisTab, true);
});

test('a deep link to ?round=N wins over the spotlight and the pointer', () => {
  const start = snapshot({ rounds: [round(R1, 1), round(R2, 2)], spotlight: { holder: DAVE, roundId: R2 } });
  assert.equal(poker.initialRound(start), R2);
  assert.equal(poker.initialRound(start, 1), R1);
  assert.equal(poker.initialRound(start, 9), R2);
});

test('optimistic vote, lock and abstain intents match what the server echoes', () => {
  const start = snapshot();
  const voted = poker.voteIntent(R1, { points: 3, high: 5 })(start);
  assert.deepEqual(voted.own[R1], { roundId: R1, pid: ME, points: 3, high: 5, locked: false, abstained: false });
  assert.deepEqual(voted.votes[R1][ME], { roundId: R1, pid: ME, status: 'selected' });

  const locked = poker.lockIntent(R1, true)(voted);
  assert.equal(locked.own[R1].locked, true);
  assert.equal(locked.votes[R1][ME].status, 'locked');
  assert.equal(poker.lockIntent(R1, true)(start), start, 'nothing to lock without a vote');

  const abstained = poker.abstainIntent(R1, true)(voted);
  assert.deepEqual(abstained.own[R1], { roundId: R1, pid: ME, locked: true, abstained: true });
  const cleared = poker.abstainIntent(R1, false)(abstained);
  assert.equal(cleared.own[R1], undefined);
  assert.equal(cleared.votes[R1][ME], undefined);

  assert.deepEqual(poker.optimistic(start, 'poker.vote.set', { roundId: R1, points: 8, lock: true }).own[R1],
    { roundId: R1, pid: ME, points: 8, locked: true, abstained: false });
  assert.equal(poker.optimistic(start, 'poker.round.reveal', { roundId: R1 }), start);
});

test('a ticket edit shows at once and bumps the version the next edit is based on', () => {
  const start = snapshot({ rounds: [round(R1, 1, { ticketKey: 'ABC-1', parentKey: 'ABC-0' })] });
  const edited = poker.ticketIntent(R1, 'ABC-2')(start);
  assert.deepEqual(poker.roundById(edited, R1), round(R1, 1, { ticketKey: 'ABC-2', version: 2 }));
  assert.equal(poker.roundById(poker.ticketIntent(R1, null)(start), R1).ticketKey, undefined);
});

test('seats: roster, present and voters minus observers, with presence and the guest kind', () => {
  const state = [
    { k: 'poker.vote.state', d: { roundId: R1, pid: BOB, status: 'locked' } },
    { k: 'poker.observers', d: { ids: [BOB] } },
  ].reduce(poker.apply, snapshot());
  const seats = poker.seatsOf(state, R1);
  assert.deepEqual(seats.map((s) => [s.name, s.present, s.kind, s.me]), [
    ['Carol', true, 'user', true],
    ['Dave', false, 'guest', false],
  ]);
  const everyone = poker.apply(state, { k: 'poker.settings', d: { presenceEnabled: false, followSpotlight: true } });
  assert.ok(poker.seatsOf(everyone, R1).every((s) => s.present), 'presence off shows everyone as present');
});

test('quick sessions seat only present principals and voters', () => {
  const state = poker.apply(snapshot({ teamId: undefined }), {
    k: 'poker.vote.state', d: { roundId: R1, pid: DAVE, status: 'selected' },
  });
  assert.deepEqual(poker.seatsOf(state, R1).map((s) => s.name).sort(), ['Bob', 'Carol', 'Dave']);
  assert.deepEqual(poker.seatsOf(state, R2).map((s) => s.name).sort(), ['Bob', 'Carol']);
});

test('presence join, update and leave keep the canonical order', () => {
  const ZED = 'ffffffff-0000-4000-8000-000000000009';
  let state = poker.apply(snapshot(), { k: 'presence.join', d: { pid: ZED, kind: 'guest', name: 'Zed' } });
  state = poker.apply(state, { k: 'presence.join', d: { pid: DAVE, kind: 'guest', name: 'Dave' } });
  assert.deepEqual(state.presence.map((p) => p.name), ['Bob', 'Carol', 'Dave', 'Zed']);
  state = poker.apply(state, { k: 'presence.update', d: { pid: ZED, name: 'Zoe' } });
  assert.equal(state.presence.at(-1).name, 'Zoe');
  state = poker.apply(state, { k: 'presence.leave', d: { pid: BOB } });
  assert.deepEqual(state.presence.map((p) => p.name), ['Carol', 'Dave', 'Zoe']);
  assert.equal(poker.apply(state, { k: 'presence.update', d: { pid: BOB, name: 'X' } }), state);
});

test('revealed groups sort by value, between after its low value, abstains only when nobody voted', () => {
  const reveal = (votes) => poker.apply(snapshot(), {
    k: 'poker.round.revealed', d: { roundId: R1, votes, winner: 5, at: 1 },
  });
  const groups = poker.revealGroups(reveal([
    { pid: BOB, points: 5, abstained: false },
    { pid: ME, points: 3, high: 5, abstained: false },
    { pid: DAVE, points: 3, abstained: false },
    { pid: 'x', abstained: true },
  ]), R1);
  assert.deepEqual(groups.map((g) => [g.points, g.high, g.voters.map((v) => v.name)]), [
    [3, null, ['Dave']],
    [3, 5, ['Carol']],
    [5, null, ['Bob']],
  ]);
  const abstained = poker.revealGroups(reveal([{ pid: BOB, abstained: true }, { pid: ME, abstained: true }]), R1);
  assert.deepEqual(abstained.map((g) => [g.abstain, g.voters.length]), [[true, 2]]);
});

test('reveal, replay and round removal update votes, own entries and the pointer', () => {
  let state = poker.apply(snapshot(), { k: 'poker.vote.state', d: { roundId: R1, pid: ME, points: 8, locked: true, abstained: false } });
  state = poker.apply(state, { k: 'poker.round.revealed', d: { roundId: R1, votes: [{ pid: ME, points: 8, abstained: false }], winner: 8, at: 5 } });
  assert.equal(state.own[R1], undefined);
  assert.deepEqual(poker.roundById(state, R1), round(R1, 1, { state: 'revealed', winner: 8, revealedAt: 5 }));
  assert.equal(poker.roundPhase(poker.roundById(state, R1)), 'revealed');
  state = poker.apply(state, { k: 'poker.round.replayed', d: { roundId: R1 } });
  assert.deepEqual(state.votes[R1][ME], { roundId: R1, pid: ME, status: 'locked' });
  state = poker.apply(state, { k: 'poker.round.deleted', d: { roundId: R1 } });
  assert.equal(state.currentRoundId, null);
  assert.deepEqual(state.votes, {});
  assert.equal(poker.apply(state, { k: 'poker.unknown', d: {} }), state);
});

test('deck helpers, labels and card scales', () => {
  assert.equal(poker.nextDeckValue(3), 5);
  assert.equal(poker.nextDeckValue(21), null);
  assert.equal(poker.voteLabel(3, 5), '3–5');
  assert.equal(poker.voteLabel(8), '8 pts');
  assert.deepEqual([1, 4, 5, 6, 7, 8, 12, 13].map((n) => poker.cardScale(n)), [1.6, 1.6, 1.4, 1.4, 1.2, 1.2, 1.0, 0.8]);
  assert.deepEqual([4, 6, 8, 12, 20].map((n) => poker.cardScale(n, true)), [1.4, 1.2, 1.0, 0.8, 0.8]);
  assert.deepEqual(['pending', 'revealed', 'voting', 'inactive', 'missing'], [
    poker.roundPhase(round(R1, 1, { pending: true })),
    poker.roundPhase(round(R1, 1, { state: 'revealed' })),
    poker.roundPhase(round(R1, 1)),
    poker.roundPhase(round(R1, 1, { active: false })),
    poker.roundPhase(null),
  ]);
});

test('uuidv7 puts the time first and sets the version and variant', () => {
  const id = poker.uuidv7(0x0190_0000_0001, (n) => new Uint8Array(n).fill(0xff));
  assert.equal(id, '01900000-0001-7fff-bfff-ffffffffffff');
  assert.match(poker.uuidv7(), /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
});
