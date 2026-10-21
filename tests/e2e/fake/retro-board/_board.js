// Shared setup for the retro board specs: a board snapshot, the boot JSON that points the page at it,
// and a small scripted server that answers board commands with the events the real decider emits.
import { expect } from '../_harness/fixtures.js';

export const BOARD_ID = 'b0000000-0000-4000-8000-0000000000b1';
export const ROOM = `board:${BOARD_ID}`;
export const CODE = 'RETRO12345';
export const TEAM_ID = '7e000000-0000-4000-8000-000000000001';

export const ALICE = { pid: 'a1000000-0000-4000-8000-000000000001', kind: 'user', name: 'Alice' };
export const BOB = { pid: 'b2000000-0000-4000-8000-000000000002', kind: 'user', name: 'Bob' };
export const CAROL = { pid: 'c3000000-0000-4000-8000-000000000003', kind: 'user', name: 'Carol Danvers' };
export const GUEST = { pid: '90000000-0000-4000-8000-000000000004', kind: 'guest', name: 'Sneaky Penguin' };

export const GOOD = 'c0000000-0000-4000-8000-000000000001';
export const BAD = 'c0000000-0000-4000-8000-000000000002';
export const ACTIONS = 'c0000000-0000-4000-8000-000000000003';

export const CONFIG = {
  allowAnonymous: true,
  votingEnabled: true,
  showAuthorNames: true,
  stagesEnabled: false,
  enforceReadiness: false,
  allowSelfVotes: true,
  voteEmoji: '👍',
  sortChronologically: false,
};

const T0 = 1_760_000_000_000;

