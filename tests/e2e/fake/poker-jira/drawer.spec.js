import { readFileSync } from 'node:fs';
import { expect, test } from '../_harness/fixtures.js';
import { DEFAULT_JIRA_FIXTURE } from '../../../web-fake-server/jira.js';
import { R1, SITE, commands, drawer, isPhone, jiraRequests, openBrowse, openTable, round, row, sent, setupJira } from './_jira.js';

const ISLAND = '[[island:t0b1e5c0ffee]]';
const issueGets = (fake, key) => jiraRequests(fake, 'GET', new RegExp(`/jira/issues/${key}$`));

async function openIssue(page, key) {
  await row(page, key).locator('.jira-row-open').click();
  await expect(drawer(page)).toHaveAttribute('data-key', key);
  await expect(drawer(page).locator('.jira-loading')).toHaveCount(0);
  return drawer(page);
}

/** Puts the caret at the start of the editor's last list item. */
async function caretInLastItem(page) {
  await page.evaluate(() => {
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
}

test('hovering a row for 600 ms prefetches the issue, and opening it then needs no second request', async ({ page, fake }) => {
  test.skip(isPhone(page), 'hover prefetch is a pointer affordance');
  setupJira(fake);
  await openBrowse(page);
  const target = row(page, 'RS-20').locator('.jira-row-open');
  const box = await target.boundingBox();
  const away = await page.locator('.nt-table').boundingBox();

  await page.mouse.move(box.x + 10, box.y + 10);
  await page.waitForTimeout(300);
  await page.mouse.move(away.x + away.width / 2, away.y + away.height / 2);
  await page.waitForTimeout(600);
  expect(issueGets(fake, 'RS-20')).toHaveLength(0);

  await page.mouse.move(box.x + 12, box.y + 12);
  await page.waitForTimeout(400);
  expect(issueGets(fake, 'RS-20')).toHaveLength(0);
  await expect.poll(() => issueGets(fake, 'RS-20').length, { timeout: 2000 }).toBe(1);
  await target.click();
  await expect(drawer(page).getByRole('button', { name: /Edit summary/ })).toHaveText('Remember the delivery address');
  expect(issueGets(fake, 'RS-20')).toHaveLength(1);
});

test('the drawer shows the issue: fields, island-rich description, attachments, links and comments', async ({ page, fake }) => {
  setupJira(fake);
  await openBrowse(page);
  const sheet = await openIssue(page, 'RS-12');

  await expect(sheet.locator('.jira-field-status')).toContainText('To Do');
  await expect(sheet.locator('.jira-field-type')).toContainText('Story');
  await expect(sheet.locator('.jira-field-priority')).toContainText('Medium');
  await expect(sheet.locator('.jira-field-assignee')).toHaveText('Ada Fake');
  await expect(sheet.locator('.jira-field-sprint')).toHaveText('Sprint 9');
  await expect(sheet.locator('.jira-description strong')).toHaveText('pay with a saved card');
  await expect(sheet.locator('.jira-description .island td').first()).toHaveText('Visa');
  await expect(sheet.locator('.jira-comment')).toHaveCount(2);
  await expect(sheet.getByRole('heading', { name: 'Comments (2)' })).toBeVisible();
  await expect(sheet.locator('.jira-comment').first()).toContainText('Amex');
  await expect(sheet.getByRole('link', { name: 'Open RS-12 in Jira' })).toHaveAttribute('href', `https://${SITE}/browse/RS-12`);
  const thumb = sheet.getByRole('button', { name: 'View screenshot.png' }).locator('img');
  await expect(thumb).toHaveAttribute('src', /^\/api\/teams\/[^/]+\/jira\/issues\/RS-12\/attachments\/20001$/);
});

test('field edits go through the proxy and the drawer reloads', async ({ page, fake }) => {
  const { jira } = setupJira(fake);
  await openBrowse(page);
  const sheet = await openIssue(page, 'RS-12');
  const call = (method, route) => jira.calls.filter((c) => c.method === method && c.route === route).at(-1);

  await sheet.getByRole('button', { name: /^Edit summary/ }).click();
  const title = sheet.getByRole('textbox', { name: 'Summary' });
  await title.fill('Pay with a stored card');
  await title.press('Enter');
  await expect(sheet.getByRole('button', { name: /^Edit summary/ })).toHaveText('Pay with a stored card');
  expect(call('PATCH', '/issues/:key').body).toEqual({ summary: 'Pay with a stored card' });

  await sheet.locator('.jira-field-status').click();
  await page.getByRole('menuitem', { name: 'In Progress' }).click();
  await expect(sheet.locator('.jira-field-status')).toContainText('In Progress');
  expect(call('POST', '/issues/:key/transition').body).toEqual({ transitionId: '21' });

  await sheet.locator('.jira-field-priority').click();
  await page.getByRole('menuitem', { name: 'Highest' }).click();
  await expect(sheet.locator('.jira-field-priority')).toContainText('Highest');
  expect(call('PATCH', '/issues/:key').body).toEqual({ priorityId: '1' });

  await sheet.locator('.jira-field-type').click();
  await page.getByRole('menuitem', { name: 'Bug' }).click();
  await expect(sheet.locator('.jira-field-type')).toContainText('Bug');

  await sheet.locator('.jira-field-assignee').click();
  await page.getByRole('searchbox', { name: 'Search assignable people' }).fill('grace');
  await page.getByRole('option', { name: 'Grace Fixture' }).click();
  await expect(sheet.locator('.jira-field-assignee')).toHaveText('Grace Fixture');
  expect(call('PATCH', '/issues/:key').body).toEqual({ assigneeAccountId: 'fake-acc-2' });

  await sheet.locator('.jira-field-points').click();
  const points = page.getByRole('spinbutton', { name: 'Story points' });
  await points.fill('3.5');
  await points.press('Enter');
  await expect(sheet.locator('.jira-field-points')).toHaveText('3.5');
  expect(call('PUT', '/issues/:key/points').body).toEqual({ points: 3.5 });

  await sheet.locator('.jira-field-sprint').click();
  await page.getByRole('menuitem', { name: 'Sprint 10' }).click();
  await expect(sheet.locator('.jira-field-sprint')).toHaveText('Sprint 10');
  expect(call('PUT', '/issues/:key/sprint').body).toEqual({ sprintId: 102 });
  await sheet.locator('.jira-field-sprint').click();
  await page.getByRole('menuitem', { name: 'Backlog' }).click();
  await expect(sheet.locator('.jira-field-sprint')).toHaveText('Backlog');
  expect(call('PUT', '/issues/:key/sprint').body).toEqual({ sprintId: null });
});

test('editing the description keeps its islands and sends descriptionBase', async ({ page, fake }) => {
  const { jira } = setupJira(fake);
  await openBrowse(page);
  const sheet = await openIssue(page, 'RS-12');
  const before = jira.issues.get('RS-12').updated;

  await sheet.getByRole('button', { name: 'Edit description' }).click();
  await expect(sheet.locator('.jira-description-editor .island td').first()).toHaveText('Visa');
  await expect(sheet.locator('.jira-description-editor .island')).toHaveAttribute('contenteditable', 'false');
  await caretInLastItem(page);
  await page.keyboard.type('Then ');
  await sheet.getByRole('button', { name: 'Save', exact: true }).click();

  await expect(sheet.locator('.jira-description li').last()).toHaveText('Then asks for the CVC');
  const patch = jira.calls.filter((c) => c.method === 'PATCH').at(-1).body;
  expect(patch.descriptionBase).toBe(before);
  expect(patch.description).toContain(ISLAND);
  expect(patch.description).toContain('- Then asks for the CVC');
  expect(patch.description).toContain('**pay with a saved card**');
  await expect(sheet.locator('.jira-description .island td').first()).toHaveText('Visa');
});

test('a description edited in Jira meanwhile is refused as stale and the drawer reloads', async ({ page, fake }) => {
  const { jira } = setupJira(fake);
  await openBrowse(page);
  const sheet = await openIssue(page, 'RS-12');
  const original = jira.issues.get('RS-12').description;

  await sheet.getByRole('button', { name: 'Edit description' }).click();
  await caretInLastItem(page);
  await page.keyboard.type('Later ');
  jira.touch('RS-12', { summary: 'Pay with a saved card (edited in Jira)' });
  await sheet.getByRole('button', { name: 'Save', exact: true }).click();

  await expect(page.getByText(/RS-12 changed in Jira meanwhile/)).toBeVisible();
  await expect(sheet.getByRole('button', { name: /^Edit summary/ })).toHaveText('Pay with a saved card (edited in Jira)');
  await expect(sheet.locator('.jira-description-editor')).toHaveCount(0);
  await expect(sheet.locator('.jira-description li').last()).toHaveText('asks for the CVC');
  expect(jira.issues.get('RS-12').description).toBe(original);
});

const detail = (key, extra) => ({
  key,
  updated: '2026-10-12T11:00:00.000Z',
  fields: { summary: `Summary of ${key}`, status: 'To Do', statusCategory: 'new', issueType: 'Story', priority: 'High', labels: [], points: 5 },
  islandIds: [],
  comments: [],
  commentTotal: 0,
  attachments: [],
  links: [],
  ...extra,
});

test('a description the server marks read-only, or one without a markup form, offers no editing', async ({ page, fake }) => {
  setupJira(fake, { rounds: [round(R1, 1, { ticketKey: 'RS-13' })] });
  fake.route('GET', '/api/teams/:teamId/jira/issues/RS-13', {
    body: detail('RS-13', {
      editable: false,
      description: { t: 'doc', c: [{ t: 'p', c: [{ t: 'text', v: 'Read me only.' }] }] },
      links: [{ id: '700', type: 'Relates', direction: 'outward', key: 'RS-14', summary: 'Fix rounding of totals' }],
    }),
  });
  fake.route('GET', '/api/teams/:teamId/jira/issues/RS-14', {
    body: detail('RS-14', { description: { t: 'doc', c: [{ t: 'panel', kind: 'info', c: [{ t: 'p', c: [{ t: 'text', v: 'A bare panel.' }] }] }] } }),
  });
  await openTable(page);

  await page.locator('.jira-details').click();
  const sheet = drawer(page);
  await expect(sheet.locator('.jira-description')).toContainText('Read me only.');
  await expect(sheet.getByRole('button', { name: 'Edit description' })).toHaveCount(0);
  await expect(sheet.locator('.jira-summary-edit')).toBeVisible();

  await sheet.locator('.jira-link[data-key="RS-14"]').getByRole('button', { name: 'RS-14', exact: true }).click();
  await expect(sheet).toHaveAttribute('data-key', 'RS-14');
  await expect(sheet.locator('.jira-description .panel-info')).toHaveText('A bare panel.');
  await expect(sheet.getByRole('button', { name: 'Edit description' })).toHaveCount(0);
});

test('comments page in 20s and a new comment goes to the top', async ({ page, fake }) => {
  const fixture = JSON.parse(readFileSync(DEFAULT_JIRA_FIXTURE, 'utf8'));
  const issue = fixture.issues.find((i) => i.key === 'RS-12');
  issue.comments = Array.from({ length: 25 }, (_, i) => ({ id: String(40000 + i), author: 'Grace Fixture', created: '2026-10-13T08:00:00.000Z', body: `Comment number ${i + 1}` }));
  const { jira } = setupJira(fake, { fixture });
  await openBrowse(page);
  const sheet = await openIssue(page, 'RS-12');

  await expect(sheet.locator('.jira-comment')).toHaveCount(20);
  await expect(sheet.getByRole('heading', { name: 'Comments (25)' })).toBeVisible();
  await sheet.getByRole('button', { name: 'Show more comments (5)' }).click();
  await expect(sheet.locator('.jira-comment')).toHaveCount(25);
  await expect(sheet.locator('.jira-comment').last()).toContainText('Comment number 25');
  expect(jiraRequests(fake, 'GET', /\/issues\/RS-12\/comments$/).map((r) => r.query.get('startAt'))).toEqual(['20']);
  await expect(sheet.locator('.jira-more-comments')).toHaveCount(0);

  const box = sheet.getByRole('textbox', { name: 'Add a comment' });
  await box.click();
  await page.keyboard.type('Looks good, ship it');
  await page.keyboard.press('Enter');
  await expect(sheet.locator('.jira-comment').first()).toContainText('Looks good, ship it');
  expect(jira.calls.filter((c) => c.method === 'POST' && c.route === '/issues/:key/comments').map((c) => c.body)).toEqual([{ body: 'Looks good, ship it' }]);
  await expect(box).toHaveText('');
  await expect(sheet.getByRole('heading', { name: 'Comments (26)' })).toBeVisible();
});

test('links are added with a type and removed', async ({ page, fake }) => {
  const { jira } = setupJira(fake);
  await openBrowse(page);
  const sheet = await openIssue(page, 'RS-12');

  await sheet.locator('.jira-link-type').click();
  await page.getByRole('menuitem', { name: 'is blocked by' }).click();
  await expect(sheet.locator('.jira-link-type')).toHaveText('is blocked by');
  await sheet.getByRole('textbox', { name: 'Issue key to link' }).fill('rs-13');
  await sheet.getByRole('button', { name: 'Link', exact: true }).click();
  await expect(sheet.locator('.jira-link[data-key="RS-13"]')).toContainText('Show order summary before paying');
  expect(jira.calls.find((c) => c.method === 'POST' && c.route === '/issues/:key/links').body).toEqual({ typeId: '10000', direction: 'inward', targetKey: 'RS-13' });

  await sheet.getByRole('textbox', { name: 'Issue key to link' }).fill('RS-12');
  await sheet.getByRole('button', { name: 'Link', exact: true }).click();
  await expect(page.getByText('An issue cannot link to itself.')).toBeVisible();

  await sheet.getByRole('button', { name: 'Remove link to RS-13' }).click();
  await expect(sheet.locator('.jira-link')).toHaveCount(0);
  expect(jira.calls.some((c) => c.method === 'DELETE' && c.route === '/links/:linkId')).toBe(true);
});

test('Clone creates a copy in Jira and adds it to the rounds', async ({ page, fake }) => {
  const { jira } = setupJira(fake);
  await openBrowse(page);
  const sheet = await openIssue(page, 'RS-12');

  await sheet.getByRole('button', { name: 'Clone' }).click();
  const dialog = page.getByRole('dialog', { name: 'Clone RS-12' });
  await expect(dialog.getByRole('textbox', { name: 'Summary' })).toHaveValue('Clone: Pay with a saved card');
  await dialog.getByRole('button', { name: 'Clone' }).click();
  await expect(dialog).toBeHidden();
  expect(jira.calls.find((c) => c.method === 'POST' && c.route === '/issues').body).toEqual({ summary: 'Clone: Pay with a saved card', cloneFrom: 'RS-12' });
  expect((await sent(fake, 'poker.round.create')).d.ticketKey).toBe('RS-32');
  expect(jira.issues.get('RS-32').description).toContain(ISLAND);
});

test('images open in a lightbox served by the attachment proxy', async ({ page, fake }) => {
  setupJira(fake);
  await openBrowse(page);
  const sheet = await openIssue(page, 'RS-12');

  await sheet.getByRole('button', { name: 'View screenshot.png' }).click();
  const box = page.getByRole('dialog', { name: /screenshot\.png/ });
  await expect(box).toBeVisible();
  await expect(box.locator('img')).toHaveAttribute('src', /\/jira\/issues\/RS-12\/attachments\/20001$/);
  await expect.poll(() => box.locator('img').evaluate((img) => img.complete && img.naturalWidth)).toBe(1);
  await page.keyboard.press('Escape');
  await expect(box).toBeHidden();
  await expect(sheet).toBeVisible();
});

test('without edit rights the drawer is read-only', async ({ page, fake }) => {
  setupJira(fake, { membersCanEdit: false });
  await openBrowse(page);
  const sheet = await openIssue(page, 'RS-12');

  await expect(sheet.locator('.jira-summary-edit')).toHaveCount(0);
  await expect(sheet.locator('button.jira-field-status')).toHaveCount(0);
  await expect(sheet.locator('span.jira-field-status')).toContainText('To Do');
  await expect(sheet.getByRole('button', { name: 'Edit description' })).toHaveCount(0);
  await expect(sheet.getByRole('button', { name: 'Clone' })).toHaveCount(0);
  await expect(sheet.getByRole('textbox', { name: 'Add a comment' })).toHaveCount(0);
  await expect(sheet.locator('.jira-link-form')).toHaveCount(0);
});

test('Issue details in the toolbar opens the viewed round’s issue; a missing issue says so', async ({ page, fake }) => {
  setupJira(fake, { rounds: [round(R1, 1, { ticketKey: 'RS-99' })] });
  await openTable(page);
  await page.locator('.jira-details').click();
  await expect(drawer(page).locator('.jira-error')).toHaveText('Jira could not find that issue.');
  expect(commands(fake, 'poker.round.create')).toHaveLength(0);
});
