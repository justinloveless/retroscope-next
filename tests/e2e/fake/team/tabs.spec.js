import { json, problem } from '../../../web-fake-server/http.js';
import { BOARD_1, MEMBER, ROOM, TEAM_ID, board, setupTeam, teamUrl, expect, test } from './_team.js';

test.describe('team page', () => {
  test('shows the team header and switches tabs through the query string', async ({ page, fake }) => {
    setupTeam(fake);
    await page.goto(teamUrl());

    await expect(page.getByRole('heading', { level: 1, name: 'Platform' })).toBeVisible();
    await expect(page.getByText('Keeps the lights on')).toBeVisible();
    await expect(page.getByText('3 members')).toBeVisible();
    await expect(page.getByRole('tab', { name: 'Boards' })).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByRole('heading', { name: 'Sprint 41 retro' })).toBeVisible();

    await page.getByRole('tab', { name: 'Members' }).click();
    await expect(page).toHaveURL(/\?tab=members$/);
    await expect(page.getByText('Team members (3)')).toBeVisible();

    await page.getByRole('tab', { name: 'Boards' }).click();
    await expect(page).toHaveURL(new RegExp(`/teams/${TEAM_ID}$`));
  });

  test('opens the tab named in the query string', async ({ page, fake }) => {
    setupTeam(fake);
    await page.goto(teamUrl('?tab=kudos'));
    await expect(page.getByRole('tab', { name: 'Kudos' })).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByRole('heading', { name: 'Kudos leaderboard' })).toBeVisible();
  });

  test('asks a signed-out visitor to sign in', async ({ page, fake }) => {
    fake.boot = { build: 'fake', theme: 'light', me: null };
    await page.goto(teamUrl());
    await expect(page.getByRole('heading', { name: 'Sign in to see this team' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Sign in' })).toHaveAttribute('href', `/login?return=${encodeURIComponent(`/teams/${TEAM_ID}`)}`);
  });

  test('says when the team is missing or hidden', async ({ page, fake }) => {
    setupTeam(fake);
    fake.route('GET', `/api/teams/${TEAM_ID}`, problem(404, 'not_found'));
    await page.goto(teamUrl());
    await expect(page.getByRole('heading', { name: 'Team not found or you do not have access.' })).toBeVisible();
  });

  test('shows why when the room kicks the viewer', async ({ page, fake }) => {
    setupTeam(fake);
    await page.goto(teamUrl());
    await expect(page.getByRole('heading', { name: 'Sprint 41 retro' })).toBeVisible();
    await fake.waitForConnection();
    fake.kick(ROOM, 'access_revoked');
    await expect(page.getByRole('heading', { name: 'You no longer have access to this team.' })).toBeVisible();
  });

  test('member count and names follow team.members frames', async ({ page, fake }) => {
    setupTeam(fake);
    await page.goto(teamUrl());
    await expect(page.getByText('3 members')).toBeVisible();
    await fake.waitForConnection();
    fake.tx(ROOM, [{ k: 'team.members', d: { members: [{ pid: MEMBER, name: 'Mia Member', role: 'member' }] } }]);
    await expect(page.getByText('1 member', { exact: true })).toBeVisible();
  });
});

test.describe('boards tab', () => {
  test('lists active and archived boards, with the filter in the query string', async ({ page, fake }) => {
    const calls = setupTeam(fake);
    await page.goto(teamUrl());

    const card = page.locator(`[data-id="${BOARD_1}"]`);
    await expect(card.getByRole('heading', { name: 'Sprint 41 retro' })).toBeVisible();
    await expect(card.getByRole('link', { name: 'Open board' })).toHaveAttribute('href', `/retro/${board(BOARD_1, '').code}`);

    await page.getByRole('button', { name: 'Archived' }).click();
    await expect(page).toHaveURL(/archived=true/);
    await expect(page.getByRole('heading', { name: 'Archived boards' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'View (read-only)' })).toBeVisible();
    expect(calls.boards.at(-1).query.get('archived')).toBe('true');

    await page.reload();
    await expect(page.getByRole('heading', { name: 'Archived boards' })).toBeVisible();
  });

  test('refetches the list on a team.boards.changed notice', async ({ page, fake }) => {
    const calls = setupTeam(fake);
    await page.goto(teamUrl());
    await expect(page.getByRole('heading', { name: 'Sprint 41 retro' })).toBeVisible();
    await fake.waitForConnection();

    const BOARD_3 = '0192f0a0-0000-7000-8000-0000000000b3';
    fake.route('GET', `/api/teams/${TEAM_ID}/boards`, (req) => {
      calls.boards.push(req);
      return json({ items: [board(BOARD_3, 'Sprint 42 retro'), board(BOARD_1, 'Sprint 41 retro')], nextCursor: null });
    });
    const before = calls.boards.length;
    fake.tx(ROOM, [{ k: 'team.boards.changed', d: { boardId: BOARD_3 } }]);

    await expect(page.getByRole('heading', { name: 'Sprint 42 retro' })).toBeVisible();
    expect(calls.boards.length).toBe(before + 1);
  });

  test('pages through boards with the cursor', async ({ page, fake }) => {
    setupTeam(fake);
    const BOARD_3 = '0192f0a0-0000-7000-8000-0000000000b3';
    fake.route('GET', `/api/teams/${TEAM_ID}/boards`, (req) => (req.query.get('cursor') === 'page2'
      ? json({ items: [board(BOARD_3, 'Older retro')], nextCursor: null })
      : json({ items: [board(BOARD_1, 'Sprint 41 retro')], nextCursor: 'page2' })));
    await page.goto(teamUrl());
    await page.getByRole('button', { name: 'Load more' }).click();
    await expect(page.getByRole('heading', { name: 'Older retro' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Load more' })).toBeHidden();
  });

  test('creates a password board from a template and opens it', async ({ page, fake }) => {
    setupTeam(fake);
    const TEMPLATE = '0192f0a0-0000-7000-8000-0000000000f1';
    fake.route('GET', `/api/teams/${TEAM_ID}/templates`, json({ items: [{ id: TEMPLATE, name: 'Starfish', isDefault: false, columns: [], config: {} }] }));
    let created = null;
    fake.route('POST', `/api/teams/${TEAM_ID}/boards`, (req) => {
      created = req.body;
      return json({ id: BOARD_1, code: 'NEWBOARD42' }, 201);
    });
    await page.goto(teamUrl());

    await page.getByRole('button', { name: 'New board' }).first().click();
    const dialog = page.getByRole('dialog', { name: 'New retro board' });
    await dialog.getByLabel('Title').fill('Sprint 43 retro');
    await dialog.getByLabel('Who can open it').selectOption('password');
    await dialog.getByLabel('Board password').fill('hunter22');
    await dialog.getByLabel('Template').selectOption({ label: 'Starfish' });
    await dialog.getByRole('button', { name: 'Create board' }).click();

    await expect(page).toHaveURL(/\/retro\/NEWBOARD42$/);
    expect(created).toEqual({ title: 'Sprint 43 retro', visibility: 'password', password: 'hunter22', templateId: TEMPLATE });
  });

  test('admins archive a board', async ({ page, fake }) => {
    setupTeam(fake);
    let archived = false;
    fake.route('POST', `/api/retro/${BOARD_1}/archive`, () => {
      archived = true;
      return { status: 204 };
    });
    await page.goto(teamUrl());
    await page.getByRole('button', { name: 'Board actions for Sprint 41 retro' }).click();
    await page.getByRole('menuitem', { name: 'Archive' }).click();
    await expect(page.getByRole('heading', { name: 'Sprint 41 retro' })).toBeHidden();
    expect(archived).toBe(true);
  });

  test('members see no board menu and no settings link', async ({ page, fake }) => {
    setupTeam(fake, { me: MEMBER });
    await page.goto(teamUrl());
    await expect(page.getByRole('heading', { name: 'Sprint 41 retro' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Board actions for Sprint 41 retro' })).toHaveCount(0);
    await expect(page.getByRole('link', { name: 'Settings' })).toBeHidden();
  });
});
