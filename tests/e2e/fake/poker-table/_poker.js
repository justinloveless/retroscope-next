// Shared setup for the poker table specs: a session the fake serves by code, the viewer's boot JSON and
// a tiny scripted server that answers vote, lock, abstain and reveal the way the real decider does for
// one viewer (the other players' votes are scripted by the spec).

export const SESSION = '0a000000-0000-4000-8000-0000000000a1';
export const ROOM = `poker:${SESSION}`;
export const CODE = 'NEOTRO1234';
export const R1 = '0a000000-0000-4000-8000-000000001001';
export const R2 = '0a000000-0000-4000-8000-000000001002';
export const ME = { pid: '0a000000-0000-4000-8000-000000000001', kind: 'user', name: 'Ada' };
export const BOB = { pid: '0a000000-0000-4000-8000-000000000002', kind: 'user', name: 'Bob' };
export const GUS = { pid: '0a000000-0000-4000-8000-000000000003', kind: 'guest', name: 'Gus' };
export const DOT = { pid: '0a000000-0000-4000-8000-000000000004', kind: 'user', name: 'Dot' };

export function round(id, number, extra = {}) {
  return { id, number, version: 1, state: 'voting', active: true, pending: false, autoReveal: true, ...extra };
}

export function snapshot(extra = {}) {
  return {
    sessionId: SESSION,
    code: CODE,
    presenceEnabled: true,
    followSpotlight: true,
    observers: [],
    rounds: [round(R1, 1, { ticketKey: 'ABC-1' })],
    votes: [],
    roster: [ME, BOB, GUS, DOT],
    presence: [ME, BOB, GUS],
    teamId: '0a000000-0000-4000-8000-0000000000b0',
    createdBy: ME.pid,
    currentRoundId: R1,
    pointDescriptions: {},
    jiraSubmits: [],
    ...extra,
  };
}

/** Serves the session to `viewer` and scripts the vote commands. Returns the votes the fake holds. */
export function setupPoker(fake, { viewer = ME, theme = 'light', snap = snapshot(), overlay = { own: [], heldByThisTab: false }, others = {} } = {}) {
  fake.me = viewer;
  fake.boot = (path) => ({ build: 'fake', theme, me: { principal: { id: viewer.pid, kind: viewer.kind, name: viewer.name } }, path });
  fake.route('GET', '/api/poker/by-code/:code', { body: { id: SESSION, access: 'granted' } });
  fake.room(ROOM, { snapshot: snap, overlay });
  const votes = new Map(Object.entries(others));
  const own = (roundId) => ({ roundId, pid: viewer.pid, ...votes.get(viewer.pid) });

  fake.onCommand('poker.vote.set', (ctx) => {
    const { roundId, points, high, lock } = ctx.frame.d;
    votes.set(viewer.pid, { points, ...(high ? { high } : {}), locked: Boolean(lock), abstained: false });
    ctx.commit([{ k: 'poker.vote.state', d: own(roundId) }]);
  });
  fake.onCommand('poker.lock.set', (ctx) => {
    const { roundId, locked } = ctx.frame.d;
    votes.set(viewer.pid, { ...votes.get(viewer.pid), locked });
    ctx.commit([{ k: 'poker.vote.state', d: own(roundId) }]);
  });
  fake.onCommand('poker.abstain.set', (ctx) => {
    const { roundId, abstain } = ctx.frame.d;
    if (abstain) {
      votes.set(viewer.pid, { locked: true, abstained: true });
      ctx.commit([{ k: 'poker.vote.state', d: own(roundId) }]);
    } else {
      votes.delete(viewer.pid);
      ctx.commit([{ k: 'poker.vote.state', d: { roundId, pid: viewer.pid, status: 'empty' } }]);
    }
  });
  fake.onCommand('poker.round.reveal', (ctx) => {
    ctx.commit([revealed(ctx.frame.d.roundId, votes, viewer.pid)]);
  });
  return votes;
}

/** The poker.round.revealed event for the votes held: unlocked votes become abstains. */
export function revealed(roundId, votes, by = null, winner = null) {
  const list = [...votes].map(([pid, v]) => (v.locked && !v.abstained
    ? { pid, points: v.points, ...(v.high ? { high: v.high } : {}), abstained: false }
    : { pid, abstained: true }));
  const counts = new Map();
  for (const v of list.filter((x) => !x.abstained)) {
    for (const p of v.high ? [v.points, v.high] : [v.points]) {
      counts.set(p, (counts.get(p) ?? 0) + 1);
    }
  }
  const best = winner ?? [...counts].sort((a, b) => b[1] - a[1] || b[0] - a[0])[0]?.[0] ?? 0;
  return { k: 'poker.round.revealed', d: { roundId, votes: list, winner: best, at: 1792497600000, ...(by ? { by } : {}) } };
}

export async function openTable(page, path = `/poker/${CODE}`) {
  await page.goto(path);
  await page.locator('.nt-table').waitFor();
}

/** The last command of kind `k` the page sent. */
export async function lastCommand(fake, k) {
  const { frame } = await fake.waitForFrame((f) => f.t === 'cmd' && f.k === k);
  return [...fake.received].reverse().find(({ frame: f }) => f.t === 'cmd' && f.k === k)?.frame ?? frame;
}

export const seat = (page, who) => page.locator(`.nt-seat[data-pid="${who.pid}"]`);
export const handCard = (page, points) => page.locator(`.nt-hand-card[data-points="${points}"]`);
