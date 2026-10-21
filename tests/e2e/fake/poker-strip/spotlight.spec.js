import { expect, test } from '../_harness/fixtures.js';
import { BOB, CODE, GUS, ME, ROOM, chip, openStrip, otherSpotlight, roundId, roundParam, setupStrip, shownRound } from './_strip.js';

const spotlightButton = (page) => page.locator('.nt-spotlight button');
const control = (page) => page.locator('.nt-spotlight');

async function choose(page, item) {
  await spotlightButton(page).click();
  await page.getByRole('menuitem', { name: item }).click();
}

test('spotlighting a free spotlight binds this tab, marks the chip and shows my avatar', async ({ page, fake }) => {
  const state = setupStrip(fake);
  await openStrip(page);
  await expect(control(page)).toHaveAttribute('data-role', 'none');

  await choose(page, 'Spotlight this round');
  expect(state.sent['poker.spotlight.take']).toEqual([{ roundId: roundId(6), force: false }]);
  await expect(control(page)).toHaveAttribute('data-role', 'bound');
  await expect(spotlightButton(page)).toHaveAttribute('aria-pressed', 'true');
  await expect(chip(page, 6)).toHaveAttribute('data-spotlight', '');
  await expect(chip(page, 6).locator('.nt-chip-holder .avatar')).toHaveAttribute('aria-label', ME.name);
  await expect(page.locator('.nt-spotlight-holder .avatar')).toBeVisible();
});

test('the bound tab drives the spotlight: moves trail navigation by 150 ms', async ({ page, fake }) => {
  const state = setupStrip(fake);
  await openStrip(page);
  await choose(page, 'Spotlight this round');
  await expect(control(page)).toHaveAttribute('data-role', 'bound');

  // Two steps in one turn of the event loop, well inside the trailing window on any machine.
  await page.evaluate(() => {
    for (let i = 0; i < 2; i++) {
      document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', code: 'ArrowLeft', bubbles: true, cancelable: true }));
    }
  });
  await expect(shownRound(page)).toHaveText('Round 4 · ABC-4');
  await expect(chip(page, 4)).toHaveAttribute('data-spotlight', '');
  // Two steps inside the trailing window send one move, to where the tab ended up.
  expect(state.sent['poker.spotlight.move']).toEqual([{ roundId: roundId(4) }]);
  await expect(control(page)).toHaveAttribute('data-role', 'bound');
});

test('taking it from someone else asks "Take spotlight?" and resends with force', async ({ page, fake }) => {
  const state = setupStrip(fake, { snap: { spotlight: { holder: BOB.pid, roundId: roundId(6) } } });
  await openStrip(page);
  await expect(control(page)).toHaveAttribute('data-role', 'other');
  await expect(spotlightButton(page)).toHaveAccessibleName('Spotlight: Bob');

  await choose(page, 'Take spotlight from Bob');
  const dialog = page.getByRole('dialog', { name: 'Take spotlight?' });
  await expect(dialog).toContainText('Bob is spotlighting');
  await dialog.getByRole('button', { name: 'Take spotlight' }).click();

  await expect(control(page)).toHaveAttribute('data-role', 'bound');
  expect(state.sent['poker.spotlight.take']).toEqual([
    { roundId: roundId(6), force: false },
    { roundId: roundId(6), force: true },
  ]);
});

test('cancelling "Take spotlight?" leaves it with the holder', async ({ page, fake }) => {
  const state = setupStrip(fake, { snap: { spotlight: { holder: BOB.pid, roundId: roundId(6) } } });
  await openStrip(page);
  await choose(page, 'Take spotlight from Bob');
  await page.getByRole('dialog', { name: 'Take spotlight?' }).getByRole('button', { name: 'Cancel' }).click();
  await page.waitForTimeout(200);
  expect(state.sent['poker.spotlight.take']).toEqual([{ roundId: roundId(6), force: false }]);
  await expect(control(page)).toHaveAttribute('data-role', 'other');
});

test('stop spotlighting releases it', async ({ page, fake }) => {
  const state = setupStrip(fake);
  await openStrip(page);
  await choose(page, 'Spotlight this round');
  await expect(control(page)).toHaveAttribute('data-role', 'bound');
  await choose(page, 'Stop spotlighting');
  await expect(control(page)).toHaveAttribute('data-role', 'none');
  await expect(chip(page, 6)).not.toHaveAttribute('data-spotlight', '');
  expect(state.sent['poker.spotlight.release']).toEqual([{}]);
});

test('give lists present people and hands the spotlight over', async ({ page, fake }) => {
  const state = setupStrip(fake);
  await openStrip(page);
  await choose(page, 'Spotlight this round');
  await expect(control(page)).toHaveAttribute('data-role', 'bound');

  await choose(page, 'Give to…');
  const dialog = page.getByRole('dialog', { name: 'Give the spotlight to…' });
  // Dot is on the roster but not present; the holder is not offered.
  await expect(dialog.locator('.nt-give-person')).toHaveText([/Bob$/, /Gus$/]);
  await dialog.locator('.nt-give-person', { hasText: 'Bob' }).click();

  expect(state.sent['poker.spotlight.give']).toEqual([{ principalId: BOB.pid }]);
  await expect(control(page)).toHaveAttribute('data-role', 'other');
  await expect(spotlightButton(page)).toHaveAccessibleName('Spotlight: Bob');
});

