// What each shell screen does beyond painting (UX18): the sign-in gate and the way back, '/' finding the user's
// team, create-or-join, invites and the pending invite, account, mentions, admin, and the early socket on room
// routes. There are no guests, quick sessions, join-by-code forms, Dashboard or team list any more.
import { json, problem } from '../../../web-fake-server/http.js';
import { expect, test } from '../_harness/fixtures.js';
import { CODE as BOARD_CODE, EMPTY_OVERLAY, ROOM as BOARD_ROOM, snapshot } from '../retro-board/_board.js';
import {
  ADMIN, APPS_ID, NEWCOMER, TEAM_ID, USER, header, installApi, installTeams, openAccount, pageErrors, requests, sections, useSession,
} from './_shell.js';

// The sign-in, sign-up and email-link forms share labels; only one is visible at a time.
const field = (page, label) => page.getByLabel(label, { exact: true }).filter({ visible: true });
const pendingInvite = (page) => page.evaluate(() => JSON.parse(localStorage.getItem('rs.invite') ?? 'null'));

test.beforeEach(({ fake }) => {
  installApi(fake);
  installTeams(fake);
});

test.describe('the sign-in gate', () => {
  test('a signed-out visitor may open only /, /login, /auth/* and /invite', async ({ page, fake }) => {
    useSession(fake, null);
    for (const path of ['/teams', '/account', '/mentions', '/admin/users', `/teams/${TEAM_ID}?tab=kudos`, `/teams/${TEAM_ID}/settings`, `/retro/${BOARD_CODE}`, '/poker/POKERCODE1']) {
      await page.goto(path);
      await expect(page.getByRole('heading', { level: 1, name: 'Sign in to Retroscope' })).toBeVisible();
      expect(new URL(page.url()).pathname).toBe('/login');
      expect(new URL(page.url()).searchParams.get('return')).toBe(path);
    }
    for (const [path, heading] of [['/', 'Planning poker and retros for your team'], ['/login', 'Sign in to Retroscope'], ['/invite#t=invite-token-0', 'Join Platform']]) {
      await page.goto(path);
      await expect(page.getByRole('heading', { level: 1, name: heading })).toBeVisible();
      expect(new URL(page.url()).pathname).toBe(path.split('#')[0]);
    }
    expect(fake.connections).toHaveLength(0);
    expect(fake.requests.some((r) => r.path.startsWith('/api/teams/') || r.path.includes('/by-code/'))).toBe(false);
  });

  test('signing in from the gate comes back to the page that was asked for', async ({ page, fake }) => {
    const errors = pageErrors(page);
    const setMe = useSession(fake, null);
    fake.route('POST', '/api/auth/login', () => {
      setMe(USER);
      return json(USER);
    });
    await page.goto(`/teams/${TEAM_ID}?tab=retros`);
    await expect(page).toHaveURL(/\/login\?return=/);

    await field(page, 'Email').fill('ada@example.com');
    await field(page, 'Password').fill('correct horse battery');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();

    await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}\\?tab=retros$`));
    await expect(page.getByRole('main').getByRole('heading', { level: 1, name: 'Retros' })).toBeVisible();
    await expect(sections(page).getByRole('link', { name: 'Retros' })).toHaveAttribute('aria-current', 'page');
    expect(errors).toEqual([]);
  });

  test('after signing out, going back to a member page asks to sign in first', async ({ page, fake }) => {
    const setMe = useSession(fake, USER);
    fake.route('POST', '/api/auth/logout', () => {
      setMe(null);
      return { status: 204 };
    });
    await page.goto('/account');
    await (await openAccount(page)).item('Sign out').click();
    await expect(page).toHaveURL(/\/login$/);

    await page.goBack();
    await expect(page).toHaveURL(/\/login\?return=%2Faccount$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Sign in to Retroscope' })).toBeVisible();
  });
});

test.describe("'/'", () => {
  test('opens the team used last, even when another comes first by name', async ({ page, fake }) => {
    useSession(fake, USER);
    await page.goto('/');
    await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}$`));
    await expect(page.getByRole('main').getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();
    await expect(header(page).getByRole('button', { name: 'Platform', exact: true })).toBeVisible();
  });

  test('opens the first team by name when there is no last team, or it is one the user left', async ({ page, fake }) => {
    for (const lastTeamId of [null, '01900000-0000-7000-8000-0000000000ff']) {
      useSession(fake, { ...USER, lastTeamId });
      await page.goto('/');
      await expect(page).toHaveURL(new RegExp(`/teams/${APPS_ID}$`));
    }
  });

  test('signed out it is the welcome page, and signing in there opens the team used last', async ({ page, fake }) => {
    const setMe = useSession(fake, null);
    fake.route('POST', '/api/auth/login', () => {
      setMe(USER);
      return json(USER);
    });
    await page.goto('/');
    await expect(page.getByRole('heading', { level: 1, name: 'Planning poker and retros for your team' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Continue with Google' })).toHaveAttribute('href', '/auth/oidc/google/start?return=%2F');
    await expect(page.getByRole('button', { name: 'Email me a sign-in link' })).toBeVisible();

    await field(page, 'Email').fill('ada@example.com');
    await field(page, 'Password').fill('correct horse battery');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();

    await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}$`));
    await expect(page.getByRole('main').getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();
  });

  test('opens create-or-join for a user in no team', async ({ page, fake }) => {
    useSession(fake, NEWCOMER);
    await page.goto('/');
    await expect(page).toHaveURL(/\/teams$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Welcome, Ada' })).toBeVisible();
  });

  test('the logo leads home in-app, through the same rule', async ({ page, fake }) => {
    useSession(fake, NEWCOMER);
    await page.goto('/account');
    await header(page).getByRole('link', { name: 'Retroscope home' }).click();
    await expect(page).toHaveURL(/\/teams$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Welcome, Ada' })).toBeVisible();
  });

  test("reached in-app, a pending invite wins over the team: the verify page's Continue reopens it", async ({ page, fake }) => {
    useSession(fake, USER);
    fake.route('POST', '/api/auth/verify/consume', { status: 204 });
    await page.goto('/invite#t=invite-token-9');
    await expect(page.getByRole('heading', { level: 1, name: 'Join Platform' })).toBeVisible();
    // The verification link from the email, opened in the same browser: its Continue goes to '/' in-app.
    await page.goto('/auth/verify#t=verify-token-9');
    await expect(page.getByText('Your email is verified.')).toBeVisible();
    await page.getByRole('link', { name: 'Continue' }).click();
    await expect(page).toHaveURL(/\/invite$/);
    await expect(page.getByRole('button', { name: 'Accept invite' })).toBeVisible();

    await page.getByRole('button', { name: 'Not now' }).click();
    await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}$`));
    expect(await pendingInvite(page)).toBeNull();
  });

  test('a full page load by a member goes to the team: the server cannot see the waiting invite', async ({ page, fake }) => {
    useSession(fake, USER);
    await page.goto('/invite#t=invite-token-9');
    await expect(page.getByRole('heading', { level: 1, name: 'Join Platform' })).toBeVisible();
    await page.goto('/');
    await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}$`));
    await expect(page.getByRole('main').getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();
    expect((await pendingInvite(page)).t).toBe('invite-token-9');
  });

  test('someone in no team who reloads it lands on the waiting invite, through create-or-join', async ({ page, fake }) => {
    useSession(fake, NEWCOMER);
    await page.goto('/invite#t=invite-token-9');
    await expect(page.getByRole('heading', { level: 1, name: 'Join Platform' })).toBeVisible();
    await page.goto('/');
    await expect(page).toHaveURL(/\/invite$/);
    await expect(page.getByRole('button', { name: 'Accept invite' })).toBeVisible();

    await page.getByRole('button', { name: 'Not now' }).click();
    await expect(page).toHaveURL(/\/teams$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Welcome, Ada' })).toBeVisible();
    expect(await pendingInvite(page)).toBeNull();
  });

  test('the old /dashboard address goes home', async ({ page, fake }) => {
    useSession(fake, USER);
    await page.goto('/dashboard');
    await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}$`));
    await expect(page.getByRole('main').getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();
  });
});

