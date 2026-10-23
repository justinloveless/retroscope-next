// Poker with Jira through the real proxy and FakeJira (§f, §j): browse filters and buckets, Pointing and Pointed
// reasons, adding one issue and a whole bucket, the spotlight holder's pending preview (committed, and discarded when
// the holder disconnects), the drawer (hover prefetch, a description edit that keeps its table and screenshot) and
// submitted points that everyone sees.
import { expect, test } from './_real.js';
import { bucket, drawer, isPhone, jiraTable, openBrowse, openSheet, row } from './_jira.js';
import { chooseSpotlight, handCard, shownRound } from './_poker.js';

test('browse buckets and filters, add one issue and a bucket, and see Pointing and Pointed', async ({ open }, testInfo) => {
  const { players, path, ground } = await jiraTable(open, testInfo, ['Ann Jira', 'Bob Jira']);
  const [ann, bob] = players;
  const panel = await openBrowse(ann.page, path);
  await bob.page.goto(path);
  await bob.page.locator('.nt-table').waitFor();

  await expect(panel.locator('.jira-bucket-name')).toHaveText(['Sprint 41', 'Sprint 42', 'Backlog']);
  // Done issues stay out of the way by default.
  await expect(row(ann.page, ground.key(2))).toBeVisible();
  await expect(row(ann.page, ground.key(3))).toHaveCount(0);

  await panel.getByRole('button', { name: 'Filters' }).click();
  await panel.getByRole('switch').click();
  await panel.locator('.jira-filter-points').click();
  await ann.page.getByRole('menuitem', { name: 'Any points' }).click();
  await panel.locator('.jira-filter-status').click();
  await ann.page.getByRole('menuitem', { name: 'All statuses' }).click();
  await expect(row(ann.page, ground.key(3))).toBeVisible();
  await panel.locator('.jira-filter-points').click();
  await ann.page.getByRole('menuitem', { name: '5 pts' }).click();
  await expect(panel.locator('.jira-row')).toHaveCount(1);
  await expect(row(ann.page, ground.key(1))).toBeVisible();
  await panel.locator('.jira-filter-points').click();
  await ann.page.getByRole('menuitem', { name: 'Any points' }).click();
  await panel.locator('.jira-filter-status').click();
  await ann.page.getByRole('menuitem', { name: 'Exclude Done' }).click();
  await expect(row(ann.page, ground.key(3))).toHaveCount(0);

  await row(ann.page, ground.key(2)).getByRole('button', { name: `Add ${ground.key(2)} to rounds` }).click();
  await expect(row(ann.page, ground.key(2)).locator('.jira-reason')).toHaveText('Pointing');
  await expect(shownRound(bob.page)).toContainText(ground.key(2));
  if (isPhone(ann.page)) {
    await ann.page.keyboard.press('Escape');
  }
  await ann.page.getByRole('button', { name: 'Reveal Cards' }).click();
  await ann.page.getByRole('button', { name: 'End' }).click();
  await expect(bob.page.locator('.nt-ticket-kicker')).toHaveText('Viewing:');
  const reopened = await openBrowse(ann.page, path);
  await expect(row(ann.page, ground.key(2)).locator('.jira-reason')).toHaveText('Pointed');
  await expect(row(ann.page, ground.key(2)).getByRole('button', { name: /^Add/ })).toHaveCount(0);

  await bucket(ann.page, 'Sprint 42').getByRole('button', { name: 'Add all' }).click();
  await expect(ann.page.getByText('Added 2 tickets to active rounds')).toBeVisible();
  await expect(bucket(ann.page, 'Sprint 42').getByRole('button', { name: 'Add all' })).toBeDisabled();
  await expect(bob.page.locator('.nt-strip .nt-chip-key')).toHaveText([ground.key(2), ground.key(4), ground.key(5)]);
  await expect(reopened.locator('.jira-row .jira-reason', { hasText: 'Pointing' })).toHaveCount(2);
});

test('the holder previews an issue for everyone, commits it, and a preview left behind is discarded when the holder goes', async ({ open }, testInfo) => {
  const { players, path, ground } = await jiraTable(open, testInfo, ['Ann Preview', 'Bob Preview']);
  const [ann, bob] = players;
  await bob.page.goto(path);
  await bob.page.locator('.nt-table').waitFor();
  await openBrowse(ann.page, path);
  if (isPhone(ann.page)) {
    await ann.page.keyboard.press('Escape');
  }
  await chooseSpotlight(ann.page, 'Spotlight this round');
  await expect(bob.page.locator('.nt-spotlight')).toHaveAttribute('data-role', 'other');
  if (isPhone(ann.page)) {
    await openSheet(ann.page);
  }

  await row(ann.page, ground.key(9)).locator('.jira-row-open').click();
  await expect(drawer(ann.page).locator('.jira-preview')).toContainText('Everyone sees this issue as a preview round.');
  await expect(shownRound(bob.page)).toContainText(ground.key(9));
  await expect(bob.page.locator('.nt-note')).toHaveText('Preview: this round has not been added yet.');
  // The preview stays on the followers' tables; the holder's tab does not pull the spotlight back.
  await bob.page.waitForTimeout(1000);
  await expect(shownRound(bob.page)).toContainText(ground.key(9));
  await drawer(ann.page).getByRole('button', { name: 'Add to rounds' }).click();
  await expect(drawer(ann.page).getByRole('button', { name: 'Added to the rounds' })).toBeDisabled();
  await expect(bob.page.locator('.nt-hand')).toBeVisible();
  await drawer(ann.page).getByRole('button', { name: 'Close' }).click();
  await expect(drawer(ann.page)).toBeHidden();

  await row(ann.page, ground.key(6)).locator('.jira-row-open').click();
  await expect(drawer(ann.page).locator('.jira-preview')).toContainText('Everyone sees this issue as a preview round.');
  await expect(shownRound(bob.page)).toContainText(ground.key(6));
  await expect(bob.page.locator('.nt-strip .nt-chip-key')).toContainText([ground.key(6)]);
  await ann.context.close();
  // The holder's last socket is gone: after the 10 s presence grace their pending rounds are discarded (R7).
  await expect(bob.page.locator('.nt-strip .nt-chip-key', { hasText: ground.key(6) })).toHaveCount(0, { timeout: 30_000 });
  await expect(bob.page.locator('.nt-strip .nt-chip-key', { hasText: ground.key(9) })).toHaveCount(1);
});

