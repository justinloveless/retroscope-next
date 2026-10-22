// A poker round with four people over real sockets (§j): pick, between by Shift-click and by drag-hold, abstain,
// lock, one auto-reveal for everyone; then replay, end, continue and next, which move every page to the new round.
import { expect, test } from './_real.js';
import { centre, eventCount, handCard, openTable, seat, shownRound, table } from './_poker.js';

test('four players vote four ways and the round auto-reveals exactly once for everyone', async ({ open }) => {
  test.setTimeout(90_000);
  const { players } = await table(open, ['Ann Vote', 'Bob Vote', 'Cy Vote', 'Dee Vote']);
  const [ann, bob, cy, dee] = players;

  await handCard(ann.page, 5).click();
  await expect(ann.page.locator('.nt-hand-pill')).toHaveText('5 pts');
  await ann.page.getByRole('button', { name: 'Lock In' }).click();
  await expect(seat(bob.page, ann.user)).toHaveAttribute('data-status', 'locked');
  await expect(bob.page.locator(`.nt-seat .nt-card[data-card="5"]`)).toHaveCount(0);

  await handCard(bob.page, 3).click();
  await handCard(bob.page, 5).click({ modifiers: ['Shift'] });
  await expect(bob.page.locator('.nt-hand-card.is-between')).toHaveAttribute('aria-label', 'Between 3 and 5');
  await bob.page.getByRole('button', { name: 'Lock In' }).click();

  const from = await centre(handCard(cy.page, 5));
  await cy.page.mouse.move(from.x, from.y);
  await cy.page.mouse.down();
  await cy.page.mouse.move(from.x, from.y - 20, { steps: 3 });
  const zone = cy.page.locator('.nt-zone-right');
  await expect(zone).toContainText('Between 5 & 8');
  const target = await centre(zone);
  await cy.page.mouse.move(target.x, target.y, { steps: 4 });
  await expect(zone).toHaveClass(/is-holding/);
  await expect(cy.page.locator('.nt-zone')).toHaveCount(0, { timeout: 5000 });
  await cy.page.mouse.up();
  await expect(seat(ann.page, cy.user)).toHaveAttribute('data-status', 'locked');

  await dee.page.getByRole('button', { name: 'Abstain' }).click();
  await expect(seat(ann.page, dee.user)).toHaveAttribute('data-status', 'locked');

  // Everyone is locked in; the server reveals once the room's 15 s warm-up is over (§c.7).
  for (const { page } of players) {
    await expect(page.locator('.nt-stack-pill')).toHaveText(['1 x 3–5 pts', '1 x 5 pts', '1 x 5–8 pts'], { timeout: 30_000 });
    await expect(page.locator('.nt-winner')).toHaveText('Winning Points: 5 pts');
    await expect(page.locator('.nt-hand')).toBeHidden();
  }
  await expect(dee.page.locator('.nt-stack-names').filter({ hasText: 'Dee' })).toHaveCount(0);
  await ann.page.waitForTimeout(2000);
  expect(players.map(({ frames }) => eventCount(frames, 'poker.round.revealed'))).toEqual([1, 1, 1, 1]);
});

test('replay, end, continue and next move the whole table, and parallel rounds vote apart', async ({ open }) => {
  const { players, session } = await table(open, ['Ann Next', 'Bob Next', 'Cy Next']);
  const [ann, bob, cy] = players;

  await handCard(bob.page, 8).click();
  await ann.page.keyboard.press('r');
  for (const { page } of players) {
    await expect(page.locator('.nt-stack')).toHaveCount(1);
  }
  // An unlocked pick is an abstain at reveal time; replay reopens the round for everyone.

  await bob.page.getByRole('button', { name: 'Replay round' }).click();
  for (const { page } of players) {
    await expect(page.getByRole('heading', { name: 'Your Hand' })).toBeVisible();
    await expect(page.locator('.nt-stack')).toHaveCount(0);
  }

  await handCard(cy.page, 2).click();
  await cy.page.getByRole('button', { name: 'Lock In' }).click();
  await ann.page.getByRole('button', { name: 'Reveal Cards' }).click();
  await expect(bob.page.locator('.nt-stack-pill')).toHaveText(['1 x 2 pts']);

  await cy.page.getByRole('button', { name: 'Continue' }).click();
  const dialog = cy.page.getByRole('dialog', { name: 'Start New Round' });
  await dialog.getByRole('textbox', { name: 'Ticket key (optional)' }).fill('NXT-2');
  await dialog.getByRole('textbox').press('Enter');
  for (const { page } of players) {
    await expect(page).toHaveURL(/[?&]round=2$/);
    await expect(shownRound(page)).toHaveText('Round 2 · NXT-2');
  }

  await handCard(ann.page, 1).click();
  await ann.page.getByRole('button', { name: 'Lock In' }).click();
  await bob.page.keyboard.press('r');
  await expect(cy.page.locator('.nt-stack-pill')).toHaveText(['1 x 1 pts']);
  await bob.page.getByRole('button', { name: 'End' }).click();
  for (const { page } of players) {
    await expect(page.getByRole('button', { name: 'Continue' })).toHaveCount(0);
    await expect(page.locator('.nt-ticket-kicker')).toHaveText('Viewing:');
  }

  // Two open rounds side by side: votes on one do not touch the other.
  for (const key of ['PAR-3', 'PAR-4']) {
    await bob.page.getByRole('button', { name: 'New round' }).click();
    await bob.page.getByRole('textbox', { name: 'Ticket key (optional)' }).fill(key);
    await bob.page.keyboard.press('Enter');
    await expect(shownRound(ann.page)).toHaveText(`Round ${key.at(-1)} · ${key}`);
  }
  await handCard(ann.page, 13).click();
  await ann.page.getByRole('button', { name: 'Lock In' }).click();
  await openTable(cy.page, session.code, '?round=3');
  await expect(shownRound(cy.page)).toHaveText('Round 3 · PAR-3');
  await expect(seat(cy.page, ann.user)).toHaveAttribute('data-status', 'empty');
  await cy.page.getByRole('button', { name: 'Next open round' }).click();
  await expect(shownRound(cy.page)).toHaveText('Round 4 · PAR-4');
  await expect(seat(cy.page, ann.user)).toHaveAttribute('data-status', 'locked');
});
