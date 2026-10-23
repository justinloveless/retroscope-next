// The rich description editor (UX12): "Edit description" opens ProseMirror on the issue's edit view, everything is
// edited in place with Jira's toolbar and shortcuts, and the save sends descriptionAdf with descriptionBase. The
// web fake serves RS-12's real projection (edit-views.gen.json) and restores its tokens on save as the server does.
// Keys go out with small delays: at 0 ms ProseMirror can act on a selection the browser has not reported yet.
import { expect, test } from '../_harness/fixtures.js';
import { setupPoker, snapshot } from '../poker-table/_poker.js';
import { R1, drawer, issueView, jiraRequests, openBrowse, openTable, round, row, setupJira } from './_jira.js';

const TYPE = { delay: 25 };
const SETTLE_MS = 80;
// Past the prefetch's idle-callback timeout (2 s), so a chunk that was going to load has been asked for.
const PREFETCH_WINDOW_MS = 2500;

/** ProseMirror's Mod: Meta where the page reports a Mac or an iPhone, Control elsewhere. */
async function mod(page) {
  return (await page.evaluate(() => /Mac|iPhone|iPad/.test(navigator.platform))) ? 'Meta' : 'Control';
}

async function openEditor(page, scope) {
  await scope.getByRole('button', { name: 'Edit description' }).click();
  const editor = scope.locator('.rich-editor');
  await expect(editor).toBeVisible();
  return editor;
}

// Focus first and let it settle: ProseMirror puts its own selection back into the DOM shortly after a focus, which
// would overwrite a selection set in the same moment.
async function focusEditor(page) {
  await page.evaluate(() => document.querySelector('.rich-editor').focus());
  await page.waitForTimeout(SETTLE_MS);
}

/** Puts the caret at the end (or start) of `text`, inside the first editor element matching `selector`. */
async function caret(page, text, { selector = '*', start = false } = {}) {
  await focusEditor(page);
  await page.evaluate(([text, selector, start]) => {
    const root = document.querySelector('.rich-editor');
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const at = node.data.indexOf(text);
      if (at >= 0 && node.parentElement.closest(selector)) {
        const range = document.createRange();
        range.setStart(node, start ? at : at + text.length);
        range.collapse(true);
        getSelection().removeAllRanges();
        getSelection().addRange(range);
        return;
      }
    }
    throw new Error(`no text ${text}`);
  }, [text, selector, start]);
  await page.waitForTimeout(SETTLE_MS);
}

/** Selects `text` in the editor. */
async function select(page, text) {
  await focusEditor(page);
  await page.evaluate((text) => {
    const root = document.querySelector('.rich-editor');
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const at = node.data.indexOf(text);
      if (at >= 0) {
        const range = document.createRange();
        range.setStart(node, at);
        range.setEnd(node, at + text.length);
        getSelection().removeAllRanges();
        getSelection().addRange(range);
        return;
      }
    }
    throw new Error(`no text ${text}`);
  }, text);
  await page.waitForTimeout(SETTLE_MS);
}

const tool = (scope, name) => scope.locator('.rich-toolbar').getByRole('button', { name: new RegExp(`^${name}`) });
const patches = (jira) => jira.calls.filter((c) => c.method === 'PATCH');
const lastPatch = (jira) => patches(jira).at(-1)?.body;
const textOf = (node) => (node.text ?? '') + (node.content ?? []).map(textOf).join('');
const chunkRequests = (fake) => fake.requests.filter((r) => /\/assets\/chunks\/rich-editor-[^/]+\.js$/.test(r.path));

async function openCentre(page, fake) {
  const setup = setupJira(fake, { rounds: [round(R1, 1, { ticketKey: 'RS-12' })] });
  await openTable(page);
  const view = issueView(page);
  await expect(view).toHaveAttribute('data-key', 'RS-12');
  return { ...setup, view };
}

test('text typed in a paragraph, a table cell and a panel saves as ADF and the read view keeps its structure', async ({ page, fake }) => {
  const { jira, view } = await openCentre(page, fake);
  const before = jira.issues.get('RS-12').updated;
  await openEditor(page, view);

  await caret(page, 'pay with a saved card');
  await page.keyboard.type(' quickly', TYPE);
  await caret(page, 'Visa', { selector: 'td' });
  await page.keyboard.type(' and Mastercard', TYPE);
  await caret(page, 'Never store the CVC.', { selector: '.panel-body' });
  await page.keyboard.type(' Not even encrypted.', TYPE);
  await view.getByRole('button', { name: 'Save', exact: true }).click();

  await expect(view.locator('.rich-editor')).toHaveCount(0);
  const patch = lastPatch(jira);
  expect(patch.descriptionBase).toBe(before);
  expect(patch.description).toBeUndefined();
  const types = patch.descriptionAdf.content.map((n) => n.type);
  expect(types).toEqual(['paragraph', 'table', 'paragraph', 'panel', 'mediaSingle', 'paragraph', 'rsOpaque', 'bulletList']);
  expect(textOf(patch.descriptionAdf.content[0])).toContain('pay with a saved card quickly');
  expect(textOf(patch.descriptionAdf.content[1])).toContain('Visa and Mastercard');
  expect(textOf(patch.descriptionAdf.content[3])).toBe('Never store the CVC. Not even encrypted.');

  const read = view.locator('.jira-description .jira-prose');
  await expect(read.locator('td').first()).toHaveText('Visa and Mastercard');
  await expect(read.locator('.panel-warning')).toHaveText('Never store the CVC. Not even encrypted.');
  await expect(read.locator('figure.media img')).toHaveAttribute('alt', 'screenshot.png');
  await expect(read.locator('li')).toHaveCount(2);
});

