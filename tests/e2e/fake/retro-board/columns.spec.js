import { expect, test } from '../_harness/fixtures.js';
import { ACTIONS, BAD, BOB, GOOD, ROOM, column, openBoard, sent, serveBoard, snapshot } from './_board.js';

const titles = (page) => page.locator('.retro-column-title').allTextContents();

test('a facilitator adds, edits and deletes columns', async ({ page, fake }) => {
  serveBoard(fake);
  await openBoard(page, fake);

  await page.getByRole('button', { name: 'Add column' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Title').fill('Ideas');
  await dialog.getByRole('radio', { name: 'Indigo' }).check();
  await dialog.getByRole('button', { name: 'Add column' }).click();
  await expect.poll(() => titles(page)).toEqual(['Good', 'Bad', 'Action Items', 'Ideas']);
  expect(sent(fake, 'retro.column.add')).toEqual([expect.objectContaining({ title: 'Ideas', color: 'indigo' })]);
  await expect(column(page, 'Ideas')).toHaveAttribute('data-color', 'indigo');

  await page.getByRole('button', { name: 'Actions for Bad' }).click();
  await page.getByRole('menuitem', { name: 'Edit column' }).click();
  await dialog.getByLabel('Title').fill('Could be better');
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect.poll(() => titles(page)).toEqual(['Good', 'Could be better', 'Action Items', 'Ideas']);
  expect(sent(fake, 'retro.column.update')).toEqual([{ columnId: BAD, baseVersion: 1, title: 'Could be better' }]);

  await page.getByRole('button', { name: 'Actions for Ideas' }).click();
  await page.getByRole('menuitem', { name: 'Delete column' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete column' }).click();
  await expect.poll(() => titles(page)).toEqual(['Good', 'Could be better', 'Action Items']);
});

test('the action items column moves with one command', async ({ page, fake }) => {
  serveBoard(fake);
  await openBoard(page, fake);
  await expect(column(page, 'Action Items').locator('.retro-action-badge')).toBeVisible();

  await page.getByRole('button', { name: 'Actions for Good' }).click();
  await page.getByRole('menuitem', { name: 'Use for action items' }).click();
  await expect(column(page, 'Good').locator('.retro-action-badge')).toBeVisible();
  await expect(column(page, 'Action Items').locator('.retro-action-badge')).toBeHidden();

  await page.getByRole('button', { name: 'Actions for Good' }).click();
  await page.getByRole('menuitem', { name: 'Not the action items column' }).click();
  await expect(page.locator('.retro-action-badge:visible')).toHaveCount(0);
  expect(sent(fake, 'retro.column.setActionItems')).toEqual([{ columnId: GOOD }, { columnId: null }]);
});

test('a column is reordered by dragging its grip', async ({ page, fake }) => {
  serveBoard(fake);
  await openBoard(page, fake);
  const grip = page.getByRole('button', { name: 'Move column Good' });
  const target = await column(page, 'Action Items').boundingBox();
  const start = await grip.boundingBox();

  await page.mouse.move(start.x + start.width / 2, start.y + start.height / 2);
  await page.mouse.down();
  await page.mouse.move(start.x + 30, start.y + 5, { steps: 3 });
  await expect(column(page, 'Good')).toHaveClass(/retro-column-dragging/);
  await page.mouse.move(target.x + target.width - 5, target.y + 20, { steps: 8 });
  await page.mouse.up();

  await expect.poll(() => titles(page)).toEqual(['Bad', 'Action Items', 'Good']);
  expect(sent(fake, 'retro.column.reorder')).toEqual([{ order: [BAD, ACTIONS, GOOD] }]);
  await expect(column(page, 'Good')).not.toHaveClass(/retro-column-dragging/);
});

test('arrow keys on the grip move a column one place', async ({ page, fake }) => {
  serveBoard(fake);
  await openBoard(page, fake);
  await page.getByRole('button', { name: 'Move column Bad' }).focus();
  await page.keyboard.press('ArrowLeft');
  await expect.poll(() => titles(page)).toEqual(['Bad', 'Good', 'Action Items']);
  await expect(page.getByRole('button', { name: 'Move column Bad' })).toBeFocused();
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('ArrowRight');
  await expect.poll(() => titles(page)).toEqual(['Good', 'Action Items', 'Bad']);
});

test('a stale reorder is rebased on the new columns and sent once more', async ({ page, fake }) => {
  const extra = { id: 'c0000000-0000-4000-8000-000000000009', title: 'Late', color: 'gray', actionItems: false, version: 1 };
  await openBoard(page, fake);
  let calls = 0;
  fake.onCommand('retro.column.reorder', (ctx) => {
    calls += 1;
    if (calls === 1) {
      fake.tx(ROOM, [{ k: 'retro.column.added', d: { column: extra } }]);
      ctx.nack('stale', { d: { current: [GOOD, BAD, ACTIONS, extra.id] } });
    } else {
      ctx.commit([{ k: 'retro.column.order', d: ctx.frame.d }]);
    }
  });
  await page.getByRole('button', { name: 'Move column Action Items' }).focus();
  await page.keyboard.press('ArrowLeft');

  await expect.poll(() => sent(fake, 'retro.column.reorder')).toEqual([
    { order: [GOOD, ACTIONS, BAD] },
    { order: [GOOD, ACTIONS, BAD, extra.id] },
  ]);
  await expect.poll(() => titles(page)).toEqual(['Good', 'Action Items', 'Bad', 'Late']);
});

test.describe('who facilitates', () => {
  test('a team board offers column tools to members only', async ({ page, fake }) => {
    const nonMember = { pid: 'd4000000-0000-4000-8000-000000000005', kind: 'user', name: 'Dana' };
    const snap = snapshot();
    const { members, endorsements, endorsementTypes, maxEndorsements, ...visible } = snap;
    await openBoard(page, fake, { snap: visible, me: nonMember });
    await expect(page.getByRole('button', { name: 'Add column' })).toBeHidden();
    await expect(page.getByRole('button', { name: 'Move column Good' })).toBeHidden();
    await expect(page.getByRole('button', { name: 'Actions for Good' })).toBeHidden();
  });

  test('a member renames the board inline', async ({ page, fake }) => {
    await openBoard(page, fake, { me: BOB, snap: snapshot() });
    await page.getByRole('button', { name: 'Rename board' }).click();
    const input = page.getByRole('textbox', { name: 'Board title' });
    await input.fill('Sprint 43 retro');
    await input.press('Enter');
    await expect.poll(() => sent(fake, 'retro.board.rename')).toEqual([{ title: 'Sprint 43 retro', expected: 'Sprint 42 retro' }]);
  });

  test('an archived board is read-only', async ({ page, fake }) => {
    const snap = snapshot({ meta: { title: 'Old retro', archived: true } });
    await openBoard(page, fake, { snap });
    await expect(page.getByText('This board is archived and read-only.')).toBeVisible();
    await expect(page.locator('.retro-add-card:visible')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Add column' })).toBeHidden();
  });
});
