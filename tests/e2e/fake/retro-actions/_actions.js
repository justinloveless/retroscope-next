// Shared setup for the action-item and kudos specs: a team board (from the retro-board helpers), the
// team room its members also join, and a scripted team decider for assign/done.
import { expect } from '../_harness/fixtures.js';
import { json } from '../../../web-fake-server/http.js';
import { ACTIONS, ALICE, BOB, CAROL, CODE, EMPTY_OVERLAY, ROOM, TEAM_ID, card, teamSnapshot } from '../retro-board/_board.js';

export { ACTIONS, ALICE, BOARD_ID, BOB, CAROL, GOOD, ROOM, TEAM_ID, card, cardId, sent, serveBoard, snapshot, teamSnapshot } from '../retro-board/_board.js';

export const TEAM_ROOM = `team:${TEAM_ID}`;
export const OTHER_BOARD = 'b0000000-0000-4000-8000-0000000000b2';
export const TYPE_HELPER = 'e0000000-0000-4000-8000-000000000001';
export const TYPE_IDEAS = 'e0000000-0000-4000-8000-000000000002';
export const TYPE_RETIRED = 'e0000000-0000-4000-8000-000000000003';

export const MEMBERS = [
  { pid: ALICE.pid, name: ALICE.name, role: 'owner' },
  { pid: BOB.pid, name: BOB.name, role: 'member' },
  { pid: CAROL.pid, name: CAROL.name, role: 'member' },
];

export const TYPES = [
  { id: TYPE_HELPER, name: 'Team Player', emoji: '🤝', archived: false, description: 'Always there to help' },
  { id: TYPE_IDEAS, name: 'Innovator', emoji: '💡', archived: false },
  { id: TYPE_RETIRED, name: 'Old type', emoji: '🗿', archived: true },
];

const T0 = 1_760_000_000_000;

export function itemId(n) {
  return `a0000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
}

export function item(n, fields = {}) {
  return { id: itemId(n), text: `Action ${n}`, sourceVersion: 1, createdAt: T0 + n * 1000, commentCount: 0, ...fields };
}

/** An action-items card on this board and its team item. */
export function actionCard(n, fields = {}) {
  return card(n, { columnId: ACTIONS, body: `Follow up ${n}`, ...fields });
}

export function teamRoom(fields = {}) {
  return {
    teamId: TEAM_ID,
    name: 'Platform',
    members: MEMBERS,
    actionItems: [],
    endorsementTypes: TYPES,
    pointDescriptions: {},
    maxEndorsements: 3,
    ...fields,
  };
}

export function boardFor(fields = {}) {
  return teamSnapshot({ members: MEMBERS, endorsementTypes: TYPES, ...fields });
}

/** The board's own column titled `title` (not the open-action-items column). */
export const column = (page, title) => page.locator('.retro-column-list > .retro-column')
  .filter({ has: page.locator('.retro-column-title', { hasText: new RegExp(`^${title}$`) }) });
export const cardsIn = (page, title) => column(page, title).locator('.retro-card');

/**
 * Points the page at the board and waits for its own columns. The retro-board helper counts every
 * `.retro-column`, which would include the open-action-items column.
 */
export async function openBoard(page, fake, { snap = boardFor(), overlay = EMPTY_OVERLAY, me = ALICE } = {}) {
  fake.me = me;
  fake.boot = { build: 'fake', theme: 'light', me, room: { r: ROOM } };
  fake.room(ROOM, { snapshot: snap, overlay, epoch: 'aaaaaaaaaaaaaaaa', seq: 0 });
  await page.goto(`/retro/${CODE}`);
  await expect(page.locator('.retro-column-list > .retro-column')).toHaveCount(snap.columns.length);
  await fake.waitForFrame((f) => f.t === 'hello');
}

/** Installs the team room and answers assign/done the way TeamDecider does. */
export function serveTeam(fake, snap = teamRoom()) {
  const items = new Map(snap.actionItems.map((i) => [i.id, { ...i }]));
  fake.room(TEAM_ROOM, { snapshot: snap, epoch: 'bbbbbbbbbbbbbbbb', seq: 0 });
  fake.onCommand('team.action.assign', (ctx) => {
    const { itemId: id, userId } = ctx.frame.d;
    const { assignedTo, ...rest } = items.get(id);
    const next = userId ? { ...rest, assignedTo: userId } : rest;
    items.set(id, next);
    ctx.commit([{ k: 'team.action.updated', d: { item: next } }]);
  });
  fake.onCommand('team.action.done', (ctx) => {
    const { itemId: id, done } = ctx.frame.d;
    const { doneAt, doneBy, ...rest } = items.get(id);
    const next = done ? { ...rest, doneAt: T0 + 500_000, doneBy: ctx.conn.me.pid } : rest;
    items.set(id, next);
    ctx.commit([{ k: 'team.action.updated', d: { item: next } }]);
  });
  return items;
}

/** `GET /api/teams/{id}` in the U30 shape, with the Jira part the board reads. */
export function serveTeamInfo(fake, jira = { configured: false }) {
  fake.route('GET', `/api/teams/${TEAM_ID}`, json({
    id: TEAM_ID,
    name: 'Platform',
    description: '',
    role: 'owner',
    pointDescriptions: {},
    maxEndorsements: 3,
    jira: { status: 'ok', membersCanEdit: true, ...jira },
    counts: { boards: 2, members: 3 },
  }));
}

export function teamSubs(fake) {
  return fake.received.filter(({ frame }) => (frame.t === 'sub' && frame.r === TEAM_ROOM)
    || (frame.t === 'hello' && frame.subs.some((s) => s.r === TEAM_ROOM)));
}

