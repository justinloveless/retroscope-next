import { expect, test } from '../_harness/fixtures.js';
import { ALICE, BOB, card, cardsIn, editorIn, openBoard, sent, serveBoard, snapshot, typeCard } from './_board.js';

const COMMENT = 'c0aa0000-0000-4000-8000-000000000001';

test('comments open per card, post at once and delete your own', async ({ page, fake }) => {
  const snap = snapshot({
    cards: [card(1)],
    comments: [{ id: COMMENT, cardId: card(1).id, body: 'Agreed', anonymous: false, createdAt: 1_760_000_002_000, author: { pid: BOB.pid, name: 'Bob', guest: false } }],
  });
  serveBoard(fake, snap);
  await openBoard(page, fake, { snap });
  const target = cardsIn(page, 'Good').first();
  const toggle = target.getByRole('button', { name: 'Comments (1)' });
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  const thread = target.locator('.retro-comment');
  await expect(thread).toHaveCount(1);
  await expect(thread.first().getByRole('button', { name: 'Delete comment' })).toBeHidden();

  await expect(editorIn(target)).toBeFocused();
  await page.keyboard.type('Me too');
  await page.keyboard.press('Enter');
  await expect(thread).toHaveCount(2);
  await expect(thread.nth(1).locator('.retro-comment-body')).toHaveText('Me too');
  await expect(target.getByRole('button', { name: 'Comments (2)' })).toBeVisible();
  const [d] = sent(fake, 'retro.comment.add');
  expect(d).toMatchObject({ cardId: card(1).id, body: 'Me too', anonymous: false });

  await thread.nth(1).getByRole('button', { name: 'Delete comment' }).click();
  await expect(thread).toHaveCount(1);
  expect(sent(fake, 'retro.comment.delete')).toEqual([{ commentId: d.commentId }]);
});

test('a team member is mentioned by keyboard and shows by their current name', async ({ page, fake }) => {
  const snap = snapshot({ cards: [card(1)] });
  serveBoard(fake, snap);
  await openBoard(page, fake, { snap });
  const target = cardsIn(page, 'Good').first();
  await target.getByRole('button', { name: /^Comments/ }).click();
  await expect(editorIn(target)).toBeFocused();

  await page.keyboard.type('Ask @ca');
  const options = page.getByRole('option');
  await expect(options).toHaveText(['Carol Danvers']);
  await page.keyboard.press('Backspace');
  await page.keyboard.press('Backspace');
  await page.keyboard.type('b');
  await expect(options).toHaveText(['Bob']);
  await page.keyboard.press('Enter');
  await page.keyboard.type('please');
  await page.keyboard.press('Enter');

  await expect.poll(() => sent(fake, 'retro.comment.add').map((c) => c.body)).toEqual([`Ask [[mention:${BOB.pid}:Bob]] please`]);
  const mention = target.locator('.retro-comment .mention');
  await expect(mention).toHaveText('@Bob');

  fake.tx('board:b0000000-0000-4000-8000-0000000000b1', [{
    k: 'retro.members',
    d: { members: [{ pid: ALICE.pid, name: 'Alice', role: 'owner' }, { pid: BOB.pid, name: 'Robert', role: 'member' }] },
  }]);
  await expect(mention).toHaveText('@Robert');
});

test('arrow keys choose among several people', async ({ page, fake }) => {
  const snap = snapshot();
  serveBoard(fake, snap);
  await openBoard(page, fake, { snap });
  await typeCard(page, 'Good', 'Thanks @');
  await expect(page.getByRole('option')).toHaveCount(3);
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Tab');
  await page.keyboard.press('Enter');
  await expect.poll(() => sent(fake, 'retro.card.add').map((c) => c.body)).toEqual([`Thanks [[mention:${BOB.pid}:Bob]]`]);
});

test('a refused comment comes back into the composer', async ({ page, fake }) => {
  const snap = snapshot({ cards: [card(1)] });
  await openBoard(page, fake, { snap });
  fake.onCommand('retro.comment.add', (ctx) => ctx.nack('stage_forbids'));
  const target = cardsIn(page, 'Good').first();
  await target.getByRole('button', { name: /^Comments/ }).click();
  await expect(editorIn(target)).toBeFocused();
  await page.keyboard.type('Lost words');
  await page.keyboard.press('Enter');

  await expect(page.getByRole('alert').filter({ hasText: 'The current stage does not allow that.' })).toBeVisible();
  await expect(target.locator('.retro-comment')).toHaveCount(0);
  await expect(editorIn(target)).toHaveText('Lost words');
});