test.describe('following', () => {
  test('followers go wherever the holder moves the spotlight', async ({ page, fake }) => {
    const state = setupStrip(fake, { snap: { spotlight: { holder: BOB.pid, roundId: roundId(6) } } });
    await openStrip(page);
    otherSpotlight(fake, state, BOB, roundId(2));
    await expect(shownRound(page)).toHaveText('Round 2 · ABC-2');
    expect(roundParam(page)).toBe('2');
    await expect(chip(page, 2)).toHaveAttribute('data-spotlight', '');

    // Browsing away is allowed; the next spotlight move brings the follower back.
    await chip(page, 5).locator('.nt-chip-main').click();
    await expect(shownRound(page)).toHaveText('Round 5 · ABC-5');
    otherSpotlight(fake, state, BOB, roundId(3));
    await expect(shownRound(page)).toHaveText('Round 3 · ABC-3');
  });

  test('with follow off the spotlight is shown but nobody is moved', async ({ page, fake }) => {
    const state = setupStrip(fake, { snap: { followSpotlight: false, spotlight: { holder: BOB.pid, roundId: roundId(6) } } });
    await openStrip(page);
    otherSpotlight(fake, state, BOB, roundId(2));
    await expect(chip(page, 2)).toHaveAttribute('data-spotlight', '');
    await expect(shownRound(page)).toHaveText('Round 6 · ABC-6');
  });

  test('a joiner lands on the spotlight round, unless the link names a round', async ({ page, fake }) => {
    setupStrip(fake, { snap: { spotlight: { holder: BOB.pid, roundId: roundId(3) } } });
    await openStrip(page);
    await expect(shownRound(page)).toHaveText('Round 3 · ABC-3');

    await openStrip(page, `/poker/${CODE}?round=5`);
    await expect(shownRound(page)).toHaveText('Round 5 · ABC-5');
  });
});

test.describe('tabs of the holder', () => {
  test('a holder whose spotlight was given (no bound tab) binds this tab by navigating', async ({ page, fake }) => {
    const state = setupStrip(fake, { snap: { spotlight: { holder: ME.pid, roundId: roundId(6) } } });
    await openStrip(page);
    await expect(control(page)).toHaveAttribute('data-role', 'mine');
    await chip(page, 4).locator('.nt-chip-main').click();
    await expect(control(page)).toHaveAttribute('data-role', 'bound');
    expect(state.sent['poker.spotlight.move']).toEqual([{ roundId: roundId(4) }]);
    await expect(chip(page, 4)).toHaveAttribute('data-spotlight', '');
  });

  test('a second window of the holder learns the spotlight is bound elsewhere and can take it over', async ({ page, fake, context }) => {
    const state = setupStrip(fake);
    await openStrip(page);
    await choose(page, 'Spotlight this round');
    await expect(control(page)).toHaveAttribute('data-role', 'bound');

    const second = await context.newPage();
    await openStrip(second);
    await expect(control(second)).toHaveAttribute('data-role', 'mine');
    await chip(second, 2).locator('.nt-chip-main').click();
    // The move is refused not_holder: the first window is bound.
    await expect(control(second)).toHaveAttribute('data-role', 'elsewhere');
    await expect(spotlightButton(second)).toHaveAccessibleName('Spotlight: Ada (another window)');
    await expect(chip(page, 6)).toHaveAttribute('data-spotlight', '');

    await choose(second, 'Spotlight from this window');
    const dialog = second.getByRole('dialog', { name: 'Take spotlight?' });
    await expect(dialog).toContainText('Your other window is spotlighting');
    await dialog.getByRole('button', { name: 'Take spotlight' }).click();
    await expect(control(second)).toHaveAttribute('data-role', 'bound');
    await expect(chip(second, 2)).toHaveAttribute('data-spotlight', '');
    expect(state.sent['poker.spotlight.take'].at(-1)).toEqual({ roundId: roundId(2), force: true });

    // The first window still believes it is bound until its next move is refused (U31 contract 3).
    await page.keyboard.press('ArrowLeft');
    await expect(control(page)).toHaveAttribute('data-role', 'elsewhere');
    await expect(chip(page, 2)).toHaveAttribute('data-spotlight', '');
    await second.close();
  });
});

test('presence: the spotlight cannot be given to someone who left', async ({ page, fake }) => {
  setupStrip(fake);
  await openStrip(page);
  fake.tx(ROOM, [{ k: 'presence.leave', d: { pid: GUS.pid } }]);
  await choose(page, 'Give to…');
  await expect(page.getByRole('dialog', { name: 'Give the spotlight to…' }).locator('.nt-give-person')).toHaveText([/Ada \(you\)$/, /Bob$/]);
});