test.describe('create or join a team', () => {
  test('a verified user creates a team, and the header knows it at once', async ({ page, fake }) => {
    const created = '01900000-0000-7000-8000-0000000000a9';
    const verified = { ...NEWCOMER, user: { ...NEWCOMER.user, emailVerified: true } };
    const setMe = useSession(fake, verified);
    fake.route('POST', '/api/teams', (req) => {
      setMe({ ...verified, teams: [{ id: created, name: req.body.name, role: 'owner', openActions: 0 }] });
      return json({ id: created }, 201);
    });
    fake.route('GET', `/api/teams/${created}`, json({
      id: created, name: 'Data', description: null, role: 'owner', pointDescriptions: {}, maxEndorsements: 3, jira: { configured: false }, counts: { boards: 0, members: 1 },
    }));
    fake.route('GET', `/api/teams/${created}/poker-sessions`, json({ items: [], nextCursor: null }));
    fake.room(`team:${created}`, {
      snapshot: {
        teamId: created, name: 'Data', members: [{ pid: USER.principal.id, name: USER.principal.name, role: 'owner' }], actionItems: [], endorsementTypes: [], pointDescriptions: {}, maxEndorsements: 3,
      },
    });
    await page.goto('/teams');

    const create = page.getByRole('region', { name: 'Create a team' });
    await expect(create.getByText('Verify your email first')).toBeHidden();
    await create.getByLabel('Team name').fill('Data');
    await create.getByRole('button', { name: 'Create team' }).click();

    await expect(page).toHaveURL(new RegExp(`/teams/${created}$`));
    await expect(page.getByRole('main').getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();
    await expect(header(page).getByRole('button', { name: 'Data', exact: true })).toBeVisible();
    expect(requests(fake, 'POST', '/api/teams').map((r) => r.json)).toEqual([{ name: 'Data', description: null }]);
  });

  test('an unverified user cannot create one yet, and can have the verification link sent again', async ({ page, fake }) => {
    useSession(fake, NEWCOMER);
    fake.route('POST', '/api/auth/verify/request', { status: 202 });
    await page.goto('/teams');

    const create = page.getByRole('region', { name: 'Create a team' });
    await expect(create.getByText('Verify your email first')).toBeVisible();
    await expect(create.getByRole('button', { name: 'Create team' })).toBeDisabled();
    await create.getByRole('button', { name: 'Send the link again' }).click();
    await expect(create.getByRole('status')).toHaveText('If your address still needs verifying, a new link is on its way.');
    expect(requests(fake, 'POST', '/api/auth/verify/request')).toHaveLength(1);
    expect(requests(fake, 'POST', '/api/teams')).toEqual([]);
  });

  test('a resend refused for its rate says so', async ({ page, fake }) => {
    useSession(fake, NEWCOMER);
    fake.route('POST', '/api/auth/verify/request', problem(429, 'rate_limited'));
    await page.goto('/teams');
    await page.getByRole('button', { name: 'Send the link again' }).click();
    await expect(page.getByRole('region', { name: 'Create a team' }).getByRole('status'))
      .toHaveText('You asked for a link recently. Check your inbox, or try again later.');
  });

  test('verifying in another tab unlocks creating when the user comes back', async ({ page, fake }) => {
    const setMe = useSession(fake, NEWCOMER);
    await page.goto('/teams');
    const button = page.getByRole('region', { name: 'Create a team' }).getByRole('button', { name: 'Create team' });
    await expect(button).toBeDisabled();
    setMe({ ...NEWCOMER, user: { ...NEWCOMER.user, emailVerified: true } });
    await page.evaluate(() => window.dispatchEvent(new Event('focus')));
    await expect(button).toBeEnabled();
  });

  for (const pasted of ['https://retroscope.example/invite#t=pasted-token-1', 'https://retroscope.example/invite/pasted-token-1', ' pasted-token-1 ']) {
    test(`a pasted invite opens on /invite: ${pasted.trim()}`, async ({ page, fake }) => {
      useSession(fake, NEWCOMER);
      await page.goto('/teams');
      const join = page.getByRole('region', { name: 'Join a team' });
      await expect(join.getByText('Teams are invite-only.', { exact: false })).toBeVisible();
      await join.getByLabel('Already have the link? Paste it').fill(pasted);
      await join.getByRole('button', { name: 'Open invite' }).click();

      await expect(page).toHaveURL(/\/invite$/);
      await expect(page.getByRole('heading', { level: 1, name: 'Join Platform' })).toBeVisible();
      expect(requests(fake, 'POST', '/api/invites/preview').map((r) => r.json)).toEqual([{ token: 'pasted-token-1' }]);
      expect((await pendingInvite(page)).t).toBe('pasted-token-1');
    });
  }

  test('a paste that holds no invite is refused on the spot', async ({ page, fake }) => {
    useSession(fake, NEWCOMER);
    await page.goto('/teams');
    const join = page.getByRole('region', { name: 'Join a team' });
    await join.getByLabel('Already have the link? Paste it').fill('https://elsewhere.example/teams/42');
    await join.getByRole('button', { name: 'Open invite' }).click();
    await expect(join.getByText('Paste the whole invite link, or the code at its end.')).toBeVisible();
    await expect(page).toHaveURL(/\/teams$/);
    expect(requests(fake, 'POST', '/api/invites/preview')).toEqual([]);
  });
});

test.describe('invites', () => {
  test('signed out, the invite leads with an emailed sign-in link and keeps the token out of every URL', async ({ page, fake }) => {
    useSession(fake, null);
    fake.route('POST', '/api/auth/magic/request', { status: 202 });
    await page.goto('/invite#t=invite-token-1');

    await expect(page.getByRole('heading', { level: 1, name: 'Join Platform' })).toBeVisible();
    await expect(page).toHaveURL(/\/invite$/);
    await expect(page.getByText('Sign in or create an account to accept.', { exact: false })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Continue with Google' })).toHaveAttribute('href', '/auth/oidc/google/start?return=%2Finvite');
    await field(page, 'Email').fill('ada@example.com');
    await page.getByRole('button', { name: 'Email me a sign-in link' }).click();

    await expect(page.getByRole('status')).toContainText('If ada@example.com can receive mail, a sign-in link is on its way.');
    expect(requests(fake, 'POST', '/api/auth/magic/request').map((r) => r.json)).toEqual([{ email: 'ada@example.com' }]);
    expect((await pendingInvite(page)).t).toBe('invite-token-1');
    expect(fake.requests.every((r) => !r.path.includes('invite-token-1') && !String(r.query).includes('invite-token-1'))).toBe(true);
  });

  test('the emailed sign-in link brings the visitor back to the waiting invite, which joins the team', async ({ page, fake }) => {
    const setMe = useSession(fake, null);
    fake.route('POST', '/api/auth/magic/consume', () => {
      setMe(NEWCOMER);
      return json(NEWCOMER);
    });
    fake.route('POST', '/api/invites/accept', () => {
      setMe({ ...NEWCOMER, teams: [{ id: TEAM_ID, name: 'Platform', role: 'member', openActions: 3 }] });
      return json({ teamId: TEAM_ID });
    });
    await page.goto('/invite#t=invite-token-2');
    await expect(page.getByRole('heading', { level: 1, name: 'Join Platform' })).toBeVisible();

    // The link from the email, opened in the same browser.
    await page.goto('/auth/magic#t=magic-token-2');
    await page.getByRole('button', { name: 'Sign in as a•••@example.com' }).click();

    await expect(page).toHaveURL(/\/invite$/);
    await expect(page.getByText('You are signed in as ada@example.com.')).toBeVisible();
    await page.getByRole('button', { name: 'Accept invite' }).click();

    await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}$`));
    await expect(header(page).getByRole('button', { name: 'Platform', exact: true })).toBeVisible();
    expect(requests(fake, 'POST', '/api/invites/accept').map((r) => r.json)).toEqual([{ token: 'invite-token-2' }]);
    expect(await pendingInvite(page)).toBeNull();
  });

  test('signing in with a password returns to the invite', async ({ page, fake }) => {
    const setMe = useSession(fake, null);
    fake.route('POST', '/api/auth/login', () => {
      setMe(USER);
      return json(USER);
    });
    fake.route('POST', '/api/invites/accept', json({ teamId: TEAM_ID }));
    await page.goto('/invite#t=invite-token-3');
    await page.getByRole('link', { name: 'Sign in with a password' }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'Sign in to Retroscope' })).toBeVisible();
    await expect(page).toHaveURL(/\/login\?return=%2Finvite$/);
    await field(page, 'Email').fill('ada@example.com');
    await field(page, 'Password').fill('correct horse battery');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();

    await expect(page).toHaveURL(/\/invite$/);
    await page.getByRole('button', { name: 'Accept invite' }).click();
    await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}$`));
    expect(requests(fake, 'POST', '/api/invites/accept').map((r) => r.json)).toEqual([{ token: 'invite-token-3' }]);
  });

  test('Google from the welcome page comes back to the waiting invite', async ({ page, fake }) => {
    const setMe = useSession(fake, null);
    // The provider's round trip: the callback signs the visitor in and redirects to the return, a full page load.
    fake.route('GET', '/auth/oidc/:provider/start', (req) => {
      setMe(NEWCOMER);
      return { status: 302, headers: { Location: req.query.get('return') } };
    });
    await page.goto('/invite#t=invite-token-7');
    await expect(page.getByRole('heading', { level: 1, name: 'Join Platform' })).toBeVisible();
    await header(page).getByRole('link', { name: 'Retroscope home' }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'Planning poker and retros for your team' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Continue with Google' })).toHaveAttribute('href', '/auth/oidc/google/start?return=%2Finvite');
    await page.getByRole('link', { name: 'Continue with Google' }).click();

    await expect(page).toHaveURL(/\/invite$/);
    await expect(page.getByText('You are signed in as ada@example.com.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Accept invite' })).toBeVisible();
    expect(requests(fake, 'GET', '/auth/oidc/google/start').map((r) => r.query.get('return'))).toEqual(['/invite']);
  });

  test('an invite for another address explains why it failed', async ({ page, fake }) => {
    useSession(fake, USER);
    fake.route('POST', '/api/invites/accept', problem(403, 'email_mismatch'));
    await page.goto('/invite#t=invite-token-4');
    await page.getByRole('button', { name: 'Accept invite' }).click();
    await expect(page.getByRole('alert')).toHaveText('This invite was sent to a different email address. Sign in with that address to accept it.');
  });

  test('an expired invite cannot be accepted, and is forgotten', async ({ page, fake }) => {
    useSession(fake, USER);
    fake.route('POST', '/api/invites/preview', json({ teamName: 'Platform', kind: 'email', expired: true }));
    await page.goto('/invite#t=invite-token-5');
    await expect(page.getByText('This invite has expired. Ask a team admin for a new one.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Accept invite' })).toHaveCount(0);
    expect(await pendingInvite(page)).toBeNull();
  });

  test('signing out forgets the waiting invite', async ({ page, fake }) => {
    const setMe = useSession(fake, USER);
    fake.route('POST', '/api/auth/logout', () => {
      setMe(null);
      return { status: 204 };
    });
    await page.goto('/invite#t=invite-token-6');
    await expect(page.getByRole('button', { name: 'Accept invite' })).toBeVisible();
    await page.goto('/account');
    await (await openAccount(page)).item('Sign out').click();
    await expect(page).toHaveURL(/\/login$/);
    expect(await pendingInvite(page)).toBeNull();
  });
});

