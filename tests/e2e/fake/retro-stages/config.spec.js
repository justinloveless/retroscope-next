import { expect, test } from '../_harness/fixtures.js';
import { CONFIG, ROOM, openBoard, sent, snapshot } from './_stages.js';

const settingsButton = (page) => page.getByRole('button', { name: 'Board settings' });
const dialogOf = (page) => page.getByRole('dialog', { name: 'Board settings' });
const votes = (dialog) => dialog.getByRole('textbox', { name: /Votes per person/ });

/** Answers `retro.config.update` as the decider does; `change(fields)` is someone else's committed change. */
function serveConfig(fake, initial) {
  let config = { ...initial };
  const wire = (key) => (key === 'maxVotes' ? config.maxVotes ?? 0 : config[key]);
  const commit = () => [{ k: 'retro.config', d: { config: { ...config } } }];
  fake.onCommand('retro.config.update', (ctx) => {
    const { patch, expected } = ctx.frame.d;
    if (Object.keys(patch).some((key) => expected[key] !== wire(key))) {
      ctx.nack('stale', { d: { current: { ...config } } });
      return;
    }
    config = { ...config, ...patch };
    if (config.maxVotes === 0) {
      delete config.maxVotes;
    }
    ctx.commit(commit());
  });
  return {
    change(fields) {
      config = { ...config, ...fields };
      fake.tx(ROOM, commit());
    },
  };
}

test('settings send only what changed, with the values the facilitator saw', async ({ page, fake }) => {
  serveConfig(fake, CONFIG);
  await openBoard(page, fake, { snap: snapshot() });
  await settingsButton(page).click();
  const dialog = dialogOf(page);
  await expect(dialog.getByRole('switch', { name: 'Allow anonymous posts' })).toHaveAttribute('aria-checked', 'true');
  await expect(votes(dialog)).toHaveValue('');
  await expect(dialog.getByRole('switch', { name: /Advance only when everyone/ })).toBeHidden();

  await dialog.getByRole('switch', { name: 'Allow anonymous posts' }).click();
  await votes(dialog).fill('5');
  await dialog.getByRole('switch', { name: /Structured stages/ }).click();
  await expect(dialog.getByRole('switch', { name: /Advance only when everyone/ })).toBeVisible();
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog).toBeHidden();
  expect(sent(fake, 'retro.config.update')).toEqual([{
    patch: { allowAnonymous: false, maxVotes: 5, stagesEnabled: true },
    expected: { allowAnonymous: true, maxVotes: 0, stagesEnabled: false },
  }]);
  await expect(page.locator('.retro-stages')).toBeVisible();

  await settingsButton(page).click();
  await expect(votes(dialog)).toHaveValue('5');
  await votes(dialog).fill('');
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog).toBeHidden();
  expect(sent(fake, 'retro.config.update').at(-1)).toEqual({ patch: { maxVotes: 0 }, expected: { maxVotes: 5 } });
});

test('a stale save shows the fresh values and keeps the facilitator\'s other changes', async ({ page, fake }) => {
  const server = serveConfig(fake, CONFIG);
  await openBoard(page, fake, { snap: snapshot() });
  await settingsButton(page).click();
  const dialog = dialogOf(page);
  await votes(dialog).fill('5');
  await dialog.getByRole('switch', { name: 'Show author names' }).click();

  fake.onCommand('retro.config.update', (ctx) => {
    server.change({ maxVotes: 3 });
    ctx.nack('stale', { d: { current: { ...CONFIG, maxVotes: 3 } } });
  });
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog.getByRole('status')).toContainText('Someone else changed votes per person first');
  await expect(votes(dialog)).toHaveValue('3');
  await expect(dialog.getByRole('switch', { name: 'Show author names' })).toHaveAttribute('aria-checked', 'false');
  await expect(dialog.getByRole('button', { name: 'Save' })).toBeEnabled();
  await expect(page.getByRole('alert')).toHaveCount(0);

  serveConfig(fake, { ...CONFIG, maxVotes: 3 });
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog).toBeHidden();
  expect(sent(fake, 'retro.config.update').at(-1)).toEqual({ patch: { showAuthorNames: false }, expected: { showAuthorNames: true } });
});

test('a save that someone else\'s change already made stale is not sent', async ({ page, fake }) => {
  const server = serveConfig(fake, CONFIG);
  await openBoard(page, fake, { snap: snapshot() });
  await settingsButton(page).click();
  const dialog = dialogOf(page);
  await votes(dialog).fill('5');
  await dialog.getByRole('switch', { name: 'Show author names' }).click();
  server.change({ maxVotes: 3 });
  await expect(votes(dialog)).toHaveValue('5');

  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog.getByRole('status')).toContainText('Someone else changed votes per person first');
  await expect(votes(dialog)).toHaveValue('3');
  await expect(dialog.getByRole('switch', { name: 'Show author names' })).toHaveAttribute('aria-checked', 'false');
  expect(sent(fake, 'retro.config.update')).toEqual([]);

  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog).toBeHidden();
  expect(sent(fake, 'retro.config.update')).toEqual([{ patch: { showAuthorNames: false }, expected: { showAuthorNames: true } }]);
});

test('a board archived while the dialog is open refuses the save with the reason', async ({ page, fake }) => {
  serveConfig(fake, CONFIG);
  const snap = snapshot();
  await openBoard(page, fake, { snap });
  await settingsButton(page).click();
  const dialog = dialogOf(page);
  await dialog.getByRole('switch', { name: 'Allow anonymous posts' }).click();
  fake.tx(ROOM, [{ k: 'retro.board.meta', d: { ...snap.meta, archived: true } }]);
  await expect(page.locator('.retro-banner-archived')).toBeVisible();

  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog.getByRole('alert')).toHaveText('This board is archived, so its settings cannot change.');
  await expect(dialog).toBeVisible();
  expect(sent(fake, 'retro.config.update')).toEqual([]);
});

test('a vote emoji with a control character is refused before sending', async ({ page, fake }) => {
  serveConfig(fake, CONFIG);
  await openBoard(page, fake, { snap: snapshot() });
  await settingsButton(page).click();
  const dialog = dialogOf(page);
  // Set directly: some engines drop a typed control character, a paste or a script need not.
  await dialog.getByRole('textbox', { name: /Vote emoji/ }).evaluate((input) => {
    input.value = '👍\u0007';
    input.dispatchEvent(new Event('input', { bubbles: true }));
  });
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog.getByRole('alert')).toHaveText('The vote emoji cannot contain control characters.');
  expect(sent(fake, 'retro.config.update')).toEqual([]);
});

test('fields the facilitator has not touched follow other people\'s changes', async ({ page, fake }) => {
  const server = serveConfig(fake, CONFIG);
  await openBoard(page, fake, { snap: snapshot() });
  await settingsButton(page).click();
  const dialog = dialogOf(page);
  await dialog.getByRole('switch', { name: 'Enable voting' }).click();

  server.change({ voteEmoji: '🔥', votingEnabled: true, allowSelfVotes: false });
  await expect(dialog.getByRole('textbox', { name: /Vote emoji/ })).toHaveValue('🔥');
  await expect(dialog.getByRole('switch', { name: /Allow votes for your own cards/ })).toHaveAttribute('aria-checked', 'false');
  await expect(dialog.getByRole('switch', { name: 'Enable voting' })).toHaveAttribute('aria-checked', 'false');
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog).toBeHidden();
  expect(sent(fake, 'retro.config.update')).toEqual([{ patch: { votingEnabled: false }, expected: { votingEnabled: true } }]);
});
