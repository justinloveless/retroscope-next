// The team page under the header's sections (UX18 decisions 9 and 11): one section at a time in a <main> with
// its own h1, chosen by ?tab=; no in-page tab bar or team header block. The Retros section lists Active and
// Archived boards and makes new boards from a title and a template; boards are for the team's members only.
import { json, problem } from '../../../web-fake-server/http.js';
import { BOARD_1, MEMBER, OWNER, ROOM, TEAM_ID, board, choose, expect, members, setupTeam, teamUrl, test } from './_team.js';

const bar = (page) => page.locator('header.app-header');
const sections = (page) => bar(page).getByRole('navigation', { name: 'Platform sections' });
const main = (page) => page.getByRole('main');
const narrow = (page) => page.viewportSize().width < 768;

test.describe('team page', () => {
  test('shows one section at a time, each with its own heading, as the header’s sections choose', async ({ page, fake }) => {
    setupTeam(fake);
    await page.goto(teamUrl());

    await expect(main(page).getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();
    await expect(page.getByRole('tablist')).toHaveCount(0);
    await expect(page).toHaveTitle('Poker · Platform · Retroscope');
    await expect(sections(page).getByRole('link', { name: 'Poker' })).toHaveAttribute('aria-current', 'page');

    await sections(page).getByRole('link', { name: 'Members' }).click();
    await expect(page).toHaveURL(/\?tab=members$/);
    await expect(main(page).getByRole('heading', { level: 1 })).toHaveText(['Members']);
    await expect(main(page).getByText('3 members')).toBeVisible();
    await expect(page).toHaveTitle('Members · Platform · Retroscope');

    await sections(page).getByRole('link', { name: 'Retros' }).click();
    await expect(page).toHaveURL(/\?tab=retros$/);
    await expect(main(page).getByRole('heading', { level: 1 })).toHaveText(['Retros']);

    await page.goBack();
    await expect(main(page).getByRole('heading', { level: 1 })).toHaveText(['Members']);
  });

  test('opens the section named in the query string', async ({ page, fake }) => {
    setupTeam(fake);
    await page.goto(teamUrl('?tab=kudos'));
    await expect(main(page).getByRole('heading', { level: 1, name: 'Kudos' })).toBeVisible();
    await expect(sections(page).getByRole('link', { name: 'Kudos' })).toHaveAttribute('aria-current', 'page');
  });

  test('a signed-out visitor is sent to sign in and comes back to the section', async ({ page, fake }) => {
    fake.boot = { build: 'fake', theme: 'light', me: null };
    await page.goto(teamUrl('?tab=members'));
    await expect(page.getByRole('heading', { level: 1, name: 'Sign in to Retroscope' })).toBeVisible();
    expect(new URL(page.url()).searchParams.get('return')).toBe(`/teams/${TEAM_ID}?tab=members`);
    expect(fake.requests.some((r) => r.path.startsWith(`/api/teams/${TEAM_ID}`))).toBe(false);
  });

  test('a missing team and one the user is not on show the same screen, which names neither', async ({ page, fake }) => {
    setupTeam(fake);
    const other = '0192f0a0-0000-7000-8000-00000000a0ff';
    fake.route('GET', `/api/teams/${other}`, problem(404, 'not_found'));
    await page.goto(`/teams/${other}?tab=retros`);

    await expect(page.getByRole('heading', { level: 1, name: "You can't open this page" })).toBeVisible();
    await expect(page.getByText("It doesn't exist, or it belongs to a team you're not on.")).toBeVisible();
    await expect(page.getByRole('link', { name: 'Back to Platform' })).toHaveAttribute('href', `/teams/${TEAM_ID}`);
    await expect(page.getByRole('button', { name: 'Use a different account' })).toBeVisible();
    await expect(sections(page).locator('[aria-current]')).toHaveCount(0);
    await expect(page).toHaveTitle("You can't open this page · Retroscope");

    await page.getByRole('link', { name: 'Back to Platform' }).click();
    await expect(main(page).getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();
  });

  test('a member removed while the page is open is told they no longer have access', async ({ page, fake }) => {
    setupTeam(fake);
    await page.goto(teamUrl());
    await expect(main(page).getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();
    await fake.waitForSubscription(ROOM);
    fake.kick(ROOM, 'access_revoked');
    await expect(page.getByRole('heading', { level: 1, name: 'You no longer have access' })).toBeVisible();
    await expect(page.getByText('You were removed from this team, or it was deleted.', { exact: false })).toBeVisible();
  });

  test('a tab over its room limit offers Try again', async ({ page, fake }) => {
    setupTeam(fake);
    await page.goto(teamUrl());
    await expect(main(page).getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();
    await fake.waitForSubscription(ROOM);
    fake.kick(ROOM, 'too_many_rooms');
    await expect(main(page).getByRole('button', { name: 'Try again' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'You no longer have access' })).toHaveCount(0);
  });

  test('a team open in too many of the user’s tabs says so, not that they lost access, and Try again opens it again', async ({ page, fake }) => {
    setupTeam(fake);
    await page.goto(teamUrl());
    await expect(main(page).getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();
    await fake.waitForSubscription(ROOM);
    fake.kick(ROOM, 'too_many_connections');

    await expect(main(page).getByRole('heading', { level: 1, name: 'This team is open in too many of your tabs. Close some of them, then try again.' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'You no longer have access' })).toHaveCount(0);

    await main(page).getByRole('button', { name: 'Try again' }).click();
    await expect(main(page).getByRole('heading', { level: 1, name: 'Poker' })).toBeVisible();
    await fake.waitForSubscription(ROOM);
  });

  test('the header’s gear, name and open action items follow the team room', async ({ page, fake }) => {
    test.skip(narrow(page), 'below 768 px the gear is in the sheet');
    setupTeam(fake);
    await page.goto(teamUrl());
    await expect(bar(page).getByRole('link', { name: 'Team settings' })).toBeVisible();
    await expect(sections(page).getByRole('link', { name: /^Action items/ })).toHaveText('Action items2');
    await fake.waitForSubscription(ROOM);

    const demoted = members().map((m) => (m.pid === OWNER ? { ...m, role: 'member' } : m));
    fake.tx(ROOM, [{ k: 'team.members', d: { members: demoted } }, { k: 'team.action.deleted', d: { itemId: '0192f0a0-0000-7000-8000-0000000000c2' } }]);
    await expect(bar(page).getByRole('link', { name: 'Team settings' })).toBeHidden();
    await expect(sections(page).getByRole('link', { name: /^Action items/ })).toHaveText('Action items1');
  });
});

test.describe('retros section', () => {
  test('lists active and archived boards, with the filter in the query string', async ({ page, fake }) => {
    const calls = setupTeam(fake);
    fake.route('GET', `/api/teams/${TEAM_ID}/boards`, (req) => {
      calls.boards.push(req);
      return json({
        items: req.query.get('archived') === 'true'
          ? [board('0192f0a0-0000-7000-8000-0000000000b2', 'Sprint 40 retro', { archived: true, archivedAt: Date.UTC(2026, 9, 2, 9) })]
          : [board(BOARD_1, 'Sprint 41 retro', { stagesEnabled: true, stage: 'voting', cardCount: 12 })],
        nextCursor: null,
      });
    });
    await page.goto(teamUrl('?tab=retros'));

    const card = page.locator(`[data-id="${BOARD_1}"]`);
    await expect(card.getByRole('link', { name: 'Sprint 41 retro' })).toHaveAttribute('href', `/retro/${board(BOARD_1, '').code}`);
    await expect(card).toContainText('Voting');
    await expect(main(page).getByRole('button', { name: 'Active' })).toHaveAttribute('aria-pressed', 'true');

    await main(page).getByRole('button', { name: 'Archived' }).click();
    await expect(page).toHaveURL(/archived=true/);
    await expect(main(page).getByRole('button', { name: 'Archived' })).toHaveAttribute('aria-pressed', 'true');
    await expect(main(page).getByRole('link', { name: 'Sprint 40 retro' })).toBeVisible();
    await expect(main(page)).toContainText('archived');
    expect(calls.boards.at(-1).query.get('archived')).toBe('true');

    await page.reload();
    await expect(main(page).getByRole('link', { name: 'Sprint 40 retro' })).toBeVisible();
  });

  test('refetches the list on a team.boards.changed notice', async ({ page, fake }) => {
    const calls = setupTeam(fake);
    await page.goto(teamUrl('?tab=retros'));
    await expect(main(page).getByRole('link', { name: 'Sprint 41 retro' })).toBeVisible();
    await fake.waitForSubscription(ROOM);

    const BOARD_3 = '0192f0a0-0000-7000-8000-0000000000b3';
    fake.route('GET', `/api/teams/${TEAM_ID}/boards`, (req) => {
      calls.boards.push(req);
      return json({ items: [board(BOARD_3, 'Sprint 42 retro'), board(BOARD_1, 'Sprint 41 retro')], nextCursor: null });
    });
    const before = calls.boards.length;
    fake.tx(ROOM, [{ k: 'team.boards.changed', d: { boardId: BOARD_3 } }]);

    await expect(main(page).getByRole('link', { name: 'Sprint 42 retro' })).toBeVisible();
    expect(calls.boards.length).toBe(before + 1);
  });

  test('pages through boards with the cursor', async ({ page, fake }) => {
    setupTeam(fake);
    const BOARD_3 = '0192f0a0-0000-7000-8000-0000000000b3';
    fake.route('GET', `/api/teams/${TEAM_ID}/boards`, (req) => (req.query.get('cursor') === 'page2'
      ? json({ items: [board(BOARD_3, 'Older retro')], nextCursor: null })
      : json({ items: [board(BOARD_1, 'Sprint 41 retro')], nextCursor: 'page2' })));
    await page.goto(teamUrl('?tab=retros'));
    await main(page).getByRole('button', { name: 'Load more' }).click();
    await expect(main(page).getByRole('link', { name: 'Older retro' })).toBeVisible();
    await expect(main(page).getByRole('button', { name: 'Load more' })).toBeHidden();
  });

  test('New board asks for a title and a template only, and opens the board', async ({ page, fake }) => {
    setupTeam(fake);
    const TEMPLATE = '0192f0a0-0000-7000-8000-0000000000f1';
    fake.route('GET', `/api/teams/${TEAM_ID}/templates`, json({ items: [{ id: TEMPLATE, name: 'Starfish', isDefault: false, columns: [], config: {} }] }));
    let created = null;
    fake.route('POST', `/api/teams/${TEAM_ID}/boards`, (req) => {
      created = req.body;
      return json({ id: BOARD_1, code: 'NEWBOARD42' }, 201);
    });
    await page.goto(teamUrl('?tab=retros'));

    await main(page).getByRole('button', { name: 'New board' }).click();
    const dialog = page.getByRole('dialog', { name: 'New retro board' });
    await expect(dialog.getByText('Only members of Platform can open it.')).toBeVisible();
    await expect(dialog.getByLabel('Who can open it')).toHaveCount(0);
    await expect(dialog.getByLabel('Board password')).toHaveCount(0);
    await dialog.getByLabel('Title').fill('Sprint 43 retro');
    await choose(dialog.getByLabel('Template'), 'Starfish');
    await dialog.getByRole('button', { name: 'Create board' }).click();

    await expect(page).toHaveURL(/\/retro\/NEWBOARD42$/);
    expect(created).toEqual({ title: 'Sprint 43 retro', templateId: TEMPLATE });
  });

  test('admins archive a board', async ({ page, fake }) => {
    setupTeam(fake);
    let archived = false;
    fake.route('POST', `/api/retro/${BOARD_1}/archive`, () => {
      archived = true;
      return { status: 204 };
    });
    await page.goto(teamUrl('?tab=retros'));
    await page.getByRole('button', { name: 'Board actions for Sprint 41 retro' }).click();
    await page.getByRole('menuitem', { name: 'Archive' }).click();
    await expect(main(page).getByRole('link', { name: 'Sprint 41 retro' })).toBeHidden();
    expect(archived).toBe(true);
  });

  test('members see no board menu and no settings gear', async ({ page, fake }) => {
    setupTeam(fake, { me: MEMBER });
    await page.goto(teamUrl('?tab=retros'));
    await expect(main(page).getByRole('link', { name: 'Sprint 41 retro' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Board actions for Sprint 41 retro' })).toHaveCount(0);
    await expect(bar(page).getByRole('link', { name: 'Team settings' })).toBeHidden();
  });
});
