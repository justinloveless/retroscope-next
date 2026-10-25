// No theme flash: the first paint, taken while the entry script is still held back, is already in the
// theme the app keeps after it starts (§g.8 pre-paint script).
import { expect, test } from '../_harness/fixtures.js';
import { ADMIN, installApi, meanLuminance, openAccount, readPng, requests, useSession } from './_shell.js';

// Playwright's WebKit screenshot injects an inline <style> into the page, which the CSP reports
// (asynchronously) as a style-src-elem violation. Those reports are the tool's, not the app's: in tests
// that took a screenshot they are dropped before the fixture checks for violations.
async function screenshot(page) {
  page.tookScreenshot = true;
  return readPng(await page.screenshot({ caret: 'initial' }));
}

test.afterEach(async ({ page, browserName }) => {
  if (page.tookScreenshot && browserName === 'webkit') {
    await page.waitForTimeout(100);
    const kept = page.cspViolations.filter((v) => !(v.directive === 'style-src-elem' && v.blocked === 'inline' && v.sample === ''));
    page.cspViolations.splice(0, page.cspViolations.length, ...kept);
  }
});

// The page as it paints before the app runs: the entry module is answered with an empty script, so only
// the shell, the stylesheets and the inline pre-paint script take part. Later loads run the app.
async function firstPaint(page, path) {
  const entry = /\/assets\/entry-[^/]*\.js$/;
  await page.route(entry, (route) => route.fulfill({ status: 200, contentType: 'text/javascript', body: '' }));
  await page.goto(path);
  expect(await page.locator('#app').evaluate((el) => el.childElementCount), 'the app did not run').toBe(0);
  const theme = await page.locator('html').getAttribute('data-theme');
  const shot = await screenshot(page);
  await page.unroute(entry);
  return { theme, luminance: meanLuminance(shot) };
}

for (const theme of ['dark', 'light']) {
  test(`the first paint is already ${theme} when the account prefers ${theme}`, async ({ page, fake }) => {
    installApi(fake);
    useSession(fake, { ...ADMIN, user: { ...ADMIN.user, theme } });

    const before = await firstPaint(page, '/account');
    expect(before.theme).toBe(theme);

    await page.goto('/account');
    await expect(page.getByRole('heading', { level: 1, name: 'Account' })).toBeVisible();
    const after = meanLuminance(await screenshot(page));
    await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
    if (theme === 'dark') {
      expect(before.luminance).toBeLessThan(0.25);
      expect(after).toBeLessThan(0.25);
    } else {
      expect(before.luminance).toBeGreaterThan(0.75);
      expect(after).toBeGreaterThan(0.75);
    }
  });
}

test('a signed-out visitor’s stored choice paints first when the boot JSON has no preference', async ({ page, fake }) => {
  installApi(fake);
  fake.boot = { build: 'fake', me: null };
  await page.addInitScript(() => localStorage.setItem('rs.theme', 'dark'));

  const before = await firstPaint(page, '/login');

  expect(before).toEqual({ theme: 'dark', luminance: expect.any(Number) });
  expect(before.luminance).toBeLessThan(0.25);
  await page.goto('/login');
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in to Retroscope' })).toBeVisible();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
});

test('the account menu’s theme choice switches the theme and saves it to the account', async ({ page, fake }) => {
  installApi(fake);
  useSession(fake, { ...ADMIN, user: { ...ADMIN.user, theme: 'light' } });
  fake.route('PATCH', '/api/me', (req) => ({ status: 200, body: { ...ADMIN, user: { ...ADMIN.user, theme: req.body.theme } } }));
  await page.goto('/account');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');

  const account = await openAccount(page);
  await account.item('Dark').click();

  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await expect.poll(() => requests(fake, 'PATCH', '/api/me').map((r) => r.json)).toEqual([{ theme: 'dark' }]);
  expect(await page.evaluate(() => localStorage.getItem('rs.theme'))).toBe('dark');
});