test('a board route opens the tab socket before the room chunk mounts', async ({ page, fake }) => {
  useSession(fake, USER);
  let release;
  const held = new Promise((done) => {
    release = done;
  });
  await page.route(/\/assets\/chunks\/retro-[A-Z0-9]+\.js$/, async (route) => {
    await held;
    await route.continue();
  });
  const connected = fake.waitForConnection();
  await page.goto(`/retro/${BOARD_CODE}`, { waitUntil: 'commit' });
  await connected;
  expect(fake.received.find((f) => f.frame.t === 'hello')).toBeTruthy();
  release();
});

test('a board joins on the tab socket instead of opening a second one', async ({ page, fake }) => {
  useSession(fake, USER);
  const snap = snapshot();
  fake.boot = { build: 'fake', theme: 'light', me: USER, room: { r: BOARD_ROOM } };
  fake.room(BOARD_ROOM, { snapshot: snap, overlay: EMPTY_OVERLAY, epoch: 'aaaaaaaaaaaaaaaa', seq: 0 });
  await page.goto(`/retro/${BOARD_CODE}`);
  await expect(page.locator('.retro-column')).toHaveCount(snap.columns.length);

  // The snapshot only comes over the socket (the boot JSON carries none), so the columns prove the join.
  await page.waitForTimeout(300);
  expect(fake.connections).toHaveLength(1);
  expect(fake.received.filter((f) => f.frame.t === 'hello')).toHaveLength(1);
});

