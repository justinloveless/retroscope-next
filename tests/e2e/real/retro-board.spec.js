// The retro board over the real server (§j): cards with a mention and a pasted image, anonymous cards that name
// nobody in the DOM, on the wire or in the database, and votes with their per-person limit.
import { expect, sql, test } from './_real.js';
import { recordFrames } from './_poker.js';
import { addCard, cardsIn, column, configure, editorIn, pasteImage, retroBoard, typeCard } from './_retro.js';

test('a card mentions a teammate and carries a pasted image; both reach the others', async ({ open }) => {
  const { players } = await retroBoard(open, ['Ann Card', 'Bob Card', 'Cy Card']);
  const [ann, bob] = players;

  await typeCard(ann.page, 'Good', 'Thanks @bo');
  await expect(ann.page.getByRole('option')).toHaveText(['Bob Card']);
  await ann.page.keyboard.press('Enter');
  await ann.page.keyboard.type(' for the fix ');
  await pasteImage(column(ann.page, 'Good'));
  await expect(editorIn(column(ann.page, 'Good')).locator('img')).toHaveCount(1);
  await ann.page.keyboard.press('Enter');

  const seen = cardsIn(bob.page, 'Good').first();
  await expect(seen.locator('.mention')).toHaveText('@Bob Card');
  await expect(seen.locator('.retro-author-name')).toHaveText('Ann Card');
  const image = seen.locator('.retro-card-body img');
  await expect(image).toHaveAttribute('src', /^\/blob\/[0-9a-f-]{36}$/);
  await expect.poll(() => image.evaluate((img) => img.complete && img.naturalWidth)).toBe(1);

  const mentions = await (await bob.page.request.get('/api/me/mentions')).json();
  expect(mentions.items.map((item) => item.author?.name)).toEqual(['Ann Card']);
});

test('an anonymous card names nobody in the page, on the wire or in the database', async ({ open, browserName }) => {
  const { players, board } = await retroBoard(open, ['Ann Anon', 'Bob Anon']);
  const [ann, bob] = players;
  // A fresh load, so every frame of the board (snapshot included) is captured.
  const frames = recordFrames(bob.page);
  let cdpFrames = null;
  if (browserName === 'chromium') {
    const cdp = await bob.context.newCDPSession(bob.page);
    await cdp.send('Network.enable');
    cdpFrames = [];
    cdp.on('Network.webSocketFrameReceived', ({ response }) => cdpFrames.push(response.payloadData));
  }
  await bob.page.reload();
  await expect(column(bob.page, 'Bad')).toBeVisible();

  await typeCard(ann.page, 'Bad', 'Too many meetings');
  await column(ann.page, 'Bad').getByLabel('Post anonymously').check();
  await column(ann.page, 'Bad').getByRole('button', { name: 'Add card' }).click();
  await expect(cardsIn(ann.page, 'Bad').first().locator('.retro-anonymous')).toHaveText('Anonymous');

  const card = cardsIn(bob.page, 'Bad').first();
  await expect(card.locator('.retro-anonymous')).toHaveText('Anonymous');
  await expect(card.locator('.retro-author-name')).toHaveCount(0);
  const html = await card.evaluate((node) => node.outerHTML);
  expect(html).not.toContain(ann.user.id);
  expect(html).not.toContain('Ann Anon');

  // Ann is on the board, so presence names her; what must not is anything that carries the card.
  const carrying = (texts) => texts.filter((text) => text.includes('Too many meetings'));
  const events = carrying(frames.flatMap((frame) => frame.ev ?? frame.d?.cards ?? []).map((event) => JSON.stringify(event)));
  expect(events.length).toBeGreaterThan(0);
  for (const event of events) {
    expect(event).not.toContain(ann.user.id);
    expect(event).not.toContain('Ann Anon');
  }
  if (cdpFrames) {
    const payloads = carrying(cdpFrames);
    expect(payloads.length).toBeGreaterThan(0);
    for (const payload of payloads) {
      expect(payload).not.toContain(ann.user.id);
      expect(payload).not.toContain('Ann Anon');
    }
  }

  const rows = await sql(`select author_id is null, anon_tag is not null, row_to_json(c)::text from retro_cards c where board_id = '${board.id}'`);
  expect(rows).toHaveLength(1);
  expect(rows[0].slice(0, 2)).toEqual(['t', 't']);
  expect(rows[0][2]).not.toContain(ann.user.id);
});

test('votes count for everyone and stop at the per-person limit', async ({ open }) => {
  const { players } = await retroBoard(open, ['Ann Votes', 'Bob Votes']);
  const [ann, bob] = players;
  // A team board allows any number of votes unless its settings cap them.
  await configure(ann.page, { maxVotes: 3 });
  for (const text of ['One', 'Two', 'Three', 'Four']) {
    await addCard(bob.page, 'Good', text);
  }
  // Cards sort by votes, so they are found by text, not position.
  const vote = (page, text) => cardsIn(page, 'Good').filter({ hasText: text }).locator('.retro-vote');
  await expect(cardsIn(ann.page, 'Good')).toHaveCount(4);

  for (const text of ['One', 'Two', 'Three']) {
    await vote(ann.page, text).click();
    await expect(vote(ann.page, text)).toHaveAttribute('aria-pressed', 'true');
  }
  await expect(vote(ann.page, 'Four')).toBeDisabled();
  await expect(vote(ann.page, 'Four')).toHaveAttribute('title', 'You have used all your votes');
  for (const text of ['One', 'Two', 'Three']) {
    await expect(vote(bob.page, text)).toContainText('1');
  }
  await vote(ann.page, 'One').click();
  await expect(vote(ann.page, 'Four')).toBeEnabled();
  await vote(ann.page, 'Four').click();
  await expect(vote(bob.page, 'One')).toContainText('0');
  await expect(vote(bob.page, 'Four')).toContainText('1');
});
