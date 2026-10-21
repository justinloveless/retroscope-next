import { expect, test } from './fixtures.js';

test('serves the built shell: boot JSON, theme before paint, stylesheets and the entry under the CSP', async ({ page, fake }) => {
  fake.boot = (path) => ({ build: 'fake', theme: 'dark', me: null, path, hostile: '</script><script>alert(1)</script>' });
  const assets = [];
  page.on('response', (r) => {
    if (new URL(r.url()).pathname.startsWith('/assets/')) {
      assets.push({ path: new URL(r.url()).pathname, status: r.status() });
    }
  });
  const dialogs = [];
  page.on('dialog', (d) => {
    dialogs.push(d.message());
    d.dismiss();
  });

  await page.goto('/poker/FAKE123456?round=2');

  const boot = await page.evaluate(() => JSON.parse(document.getElementById('boot').textContent));
  expect(boot).toEqual({
    build: 'fake',
    theme: 'dark',
    me: null,
    path: '/poker/FAKE123456?round=2',
    hostile: '</script><script>alert(1)</script>',
  });
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await expect(page.locator('#app')).toHaveText('poker');
  const manifest = fake.dist.manifest;
  for (const file of [manifest.entry[0], ...manifest.css, ...manifest.routeCss['/poker/:code']]) {
    expect(assets).toContainEqual({ path: file, status: 200 });
  }
  expect(dialogs).toEqual([]);
});

for (const colorScheme of ['dark', 'light']) {
  test.describe(`with a ${colorScheme} system preference`, () => {
    test.use({ colorScheme });

    test('the theme follows it when boot has no preference', async ({ page, fake, browserName }) => {
      // Playwright 1.64's Firefox ignores the emulated dark scheme (matchMedia stays false).
      test.skip(browserName === 'firefox' && colorScheme === 'dark', 'Firefox does not emulate prefers-color-scheme: dark');
      fake.boot = { build: 'fake', me: null };
      await page.goto('/');
      await expect(page.locator('html')).toHaveAttribute('data-theme', colorScheme);
    });
  });
}

test('in-app boot JSON and unknown paths', async ({ page, fake }) => {
  fake.boot = (path) => ({ build: 'fake', theme: 'light', me: null, path });
  await page.goto('/dashboard');
  const boot = await page.evaluate(async () => (await fetch('/api/boot?path=/retro/ABC')).json());
  expect(boot.path).toBe('/retro/ABC');
  const missing = await page.request.get('/no/such/page');
  expect(missing.status()).toBe(404);
});
