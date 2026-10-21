// Sign-in, sign-up and the emailed-link pages (§d, §e): magic links sign in only on a click (S18),
// verification needs a session of the right account (S4), and link tokens never stay in the URL.
import { problem } from '../../../web-fake-server/http.js';
import { expect, test } from '../_harness/fixtures.js';
import { USER, installApi, pageErrors, requests, useSession } from './_shell.js';

// The sign-in, sign-up and email-link forms share labels; only one is visible at a time.
const field = (page, label) => page.getByLabel(label, { exact: true }).filter({ visible: true });

const UNVERIFIED = { ...USER, user: { ...USER.user, emailVerified: false } };

test.beforeEach(({ fake }) => {
  installApi(fake);
});

test('signing in lands on the return path with the session in the header', async ({ page, fake }) => {
  const errors = pageErrors(page);
  const setMe = useSession(fake, null);
  fake.route('POST', '/api/auth/login', () => {
    setMe(USER);
    return { status: 200, body: USER };
  });
  await page.goto('/login?return=%2Fteams');

  await field(page, 'Email').fill('ada@example.com');
  await field(page, 'Password').fill('correct horse battery');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();

  await expect(page).toHaveURL(/\/teams$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Teams' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Account menu for Ada Lovelace' })).toBeVisible();
  expect(requests(fake, 'POST', '/api/auth/login').map((r) => r.json)).toEqual([{ email: 'ada@example.com', password: 'correct horse battery' }]);
  expect(errors).toEqual([]);
});

test('a wrong password shows one message and stays on the form', async ({ page, fake }) => {
  useSession(fake, null);
  fake.route('POST', '/api/auth/login', problem(401, 'invalid_credentials'));
  await page.goto('/login');

  await field(page, 'Email').fill('ada@example.com');
  await field(page, 'Password').fill('wrong');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();

  await expect(page.getByRole('alert')).toHaveText('Email or password is incorrect.');
  await expect(page).toHaveURL(/\/login$/);
});

test('an open-redirect return path falls back to the dashboard', async ({ page, fake }) => {
  const setMe = useSession(fake, null);
  fake.route('POST', '/api/auth/login', () => {
    setMe(USER);
    return { status: 200, body: USER };
  });
  await page.goto(`/login?return=${encodeURIComponent('//evil.example/x')}`);
  await expect(page.getByRole('link', { name: 'Continue with Google' })).toHaveAttribute('href', '/auth/oidc/google/start?return=%2Fdashboard');

  await field(page, 'Email').fill('ada@example.com');
  await field(page, 'Password').fill('correct horse battery');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();

  await expect(page).toHaveURL(/\/dashboard$/);
});

test('creating an account signs in with the same credentials', async ({ page, fake }) => {
  const setMe = useSession(fake, null);
  fake.route('POST', '/api/auth/signup', { status: 202 });
  fake.route('POST', '/api/auth/login', () => {
    setMe(UNVERIFIED);
    return { status: 200, body: UNVERIFIED };
  });
  await page.goto('/login');

  await page.getByRole('tab', { name: 'Create account' }).click();
  await field(page, 'Full name').fill('Ada Lovelace');
  await field(page, 'Email').fill('ada@example.com');
  await field(page, 'Password').fill('a long enough secret');
  await page.getByRole('button', { name: 'Create account' }).click();

  await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible();
  await expect(page.getByText('Account created. Check your inbox to verify your email.')).toBeVisible();
  expect(requests(fake, 'POST', '/api/auth/signup').map((r) => r.json))
    .toEqual([{ email: 'ada@example.com', password: 'a long enough secret', fullName: 'Ada Lovelace' }]);
});

test('sign-up field errors land on their fields', async ({ page, fake }) => {
  useSession(fake, null);
  fake.route('POST', '/api/auth/signup', problem(400, 'invalid', { fields: { password: 'too_common' } }));
  await page.goto('/login?tab=signup');

  await field(page, 'Full name').fill('Ada Lovelace');
  await field(page, 'Email').fill('ada@example.com');
  await field(page, 'Password').fill('password1234');
  await page.getByRole('button', { name: 'Create account' }).click();

  await expect(page.getByText('This password is too common. Choose another.')).toBeVisible();
  await expect(field(page, 'Password')).toHaveAttribute('aria-invalid', 'true');
});

test('a sign-in link and a reset link can be requested', async ({ page, fake }) => {
  useSession(fake, null);
  fake.route('POST', '/api/auth/magic/request', { status: 202 });
  fake.route('POST', '/api/auth/reset/request', { status: 202 });
  await page.goto('/login');

  await page.getByRole('button', { name: 'Email me a sign-in link' }).click();
  await field(page, 'Email').fill('ada@example.com');
  await page.getByRole('button', { name: 'Send sign-in link' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'a sign-in link is on its way' })).toBeVisible();

  await page.getByRole('button', { name: 'Back to sign in' }).click();
  await page.getByRole('button', { name: 'Forgot password?' }).click();
  await field(page, 'Email').fill('ada@example.com');
  await page.getByRole('button', { name: 'Send reset link' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'a reset link is on its way' })).toBeVisible();

  expect(requests(fake, 'POST', '/api/auth/magic/request').map((r) => r.json)).toEqual([{ email: 'ada@example.com' }]);
  expect(requests(fake, 'POST', '/api/auth/reset/request').map((r) => r.json)).toEqual([{ email: 'ada@example.com' }]);
});

