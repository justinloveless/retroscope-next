// The persistent app header: brand, team switcher, mentions, theme and account menus on every route, the
// board title in its centre slot, a slide-out menu on narrow screens, and the account menu docked into
// the poker toolbar.
import { json } from '../../../web-fake-server/http.js';
import { EMPTY_OVERLAY, CODE as BOARD_CODE, ROOM as BOARD_ROOM, snapshot } from '../retro-board/_board.js';
import { expect, test } from '../_harness/fixtures.js';
import { ADMIN, TEAM_ID, USER, installApi, pageErrors, useSession } from './_shell.js';

const header = (page) => page.locator('header.app-header');
const narrow = (page) => page.viewportSize().width < 768;

test.beforeEach(({ fake }) => {
  installApi(fake);
});

test('every signed-in shell route shows the header with the brand, mentions, theme and account menu', async ({ page, fake }) => {
  const errors = pageErrors(page);
  useSession(fake, USER);
  for (const path of ['/dashboard', '/teams', '/account', '/mentions']) {
    await page.goto(path);
    const bar = header(page);
    await expect(bar).toBeVisible();
    await expect(bar.getByRole('link', { name: 'Retroscope home' })).toHaveAttribute('href', '/dashboard');
    await expect(bar.getByRole('link', { name: /^Mentions/ })).toBeVisible();
    await expect(bar.getByRole('button', { name: 'Theme' })).toBeVisible();
    await expect(bar.getByRole('button', { name: 'Account menu for Ada Lovelace' })).toBeVisible();
  }
  expect(errors).toEqual([]);
});

test('the header is sticky on wide screens and fixed on narrow ones, and never covers the page heading', async ({ page, fake }) => {
  useSession(fake, USER);
  await page.goto('/dashboard');
  const heading = page.getByRole('heading', { level: 1, name: 'Dashboard' });
  await expect(heading).toBeVisible();

  const position = await header(page).evaluate((el) => getComputedStyle(el).position);
  expect(position).toBe(narrow(page) ? 'fixed' : 'sticky');
  const bar = await header(page).boundingBox();
  const title = await heading.boundingBox();
  expect(title.y).toBeGreaterThanOrEqual(bar.y + bar.height);
});

test('the account menu offers account and sign out, and admin only to app admins', async ({ page, fake }) => {
  useSession(fake, USER);
  await page.goto('/dashboard');
  await header(page).getByRole('button', { name: 'Account menu for Ada Lovelace' }).click();
  await expect(page.getByRole('menuitem', { name: 'Account' })).toBeVisible();
  await expect(page.getByRole('menuitem', { name: 'Sign out' })).toBeVisible();
  await expect(page.getByRole('menuitem', { name: 'Admin' })).toHaveCount(0);
  await page.keyboard.press('Escape');

  useSession(fake, ADMIN);
  await page.goto('/dashboard');
  await header(page).getByRole('button', { name: 'Account menu for Ada Lovelace' }).click();
  await page.getByRole('menuitem', { name: 'Admin' }).click();
  await expect(page).toHaveURL(/\/admin\/users$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Admin' })).toBeVisible();
});

test('signing out from the account menu returns to the sign-in page', async ({ page, fake }) => {
  const setMe = useSession(fake, USER);
  fake.route('POST', '/api/auth/logout', () => {
    setMe(null);
    return { status: 204 };
  });
  await page.goto('/dashboard');
  await header(page).getByRole('button', { name: 'Account menu for Ada Lovelace' }).click();
  await page.getByRole('menuitem', { name: 'Sign out' }).click();

  await expect(page).toHaveURL(/\/login$/);
  await expect(header(page).getByRole('button', { name: /Account menu/ })).toHaveCount(0);
});

test('the team switcher lists the user’s teams and opens one', async ({ page, fake }) => {
  test.skip(narrow(page), 'below 768 px the teams are in the slide-out menu');
  useSession(fake, USER);
  await page.goto('/dashboard');
  await header(page).getByRole('button', { name: 'Teams', exact: true }).click();
  await expect(page.getByRole('menuitem', { name: 'All teams' })).toBeVisible();
  await page.getByRole('menuitem', { name: 'Platform' }).click();

  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}$`));
  await expect(header(page).getByRole('button', { name: 'Switch team: Platform' })).toBeVisible();
});

test('the slide-out menu stacks the links, teams, theme and account items on narrow screens', async ({ page, fake }) => {
  test.skip(!narrow(page), 'the slide-out menu is for screens below 768 px');
  useSession(fake, ADMIN);
  await page.goto('/dashboard');
  await expect(header(page).getByRole('navigation', { name: 'Main' })).toBeHidden();

  await header(page).getByRole('button', { name: 'Menu', exact: true }).click();
  const sheet = page.getByRole('dialog', { name: 'Menu' });
  await expect(sheet.getByRole('link', { name: 'Dashboard' })).toBeVisible();
  await expect(sheet.getByRole('link', { name: 'Admin' })).toBeVisible();
  await expect(sheet.getByRole('button', { name: 'Sign out' })).toBeVisible();
  await sheet.getByRole('button', { name: 'Dark' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

  await sheet.getByRole('link', { name: 'Platform' }).click();
  await expect(sheet).toBeHidden();
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}$`));
});

