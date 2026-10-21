import { expect, test } from '../_harness/fixtures.js';
import { ROOM, center, chip, chipKeys, openStrip, pointer, round, roundId, setupStrip } from './_strip.js';

// Presses (and optionally moves) in one turn of the page's event loop, before the 300 ms timer can
// fire however slow the machine is, and returns the chip's classes right after.
function pressAndRead(locator, at, moveTo = null) {
  return locator.evaluate((el, { at: start, moveTo: next }) => {
    const fire = (type, { x, y }) => el.dispatchEvent(new PointerEvent(type, {
      bubbles: true, cancelable: true, pointerId: 7, pointerType: 'touch', isPrimary: true, button: type === 'pointermove' ? -1 : 0, buttons: 1, clientX: x, clientY: y,
    }));
    fire('pointerdown', start);
    if (next) {
      fire('pointermove', next);
    }
    return el.closest('.nt-chip').className;
  }, { at, moveTo });
}

const grip = (page, n) => page.getByRole('button', { name: `Move round ${n}` });

test('dragging a grip with the mouse reorders the rounds', async ({ page, fake, isMobile }) => {
  test.skip(isMobile, 'mouse drag; the touch path is covered below');
  const state = setupStrip(fake);
  await openStrip(page);
  const from = await center(grip(page, 5));
  const to = await center(chip(page, 3));

  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await page.mouse.move(from.x - 20, from.y, { steps: 3 });
  await expect(chip(page, 5)).toHaveClass(/is-dragging/);
  await page.mouse.move(to.x - 10, to.y, { steps: 8 });
  await expect(chip(page, 3)).toHaveClass(/is-drop-target/);
  await page.mouse.up();

  await expect.poll(() => chipKeys(page)).toEqual(['ABC-1', 'ABC-2', 'ABC-5', 'ABC-3', 'ABC-4', 'ABC-6']);
  expect(state.sent['poker.round.reorder']).toEqual([{ order: [1, 2, 5, 3, 4, 6].map(roundId) }]);
  await expect(chip(page, 5)).not.toHaveClass(/is-dragging/);
});

test('a short mouse press on the grip does not reorder', async ({ page, fake }) => {
  const state = setupStrip(fake);
  await openStrip(page);
  const at = await center(grip(page, 2));
  await pointer(grip(page, 2), 'pointerdown', { ...at, pointerType: 'mouse' });
  await pointer(grip(page, 2), 'pointermove', { x: at.x - 3, y: at.y, pointerType: 'mouse' });
  await pointer(grip(page, 2), 'pointerup', { x: at.x - 3, y: at.y, pointerType: 'mouse' });
  await page.waitForTimeout(200);
  expect(state.sent['poker.round.reorder']).toBeUndefined();
});

test.describe('touch', () => {
  test('a long press on the grip starts a reorder; the drop sends the new order', async ({ page, fake }) => {
    const state = setupStrip(fake);
    await openStrip(page);
    const from = await center(grip(page, 2));
    const to = await center(chip(page, 4));

    expect(await pressAndRead(grip(page, 2), from)).not.toContain('is-dragging');
    await expect(chip(page, 2)).toHaveClass(/is-dragging/);
    await pointer(grip(page, 2), 'pointermove', { x: (from.x + to.x) / 2, y: from.y });
    await pointer(grip(page, 2), 'pointermove', { x: to.x + 10, y: from.y });
    await pointer(grip(page, 2), 'pointerup', { x: to.x + 10, y: from.y });

    await expect.poll(() => chipKeys(page)).toEqual(['ABC-1', 'ABC-3', 'ABC-4', 'ABC-2', 'ABC-5', 'ABC-6']);
    expect(state.sent['poker.round.reorder']).toEqual([{ order: [1, 3, 4, 2, 5, 6].map(roundId) }]);
  });

  test('moving before the long press ends is not a reorder', async ({ page, fake }) => {
    const state = setupStrip(fake);
    await openStrip(page);
    const from = await center(grip(page, 2));
    await pressAndRead(grip(page, 2), from, { x: from.x + 30, y: from.y });
    await page.waitForTimeout(450);
    await expect(chip(page, 2)).not.toHaveClass(/is-dragging/);
    await pointer(grip(page, 2), 'pointerup', { x: from.x + 120, y: from.y });
    await page.waitForTimeout(200);
    expect(state.sent['poker.round.reorder']).toBeUndefined();
  });

  test('a cancelled touch (the browser took the gesture) drops the reorder', async ({ page, fake }) => {
    const state = setupStrip(fake);
    await openStrip(page);
    const from = await center(grip(page, 2));
    await pointer(grip(page, 2), 'pointerdown', from);
    await expect(chip(page, 2)).toHaveClass(/is-dragging/);
    await pointer(grip(page, 2), 'pointermove', { x: from.x + 200, y: from.y });
    await pointer(grip(page, 2), 'pointercancel', { x: from.x + 200, y: from.y });
    await expect(chip(page, 2)).not.toHaveClass(/is-dragging/);
    await page.waitForTimeout(200);
    expect(state.sent['poker.round.reorder']).toBeUndefined();
  });

  test('a real touch long press reorders (Chromium input pipeline)', async ({ page, fake, browserName }) => {
    test.skip(browserName !== 'chromium', 'CDP touch input exists in Chromium only');
    const state = setupStrip(fake);
    await openStrip(page);
    const cdp = await page.context().newCDPSession(page);
    const from = await center(grip(page, 1));
    const to = await center(chip(page, 3));
    const touch = (type, x, y) => cdp.send('Input.dispatchTouchEvent', { type, touchPoints: type === 'touchEnd' ? [] : [{ x, y }] });

    await touch('touchStart', from.x, from.y);
    await expect(chip(page, 1)).toHaveClass(/is-dragging/);
    for (let i = 1; i <= 6; i++) {
      await touch('touchMove', from.x + ((to.x + 10 - from.x) * i) / 6, from.y);
    }
    await touch('touchEnd');
    await expect.poll(() => chipKeys(page)).toEqual(['ABC-2', 'ABC-3', 'ABC-1', 'ABC-4', 'ABC-5', 'ABC-6']);
    expect(state.sent['poker.round.reorder']).toHaveLength(1);
  });
});