test('bold, italic and a text colour from the toolbar and the keyboard', async ({ page, fake }) => {
  const { jira, view } = await openCentre(page, fake);
  await openEditor(page, view);
  const key = await mod(page);

  await select(page, 'returning');
  await tool(view, 'Bold').click();
  await expect(tool(view, 'Bold')).toHaveAttribute('aria-pressed', 'true');
  await tool(view, 'Text color').click();
  await page.getByRole('button', { name: 'Red', exact: true }).click();
  await select(page, 'customer');
  await page.keyboard.press(`${key}+i`);
  await expect(tool(view, 'Italic')).toHaveAttribute('aria-pressed', 'true');
  await view.getByRole('button', { name: 'Save', exact: true }).click();

  await expect(view.locator('.rich-editor')).toHaveCount(0);
  const first = lastPatch(jira).descriptionAdf.content[0].content;
  expect(first.find((n) => n.text === 'returning').marks).toEqual([{ type: 'strong' }, { type: 'textColor', attrs: { color: '#ff5630' } }]);
  expect(first.find((n) => n.text === 'customer').marks).toEqual([{ type: 'em' }]);
  const read = view.locator('.jira-description .jira-prose p').first();
  await expect(read.locator('strong', { hasText: 'returning' })).toBeVisible();
  await expect(read.locator('.text-color')).toHaveText('returning');
  await expect(read.locator('em')).toHaveText('customer');
});

test('a list made from the toolbar, indented with Tab and saved nested', async ({ page, fake }) => {
  const { jira, view } = await openCentre(page, fake);
  await openEditor(page, view);

  // The first paragraph ends with a text node holding just its full stop.
  await caret(page, '.', { selector: 'p' });
  await page.keyboard.press('Enter');
  await page.waitForTimeout(SETTLE_MS);
  await page.keyboard.type('Steps', TYPE);
  await tool(view, 'Bullet list').click();
  await expect(tool(view, 'Bullet list')).toHaveAttribute('aria-pressed', 'true');
  await page.keyboard.press('Enter');
  await page.keyboard.type('pick a card', TYPE);
  await page.keyboard.press('Tab');
  await expect(tool(view, 'Outdent')).toBeEnabled();
  await view.getByRole('button', { name: 'Save', exact: true }).click();

  await expect(view.locator('.rich-editor')).toHaveCount(0);
  const list = lastPatch(jira).descriptionAdf.content[1];
  expect(list.type).toBe('bulletList');
  expect(list.content).toHaveLength(1);
  expect(textOf(list.content[0].content[0])).toBe('Steps');
  expect(list.content[0].content[1].type).toBe('bulletList');
  expect(textOf(list.content[0].content[1])).toBe('pick a card');
  await expect(view.locator('.jira-description .jira-prose ul ul li')).toHaveText('pick a card');
});

test('a link inserted with the keyboard, then undo and redo', async ({ page, fake }) => {
  const { jira, view } = await openCentre(page, fake);
  await openEditor(page, view);
  const key = await mod(page);

  await select(page, 'saved card');
  await page.keyboard.press(`${key}+k`);
  const address = page.getByRole('textbox', { name: 'Link address' });
  await expect(address).toBeFocused();
  await address.fill('javascript:alert(1)');
  await address.press('Enter');
  await expect(page.getByText('Enter a web or email address')).toBeVisible();
  await address.fill('example.com/cards');
  await address.press('Enter');
  await expect(view.locator('.rich-editor a.rich-link')).toHaveText('saved card');
  await caret(page, 'saved', { selector: 'a' });
  await expect(tool(view, 'Link')).toHaveAttribute('aria-pressed', 'true');

  // Not at the very start: ProseMirror reads a caret there, right after a focus, as the browser resetting it.
  await caret(page, 'customer', { start: true });
  // One input event, so one undo step however slow the machine is.
  await page.keyboard.insertText('loyal ');
  const opening = view.locator('.rich-editor p').first();
  await expect(opening).toHaveText(/^As a returning loyal customer/);
  await page.keyboard.press(`${key}+z`);
  await expect(opening).toHaveText(/^As a returning customer/);
  await page.keyboard.press(`${key}+Shift+z`);
  await expect(opening).toHaveText(/^As a returning loyal customer/);
  await tool(view, 'Undo').click();
  await expect(opening).toHaveText(/^As a returning customer/);
  await expect(view.locator('.rich-editor a.rich-link')).toHaveText('saved card');
  await view.getByRole('button', { name: 'Save', exact: true }).click();

  await expect(view.locator('.rich-editor')).toHaveCount(0);
  const first = lastPatch(jira).descriptionAdf.content[0].content;
  expect(first.find((n) => n.text === 'saved card').marks).toEqual([{ type: 'strong' }, { type: 'link', attrs: { href: 'https://example.com/cards' } }]);
  await expect(view.locator('.jira-description .jira-prose a[href="https://example.com/cards"]')).toHaveText('saved card');
});

