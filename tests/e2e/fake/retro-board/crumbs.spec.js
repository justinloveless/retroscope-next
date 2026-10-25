// A board leads back to its team (UX18 decisions 2 and 12): its header shows "<team> / Retros /" above the title,
// from the snapshot's team named from `me`; the app header shows that team with Retros current. A board that is
// missing or not the user's shows "You can't open this page" without naming it, and losing access while it is
// open says so.
import { json, problem } from '../../../web-fake-server/http.js';
import { expect, test } from '../_harness/fixtures.js';
import { memberMe } from '../_harness/me.js';
import { ALICE, CODE, ROOM, TEAM_ID, openBoard, snapshot } from './_board.js';

const crumbs = (page) => page.getByRole('main').getByRole('navigation', { name: 'Breadcrumb' });
const appHeader = (page) => page.locator('header.app-header');

test('the board’s header leads back to its team’s Retros, and the app header shows that team', async ({ page, fake }) => {
  await openBoard(page, fake);

  await expect(crumbs(page).getByRole('link')).toHaveText(['Platform', 'Retros']);
  await expect(crumbs(page).getByRole('link', { name: 'Platform' })).toHaveAttribute('href', `/teams/${TEAM_ID}`);
  await expect(crumbs(page).getByRole('link', { name: 'Retros' })).toHaveAttribute('href', `/teams/${TEAM_ID}?tab=retros`);
  await expect(page.getByRole('heading', { level: 1, name: 'Sprint 42 retro' })).toHaveAttribute('aria-current', 'page');
  const sections = appHeader(page).getByRole('navigation', { name: 'Platform sections' });
  await expect(sections.getByRole('link', { name: 'Retros' })).toHaveAttribute('aria-current', 'page');
  await expect(appHeader(page).getByText('Sprint 42 retro')).toHaveCount(0);

  await crumbs(page).getByRole('link', { name: 'Retros' }).click();
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}\\?tab=retros$`));
});

test('a rename shows in the board’s title and leaves the crumbs alone', async ({ page, fake }) => {
  await openBoard(page, fake);
  await fake.waitForSubscription(ROOM);
  fake.tx(ROOM, [{ k: 'retro.board.meta', d: { title: 'Sprint 42 retro (done)', archived: false } }]);
  await expect(page.getByRole('heading', { level: 1, name: 'Sprint 42 retro (done)' })).toBeVisible();
  await expect(crumbs(page).getByRole('link')).toHaveText(['Platform', 'Retros']);
});

test('a board that is missing or not the user’s shows the no-access screen, which names nothing', async ({ page, fake }) => {
  fake.me = ALICE;
  fake.boot = { build: 'fake', theme: 'light', me: memberMe(ALICE, [{ id: TEAM_ID, name: 'Platform', role: 'owner' }]) };
  fake.route('GET', '/api/retro/by-code/:code', problem(404, 'not_found'));
  await page.goto(`/retro/${CODE}`);

  await expect(page.getByRole('heading', { level: 1, name: "You can't open this page" })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Back to Platform' })).toHaveAttribute('href', `/teams/${TEAM_ID}`);
  await expect(page.getByRole('button', { name: 'Use a different account' })).toBeVisible();
  await expect(page.locator('body')).not.toContainText(CODE);
  await expect(page.locator('.retro-column')).toHaveCount(0);
  await expect(appHeader(page).getByRole('navigation', { name: 'Platform sections' }).locator('[aria-current]')).toHaveCount(0);
  expect(fake.received.filter(({ frame }) => frame.t === 'sub' && frame.r?.startsWith('board:'))).toEqual([]);
});

test('“Use a different account” signs out and comes back to the board after signing in', async ({ page, fake }) => {
  fake.me = ALICE;
  fake.boot = { build: 'fake', theme: 'light', me: memberMe(ALICE, [{ id: TEAM_ID, name: 'Platform', role: 'owner' }]) };
  fake.route('GET', '/api/retro/by-code/:code', problem(404, 'not_found'));
  fake.route('POST', '/api/auth/logout', () => {
    fake.boot = { build: 'fake', theme: 'light', me: null };
    return { status: 204 };
  });
  await page.goto(`/retro/${CODE}`);
  await page.getByRole('button', { name: 'Use a different account' }).click();

  await expect(page.getByRole('heading', { level: 1, name: 'Sign in to Retroscope' })).toBeVisible();
  expect(new URL(page.url()).searchParams.get('return')).toBe(`/retro/${CODE}`);
});

test('a member removed while the board is open is told they no longer have access', async ({ page, fake }) => {
  fake.route('GET', '/api/me', json(memberMe(ALICE, [])));
  await openBoard(page, fake, { snap: snapshot() });
  await fake.waitForSubscription(ROOM);
  fake.kick(ROOM, 'access_revoked');

  await expect(page.getByRole('heading', { level: 1, name: 'You no longer have access' })).toBeVisible();
  await expect(page.locator('.retro-column')).toHaveCount(0);
  // `me` is reloaded, so the screen no longer offers the team just left.
  await expect(page.getByRole('link', { name: 'Create or join a team' })).toBeVisible();
});
