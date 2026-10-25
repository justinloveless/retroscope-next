import { expect, test } from '../_harness/fixtures.js';
import { BOB, CODE, GOOD, ROOM, card, cardsIn, openBoard, serveBoard, snapshot, typeCard } from './_board.js';

// Hostile text in every place the board shows user content. Each payload records itself in
// window.__xss if it ever runs.
const RUN = 'window.__xss.push(1)';
const CORPUS = [
  `<script>${RUN}</script>`,
  `<img src=x onerror="${RUN}">`,
  `<svg onload="${RUN}"></svg>`,
  `<iframe src="javascript:${RUN}"></iframe>`,
  `[click](javascript:${RUN})`,
  `[click](JaVaScRiPt:${RUN})`,
  '[click](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)',
  '[click](//evil.example/x)',
  '[ok](https://example.com/" onmouseover="alert(1))',
  '![a](blob:not-a-uuid)',
  '![a](https://evil.example/x.png)',
  '[[mention:not-a-uuid:<b>x</b>]]',
  `[[mention:${BOB.pid}:<img src=x onerror="${RUN}">]]`,
  `**<a href="javascript:${RUN}">bold</a>**`,
  `"><img src=x onerror=${RUN}>`,
  '<style>.retro { display: none }</style>',
  '```\n</code><script>alert(1)</script>\n```',
  '&lt;script&gt;alert(1)&lt;/script&gt;',
];
const HOSTILE_NAME = `<img src=x onerror="${RUN}">`;

async function expectInert(page) {
  expect(await page.evaluate(() => window.__xss)).toEqual([]);
  const board = page.locator('.retro');
  await expect(board.locator('script, iframe, object, embed, style, svg:not(.icon)')).toHaveCount(0);
  const unsafe = await board.evaluate((root) => [...root.querySelectorAll('*')].flatMap((el) => [...el.attributes]
    .filter((a) => /^on/i.test(a.name)
      || (a.name === 'href' && !/^(https?:|mailto:|#|\/(?!\/))/i.test(a.value) && !a.value.startsWith('/'))
      || (a.name === 'src' && !/^\/blob\/[0-9a-f-]{36}$/.test(a.value)))
    .map((a) => `${el.localName} ${a.name}=${a.value}`)));
  expect(unsafe).toEqual([]);
}

test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    window.__xss = [];
  });
});

test('hostile cards, comments, names and titles render as text', async ({ page, fake }) => {
  const evil = { pid: BOB.pid, kind: 'user', name: HOSTILE_NAME };
  const cards = CORPUS.map((body, i) => card(i + 1, { body, author: evil }));
  const snap = snapshot({
    meta: { title: `<script>${RUN}</script>`, archived: false },
    columns: [{ id: GOOD, title: HOSTILE_NAME, color: 'green', actionItems: false, version: 1 }],
    cards,
    comments: CORPUS.map((body, i) => ({
      id: `c0aa0000-0000-4000-8000-${String(i).padStart(12, '0')}`,
      cardId: cards[0].id,
      body,
      anonymous: false,
      createdAt: 1_760_000_100_000 + i,
      author: { pid: BOB.pid, name: HOSTILE_NAME, guest: false },
    })),
    votes: [{ cardId: cards[0].id, count: 1, voters: [BOB.pid] }],
    presence: [evil],
  });
  await openBoard(page, fake, { snap });
  await expect(cardsIn(page, HOSTILE_NAME)).toHaveCount(CORPUS.length);
  await cardsIn(page, HOSTILE_NAME).first().getByRole('button', { name: /^Comments/ }).click();
  await expect(page.locator('.retro-comment')).toHaveCount(CORPUS.length);
  await cardsIn(page, HOSTILE_NAME).first().hover();

  await expect(page.locator('.retro-title')).toHaveText(`<script>${RUN}</script>`);
  await expect(page.locator('.retro-column-title')).toHaveText(HOSTILE_NAME);
  await expect(cardsIn(page, HOSTILE_NAME).nth(0).locator('.retro-card-body')).toHaveText(CORPUS[0]);
  await expect(cardsIn(page, HOSTILE_NAME).nth(1).locator('.retro-author-name')).toHaveText(HOSTILE_NAME);
  await expectInert(page);
});

test('hostile text typed into the composer stays text', async ({ page, fake }) => {
  serveBoard(fake);
  await openBoard(page, fake);
  await typeCard(page, 'Good', CORPUS[1]);
  await page.keyboard.press('Enter');
  await expect(cardsIn(page, 'Good').first().locator('.retro-card-body')).toHaveText(CORPUS[1]);
  fake.tx(ROOM, [{ k: 'retro.board.meta', d: { title: HOSTILE_NAME, archived: false } }]);
  await expect(page.locator('.retro-title')).toHaveText(HOSTILE_NAME);
  await expect(page).toHaveTitle(`${HOSTILE_NAME} · Retroscope`);
  await expectInert(page);
  expect(CODE).toBe('RETRO12345');
});
