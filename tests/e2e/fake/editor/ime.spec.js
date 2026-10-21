import { expect, test } from '@playwright/test';
import { expectClean, expectValue, openEditor, select } from './_page.js';

// Before composing, the paragraph gets a stray <span>: the editor re-renders as soon as it
// normalises, so the span surviving every composition step proves it never did mid-composition.
async function plantSoup(page) {
  await page.evaluate(() => {
    const p = window.editor.input.querySelector('p');
    const span = document.createElement('span');
    span.setAttribute('data-soup', '');
    span.append('x');
    p.append(span);
    window.soupParagraph = p;
    window.changesBefore = window.log.changes.length;
    window.compositionEvents = [];
    for (const type of ['compositionstart', 'compositionupdate', 'compositionend']) {
      window.editor.input.addEventListener(type, () => window.compositionEvents.push(type));
    }
  });
}

async function expectUntouched(page) {
  expect(await page.evaluate(() => ({
    span: !!window.editor.input.querySelector('span[data-soup]'),
    sameParagraph: window.editor.input.querySelector('p') === window.soupParagraph,
    changes: window.log.changes.length - window.changesBefore,
  }))).toEqual({ span: true, sameParagraph: true, changes: 0 });
}

async function expectNormalised(page) {
  await expect(page.locator('.rs-editor-input span[data-soup]')).toHaveCount(0);
}

test.afterEach(async ({ page }) => {
  await expectClean(page);
});

test.describe('IME through CDP Input.imeSetComposition', () => {
  test.skip(({ browserName }) => browserName !== 'chromium', 'CDP is Chromium only; the synthetic test below covers the rest');

  async function compose(page, cdp, steps, commit) {
    for (const text of steps) {
      await cdp.send('Input.imeSetComposition', { text, selectionStart: text.length, selectionEnd: text.length });
      await expectUntouched(page);
    }
    await cdp.send('Input.insertText', { text: commit });
  }

  test('Korean syllables compose without normalising and commit once', async ({ page }) => {
    await openEditor(page, { value: 'a' });
    await select(page, 1);
    await plantSoup(page);
    await select(page, 1);
    const cdp = await page.context().newCDPSession(page);
    await compose(page, cdp, ['ㅎ', '하', '한'], '한');
    await expectNormalised(page);
    await expectValue(page, 'a한x');
    await plantSoup(page);
    await select(page, 2);
    await compose(page, cdp, ['ㄱ', '그', '글'], '글');
    await expectNormalised(page);
    await expectValue(page, 'a한글xx');
    expect(await page.evaluate(() => window.compositionEvents.includes('compositionstart'))).toBe(true);
    await page.keyboard.press('Enter');
    expect(await page.evaluate(() => window.log.submits)).toEqual(['a한글xx']);
  });

  test('Japanese conversion composes without normalising', async ({ page }) => {
    await openEditor(page, { value: 'x' });
    await select(page, 1);
    await plantSoup(page);
    await select(page, 1);
    const cdp = await page.context().newCDPSession(page);
    await compose(page, cdp, ['に', 'にほ', 'にほん', '日本'], '日本');
    await expectNormalised(page);
    await expectValue(page, 'x日本x');
  });

  test('Chinese pinyin composes inside a bold run', async ({ page }) => {
    await openEditor(page, { value: '**bold**' });
    await select(page, 4);
    await plantSoup(page);
    await select(page, 4);
    const cdp = await page.context().newCDPSession(page);
    await compose(page, cdp, ['n', 'ni', 'nih', 'niha', 'nihao'], '你好');
    await expectValue(page, '**bold你好**x');
  });
});

test('synthetic composition events never normalise until compositionend', async ({ page }) => {
  await openEditor(page, { value: 'go' });
  await select(page, 2);
  await plantSoup(page);
  await page.evaluate(() => {
    const input = window.editor.input;
    const text = input.querySelector('p').firstChild;
    input.dispatchEvent(new CompositionEvent('compositionstart', { data: '', bubbles: true }));
    window.steps = [];
    for (const step of ['ㅎ', '하', '한']) {
      text.data = `go${step}`;
      input.dispatchEvent(new InputEvent('beforeinput', { inputType: 'insertCompositionText', data: step, isComposing: true, bubbles: true, cancelable: true }));
      input.dispatchEvent(new InputEvent('input', { inputType: 'insertCompositionText', data: step, isComposing: true, bubbles: true }));
      window.steps.push(!!input.querySelector('span[data-soup]') && window.log.changes.length === window.changesBefore);
    }
    const enter = new KeyboardEvent('keydown', { key: 'Enter', isComposing: true, bubbles: true, cancelable: true });
    input.dispatchEvent(enter);
    window.enterPrevented = enter.defaultPrevented;
  });
  expect(await page.evaluate(() => window.steps)).toEqual([true, true, true]);
  expect(await page.evaluate(() => window.enterPrevented)).toBe(false);
  expect(await page.evaluate(() => window.log.submits)).toEqual([]);
  await expectUntouched(page);
  await page.evaluate(() => {
    window.editor.input.dispatchEvent(new CompositionEvent('compositionend', { data: '한', bubbles: true }));
  });
  await expectNormalised(page);
  await expectValue(page, 'go한x');
});
