// Image uploads from an action item's comment composer: the raw bytes go to the source board, and a refusal
// is explained from the problem code or, when a proxy answered without one, from the HTTP status.
import { json, problem } from '../../../web-fake-server/http.js';
import { BOARD_1, ITEM_1, TEAM_ID, setupTeam, teamUrl, expect, test } from './_team.js';

const PNG = [137, 80, 78, 71, 13, 10, 26, 10];

async function openComposer(page, fake, answer) {
  setupTeam(fake);
  const uploads = [];
  fake.route('GET', `/api/teams/${TEAM_ID}/action-items/${ITEM_1}/comments`, json({ items: [] }));
  fake.route('POST', `/api/retro/${BOARD_1}/images`, (req) => {
    uploads.push(req);
    return answer;
  });
  await page.goto(teamUrl('?tab=actions'));
  const item = page.locator(`.team-item[data-id="${ITEM_1}"]`);
  await item.getByRole('button', { name: '1 comment' }).click();
  const editor = item.getByRole('textbox', { name: 'Add a comment' });
  await expect(editor).toBeVisible();
  return { editor, uploads };
}

async function pasteImage(editor) {
  await editor.evaluate((input, bytes) => {
    const data = new DataTransfer();
    data.items.add(new File([new Uint8Array(bytes)], 'shot.png', { type: 'image/png' }));
    // Firefox empties the DataTransfer of a constructed ClipboardEvent, so the data rides on a plain event.
    const event = new Event('paste', { bubbles: true, cancelable: true });
    Object.defineProperty(event, 'clipboardData', { value: data });
    input.dispatchEvent(event);
  }, PNG);
}

test('a 413 without a problem body still says the image is too large', async ({ page, fake }) => {
  const tooLarge = { status: 413, body: 'Request Entity Too Large', headers: { 'Content-Type': 'text/plain; charset=utf-8' } };
  const { editor, uploads } = await openComposer(page, fake, tooLarge);

  await pasteImage(editor);

  await expect(page.getByRole('alert').filter({ hasText: 'Images can be at most 2 MB.' })).toBeVisible();
  expect(uploads).toHaveLength(1);
  expect(uploads[0].headers['x-rs']).toBe('1');
  expect([...uploads[0].bytes]).toEqual(PNG);
  await expect(editor.locator('img')).toHaveCount(0);
});

test('a 409 board quota says the board has no image space left', async ({ page, fake }) => {
  const { editor, uploads } = await openComposer(page, fake, problem(409, 'board_quota'));

  await pasteImage(editor);

  await expect(page.getByRole('alert').filter({ hasText: 'This board has no image space left.' })).toBeVisible();
  expect(uploads).toHaveLength(1);
  await expect(editor.locator('img')).toHaveCount(0);
});

test('a bare 409 maps to the board quota too', async ({ page, fake }) => {
  const { editor } = await openComposer(page, fake, { status: 409, body: '' });

  await pasteImage(editor);

  await expect(page.getByRole('alert').filter({ hasText: 'This board has no image space left.' })).toBeVisible();
});
