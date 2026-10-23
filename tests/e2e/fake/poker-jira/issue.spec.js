// The viewed round's issue at the centre of the stage (docs/followups/UX1.md): always shown, scrolled by the
// stage clear of the docked hand, edited as in the drawer, and driven by navigation and the spotlight.
import { expect, test } from '../_harness/fixtures.js';
import { BOB, R2 } from '../poker-table/_poker.js';
import { R1, ROOM, issueView, openTable, round, setupJira } from './_jira.js';

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

const paragraph = (v) => ({ t: 'p', c: [{ t: 'text', v }] });

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

test('a long description scrolls fully clear of the hand', async ({ page, fake }) => {
  setupJira(fake, { rounds: [round(R1, 1, { ticketKey: 'RS-30' })] });
  const lines = Array.from({ length: 60 }, (_, i) => paragraph(`Line ${i + 1} of a long description.`));
  fake.route('GET', '/api/teams/:teamId/jira/issues/RS-30', {
    body: detail('RS-30', { description: { t: 'doc', c: [...lines, paragraph('The very last line.')] } }),
  });
  await openTable(page);
  const last = issueView(page).getByText('The very last line.');
  await expect(last).toBeAttached();
  await page.mouse.move(0, 0);

  const scroller = page.locator('.nt-issue');
  const { padding, dock } = await scroller.evaluate((el) => ({
    padding: parseFloat(getComputedStyle(el).paddingBottom),
    dock: document.querySelector('.nt-dock').offsetHeight,
  }));
  expect(Math.abs(padding - (dock + 24))).toBeLessThanOrEqual(1);

  await scroller.evaluate((el) => {
    el.scrollTop = el.scrollHeight;
  });
  await expect.poll(async () => {
    const text = await last.boundingBox();
    const hand = await page.locator('.nt-hand-card').evaluateAll((cards) => Math.min(...cards.map((c) => c.getBoundingClientRect().top)));
    const bar = await page.locator('.nt-dock-bar').boundingBox();
    return text.y + text.height <= Math.min(hand, bar.y);
  }).toBe(true);
});

test('the centre follows the viewed round and offers to link an issue when there is none', async ({ page, fake }) => {
  setupJira(fake, { rounds: [round(R1, 1, { ticketKey: 'RS-12' }), round(R2, 2)] });
  await openTable(page);
  await expect(issueView(page)).toHaveAttribute('data-key', 'RS-12');
  await expect(issueView(page).getByRole('button', { name: /^Edit summary/ })).toHaveText('Pay with a saved card');

  await page.getByRole('button', { name: 'Next round' }).click();
  await expect(issueView(page).getByText('No issue linked')).toBeVisible();
  await issueView(page).getByRole('button', { name: 'Link a Jira issue' }).click();
  await expect(page.getByRole('textbox', { name: 'Ticket key' })).toBeFocused();
});

test('a spotlight move shows its round’s issue at the centre', async ({ page, fake }) => {
  setupJira(fake, {
    rounds: [round(R1, 1, { ticketKey: 'RS-12' }), round(R2, 2, { ticketKey: 'RS-20' })],
    snap: { spotlight: { holder: BOB.pid, roundId: R1 } },
  });
  await openTable(page);
  await expect(issueView(page)).toHaveAttribute('data-key', 'RS-12');
  fake.tx(ROOM, [{ k: 'poker.spotlight', d: { holder: BOB.pid, roundId: R2 } }]);
  await expect(page).toHaveURL(/[?&]round=2$/);
  await expect(issueView(page)).toHaveAttribute('data-key', 'RS-20');
  await expect(issueView(page).getByRole('button', { name: /^Edit summary/ })).toHaveText('Remember the delivery address');
});

test('the description is edited in place, keeping its islands and sending descriptionBase', async ({ page, fake }) => {
  const { jira } = setupJira(fake, { rounds: [round(R1, 1, { ticketKey: 'RS-12' })] });
  await openTable(page);
  const view = issueView(page);
  await expect(view).toHaveAttribute('data-key', 'RS-12');
  const before = jira.issues.get('RS-12').updated;

  await view.getByRole('button', { name: 'Edit description' }).click();
  const islands = view.locator('.jira-description-editor .island');
  await expect(islands).toHaveCount(6);
  for (const island of await islands.all()) {
    await expect(island).toHaveAttribute('contenteditable', 'false');
  }
  await caretInLastItem(page);
  await page.keyboard.type('Then ');
  await view.getByRole('button', { name: 'Save', exact: true }).click();

  await expect(view.locator('.jira-description li').last()).toHaveText('Then asks for the CVC');
  const patch = jira.calls.filter((c) => c.method === 'PATCH').at(-1).body;
  expect(patch.descriptionBase).toBe(before);
  expect(patch.description).toContain('[[island:t0b1e5c0ffee]]');
});

test('a description edited in Jira meanwhile is refused as stale and the centre reloads', async ({ page, fake }) => {
  const { jira } = setupJira(fake, { rounds: [round(R1, 1, { ticketKey: 'RS-12' })] });
  await openTable(page);
  const view = issueView(page);
  await expect(view).toHaveAttribute('data-key', 'RS-12');

  await view.getByRole('button', { name: 'Edit description' }).click();
  await caretInLastItem(page);
  await page.keyboard.type('Later ');
  jira.touch('RS-12', { summary: 'Pay with a saved card (edited in Jira)' });
  await view.getByRole('button', { name: 'Save', exact: true }).click();

  await expect(page.getByText(/RS-12 changed in Jira meanwhile/)).toBeVisible();
  await expect(view.getByRole('button', { name: /^Edit summary/ })).toHaveText('Pay with a saved card (edited in Jira)');
  await expect(view.locator('.jira-description-editor')).toHaveCount(0);
});