test('shell routes do not open a socket', async ({ page, fake }) => {
  useSession(fake, USER);
  for (const [path, heading] of [['/account', 'Account'], ['/mentions', 'Mentions'], ['/teams', 'Create or join a team']]) {
    await page.goto(path);
    await expect(page.getByRole('heading', { level: 1, name: heading })).toBeVisible();
  }
  await page.waitForTimeout(300);
  expect(fake.connections).toHaveLength(0);
});

test('account: the profile saves and the header follows', async ({ page, fake }) => {
  useSession(fake, USER);
  fake.route('PATCH', '/api/me', (req) => json({
    ...USER,
    principal: { ...USER.principal, name: req.body.nickname || req.body.fullName },
    user: { ...USER.user, fullName: req.body.fullName, nickname: req.body.nickname },
  }));
  await page.goto('/account');

  await page.getByLabel('Nickname').fill('Countess');
  await page.getByRole('button', { name: 'Save profile' }).click();

  await expect(page.getByText('Profile saved.')).toBeVisible();
  await expect(header(page).getByRole('button', { name: 'Account menu for Countess' }).filter({ visible: true })).toHaveCount(1);
  expect(requests(fake, 'PATCH', '/api/me').map((r) => r.json)).toEqual([{ fullName: 'Ada Lovelace', nickname: 'Countess' }]);
});

