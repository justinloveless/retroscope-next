import { expect, test } from '../_harness/fixtures.js';
import { ALICE, ROOM, card, cardsIn, editorIn, openBoard, sent, serveBoard, snapshot } from './_board.js';

const MINE = { mineCards: [card(1).id], mineComments: [], myVotes: [] };

async function editConcurrently(page, fake, text) {
  const snap = snapshot({ cards: [card(1, { author: ALICE, body: 'Original' })] });
  const state = serveBoard(fake, snap);
  await openBoard(page, fake, { snap, overlay: MINE });
  const target = cardsIn(page, 'Good').first();
  await target.getByRole('button', { name: 'Edit card' }).click();
  await expect(editorIn(target)).toBeFocused();

  // Someone else's edit commits while this one is open.
  const theirs = { ...state.cards.get(card(1).id), body: 'Their text', version: 2 };
  state.cards.set(card(1).id, theirs);
  fake.tx(ROOM, [{ k: 'retro.card.edited', d: { cardId: card(1).id, body: 'Their text', version: 2 } }]);

  await page.keyboard.press('ControlOrMeta+a');
  await page.keyboard.type(text);
  await page.keyboard.press('Enter');
  return target;
}

test('a stale edit shows both texts and can replace the latest', async ({ page, fake }) => {
  const target = await editConcurrently(page, fake, 'My text');
  const dialog = page.getByRole('dialog', { name: 'This card changed while you were editing' });
  await expect(dialog).toBeVisible();
  await expect(dialog.locator('.retro-merge-pane').nth(0)).toContainText('Their text');
  await expect(dialog.locator('.retro-merge-pane').nth(1)).toContainText('My text');
  await expect(target.locator('.retro-card-body')).toHaveText('Their text');

  await dialog.getByRole('button', { name: 'Use mine' }).click();
  await expect(target.locator('.retro-card-body')).toHaveText('My text');
  await expect.poll(() => sent(fake, 'retro.card.edit')).toEqual([
    { cardId: card(1).id, body: 'My text', baseVersion: 1 },
    { cardId: card(1).id, body: 'My text', baseVersion: 2 },
  ]);
});

test('keeping the latest version sends nothing more', async ({ page, fake }) => {
  const target = await editConcurrently(page, fake, 'My text');
  await page.getByRole('dialog').getByRole('button', { name: 'Keep latest' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(target.locator('.retro-card-body')).toHaveText('Their text');
  expect(sent(fake, 'retro.card.edit')).toHaveLength(1);
});

test('editing further starts from my text against the latest version', async ({ page, fake }) => {
  const target = await editConcurrently(page, fake, 'My text');
  await page.getByRole('dialog').getByRole('button', { name: 'Edit mine' }).click();
  await expect(editorIn(target)).toHaveText('My text');
  await expect(editorIn(target)).toBeFocused();
  await page.keyboard.press('End');
  await page.keyboard.type(' and theirs');
  await page.keyboard.press('Enter');
  await expect.poll(() => sent(fake, 'retro.card.edit').at(-1)).toEqual({ cardId: card(1).id, body: 'My text and theirs', baseVersion: 2 });
  await expect(target.locator('.retro-card-body')).toHaveText('My text and theirs');
});
