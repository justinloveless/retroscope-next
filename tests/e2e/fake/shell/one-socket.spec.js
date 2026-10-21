// One socket per tab (§g.4): the team page, a poker session and a retro board share the connection the
// entry opens, so in-app navigation between them never opens a second socket, never sends a second
// hello and never runs into 4409 (two sockets of one tab would share the cid).
import { expect, test } from '../_harness/fixtures.js';
import { OWNER, ROOM as TEAM_ROOM, TEAM_ID, setupTeam, teamUrl } from '../team/_team.js';
import { CODE as POKER_CODE, ROOM as POKER_ROOM, SESSION, snapshot as pokerSnapshot } from '../poker-table/_poker.js';
import { BOARD_ID, CODE as BOARD_CODE, ROOM as BOARD_ROOM, snapshot as boardSnapshot } from '../retro-board/_board.js';

const OWNER_SEAT = { pid: OWNER, kind: 'user', name: 'Olive Owner' };

/** Navigates in-app, the way a `<a data-nav>` link on any screen does. */
async function follow(page, href) {
  await page.evaluate((to) => {
    const link = document.createElement('a');
    link.href = to;
    link.dataset.nav = '';
    document.body.append(link);
    link.click();
    link.remove();
  }, href);
}

test('team, poker and board pages share one socket and one client id', async ({ page, fake }) => {
  setupTeam(fake, { me: OWNER });
  fake.route('GET', '/api/poker/by-code/:code', { body: { id: SESSION, access: 'granted' } });
  fake.room(POKER_ROOM, { snapshot: pokerSnapshot({ teamId: TEAM_ID, roster: [OWNER_SEAT], presence: [OWNER_SEAT], createdBy: OWNER }) });
  fake.route('GET', '/api/retro/by-code/:code', { body: { id: BOARD_ID, access: 'granted' } });
  fake.room(BOARD_ROOM, { snapshot: boardSnapshot({ presence: [OWNER_SEAT] }) });

  const sockets = [];
  page.on('websocket', (ws) => sockets.push(ws));
  const subscribed = (room) => fake.waitForFrame((f) => (f.t === 'hello' && f.subs.some((s) => s.r === room)) || (f.t === 'sub' && f.r === room));

  await page.goto(teamUrl());
  await expect(page.getByRole('heading', { name: 'Platform' })).toBeVisible();
  await subscribed(TEAM_ROOM);

  await follow(page, `/teams/${TEAM_ID}/poker/${POKER_CODE}`);
  await page.locator('.nt-table').waitFor();
  await subscribed(POKER_ROOM);

  await follow(page, `/retro/${BOARD_CODE}`);
  await expect(page.locator('.retro-column')).toHaveCount(3);
  await subscribed(BOARD_ROOM);

  await follow(page, teamUrl());
  await expect(page.getByRole('heading', { name: 'Platform' })).toBeVisible();
  await fake.waitForFrame((f) => f.t === 'sub' && f.r === TEAM_ROOM, { after: fake.received.findIndex(({ frame }) => frame.t === 'sub' && frame.r === BOARD_ROOM) });

  expect(sockets).toHaveLength(1);
  expect(sockets[0].isClosed()).toBe(false);
  expect(fake.connections).toHaveLength(1);
  expect(fake.connections[0].open).toBe(true);
  const hellos = fake.received.filter(({ frame }) => frame.t === 'hello');
  expect(hellos).toHaveLength(1);
  expect(fake.received.every(({ conn }) => conn === fake.connections[0])).toBe(true);
});
