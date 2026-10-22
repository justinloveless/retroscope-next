import { expect, test } from '../_harness/fixtures.js';
import { BOB, DOT, GUS, ME, ROOM, chipKeys, openStrip, setupStrip, shownRound } from './_strip.js';

const settingsDialog = (page) => page.getByRole('dialog', { name: 'Session Configuration' });

async function openSettings(page) {
  await page.getByRole('button', { name: 'Session settings' }).click();
  await expect(settingsDialog(page)).toBeVisible();
  return settingsDialog(page);
}

test('changed settings are sent with the values the dialog opened with as expected', async ({ page, fake }) => {
  const state = setupStrip(fake);
  await openStrip(page);
  const dialog = await openSettings(page);
  await expect(dialog.getByRole('switch', { name: 'Enable presence detection' })).toHaveAttribute('aria-checked', 'true');
  await expect(dialog.getByRole('switch', { name: 'Follow spotlight ticket' })).toHaveAttribute('aria-checked', 'true');

  await dialog.getByRole('switch', { name: 'Follow spotlight ticket' }).click();
  await page.getByRole('button', { name: 'Save changes' }).click();
  await expect(dialog).toBeHidden();
  await expect.poll(() => state.sent['poker.settings.update']).toEqual([{ followSpotlight: false, expected: { followSpotlight: true } }]);
  expect(state.settings).toEqual({ presenceEnabled: true, followSpotlight: false });

  const again = await openSettings(page);
  await expect(again.getByRole('switch', { name: 'Follow spotlight ticket' })).toHaveAttribute('aria-checked', 'false');
  await page.getByRole('button', { name: 'Cancel' }).click();
});

test('saving without a change sends nothing', async ({ page, fake }) => {
  const state = setupStrip(fake);
  await openStrip(page);
  await openSettings(page);
  await page.getByRole('button', { name: 'Save changes' }).click();
  await page.waitForTimeout(200);
  expect(state.sent['poker.settings.update']).toBeUndefined();
});

test('a concurrent change is refused stale and the fresh value stays', async ({ page, fake }) => {
  const state = setupStrip(fake);
  await openStrip(page);
  const dialog = await openSettings(page);
  await dialog.getByRole('switch', { name: 'Enable presence detection' }).click();

  // Someone else turns presence off while the dialog is open.
  state.settings.presenceEnabled = false;
  fake.tx(ROOM, [{ k: 'poker.settings', d: { ...state.settings } }]);
  await page.getByRole('button', { name: 'Save changes' }).click();

  await expect.poll(() => state.sent['poker.settings.update']).toEqual([{ presenceEnabled: false, expected: { presenceEnabled: true } }]);
  expect(state.settings.presenceEnabled).toBe(false);
  // Stale settings are dropped silently (U28 STALE policy): no toast, and the dialog shows the fresh value.
  await expect(page.locator('.toast')).toHaveCount(0);
  const again = await openSettings(page);
  await expect(again.getByRole('switch', { name: 'Enable presence detection' })).toHaveAttribute('aria-checked', 'false');
});

test.describe('observers', () => {
  test('the toolbar toggle makes me an observer and back', async ({ page, fake }) => {
    const state = setupStrip(fake);
    await openStrip(page);
    await expect(page.locator('.nt-hand')).toBeVisible();

    await page.getByRole('button', { name: 'Observer mode' }).click();
    await expect(page.getByRole('button', { name: 'Stop observing' })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('.nt-observer')).toBeVisible();
    await expect(page.locator(`.nt-seat[data-pid="${ME.pid}"]`)).toHaveCount(0);
    await expect(page.locator('.nt-hand')).toBeHidden();

    await page.getByRole('button', { name: 'Stop observing' }).click();
    await expect(page.getByRole('button', { name: 'Observer mode' })).toHaveAttribute('aria-pressed', 'false');
    await expect(page.locator('.nt-hand')).toBeVisible();
    await expect(page.locator(`.nt-seat[data-pid="${ME.pid}"]`)).toHaveCount(0);
    expect(state.sent['poker.observer.set']).toEqual([
      { principalId: ME.pid, observer: true },
      { principalId: ME.pid, observer: false },
    ]);
  });

  test('anyone can mark others as observers from the settings checklist', async ({ page, fake }) => {
    const state = setupStrip(fake, { snap: { observers: [DOT.pid] } });
    await openStrip(page);
    const dialog = await openSettings(page);
    const list = dialog.locator('.nt-observer-option');
    await expect(list).toHaveText(['Ada (you)', 'Bob', 'Dot', 'Gus']);
    await expect(dialog.getByRole('checkbox', { name: 'Dot' })).toBeChecked();

    await dialog.getByRole('checkbox', { name: 'Gus' }).check();
    await dialog.getByRole('checkbox', { name: 'Dot' }).uncheck();
    await page.getByRole('button', { name: 'Save changes' }).click();

    await expect.poll(() => state.observers).toEqual([GUS.pid]);
    expect(state.sent['poker.observer.set']).toEqual(expect.arrayContaining([
      { principalId: GUS.pid, observer: true },
      { principalId: DOT.pid, observer: false },
    ]));
    expect(state.sent['poker.settings.update']).toBeUndefined();
    // Observers keep their seat, dimmed and without a card.
    await expect(page.locator(`.nt-seat[data-pid="${GUS.pid}"]`)).toHaveAttribute('data-observer', '');
    await expect(page.locator(`.nt-seat[data-pid="${GUS.pid}"]`)).toHaveAttribute('aria-label', 'Gus (guest), observer');
    await expect(page.locator(`.nt-seat[data-pid="${DOT.pid}"]`)).not.toHaveAttribute('data-observer', '');
    await expect(page.locator(`.nt-seat[data-pid="${BOB.pid}"]`)).toHaveCount(1);
  });
});

test.describe('delete all rounds', () => {
  test('a team admin clears every round after confirming', async ({ page, fake }) => {
    const state = setupStrip(fake, { role: 'owner' });
    await openStrip(page);
    const dialog = await openSettings(page);
    await dialog.getByRole('button', { name: 'Delete all rounds' }).click();
    await page.getByRole('dialog', { name: 'Delete all rounds?' }).getByRole('button', { name: 'Delete all rounds' }).click();

    await expect.poll(() => chipKeys(page)).toEqual(['Round 1']);
    await expect(shownRound(page)).toHaveText('Round 1');
    expect(state.sent['poker.rounds.clearAll']).toHaveLength(1);
    await expect(settingsDialog(page)).toBeHidden();
  });

  test('a team member does not get the danger zone', async ({ page, fake }) => {
    setupStrip(fake, { role: 'member' });
    await openStrip(page);
    const dialog = await openSettings(page);
    await expect(dialog.getByRole('button', { name: 'Delete all rounds' })).toHaveCount(0);
  });

  test('in a quick session any signed-in user may', async ({ page, fake }) => {
    setupStrip(fake, { snap: { teamId: undefined } });
    await openStrip(page);
    await expect((await openSettings(page)).getByRole('button', { name: 'Delete all rounds' })).toBeVisible();
  });

  test('guests of a quick session do not get the danger zone', async ({ page, fake }) => {
    setupStrip(fake, { viewer: GUS, snap: { teamId: undefined } });
    await openStrip(page);
    await expect((await openSettings(page)).getByRole('button', { name: 'Delete all rounds' })).toHaveCount(0);
  });
});
