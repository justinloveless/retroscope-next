// One smoke test per shell route in each theme: the screen paints its heading under the CSP, in the
// requested theme, with no uncaught errors (DESIGN §k U29).
import { expect, test } from '../_harness/fixtures.js';
import { ADMIN, installApi, mainLink, pageErrors, useSession } from './_shell.js';

const ROUTES = [
  { path: '/', heading: 'Retros and planning poker for your team' },
  { path: '/login', heading: 'Sign in to Retroscope', signedOut: true },
  { path: '/auth/magic#t=token-1', heading: 'Sign in with your email link', signedOut: true },
  { path: '/auth/reset#t=token-1', heading: 'Choose a new password', signedOut: true },
  { path: '/auth/verify#t=token-1', heading: 'Sign in to finish verifying', signedOut: true },
  { path: '/dashboard', heading: 'Dashboard' },
  { path: '/account', heading: 'Account' },
  { path: '/mentions', heading: 'Mentions' },
  { path: '/teams', heading: 'Teams' },
  { path: '/invite#t=token-1', heading: 'Team invite' },
  { path: '/admin/users', heading: 'Admin' },
];

const isDark = (rgb) => {
  const [r, g, b] = rgb.match(/\d+/g).map(Number);
  return 0.2126 * r + 0.7152 * g + 0.0722 * b < 128;
};

for (const theme of ['light', 'dark']) {
  test.describe(`${theme} theme`, () => {
    for (const route of ROUTES) {
      test(`${route.path} renders`, async ({ page, fake }) => {
        const errors = pageErrors(page);
        installApi(fake);
        useSession(fake, route.signedOut ? null : ADMIN, { theme });

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
  useSession(fake, ADMIN, { theme: 'light' });
  await page.goto('/dashboard');
  await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible();
  await page.evaluate(() => {
    window.sameDocument = true;
  });

  await (await mainLink(page, 'Teams')).click();

  await expect(page).toHaveURL(/\/teams$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Teams' })).toBeVisible();
  expect(await page.evaluate(() => window.sameDocument)).toBe(true);
  expect(fake.requests.some((r) => r.path === '/api/boot' && r.query.get('path') === '/teams')).toBe(true);

  await page.goBack();
  await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible();
  expect(errors).toEqual([]);
});

test('an unknown in-app path shows the not-found screen', async ({ page, fake }) => {
  installApi(fake);
  useSession(fake, ADMIN);
  await page.goto('/dashboard');
  await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible();

  await page.evaluate(() => {
    const a = document.createElement('a');
    a.href = '/no/such/page';
    a.dataset.nav = '';
    a.textContent = 'nowhere';
    document.getElementById('app').append(a);
    a.click();
  });

  await expect(page.getByRole('heading', { level: 1, name: 'Page not found' })).toBeVisible();
  await page.getByRole('link', { name: 'Go home' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Retros and planning poker for your team' })).toBeVisible();
});