export function cardId(n) {
  return `ca000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
}

export function card(n, fields = {}) {
  const { author = BOB, ...rest } = fields;
  const base = { id: cardId(n), columnId: GOOD, body: `Card ${n}`, anonymous: false, version: 1, createdAt: T0 + n * 1000 };
  return author ? { ...base, author: { pid: author.pid, name: author.name, guest: author.kind === 'guest' }, ...rest } : { ...base, ...rest };
}

export function snapshot(fields = {}) {
  return {
    boardId: BOARD_ID,
    code: CODE,
    meta: { title: 'Sprint 42 retro', visibility: 'link', archived: false },
    config: CONFIG,
    stage: 'thinking',
    timer: { state: 'idle', durationMs: 900000, alarm: true, music: false },
    columns: [
      { id: GOOD, title: 'Good', color: 'green', actionItems: false, version: 1 },
      { id: BAD, title: 'Bad', color: 'red', actionItems: false, version: 1 },
      { id: ACTIONS, title: 'Action Items', color: 'yellow', actionItems: true, version: 1 },
    ],
    cards: [],
    comments: [],
    votes: [],
    readiness: { stage: 'thinking', ready: [] },
    presence: [ALICE, BOB],
    ...fields,
  };
}

/** A team board where Alice and Bob are members, as members see it. */
export function teamSnapshot(fields = {}) {
  return snapshot({
    teamId: TEAM_ID,
    members: [
      { pid: ALICE.pid, name: ALICE.name, role: 'owner' },
      { pid: BOB.pid, name: BOB.name, role: 'member' },
      { pid: CAROL.pid, name: CAROL.name, role: 'member' },
    ],
    endorsements: [],
    endorsementTypes: [],
    maxEndorsements: 3,
    ...fields,
  });
}

export const EMPTY_OVERLAY = { mineCards: [], mineComments: [], myVotes: [] };

/** Points the page at the board and waits for its columns. The snapshot arrives over the socket. */
export async function openBoard(page, fake, { snap = snapshot(), overlay = EMPTY_OVERLAY, me = ALICE, seedBoot = false } = {}) {
  fake.me = me;
  fake.boot = { build: 'fake', theme: 'light', me, room: seedBoot ? { r: ROOM, e: 'aaaaaaaaaaaaaaaa', s: 0, d: snap, o: overlay } : { r: ROOM } };
  fake.room(ROOM, { snapshot: snap, overlay, epoch: 'aaaaaaaaaaaaaaaa', seq: 0 });
  await page.goto(`/retro/${CODE}`);
  await expect(page.locator('.retro-column')).toHaveCount(snap.columns.length);
  await fake.waitForFrame((f) => f.t === 'hello');
}

function authorFor(me, anonymous, config) {
  return anonymous || !config.showAuthorNames ? undefined : { pid: me.pid, name: me.name, guest: me.kind === 'guest' };
}

/**
 * Answers card, comment, vote and column commands the way the decider does, against a copy of the
 * snapshot it keeps up to date. Returns the state so specs can inspect it.
 */
export function serveBoard(fake, snap = snapshot(), { config = snap.config } = {}) {
  const state = {
    cards: new Map(snap.cards.map((c) => [c.id, c])),
    votes: new Map(snap.votes.map((v) => [v.cardId, v.count])),
    mine: new Set(),
    comments: new Map(snap.comments.map((c) => [c.id, c])),
    columns: snap.columns.map((c) => ({ ...c })),
    clock: T0 + 100_000,
  };
  const at = () => (state.clock += 1000);
  fake.onCommand('retro.card.add', (ctx) => {
    const d = ctx.frame.d;
    const added = { id: d.cardId, columnId: d.columnId, body: d.body, anonymous: d.anonymous, version: 1, createdAt: at() };
    const author = authorFor(ctx.conn.me, d.anonymous, config);
    const cardInfo = author ? { ...added, author } : added;
    state.cards.set(d.cardId, cardInfo);
    ctx.commit([{ k: 'retro.card.added', d: { card: { ...cardInfo, mine: true } } }]);
  });
  fake.onCommand('retro.card.edit', (ctx) => {
    const d = ctx.frame.d;
    const current = state.cards.get(d.cardId);
    if (current.version !== d.baseVersion) {
      ctx.nack('stale', { d: { current: { body: current.body, version: current.version } } });
      return;
    }
    state.cards.set(d.cardId, { ...current, body: d.body, version: current.version + 1 });
    ctx.commit([{ k: 'retro.card.edited', d: { cardId: d.cardId, body: d.body, version: current.version + 1 } }]);
  });
  fake.onCommand('retro.card.delete', (ctx) => {
    state.cards.delete(ctx.frame.d.cardId);
    ctx.commit([{ k: 'retro.card.deleted', d: { cardId: ctx.frame.d.cardId } }]);
  });
  fake.onCommand('retro.vote.set', (ctx) => {
    const { cardId, on } = ctx.frame.d;
    if (state.mine.has(cardId) === on) {
      ctx.ack();
      return;
    }
    if (on && config.maxVotes != null && state.mine.size >= config.maxVotes) {
      ctx.nack('vote_limit');
      return;
    }
    if (on) {
      state.mine.add(cardId);
    } else {
      state.mine.delete(cardId);
    }
    const count = (state.votes.get(cardId) ?? 0) + (on ? 1 : -1);
    state.votes.set(cardId, count);
    ctx.commit([{ k: 'retro.vote', d: { cardId, count, mine: on } }]);
  });
  fake.onCommand('retro.comment.add', (ctx) => {
    const d = ctx.frame.d;
    const comment = { id: d.commentId, cardId: d.cardId, body: d.body, anonymous: d.anonymous, createdAt: at() };
    const author = authorFor(ctx.conn.me, d.anonymous, config);
    state.comments.set(d.commentId, comment);
    ctx.commit([{ k: 'retro.comment.added', d: { comment: { ...(author ? { ...comment, author } : comment), mine: true } } }]);
  });
  fake.onCommand('retro.comment.delete', (ctx) => {
    const { commentId } = ctx.frame.d;
    const comment = state.comments.get(commentId);
    state.comments.delete(commentId);
    ctx.commit([{ k: 'retro.comment.deleted', d: { commentId, cardId: comment.cardId } }]);
  });
  fake.onCommand('retro.column.add', (ctx) => {
    const d = ctx.frame.d;
    const column = { id: d.columnId, title: d.title, color: d.color, actionItems: false, version: 1 };
    state.columns.push(column);
    ctx.commit([{ k: 'retro.column.added', d: { column } }]);
  });
  fake.onCommand('retro.column.update', (ctx) => {
    const d = ctx.frame.d;
    const column = state.columns.find((c) => c.id === d.columnId);
    if (column.version !== d.baseVersion) {
      ctx.nack('stale', { d: { current: column } });
      return;
    }
    Object.assign(column, d.title === undefined ? {} : { title: d.title }, d.color === undefined ? {} : { color: d.color });
    column.version += 1;
    ctx.commit([{ k: 'retro.column.updated', d: { column: { ...column } } }]);
  });
  fake.onCommand('retro.column.delete', (ctx) => {
    state.columns = state.columns.filter((c) => c.id !== ctx.frame.d.columnId);
    ctx.commit([{ k: 'retro.column.deleted', d: { columnId: ctx.frame.d.columnId } }]);
  });
  fake.onCommand('retro.column.setActionItems', (ctx) => {
    const { columnId } = ctx.frame.d;
    for (const column of state.columns) {
      column.actionItems = column.id === columnId;
    }
    ctx.commit([{ k: 'retro.column.actionItems', d: columnId ? { columnId } : {} }]);
  });
  fake.onCommand('retro.column.reorder', (ctx) => {
    const { order } = ctx.frame.d;
    const current = state.columns.map((c) => c.id);
    if (order.length !== current.length || !current.every((id) => order.includes(id))) {
      ctx.nack('stale', { d: { current } });
      return;
    }
    state.columns = order.map((id) => state.columns.find((c) => c.id === id));
    ctx.commit([{ k: 'retro.column.order', d: { order } }]);
  });
  return state;
}

export const column = (page, title) => page.locator('.retro-column').filter({ has: page.locator('.retro-column-title', { hasText: title }) });
export const cardsIn = (page, title) => column(page, title).locator('.retro-card');
export const editorIn = (scope) => scope.locator('.rs-editor-input');

/** The `d` of every command of kind `k` the page sent, in order. */
export function sent(fake, k) {
  return fake.received.filter(({ frame }) => frame.t === 'cmd' && frame.k === k).map(({ frame }) => frame.d);
}

export async function typeCard(page, title, text) {
  const target = column(page, title);
  await target.locator('.retro-add-card').click();
  const input = editorIn(target);
  await expect(input).toBeFocused();
  await page.keyboard.type(text);
  return input;
}
