// Auth end to end (§e): sign-up, verification, password sign-in, magic link, reset, Google and Entra through
// FakeOidc, and invites: an email invite refuses another account, and a link invite opened while signed out leads
// through an emailed sign-in link into the team (UX18 decision 13). Every sign-in lands on '/', which goes on to the
// user's team, or to the create-or-join screen for someone in none (decisions 7, 8). Mail goes through SMTP into
// Mailpit.
import { randomUUID } from 'node:crypto';
import {
  ENTRA_TENANT, PASSWORD, createTeam, expect, inviteLink, json, mailedToken, routeOidc, seedUsers, sql, test,
} from './_real.js';

const field = (page, label) => page.getByLabel(label, { exact: true }).filter({ visible: true });

/** Where a signed-in user in no team lands: /teams, the create-or-join screen. */
async function noTeam(page) {
  await expect(page).toHaveURL(/\/teams$/);
  await expect(page.getByRole('heading', { level: 1, name: /^Welcome, / })).toBeVisible();
}

/** A team's home: its Poker section, marked as the current one among the header's sections of that team. */
async function teamHome(page, team) {
  await expect(page).toHaveURL(new RegExp(`/teams/${team.id}$`));
  await expect(page.getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();
  await expect(page.getByRole('navigation', { name: `${team.name} sections` }).getByRole('link', { name: 'Poker' }))
    .toHaveAttribute('aria-current', 'page');
}

async function signInWithForm(page, email, password) {
  await page.goto('/login');
  await field(page, 'Email').fill(email);
  await field(page, 'Password').fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
}

test('sign up, verify through the mailed link, then sign in again with the password', async ({ open }) => {
  const email = `e2e-${randomUUID()}@example.test`;
  const { page } = await open();
  await page.goto('/login?tab=signup');
  await field(page, 'Full name').fill('Ada Signup');
  await field(page, 'Email').fill(email);
  await field(page, 'Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Create account' }).click();
  await noTeam(page);
  await expect(page.getByText('Account created. Check your inbox to verify your email.')).toBeVisible();

  const token = await mailedToken(page.request, email, '/auth/verify');
  await page.goto(`/auth/verify#t=${token}`);
  await expect(page.getByText('Your email is verified.')).toBeVisible();
  await expect(page).toHaveURL(/\/auth\/verify$/);
  expect(await sql(`select email_verified_at is not null from users where email = '${email}'`)).toEqual([['t']]);

  const other = await open();
  await signInWithForm(other.page, email, 'not the password at all');
  await expect(other.page.getByRole('alert')).toHaveText('Email or password is incorrect.');
  await field(other.page, 'Password').fill(PASSWORD);
  await other.page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await noTeam(other.page);
  await expect(other.page.getByText('Account created.')).toHaveCount(0);
});

test('a verification link opened while signed out waits for the right sign-in', async ({ open }) => {
  const email = `e2e-${randomUUID()}@example.test`;
  const { page } = await open();
  const signup = await page.request.post('/api/auth/signup', json({ email, password: PASSWORD, fullName: 'Vera Late' }));
  expect(signup.status()).toBe(202);
  const token = await mailedToken(page.request, email, '/auth/verify');

  await page.goto(`/auth/verify#t=${token}`);
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in to finish verifying' })).toBeVisible();
  await page.getByRole('link', { name: 'Sign in' }).last().click();
  await field(page, 'Email').fill(email);
  await field(page, 'Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByText('Your email is verified.')).toBeVisible();
  expect(await sql(`select email_verified_at is not null from users where email = '${email}'`)).toEqual([['t']]);
});

test('a magic link signs in only after the click, then lands on the team home', async ({ open }) => {
  const [user] = await seedUsers(['Maggie Link']);
  const team = await createTeam((await open(user)).page.request);
  const { page } = await open();
  await page.goto('/login');
  await page.getByRole('button', { name: 'Email me a sign-in link' }).click();
  await field(page, 'Email').fill(user.email);
  await page.getByRole('button', { name: 'Send sign-in link' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'a sign-in link is on its way' })).toBeVisible();

  const token = await mailedToken(page.request, user.email, '/auth/magic');
  await page.goto(`/auth/magic#t=${token}`);
  const confirm = page.getByRole('button', { name: /^Sign in as / });
  await expect(confirm).toBeVisible();
  await expect(page).toHaveURL(/\/auth\/magic$/);
  expect((await page.request.get('/api/me')).status()).toBe(401);

  await confirm.click();
  await teamHome(page, team);
  expect((await (await page.request.get('/api/me')).json()).principal.id).toBe(user.id);

  const again = await open();
  await again.page.goto(`/auth/magic#t=${token}`);
  await expect(again.page.getByText('This link has expired or was already used.')).toBeVisible();
});

