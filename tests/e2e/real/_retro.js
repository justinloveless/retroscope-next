// Retro helpers for the real-stack specs: several people on one board and the selectors the U34–U36 specs use.
// Boards start with the default columns Good, Bad, Kudos and Action Items (RetroListsRepo.DefaultColumns).
import { createBoard, createTeam, expect, seedUsers } from './_real.js';

export const COLUMNS = ['Good', 'Bad', 'Kudos', 'Action Items'];
export const column = (page, title) => page.locator('.retro-column-list > .retro-column')
  .filter({ has: page.locator('.retro-column-title', { hasText: new RegExp(`^${title}$`) }) });
export const cardsIn = (page, title) => column(page, title).locator('.retro-card');
export const editorIn = (scope) => scope.locator('.rs-editor-input');

export async function openBoard(page, code) {
  await page.goto(`/retro/${code}`);
  await expect(page.locator('.retro-column-list > .retro-column')).toHaveCount(COLUMNS.length);
}

/**
 * `names` on one board of a team they all belong to: the first owns the team and made the board, the others joined
 * the team through a link invite. Every page is open on the board. Returns {players, board, team}.
 */
export async function retroBoard(open, names, { body = {} } = {}) {
  const users = await seedUsers(names);
  const players = [];
  for (const user of users) {
    players.push(await open(user));
  }
  const team = await createTeam(players[0].page.request, players.slice(1).map((p) => p.page.request));
  const board = await createBoard(players[0].page.request, team.id, body);
  for (const player of players) {
    await openBoard(player.page, board.code);
  }
  return { players, board, team };
}

/** Types into the column's composer, opening it first unless it is still open from the last card. */
export async function typeCard(page, title, text) {
  const target = column(page, title);
  const input = editorIn(target);
  if (!(await input.isVisible())) {
    await target.locator('.retro-add-card').click();
  }
  await expect(input).toBeFocused();
  await page.keyboard.type(text);
  return input;
}

export async function addCard(page, title, text) {
  await typeCard(page, title, text);
  await page.keyboard.press('Enter');
  await expect(cardsIn(page, title).filter({ hasText: text })).toHaveCount(1);
}

/** Opens Board settings, flips the named switches, optionally sets votes per person, and saves. */
export async function configure(page, { switches = [], maxVotes } = {}) {
  await page.getByRole('button', { name: 'Board settings' }).click();
  const dialog = page.getByRole('dialog', { name: 'Board settings' });
  for (const name of switches) {
    await dialog.getByRole('switch', { name }).click();
  }
  if (maxVotes !== undefined) {
    await dialog.getByRole('textbox', { name: /Votes per person/ }).fill(String(maxVotes));
  }
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog).toBeHidden();
}

export const PNG = [...Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==', 'base64')];

/** Pastes a PNG into the editor in `scope` (Firefox empties a constructed ClipboardEvent, so a plain event carries it). */
export async function pasteImage(scope) {
  await editorIn(scope).evaluate((input, bytes) => {
    const data = new DataTransfer();
    data.items.add(new File([new Uint8Array(bytes)], 'shot.png', { type: 'image/png' }));
    const event = new Event('paste', { bubbles: true, cancelable: true });
    Object.defineProperty(event, 'clipboardData', { value: data });
    input.dispatchEvent(event);
  }, PNG);
}
