import { expect, test } from '../_harness/fixtures.js';
import { isPhone, openBrowse, setupJira } from './_jira.js';

const panels = (page) => page.evaluate(() => JSON.parse(localStorage.getItem('rs.panels')));

test.beforeEach(({ page }) => {
  test.skip(isPhone(page), 'phones open the browse panel as a sheet');
});

test('the browse panel resizes between 240 and 480 px and keeps its width', async ({ page, fake }) => {
  setupJira(fake);
  await openBrowse(page);
  const handle = page.getByRole('separator', { name: 'Resize Jira panel' });
  await expect(handle).toHaveAttribute('aria-valuenow', '320');
  await handle.focus();
  for (let i = 0; i < 20; i++) {
    await page.keyboard.press('ArrowRight');
  }
  await expect(handle).toHaveAttribute('aria-valuenow', '480');
  expect(Math.round((await page.locator('.nt-panel-left').boundingBox()).width)).toBe(480);
  expect((await panels(page)).left.width).toBe(480);

  await page.reload();
  await openBrowse(page);
  await expect(page.getByRole('separator', { name: 'Resize Jira panel' })).toHaveAttribute('aria-valuenow', '480');
  expect(Math.round((await page.locator('.nt-panel-left').boundingBox()).width)).toBe(480);
});

test('the browse panel resizes with the pointer down to its 240 px minimum', async ({ page, fake }) => {
  setupJira(fake);
  await openBrowse(page);
  // The grip chip on the strip (the middle of the strip is the collapse tab).
  const box = await page.getByRole('separator', { name: 'Resize Jira panel' }).locator('.resize-grip').boundingBox();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x - 300, box.y + box.height / 2, { steps: 5 });
  await page.mouse.up();
  expect(Math.round((await page.locator('.nt-panel-left').boundingBox()).width)).toBe(240);
  expect((await panels(page)).left.width).toBe(240);
});

test('the drawer tabs close and reopen the browse panel, and a closed panel stays closed after a reload', async ({ page, fake }) => {
  setupJira(fake);
  await openBrowse(page);
  await expect(page.locator('.jira-toggle')).toHaveCount(0);
  const collapse = page.getByRole('button', { name: 'Collapse Jira panel' });
  await expect(collapse).toHaveAttribute('aria-expanded', 'true');
  await expect(collapse).toHaveAttribute('aria-controls', 'nt-panel-left');
  await collapse.click();
  await expect(page.locator('.nt-panel-left')).toBeHidden();
  const open = page.getByRole('button', { name: 'Open Jira panel' });
  await expect(open).toBeFocused();
  await expect(open).toHaveAttribute('aria-expanded', 'false');
  // The open tab sits on the screen's left edge, over the stage.
  expect(Math.round((await open.boundingBox()).x)).toBe(0);
  expect((await panels(page)).left.open).toBe(false);

  await page.reload();
  await expect(page.getByRole('button', { name: 'Open Jira panel' })).toBeVisible();
  await expect(page.locator('.nt-panel-left')).toBeHidden();

  await page.getByRole('button', { name: 'Open Jira panel' }).click();
  await expect(page.locator('.jira-browse')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Collapse Jira panel' })).toBeFocused();
  expect((await panels(page)).left.open).toBe(true);
});