test('a reset link sets a new password; the old one stops working', async ({ open }) => {
  const [user] = await seedUsers(['Reese Reset']);
  const { page } = await open();
  await page.goto('/login');
  await page.getByRole('button', { name: 'Forgot password?' }).click();
  await field(page, 'Email').fill(user.email);
  await page.getByRole('button', { name: 'Send reset link' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'a reset link is on its way' })).toBeVisible();

  const token = await mailedToken(page.request, user.email, '/auth/reset');
  await page.goto(`/auth/reset#t=${token}`);
  const fresh = 'a brand new secret phrase';
  await field(page, 'New password').fill(fresh);
  await field(page, 'Repeat the new password').fill(fresh);
  await page.getByRole('button', { name: 'Set password' }).click();
  await expect(page.getByText('Password updated. Sign in with your new password.')).toBeVisible();

  await field(page, 'Email').fill(user.email);
  await field(page, 'Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('alert')).toHaveText('Email or password is incorrect.');
  await field(page, 'Password').fill(fresh);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await noTeam(page);
});

test('Google sign-in creates a verified account and signs in again by subject', async ({ open }) => {
  const sub = `g-${randomUUID()}`;
  const email = `e2e-${randomUUID()}@gmail.example`;
  const claims = { sub, email, email_verified: true, name: 'Gina Google' };
  const first = await open();
  await routeOidc(first.context, claims);
  await first.page.goto('/login?return=%2Fteams');
  await first.page.getByRole('link', { name: 'Continue with Google' }).click();
  await noTeam(first.page);
  expect(await sql(`select u.email_verified_at is not null, u.full_name from users u
    join external_identities i on i.user_id = u.id where i.provider = 'google' and i.subject = '${sub}'`))
    .toEqual([['t', 'Gina Google']]);

  const second = await open();
  await routeOidc(second.context, { ...claims, email: `changed-${email}` });
  await second.page.goto('/login');
  await second.page.getByRole('link', { name: 'Continue with Google' }).click();
  await noTeam(second.page);
  const [me1, me2] = await Promise.all([first, second].map(async ({ page }) => (await (await page.request.get('/api/me')).json()).principal.id));
  expect(me2).toBe(me1);
});

test('Entra sign-in trusts a tenant domain; an untrusted email never signs into an existing account', async ({ open }) => {
  const trusted = await open();
  const oid = randomUUID();
  await routeOidc(trusted.context, { tid: ENTRA_TENANT, oid, email: `e2e-${randomUUID()}@contoso.test`, name: 'Ena Entra' });
  await trusted.page.goto('/login');
  await trusted.page.getByRole('link', { name: 'Continue with Microsoft' }).click();
  await noTeam(trusted.page);
  expect(await sql(`select u.email_verified_at is not null from users u join external_identities i on i.user_id = u.id
    where i.provider = 'entra' and i.subject = '${ENTRA_TENANT}:${oid}'`)).toEqual([['t']]);

  const [victim] = await seedUsers(['Vic Existing']);
  const attacker = await open();
  await routeOidc(attacker.context, { tid: ENTRA_TENANT, oid: randomUUID(), email: victim.email, name: 'Mallory' });
  await attacker.page.goto('/login');
  await attacker.page.getByRole('link', { name: 'Continue with Microsoft' }).click();
  await expect(attacker.page).toHaveURL(/\/login\?error=account_exists/);
  await expect(attacker.page.getByRole('alert')).toContainText('An account with this email exists.');
  expect((await attacker.page.request.get('/api/me')).status()).toBe(401);
});

