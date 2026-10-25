// Shared fixtures for the team screen specs: a team room snapshot, the boot JSON of a signed-in
// member, and HTTP routes for the lists. Response shapes follow docs/decisions/U30.md and UX18.md.
import { json } from '../../../web-fake-server/http.js';
import { memberMe } from '../_harness/me.js';

export { expect, test } from '../_harness/fixtures.js';

/** Picks `option` from a menu-button choice (ui/choice.js), the screens' stand-in for a native select. */
export async function choose(choice, option) {
  await choice.click();
  await choice.page().getByRole('menuitem', { name: option, exact: true }).click();
}

export const TEAM_ID = '0192f0a0-0000-7000-8000-00000000a001';
export const ROOM = `team:${TEAM_ID}`;
export const OWNER = '0192f0a0-0000-7000-8000-0000000000a1';
export const ADMIN = '0192f0a0-0000-7000-8000-0000000000a2';
export const MEMBER = '0192f0a0-0000-7000-8000-0000000000a3';
export const BOARD_1 = '0192f0a0-0000-7000-8000-0000000000b1';
export const BOARD_2 = '0192f0a0-0000-7000-8000-0000000000b2';
export const ITEM_1 = '0192f0a0-0000-7000-8000-0000000000c1';
export const ITEM_2 = '0192f0a0-0000-7000-8000-0000000000c2';
export const CARD_1 = '0192f0a0-0000-7000-8000-0000000000d1';
export const TYPE_1 = '0192f0a0-0000-7000-8000-0000000000e1';
export const TYPE_2 = '0192f0a0-0000-7000-8000-0000000000e2';

const NAMES = { [OWNER]: 'Olive Owner', [ADMIN]: 'Adam Admin', [MEMBER]: 'Mia Member' };
const T0 = Date.UTC(2026, 9, 1, 9, 30);

export const members = () => [
  { pid: OWNER, name: NAMES[OWNER], role: 'owner' },
  { pid: ADMIN, name: NAMES[ADMIN], role: 'admin' },
  { pid: MEMBER, name: NAMES[MEMBER], role: 'member' },
];

export function actionItem(id, text, extra = {}) {
  return { id, text, sourceVersion: 1, createdAt: T0, commentCount: 0, ...extra };
}

export function snapshot(extra = {}) {
  return {
    teamId: TEAM_ID,
    name: 'Platform',
    members: members(),
    actionItems: [
      actionItem(ITEM_1, 'Fix the **flaky** deploy', { sourceBoardId: BOARD_1, sourceCardId: CARD_1, commentCount: 1, createdAt: T0 + 1000 }),
      actionItem(ITEM_2, 'Write the runbook', { sourceBoardId: BOARD_2, assignedTo: MEMBER }),
    ],
    endorsementTypes: [
      { id: TYPE_1, name: 'Helper', emoji: '🤝', archived: false },
      { id: TYPE_2, name: 'Teacher', emoji: '🎓', archived: false },
    ],
    pointDescriptions: { 3: 'A day' },
    maxEndorsements: 3,
    ...extra,
  };
}

export function team(role, extra = {}) {
  return {
    id: TEAM_ID,
    name: 'Platform',
    description: 'Keeps the lights on',
    role,
    pointDescriptions: { 3: 'A day' },
    maxEndorsements: 3,
    jira: { configured: true, siteHost: 'acme.atlassian.net', projectKey: 'PLAT', boardId: 7, ticketPrefix: 'PLAT-', status: 'ok', membersCanEdit: true },
    counts: { boards: 2, members: 3 },
    ...extra,
  };
}

export function board(id, title, extra = {}) {
  return {
    id,
    code: `CODE${id.slice(-2).toUpperCase()}AAAA`,
    title,
    archived: false,
    createdAt: T0,
    stage: 'thinking',
    stagesEnabled: false,
    cardCount: 0,
    lastActivityAt: T0,
    ...extra,
  };
}

export const listItems = () => [
  { ...snapshot().actionItems[0], boardTitle: 'Sprint 41 retro' },
  { ...snapshot().actionItems[1], boardTitle: 'Sprint 40 retro' },
];

/**
 * Signs in as `me` (OWNER, ADMIN or MEMBER), creates the team room and routes the team's HTTP API.
 * Returns the routes' call log by name, e.g. calls.boards, and `leave()`, which takes the team out of `me`.
 */
export function setupTeam(fake, { me = OWNER, emailVerified = true, roomSnapshot = snapshot(), teamInfo } = {}) {
  const role = members().find((m) => m.pid === me).role;
  const signedIn = memberMe({ pid: me, name: NAMES[me] }, [{ id: TEAM_ID, name: 'Platform', role, openActions: 2 }]);
  signedIn.user.emailVerified = emailVerified;
  let current = signedIn;
  fake.boot = (path) => ({ build: 'fake', theme: 'light', path, me: current });
  fake.route('GET', '/api/me', () => json(current));
  fake.me = { pid: me, kind: 'user', name: NAMES[me] };
  fake.room(ROOM, { snapshot: roomSnapshot });

  const base = `/api/teams/${TEAM_ID}`;
  const calls = {};
  const route = (name, method, path, response) => {
    calls[name] = [];
    fake.route(method, base + path, (req) => {
      calls[name].push(req);
      return typeof response === 'function' ? response(req) : response;
    });
  };
  // The user is no longer on the team (left it, or deleted it): `me` drops it.
  const leave = () => {
    current = { ...current, teams: [], lastTeamId: null };
  };
  route('team', 'GET', '', json(teamInfo ?? team(role)));
  route('boards', 'GET', '/boards', (req) => json({
    items: req.query.get('archived') === 'true'
      ? [board(BOARD_2, 'Sprint 40 retro', { archived: true, archivedAt: T0 + 86_400_000 })]
      : [board(BOARD_1, 'Sprint 41 retro')],
    nextCursor: null,
  }));
  route('sessions', 'GET', '/poker-sessions', json({ items: [], nextCursor: null }));
  route('actionItems', 'GET', '/action-items', json({ items: listItems(), nextCursor: null }));
  route('invites', 'GET', '/invites', json({ items: [] }));
  route('templates', 'GET', '/templates', json({ items: [] }));
  route('leaderboard', 'GET', '/endorsements/leaderboard', json({ entries: [] }));
  route('types', 'GET', '/endorsement-types', json({ items: snapshot().endorsementTypes }));
  route('jira', 'GET', '/jira', json({
    configured: true,
    siteHost: 'acme.atlassian.net',
    projectKey: 'PLAT',
    boardId: 7,
    ticketPrefix: 'PLAT-',
    membersCanEdit: true,
    status: 'ok',
    lastCheckedAt: T0,
    emailMasked: 'j•••@acme.com',
    tokenLast4: 'x9Qa',
  }));
  return Object.assign(calls, { leave });
}

export const teamUrl = (query = '') => `/teams/${TEAM_ID}${query}`;
