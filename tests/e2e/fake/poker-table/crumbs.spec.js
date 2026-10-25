// The poker toolbar leads back (UX18 decision 12): "<team> / Poker / <day>" from the snapshot's team, named
// from `me`, and the session's day from its id. The address becomes the session's under its own team without a
// history entry, Copy link shows for every session, and a session that is missing or not the user's shows the
// no-access screen with the app header.
import { problem } from '../../../web-fake-server/http.js';
import { expect, test } from '../_harness/fixtures.js';
import { memberMe } from '../_harness/me.js';
import { CODE, ME, ROOM, TEAM, openTable, setupPoker, snapshot } from './_poker.js';

const CREATED = Date.UTC(2026, 9, 14, 13, 5);
// A UUIDv7 carries its creation time in its first 48 bits, which is where the toolbar's day comes from.
const SESSION_V7 = `${CREATED.toString(16).padStart(12, '0').replace(/^(.{8})(.{4})$/, '$1-$2')}-7000-8000-0000000000a7`;
const OTHER_TEAM = '0a000000-0000-4000-8000-0000000000b9';

const crumbs = (page) => page.locator('.nt-toolbar').getByRole('navigation', { name: 'Breadcrumb' });

function setup(fake, snap = {}) {
  setupPoker(fake, { snap: snapshot({ sessionId: SESSION_V7, ...snap }) });
}

test('the toolbar leads back with “<team> / Poker / <day>”', async ({ page, fake }) => {
  setup(fake);
  await openTable(page, `/teams/${TEAM.id}/poker/${CODE}`);

  await expect(crumbs(page).getByRole('link')).toHaveText(['PPlatform', 'Poker']);
  await expect(crumbs(page).getByRole('link', { name: 'Platform' })).toHaveAttribute('href', `/teams/${TEAM.id}`);
  await expect(crumbs(page).getByRole('link', { name: 'Poker' })).toHaveAttribute('href', `/teams/${TEAM.id}`);
  await expect(crumbs(page).locator('[aria-current="page"]')).toHaveText(/Oct 14/);
  await expect(page.locator('header.app-header')).toBeHidden();

  await crumbs(page).getByRole('link', { name: 'Poker' }).click();
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM.id}$`));
  await expect(page.locator('header.app-header')).toBeVisible();
});

for (const [name, path] of [['/poker/:code', `/poker/${CODE}`], ['another team’s address', `/teams/${OTHER_TEAM}/poker/${CODE}`]]) {
  test(`a session opened at ${name} moves to its own team’s address without a history entry`, async ({ page, fake }) => {
    setup(fake);
    await page.goto('/login');
    const before = await page.evaluate(() => history.length);
    await page.goto(path);
    await page.locator('.nt-table').waitFor();

    await expect(page).toHaveURL(new RegExp(`/teams/${TEAM.id}/poker/${CODE}\\?round=1$`));
    expect(await page.evaluate(() => history.length)).toBe(before + 1);
    await expect(crumbs(page).getByRole('link', { name: 'Platform' })).toBeVisible();
  });
}

test('Copy link copies the session’s own address', async ({ page, fake }) => {
  await page.addInitScript(() => {
    window.__copied = [];
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: (text) => Promise.resolve(window.__copied.push(text)) } });
  });
  setup(fake);
  await openTable(page, `/poker/${CODE}`);
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM.id}/poker/`));

  await page.locator('.nt-toolbar').getByRole('button', { name: 'Copy link' }).click();

  await expect(page.getByText('Link copied!')).toBeVisible();
  expect(await page.evaluate(() => window.__copied)).toEqual([`${fake.url}/teams/${TEAM.id}/poker/${CODE}`]);
});

test('a session that is missing or not the user’s shows the no-access screen under the app header', async ({ page, fake }) => {
  fake.me = ME;
  fake.boot = { build: 'fake', theme: 'light', me: memberMe(ME, [{ ...TEAM, role: 'member' }]) };
  fake.route('GET', '/api/poker/by-code/:code', problem(404, 'not_found'));
  await page.goto(`/teams/${OTHER_TEAM}/poker/${CODE}`);

  await expect(page.getByRole('heading', { level: 1, name: "You can't open this page" })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Back to Platform' })).toHaveAttribute('href', `/teams/${TEAM.id}`);
  await expect(page.locator('header.app-header')).toBeVisible();
  await expect(page.locator('.nt-toolbar')).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText(CODE);
  expect(fake.received.filter(({ frame }) => frame.t === 'sub' || (frame.t === 'hello' && frame.subs.length > 0))).toEqual([]);
});

test('a member removed while the session is open is told they no longer have access', async ({ page, fake }) => {
  setup(fake);
  await openTable(page, `/teams/${TEAM.id}/poker/${CODE}`);
  await fake.waitForSubscription(ROOM);
  fake.kick(ROOM, 'access_revoked');

  await expect(page.getByRole('heading', { level: 1, name: 'You no longer have access' })).toBeVisible();
  await expect(page.locator('header.app-header')).toBeVisible();
});
