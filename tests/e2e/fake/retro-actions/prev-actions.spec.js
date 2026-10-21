import { expect, test } from '../_harness/fixtures.js';
import { json, problem } from '../../../web-fake-server/http.js';
import {
  ALICE, BOARD_ID, BOB, CAROL, OTHER_BOARD, TEAM_ID, TEAM_ROOM, boardFor, cardId, item, openBoard, serveTeam, teamRoom,
} from './_actions.js';

const prevColumn = (page) => page.locator('.retro-prev-actions');
const prevItems = (page) => prevColumn(page).locator('.retro-prev-item');

const T1 = 1_760_000_200_000;

function comment(n, fields = {}) {
  return { id: `cc000000-0000-4000-8000-${String(n).padStart(12, '0')}`, body: `Comment ${n}`, anonymous: false, createdAt: T1 + n, author: { pid: BOB.pid, name: BOB.name, guest: false }, ...fields };
}

/** Comments of item 1's source card, served by `GET` and grown by `POST`. */
function serveComments(fake, initial = []) {
  const comments = [...initial];
  const path = `/api/teams/${TEAM_ID}/action-items/${item(1).id}/comments`;
  fake.route('GET', path, () => json({ items: comments }));
  fake.route('POST', path, (req) => {
    comments.push({ id: req.body.commentId, body: req.body.body, anonymous: false, createdAt: T1 + 100, author: { pid: ALICE.pid, name: ALICE.name, guest: false }, mine: true });
    return json({}, 201);
  });
  return { comments, gets: () => fake.requests.filter((r) => r.method === 'GET' && r.path === path).length };
}

test('open items from earlier retros come first; this board\'s and closed ones do not', async ({ page, fake }) => {
  serveTeam(fake, teamRoom({
    actionItems: [
      item(2, { text: 'Second oldest', sourceBoardId: OTHER_BOARD, sourceCardId: cardId(92) }),
      item(1, { text: 'Oldest **open** item', sourceBoardId: OTHER_BOARD, sourceCardId: cardId(91), assignedTo: CAROL.pid }),
      item(3, { text: 'From this board', sourceBoardId: BOARD_ID, sourceCardId: cardId(93) }),
      item(4, { text: 'Already closed', sourceBoardId: OTHER_BOARD, doneAt: T1, doneBy: BOB.pid }),
      item(5, { text: 'Its board was deleted' }),
    ],
  }));
  await openBoard(page, fake, { snap: boardFor() });

  await expect(prevItems(page)).toHaveCount(3);
  await expect(prevItems(page).locator('.retro-card-body')).toHaveText(['Oldest open item', 'Second oldest', 'Its board was deleted']);
  await expect(prevItems(page).first().locator('strong')).toHaveText('open');
  await expect(prevColumn(page).locator('.retro-column-count')).toHaveText('3');
  await expect(prevItems(page).first().locator('.retro-assignee')).toHaveText('Carol Danvers');
  await expect(prevItems(page).nth(2).locator('.retro-comments-toggle')).toBeHidden();
  const order = await page.locator('.retro-columns > *').evaluateAll((nodes) => nodes.map((n) => n.className));
  expect(order[0]).toBe('retro-before');
});

test('assign and done work from the column, and a done item can be re-opened in place', async ({ page, fake }) => {
  serveTeam(fake, teamRoom({ actionItems: [item(1, { sourceBoardId: OTHER_BOARD, sourceCardId: cardId(91) })] }));
  await openBoard(page, fake, { snap: boardFor() });
  const row = prevItems(page).first();

  await row.locator('.retro-assignee').click();
  await page.getByRole('menuitem', { name: 'Carol Danvers' }).click();
  await expect(row.locator('.retro-assignee')).toHaveText('Carol Danvers');

  await row.locator('.retro-done').click();
  await expect(row.locator('.retro-done')).toHaveAttribute('aria-pressed', 'true');
  await expect(row).toHaveClass(/retro-card-done/);
  await expect(prevColumn(page).locator('.retro-column-count')).toHaveText('0');
  await row.locator('.retro-done').click();
  await expect(row.locator('.retro-done')).toHaveAttribute('aria-pressed', 'false');

  const commands = fake.received.filter(({ frame }) => frame.t === 'cmd' && frame.r === TEAM_ROOM).map(({ frame }) => [frame.k, frame.d]);
  expect(commands).toEqual([
    ['team.action.assign', { itemId: item(1).id, userId: CAROL.pid }],
    ['team.action.done', { itemId: item(1).id, done: true }],
    ['team.action.done', { itemId: item(1).id, done: false }],
  ]);
});

test('an item closed elsewhere leaves the column, a new one joins it', async ({ page, fake }) => {
  serveTeam(fake, teamRoom({ actionItems: [item(1, { sourceBoardId: OTHER_BOARD, sourceCardId: cardId(91) })] }));
  await openBoard(page, fake, { snap: boardFor() });
  await expect(prevItems(page)).toHaveCount(1);

  fake.tx(TEAM_ROOM, [{ k: 'team.action.added', d: { item: item(6, { text: 'Fresh one', sourceBoardId: OTHER_BOARD }) } }]);
  await expect(prevItems(page)).toHaveCount(2);
  fake.tx(TEAM_ROOM, [{ k: 'team.action.deleted', d: { itemId: item(1).id } }, { k: 'team.action.deleted', d: { itemId: item(6).id } }]);
  await expect(prevColumn(page)).toHaveCount(0);
});

