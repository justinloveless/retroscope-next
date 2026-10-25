// One smoke test per shell route in each theme: the screen paints its heading under the CSP, in the
// requested theme, with no uncaught errors (DESIGN §k U29, UX18).
import { expect, test } from '../_harness/fixtures.js';
import { ADMIN, NEWCOMER, TEAM_ID, header, installApi, installTeams, pageErrors, sections, useSession } from './_shell.js';

const ROUTES = [
  { path: '/', heading: 'Planning poker and retros for your team', signedOut: true },
  { path: '/', heading: 'Poker' },
  { path: `/teams/${TEAM_ID}`, heading: 'Poker' },
  { path: `/teams/${TEAM_ID}?tab=retros`, heading: 'Retros' },
  { path: `/teams/${TEAM_ID}/settings`, heading: 'Team settings' },
  { path: '/login', heading: 'Sign in to Retroscope', signedOut: true },
  { path: '/auth/magic#t=token-1', heading: 'Sign in with your email link', signedOut: true },
  { path: '/auth/reset#t=token-1', heading: 'Choose a new password', signedOut: true },
  { path: '/auth/verify#t=token-1', heading: 'Sign in to finish verifying', signedOut: true },
  { path: '/account', heading: 'Account' },
  { path: '/mentions', heading: 'Mentions' },
  { path: '/teams', heading: 'Create or join a team' },
  { path: '/teams', heading: 'Welcome, Ada', me: NEWCOMER },
  { path: '/invite#t=token-1', heading: 'Join Platform' },
  { path: '/invite#t=token-1', heading: 'Join Platform', signedOut: true },
  { path: '/admin/users', heading: 'Admin' },
];

const isDark = (rgb) => {
  const [r, g, b] = rgb.match(/\d+/g).map(Number);
  return 0.2126 * r + 0.7152 * g + 0.0722 * b < 128;
};

for (const theme of ['light', 'dark']) {
  test.describe(`${theme} theme`, () => {
    for (const route of ROUTES) {
      test(`${route.path} renders ${route.signedOut ? 'signed out' : `for ${route.me ? 'a newcomer' : 'a member'}`}`, async ({ page, fake }) => {
        const errors = pageErrors(page);
        installApi(fake);
        installTeams(fake);
        useSession(fake, route.signedOut ? null : route.me ?? ADMIN, { theme });

        await page.goto(route.path);

        await expect(page.getByRole('heading', { level: 1, name: route.heading })).toBeVisible();
        await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
        const background = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
        expect(isDark(background)).toBe(theme === 'dark');
        expect(errors).toEqual([]);
      });
    }
  });
}

test('in-app navigation swaps screens without reloading and refreshes the boot JSON', async ({ page, fake }) => {
  const errors = pageErrors(page);
  installApi(fake);
  installTeams(fake);
  useSession(fake, ADMIN, { theme: 'light' });
  await page.goto('/account');
  await expect(page.getByRole('heading', { level: 1, name: 'Account' })).toBeVisible();
  await page.evaluate(() => {
    window.sameDocument = true;
  });

  await sections(page).getByRole('link', { name: 'Retros' }).click();

  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}\\?tab=retros$`));
  await expect(page.getByRole('main').getByRole('heading', { level: 1, name: 'Retros' })).toBeVisible();
  expect(await page.evaluate(() => window.sameDocument)).toBe(true);
  expect(fake.requests.some((r) => r.path === '/api/boot' && r.query.get('path') === `/teams/${TEAM_ID}?tab=retros`)).toBe(true);

  await page.goBack();
  await expect(page.getByRole('heading', { level: 1, name: 'Account' })).toBeVisible();
  expect(errors).toEqual([]);
});

test('an unknown in-app path shows the no-access screen, which leads back to the last team', async ({ page, fake }) => {
  installApi(fake);
  installTeams(fake);
  useSession(fake, ADMIN);
  await page.goto('/account');
  await expect(page.getByRole('heading', { level: 1, name: 'Account' })).toBeVisible();

  await page.evaluate(() => {
    const a = document.createElement('a');
    a.href = '/no/such/page';
    a.dataset.nav = '';
    a.textContent = 'nowhere';
    document.getElementById('app').append(a);
    a.click();
  });

  await expect(page.getByRole('heading', { level: 1, name: "You can't open this page" })).toBeVisible();
  await expect(header(page)).toBeVisible();
  await page.getByRole('link', { name: 'Back to Platform' }).click();
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}$`));
  await expect(page.getByRole('main').getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();
});
