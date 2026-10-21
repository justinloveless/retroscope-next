// Shared setup for specs against deploy/compose.test.yml: Traefik's self-signed https front, Mailpit, and a fresh
// client address per test so the server's per-IP limits (5 signups an hour) never couple tests together. The
// stack trusts X-Test-Client-Ip from its proxy the way production trusts CF-Connecting-IP behind Cloudflare.
import { randomInt, randomUUID } from 'node:crypto';
import { expect, test as base } from '@playwright/test';

export const BASE = process.env.RS_E2E_BASE_URL?.startsWith('https:')
  ? process.env.RS_E2E_BASE_URL
  : `https://localhost:${process.env.RS_TEST_HTTPS_PORT ?? 18443}`;
export const MAILPIT = `http://localhost:${process.env.RS_TEST_MAILPIT_PORT ?? 18025}`;
export const PASSWORD = 'correct horse battery staple';

export const clientIp = () => `10.${randomInt(256)}.${randomInt(256)}.${randomInt(1, 255)}`;

export const test = base.extend({
  baseURL: async ({}, use) => use(BASE),
  ignoreHTTPSErrors: async ({}, use) => use(true),
  extraHTTPHeaders: async ({}, use) => use({ 'X-Test-Client-Ip': clientIp() }),
  // Any CSP violation or uncaught page error fails the test (§j security rule).
  page: async ({ page }, use) => {
    const problems = [];
    await page.addInitScript(() => {
      document.addEventListener('securitypolicyviolation', (e) => {
        console.error(`CSP violation: ${e.violatedDirective} ${e.blockedURI}`);
      });
    });
    page.on('console', (message) => {
      if (message.type() === 'error' && message.text().startsWith('CSP violation')) problems.push(message.text());
    });
    page.on('pageerror', (error) => problems.push(`page error: ${error.message}`));
    await use(page);
    expect(problems).toEqual([]);
  },
});

export { expect };

const json = (data) => ({ data, headers: { 'X-RS': '1', Origin: BASE } });

/** Signs a new user up and in on the context behind `api` (a page's request shares its cookies). */
export async function signUp(api, name) {
  const email = `e2e-${randomUUID()}@example.test`;
  const signup = await api.post('/api/auth/signup', json({ email, password: PASSWORD, fullName: name }));
  expect(signup.status()).toBe(202);
  const login = await api.post('/api/auth/login', json({ email, password: PASSWORD }));
  expect(login.status()).toBe(200);
  return { email, name };
}

/** The token of the newest mail to `email` whose link path is `path` (e.g. /auth/verify), from Mailpit's API. */
export async function mailedToken(api, email, path) {
  const pattern = new RegExp(`${path}#t=([A-Za-z0-9_-]+)`);
  let token = null;
  await expect.poll(async () => {
    const list = await (await api.get(`${MAILPIT}/api/v1/search?query=${encodeURIComponent(`to:${email}`)}`)).json();
    for (const summary of list.messages ?? []) {
      const message = await (await api.get(`${MAILPIT}/api/v1/message/${summary.ID}`)).json();
      token = pattern.exec(`${message.Text ?? ''} ${message.HTML ?? ''}`)?.[1] ?? token;
    }
    return token;
  }, { timeout: 20_000 }).not.toBeNull();
  return token;
}

/** Signs up and verifies through the mailed link, so the account may create teams. */
export async function verifiedUser(api, name) {
  const user = await signUp(api, name);
  const token = await mailedToken(api, user.email, '/auth/verify');
  const verify = await api.post('/api/auth/verify/consume', json({ token }));
  expect(verify.ok()).toBe(true);
  return user;
}

export { json };
