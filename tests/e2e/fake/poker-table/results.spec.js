// The results overlay (docs/followups/UX2.md, item 5): on a reveal a translucent, non-modal "Results" dialog
// opens over the issue with one pile of face-up cards per point value (a between vote counts on both ends,
// abstains pile on their own) and the winner marked. Minimize folds it into the dock's compact bar, Show
// results brings it back, and each tab remembers a minimized round until it is revealed again.
import { expect, test } from '../_harness/fixtures.js';
import { BOB, CODE, DOT, GUS, ME, R1, R2, ROOM, openTable, revealed, round, setupPoker, snapshot } from './_poker.js';

const OTHERS = {
  [BOB.pid]: { points: 5, locked: true, abstained: false },
  [GUS.pid]: { points: 3, high: 5, locked: true, abstained: false },
  [DOT.pid]: { abstained: true, locked: true },
};

const results = (page) => page.getByRole('dialog', { name: 'Results' });
const piles = (page) => results(page).locator('.nt-pile');

test('a reveal opens the results for every open tab, with a pile per value and the winner marked', async ({ page, fake, browser }) => {
  const votes = setupPoker(fake, { others: OTHERS });
  await openTable(page);
  const peer = await (await browser.newContext({ baseURL: fake.url })).newPage();
  await openTable(peer);
  await expect(results(page)).toBeHidden();

  votes.set(ME.pid, { points: 8, locked: true, abstained: false });
  fake.tx(ROOM, [revealed(R1, votes, BOB.pid, 5)]);

  for (const p of [page, peer]) {
    const dialog = results(p);
    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveAttribute('aria-modal', 'false');
    await expect(piles(p)).toHaveCount(4);
    expect(await piles(p).evaluateAll((nodes) => nodes.map((n) => [n.dataset.points, n.querySelectorAll('.nt-pile-card').length]))).toEqual([['3', 1], ['5', 2], ['8', 1], ['abstain', 1]]);
    await expect(p.locator('.nt-pile.is-winner')).toHaveAttribute('data-points', '5');
    await expect(p.locator('.nt-pile.is-winner')).toHaveAttribute('aria-label', '5 pts, 2 votes, winner: Bob, Gus');
    await expect(piles(p).nth(0).locator('.nt-pile-names')).toHaveText('Gus');
    await expect(dialog.locator('.nt-winner')).toHaveText('Winning Points: 5 pts');
    await expect(dialog.getByRole('button', { name: 'Replay round' })).toBeVisible();
    // The compact bar steps aside while the overlay holds the results.
    await expect(p.locator('.nt-dock .nt-actions')).toBeHidden();
  }
  await expect(results(page)).toBeFocused();
  await peer.context().close();
});

test('Minimize folds the results into the dock bar and Show results brings them back', async ({ page, fake }) => {
  const votes = setupPoker(fake, { others: OTHERS });
  await openTable(page);
  fake.tx(ROOM, [revealed(R1, votes, BOB.pid, 5)]);
  await expect(results(page)).toBeVisible();

  await results(page).getByRole('button', { name: 'Minimize' }).click();
  await expect(results(page)).toBeHidden();
  const bar = page.locator('.nt-dock .nt-actions');
  await expect(bar.locator('.nt-winner')).toHaveText('Winning Points: 5 pts');
  await expect(bar.locator('.nt-stack-pill')).toHaveText(['1 x 3–5 pts', '1 x 5 pts']);
  const show = bar.getByRole('button', { name: 'Show results' });
  await expect(show).toBeFocused();

  await show.click();
  await expect(results(page)).toBeVisible();
  await expect(results(page)).toBeFocused();
  await expect(bar).toBeHidden();

  await page.keyboard.press('Escape');
  await expect(results(page)).toBeHidden();
  await expect(show).toBeFocused();
});

test('a minimized round stays minimized in this tab, other rounds and a new reveal open again', async ({ page, fake }) => {
  setupPoker(fake, {
    snap: snapshot({
      rounds: [round(R1, 1, { state: 'revealed', winner: 5, revealedAt: 1 }), round(R2, 2, { state: 'revealed', winner: 3, revealedAt: 2 })],
      votes: [
        { roundId: R1, pid: BOB.pid, points: 5, locked: true, abstained: false },
        { roundId: R2, pid: BOB.pid, points: 3, locked: true, abstained: false },
      ],
      currentRoundId: R1,
    }),
  });
  await openTable(page);
  await expect(results(page)).toBeVisible();
  await results(page).getByRole('button', { name: 'Minimize' }).click();
  await expect(results(page)).toBeHidden();

  await page.goto(`/poker/${CODE}?round=2`);
  await expect(results(page)).toBeVisible();
  await expect(page.locator('.nt-pile.is-winner')).toHaveAttribute('data-points', '3');
  await page.goto(`/poker/${CODE}?round=1`);
  await page.locator('.nt-table').waitFor();
  await expect(page.locator('.nt-show-results')).toBeVisible();
  await expect(results(page)).toBeHidden();

  // Replayed and revealed again: a fresh result opens for everyone.
  fake.tx(ROOM, [{ k: 'poker.round.replayed', d: { roundId: R1 } }, { k: 'poker.round.updated', d: { round: round(R1, 1, { version: 2 }) } }]);
  await expect(page.getByRole('button', { name: 'Reveal Cards' })).toBeVisible();
  fake.tx(ROOM, [revealed(R1, new Map([[BOB.pid, { points: 8, locked: true }]]), BOB.pid, 8)]);
  await expect(results(page)).toBeVisible();
  await expect(page.locator('.nt-pile.is-winner')).toHaveAttribute('data-points', '8');
});

test('a reveal leaves the focus in a text field the viewer is typing in', async ({ page, fake }) => {
  const votes = setupPoker(fake, { others: OTHERS });
  await openTable(page);
  // Any text field counts; this one stays put while the round header swaps its key editor out on the reveal.
  await page.evaluate(() => document.body.append(Object.assign(document.createElement('textarea'), { ariaLabel: 'Notes' })));
  await page.getByRole('textbox', { name: 'Notes' }).focus();
  fake.tx(ROOM, [revealed(R1, votes, BOB.pid, 5)]);
  await expect(results(page)).toBeVisible();
  await expect(results(page)).not.toBeFocused();
  await expect(page.getByRole('textbox', { name: 'Notes' })).toBeFocused();
});

test('no vote value reaches the page before the reveal', async ({ page, fake }) => {
  setupPoker(fake, { others: OTHERS });
  await openTable(page);
  fake.tx(ROOM, [{ k: 'poker.vote.state', d: { roundId: R1, pid: BOB.pid, status: 'locked' } }]);
  await expect(page.locator('.nt-seat[data-status="locked"]')).toHaveCount(1);
  expect(await page.locator('.nt-results').evaluate((el) => ({ hidden: el.hidden, piles: el.querySelectorAll('.nt-pile').length, digits: el.textContent.match(/\d/g) ?? [] })))
    .toEqual({ hidden: true, piles: 0, digits: [] });
});

test.describe('with reduced motion', () => {
  test.use({ reducedMotion: 'reduce' });

  test('the piles fade in instead of being dealt', async ({ page, fake }) => {
    const votes = setupPoker(fake, { others: OTHERS });
    await openTable(page);
    fake.tx(ROOM, [revealed(R1, votes, BOB.pid, 5)]);
    await expect(piles(page)).toHaveCount(3);
    await expect(page.locator('.nt-pile-card').first()).toHaveCSS('animation-name', 'nt-fade-in');
  });
});
