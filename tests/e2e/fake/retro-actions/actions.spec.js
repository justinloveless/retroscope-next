import { expect, test } from '../_harness/fixtures.js';
import { problem } from '../../../web-fake-server/http.js';
import {
  BOARD_ID, BOB, CAROL, GOOD, ROOM, TEAM_ID, TEAM_ROOM, actionCard, boardFor, card, cardsIn, item, openBoard,
  serveTeam, serveTeamInfo, teamRoom,
} from './_actions.js';

const actionsOf = (scope) => scope.locator('.retro-action-item');

function setup(fake, { cards = [actionCard(1)], items = [item(1, { sourceCardId: actionCard(1).id, sourceBoardId: BOARD_ID })] } = {}) {
  const snap = boardFor({ cards });
  serveTeam(fake, teamRoom({ actionItems: items }));
  return snap;
}

test('assign and done on an action-items card go to the team room', async ({ page, fake }) => {
  const snap = setup(fake);
  await openBoard(page, fake, { snap });
  const target = cardsIn(page, 'Action Items').first();
  const controls = actionsOf(target);
  await expect(controls).toBeVisible();
  await expect(controls.locator('.retro-assignee')).toHaveText('Assign');

  await controls.locator('.retro-assignee').click();
  await page.getByRole('menuitem', { name: 'Bob' }).click();
  await expect(controls.locator('.retro-assignee')).toHaveText('Bob');
  await expect(controls.locator('.retro-assignee')).toHaveAttribute('aria-label', 'Assigned to Bob. Change assignee');

  const done = controls.locator('.retro-done');
  await expect(done).toHaveAttribute('aria-pressed', 'false');
  await done.click();
  await expect(done).toHaveAttribute('aria-pressed', 'true');
  await expect(done).toHaveText('Done');
  await expect(target).toHaveClass(/retro-card-done/);

  await controls.locator('.retro-assignee').click();
  await page.getByRole('menuitem', { name: 'Nobody' }).click();
  await expect(controls.locator('.retro-assignee')).toHaveText('Assign');

  const commands = fake.received.filter(({ frame }) => frame.t === 'cmd' && frame.k.startsWith('team.action.'));
  expect(commands.map(({ frame }) => [frame.r, frame.k, frame.d])).toEqual([
    [TEAM_ROOM, 'team.action.assign', { itemId: item(1).id, userId: BOB.pid }],
    [TEAM_ROOM, 'team.action.done', { itemId: item(1).id, done: true }],
    [TEAM_ROOM, 'team.action.assign', { itemId: item(1).id, userId: null }],
  ]);
});

test('changes made elsewhere show live, and only action-items cards carry the controls', async ({ page, fake }) => {
  const snap = setup(fake, {
    cards: [card(2, { columnId: GOOD }), actionCard(1)],
  });
  await openBoard(page, fake, { snap });
  const target = cardsIn(page, 'Action Items').first();
  await expect(actionsOf(target)).toBeVisible();
  await expect(actionsOf(cardsIn(page, 'Good').first())).toBeHidden();

  fake.tx(TEAM_ROOM, [{ k: 'team.action.updated', d: { item: item(1, { sourceCardId: actionCard(1).id, sourceBoardId: BOARD_ID, assignedTo: CAROL.pid, doneAt: 1_760_000_900_000, doneBy: BOB.pid }) } }]);
  await expect(actionsOf(target).locator('.retro-assignee')).toHaveText('Carol Danvers');
  await expect(actionsOf(target).locator('.retro-done')).toHaveAttribute('aria-pressed', 'true');

  await expect(target).toHaveClass(/retro-card-done/);

  fake.tx(TEAM_ROOM, [{ k: 'team.action.deleted', d: { itemId: item(1).id } }]);
  await expect(actionsOf(target)).toBeHidden();
  await expect(target).not.toHaveClass(/retro-card-done/);
});

