import { expect, test } from '../_harness/fixtures.js';
import { BOB, ROOM, chip, chipKeys, openStrip, round, roundId, roundParam, rounds, setupStrip, shownRound } from './_strip.js';

test('chips show the ticket or round number, pending and points pills, and the open rounds', async ({ page, fake }) => {
  const list = rounds(4);
  list[0] = { ...list[0], state: 'revealed', winner: 5 };
  list[1] = { ...list[1], ticketKey: undefined, jira: { points: 3, at: 1, by: BOB.pid } };
  list[2] = { ...list[2], pending: true, pendingOwner: BOB.pid };
  setupStrip(fake, { snap: { rounds: list, currentRoundId: roundId(4) } });
  await openStrip(page);

  expect(await chipKeys(page)).toEqual(['ABC-1', 'Round 2', 'ABC-3', 'ABC-4']);
  await expect(chip(page, 1).locator('.nt-chip-points')).toHaveText('5');
  await expect(chip(page, 2).locator('.nt-chip-points')).toHaveText('3');
  await expect(chip(page, 3).locator('.nt-chip-pending')).toBeVisible();
  await expect(chip(page, 4)).toHaveClass(/is-active/);
  await expect(chip(page, 4)).toHaveClass(/is-selected/);
  await expect(chip(page, 4).locator('.nt-chip-main')).toHaveAttribute('aria-current', 'true');
  await expect(chip(page, 1)).not.toHaveClass(/is-active/);
});

test('clicking a chip navigates to that round and centres it', async ({ page, fake }) => {
  setupStrip(fake);
  await openStrip(page);
  await chip(page, 2).locator('.nt-chip-main').click();
  await expect(shownRound(page)).toHaveText('Round 2 · ABC-2');
  expect(roundParam(page)).toBe('2');
  await expect(chip(page, 2)).toHaveClass(/is-selected/);
  await expect.poll(() => page.locator('.nt-strip-scroller').evaluate((scroller) => {
    const node = scroller.querySelector('.nt-chip.is-selected');
    return Math.abs(node.offsetLeft + node.offsetWidth / 2 - scroller.clientWidth / 2 - scroller.scrollLeft);
  })).toBeLessThan(3);
});

test('a new round from the + button and the next open round button', async ({ page, fake }) => {
  const list = rounds(4).map((r, i) => ({ ...r, active: i === 1 || i === 3 }));
  setupStrip(fake, { snap: { rounds: list, currentRoundId: roundId(4) } });
  await openStrip(page);

  await page.getByRole('button', { name: 'Next open round' }).click();
  await expect(shownRound(page)).toHaveText('Round 2 · ABC-2');
  await page.getByRole('button', { name: 'Next open round' }).click();
  await expect(shownRound(page)).toHaveText('Round 4 · ABC-4');

  await page.getByRole('button', { name: 'New round' }).click();
  await page.getByRole('textbox', { name: 'Ticket key (optional)' }).fill('XYZ-9');
  await page.keyboard.press('Enter');
  await expect(shownRound(page)).toHaveText('Round 5 · XYZ-9');
  expect(await chipKeys(page)).toEqual(['ABC-1', 'ABC-2', 'ABC-3', 'ABC-4', 'XYZ-9']);
});

for (const mode of ['scrollend', 'debounce']) {
  test.describe(`navigation when a scroll settles (${mode})`, () => {
    test.beforeEach(async ({ page, browserName }, testInfo) => {
      if (mode === 'debounce') {
        // Browsers without scrollend: the strip falls back to a pause in scroll events (F19).
        await page.addInitScript(() => {
          for (const proto of [HTMLElement.prototype, Element.prototype, Document.prototype, Window.prototype]) {
            delete proto.onscrollend;
          }
          delete window.onscrollend;
        });
      } else {
        test.skip(await page.evaluate(() => !('onscrollend' in document.createElement('div'))), `${browserName} (${testInfo.project.name}) has no scrollend`);
      }
    });

    test('a wheel turn moves the strip one chip per step and the settled chip is opened', async ({ page, fake }) => {
      setupStrip(fake);
      await openStrip(page);
      await expect(page.locator('.nt-strip')).toHaveAttribute('data-settle', mode);
      await expect(shownRound(page)).toHaveText('Round 6 · ABC-6');

      await page.locator('.nt-strip-scroller').dispatchEvent('wheel', { deltaY: -200, deltaX: 0, deltaMode: 0 });
      await expect(shownRound(page)).toHaveText('Round 4 · ABC-4');
      expect(roundParam(page)).toBe('4');
    });

    test('a scroll the page made itself does not navigate', async ({ page, fake }) => {
      setupStrip(fake);
      await openStrip(page);
      await page.locator('.nt-strip-scroller').evaluate((el) => el.scrollTo({ left: 0 }));
      await page.waitForTimeout(400);
      await expect(shownRound(page)).toHaveText('Round 6 · ABC-6');
    });
  });
}

