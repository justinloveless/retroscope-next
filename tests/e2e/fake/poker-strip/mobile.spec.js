import { expect, test } from '../_harness/fixtures.js';
import { openStrip, pointer, setupStrip, shownRound } from './_strip.js';

const PHONE = { width: 390, height: 844 };
const bar = (page) => page.getByRole('navigation', { name: 'Session controls' });
const table = (page) => page.locator('.nt-center');

/** A one-finger swipe across the table, as Pointer Events (pointerType touch). */
async function swipe(page, { dx, dy = 0, from = { x: 195, y: 330 }, steps = 6, cancel = false }) {
  const target = table(page);
  await pointer(target, 'pointerdown', from);
  for (let i = 1; i <= steps; i++) {
    await pointer(target, 'pointermove', { x: from.x + (dx * i) / steps, y: from.y + (dy * i) / steps });
  }
  await pointer(target, cancel ? 'pointercancel' : 'pointerup', { x: from.x + dx, y: from.y + dy });
}

test.use({ viewport: PHONE });

test('on a phone the controls sit in a bottom bar with the history button', async ({ page, fake }) => {
  setupStrip(fake);
  await openStrip(page);
  await expect(bar(page)).toBeVisible();
  for (const name of [/^Spotlight/, 'Observer mode', 'Session settings', 'Round history']) {
    await expect(bar(page).getByRole('button', { name })).toBeVisible();
  }
  await expect(page.locator('.nt-toolbar-extras .nt-control')).toHaveCount(0);
  await expect(page.locator('.nt-panel-right')).toBeHidden();
  await expect(page.getByRole('button', { name: 'Open round panel' })).toBeHidden();
  // Browse Jira joins the bar only for teams with Jira (poker-jira's specs open the sheet from it).
  await expect(bar(page).getByRole('button', { name: 'Browse Jira' })).toHaveCount(0);

  // The round tools share the toolbar's first row with Back and still fit beside the round's name.
  const rounds = page.getByRole('navigation', { name: 'Rounds' });
  for (const name of ['Previous round', 'Next round', 'Next open round', 'New round']) {
    await expect(rounds.getByRole('button', { name })).toBeInViewport({ ratio: 1 });
  }
  expect(await page.locator('.nt-toolbar').evaluate((el) => el.scrollWidth <= el.clientWidth)).toBe(true);
});

test('widening the window moves the controls back to the toolbar', async ({ page, fake }) => {
  setupStrip(fake);
  await openStrip(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  await expect(bar(page)).toBeHidden();
  await expect(page.locator('.nt-toolbar-extras .nt-control')).toHaveCount(3);
  await expect(page.locator('.nt-toolbar-extras').getByRole('button', { name: 'Session settings' })).toBeVisible();
  await page.setViewportSize(PHONE);
  await expect(bar(page).getByRole('button', { name: 'Session settings' })).toBeVisible();
});

test('swiping left opens the next round, swiping right the previous one', async ({ page, fake }) => {
  setupStrip(fake);
  await openStrip(page);
  await expect(shownRound(page)).toHaveText('Round 6 · ABC-6');
  await swipe(page, { dx: 120 });
  await expect(shownRound(page)).toHaveText('Round 5 · ABC-5');
  await swipe(page, { dx: 90, dy: 20 });
  await expect(shownRound(page)).toHaveText('Round 4 · ABC-4');
  await swipe(page, { dx: -120 });
  await expect(shownRound(page)).toHaveText('Round 5 · ABC-5');
});

test('a swipe shorter than 50 px settles back without navigating', async ({ page, fake }) => {
  setupStrip(fake);
  await openStrip(page);
  await swipe(page, { dx: 40 });
  await page.waitForTimeout(300);
  await expect(shownRound(page)).toHaveText('Round 6 · ABC-6');
  await expect.poll(() => table(page).evaluate((el) => el.style.transform)).toBe('');
});

test('the table follows the finger with damping while swiping', async ({ page, fake }) => {
  setupStrip(fake);
  await openStrip(page);
  const target = table(page);
  await pointer(target, 'pointerdown', { x: 195, y: 330 });
  await pointer(target, 'pointermove', { x: 215, y: 331 });
  await pointer(target, 'pointermove', { x: 276, y: 332 });
  // sign(dx)·|dx|^0.75 for dx = 81
  await expect.poll(() => target.evaluate((el) => el.style.transform)).toBe(`translateX(${81 ** 0.75}px)`);
  await pointer(target, 'pointercancel', { x: 276, y: 332 });
  await expect(shownRound(page)).toHaveText('Round 6 · ABC-6');
  await expect.poll(() => target.evaluate((el) => el.style.transform)).toBe('');
});

test('a mostly vertical drag scrolls the page and never changes round', async ({ page, fake }) => {
  setupStrip(fake);
  await openStrip(page);
  await swipe(page, { dx: 70, dy: 80 });
  await page.waitForTimeout(300);
  await expect(shownRound(page)).toHaveText('Round 6 · ABC-6');
});

test('a swipe that starts on the hand or its bar belongs to them', async ({ page, fake }) => {
  setupStrip(fake);
  await openStrip(page);
  const hand = page.locator('.nt-dock-bar');
  const box = await hand.boundingBox();
  const from = { x: box.x + 5, y: box.y + 5 };
  await pointer(hand, 'pointerdown', from);
  await pointer(hand, 'pointermove', { x: from.x + 60, y: from.y });
  await pointer(hand, 'pointermove', { x: from.x + 120, y: from.y });
  await pointer(hand, 'pointerup', { x: from.x + 120, y: from.y });
  await page.waitForTimeout(300);
  await expect(shownRound(page)).toHaveText('Round 6 · ABC-6');
});

test('the history sheet lists the rounds and picking one opens it', async ({ page, fake }) => {
  setupStrip(fake);
  await openStrip(page);
  await bar(page).getByRole('button', { name: 'Round history' }).click();
  const sheet = page.getByRole('dialog', { name: 'Round history' });
  await expect(sheet.locator('.nt-history-entry')).toHaveCount(6);
  await sheet.locator('.nt-history-entry').nth(2).click();
  await expect(sheet).toBeHidden();
  await expect(shownRound(page)).toHaveText('Round 3 · ABC-3');
});

test.describe('round change slide', () => {
  test.use({ viewport: { width: 1280, height: 900 } });

  test('a round change slides the table in from the side it came from', async ({ page, fake }) => {
    setupStrip(fake);
    await openStrip(page);
    // Click and read in one turn, so a slow machine cannot finish the 280 ms slide in between.
    const keyframes = await table(page).evaluate((el) => {
      document.querySelector('button[aria-label="Previous round"]').click();
      return el.getAnimations().map((a) => a.effect.getKeyframes()[0].transform);
    });
    expect(keyframes).toEqual(['translateX(-30%)']);
    await expect(shownRound(page)).toHaveText('Round 5 · ABC-5');
  });

  test('reduced motion turns the slide off', async ({ page, fake }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    setupStrip(fake);
    await openStrip(page);
    await page.keyboard.press('ArrowLeft');
    await expect(shownRound(page)).toHaveText('Round 5 · ABC-5');
    expect(await table(page).evaluate((el) => el.getAnimations().length)).toBe(0);
  });
});