test('account: a first password needs a fresh sign-in, and other sessions can be revoked', async ({ page, fake }) => {
  useSession(fake, { ...USER, user: { ...USER.user, hasPassword: false } });
  fake.route('PUT', '/api/me/password', problem(403, 'reauth_required'));
  await page.goto('/account');

  await page.getByLabel('Password', { exact: true }).fill('a long enough secret');
  await page.getByRole('button', { name: 'Add password' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'sign in again' })).toBeVisible();
  expect(requests(fake, 'PUT', '/api/me/password').map((r) => r.json)).toEqual([{ new: 'a long enough secret' }]);

  await page.getByRole('button', { name: 'Revoke' }).click();
  await expect.poll(() => requests(fake, 'DELETE', '/api/me/sessions/aaaaaaaa-0000-0000-0000-000000000002').length).toBe(1);
});

test('mentions: opening the page clears the unread badge', async ({ page, fake }) => {
  useSession(fake, USER);
  await page.goto('/account');
  const bell = header(page).getByRole('link', { name: 'Mentions, 2 unread' });
  await expect(bell).toBeVisible();

  await bell.click();

  await expect(page.getByText('Thanks @Ada Lovelace for the deploy fix')).toBeVisible();
  await expect(page.getByText('Anonymous', { exact: true })).toBeVisible();
  await expect(header(page).getByRole('link', { name: 'Mentions', exact: true })).toHaveAttribute('aria-current', 'page');
  expect(requests(fake, 'POST', '/api/me/mentions/seen')).toHaveLength(1);
});

