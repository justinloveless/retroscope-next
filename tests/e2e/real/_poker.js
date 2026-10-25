// Poker helpers for the real-stack specs: a table of several signed-in people and the selectors the U31/U32 specs use.
import { createPoker, createTeam, expect, seedUsers } from './_real.js';

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
 * `names.length` people at one session of a team: the first owns the team and made the session, the others joined
 * the team through a link invite. Each page is open on the table and the first sees every other player's seat.
 * Returns {players: [{context, page, user, frames}], session: {id, code}, team}.
 */
export async function table(open, names, { ordered = false } = {}) {
  const users = await seedUsers(names, { ordered });
  const players = [];
  for (const user of users) {
    const player = await open(user);
    player.frames = recordFrames(player.page);
    players.push(player);
  }
  const [owner, ...others] = players.map((player) => player.page.request);
  const team = await createTeam(owner, others);
  const session = await createPoker(owner, team.id);
  for (const player of players) {
    await openTable(player.page, session.code);
  }
  // A session opened by its code moves to its team's address (UX18 decision 12).
  await expect(players[0].page).toHaveURL(new RegExp(`/teams/${team.id}/poker/${session.code}(?:[?#]|$)`));
  // Nobody has a seat of their own: the first sees everyone else.
  await expect(players[0].page.locator('.nt-seat')).toHaveCount(players.length - 1);
  return { players, session, team };
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
