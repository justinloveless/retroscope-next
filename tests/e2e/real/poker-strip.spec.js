// The round strip, observers and guests over the real server (§j): reorder by mouse drag, touch long press and
// keyboard, seen by the other players; an observer leaves the table and does not hold up the auto-reveal; a guest
// joins a quick session by name.
import { expect, test } from './_real.js';
import { centre, chipKeys, handCard, openTable, seat, shownRound, table } from './_poker.js';

const grip = (page, n) => page.getByRole('button', { name: `Move round ${n}` });
const chip = (page, key) => page.locator('.nt-strip .nt-chip').filter({ has: page.locator('.nt-chip-key', { hasText: key }) });

async function withRounds(open, names) {
  const setup = await table(open, names);
  const page = setup.players[0].page;
  for (const key of ['ORD-1', 'ORD-2', 'ORD-3', 'ORD-4']) {
    await page.getByRole('button', { name: 'New round' }).click();
    await page.getByRole('textbox', { name: 'Ticket key (optional)' }).fill(key);
    await page.keyboard.press('Enter');
    await expect(shownRound(page)).toContainText(key);
  }
  return setup;
}

/** Fires pointer events of `pointerType` on `locator` from the page itself (synthetic touch works in every engine). */
function pointer(locator, type, { x, y }, pointerType = 'touch') {
  return locator.evaluate((el, init) => {
    el.dispatchEvent(new PointerEvent(init.type, {
      bubbles: true,
      cancelable: true,
      composed: true,
      pointerId: 7,
      pointerType: init.pointerType,
      isPrimary: true,
      button: init.type === 'pointermove' ? -1 : 0,
      buttons: init.type === 'pointerup' ? 0 : 1,
      clientX: init.x,
      clientY: init.y,
    }));
  }, { type, x, y, pointerType });
}

test('rounds reorder by mouse drag and keyboard, and the others see the new order', async ({ open, isMobile }) => {
  const { players } = await withRounds(open, ['Ann Order', 'Bob Order']);
  const [ann, bob] = players;
  await expect.poll(() => chipKeys(bob.page)).toEqual(['ORD-1', 'ORD-2', 'ORD-3', 'ORD-4']);

  if (!isMobile) {
    const from = await centre(grip(ann.page, 4));
    const to = await centre(chip(ann.page, 'ORD-2'));
    await ann.page.mouse.move(from.x, from.y);
    await ann.page.mouse.down();
    await ann.page.mouse.move(from.x - 20, from.y, { steps: 3 });
    await ann.page.mouse.move(to.x - 10, to.y, { steps: 8 });
    await ann.page.mouse.up();
    // Where the drop lands left of ORD-2 depends on the engine's layout during the drag; that it moved does not.
    await expect.poll(async () => {
      const keys = await chipKeys(bob.page);
      return keys.indexOf('ORD-4') < keys.indexOf('ORD-2') && keys.filter((key) => key !== 'ORD-4').join() === 'ORD-1,ORD-2,ORD-3';
    }).toBe(true);
    await expect.poll(() => chipKeys(ann.page)).toEqual(await chipKeys(bob.page));
  }

  const before = await chipKeys(bob.page);
  await expect.poll(() => chipKeys(ann.page)).toEqual(before);
  const first = before.indexOf('ORD-1');
  expect(first).toBeLessThan(before.length - 1);
  await grip(bob.page, 1).focus();
  await bob.page.keyboard.press('ArrowRight');
  const expected = [...before];
  [expected[first], expected[first + 1]] = [expected[first + 1], expected[first]];
  await expect.poll(() => chipKeys(bob.page)).toEqual(expected);
  await expect.poll(() => chipKeys(ann.page)).toEqual(expected);
  await expect(grip(bob.page, 1)).toBeFocused();
});

test('a touch long press on a grip reorders the rounds', async ({ open }) => {
  const { players } = await withRounds(open, ['Ann Touch', 'Bob Touch']);
  const [ann, bob] = players;
  const from = await centre(grip(ann.page, 1));
  const to = await centre(chip(ann.page, 'ORD-3'));

  await pointer(grip(ann.page, 1), 'pointerdown', from);
  await expect(chip(ann.page, 'ORD-1')).toHaveClass(/is-dragging/);
  await pointer(grip(ann.page, 1), 'pointermove', { x: (from.x + to.x) / 2, y: from.y });
  await pointer(grip(ann.page, 1), 'pointermove', { x: to.x + 10, y: from.y });
  await pointer(grip(ann.page, 1), 'pointerup', { x: to.x + 10, y: from.y });
  await expect.poll(() => chipKeys(bob.page)).toEqual(['ORD-2', 'ORD-3', 'ORD-1', 'ORD-4']);
});

test('an observer leaves the table and does not hold up the auto-reveal', async ({ open }) => {
  test.setTimeout(60_000);
  const { players } = await table(open, ['Ann Observer', 'Bob Observer']);
  const [ann, bob] = players;
  await ann.page.getByRole('button', { name: 'Observer mode' }).click();
  await expect(ann.page.getByText('You are an observer')).toBeVisible();
  await expect(ann.page.locator('.nt-hand')).toBeHidden();
  await expect(seat(bob.page, ann.user)).toHaveCount(0);

  await handCard(bob.page, 3).click();
  await bob.page.getByRole('button', { name: 'Lock In' }).click();
  for (const { page } of players) {
    await expect(page.locator('.nt-stack-pill')).toHaveText(['1 x 3 pts'], { timeout: 30_000 });
  }

  await ann.page.getByRole('button', { name: 'Stop observing' }).click();
  await expect(ann.page.getByRole('button', { name: 'Observer mode' })).toHaveAttribute('aria-pressed', 'false');
});

test('a guest names themself and plays in a quick session', async ({ open }) => {
  const { players, session } = await table(open, ['Ann Host']);
  const [ann] = players;
  const guest = await open();
  await guest.page.goto(`/poker/${session.code}`);
  await guest.page.getByRole('textbox', { name: 'Your name' }).fill('Gus Guest');
  await guest.page.getByRole('button', { name: 'Join' }).click();
  const mine = guest.page.locator('.nt-seat[data-me]');
  await expect(mine.locator('.nt-badge-guest')).toBeVisible();

  const seen = ann.page.locator('.nt-seat', { hasText: 'Gus Guest' });
  await expect(seen.locator('.nt-badge-guest')).toBeVisible();
  await handCard(guest.page, 13).click();
  await guest.page.getByRole('button', { name: 'Lock In' }).click();
  await expect(seen).toHaveAttribute('data-status', 'locked');
  await openTable(guest.page, session.code);
  await expect(guest.page.locator('.nt-seat[data-me]')).toHaveAttribute('data-status', 'locked');
});
