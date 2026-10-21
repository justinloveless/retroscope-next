import { expect, test } from '@playwright/test';
import { expectClean, expectValue, openEditor, select, selection, value } from './_page.js';

const ALICE = '0f8fad5b-d9cb-469f-a165-70867728950e';
const BOB = '7c9e6679-7425-40de-944b-e07fc1f90ae7';
const MEMBERS = [{ id: ALICE, name: 'Alice' }, { id: BOB, name: 'Bob' }];

// Firefox empties the DataTransfer of a constructed ClipboardEvent, so the data rides on a plain event.
async function installPaste(page) {
  await page.evaluate(() => {
    window.paste = (data) => {
      const event = new Event('paste', { bubbles: true, cancelable: true });
      Object.defineProperty(event, 'clipboardData', { value: data });
      window.editor.input.dispatchEvent(event);
    };
  });
}

test.afterEach(async ({ page }) => {
  await expectClean(page);
});

test('typing produces canonical markup and Enter submits it', async ({ page }) => {
  await openEditor(page);
  await page.keyboard.type('a **literal** b');
  await expectValue(page, 'a \\*\\*literal\\*\\* b');
  await page.keyboard.press('Shift+Enter');
  await page.keyboard.type('next line');
  await expectValue(page, 'a \\*\\*literal\\*\\* b\nnext line');
  await page.keyboard.press('Enter');
  expect(await page.evaluate(() => window.log.submits)).toEqual(['a \\*\\*literal\\*\\* b\nnext line']);
  await expect(page.locator('.rs-editor-input strong')).toHaveCount(0);
});

test('Shift+Enter at the end opens a line that typing fills', async ({ page }) => {
  await openEditor(page);
  await page.keyboard.type('one');
  await page.keyboard.press('Shift+Enter');
  expect(await selection(page)).toEqual({ start: 4, end: 4 });
  await page.keyboard.type('two');
  await expectValue(page, 'one\ntwo');
});

test('Ctrl/Cmd+B, I and E toggle marks on the selection', async ({ page }) => {
  await openEditor(page, { value: 'make this bold' });
  await select(page, 5, 9);
  await page.keyboard.press('ControlOrMeta+b');
  await expectValue(page, 'make **this** bold');
  await expect(page.locator('.rs-editor-input strong')).toHaveText('this');
  expect(await selection(page)).toEqual({ start: 5, end: 9 });
  await page.keyboard.press('ControlOrMeta+b');
  await expectValue(page, 'make this bold');
  await page.keyboard.press('ControlOrMeta+i');
  await expectValue(page, 'make _this_ bold');
  await page.keyboard.press('ControlOrMeta+i');
  await select(page, 10, 14);
  await page.keyboard.press('ControlOrMeta+e');
  await expectValue(page, 'make this `bold`');
});

test('a mark on a selection with outer spaces wraps only the words', async ({ page }) => {
  await openEditor(page, { value: 'one two three' });
  await select(page, 3, 8);
  await page.keyboard.press('ControlOrMeta+b');
  await expectValue(page, 'one **two** three');
});

test('Ctrl/Cmd+B with no selection bolds what is typed next', async ({ page }) => {
  await openEditor(page, { value: 'plain' });
  await page.keyboard.type(' ');
  await page.keyboard.press('ControlOrMeta+b');
  await page.keyboard.type('loud');
  await expectValue(page, 'plain **loud**');
});

test('Ctrl/Cmd+K links the selection with an encoded href and removes it again', async ({ page }) => {
  await openEditor(page, { value: 'see the article' });
  await select(page, 8, 15);
  await page.keyboard.press('ControlOrMeta+k');
  const address = page.locator('.rs-editor-link input');
  await expect(address).toBeFocused();
  await address.fill('en.wikipedia.org/wiki/Foo_(bar)');
  await address.press('Enter');
  await expectValue(page, 'see the [article](https://en.wikipedia.org/wiki/Foo_%28bar%29)');
  await expect(page.locator('.rs-editor-input')).toBeFocused();
  await select(page, 9, 12);
  await page.keyboard.press('ControlOrMeta+k');
  await expectValue(page, 'see the article');
});

test('Ctrl/Cmd+K refuses a javascript: address', async ({ page }) => {
  await openEditor(page, { value: 'click' });
  await select(page, 0, 5);
  await page.keyboard.press('ControlOrMeta+k');
  const address = page.locator('.rs-editor-link input');
  await address.fill('javascript:alert(1)');
  await address.press('Enter');
  await expect(address).toHaveAttribute('aria-invalid', 'true');
  await address.press('Escape');
  await expectValue(page, 'click');
  await expect(page.locator('.rs-editor-input a')).toHaveCount(0);
});