test('Jira content the editor cannot read is shown read-only and saved back byte for byte', async ({ page, fake }) => {
  const { jira, view } = await openCentre(page, fake);
  const original = jira.issues.get('RS-12').adf;
  const editor = await openEditor(page, view);

  const block = editor.locator('.rich-opaque');
  await expect(block).toHaveAttribute('contenteditable', 'false');
  await expect(block.locator('.rich-opaque-label')).toHaveText('Payments roadmap');
  const chip = editor.locator('.rich-opaque-chip');
  await expect(chip).toHaveText('Sprint 10');
  await expect(chip).toHaveAttribute('contenteditable', 'false');
  await expect(editor.locator('.rich-opaque-mark')).toHaveText('payments team');

  await caret(page, 'Reviewed in ', { start: true });
  await page.keyboard.type('Sprint review: ', TYPE);
  await view.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(view.locator('.rich-editor')).toHaveCount(0);

  const saved = jira.issues.get('RS-12');
  const originals = jira.originals('RS-12');
  for (const raws of [...Object.values(originals.opaque), ...Object.values(originals.marks)]) {
    expect(saved.adfText).toContain(raws[0]);
  }
  const note = saved.adf.content[5];
  expect(note.attrs).toEqual({ localId: 'rs12-note', dataSource: 'import' });
  expect(textOf(note)).toBe('Sprint review: Reviewed in  with the payments team.');
  expect(saved.adf.content[6]).toEqual(original.content[6]);
  await expect(view.locator('.jira-description .jira-prose')).toContainText('Sprint review: Reviewed in Sprint 10 with the payments team.');
});

test('saving without a change sends nothing', async ({ page, fake }) => {
  const { jira, view } = await openCentre(page, fake);
  await openEditor(page, view);
  await caret(page, 'pay with a saved card');
  await page.keyboard.type('x', TYPE);
  await page.keyboard.press('Backspace');
  await view.getByRole('button', { name: 'Save', exact: true }).click();

  await expect(view.locator('.rich-editor')).toHaveCount(0);
  expect(patches(jira)).toHaveLength(0);
});

test('a description changed in Jira meanwhile keeps the draft and offers to reload', async ({ page, fake }) => {
  const { jira, view } = await openCentre(page, fake);
  await openEditor(page, view);
  await caret(page, 'asks for the CVC');
  await page.keyboard.type(' twice', TYPE);
  jira.touch('RS-12', { summary: 'Pay with a saved card (edited in Jira)' });
  await view.getByRole('button', { name: 'Save', exact: true }).click();

  const banner = view.locator('.rich-banner.is-conflict');
  await expect(banner).toContainText('RS-12 changed in Jira after you started editing');
  await expect(view.locator('.rich-editor li').last()).toHaveText('asks for the CVC twice');

  await banner.getByRole('button', { name: 'Reload' }).click();
  const dialog = page.getByRole('dialog', { name: 'Load the latest description?' });
  await dialog.getByRole('button', { name: 'Keep my draft' }).click();
  await expect(view.locator('.rich-editor li').last()).toHaveText('asks for the CVC twice');

  await banner.getByRole('button', { name: 'Reload' }).click();
  await page.getByRole('dialog', { name: 'Load the latest description?' }).getByRole('button', { name: 'Discard draft and reload' }).click();
  await expect(view.getByRole('button', { name: /^Edit summary/ })).toHaveText('Pay with a saved card (edited in Jira)');
  await expect(view.locator('.rich-editor li').last()).toHaveText('asks for the CVC');
  await expect(view.locator('.rich-banner')).toHaveCount(0);
  expect(textOf(jira.issues.get('RS-12').adf)).not.toContain('twice');
});

