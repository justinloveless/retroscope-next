// The app header is the team (UX18 decisions 9, 10 and 17): the logo leads to the team's home, the switcher
// changes team, the team's sections are links with the current one marked, and on the right sit the settings
// gear (owners and admins), the mentions bell and the account menu with the theme. Below 768 px the sections
// scroll in a second row and the account button opens the slide-out sheet. The poker screen docks the bell
// and the account menu into its own toolbar.
import { json } from '../../../web-fake-server/http.js';
import { EMPTY_OVERLAY, ROOM as BOARD_ROOM, CODE as BOARD_CODE, TEAM_ID as BOARD_TEAM, snapshot } from '../retro-board/_board.js';
import { expect, test } from '../_harness/fixtures.js';
import { ADMIN, APPS_ID, NEWCOMER, TEAM_ID, USER, header, installApi, installTeams, narrow, openAccount, pageErrors, requests, sections, useSession } from './_shell.js';

test.beforeEach(({ fake }) => {
  installApi(fake);
  installTeams(fake);
});

test('a page without a team shows the last team: its home, its switcher and its sections with none current', async ({ page, fake }) => {
  const errors = pageErrors(page);
  useSession(fake, USER);
  // The mentions page marks them seen, so its bell has no count.
  for (const [path, bell] of [['/account', 'Mentions, 2 unread'], ['/mentions', 'Mentions']]) {
    await page.goto(path);
    const bar = header(page);
    await expect(bar).toBeVisible();
    await expect(bar.getByRole('button', { name: 'Platform', exact: true })).toBeVisible();
    await expect(sections(page).getByRole('link')).toHaveText(['Poker', 'Retros', 'Action items3', 'Kudos', 'Members']);
    await expect(sections(page).locator('[aria-current]')).toHaveCount(0);
    await expect(sections(page).getByRole('link', { name: 'Retros' })).toHaveAttribute('href', `/teams/${TEAM_ID}?tab=retros`);
    await expect(bar.getByRole('link', { name: 'Team settings' })).toBeHidden();
    await expect(bar.getByRole('link', { name: bell, exact: true })).toBeVisible();
    await expect(bar.getByRole('button', { name: 'Account menu for Ada Lovelace' }).filter({ visible: true })).toHaveCount(1);
    await expect(bar.getByRole('button', { name: 'Theme' })).toHaveCount(0);
  }
  await expect(header(page).getByRole('link', { name: 'Mentions', exact: true })).toHaveAttribute('aria-current', 'page');
  expect(errors).toEqual([]);
});

