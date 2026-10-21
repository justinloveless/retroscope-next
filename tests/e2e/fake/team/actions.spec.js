import { json, problem } from '../../../web-fake-server/http.js';
import { BOARD_1, ITEM_1, ITEM_2, MEMBER, OWNER, ROOM, TEAM_ID, actionItem, listItems, setupTeam, snapshot, teamUrl, choose, expect, test } from './_team.js';

const COMMENT_1 = '0192f0a0-0000-7000-8000-000000007001';
const COMMENT_2 = '0192f0a0-0000-7000-8000-000000007002';

function item(page, id) {
  return page.locator(`.team-item[data-id="${id}"]`);
}

function comment(id, body, name) {
  return { id, body, anonymous: name === null, createdAt: Date.now() - 60_000, author: name === null ? undefined : { pid: MEMBER, name, guest: false } };
}

test.describe('action items tab', () => {
  test('groups items by board, renders their markup and filters through the query string', async ({ page, fake }) => {
    const calls = setupTeam(fake);
    await page.goto(teamUrl('?tab=actions'));

    await expect(page.locator(`.team-group[data-board="${BOARD_1}"] .team-group-title`)).toHaveText('Sprint 41 retro');
    await expect(item(page, ITEM_1).locator('strong')).toHaveText('flaky');
    await expect(item(page, ITEM_2).getByLabel('Assigned to')).toHaveAccessibleName('Assigned to: Mia Member');

    await choose(page.getByLabel('Assignee'), 'Mia Member');
    await expect(page).toHaveURL(new RegExp(`assignee=${MEMBER}`));
    await expect(item(page, ITEM_1)).toHaveCount(0);
    await expect(item(page, ITEM_2)).toBeVisible();
    expect(calls.actionItems.at(-1).query.get('assignee')).toBe(MEMBER);

    await choose(page.getByLabel('Status'), 'Done');
    await expect(page.getByText('No action items match these filters.')).toBeVisible();
    expect(calls.actionItems.at(-1).query.get('status')).toBe('done');
  });

  test('assign and done go to the team room and show at once', async ({ page, fake }) => {
    setupTeam(fake);
    const commands = [];
    let release;
    const held = new Promise((done) => {
      release = done;
    });
    fake.onCommand('team.action.assign', (ctx) => {
      commands.push(ctx.frame.d);
      ctx.commit([{ k: 'team.action.updated', d: { item: { ...snapshot().actionItems[0], assignedTo: OWNER } } }]);
    });
    fake.onCommand('team.action.done', async (ctx) => {
      commands.push(ctx.frame.d);
      await held;
      ctx.commit([{ k: 'team.action.updated', d: { item: { ...snapshot().actionItems[0], assignedTo: OWNER, doneAt: Date.now(), doneBy: OWNER } } }]);
    });
    await page.goto(teamUrl('?tab=actions&status=all'));

    await choose(item(page, ITEM_1).getByLabel('Assigned to'), 'Olive Owner');
    await expect.poll(() => commands).toEqual([{ itemId: ITEM_1, userId: OWNER }]);
    await expect(item(page, ITEM_1).getByLabel('Assigned to')).toHaveAccessibleName('Assigned to: Olive Owner');

    // The server has not answered yet: the done state on screen is the optimistic intent.
    await item(page, ITEM_1).getByRole('button', { name: 'Mark done' }).click();
    await expect(item(page, ITEM_1).getByRole('button', { name: 'Re-open' })).toBeVisible();
    await expect(item(page, ITEM_1)).toHaveClass(/team-item-done/);
    expect(commands[1]).toEqual({ itemId: ITEM_1, done: true });
    release();
    await expect(item(page, ITEM_1).getByRole('button', { name: 'Re-open' })).toBeVisible();
  });

  test('a refused command rolls back and says why', async ({ page, fake }) => {
    setupTeam(fake);
    fake.onCommand('team.action.done', (ctx) => ctx.nack('forbidden'));
    await page.goto(teamUrl('?tab=actions'));
    await item(page, ITEM_2).getByRole('button', { name: 'Mark done' }).click();
    await expect(page.getByRole('alert').filter({ hasText: 'You do not have permission to do that.' })).toBeVisible();
    await expect(item(page, ITEM_2).getByRole('button', { name: 'Mark done' })).toBeVisible();
  });

  test('live frames add, update, sync text and delete items', async ({ page, fake }) => {
    const calls = setupTeam(fake);
    await page.goto(teamUrl('?tab=actions'));
    await expect(item(page, ITEM_2)).toBeVisible();
    await fake.waitForSubscription(ROOM);

    fake.tx(ROOM, [{ k: 'team.action.text', d: { itemId: ITEM_2, text: 'Write the *new* runbook', sourceVersion: 2 } }]);
    await expect(item(page, ITEM_2).locator('em')).toHaveText('new');
    fake.tx(ROOM, [{ k: 'team.action.text', d: { itemId: ITEM_2, text: 'An older edit', sourceVersion: 1 } }]);
    fake.tx(ROOM, [{ k: 'team.action.updated', d: { item: { ...snapshot().actionItems[1], text: 'Write the *new* runbook', sourceVersion: 2, assignedTo: OWNER } } }]);
    await expect(item(page, ITEM_2).getByLabel('Assigned to')).toHaveAccessibleName('Assigned to: Olive Owner');
    await expect(item(page, ITEM_2)).toContainText('Write the new runbook');

    const ITEM_3 = '0192f0a0-0000-7000-8000-0000000000c3';
    const added = actionItem(ITEM_3, 'Rotate the keys', { sourceBoardId: BOARD_1 });
    fake.route('GET', `/api/teams/${TEAM_ID}/action-items`, (req) => {
      calls.actionItems.push(req);
      return json({ items: [{ ...added, boardTitle: 'Sprint 41 retro' }, ...listItems()], nextCursor: null });
    });
    const loads = calls.actionItems.length;
    fake.tx(ROOM, [{ k: 'team.action.added', d: { item: added } }]);
    await expect(item(page, ITEM_3)).toContainText('Rotate the keys');
    expect(calls.actionItems.length).toBe(loads + 1);

    fake.tx(ROOM, [{ k: 'team.action.deleted', d: { itemId: ITEM_1 } }]);
    await expect(item(page, ITEM_1)).toHaveCount(0);
  });

  test('admins delete an item after confirming', async ({ page, fake }) => {
    setupTeam(fake);
    let deleted = null;
    fake.onCommand('team.action.delete', (ctx) => {
      deleted = ctx.frame.d.itemId;
      ctx.commit([{ k: 'team.action.deleted', d: { itemId: ctx.frame.d.itemId } }]);
    });
    await page.goto(teamUrl('?tab=actions'));
    await item(page, ITEM_2).getByRole('button', { name: 'Delete action item' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Delete' }).click();
    await expect(item(page, ITEM_2)).toHaveCount(0);
    expect(deleted).toBe(ITEM_2);
  });
});

test.describe('action item comments', () => {
  test('load on open, refetch on a team.action.comments notice and post through HTTP', async ({ page, fake }) => {
    setupTeam(fake);
    let comments = [comment(COMMENT_1, 'Is this still **flaky**?', 'Mia Member')];
    const gets = [];
    const posts = [];
    fake.route('GET', `/api/teams/${TEAM_ID}/action-items/${ITEM_1}/comments`, (req) => {
      gets.push(req);
      return json({ items: comments });
    });
    fake.route('POST', `/api/teams/${TEAM_ID}/action-items/${ITEM_1}/comments`, (req) => {
      posts.push(req.body);
      return { status: 204 };
    });
    await page.goto(teamUrl('?tab=actions'));

    await expect(item(page, ITEM_2).getByRole('button', { name: /comment/ })).toHaveCount(0);
    await item(page, ITEM_1).getByRole('button', { name: '1 comment' }).click();
    const panel = item(page, ITEM_1).locator('.team-comments-panel');
    await expect(panel.locator(`[data-id="${COMMENT_1}"]`)).toContainText('Mia Member');
    await expect(panel.locator(`[data-id="${COMMENT_1}"] strong`).last()).toHaveText('flaky');
    expect(gets).toHaveLength(1);

    await fake.waitForSubscription(ROOM);
    comments = [...comments, comment(COMMENT_2, 'Yes, twice today.', null)];
    fake.tx(ROOM, [{ k: 'team.action.comments', d: { itemId: ITEM_1, count: 2 } }]);
    await expect(panel.locator(`[data-id="${COMMENT_2}"]`)).toContainText('Anonymous');
    await expect(item(page, ITEM_1).getByRole('button', { name: '2 comments' })).toBeVisible();
    expect(gets).toHaveLength(2);

    const editor = panel.getByRole('textbox', { name: 'Add a comment' });
    await editor.click();
    await page.keyboard.type('On it');
    await page.keyboard.press('Enter');
    await expect.poll(() => posts.length).toBe(1);
    expect(posts[0].body).toBe('On it');
    expect(posts[0].commentId).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    await expect(editor).toHaveText('');
    await expect.poll(() => gets.length).toBe(3);
  });

  test('a closed panel ignores notices, and a failed post keeps the draft', async ({ page, fake }) => {
    setupTeam(fake);
    const gets = [];
    fake.route('GET', `/api/teams/${TEAM_ID}/action-items/${ITEM_1}/comments`, (req) => {
      gets.push(req);
      return json({ items: [] });
    });
    fake.route('POST', `/api/teams/${TEAM_ID}/action-items/${ITEM_1}/comments`, problem(404, 'not_found'));
    await page.goto(teamUrl('?tab=actions'));
    await fake.waitForSubscription(ROOM);

    fake.tx(ROOM, [{ k: 'team.action.comments', d: { itemId: ITEM_1, count: 0 } }]);
    await expect(item(page, ITEM_1).getByRole('button', { name: 'Add comment' })).toBeVisible();
    expect(gets).toHaveLength(0);

    await item(page, ITEM_1).getByRole('button', { name: 'Add comment' }).click();
    await expect(item(page, ITEM_1).getByText('No comments yet.')).toBeVisible();
    const editor = item(page, ITEM_1).getByRole('textbox', { name: 'Add a comment' });
    await editor.click();
    await page.keyboard.type('Lost?');
    await item(page, ITEM_1).getByRole('button', { name: 'Comment', exact: true }).click();
    await expect(page.getByRole('alert').filter({ hasText: 'That no longer exists.' })).toBeVisible();
    await expect(editor).toHaveText('Lost?');
  });
});
