import { expect, test } from '../_harness/fixtures.js';
import { card, cardsIn, column, editorIn, openBoard, sent, serveBoard, snapshot, typeCard } from './_board.js';

test('a half-written card survives a reload', async ({ page, fake }) => {
  await openBoard(page, fake);
  await typeCard(page, 'Bad', 'Builds are slow');
  await column(page, 'Bad').getByLabel('Post anonymously').check();
  await page.reload();
  await expect(page.locator('.retro-column')).toHaveCount(3);

  await expect(editorIn(column(page, 'Bad'))).toHaveText('Builds are slow');
  await expect(column(page, 'Bad').getByLabel('Post anonymously')).toBeChecked();
  await expect(column(page, 'Good').locator('.rs-editor-input')).toHaveCount(0);
});

test('switching away from the tab and back keeps the composer as it was', async ({ page, fake }) => {
  await openBoard(page, fake);
  await typeCard(page, 'Good', 'Draft in progress');
  await page.evaluate(() => {
    for (const state of ['hidden', 'visible']) {
      Object.defineProperty(document, 'visibilityState', { value: state, configurable: true });
      document.dispatchEvent(new Event('visibilitychange'));
      window.dispatchEvent(new Event(state === 'hidden' ? 'blur' : 'focus'));
    }
  });
  await expect(editorIn(column(page, 'Good'))).toHaveText('Draft in progress');
});

test('a comment draft is kept per card', async ({ page, fake }) => {
  const snap = snapshot({ cards: [card(1), card(2)] });
  await openBoard(page, fake, { snap });
  const first = cardsIn(page, 'Good').nth(0);
  await first.getByRole('button', { name: /^Comments/ }).click();
  await expect(editorIn(first)).toBeFocused();
  await page.keyboard.type('Unsent thought');
  await page.reload();

  const again = cardsIn(page, 'Good').nth(0);
  await again.getByRole('button', { name: /^Comments/ }).click();
  await expect(editorIn(again)).toHaveText('Unsent thought');
  const second = cardsIn(page, 'Good').nth(1);
  await second.getByRole('button', { name: /^Comments/ }).click();
  await expect(editorIn(second)).toHaveText('');
});

test('a refused card goes back into its composer and the draft', async ({ page, fake }) => {
  await openBoard(page, fake);
  fake.onCommand('retro.card.add', (ctx) => ctx.nack('stage_forbids'));
  await typeCard(page, 'Good', 'Refused idea');
  await page.keyboard.press('Enter');

  await expect(page.getByRole('alert').filter({ hasText: 'The current stage does not allow that.' })).toBeVisible();
  await expect(cardsIn(page, 'Good')).toHaveCount(0);
  await expect(editorIn(column(page, 'Good'))).toHaveText('Refused idea');
  await page.reload();
  await expect(editorIn(column(page, 'Good'))).toHaveText('Refused idea');
});

test('a sent card clears the draft', async ({ page, fake }) => {
  serveBoard(fake);
  await openBoard(page, fake);
  await typeCard(page, 'Good', 'Done deal');
  await page.keyboard.press('Enter');
  await expect(cardsIn(page, 'Good')).toHaveCount(1);
  await expect.poll(() => sent(fake, 'retro.card.add').length).toBe(1);
  await page.reload();
  await expect(page.locator('.retro-column')).toHaveCount(3);
  await expect(column(page, 'Good').locator('.retro-add-card')).toBeVisible();
  await expect(column(page, 'Good').locator('.rs-editor-input')).toHaveCount(0);
});
