import { expect, test } from '../_harness/fixtures.js';
import { BOB, DOT, GUS, ME, R1, R2, ROOM, lastCommand, openTable, revealed, round, seat, setupPoker, snapshot } from './_poker.js';

const OTHERS = {
  [BOB.pid]: { points: 5, locked: true, abstained: false },
  [GUS.pid]: { points: 3, high: 5, locked: true, abstained: false },
  [DOT.pid]: { points: 5, locked: true, abstained: false },
};

test('a server auto-reveal flips every seated card 300 ms later and groups the results', async ({ page, fake }) => {
  const votes = setupPoker(fake, { others: OTHERS });
  await openTable(page);
  votes.set(ME.pid, { points: 8, locked: true, abstained: false });
  fake.tx(ROOM, [revealed(R1, votes, null, 5)]);

  const stacks = page.locator('.nt-stack');
  await expect(stacks).toHaveCount(3);
  await expect(page.locator('.nt-stack-pill')).toHaveText(['1 x 3–5 pts', '2 x 5 pts', '1 x 8 pts']);
  await expect(stacks.nth(1)).toHaveAttribute('aria-label', '2 x 5 pts: Bob, Dot');
  await expect(page.locator('.nt-winner')).toHaveText('Winning Points: 5 pts');
  await expect(page.locator('.nt-hand')).toBeHidden();

  // Every seated card flips in place; mine is in the results, not on a seat.
  const flips = page.locator('.nt-seat .nt-flip');
  await expect(flips).toHaveCount(3);
  await expect(page.locator('.nt-seat .nt-flip.is-flipped')).toHaveCount(3);
  await expect(seat(page, GUS).locator('.nt-flip')).toHaveClass(/nt-flip-fade/);
  await expect(seat(page, BOB).locator('.nt-flip-inner')).toHaveCSS('transform', /matrix3d|matrix\(-1/);
  await expect(seat(page, BOB).locator('.nt-flip-front .nt-card')).toHaveAttribute('data-card', '5');
  await expect(seat(page, BOB)).toHaveAttribute('aria-label', 'Bob, voted 5');
  await expect(seat(page, GUS)).toHaveAttribute('aria-label', 'Gus (guest), voted 3 to 5');
  await expect(seat(page, BOB).locator('.nt-seat-vote')).toHaveText('5');
});

test('the flip waits 300 ms and turns over in 0.6 s', async ({ page, fake }) => {
  const votes = setupPoker(fake, { others: OTHERS });
  await openTable(page);
  await page.evaluate(() => {
    window.__flips = [];
    new MutationObserver((records) => {
      for (const r of records) {
        if (r.target.classList?.contains('is-flipped') && !window.__flips.length) {
          window.__flips.push(performance.now() - window.__revealedAt);
        }
      }
    }).observe(document.body, { subtree: true, attributes: true, attributeFilter: ['class'] });
    new MutationObserver(() => {
      if (document.querySelector('.nt-stack') && !window.__revealedAt) {
        window.__revealedAt = performance.now();
      }
    }).observe(document.body, { subtree: true, childList: true });
  });
  fake.tx(ROOM, [revealed(R1, votes, BOB.pid, 5)]);
  await expect(page.locator('.nt-flip.is-flipped')).toHaveCount(3);
  const delay = await page.evaluate(() => window.__flips[0]);
  expect(delay).toBeGreaterThanOrEqual(250);
  expect(delay).toBeLessThan(1000);
  await expect(page.locator('.nt-flip:not(.nt-flip-fade) .nt-flip-inner').first()).toHaveCSS('transition-duration', '0.6s');
  await expect(page.locator('.nt-flip-fade .nt-flip-front').first()).toHaveCSS('transition-duration', '0.5s');
});

test.describe('with reduced motion', () => {
  test.use({ reducedMotion: 'reduce' });

  test('cards are face up at once and nothing animates', async ({ page, fake }) => {
    const votes = setupPoker(fake, { others: OTHERS });
    await openTable(page);
    fake.tx(ROOM, [revealed(R1, votes, BOB.pid, 5)]);
    await expect(page.locator('.nt-flip')).toHaveCount(3);
    expect(await page.locator('.nt-flip:not(.is-flipped)').count()).toBe(0);
    await expect(page.locator('.nt-flip-inner').first()).toHaveCSS('transition-duration', '0s');
    // The flip becomes a fade.
    await expect(page.locator('.nt-flip.nt-flip-fade')).toHaveCount(3);
    await expect(page.locator('.nt-flip-front').first()).toHaveCSS('transition-duration', '0.4s');
    fake.tx(ROOM, [{ k: 'poker.round.replayed', d: { roundId: R1 } }, { k: 'poker.round.updated', d: { round: round(R1, 1, { version: 2, autoReveal: false, ticketKey: 'ABC-1' }) } }]);
    fake.tx(ROOM, [{ k: 'poker.vote.state', d: { roundId: R1, pid: BOB.pid, status: 'locked' } }]);
    await expect(page.locator('.nt-seat .nt-card[data-card="back"]').first()).toHaveCSS('animation-name', 'nt-fade-in');
  });
});

test('opening a round that is already revealed shows the faces without a flip', async ({ page, fake }) => {
  setupPoker(fake, {
    snap: snapshot({
      rounds: [round(R1, 1, { state: 'revealed', winner: 5, revealedAt: 1 })],
      votes: [{ roundId: R1, pid: BOB.pid, points: 5, locked: true, abstained: false }],
    }),
  });
  await openTable(page);
  await expect(page.locator('.nt-flip.is-flipped')).toHaveCount(1);
});

test('Reveal Cards sends reveal; Replay, End and Continue drive the round', async ({ page, fake }) => {
  setupPoker(fake, { others: { [BOB.pid]: OTHERS[BOB.pid] } });
  await openTable(page);

  await page.getByRole('button', { name: 'Reveal Cards' }).click();
  expect((await lastCommand(fake, 'poker.round.reveal')).d).toEqual({ roundId: R1 });
  await expect(page.locator('.nt-stack')).toHaveCount(1);

  await page.getByRole('button', { name: 'Replay round' }).click();
  expect((await lastCommand(fake, 'poker.round.replay')).d).toEqual({ roundId: R1 });

  await page.getByRole('button', { name: 'End' }).click();
  expect((await lastCommand(fake, 'poker.round.end')).d).toEqual({ roundId: R1 });

  fake.onCommand('poker.round.next', (ctx) => ctx.commit([
    { k: 'poker.round.created', d: { round: round(ctx.frame.d.newRoundId, 2, { ticketKey: ctx.frame.d.ticketKey }) } },
    { k: 'poker.pointer', d: { roundId: ctx.frame.d.newRoundId, cause: 'next', nav: true } },
    { k: 'poker.round.updated', d: { round: round(R1, 1, { state: 'revealed', active: false, winner: 5, revealedAt: 1 }) } },
  ]));
  await page.getByRole('button', { name: 'Continue' }).click();
  const dialog = page.getByRole('dialog', { name: 'Start New Round' });
  await dialog.getByRole('textbox', { name: 'Ticket key (optional)' }).fill('ABC-2');
  await dialog.getByRole('textbox').press('Enter');
  const next = (await lastCommand(fake, 'poker.round.next')).d;
  expect(next).toEqual({ fromRoundId: R1, newRoundId: expect.stringMatching(/^[0-9a-f-]{14}7/), ticketKey: 'ABC-2' });
  await expect(page).toHaveURL(/[?&]round=2$/);
  await expect(page.locator('.nt-hand')).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Ticket key' })).toHaveValue('ABC-2');
});

test('Next Round with other open rounds ends this one and moves to the next open round', async ({ page, fake }) => {
  setupPoker(fake, {
    snap: snapshot({
      rounds: [round(R1, 1, { state: 'revealed', winner: 3, revealedAt: 1 }), round(R2, 2, { ticketKey: 'ABC-2' })],
    }),
  });
  await openTable(page);
  await expect(page.getByRole('button', { name: 'End' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Next Round', exact: true }).click();
  expect((await lastCommand(fake, 'poker.round.end')).d).toEqual({ roundId: R1 });
  await expect(page).toHaveURL(/[?&]round=2$/);
  await expect(page.locator('.nt-ticket-kicker')).toHaveText('Now Pointing:');
});
