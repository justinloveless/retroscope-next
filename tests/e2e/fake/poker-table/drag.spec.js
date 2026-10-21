import { expect, test } from '../_harness/fixtures.js';
import { ME, R1, handCard, lastCommand, openTable, seat, setupPoker } from './_poker.js';

async function centre(locator) {
  await locator.scrollIntoViewIfNeeded();
  const box = await locator.boundingBox();
  return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
}

// The lower edge of the table, which stays on screen once the hand is scrolled into view on a phone.
async function tableTarget(page) {
  const box = await page.locator('.nt-field').boundingBox();
  return { x: box.x + box.width / 2, y: box.y + box.height - 16 };
}

test('hand cards opt out of touch panning so a touch drag is not cancelled', async ({ page, fake }) => {
  setupPoker(fake);
  await openTable(page);
  for (const points of [1, 5, 21]) {
    await expect(handCard(page, points)).toHaveCSS('touch-action', 'none');
  }
});

test('dragging a card onto the table plays it: vote and lock in one command', async ({ page, fake }) => {
  setupPoker(fake);
  await openTable(page);
  const from = await centre(handCard(page, 8));
  const to = await tableTarget(page);

  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await page.mouse.move(from.x + 4, from.y - 4);
  await expect(page.locator('.nt-float')).toHaveCount(0);
  await page.mouse.move(from.x + 12, from.y - 40, { steps: 3 });
  await expect(page.locator('.nt-float')).toBeVisible();
  await expect(page.locator('.nt-drop')).toHaveText('Drop card here');
  await expect(handCard(page, 8)).toHaveClass(/is-drag-source/);
  await page.mouse.move(to.x, to.y, { steps: 5 });
  await expect(page.locator('.nt-drop')).toHaveText('Release to lock in!');
  await page.mouse.up();

  expect((await lastCommand(fake, 'poker.vote.set')).d).toEqual({ roundId: R1, points: 8, lock: true });
  await expect(page.locator('.nt-float')).toHaveCount(0);
  await expect(page.locator('.nt-drop')).toHaveCount(0);
  await expect(seat(page, ME)).toHaveAttribute('data-status', 'locked');
});

test('releasing away from the table plays nothing', async ({ page, fake }) => {
  setupPoker(fake);
  await openTable(page);
  const from = await centre(handCard(page, 3));
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  // Firefox drops a release outside the viewport, and the strip and docked header push the hand near its edge.
  await page.mouse.move(from.x, Math.min(from.y + 60, page.viewportSize().height - 2), { steps: 4 });
  await page.mouse.up();
  await expect(page.locator('.nt-float')).toHaveCount(0);
  expect(fake.received.filter(({ frame }) => frame.t === 'cmd')).toEqual([]);
});

test('holding a card over a side zone for 2 s plays "between" and locks it in', async ({ page, fake }) => {
  setupPoker(fake);
  await openTable(page);
  const from = await centre(handCard(page, 5));
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await page.mouse.move(from.x, from.y - 20, { steps: 3 });

  const left = page.locator('.nt-zone-left');
  const right = page.locator('.nt-zone-right');
  await expect(left).toContainText('Between 3 & 5');
  await expect(right).toContainText('Between 5 & 8');

  const target = await centre(right);
  expect(target.y).toBeLessThan(page.viewportSize().height);
  await page.mouse.move(target.x, target.y, { steps: 4 });
  await expect(right).toHaveClass(/is-holding/);
  await expect(right.locator('.nt-zone-pill')).toHaveText(/^[12]s — hold$/);
  await expect(page.locator('.nt-float')).toHaveClass(/is-charging/);
  const started = Date.now();

  const command = await fake.waitForFrame((f) => f.t === 'cmd' && f.k === 'poker.vote.set', { timeout: 4000 });
  expect(Date.now() - started).toBeGreaterThan(1500);
  expect(command.frame.d).toEqual({ roundId: R1, points: 5, high: 8, lock: true });
  await expect(page.locator('.nt-zone')).toHaveCount(0);
  await page.mouse.up();
  await expect(page.locator('.nt-hand-card.is-between')).toHaveAttribute('aria-label', 'Between 5 and 8');
  expect(fake.received.filter(({ frame }) => frame.k === 'poker.vote.set')).toHaveLength(1);
});

test('leaving a side zone before 2 s resets the hold', async ({ page, fake }) => {
  setupPoker(fake);
  await openTable(page);
  const from = await centre(handCard(page, 5));
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await page.mouse.move(from.x, from.y - 20, { steps: 3 });
  const left = await centre(page.locator('.nt-zone-left'));
  await page.mouse.move(left.x, left.y, { steps: 3 });
  await page.waitForTimeout(1200);
  await page.mouse.move(from.x, from.y - 200, { steps: 3 });
  await expect(page.locator('.nt-zone-left')).not.toHaveClass(/is-holding/);
  await page.waitForTimeout(1200);
  await page.keyboard.press('Escape');
  await expect(page.locator('.nt-float')).toHaveCount(0);
  await page.mouse.up();
  expect(fake.received.filter(({ frame }) => frame.t === 'cmd')).toEqual([]);
});

test('a locked hand cannot be dragged', async ({ page, fake }) => {
  setupPoker(fake);
  await openTable(page);
  await handCard(page, 2).click();
  await page.getByRole('button', { name: 'Lock In' }).click();
  await expect(seat(page, ME)).toHaveAttribute('data-status', 'locked');
  const from = await centre(handCard(page, 13));
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await page.mouse.move(from.x, from.y - 80, { steps: 4 });
  await expect(page.locator('.nt-float')).toHaveCount(0);
  await page.mouse.up();
});
