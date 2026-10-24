import { expect, test } from '../_harness/fixtures.js';
import { BOB, DOT, GUS, ME, R1, ROOM, handCard, lastCommand, openTable, seat, setupPoker, snapshot } from './_poker.js';

test('picking, locking and abstaining send set-semantics commands and update my hand', async ({ page, fake }) => {
  setupPoker(fake);
  await openTable(page);

  // Everyone but me has a seat; my vote lives in my hand.
  await expect(page.locator('.nt-seat')).toHaveCount(3);
  await expect(seat(page, ME)).toHaveCount(0);

  await handCard(page, 5).click();
  expect((await lastCommand(fake, 'poker.vote.set')).d).toEqual({ roundId: R1, points: 5 });
  await expect(handCard(page, 5)).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('.nt-hand-pill')).toHaveText('5 pts');
  await expect(page.locator('.nt-hand-locked')).toHaveCount(0);

  await page.getByRole('button', { name: 'Lock In' }).click();
  expect((await lastCommand(fake, 'poker.lock.set')).d).toEqual({ roundId: R1, locked: true });
  await expect(handCard(page, 5).locator('.nt-hand-locked')).toHaveText('Locked');
  await expect(handCard(page, 5)).toHaveClass(/is-selected/);
  await expect(handCard(page, 8)).toHaveAttribute('aria-disabled', 'true');

  await page.getByRole('button', { name: 'Unlock' }).click();
  await expect(page.locator('.nt-hand-locked')).toHaveCount(0);

  await page.getByRole('button', { name: 'Abstain' }).click();
  expect((await lastCommand(fake, 'poker.abstain.set')).d).toEqual({ roundId: R1, abstain: true });
  await expect(page.getByRole('button', { name: 'Lock In' })).toBeDisabled();
  await expect(handCard(page, 5)).toHaveAttribute('aria-pressed', 'false');

  await page.getByRole('button', { name: 'Unabstain' }).click();
  await expect(page.getByRole('button', { name: 'Abstain' })).toBeEnabled();
});

test('Shift-click on an adjacent card votes between the two and merges them', async ({ page, fake }) => {
  setupPoker(fake);
  await openTable(page);

  await handCard(page, 3).click();
  await handCard(page, 5).click({ modifiers: ['Shift'] });
  expect((await lastCommand(fake, 'poker.vote.set')).d).toEqual({ roundId: R1, points: 3, high: 5 });
  await expect(page.locator('.nt-hand-card.is-between')).toHaveAttribute('aria-label', 'Between 3 and 5');
  await expect(page.locator('.nt-hand-pill')).toHaveText('3–5');
  await expect(page.locator('.nt-hand-card')).toHaveCount(6);

  await handCard(page, 13).click({ modifiers: ['Shift'] });
  expect((await lastCommand(fake, 'poker.vote.set')).d).toEqual({ roundId: R1, points: 13 });
  await expect(page.locator('.nt-hand-card')).toHaveCount(7);
});

test('the between merge flies both cards into the slot, unless motion is reduced', async ({ page, fake }) => {
  setupPoker(fake);
  await openTable(page);
  await handCard(page, 5).click();
  await handCard(page, 8).click({ modifiers: ['Shift'] });
  await expect(page.locator('.nt-merge-ghost')).toHaveCount(2);
  await expect(page.locator('.nt-merge-ghost')).toHaveCount(0);

  await page.emulateMedia({ reducedMotion: 'reduce' });
  await handCard(page, 2).click({ modifiers: ['Shift'] });
  await handCard(page, 3).click({ modifiers: ['Shift'] });
  await expect(page.locator('.nt-hand-card.is-between')).toHaveAttribute('aria-label', 'Between 2 and 3');
  expect(await page.locator('.nt-merge-ghost').count()).toBe(0);
});

test('keyboard: digits pick, Shift+digit votes between, L locks, A abstains, R reveals', async ({ page, fake }) => {
  setupPoker(fake);
  await openTable(page);

  await page.keyboard.press('4');
  expect((await lastCommand(fake, 'poker.vote.set')).d).toEqual({ roundId: R1, points: 5 });
  await page.keyboard.press('Shift+Digit2');
  await expect.poll(async () => (await lastCommand(fake, 'poker.vote.set')).d).toEqual({ roundId: R1, points: 2, high: 3 });
  await page.keyboard.press('l');
  expect((await lastCommand(fake, 'poker.lock.set')).d).toEqual({ roundId: R1, locked: true });
  await expect(page.locator('.nt-hand-locked')).toBeVisible();
  await page.keyboard.press('a');
  expect((await lastCommand(fake, 'poker.abstain.set')).d).toEqual({ roundId: R1, abstain: true });
  await page.keyboard.press('r');
  expect((await lastCommand(fake, 'poker.round.reveal')).d).toEqual({ roundId: R1 });
  await expect(page.locator('.nt-pile')).toHaveCount(1);
});

