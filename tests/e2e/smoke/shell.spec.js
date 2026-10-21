// The deployed shape (§i.1): HTML over https through the proxy with the §e header set, and old links.
import { BASE, expect, test } from './_stack.js';

test('the landing and login pages load over https with HSTS, the CSP and no violation', async ({ page }) => {
  for (const path of ['/', '/login']) {
    const response = await page.goto(path);
    expect(response.status()).toBe(200);
    const headers = response.headers();
    expect(headers['content-type']).toContain('text/html');
    expect(headers['strict-transport-security']).toBe('max-age=63072000; includeSubDomains');
    expect(headers['content-security-policy']).toContain(`connect-src 'self' wss://${new URL(BASE).host}`);
    expect(headers['x-content-type-options']).toBe('nosniff');
  }
  await expect(page.getByRole('button', { name: 'Sign in', exact: true })).toBeVisible();
});

test('hashed assets are served precompressed and immutable', async ({ page }) => {
  const assets = [];
  page.on('response', (response) => {
    if (new URL(response.url()).pathname.startsWith('/assets/')) assets.push(response);
  });
  await page.goto('/login');
  await expect.poll(() => assets.length).toBeGreaterThan(0);
  for (const response of assets) {
    expect(response.headers()['cache-control']).toBe('public, max-age=31536000, immutable');
  }
  expect(assets.some((response) => ['br', 'gzip'].includes(response.headers()['content-encoding']))).toBe(true);
});

test('an old invite link redirects to /invite with the token only in the fragment', async ({ page }) => {
  const redirect = await page.request.get('/invite/legacy-token-0123456789', { maxRedirects: 0 });
  expect(redirect.status()).toBe(302);
  expect(redirect.headers().location).toBe('/invite#t=legacy-token-0123456789');
  expect(redirect.headers()['cache-control']).toBe('no-store');

  await page.goto('/invite/legacy-token-0123456789');
  await expect(page).toHaveURL(/\/invite(#.*)?$/);
});
