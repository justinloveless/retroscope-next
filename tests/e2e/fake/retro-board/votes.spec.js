import { expect, test } from '../_harness/fixtures.js';
import { ALICE, BOB, CONFIG, card, cardsIn, openBoard, sent, serveBoard, snapshot } from './_board.js';

const voteButton = (scope) => scope.locator('.retro-vote');

test('a vote counts at once and toggles off again', async ({ page, fake }) => {
  const snap = snapshot({ cards: [card(1)] });
  serveBoard(fake, snap);
  await openBoard(page, fake, { snap });
  const vote = voteButton(cardsIn(page, 'Good').first());
  await expect(vote).toHaveText('👍0');

  await vote.click();
  await expect(vote).toHaveText('👍1');
  await expect(vote).toHaveAttribute('aria-pressed', 'true');
  await vote.click();
  await expect(vote).toHaveText('👍0');
  await expect(vote).toHaveAttribute('aria-pressed', 'false');
  await expect.poll(() => sent(fake, 'retro.vote.set')).toEqual([
    { cardId: card(1).id, on: true },
    { cardId: card(1).id, on: false },
  ]);
});

test('the vote limit disables further votes but not taking one back', async ({ page, fake }) => {
  const config = { ...CONFIG, maxVotes: 1 };
  const snap = snapshot({ config, cards: [card(1), card(2)] });
  serveBoard(fake, snap);
  await openBoard(page, fake, { snap });
  const [first, second] = [voteButton(cardsIn(page, 'Good').nth(0)), voteButton(cardsIn(page, 'Good').nth(1))];

  await first.click();
  await expect(first).toHaveAttribute('aria-pressed', 'true');
  await expect(second).toBeDisabled();
  await expect(second).toHaveAttribute('title', 'You have used all your votes');
  await expect(first).toBeEnabled();
  await first.click();
  await expect(second).toBeEnabled();
});

test('a refused vote rolls back and says why', async ({ page, fake }) => {
  const snap = snapshot({ cards: [card(1)] });
  await openBoard(page, fake, { snap });
  fake.onCommand('retro.vote.set', (ctx) => ctx.nack('vote_limit'));
  const vote = voteButton(cardsIn(page, 'Good').first());

  await vote.click();
  await expect(page.getByRole('alert').filter({ hasText: 'You have used all your votes.' })).toBeVisible();
  await expect(vote).toHaveText('👍0');
  await expect(vote).toHaveAttribute('aria-pressed', 'false');
});

test('self votes follow the board setting', async ({ page, fake }) => {
  const snap = snapshot({ config: { ...CONFIG, allowSelfVotes: false }, cards: [card(1, { author: ALICE }), card(2, { author: BOB })] });
  serveBoard(fake, snap);
  await openBoard(page, fake, { snap, overlay: { mineCards: [card(1).id], mineComments: [], myVotes: [] } });
  await expect(voteButton(cardsIn(page, 'Good').nth(0))).toBeDisabled();
  await expect(voteButton(cardsIn(page, 'Good').nth(0))).toHaveAttribute('title', 'You cannot vote for your own card');
  await expect(voteButton(cardsIn(page, 'Good').nth(1))).toBeEnabled();
});

test('voting follows the stage and shows voters when the server sends them', async ({ page, fake }) => {
  const config = { ...CONFIG, stagesEnabled: true };
  const snap = snapshot({ config, stage: 'thinking', cards: [card(1)], votes: [{ cardId: card(1).id, count: 2 }] });
  await openBoard(page, fake, { snap });
  const target = cardsIn(page, 'Good').first();
  await expect(voteButton(target)).toBeDisabled();
  await expect(target.locator('.retro-voters')).toBeHidden();

  fake.tx('board:b0000000-0000-4000-8000-0000000000b1', [
    { k: 'retro.stage', d: { stage: 'discussing' } },
    { k: 'retro.vote', d: { cardId: card(1).id, count: 2, voters: [ALICE.pid, BOB.pid] } },
  ]);
  await expect(target.locator('.retro-voters')).toHaveAttribute('title', 'Alice, Bob');
  await expect(voteButton(target)).toBeDisabled();

  fake.tx('board:b0000000-0000-4000-8000-0000000000b1', [
    { k: 'retro.stage', d: { stage: 'voting' } },
    { k: 'retro.vote', d: { cardId: card(1).id, count: 2 } },
  ]);
  await expect(voteButton(target)).toBeEnabled();
  await expect(target.locator('.retro-voters')).toBeHidden();
});

test('cards sort by votes by default and by the viewer\'s choice', async ({ page, fake }) => {
  const snap = snapshot({
    cards: [card(1, { body: 'one', author: BOB }), card(2, { body: 'two', author: ALICE }), card(3, { body: 'three', author: BOB })],
    votes: [{ cardId: card(3).id, count: 3 }, { cardId: card(1).id, count: 1 }],
  });
  await openBoard(page, fake, { snap });
  const bodies = () => cardsIn(page, 'Good').locator('.retro-card-body').allTextContents();
  await expect.poll(bodies).toEqual(['three', 'one', 'two']);

  await page.getByRole('button', { name: /^Sort Good/ }).click();
  await page.getByRole('menuitem', { name: 'Newest first' }).click();
  await expect.poll(bodies).toEqual(['three', 'two', 'one']);
  await page.getByRole('button', { name: /^Sort Good/ }).click();
  await page.getByRole('menuitem', { name: 'Author A–Z' }).click();
  await expect.poll(bodies).toEqual(['two', 'one', 'three']);
});