test('shortcuts stay quiet while the ticket field has focus', async ({ page, fake }) => {
  setupPoker(fake);
  await openTable(page);
  await page.getByRole('textbox', { name: 'Ticket key' }).focus();
  await page.keyboard.press('5');
  await page.keyboard.press('Escape');
  expect(fake.received.filter(({ frame }) => frame.t === 'cmd')).toEqual([]);
});

test('other players: status only, dimmed when away, guests carry a badge', async ({ page, fake }) => {
  setupPoker(fake);
  await openTable(page);

  await expect(seat(page, GUS).locator('.nt-badge-guest')).toBeVisible();
  await expect(seat(page, GUS)).toHaveAttribute('aria-label', 'Gus (guest), waiting');
  await expect(seat(page, DOT)).toHaveAttribute('aria-label', 'Dot, waiting, away');
  await expect(seat(page, BOB).locator('.nt-badge-guest')).toBeHidden();
  await expect(seat(page, DOT)).toHaveAttribute('data-absent', '');
  await expect(seat(page, DOT)).toHaveCSS('opacity', '0.5');

  fake.tx(ROOM, [{ k: 'poker.vote.state', d: { roundId: R1, pid: BOB.pid, status: 'selected' } }]);
  await expect(seat(page, BOB)).toHaveAttribute('data-status', 'selected');
  await expect(seat(page, BOB)).toHaveAttribute('aria-label', 'Bob, thinking');
  fake.tx(ROOM, [{ k: 'poker.vote.state', d: { roundId: R1, pid: BOB.pid, status: 'locked' } }]);
  await expect(seat(page, BOB).locator('.nt-card.nt-slide-in')).toBeVisible();
  await expect(seat(page, BOB)).toHaveAttribute('aria-label', 'Bob, locked in');
  fake.tx(ROOM, [{ k: 'presence.join', d: DOT }]);
  await expect(seat(page, DOT)).not.toHaveAttribute('data-absent', '');
});

test('observers see no hand', async ({ page, fake }) => {
  setupPoker(fake, { snap: snapshot({ observers: [ME.pid] }) });
  await openTable(page);
  await expect(page.getByText('You are an observer')).toBeVisible();
  await expect(page.locator('.nt-hand')).toBeHidden();
  await expect(seat(page, ME)).toHaveCount(0);
  await page.keyboard.press('3');
  fake.tx(ROOM, [{ k: 'poker.observers', d: { ids: [] } }]);
  await expect(page.locator('.nt-hand')).toBeVisible();
  expect(fake.received.filter(({ frame }) => frame.t === 'cmd')).toEqual([]);
});

test('a final nack rolls the optimistic vote back and says why', async ({ page, fake }) => {
  setupPoker(fake);
  fake.onCommand('poker.vote.set', (ctx) => ctx.nack('round_revealed'));
  await openTable(page);
  await handCard(page, 8).click();
  await expect(page.getByRole('alert')).toHaveText(/already revealed/);
  await expect(handCard(page, 8)).toHaveAttribute('aria-pressed', 'false');
  await expect(page.locator('.nt-hand-pill')).toHaveCount(0);
});

test('a guest without a name picks one before joining', async ({ page, fake }) => {
  setupPoker(fake, { viewer: GUS });
  fake.boot = (path) => ({ build: 'fake', theme: 'light', me: null, path });
  fake.route('POST', '/api/guest', (req) => ({
    status: 201,
    body: { principal: { id: GUS.pid, kind: 'guest', name: req.body.displayName } },
  }));
  await page.goto('/poker/NEOTRO1234');
  await page.getByRole('textbox', { name: 'Your name' }).fill('Gus');
  await page.getByRole('button', { name: 'Join' }).click();
  await expect(page.locator('.nt-hand')).toBeVisible();
  await expect(seat(page, GUS)).toHaveCount(0);
  await expect(seat(page, BOB)).toBeVisible();
  const post = fake.requests.find((r) => r.method === 'POST' && r.path === '/api/guest');
  expect(post.json).toEqual({ displayName: 'Gus' });
});