test('on a team page the logo, the sections and the gear belong to that team, and a section click is a history entry', async ({ page, fake }) => {
  test.skip(narrow(page), 'below 768 px the logo and the gear give way to the switcher and the sheet');
  useSession(fake, USER);
  await page.goto(`/teams/${TEAM_ID}`);
  await expect(page.getByRole('main').getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();

  const bar = header(page);
  await expect(bar.getByRole('link', { name: 'Platform home' })).toHaveAttribute('href', `/teams/${TEAM_ID}`);
  await expect(sections(page).getByRole('link', { name: 'Poker' })).toHaveAttribute('aria-current', 'page');
  await expect(bar.getByRole('link', { name: 'Team settings' })).toHaveAttribute('href', `/teams/${TEAM_ID}/settings`);

  await sections(page).getByRole('link', { name: /^Action items/ }).click();
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}\\?tab=actions$`));
  await expect(page.getByRole('main').getByRole('heading', { level: 1, name: 'Action items' })).toBeVisible();
  await expect(sections(page).getByRole('link', { name: /^Action items/ })).toHaveAttribute('aria-current', 'page');
  await expect(sections(page).getByRole('link', { name: 'Poker' })).not.toHaveAttribute('aria-current');

  await page.goBack();
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}$`));
  await expect(page.getByRole('main').getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();

  await bar.getByRole('link', { name: 'Team settings' }).click();
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}/settings$`));
  await expect(bar.getByRole('link', { name: 'Team settings' })).toHaveAttribute('aria-current', 'page');
  await expect(sections(page).locator('[aria-current]')).toHaveCount(0);
});

test('the old Boards tab address opens Retros', async ({ page, fake }) => {
  useSession(fake, USER);
  await page.goto(`/teams/${TEAM_ID}?tab=boards`);
  await expect(page.getByRole('main').getByRole('heading', { level: 1, name: 'Retros' })).toBeVisible();
  await expect(sections(page).getByRole('link', { name: 'Retros' })).toHaveAttribute('aria-current', 'page');
});

test('a member sees no settings gear', async ({ page, fake }) => {
  test.skip(narrow(page), 'below 768 px team settings are in the sheet');
  useSession(fake, USER);
  await page.goto(`/teams/${APPS_ID}`);
  await expect(page.getByRole('main').getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();
  await expect(header(page).getByRole('button', { name: 'Apps', exact: true })).toBeVisible();
  await expect(header(page).getByRole('link', { name: 'Team settings' })).toBeHidden();
});

test('the team switcher lists the teams by name with the role, checks the current one, and offers Create a team', async ({ page, fake }) => {
  test.skip(narrow(page), 'below 768 px the teams are in the slide-out sheet');
  useSession(fake, USER);
  await page.goto(`/teams/${TEAM_ID}`);

  await header(page).getByRole('button', { name: 'Platform', exact: true }).click();
  const menu = page.getByRole('menu', { name: 'Teams' });
  const teams = menu.getByRole('group', { name: 'Your teams' }).getByRole('menuitemradio');
  await expect(teams).toHaveText(['AAppsMember', 'PPlatformOwner']);
  await expect(menu.getByRole('menuitemradio', { name: /Platform/ })).toHaveAttribute('aria-checked', 'true');
  await expect(menu.getByRole('menuitemradio', { name: /Apps/ })).toHaveAttribute('aria-checked', 'false');
  await expect(menu.getByRole('menuitem', { name: 'Create a team' })).toBeVisible();

  await menu.getByRole('menuitemradio', { name: /Apps/ }).click();
  await expect(page).toHaveURL(new RegExp(`/teams/${APPS_ID}$`));
  await expect(header(page).getByRole('button', { name: 'Apps', exact: true })).toBeVisible();
  await expect(sections(page, 'Apps').getByRole('link', { name: 'Poker' })).toHaveAttribute('aria-current', 'page');

  await header(page).getByRole('button', { name: 'Apps', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Create a team' }).click();
  await expect(page).toHaveURL(/\/teams$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Create or join a team' })).toBeVisible();
});

test('the account menu holds Account, the theme and Sign out, and Admin only for app admins', async ({ page, fake }) => {
  test.skip(narrow(page), 'below 768 px the account button opens the sheet');
  useSession(fake, { ...USER, user: { ...USER.user, theme: 'light' } });
  fake.route('PATCH', '/api/me', (req) => json({ ...USER, user: { ...USER.user, theme: req.body.theme } }));
  await page.goto('/account');

  let account = await openAccount(page);
  await expect(account.el.getByRole('menuitem')).toHaveText(['Account', 'Sign out']);
  await expect(account.el.getByRole('group', { name: 'Theme' }).getByRole('menuitemradio')).toHaveText(['Light', 'Dark', 'System']);
  await expect(account.item('Light')).toHaveAttribute('aria-checked', 'true');
  await account.item('Dark').click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await expect.poll(() => requests(fake, 'PATCH', '/api/me').map((r) => r.json)).toEqual([{ theme: 'dark' }]);

  useSession(fake, ADMIN);
  await page.goto('/account');
  account = await openAccount(page);
  await account.item('Admin').click();
  await expect(page).toHaveURL(/\/admin\/users$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Admin' })).toBeVisible();
});

test('signing out from the account menu returns to the sign-in page', async ({ page, fake }) => {
  const setMe = useSession(fake, USER);
  fake.route('POST', '/api/auth/logout', () => {
    setMe(null);
    return { status: 204 };
  });
  await page.goto('/account');
  const account = await openAccount(page);
  await account.item('Sign out').click();

  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in to Retroscope' })).toBeVisible();
  await expect(header(page).getByRole('button', { name: /Account menu/ }).filter({ visible: true })).toHaveCount(0);
  await expect(header(page).getByRole('navigation')).toBeHidden();
});

test('the create-or-join screen shows only the logo, the product name and the account menu', async ({ page, fake }) => {
  useSession(fake, NEWCOMER);
  await page.goto('/teams');
  await expect(page.getByRole('heading', { level: 1, name: 'Welcome, Ada' })).toBeVisible();
  const bar = header(page);
  await expect(bar.getByRole('link', { name: 'Retroscope home' })).toBeVisible();
  await expect(bar.getByText('Retroscope', { exact: true })).toBeVisible();
  await expect(bar.getByRole('navigation')).toBeHidden();
  await expect(bar.getByRole('link', { name: /^Mentions/ })).toBeHidden();
  await expect(bar.getByRole('button', { name: 'Account menu for Ada Lovelace' }).filter({ visible: true })).toHaveCount(1);

  useSession(fake, USER);
  await page.goto('/teams');
  await expect(page.getByRole('heading', { level: 1, name: 'Create or join a team' })).toBeVisible();
  await expect(header(page).getByRole('navigation')).toBeHidden();
  await expect(header(page).getByRole('button', { name: 'Platform', exact: true })).toBeHidden();
});

test('signed out, the header is the logo and the product name', async ({ page, fake }) => {
  useSession(fake, null);
  await page.goto('/');
  const bar = header(page);
  await expect(bar.getByRole('link', { name: 'Retroscope home' })).toHaveAttribute('href', '/');
  await expect(bar.getByText('Retroscope', { exact: true })).toBeVisible();
  await expect(bar.getByRole('navigation')).toBeHidden();
  await expect(bar.getByRole('button').filter({ visible: true })).toHaveCount(0);
});

test('the header is sticky on wide screens and fixed on narrow ones, and never covers the page heading', async ({ page, fake }) => {
  useSession(fake, USER);
  await page.goto(`/teams/${TEAM_ID}`);
  const heading = page.getByRole('main').getByRole('heading', { level: 1, name: 'Poker' });
  await expect(heading).toBeVisible();

  const position = await header(page).evaluate((el) => getComputedStyle(el).position);
  expect(position).toBe(narrow(page) ? 'fixed' : 'sticky');
  const bar = await header(page).boundingBox();
  const title = await heading.boundingBox();
  expect(title.y).toBeGreaterThanOrEqual(bar.y + bar.height);
});

test('on a phone the sections scroll in their own row and the sheet holds teams, settings, theme and account', async ({ page, fake }) => {
  test.skip(!narrow(page), 'the slide-out sheet is for screens below 768 px');
  useSession(fake, ADMIN);
  await page.goto(`/teams/${TEAM_ID}`);
  await expect(page.getByRole('main').getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();
  const bar = header(page);
  await expect(bar.getByRole('button', { name: 'Platform', exact: true })).toBeVisible();
  await expect(sections(page).getByRole('link', { name: 'Poker' })).toBeVisible();
  const row = await sections(page).boundingBox();
  const switcher = await bar.getByRole('button', { name: 'Platform', exact: true }).boundingBox();
  expect(row.y).toBeGreaterThanOrEqual(switcher.y + switcher.height - 1);
  expect(await sections(page).evaluate((el) => getComputedStyle(el).overflowX)).toBe('auto');

  const { el: sheet } = await openAccount(page);
  await expect(sheet.getByRole('region', { name: 'Your teams' }).getByRole('link')).toHaveText(['AAppsMember', 'PPlatformOwner', 'Create a team', 'Team settings']);
  await expect(sheet.getByRole('link', { name: /Platform/ })).toHaveAttribute('aria-current', 'true');
  await expect(sheet.getByRole('link', { name: 'Admin' })).toBeVisible();
  await expect(sheet.getByRole('link', { name: 'Dashboard' })).toHaveCount(0);
  await expect(sheet.getByRole('button', { name: 'Sign out' })).toBeVisible();
  await sheet.getByRole('button', { name: 'Dark' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

  await sheet.getByRole('link', { name: /Apps/ }).click();
  await expect(sheet).toBeHidden();
  await expect(page).toHaveURL(new RegExp(`/teams/${APPS_ID}$`));
  await expect(bar.getByRole('button', { name: 'Apps', exact: true })).toBeVisible();
});

test('a board keeps the app header on its team, with Retros current, and no longer shows its title there', async ({ page, fake }) => {
  const me = { ...USER, teams: [...USER.teams, { id: BOARD_TEAM, name: 'Boardroom', role: 'member', openActions: 1 }] };
  useSession(fake, me);
  const snap = snapshot();
  fake.room(BOARD_ROOM, { snapshot: snap, overlay: EMPTY_OVERLAY, epoch: 'aaaaaaaaaaaaaaaa', seq: 0 });
  fake.route('GET', '/api/retro/by-code/:code', json({ id: snap.boardId, access: 'granted' }));
  await page.goto(`/retro/${BOARD_CODE}`);
  await expect(page.locator('.retro-column')).toHaveCount(snap.columns.length);

  await expect(header(page)).toBeVisible();
  await expect(header(page).getByRole('button', { name: 'Boardroom', exact: true })).toBeVisible();
  await expect(sections(page, 'Boardroom').getByRole('link', { name: 'Retros' })).toHaveAttribute('aria-current', 'page');
  await expect(header(page).getByText('Sprint 42 retro')).toHaveCount(0);
});

test('poker keeps its own toolbar and docks the mentions bell and the account menu into it', async ({ page, fake }) => {
  test.skip(narrow(page), 'the docked account menu is the wide layout');
  useSession(fake, USER);
  const session = '01900000-0000-7000-8000-0000000000c1';
  const round = '01900000-0000-7000-8000-0000000000d1';
  fake.route('GET', '/api/poker/by-code/:code', json({ id: session, access: 'granted' }));
  fake.room(`poker:${session}`, {
    snapshot: {
      sessionId: session,
      code: 'POKERCODE1',
      presenceEnabled: true,
      followSpotlight: true,
      observers: [],
      rounds: [{ id: round, number: 1, version: 1, state: 'voting', active: true, pending: false, autoReveal: true }],
      votes: [],
      roster: [{ pid: USER.principal.id, kind: 'user', name: USER.principal.name }],
      presence: [],
      teamId: TEAM_ID,
      createdAt: Date.UTC(2025, 9, 14, 13, 5),
      currentRoundId: round,
    },
    overlay: { own: [], heldByThisTab: false },
  });
  await page.goto('/poker/POKERCODE1');

  const toolbar = page.locator('.nt-toolbar');
  await expect(toolbar).toBeVisible();
  await expect(header(page)).toBeHidden();
  await expect(toolbar.getByRole('link', { name: 'Mentions, 2 unread' })).toBeVisible();
  await toolbar.getByRole('button', { name: 'Account menu for Ada Lovelace' }).click();
  await page.getByRole('menuitem', { name: 'Account' }).click();

  await expect(page).toHaveURL(/\/account$/);
  await expect(header(page)).toBeVisible();
  await expect(page.locator('.nt-toolbar')).toHaveCount(0);
});