test('ArrowLeft and ArrowRight on a focused grip move the round and keep focus', async ({ page, fake }) => {
  setupStrip(fake);
  await openStrip(page);
  await grip(page, 3).focus();
  await page.keyboard.press('ArrowLeft');
  await expect.poll(() => chipKeys(page)).toEqual(['ABC-1', 'ABC-3', 'ABC-2', 'ABC-4', 'ABC-5', 'ABC-6']);
  await expect(grip(page, 3)).toBeFocused();
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('ArrowRight');
  await expect.poll(() => chipKeys(page)).toEqual(['ABC-1', 'ABC-2', 'ABC-4', 'ABC-3', 'ABC-5', 'ABC-6']);
  // The arrows moved the round, not the viewer (the table's ←/→ shortcut stays quiet on a grip).
  await expect(page.locator('.nt-round-name')).toHaveText('Round 6 · ABC-6');
});

test('a concurrent reorder is refused stale; the move is rebased on the fresh rounds and sent once more', async ({ page, fake }) => {
  const state = setupStrip(fake);
  await openStrip(page);
  const late = round('0a000000-0000-4000-8000-000000001077', 7, { ticketKey: 'LATE-7' });
  let calls = 0;
  fake.onCommand('poker.round.reorder', (ctx) => {
    calls += 1;
    (state.sent['poker.round.reorder'] ??= []).push(ctx.frame.d);
    if (calls === 1) {
      // Someone else added a round first, so the client's order is no longer a permutation.
      state.rounds = [...state.rounds, late];
      fake.tx(ROOM, [{ k: 'poker.round.created', d: { round: late } }]);
      ctx.nack('stale', { d: { current: { order: state.rounds.map((r) => r.id) } } });
      return;
    }
    state.rounds = ctx.frame.d.order.map((id) => state.rounds.find((r) => r.id === id));
    ctx.commit([{ k: 'poker.round.order', d: ctx.frame.d }]);
  });

  await grip(page, 4).focus();
  await page.keyboard.press('ArrowLeft');

  await expect.poll(() => state.sent['poker.round.reorder']).toEqual([
    { order: [1, 2, 4, 3, 5, 6].map(roundId) },
    { order: [...[1, 2, 4, 3, 5, 6].map(roundId), late.id] },
  ]);
  await expect.poll(() => chipKeys(page)).toEqual(['ABC-1', 'ABC-2', 'ABC-4', 'ABC-3', 'ABC-5', 'ABC-6', 'LATE-7']);
});

test('a reorder by someone else moves the chips without changing the viewed round', async ({ page, fake }) => {
  setupStrip(fake);
  await openStrip(page);
  fake.tx(ROOM, [{ k: 'poker.round.order', d: { order: [6, 5, 4, 3, 2, 1].map(roundId) } }]);
  await expect.poll(() => chipKeys(page)).toEqual(['ABC-6', 'ABC-5', 'ABC-4', 'ABC-3', 'ABC-2', 'ABC-1']);
  await expect(page.locator('.nt-round-name')).toHaveText('Round 6 · ABC-6');
});