test('an email invite refuses another account and admits the invited one', async ({ open }) => {
  const [owner, invited, stranger] = await seedUsers(['Olive Owner', 'Ivy Invited', 'Stan Stranger']);
  const admin = await open(owner);
  const team = await createTeam(admin.page.request);
  await admin.page.goto(`/teams/${team.id}?tab=members`);
  await admin.page.getByRole('button', { name: 'Invite by email' }).click();
  const dialog = admin.page.getByRole('dialog', { name: 'Invite by email' });
  await dialog.getByLabel('Email').fill(invited.email);
  await dialog.getByRole('button', { name: 'Send invite' }).click();
  await expect(admin.page.locator('.team-invites')).toContainText('@example.test');

  const token = await mailedToken(admin.page.request, invited.email, '/invite');
  const wrong = await open(stranger);
  await wrong.page.goto(`/invite#t=${token}`);
  await expect(wrong.page.getByRole('heading', { level: 1, name: `Join ${team.name}` })).toBeVisible();
  await expect(wrong.page).toHaveURL(/\/invite$/);
  await wrong.page.getByRole('button', { name: 'Accept invite' }).click();
  await expect(wrong.page.getByRole('alert')).toHaveText(/^This invite was sent to a different email address\./);

  const right = await open(invited);
  await right.page.goto(`/invite#t=${token}`);
  await right.page.getByRole('button', { name: 'Accept invite' }).click();
  await teamHome(right.page, team);
  expect(await sql(`select role from team_members where team_id = '${team.id}' and user_id = '${invited.id}'`)).toEqual([['member']]);
  expect(await sql(`select count(*) from team_members where team_id = '${team.id}' and user_id = '${stranger.id}'`)).toEqual([['0']]);
});

test('a link invite opened while signed out leads through an emailed sign-in link into the team', async ({ open }) => {
  const [owner] = await seedUsers(['Lena Link']);
  const admin = await open(owner);
  const team = await createTeam(admin.page.request);
  const token = await inviteLink(admin.page.request, team.id);
  // Nobody has an account at this address yet: the emailed link creates one, already verified.
  const email = `e2e-${randomUUID()}@example.test`;

  const { page } = await open();
  await page.goto(`/invite#t=${token}`);
  await expect(page).toHaveURL(/\/invite$/);
  await expect(page.getByRole('heading', { level: 1, name: `Join ${team.name}` })).toBeVisible();
  await field(page, 'Email').fill(email);
  await page.getByRole('button', { name: 'Email me a sign-in link' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'a sign-in link is on its way' })).toBeVisible();

  const magic = await mailedToken(page.request, email, '/auth/magic');
  await page.goto(`/auth/magic#t=${magic}`);
  await page.getByRole('button', { name: /^Sign in as / }).click();
  // '/' resumes the pending invite before anything else.
  await expect(page.getByRole('heading', { level: 1, name: `Join ${team.name}` })).toBeVisible();
  await page.getByRole('button', { name: 'Accept invite' }).click();
  await teamHome(page, team);
  expect(await sql(`select u.email_verified_at is not null, m.role from users u join team_members m on m.user_id = u.id
    where u.email = '${email}' and m.team_id = '${team.id}'`)).toEqual([['t', 'member']]);

  // Accepting cleared the pending invite: '/' now goes straight to the team.
  await page.goto('/');
  await teamHome(page, team);
});
