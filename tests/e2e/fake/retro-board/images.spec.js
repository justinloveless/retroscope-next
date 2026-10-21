import { json, problem } from '../../../web-fake-server/http.js';
import { expect, test } from '../_harness/fixtures.js';
import { BOARD_ID, column, editorIn, openBoard, sent, serveBoard, typeCard } from './_board.js';

const BLOB = '9b2e4f1c-6c1d-4f7a-9a43-0d1f2a3b4c5d';
const PNG = [137, 80, 78, 71, 13, 10, 26, 10];

async function pasteImage(page, scope) {
  await editorIn(scope).evaluate((input, bytes) => {
    const data = new DataTransfer();
    data.items.add(new File([new Uint8Array(bytes)], 'shot.png', { type: 'image/png' }));
    // Firefox empties the DataTransfer of a constructed ClipboardEvent, so the data rides on a plain event.
    const event = new Event('paste', { bubbles: true, cancelable: true });
    Object.defineProperty(event, 'clipboardData', { value: data });
    input.dispatchEvent(event);
  }, PNG);
}

test('a pasted image uploads as raw bytes and lands in the card', async ({ page, fake }) => {
  fake.route('POST', `/api/retro/${BOARD_ID}/images`, () => json({ blobId: BLOB }, 201));
  fake.route('GET', `/blob/${BLOB}`, { status: 404, body: '' });
  serveBoard(fake);
  await openBoard(page, fake);
  await typeCard(page, 'Good', 'Look');
  await pasteImage(page, column(page, 'Good'));
  await expect(editorIn(column(page, 'Good')).locator('img')).toHaveCount(1);
  await page.keyboard.press('Enter');

  await expect.poll(() => sent(fake, 'retro.card.add').map((d) => d.body)).toEqual([`Look![image](blob:${BLOB})`]);
  const upload = fake.requests.find((r) => r.method === 'POST' && r.path.endsWith('/images'));
  // The fake decodes bodies as UTF-8, so byte 0x89 reads back as U+FFFD.
  expect(upload.text).toBe('\uFFFDPNG\r\n\u001a\n');
  expect(upload.headers['content-type']).toBe('image/png');
  expect(upload.headers['x-rs']).toBe('1');
  await expect(column(page, 'Good').locator('.retro-card-body img')).toHaveAttribute('src', `/blob/${BLOB}`);
});

for (const [status, code, message] of [
  [409, 'board_quota', 'This board has no room for more images.'],
  [413, 'too_large', 'Images can be at most 2 MB.'],
  [415, 'unsupported_media_type', 'Only PNG, JPEG, GIF and WebP images can be added.'],
  [507, 'storage_full', 'Image storage is full. Please tell an administrator.'],
]) {
  test(`a ${status} upload is explained and the draft is kept`, async ({ page, fake }) => {
    fake.route('POST', `/api/retro/${BOARD_ID}/images`, () => problem(status, code));
    await openBoard(page, fake);
    await typeCard(page, 'Good', 'Look');
    await pasteImage(page, column(page, 'Good'));
    await expect(page.getByRole('alert').filter({ hasText: message })).toBeVisible();
    await expect(editorIn(column(page, 'Good'))).toHaveText('Look');
    await expect(editorIn(column(page, 'Good')).locator('img')).toHaveCount(0);
  });
}
