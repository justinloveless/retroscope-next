// Copy link (UX18 decision 5): a board is for its team's members, so there is nothing to choose about who may
// open it. Every member copies its address with one click; there is no share dialog, visibility or password.
import { expect, test } from '../_harness/fixtures.js';
import { BOB, CODE, openBoard, snapshot } from './_stages.js';

// Records what the page puts on the clipboard (and can refuse it), the same way in every browser.
function stubClipboard(page, { refuse = false } = {}) {
  return page.addInitScript((refused) => {
    window.__copied = [];
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: {
        writeText: (text) => (refused ? Promise.reject(new Error('denied')) : Promise.resolve(window.__copied.push(text))),
      },
    });
  }, refuse);
}

test('any member copies the board’s link with one click', async ({ page, fake }) => {
  await stubClipboard(page);
  await openBoard(page, fake, { snap: snapshot(), me: BOB });

  await page.getByRole('button', { name: 'Copy link' }).click();

  await expect(page.getByText('Link copied!')).toBeVisible();
  expect(await page.evaluate(() => window.__copied)).toEqual([`${fake.url}/retro/${CODE}`]);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Share' })).toHaveCount(0);
  expect(fake.requests.filter((r) => r.method === 'PATCH' || r.path.endsWith('/unlock'))).toEqual([]);
});

test('a refused clipboard says the link was not copied', async ({ page, fake }) => {
  await stubClipboard(page, { refuse: true });
  await openBoard(page, fake);

  await page.getByRole('button', { name: 'Copy link' }).click();

  await expect(page.getByText('Could not copy the link.')).toBeVisible();
});
