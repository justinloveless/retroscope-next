// Poker helpers for the real-stack specs: a table of several signed-in people and the selectors the U31/U32 specs use.
import { createPoker, expect, seedUsers } from './_real.js';

export const seat = (page, user) => page.locator(`.nt-seat[data-pid="${user.id}"]`);
export const handCard = (page, points) => page.locator(`.nt-hand-card[data-points="${points}"]`);
export const shownRound = (page) => page.locator('.nt-round-name');
export const chips = (page) => page.locator('.nt-strip .nt-chip');
export const chipKeys = (page) => page.locator('.nt-chip .nt-chip-key').allTextContents();
export const spotlightControl = (page) => page.locator('.nt-spotlight');

export async function chooseSpotlight(page, item) {
  await page.locator('.nt-spotlight button').click();
  await page.getByRole('menuitem', { name: item }).click();
}

export async function openTable(page, code, query = '') {
  await page.goto(`/poker/${code}${query}`);
  await page.locator('.nt-table').waitFor();
}

/**
 * `names.length` people at one quick session created by the first; each page is open on the table and every seat is
 * visible to the first. Returns [{context, page, user}], session {id, code}.
 */
export async function table(open, names, { teamId = null, create = null } = {}) {
  const users = await seedUsers(names);
  const players = [];
  for (const user of users) {
    const player = await open(user);
    player.frames = recordFrames(player.page);
    players.push(player);
  }
  const session = create ? await create(players[0]) : await createPoker(players[0].page.request, teamId);
  for (const player of players) {
    await openTable(player.page, session.code);
  }
  await expect(players[0].page.locator('.nt-seat')).toHaveCount(players.length);
  return { players, session };
}

/** Every frame the server sends to `page`'s sockets from now on, parsed. */
export function recordFrames(page) {
  const frames = [];
  page.on('websocket', (socket) => socket.on('framereceived', ({ payload }) => {
    frames.push(JSON.parse(String(payload)));
  }));
  return frames;
}

/** How many events of `kind` the recorded frames carry (a tx frame holds several in `ev`). */
export const eventCount = (frames, kind) => frames.flatMap((frame) => frame.ev ?? []).filter((event) => event.k === kind).length;

export async function centre(locator) {
  await locator.scrollIntoViewIfNeeded();
  const box = await locator.boundingBox();
  return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
}