test('a save the server refuses shows its code and keeps the draft', async ({ page, fake }) => {
  const { view } = await openCentre(page, fake);
  fake.route('PATCH', '/api/teams/:teamId/jira/issues/RS-12', {
    status: 400,
    body: { code: 'invalid', status: 400, fields: { descriptionAdf: 'invalid_href', 'descriptionAdf.at': 'content.0.content.1.marks.0.attrs.href' } },
  });
  await openEditor(page, view);
  await caret(page, 'asks for the CVC');
  await page.keyboard.type(' now', TYPE);
  await view.getByRole('button', { name: 'Save', exact: true }).click();

  const banner = view.locator('.rich-banner.is-invalid');
  await expect(banner).toContainText('Jira did not accept the description: invalid_href at content.0.content.1.marks.0.attrs.href');
  await expect(view.locator('.rich-editor li').last()).toHaveText('asks for the CVC now');
});

test('cancel with changes asks first; cancel without changes closes at once', async ({ page, fake }) => {
  const { view } = await openCentre(page, fake);
  await openEditor(page, view);
  await caret(page, 'asks for the CVC');
  await page.keyboard.type(' later', TYPE);
  await view.getByRole('button', { name: 'Cancel', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Discard your changes?' });
  await dialog.getByRole('button', { name: 'Keep editing' }).click();
  await expect(view.locator('.rich-editor li').last()).toHaveText('asks for the CVC later');

  await view.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.getByRole('dialog', { name: 'Discard your changes?' }).getByRole('button', { name: 'Discard' }).click();
  await expect(view.locator('.rich-editor')).toHaveCount(0);
  await expect(view.locator('.jira-description .jira-prose li').last()).toHaveText('asks for the CVC');

  await openEditor(page, view);
  await view.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(view.locator('.rich-editor')).toHaveCount(0);
});

test('an edit view Jira does not hold as ADF falls back to the markup editor', async ({ page, fake }) => {
  const { view } = await openCentre(page, fake);
  fake.route('GET', '/api/teams/:teamId/jira/issues/RS-12/description', {
    body: { key: 'RS-12', updated: '2026-10-13T09:30:00.000Z', editable: false, adf: null, media: [], opaque: [], marks: [], limits: { nodes: 5000, depth: 28, text: 32000, bytes: 1048576 } },
  });
  await view.getByRole('button', { name: 'Edit description' }).click();
  await expect(view.locator('.jira-description-editor .island').first()).toHaveAttribute('contenteditable', 'false');
  await expect(view.locator('.rich-editor')).toHaveCount(0);
});

test('the editor chunk is prefetched for someone who can edit, and the drawer edits in place too', async ({ page, fake }) => {
  const { jira } = setupJira(fake);
  await openBrowse(page);
  await expect.poll(() => chunkRequests(fake).length, { timeout: 5000 }).toBeGreaterThan(0);

  await row(page, 'RS-12').locator('.jira-row-open').click();
  const sheet = drawer(page);
  await expect(sheet).toHaveAttribute('data-key', 'RS-12');
  await openEditor(page, sheet);
  await caret(page, 'asks for the CVC');
  await page.keyboard.type(' at checkout', TYPE);
  await sheet.getByRole('button', { name: 'Save', exact: true }).click();
  // The editor is a .jira-prose inside .jira-description too, so wait for it to close before reading the view.
  await expect(sheet.locator('.rich-editor')).toHaveCount(0);
  await expect(sheet.locator('.jira-description .jira-prose li').last()).toHaveText('asks for the CVC at checkout');
  expect(jiraRequests(fake, 'GET', /\/issues\/RS-12\/description$/)).toHaveLength(1);
  expect(lastPatch(jira).descriptionAdf.type).toBe('doc');
});

test('someone who cannot edit gets no editor chunk and no edit button', async ({ page, fake }) => {
  setupJira(fake, { membersCanEdit: false, rounds: [round(R1, 1, { ticketKey: 'RS-12' })] });
  await openTable(page);
  const view = issueView(page);
  await expect(view).toHaveAttribute('data-key', 'RS-12');
  await expect(view.locator('.jira-description .jira-prose')).toContainText('pay with a saved card');
  await page.waitForTimeout(PREFETCH_WINDOW_MS);

  await expect(view.getByRole('button', { name: 'Edit description' })).toHaveCount(0);
  expect(chunkRequests(fake)).toHaveLength(0);
});

test('a quick session, which has no team and no Jira, gets no editor chunk', async ({ page, fake }) => {
  setupPoker(fake, { snap: snapshot({ teamId: null }) });
  await page.goto('/poker/NEOTRO1234');
  await page.locator('.nt-table').waitFor();
  await page.waitForTimeout(PREFETCH_WINDOW_MS);

  expect(chunkRequests(fake)).toHaveLength(0);
  expect(fake.requests.filter((r) => /\/assets\/chunks\/issue-drawer-/.test(r.path))).toHaveLength(0);
});
