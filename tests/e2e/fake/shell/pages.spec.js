// What each shell screen does beyond painting: quick sessions as a guest, join by code, teams and
// favorites, invites, account, mentions, admin, and the early socket on room routes.
import { json, problem } from '../../../web-fake-server/http.js';
import { expect, test } from '../_harness/fixtures.js';
import { EMPTY_OVERLAY, CODE as BOARD_CODE, ROOM as BOARD_ROOM, snapshot } from '../retro-board/_board.js';
import { ADMIN, TEAM_ID, USER, installApi, pageErrors, requests, useSession } from './_shell.js';

const GUEST = { id: '01900000-0000-7000-8000-0000000000b1', kind: 'guest', name: 'Visitor' };

test.beforeEach(({ fake }) => {
  installApi(fake);
});

test('a visitor starts quick poker as a guest and lands in the room', async ({ page, fake }) => {
  const errors = pageErrors(page);
  useSession(fake, null);
  fake.route('POST', '/api/guest', json({ principal: GUEST }));
  fake.route('POST', '/api/poker', json({ id: '01900000-0000-7000-8000-0000000000c1', code: 'ABCDEFGHJK' }));
  await page.goto('/');

  await page.getByRole('button', { name: 'Start quick poker' }).click();
  const dialog = page.getByRole('dialog', { name: 'Join as a guest' });
  await dialog.getByLabel('Your name').fill('Visitor');
  await dialog.getByRole('button', { name: 'Continue' }).click();

  await expect(page).toHaveURL(/\/poker\/ABCDEFGHJK$/);
  expect(requests(fake, 'POST', '/api/guest').map((r) => r.json)).toEqual([{ displayName: 'Visitor' }]);
  expect(requests(fake, 'POST', '/api/poker')).toHaveLength(1);
  expect(errors).toEqual([]);
});

test('closing the guest dialog creates nothing', async ({ page, fake }) => {
  useSession(fake, null);
  await page.goto('/');
  await page.getByRole('button', { name: 'Start quick retro' }).click();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(requests(fake, 'POST', '/api/retro')).toEqual([]);
});

test('joining by code checks the code first', async ({ page, fake }) => {
  useSession(fake, USER);
  fake.route('GET', '/api/retro/by-code/:code', (req) => (req.params.code === 'GOODCODE42'
    ? json({ id: '01900000-0000-7000-8000-0000000000b2', access: 'granted' })
    : problem(404, 'not_found')));
  await page.goto('/');
  const code = page.getByLabel('Join a retro by code');

  await code.fill('NOPE123');
  await code.press('Enter');
  await expect(page.getByText('No retro board has that code.')).toBeVisible();

  await code.fill('GOODCODE42');
  await code.press('Enter');
  await expect(page).toHaveURL(/\/retro\/GOODCODE42$/);
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
  fake.me = { pid: USER.principal.id, kind: 'user', name: USER.principal.name };
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
  await page.goto('/dashboard');
  await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible();
  await page.waitForTimeout(300);
  expect(fake.connections).toHaveLength(0);
});

test('signed-out visitors are sent to sign in from member pages', async ({ page, fake }) => {
  useSession(fake, null);
  for (const path of ['/dashboard', '/teams', '/account', '/mentions', '/admin/users']) {
    await page.goto(path);
    await expect(page.getByRole('heading', { level: 1, name: 'Sign in to Retroscope' })).toBeVisible();
    expect(new URL(page.url()).searchParams.get('return')).toBe(path);
  }
});

test('the dashboard lists recent rooms with links into them', async ({ page, fake }) => {
  useSession(fake, USER);
  await page.goto('/dashboard');
  await expect(page.getByRole('link', { name: 'Sprint 42 retro' })).toHaveAttribute('href', '/retro/BOARDCODE1');
  await expect(page.getByRole('link', { name: 'POKERCODE1' })).toHaveAttribute('href', `/teams/${TEAM_ID}/poker/POKERCODE1`);
  await expect(page.getByRole('link', { name: 'Platform' }).first()).toHaveAttribute('href', `/teams/${TEAM_ID}`);
});