test('signed-out visitors get a sign-in link that returns them to the page', async ({ page, fake }) => {
  useSession(fake, null);
  await page.goto('/');
  const signIn = header(page).getByRole('link', { name: 'Sign in' });
  await expect(signIn).toHaveAttribute('href', '/login?return=%2F');
  await expect(header(page).getByRole('button', { name: /Account menu/ })).toHaveCount(0);

  await page.goto('/login');
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in to Retroscope' })).toBeVisible();
  await expect(header(page).getByRole('link', { name: 'Sign in' })).toHaveCount(0);
});

test('a board shows its title in the header’s centre slot', async ({ page, fake }) => {
  useSession(fake, USER);
  fake.boot = { build: 'fake', theme: 'light', me: USER, room: { r: BOARD_ROOM, e: 'aaaaaaaaaaaaaaaa', s: 0, d: snapshot(), o: EMPTY_OVERLAY } };
  fake.me = { pid: USER.principal.id, kind: 'user', name: USER.principal.name };
  fake.room(BOARD_ROOM, { snapshot: snapshot(), overlay: EMPTY_OVERLAY, epoch: 'aaaaaaaaaaaaaaaa', seq: 0 });
  await page.goto(`/retro/${BOARD_CODE}`);

  await expect(header(page).locator('.app-header-center')).toHaveText('Sprint 42 retro');
  await expect(header(page).getByRole('button', { name: 'Account menu for Ada Lovelace' })).toBeVisible();

  await header(page).getByRole('link', { name: 'Retroscope home' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible();
  await expect(header(page).locator('.app-header-center')).toHaveText('');
});

test('poker keeps its own toolbar and docks the theme and account menus into it', async ({ page, fake }) => {
  useSession(fake, USER);
  fake.me = { pid: USER.principal.id, kind: 'user', name: USER.principal.name };
  fake.route('GET', '/api/poker/by-code/:code', json({ id: '01900000-0000-7000-8000-0000000000c1' }));
  await page.goto('/poker/POKERCODE1');

  const toolbar = page.locator('.nt-toolbar');
  await expect(toolbar).toBeVisible();
  await expect(header(page)).toBeHidden();
  await expect(toolbar.getByRole('button', { name: 'Theme' })).toBeVisible();
  await toolbar.getByRole('button', { name: 'Account menu for Ada Lovelace' }).click();
  await page.getByRole('menuitem', { name: 'Account' }).click();

  await expect(page).toHaveURL(/\/account$/);
  await expect(header(page)).toBeVisible();
  await expect(page.locator('.nt-toolbar')).toHaveCount(0);
});