test('a magic link signs in only after the click, as the masked address', async ({ page, fake }) => {
  const errors = pageErrors(page);
  const setMe = useSession(fake, null);
  fake.route('POST', '/api/auth/magic/consume', () => {
    setMe(USER);
    return { status: 200, body: USER };
  });
  await page.goto('/auth/magic#t=magic-token-1');

  const confirm = page.getByRole('button', { name: 'Sign in as a•••@example.com' });
  await expect(confirm).toBeVisible();
  await expect(page).toHaveURL(/\/auth\/magic$/);
  expect(requests(fake, 'POST', '/api/auth/magic/preview').map((r) => r.json)).toEqual([{ token: 'magic-token-1' }]);
  await page.waitForTimeout(300);
  expect(requests(fake, 'POST', '/api/auth/magic/consume')).toEqual([]);

  await confirm.click();

  await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible();
  expect(requests(fake, 'POST', '/api/auth/magic/consume').map((r) => r.json)).toEqual([{ token: 'magic-token-1' }]);
  expect(errors).toEqual([]);
});

test('a used magic link says so', async ({ page, fake }) => {
  useSession(fake, null);
  fake.route('POST', '/api/auth/magic/preview', problem(410, 'invalid_token'));
  await page.goto('/auth/magic#t=used');

  await expect(page.getByText('This link has expired or was already used.')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Request a new link' })).toBeVisible();
});

test('verifying while signed out asks to sign in first, then finishes after sign-in', async ({ page, fake }) => {
  const setMe = useSession(fake, null);
  fake.route('POST', '/api/auth/login', () => {
    setMe(UNVERIFIED);
    return { status: 200, body: UNVERIFIED };
  });
  fake.route('POST', '/api/auth/verify/consume', () => {
    setMe(USER);
    return { status: 204 };
  });
  fake.route('GET', '/api/me', () => ({ status: 200, body: USER }));
  await page.goto('/auth/verify#t=verify-token-1');

  await expect(page.getByRole('heading', { level: 1, name: 'Sign in to finish verifying' })).toBeVisible();
  await expect(page).toHaveURL(/\/auth\/verify$/);
  await page.waitForTimeout(300);
  expect(requests(fake, 'POST', '/api/auth/verify/consume')).toEqual([]);

  await page.getByRole('link', { name: 'Sign in' }).last().click();
  await field(page, 'Email').fill('ada@example.com');
  await field(page, 'Password').fill('correct horse battery');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();

  await expect(page.getByText('Your email is verified.')).toBeVisible();
  await expect(page).toHaveURL(/\/auth\/verify$/);
  expect(requests(fake, 'POST', '/api/auth/verify/consume').map((r) => r.json)).toEqual([{ token: 'verify-token-1' }]);
  expect(fake.requests.every((r) => !r.path.includes('verify-token-1') && !String(r.query).includes('verify-token-1'))).toBe(true);
});

test('verifying with another account’s session asks to sign in to the right one', async ({ page, fake }) => {
  useSession(fake, USER);
  fake.route('POST', '/api/auth/verify/consume', problem(403, 'forbidden'));
  await page.goto('/auth/verify#t=verify-token-2');

  await expect(page.getByRole('heading', { level: 1, name: 'Sign in to finish verifying' })).toBeVisible();
  expect(requests(fake, 'POST', '/api/auth/verify/consume').map((r) => r.json)).toEqual([{ token: 'verify-token-2' }]);
});

test('a reset link sets a new password and returns to sign-in', async ({ page, fake }) => {
  useSession(fake, null);
  fake.route('POST', '/api/auth/reset/consume', { status: 204 });
  await page.goto('/auth/reset#t=reset-token-1');
  await expect(page).toHaveURL(/\/auth\/reset$/);

  await field(page, 'New password').fill('a brand new secret');
  await field(page, 'Repeat the new password').fill('a different secret');
  await page.getByRole('button', { name: 'Set password' }).click();
  await expect(page.getByText('The passwords do not match.')).toBeVisible();
  expect(requests(fake, 'POST', '/api/auth/reset/consume')).toEqual([]);

  await field(page, 'Repeat the new password').fill('a brand new secret');
  await page.getByRole('button', { name: 'Set password' }).click();

  await expect(page.getByRole('heading', { level: 1, name: 'Sign in to Retroscope' })).toBeVisible();
  await expect(page.getByText('Password updated. Sign in with your new password.')).toBeVisible();
  expect(requests(fake, 'POST', '/api/auth/reset/consume').map((r) => r.json)).toEqual([{ token: 'reset-token-1', password: 'a brand new secret' }]);
});

test('provider errors from the OIDC callback are explained', async ({ page, fake }) => {
  useSession(fake, null);
  await page.goto('/login?error=account_exists');
  await expect(page.getByRole('alert')).toContainText('An account with this email exists.');
});