test('a new action-items card gets its controls once the team room has its item', async ({ page, fake }) => {
  const snap = setup(fake, { cards: [], items: [] });
  await openBoard(page, fake, { snap });
  fake.tx(ROOM, [{ k: 'retro.card.added', d: { card: actionCard(3) } }]);
  const target = cardsIn(page, 'Action Items').first();
  await expect(target).toContainText('Follow up 3');
  await expect(actionsOf(target)).toBeHidden();

  fake.tx(TEAM_ROOM, [{ k: 'team.action.added', d: { item: item(3, { sourceCardId: actionCard(3).id, sourceBoardId: BOARD_ID }) } }]);
  await expect(actionsOf(target)).toBeVisible();
});

test('"Create in Jira" creates an issue from the card and shows its key', async ({ page, fake }) => {
  const body = '**Fix** the flaky deploy for @Bob\n\nSecond paragraph with details';
  const snap = setup(fake, { cards: [actionCard(1, { body })] });
  serveTeamInfo(fake, { configured: true, siteHost: 'acme.atlassian.net', projectKey: 'RS' });
  const jira = fake.jira({ teamId: TEAM_ID });
  await openBoard(page, fake, { snap });
  const controls = actionsOf(cardsIn(page, 'Action Items').first());

  await controls.getByRole('button', { name: 'Create in Jira' }).click();
  const key = controls.locator('.retro-jira-key');
  await expect(key).toHaveText(/^RS-\d+$/);
  const created = jira.calls.filter((c) => c.method === 'POST' && c.route === '/issues');
  expect(created).toHaveLength(1);
  expect(created[0].body).toEqual({ summary: 'Fix the flaky deploy for @Bob', description: body });
  await expect(key).toHaveAttribute('href', `https://acme.atlassian.net/browse/${await key.textContent()}`);
  await expect(page.getByRole('status').filter({ hasText: /Created RS-\d+ in Jira/ })).toBeVisible();
});

test('a refused Jira create says why and keeps the button', async ({ page, fake }) => {
  const snap = setup(fake);
  serveTeamInfo(fake, { configured: true, siteHost: 'acme.atlassian.net' });
  const jira = fake.jira({ teamId: TEAM_ID });
  await openBoard(page, fake, { snap });
  jira.failNext(problem(503, 'jira_unavailable'));
  const controls = actionsOf(cardsIn(page, 'Action Items').first());

  await controls.getByRole('button', { name: 'Create in Jira' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'Jira could not be reached. Try again later.' })).toBeVisible();
  await expect(controls.getByRole('button', { name: 'Create in Jira' })).toBeEnabled();
});

test('"Create in Jira" shows only when the team has Jira and the viewer may write to it', async ({ page, fake }) => {
  const snap = setup(fake);
  serveTeamInfo(fake, { configured: false });
  await openBoard(page, fake, { snap });
  const controls = actionsOf(cardsIn(page, 'Action Items').first());
  await expect(controls.locator('.retro-done')).toBeVisible();
  await expect.poll(() => fake.requests.some((r) => r.method === 'GET' && r.path === `/api/teams/${TEAM_ID}`)).toBe(true);
  await expect(controls.getByRole('button', { name: 'Create in Jira' })).toBeHidden();
});

test('members who may not write to Jira do not get the button', async ({ page, fake }) => {
  const snap = setup(fake);
  serveTeamInfo(fake, { configured: true, siteHost: 'acme.atlassian.net', membersCanEdit: false });
  await openBoard(page, fake, { snap, me: BOB });
  const controls = actionsOf(cardsIn(page, 'Action Items').first());
  await expect(controls.locator('.retro-done')).toBeVisible();
  await expect.poll(() => fake.requests.some((r) => r.method === 'GET' && r.path === `/api/teams/${TEAM_ID}`)).toBe(true);
  await expect(controls.getByRole('button', { name: 'Create in Jira' })).toBeHidden();
});
