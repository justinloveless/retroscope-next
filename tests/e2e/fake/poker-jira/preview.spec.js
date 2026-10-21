import { expect, test } from '../_harness/fixtures.js';
import { BOB } from '../poker-table/_poker.js';
import { ME, R1, commands, drawer, openBrowse, openTable, round, row, sent, setupJira } from './_jira.js';

const holder = { snap: { spotlight: { holder: ME.pid, roundId: R1 } }, overlay: { own: [], heldByThisTab: true } };

test('the spotlight holder previews an issue as a pending round and commits it with Add to rounds', async ({ page, fake }) => {
  const { rounds } = setupJira(fake, holder);
  await openBrowse(page);

  await row(page, 'RS-20').locator('.jira-row-open').click();
  const { d } = await sent(fake, 'poker.round.create');
  expect(d).toEqual({ roundId: expect.any(String), ticketKey: 'RS-20', pending: true, forceNew: true });
  await expect(drawer(page).locator('.jira-preview')).toContainText('Everyone sees this issue as a preview round.');
  await expect.poll(() => rounds.find((r) => r.id === d.roundId)?.pending).toBe(true);

  await drawer(page).getByRole('button', { name: 'Add to rounds' }).click();
  expect((await sent(fake, 'poker.round.commitPending')).d).toEqual({ roundId: d.roundId });
  await expect(drawer(page).getByRole('button', { name: 'Added to the rounds' })).toBeDisabled();
  await expect.poll(() => rounds.find((r) => r.id === d.roundId)?.pending).toBe(false);

  await drawer(page).getByRole('button', { name: 'Close' }).click();
  await expect(drawer(page)).toBeHidden();
  await page.waitForTimeout(200);
  expect(commands(fake, 'poker.round.discardPending')).toHaveLength(0);
});

test('closing the drawer without adding discards the preview', async ({ page, fake }) => {
  const { rounds } = setupJira(fake, holder);
  await openBrowse(page);

  await row(page, 'RS-31').locator('.jira-row-open').click();
  const { d } = await sent(fake, 'poker.round.create');
  await expect(drawer(page).getByRole('button', { name: 'Add to rounds' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(drawer(page)).toBeHidden();
  expect((await sent(fake, 'poker.round.discardPending')).d).toEqual({ roundId: d.roundId });
  await expect.poll(() => rounds.some((r) => r.id === d.roundId)).toBe(false);
});

test('previewing another issue discards the previous preview first', async ({ page, fake }) => {
  setupJira(fake, holder);
  await openBrowse(page);

  await row(page, 'RS-20').locator('.jira-row-open').click();
  const first = await sent(fake, 'poker.round.create');
  await expect(drawer(page)).toHaveAttribute('data-key', 'RS-20');
  await row(page, 'RS-31').locator('.jira-row-open').dispatchEvent('click');
  expect((await sent(fake, 'poker.round.discardPending')).d.roundId).toBe(first.d.roundId);
  await expect.poll(() => commands(fake, 'poker.round.create').map((c) => c.d.ticketKey)).toEqual(['RS-20', 'RS-31']);
  await expect(drawer(page)).toHaveAttribute('data-key', 'RS-31');
});

test('without the spotlight, opening an issue only shows its details', async ({ page, fake }) => {
  setupJira(fake, { snap: { spotlight: { holder: BOB.pid, roundId: R1 } } });
  await openBrowse(page);

  await row(page, 'RS-20').locator('.jira-row-open').click();
  await expect(drawer(page).getByRole('button', { name: 'Edit summary: Remember the delivery address' })).toBeVisible();
  await expect(drawer(page).locator('.jira-preview')).toHaveCount(0);
  await page.waitForTimeout(200);
  expect(commands(fake, 'poker.round.create')).toHaveLength(0);
});

test('an issue already on a round is opened without a preview, even by the holder', async ({ page, fake }) => {
  setupJira(fake, { ...holder, rounds: [round(R1, 1, { ticketKey: 'RS-12' })] });
  await openTable(page);
  await page.locator('.jira-details').click();
  await expect(drawer(page)).toHaveAttribute('data-key', 'RS-12');
  await expect(drawer(page).locator('.jira-preview')).toHaveCount(0);
  expect(commands(fake, 'poker.round.create')).toHaveLength(0);
});