test('admin: members without the flag are turned away; admins can disable a user', async ({ page, fake }) => {
  useSession(fake, USER);
  await page.goto('/admin/users');
  await expect(page.getByText('This area is for app admins.')).toBeVisible();

  useSession(fake, ADMIN);
  await page.goto('/admin');
  await expect(page).toHaveURL(/\/admin\/users$/);
  await page.getByLabel('Email prefix or name').fill('gr');
  await page.getByRole('button', { name: 'Search' }).click();
  await page.getByRole('button', { name: 'Disable' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Disable' }).click();

  await expect(page.getByRole('button', { name: 'Enable' })).toBeVisible();
  expect(requests(fake, 'PATCH', '/api/admin/users/u2').map((r) => r.json)).toEqual([{ disabled: true }]);
  expect(requests(fake, 'GET', '/api/admin/users').map((r) => r.query.get('q'))).toEqual(['gr']);

  await page.getByRole('link', { name: 'Stats' }).click();
  await expect(page.getByText('liveRooms')).toBeVisible();
});

for (const [name, path, setup] of [
  ['poker', '/poker/POKERCODE1', (fake) => {
    fake.route('GET', '/api/poker/by-code/:code', json({ id: '01900000-0000-7000-8000-0000000000c1', access: 'granted' }));
  }],
  ['a board', `/retro/${BOARD_CODE}`, (fake) => {
    fake.boot = { build: 'fake', theme: 'light', me: USER, room: { r: BOARD_ROOM, e: 'aaaaaaaaaaaaaaaa', s: 0, d: snapshot(), o: EMPTY_OVERLAY } };
  }],
]) {
  test(`${name}: a socket that stays down for more than 10 s shows one reconnecting notice`, async ({ page, fake }) => {
    useSession(fake, USER);
    setup(fake);
    await page.clock.install();
    let attempted;
    const tried = new Promise((done) => {
      attempted = done;
    });
    await page.routeWebSocket(/\/ws$/, (ws) => {
      attempted();
      ws.close();
    });
    await page.goto(path);
    // The clock only counts from the first attempt; poker connects once its chunk has resolved the code.
    await tried;
    const notice = page.getByText('Reconnecting… your changes are saved and will sync.');
    await page.clock.runFor(5_000);
    await expect(notice).toBeHidden();

    await page.clock.runFor(7_000);

    await expect(notice).toBeVisible();
    await expect(page.getByRole('alert').filter({ hasText: 'Reconnecting' })).toHaveCount(0);
  });
}
