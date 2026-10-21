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
  const box = await page.getByRole('separator', { name: 'Resize Jira panel' }).boundingBox();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x - 300, box.y + box.height / 2, { steps: 5 });
  await page.mouse.up();
  expect(Math.round((await page.locator('.nt-panel-left').boundingBox()).width)).toBe(240);
  expect((await panels(page)).left.width).toBe(240);
});

test('a closed browse panel stays closed after a reload', async ({ page, fake }) => {
  setupJira(fake);
  await openBrowse(page);
  await page.locator('.jira-toggle').click();
  await expect(page.locator('.nt-panel-left')).toBeHidden();
  expect((await panels(page)).left.open).toBe(false);

  await page.reload();
  await expect(page.locator('.jira-toggle')).toBeVisible();
  await expect(page.locator('.nt-panel-left')).toBeHidden();
});