test.describe('context menu', () => {
  test('moves a round, activates an ended one and deletes as a team admin', async ({ page, fake }) => {
    const state = setupStrip(fake, { role: 'admin' });
    await openStrip(page);

    await chip(page, 1).click({ button: 'right' });
    await expect(page.getByRole('menuitem', { name: 'Move left' })).toBeDisabled();
    await page.getByRole('menuitem', { name: 'Move right' }).click();
    await expect.poll(() => chipKeys(page)).toEqual(['ABC-2', 'ABC-1', 'ABC-3', 'ABC-4', 'ABC-5', 'ABC-6']);

    await chip(page, 3).click({ button: 'right' });
    await page.getByRole('menuitem', { name: 'Activate round' }).click();
    await expect(chip(page, 3)).toHaveClass(/is-active/);
    expect(state.sent['poker.round.activate']).toEqual([{ roundId: roundId(3) }]);

    await chip(page, 5).click({ button: 'right' });
    await page.getByRole('menuitem', { name: 'Delete round' }).click();
    await page.getByRole('button', { name: 'Delete round' }).click();
    await expect(chip(page, 5)).toHaveCount(0);
    expect(state.sent['poker.round.delete']).toEqual([{ roundId: roundId(5) }]);
  });

  test('members of a team session cannot delete rounds; active rounds offer no Activate', async ({ page, fake }) => {
    setupStrip(fake, { role: 'member' });
    await openStrip(page);
    await chip(page, 6).click({ button: 'right' });
    await expect(page.getByRole('menuitem', { name: 'Move right' })).toBeDisabled();
    await expect(page.getByRole('menuitem', { name: 'Delete round' })).toHaveCount(0);
    await expect(page.getByRole('menuitem', { name: 'Activate round' })).toHaveCount(0);
  });
});

test('rounds added by others appear in the strip; a deleted viewed round sends the viewer to the pointer', async ({ page, fake }) => {
  const state = setupStrip(fake);
  await openStrip(page);
  await chip(page, 2).locator('.nt-chip-main').click();
  await expect(shownRound(page)).toHaveText('Round 2 · ABC-2');

  const extra = round('0a000000-0000-4000-8000-000000001099', 7, { ticketKey: 'NEW-7' });
  state.rounds = [...state.rounds, extra];
  fake.tx(ROOM, [{ k: 'poker.round.created', d: { round: extra } }]);
  await expect.poll(() => chipKeys(page)).toContain('NEW-7');
  await expect(shownRound(page)).toHaveText('Round 2 · ABC-2');

  fake.tx(ROOM, [{ k: 'poker.round.deleted', d: { roundId: roundId(2) } }]);
  await expect(shownRound(page)).toHaveText('Round 6 · ABC-6');
});

test('chips show the issue-type glyph once the room knows the issue', async ({ page, fake }) => {
  setupStrip(fake);
  await openStrip(page);
  await expect(chip(page, 2).locator('.nt-chip-icon svg[aria-label="Issue"]')).toHaveCount(1);

  fake.x(ROOM, 'jira.meta', { key: 'ABC-2', summary: 'Crash on save', issueType: 'Bug' });
  await expect(chip(page, 2).locator('.nt-chip-icon svg[aria-label="Bug"]')).toHaveCount(1);
  await expect(chip(page, 3).locator('.nt-chip-icon svg[aria-label="Issue"]')).toHaveCount(1);
});
