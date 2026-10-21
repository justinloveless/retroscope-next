import { expect, test } from '../_harness/fixtures.js';
import { ALICE, BOB, card, cardsIn, column, editorIn, openBoard, sent, serveBoard, snapshot, typeCard } from './_board.js';

test('a new card shows at once and stays when the server confirms it', async ({ page, fake }) => {
  let release;
  const held = new Promise((done) => {
    release = done;
  });
  const state = serveBoard(fake);
  const add = fake.commandHandlers.find((h) => h.kind === 'retro.card.add').handler;
  fake.onCommand('retro.card.add', async (ctx) => {
    await held;
    add(ctx);
  });
  await openBoard(page, fake);

  await typeCard(page, 'Good', 'Shipped the ');
  await page.keyboard.press('ControlOrMeta+b');
  await page.keyboard.type('release');
  await page.keyboard.press('Enter');
  const cards = cardsIn(page, 'Good');
  await expect(cards).toHaveCount(1);
  await expect(cards.first().locator('.retro-card-body strong')).toHaveText('release');
  await expect(cards.first().locator('.retro-author-name')).toHaveText('Alice');

  release();
  await expect.poll(() => state.cards.size).toBe(1);
  await expect(cards).toHaveCount(1);
  const [d] = sent(fake, 'retro.card.add');
  expect(d).toMatchObject({ body: 'Shipped the **release**', anonymous: false });
  expect(d.cardId).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
  await expect(editorIn(column(page, 'Good'))).toBeFocused();
});

test('editing your own card sends the new body against its version', async ({ page, fake }) => {
  const snap = snapshot({ cards: [card(1, { author: ALICE, body: 'Old text' })] });
  serveBoard(fake, snap);
  await openBoard(page, fake, { snap, overlay: { mineCards: [card(1).id], mineComments: [], myVotes: [] } });

  const target = cardsIn(page, 'Good').first();
  await target.getByRole('button', { name: 'Edit card' }).click();
  const input = editorIn(target);
  await expect(input).toBeFocused();
  await page.keyboard.press('ControlOrMeta+a');
  await page.keyboard.type('New text');
  await page.keyboard.press('Enter');
  await expect(target.locator('.retro-card-body')).toHaveText('New text');
  await expect.poll(() => sent(fake, 'retro.card.edit')).toEqual([{ cardId: card(1).id, body: 'New text', baseVersion: 1 }]);
  await expect(target.getByRole('button', { name: 'Edit card' })).toBeVisible();
});

test('only your own cards offer edit and delete', async ({ page, fake }) => {
  const snap = snapshot({ cards: [card(1, { author: ALICE }), card(2, { author: BOB })] });
  serveBoard(fake, snap);
  await openBoard(page, fake, { snap, overlay: { mineCards: [card(1).id], mineComments: [], myVotes: [] } });
  const [mine, theirs] = [cardsIn(page, 'Good').nth(0), cardsIn(page, 'Good').nth(1)];
  await expect(mine.getByRole('button', { name: 'Delete card' })).toBeVisible();
  await expect(theirs.getByRole('button', { name: 'Delete card' })).toBeHidden();
  await expect(theirs.getByRole('button', { name: 'Edit card' })).toBeHidden();
});

test('deleting a card asks first, then removes it', async ({ page, fake }) => {
  const snap = snapshot({ cards: [card(1, { author: ALICE })] });
  serveBoard(fake, snap);
  await openBoard(page, fake, { snap, overlay: { mineCards: [card(1).id], mineComments: [], myVotes: [] } });

  await cardsIn(page, 'Good').first().getByRole('button', { name: 'Delete card' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete card' }).click();
  await expect(cardsIn(page, 'Good')).toHaveCount(0);
  expect(sent(fake, 'retro.card.delete')).toEqual([{ cardId: card(1).id }]);
});

test('anonymous cards carry no name, and guests cannot post anonymously', async ({ page, fake }) => {
  serveBoard(fake);
  await openBoard(page, fake);
  await typeCard(page, 'Bad', 'Too many meetings');
  const good = column(page, 'Bad');
  await good.getByLabel('Post anonymously').check();
  await good.getByRole('button', { name: 'Add card' }).click();
  const posted = cardsIn(page, 'Bad').first();
  await expect(posted.locator('.retro-anonymous')).toHaveText('Anonymous');
  await expect(posted.locator('.retro-author-name')).toHaveCount(0);
  await expect.poll(() => sent(fake, 'retro.card.add').map((d) => d.anonymous)).toEqual([true]);
});

test.describe('as a guest', () => {
  test('the anonymous toggle is not offered', async ({ page, fake }) => {
    const guest = { pid: '90000000-0000-4000-8000-000000000004', kind: 'guest', name: 'Sneaky Penguin' };
    serveBoard(fake);
    await openBoard(page, fake, { me: guest });
    await typeCard(page, 'Good', 'Hello');
    await expect(column(page, 'Good').getByLabel('Post anonymously')).toBeHidden();
  });
});

test('the boot snapshot paints the board before the socket answers', async ({ page, fake }) => {
  const snap = snapshot({ cards: [card(1, { body: 'From boot' })] });
  await openBoard(page, fake, { snap, seedBoot: true });
  await expect(cardsIn(page, 'Good').first()).toHaveText(/From boot/);
  const hello = await fake.waitForFrame((f) => f.t === 'hello');
  expect(hello.frame.subs).toEqual([{ r: 'board:b0000000-0000-4000-8000-0000000000b1', e: 'aaaaaaaaaaaaaaaa', s: 0 }]);
});