test('comments are fetched when opened, refetched on notice, and posted to the source card', async ({ page, fake }) => {
  serveTeam(fake, teamRoom({ actionItems: [item(1, { sourceBoardId: OTHER_BOARD, sourceCardId: cardId(91), commentCount: 1 })] }));
  const served = serveComments(fake, [comment(1)]);
  await openBoard(page, fake, { snap: boardFor() });
  const row = prevItems(page).first();
  await expect(row.locator('.retro-comments-toggle')).toHaveAttribute('aria-label', 'Comments (1)');
  expect(served.gets()).toBe(0);

  await row.locator('.retro-comments-toggle').click();
  const comments = row.locator('.retro-comment-list > li');
  await expect(comments).toHaveText([/Comment 1/]);
  await expect(comments.first().locator('.retro-author-name')).toHaveText('Bob');

  served.comments.push(comment(2, { body: 'From the team page' }));
  fake.tx(TEAM_ROOM, [{ k: 'team.action.comments', d: { itemId: item(1).id, count: 2 } }]);
  await expect(comments).toHaveText([/Comment 1/, /From the team page/]);
  await expect(row.locator('.retro-comments-toggle')).toHaveAttribute('aria-label', 'Comments (2)');
  const afterNotice = served.gets();

  const input = row.locator('.rs-editor-input');
  await input.click();
  await page.keyboard.type('Done on Monday');
  await page.keyboard.press('Enter');
  await expect(comments).toHaveText([/Comment 1/, /From the team page/, /Done on Monday/]);
  const posted = fake.requests.filter((r) => r.method === 'POST');
  expect(posted).toHaveLength(1);
  expect(posted[0].json).toEqual({ commentId: expect.stringMatching(/^[0-9a-f-]{36}$/), body: 'Done on Monday' });
  expect(posted[0].headers['x-rs']).toBe('1');
  expect(served.gets()).toBeGreaterThan(afterNotice);
});

test('a refused comment goes back into the composer', async ({ page, fake }) => {
  serveTeam(fake, teamRoom({ actionItems: [item(1, { sourceBoardId: OTHER_BOARD, sourceCardId: cardId(91) })] }));
  serveComments(fake);
  fake.route('POST', `/api/teams/${TEAM_ID}/action-items/${item(1).id}/comments`, () => problem(403, 'forbidden'));
  await openBoard(page, fake, { snap: boardFor() });
  const row = prevItems(page).first();
  await row.locator('.retro-comments-toggle').click();
  await expect(row.locator('.retro-comment-list')).toHaveText('No comments yet.');

  const input = row.locator('.rs-editor-input');
  await input.click();
  await page.keyboard.type('Lost?');
  await page.keyboard.press('Enter');
  await expect(page.getByRole('alert').filter({ hasText: 'The comment could not be added.' })).toBeVisible();
  await expect(input).toHaveText('Lost?');
});

for (const [status, code, message] of [
  [409, 'board_quota', 'This board has no room for more images.'],
  [413, 'too_large', 'Images can be at most 2 MB.'],
  [415, 'unsupported_media_type', 'Only PNG, JPEG, GIF and WebP images can be added.'],
  [507, 'storage_full', 'Image storage is full. Please tell an administrator.'],
]) {
  test(`a comment image goes to the source card's board; ${status} is explained`, async ({ page, fake }) => {
    serveTeam(fake, teamRoom({ actionItems: [item(1, { sourceBoardId: OTHER_BOARD, sourceCardId: cardId(91) })] }));
    serveComments(fake);
    fake.route('POST', `/api/retro/${OTHER_BOARD}/images`, () => problem(status, code));
    await openBoard(page, fake, { snap: boardFor() });
    const row = prevItems(page).first();
    await row.locator('.retro-comments-toggle').click();
    const input = row.locator('.rs-editor-input');
    await input.click();
    await page.keyboard.type('See');

    await input.evaluate((el, bytes) => {
      const data = new DataTransfer();
      data.items.add(new File([new Uint8Array(bytes)], 'shot.png', { type: 'image/png' }));
      // Firefox empties the DataTransfer of a constructed ClipboardEvent, so the data rides on a plain event.
      const event = new Event('paste', { bubbles: true, cancelable: true });
      Object.defineProperty(event, 'clipboardData', { value: data });
      el.dispatchEvent(event);
    }, [137, 80, 78, 71, 13, 10, 26, 10]);
    await expect(page.getByRole('alert').filter({ hasText: message })).toBeVisible();
    await expect(input).toHaveText('See');
    const upload = fake.requests.find((r) => r.method === 'POST' && r.path === `/api/retro/${OTHER_BOARD}/images`);
    expect(upload.headers['content-type']).toBe('image/png');
    expect(upload.headers['x-rs']).toBe('1');
    expect(fake.requests.some((r) => r.path === `/api/retro/${BOARD_ID}/images`)).toBe(false);
  });
}
