// The spotlight over the real server (§c.4, R6): take, follow, give, a joiner landing on it, Next moving followers
// of someone else's spotlight, a hidden holder keeping it, and the release three minutes after the holder leaves.
import { expect, seedUsers, test } from './_real.js';
import { chooseSpotlight, openTable, shownRound, spotlightControl, table } from './_poker.js';

const chip = (page, n) => page.locator('.nt-strip .nt-chip').nth(n - 1);

async function addRounds(page, keys) {
  for (const key of keys) {
    await page.getByRole('button', { name: 'New round' }).click();
    await page.getByRole('textbox', { name: 'Ticket key (optional)' }).fill(key);
    await page.keyboard.press('Enter');
    await expect(shownRound(page)).toContainText(key);
  }
}

test('take, follow, give and a joiner landing on the spotlight; Next still moves every follower', async ({ open }) => {
  const { players, session } = await table(open, ['Ann Spot', 'Bob Spot', 'Cy Spot']);
  const [ann, bob, cy] = players;
  // The first key fills the untouched round 1 instead of adding one (§c.4, "fill placeholder").
  await addRounds(ann.page, ['SPT-1', 'SPT-2']);
  for (const { page } of players) {
    await expect(shownRound(page)).toHaveText('Round 2 · SPT-2');
  }
  // Round 1 is done, so Continue (poker.round.next) is what ends round 2 later on.
  await chip(ann.page, 1).locator('.nt-chip-main').click();
  await ann.page.getByRole('button', { name: 'Reveal Cards' }).click();
  await ann.page.getByRole('button', { name: 'Next Round', exact: true }).click();
  await expect(shownRound(ann.page)).toHaveText('Round 2 · SPT-2');

  await chip(bob.page, 1).locator('.nt-chip-main').click();
  await expect(shownRound(bob.page)).toHaveText('Round 1 · SPT-1');
  await chooseSpotlight(bob.page, 'Spotlight this round');
  await expect(spotlightControl(bob.page)).toHaveAttribute('data-role', 'bound');
  for (const { page } of [ann, cy]) {
    await expect(shownRound(page)).toHaveText('Round 1 · SPT-1');
    await expect(spotlightControl(page)).toHaveAttribute('data-role', 'other');
    await expect(page.locator('.nt-spotlight button')).toHaveAccessibleName('Spotlight: Bob Spot');
  }

  await chip(bob.page, 2).locator('.nt-chip-main').click();
  for (const { page } of [ann, cy]) {
    await expect(shownRound(page)).toHaveText('Round 2 · SPT-2');
  }

  const dee = await open((await seedUsers(['Dee Spot']))[0]);
  await openTable(dee.page, session.code);
  await expect(shownRound(dee.page)).toHaveText('Round 2 · SPT-2');
  await openTable(dee.page, session.code, '?round=1');
  await expect(shownRound(dee.page)).toHaveText('Round 1 · SPT-1');
  await dee.context.close();

  await chooseSpotlight(bob.page, 'Give to…');
  const dialog = bob.page.getByRole('dialog', { name: 'Give the spotlight to…' });
  await dialog.locator('.nt-give-person', { hasText: 'Cy Spot' }).click();
  await expect(spotlightControl(cy.page)).toHaveAttribute('data-role', 'mine');
  await expect(spotlightControl(bob.page)).toHaveAttribute('data-role', 'other');
  await expect(ann.page.locator('.nt-spotlight button')).toHaveAccessibleName('Spotlight: Cy Spot');

  // Ann does not hold the spotlight: her Continue moves everyone, Bob and Cy included, while it stays on round 2.
  await ann.page.getByRole('button', { name: 'Reveal Cards' }).click();
  await ann.page.getByRole('button', { name: 'Continue' }).click();
  await ann.page.getByRole('dialog', { name: 'Start New Round' }).getByRole('textbox').press('Enter');
  for (const { page } of players) {
    await expect(page).toHaveURL(/[?&]round=3$/);
  }
  await expect(chip(ann.page, 2)).toHaveAttribute('data-spotlight', '');
});

test('a holder whose page is hidden for six minutes keeps the spotlight', async ({ open }) => {
  const { players } = await table(open, ['Ann Hidden', 'Bob Hidden']);
  const [ann, bob] = players;
  await bob.page.clock.install();
  await bob.page.reload();
  await bob.page.locator('.nt-table').waitFor();
  await chooseSpotlight(bob.page, 'Spotlight this round');
  await expect(spotlightControl(ann.page)).toHaveAttribute('data-role', 'other');

  await bob.page.evaluate(() => {
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' });
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await bob.page.clock.fastForward('06:00');
  await ann.page.waitForTimeout(1500);

  await expect(spotlightControl(ann.page)).toHaveAttribute('data-role', 'other');
  await expect(ann.page.locator('.nt-spotlight button')).toHaveAccessibleName('Spotlight: Bob Hidden');
  await bob.page.evaluate(() => {
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'visible' });
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => false });
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await expect(spotlightControl(bob.page)).toHaveAttribute('data-role', /bound|mine/);
});

test('the spotlight is released three minutes after its holder leaves, not before', async ({ open }) => {
  test.setTimeout(300_000);
  const { players } = await table(open, ['Ann Leave', 'Bob Leave']);
  const [ann, bob] = players;
  await chooseSpotlight(bob.page, 'Spotlight this round');
  await expect(spotlightControl(ann.page)).toHaveAttribute('data-role', 'other');

  await bob.context.close();
  const left = Date.now();
  await expect(ann.page.locator(`.nt-seat[data-pid="${bob.user.id}"]`)).toHaveAttribute('data-absent', '', { timeout: 30_000 });
  await ann.page.waitForTimeout(150_000 - (Date.now() - left));
  await expect(spotlightControl(ann.page)).toHaveAttribute('data-role', 'other');

  await expect(spotlightControl(ann.page)).toHaveAttribute('data-role', 'none', { timeout: 60_000 });
  // Grace for the leave (10 s) plus the spotlight's three minutes, give or take the socket close.
  expect(Date.now() - left).toBeGreaterThan(180_000);
});