test('undo and redo walk typing bursts and transforms', async ({ page }) => {
  await openEditor(page);
  await page.keyboard.type('one');
  await select(page, 0, 3);
  await page.keyboard.press('ControlOrMeta+b');
  await expectValue(page, '**one**');
  await page.keyboard.press('ControlOrMeta+z');
  await expectValue(page, 'one');
  await page.keyboard.press('ControlOrMeta+z');
  await expectValue(page, '');
  await page.keyboard.press('ControlOrMeta+Shift+z');
  await expectValue(page, 'one');
  await page.keyboard.press('ControlOrMeta+Shift+z');
  await expectValue(page, '**one**');
  await page.keyboard.press('ControlOrMeta+z');
  await page.keyboard.press('End');
  await page.keyboard.type('!');
  await expectValue(page, 'one!');
  await page.keyboard.press('ControlOrMeta+Shift+z');
  await expectValue(page, 'one!');
});

test('paste inserts plain text only', async ({ page }) => {
  await openEditor(page, { value: 'before after' });
  await installPaste(page);
  await select(page, 7);
  await page.evaluate(() => {
    const data = new DataTransfer();
    data.setData('text/html', '<b>bold</b><img src=x onerror="window.pwned=1">');
    data.setData('text/plain', 'pasted **text**\r\nline two');
    window.paste(data);
  });
  await expectValue(page, 'before pasted \\*\\*text\\*\\*\nline twoafter');
  await expect(page.locator('.rs-editor-input strong, .rs-editor-input b, .rs-editor-input img')).toHaveCount(0);
  expect(await page.evaluate(() => window.pwned)).toBeUndefined();
  await page.keyboard.press('ControlOrMeta+z');
  await expectValue(page, 'before after');
});

test('pasting an image uploads it and inserts a blob reference', async ({ page }) => {
  await openEditor(page, { value: 'pic' });
  await installPaste(page);
  await page.evaluate(() => {
    const data = new DataTransfer();
    data.items.add(new File([new Uint8Array([137, 80, 78, 71])], 'shot.png', { type: 'image/png' }));
    window.paste(data);
  });
  await expectValue(page, 'pic![image](blob:9b2e4f1c-6c1d-4f7a-9a43-0d1f2a3b4c5d)');
  expect(await page.evaluate(() => window.log.uploads)).toBe(1);
});

test('one Backspace deletes a mention pill; one Delete deletes the next one', async ({ page }) => {
  await openEditor(page, {
    value: `hi [[mention:${ALICE}:Stale]] and [[mention:${BOB}:Bob]] there`,
    mentions: MEMBERS,
  });
  await expect(page.locator('.rs-editor-input .mention').first()).toHaveText('@Alice');
  await select(page, 4);
  await page.keyboard.press('Backspace');
  await expectValue(page, `hi  and [[mention:${BOB}:Bob]] there`);
  expect(await selection(page)).toEqual({ start: 3, end: 3 });
  await select(page, 8);
  await page.keyboard.press('Delete');
  await expectValue(page, 'hi  and  there');
  await expect(page.locator('.rs-editor-input .mention')).toHaveCount(0);
});

test('one Backspace after a Jira island deletes the whole island', async ({ page }) => {
  await openEditor(page, {
    dialect: 'jira-description',
    value: 'intro\n\n[[island:t1]]\n\nafter',
    islands: { t1: { t: 'island', id: 't1', c: [{ t: 'table', c: [{ t: 'tr', c: [{ t: 'td', c: [{ t: 'p', c: [{ t: 'text', v: 'cell' }] }] }] }] }] } },
    submit: false,
  });
  await expect(page.locator('.rs-editor-input .island')).toContainText('cell');
  await select(page, 8);
  await page.keyboard.press('Backspace');
  await expectValue(page, 'intro\n\nafter');
  await expect(page.locator('.rs-editor-input .island')).toHaveCount(0);
});

test('typing next to an island keeps it byte-identical', async ({ page }) => {
  await openEditor(page, {
    dialect: 'jira-description',
    value: '[[island:t1]]\n\ntext',
    islands: { t1: { t: 'island', id: 't1', c: [{ t: 'p', c: [{ t: 'text', v: 'frozen' }] }] } },
    submit: false,
  });
  await select(page, 6);
  await page.keyboard.type(' more');
  await page.keyboard.press('Enter');
  await page.keyboard.type('para');
  await expectValue(page, '[[island:t1]]\n\ntext more\npara');
});