test('teams: favorites sort first and toggle optimistically; creating a team opens it', async ({ page, fake }) => {
  useSession(fake, USER);
  fake.route('POST', '/api/teams', json({ id: '01900000-0000-7000-8000-0000000000a9' }, 201));
  await page.goto('/teams');
  const names = page.locator('main li a');
  await expect(names).toHaveText(['Apps', 'Platform']);

  await page.getByRole('button', { name: 'Favorite Platform' }).click();
  await expect(page.getByRole('button', { name: 'Unfavorite Platform' })).toHaveAttribute('aria-pressed', 'true');
  await expect.poll(() => requests(fake, 'PUT', `/api/teams/${TEAM_ID}/favorite`).length).toBe(1);

  await page.getByRole('button', { name: 'Create team' }).click();
  const dialog = page.getByRole('dialog', { name: 'Create a team' });
  await dialog.getByLabel('Team name').fill('Data');
  await dialog.getByRole('button', { name: 'Create team' }).click();
  await expect(page).toHaveURL(/\/teams\/01900000-0000-7000-8000-0000000000a9$/);
  expect(requests(fake, 'POST', '/api/teams').map((r) => r.json)).toEqual([{ name: 'Data', description: null }]);
});

test('a failed favorite toggle rolls back', async ({ page, fake }) => {
  useSession(fake, USER);
  fake.route('PUT', '/api/teams/:id/favorite', problem(403, 'forbidden'));
  await page.goto('/teams');
  await page.getByRole('button', { name: 'Favorite Platform' }).click();
  await expect(page.getByText('You are not allowed to do that.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Favorite Platform' })).toHaveAttribute('aria-pressed', 'false');
});

test('an unverified account cannot create teams', async ({ page, fake }) => {
  useSession(fake, { ...USER, user: { ...USER.user, emailVerified: false } });
  await page.goto('/teams');
  await expect(page.getByRole('button', { name: 'Create team' })).toBeDisabled();
  await expect(page.getByText('Verify your email address to create a team.')).toBeVisible();
});

test('an invite is accepted after signing in, and the token never reaches a URL', async ({ page, fake }) => {
  const setMe = useSession(fake, null);
  fake.route('POST', '/api/auth/login', () => {
    setMe(USER);
    return { status: 200, body: USER };
  });
  fake.route('POST', '/api/invites/accept', json({ teamId: TEAM_ID }));
  await page.goto('/invite#t=invite-token-1');

  await expect(page.getByText('You are invited to join Platform.')).toBeVisible();
  await expect(page).toHaveURL(/\/invite$/);
  await page.getByRole('link', { name: 'Sign in' }).last().click();
  await page.getByLabel('Email', { exact: true }).filter({ visible: true }).fill('ada@example.com');
  await page.getByLabel('Password', { exact: true }).filter({ visible: true }).fill('correct horse battery');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();

  await page.getByRole('button', { name: 'Accept invite' }).click();
  await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}$`));
  expect(requests(fake, 'POST', '/api/invites/accept').map((r) => r.json)).toEqual([{ token: 'invite-token-1' }]);
  expect(fake.requests.every((r) => !r.path.includes('invite-token-1') && !String(r.query).includes('invite-token-1'))).toBe(true);
});

test('an invite for another address explains why it failed', async ({ page, fake }) => {
  useSession(fake, USER);
  fake.route('POST', '/api/invites/accept', problem(403, 'email_mismatch'));
  await page.goto('/invite#t=invite-token-2');
  await page.getByRole('button', { name: 'Accept invite' }).click();
  await expect(page.getByRole('alert')).toHaveText('This invite was sent to a different email address. Sign in with that account to accept it.');
});

test('an expired invite cannot be accepted', async ({ page, fake }) => {
  useSession(fake, USER);
  fake.route('POST', '/api/invites/preview', json({ teamName: 'Platform', kind: 'email', expired: true }));
  await page.goto('/invite#t=invite-token-3');
  await expect(page.getByText('This invite has expired. Ask a team admin for a new one.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Accept invite' })).toHaveCount(0);
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
  await expect(page.getByRole('button', { name: 'Account menu for Countess' })).toBeVisible();
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
  await page.goto('/dashboard');
  await expect(page.getByRole('link', { name: 'Mentions (2 unread)' })).toBeVisible();

  await page.getByRole('link', { name: 'Mentions (2 unread)' }).click();

  await expect(page.getByText('Thanks @Ada Lovelace for the deploy fix')).toBeVisible();
  await expect(page.getByText('Anonymous', { exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Mentions', exact: true })).toBeVisible();
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
    fake.route('GET', '/api/poker/by-code/:code', json({ id: '01900000-0000-7000-8000-0000000000c1' }));
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
