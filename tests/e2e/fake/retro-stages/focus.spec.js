import { expect, test } from '../_harness/fixtures.js';
import { ALICE, BOB, ROOM, STAGED, card, cardsIn, openBoard, sent, snapshot } from './_stages.js';

const discussing = (fields = {}) => snapshot({
  config: STAGED,
  stage: 'discussing',
  readiness: { stage: 'discussing', ready: [] },
  cards: [card(1, { body: 'one' }), card(2, { body: 'two', author: ALICE }), card(3, { body: 'three' })],
  votes: [{ cardId: card(3).id, count: 3, voters: [ALICE.pid, BOB.pid] }, { cardId: card(1).id, count: 1, voters: [BOB.pid] }],
  ...fields,
});

const banner = (page) => page.getByRole('region', { name: 'Focused card' });
const focusButton = (scope) => scope.locator('.retro-focus-toggle');

/** Answers `retro.focus.set` as the decider does; `move(cardId)` is someone else's committed focus. */
function serveFocus(fake, initial = null) {
  let focus = initial;
  const move = (cardId) => {
    focus = cardId;
    fake.tx(ROOM, [{ k: 'retro.focus', d: cardId ? { cardId } : {} }]);
  };
  fake.onCommand('retro.focus.set', (ctx) => {
    const { cardId, from } = ctx.frame.d;
    if (from !== focus) {
      ctx.nack('stale', { d: { current: focus } });
      return;
    }
    focus = cardId;
    ctx.commit([{ k: 'retro.focus', d: cardId ? { cardId } : {} }]);
  });
  return { move };
}

test('a late joiner sees the focused card from the snapshot', async ({ page, fake }) => {
  await openBoard(page, fake, { snap: discussing({ focus: card(2).id }), me: BOB });
  await expect(banner(page)).toBeVisible();
  await expect(banner(page).locator('.retro-focus-body')).toHaveText('two');
  await expect(banner(page).locator('.retro-focus-column')).toHaveText('Good');
  await expect(banner(page).locator('.retro-focus-author .retro-author-name')).toHaveText('Alice');
  await expect(cardsIn(page, 'Good').filter({ hasText: 'two' })).toHaveClass(/retro-card-focused/);
});

test('focus moves at once, carries the focus it replaces, and follows other people', async ({ page, fake }) => {
  const focus = serveFocus(fake, card(2).id);
  await openBoard(page, fake, { snap: discussing({ focus: card(2).id }) });
  const one = cardsIn(page, 'Good').filter({ hasText: 'one' });

  await focusButton(one).click();
  await expect(banner(page).locator('.retro-focus-body')).toHaveText('one');
  await expect(focusButton(one)).toHaveAttribute('aria-pressed', 'true');
  await expect.poll(() => sent(fake, 'retro.focus.set')).toEqual([{ cardId: card(1).id, from: card(2).id }]);

  focus.move(card(3).id);
  await expect(banner(page).locator('.retro-focus-body')).toHaveText('three');
  await banner(page).getByRole('button', { name: 'Stop focusing' }).click();
  await expect(banner(page)).toBeHidden();
  await expect.poll(() => sent(fake, 'retro.focus.set').at(-1)).toEqual({ cardId: null, from: card(3).id });
});

test('a focus change that lost the race is dropped without a word', async ({ page, fake }) => {
  serveFocus(fake, card(3).id);
  await openBoard(page, fake, { snap: discussing({ focus: card(2).id }) });
  await focusButton(cardsIn(page, 'Good').filter({ hasText: 'one' })).click();
  await expect.poll(() => sent(fake, 'retro.focus.set')).toEqual([{ cardId: card(1).id, from: card(2).id }]);
  await expect(banner(page).locator('.retro-focus-body')).toHaveText('two');
  await expect(page.getByRole('alert')).toHaveCount(0);
  await expect(page.getByRole('status').filter({ hasText: 'changed' })).toHaveCount(0);
});

test('previous and next follow the viewer\'s own sort of the column', async ({ page, fake }) => {
  serveFocus(fake, card(3).id);
  await openBoard(page, fake, { snap: discussing({ focus: card(3).id }) });
  const body = banner(page).locator('.retro-focus-body');
  const next = banner(page).getByRole('button', { name: 'Next card' });
  const prev = banner(page).getByRole('button', { name: 'Previous card' });

  await expect(prev).toBeDisabled();
  await next.click();
  await expect(body).toHaveText('one');
  await next.click();
  await expect(body).toHaveText('two');
  await expect(next).toBeDisabled();

  await page.getByRole('button', { name: /^Sort Good/ }).click();
  await page.getByRole('menuitem', { name: 'Oldest first' }).click();
  await prev.click();
  await expect(body).toHaveText('one');
  await expect(prev).toBeDisabled();
});

test('focus is offered only while discussing, and on team boards only to members', async ({ page, fake }) => {
  await openBoard(page, fake, { snap: discussing({ stage: 'voting', readiness: { stage: 'voting', ready: [] }, focus: card(1).id }) });
  await expect(focusButton(page)).toHaveCount(3);
  await expect(focusButton(page).first()).toBeHidden();
  await expect(banner(page)).toBeHidden();

  fake.tx(ROOM, [{ k: 'retro.stage', d: { stage: 'discussing' } }]);
  await expect(banner(page)).toBeVisible();
  await expect(focusButton(page).first()).toBeVisible();
});

test('a focused card that is deleted clears the banner', async ({ page, fake }) => {
  await openBoard(page, fake, { snap: discussing({ focus: card(1).id }) });
  await expect(banner(page)).toBeVisible();
  fake.tx(ROOM, [{ k: 'retro.card.deleted', d: { cardId: card(1).id } }, { k: 'retro.focus', d: {} }]);
  await expect(banner(page)).toBeHidden();
});
