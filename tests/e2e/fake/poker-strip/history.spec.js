import { expect, test } from '../_harness/fixtures.js';
import { BOB, ROOM, openStrip, roundId, rounds, setupStrip, shownRound } from './_strip.js';

const AT = Date.UTC(2026, 9, 21, 14, 2);

function history(page) {
  return page.getByRole('list', { name: 'Round history' });
}

test.beforeEach(({ isMobile }) => {
  test.skip(isMobile, 'phones show the history as a sheet (mobile.spec.js)');
});

test('the History tab lists every round with its result and who revealed it when', async ({ page, fake }) => {
  const list = rounds(4);
  list[0] = { ...list[0], state: 'revealed', winner: 5, revealedBy: BOB.pid, revealedAt: AT, jira: { points: 5, at: AT, by: BOB.pid } };
  list[1] = { ...list[1], state: 'revealed', winner: 8, revealedAt: AT };
  list[2] = { ...list[2], pending: true, pendingOwner: BOB.pid };
  setupStrip(fake, { snap: { rounds: list, currentRoundId: roundId(4) } });
  await openStrip(page);

  await expect(page.getByRole('tab', { name: 'History' })).toHaveAttribute('aria-selected', 'true');
  const time = await page.evaluate((at) => new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' }).format(at), AT);
  const entries = history(page).locator('.nt-history-entry');
  await expect(entries).toHaveCount(4);
  await expect(entries.nth(0)).toContainText('Round 1 · ABC-1');
  await expect(entries.nth(0)).toContainText('5 pts');
  await expect(entries.nth(0)).toContainText(`Revealed by Bob · ${time}`);
  await expect(entries.nth(0)).toContainText('Jira: 5 pts');
  await expect(entries.nth(1)).toContainText(`Revealed automatically · ${time}`);
  await expect(entries.nth(2)).toContainText('Preview');
  await expect(entries.nth(3)).toContainText('Voting');
  await expect(entries.nth(3)).toHaveAttribute('aria-current', 'true');
});

test('the list follows reveals live and an entry opens its round', async ({ page, fake }) => {
  setupStrip(fake);
  await openStrip(page);
  const entries = history(page).locator('.nt-history-entry');
  await entries.nth(1).click();
  await expect(shownRound(page)).toHaveText('Round 2 · ABC-2');
  await expect(entries.nth(1)).toHaveAttribute('aria-current', 'true');

  fake.tx(ROOM, [{ k: 'poker.round.revealed', d: { roundId: roundId(6), votes: [{ pid: BOB.pid, points: 3, abstained: false }], winner: 3, at: AT, by: BOB.pid } }]);
  await expect(entries.nth(5)).toContainText('3 pts');
  await expect(entries.nth(5)).toContainText('Revealed by Bob');
  fake.tx(ROOM, [{ k: 'poker.jira.points', d: { roundId: roundId(6), key: 'ABC-6', points: 3, status: 'pending', by: BOB.pid } }]);
  await expect(entries.nth(5)).toContainText('Jira: sending 3 pts…');
});

test('the panel collapses to a tab on the screen edge, remembers it, and reopens', async ({ page, fake }) => {
  setupStrip(fake);
  await openStrip(page);
  await expect(page.getByRole('button', { name: 'Collapse panel' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Collapse round panel' }).click();
  await expect(history(page)).toBeHidden();
  const open = page.getByRole('button', { name: 'Open round panel' });
  await expect(open).toBeFocused();
  await expect(open).toHaveAttribute('aria-expanded', 'false');
  await expect(open).toHaveAttribute('aria-controls', 'nt-panel-right');
  const box = await open.boundingBox();
  expect(Math.round(box.x + box.width)).toBe(page.viewportSize().width);
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('rs.panels')).right.open)).toBe(false);

  await page.reload();
  await page.locator('.nt-chip').first().waitFor();
  await expect(history(page)).toBeHidden();
  await page.getByRole('button', { name: 'Open round panel' }).click();
  await expect(history(page)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Collapse round panel' })).toBeFocused();
});

test('the panel resizes between 240 and 480 px with the keyboard and keeps its width', async ({ page, fake }) => {
  setupStrip(fake);
  await openStrip(page);
  const handle = page.getByRole('separator', { name: 'Resize round panel' });
  await handle.focus();
  for (let i = 0; i < 20; i++) {
    await page.keyboard.press('ArrowLeft');
  }
  await expect(handle).toHaveAttribute('aria-valuenow', '480');
  expect(Math.round((await page.locator('.nt-round-panel').boundingBox()).width)).toBe(480);
  await page.reload();
  await page.locator('.nt-chip').first().waitFor();
  await expect(page.getByRole('separator', { name: 'Resize round panel' })).toHaveAttribute('aria-valuenow', '480');
});
