import { expect, test } from '@playwright/test';
import { expectClean, expectValue, openEditor } from './_page.js';

const UPLOADED = '9b2e4f1c-6c1d-4f7a-9a43-0d1f2a3b4c5d';

async function pasteImages(page, names) {
  await page.evaluate((files) => {
    const data = new DataTransfer();
    for (const name of files) {
      data.items.add(new File([new Uint8Array([137, 80, 78, 71])], name, { type: 'image/png' }));
    }
    // Firefox empties the DataTransfer of a constructed ClipboardEvent, so the data rides on a plain event.
    const event = new Event('paste', { bubbles: true, cancelable: true });
    Object.defineProperty(event, 'clipboardData', { value: data });
    window.editor.input.dispatchEvent(event);
  }, names);
}

async function openHeld(page) {
  await openEditor(page, { value: 'pic', uploads: 'held' });
  await page.evaluate(() => {
    window.unhandled = [];
    window.addEventListener('unhandledrejection', (e) => window.unhandled.push(String(e.reason)));
  });
}

const settle = (page, index, outcome) => page.evaluate(([i, o]) => window.pendingUploads[i][o](), [index, outcome]);

test.afterEach(async ({ page }) => {
  await expectClean(page);
  expect(await page.evaluate(() => window.unhandled)).toEqual([]);
});

test('a placeholder shows while an image uploads and gives way to the image', async ({ page }) => {
  await openHeld(page);
  await pasteImages(page, ['shot.png']);
  const placeholder = page.locator('.rs-editor-upload');
  await expect(placeholder).toHaveCount(1);
  await expect(placeholder).toHaveAttribute('role', 'status');
  expect(await page.evaluate(() => window.editor.getValue())).toBe('pic');

  await settle(page, 0, 'resolve');
  await expectValue(page, `pic![image](blob:${UPLOADED})`);
  await expect(placeholder).toHaveCount(0);
  expect(await page.evaluate(() => window.log.errors)).toEqual([]);
});

test('a failed upload removes its placeholder and reports the error', async ({ page }) => {
  await openHeld(page);
  await pasteImages(page, ['shot.png']);
  await expect(page.locator('.rs-editor-upload')).toHaveCount(1);

  await settle(page, 0, 'reject');
  await expect(page.locator('.rs-editor-upload')).toHaveCount(0);
  await expect.poll(() => page.evaluate(() => window.log.errors)).toEqual([{ message: 'upload failed', file: 'shot.png' }]);
  await expectValue(page, 'pic');
  await expect(page.locator('.rs-editor-input img')).toHaveCount(0);
});

test('one failed upload does not stop the images after it', async ({ page }) => {
  await openHeld(page);
  await pasteImages(page, ['a.png', 'b.png']);
  await expect(page.locator('.rs-editor-upload')).toHaveCount(2);

  await settle(page, 0, 'reject');
  await expect(page.locator('.rs-editor-upload')).toHaveCount(1);
  await expect.poll(() => page.evaluate(() => window.pendingUploads.length)).toBe(2);
  await settle(page, 1, 'resolve');
  await expectValue(page, `pic![image](blob:${UPLOADED})`);
  await expect(page.locator('.rs-editor-upload')).toHaveCount(0);
  expect(await page.evaluate(() => window.log.errors)).toEqual([{ message: 'upload failed', file: 'a.png' }]);
});

test('an upload that fails after the editor is destroyed reports nothing', async ({ page }) => {
  await openHeld(page);
  await pasteImages(page, ['shot.png']);
  await expect(page.locator('.rs-editor-upload')).toHaveCount(1);

  await page.evaluate(() => window.editor.destroy());
  await settle(page, 0, 'reject');
  await page.evaluate(() => new Promise((resolve) => setTimeout(resolve, 20)));
  expect(await page.evaluate(() => window.log.errors)).toEqual([]);
  await expect(page.locator('.rs-editor-upload')).toHaveCount(0);
});