test('@ opens a listbox of members that the keyboard drives', async ({ page }) => {
  await openEditor(page, { mentions: MEMBERS });
  await page.keyboard.type('hi @b');
  const listbox = page.getByRole('listbox');
  await expect(listbox).toBeVisible();
  await expect(listbox.getByRole('option')).toHaveText(['Bob']);
  await page.keyboard.press('Backspace');
  await expect(listbox.getByRole('option')).toHaveText(['Alice', 'Bob']);
  await page.keyboard.press('ArrowDown');
  await expect(listbox.getByRole('option', { name: 'Bob' })).toHaveAttribute('aria-selected', 'true');
  await page.keyboard.press('Tab');
  await expect(listbox).toBeHidden();
  await page.keyboard.type('thanks');
  await expectValue(page, `hi [[mention:${BOB}:Bob]] thanks`);
  await page.keyboard.type(' @al');
  await expect(listbox).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(listbox).toBeHidden();
  await page.keyboard.press('Enter');
  expect(await page.evaluate(() => window.log.submits)).toEqual([`hi [[mention:${BOB}:Bob]] thanks @al`]);
});

test('normalising browser soup restores the caret', async ({ page }) => {
  await openEditor(page, { value: 'hello world' });
  await page.evaluate(() => {
    const p = window.editor.input.querySelector('p');
    const span = document.createElement('span');
    span.setAttribute('data-soup', '');
    span.append('wor');
    p.replaceChildren('hello ', span, 'ld');
    window.editor.select(8);
    window.editor.input.dispatchEvent(new InputEvent('input', { inputType: 'insertText', data: 'x', bubbles: true }));
  });
  await expect(page.locator('.rs-editor-input span')).toHaveCount(0);
  expect(await selection(page)).toEqual({ start: 8, end: 8 });
  await page.keyboard.type('X');
  await expectValue(page, 'hello woXrld');
});

test('div and br soup from soft keyboards becomes paragraphs and line breaks', async ({ page }) => {
  await openEditor(page);
  await page.evaluate(() => {
    const input = window.editor.input;
    const first = document.createElement('div');
    first.append('line one');
    const second = document.createElement('div');
    const font = document.createElement('font');
    font.append('line two');
    second.append(font, document.createElement('br'));
    input.replaceChildren(first, second);
    const range = document.createRange();
    range.setStart(font.firstChild, 4);
    getSelection().removeAllRanges();
    getSelection().addRange(range);
    input.dispatchEvent(new InputEvent('input', { inputType: 'insertReplacementText', bubbles: true }));
  });
  await expectValue(page, 'line one\n\nline two');
  await expect(page.locator('.rs-editor-input div, .rs-editor-input font')).toHaveCount(0);
  expect(await selection(page)).toEqual({ start: 13, end: 13 });
});

test('the value round-trips lists, quotes and code untouched', async ({ page }) => {
  const markup = '- one\n- **two**\n\n> quoted\n\n```\ncode\n```';
  await openEditor(page, { value: markup });
  await select(page, 3);
  await page.keyboard.type('!');
  await expectValue(page, '- one!\n- **two**\n\n> quoted\n\n```\ncode\n```');
  expect(await value(page)).not.toContain('<');
});

test('beforeinput history and paragraph intents from menus and soft keyboards go through the editor', async ({ page }) => {
  await openEditor(page);
  await page.keyboard.type('draft');
  const prevented = await page.evaluate(() => {
    const input = window.editor.input;
    const fire = (inputType) => {
      const event = new InputEvent('beforeinput', { inputType, bubbles: true, cancelable: true });
      input.dispatchEvent(event);
      return event.defaultPrevented;
    };
    return [fire('historyUndo'), fire('historyRedo'), fire('formatUnderline'), fire('insertParagraph')];
  });
  expect(prevented).toEqual([true, true, true, true]);
  await expectValue(page, 'draft');
  expect(await page.evaluate(() => window.log.submits)).toEqual(['draft']);
  await page.evaluate(() => window.editor.input.dispatchEvent(new InputEvent('beforeinput', { inputType: 'historyUndo', bubbles: true, cancelable: true })));
  await expectValue(page, '');
});