test('the drawer prefetches on hover and edits the summary and a description without losing its table or screenshot', async ({ open }, testInfo) => {
  const { players, path, ground, team } = await jiraTable(open, testInfo, ['Ann Drawer']);
  const [ann] = players;
  const key = ground.key(7);
  const gets = [];
  ann.page.on('request', (request) => {
    if (request.method() === 'GET' && request.url().endsWith(`/jira/issues/${key}`)) {
      gets.push(request.url());
    }
  });
  await openBrowse(ann.page, path);
  const target = row(ann.page, key).locator('.jira-row-open');
  if (!isPhone(ann.page)) {
    await target.hover();
    await expect.poll(() => gets.length, { timeout: 3000 }).toBe(1);
  }
  await target.click();
  const sheet = drawer(ann.page);
  await expect(sheet).toHaveAttribute('data-key', key);
  await expect(sheet.locator('.jira-loading')).toHaveCount(0);
  expect(gets).toHaveLength(1);
  await expect(sheet.locator('.jira-description strong')).toHaveText('pay with a saved card');
  await expect(sheet.locator('.jira-description .island td').first()).toHaveText('Visa');
  await expect(sheet.getByRole('button', { name: 'View screenshot.png' }).locator('img')).toBeVisible();

  const lastItem = await sheet.locator('.jira-description li').last().textContent();
  await sheet.getByRole('button', { name: 'Edit description' }).click();
  await expect(sheet.locator('.jira-description-editor .island').first()).toHaveAttribute('contenteditable', 'false');
  await ann.page.evaluate(() => {
    const input = document.querySelector('.jira-description-editor [contenteditable="true"]');
    input.focus();
    const item = [...input.querySelectorAll('li')].at(-1);
    const text = document.createTreeWalker(item, NodeFilter.SHOW_TEXT).nextNode();
    const range = document.createRange();
    range.setStart(text, 0);
    range.collapse(true);
    getSelection().removeAllRanges();
    getSelection().addRange(range);
  });
  await ann.page.keyboard.type('Then ');
  await sheet.getByRole('button', { name: 'Save', exact: true }).click();
  // The stack keeps FakeJira's state between runs, so the item grows by one "Then " per run.
  const item = `Then ${lastItem}`;
  await expect(sheet.locator('.jira-description li').last()).toHaveText(item);
  await expect(sheet.locator('.jira-description .island td').first()).toHaveText('Visa');

  const summary = `Pay with a stored card at ${Date.now()}`;
  await sheet.getByRole('button', { name: /^Edit summary/ }).click();
  await sheet.getByRole('textbox', { name: 'Summary' }).fill(summary);
  await sheet.getByRole('textbox', { name: 'Summary' }).press('Enter');
  await expect(sheet.getByRole('button', { name: /^Edit summary/ })).toHaveText(summary);
  await expect(sheet.locator('.jira-description .island td').first()).toHaveText('Visa');

  const fresh = await (await ann.page.request.get(`/api/teams/${team.id}/jira/issues/${key}`, { headers: { 'X-RS': '1' } })).json();
  const islands = fresh.description.c.filter((node) => node.t === 'island');
  expect(islands).toHaveLength(2);
  expect(JSON.stringify(fresh.description)).toContain(item);
  expect(JSON.stringify(islands)).toContain('Visa');
});

test('submitted points show pending and then the result to everyone at the table', async ({ open }, testInfo) => {
  const { players, path, ground, team } = await jiraTable(open, testInfo, ['Ann Submit', 'Bob Submit']);
  const [ann, bob] = players;
  const key = ground.key(8);
  await bob.page.goto(path);
  await bob.page.locator('.nt-table').waitFor();
  const panel = await openBrowse(ann.page, path);
  const manual = panel.getByRole('textbox', { name: 'Issue key to add' });
  await manual.fill(key);
  await manual.press('Enter');
  await expect(shownRound(bob.page)).toContainText(key);
  if (isPhone(ann.page)) {
    await ann.page.keyboard.press('Escape');
  }

  for (const [player, points] of [[ann, 8], [bob, 8]]) {
    await handCard(player.page, points).click();
    await player.page.getByRole('button', { name: 'Lock In' }).click();
  }
  await ann.page.getByRole('button', { name: 'Reveal Cards' }).click();
  const submit = (page) => page.locator('.jira-submit');
  await expect(submit(ann.page).getByRole('button', { name: '8', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await submit(ann.page).getByRole('button', { name: `Submit 8 pts to ${key}` }).click();
  await expect(submit(bob.page)).toHaveAttribute('data-state', 'ok');
  await expect(submit(bob.page).getByRole('status')).toHaveText(`${key} has 8 pts in Jira · submitted by Ann Submit`);

  const issue = await (await ann.page.request.get(`/api/teams/${team.id}/jira/issues/${key}`, { headers: { 'X-RS': '1' } })).json();
  expect(JSON.stringify(issue)).toContain('8');
});
